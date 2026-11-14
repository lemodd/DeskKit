using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace ShotShelf
{
    /// <summary>暂存内容类型（2026-10-26 十二次更新：新增文本）</summary>
    public enum ShotKind
    {
        /// <summary>图片（截图）</summary>
        Image,

        /// <summary>文本（剪贴板文字）</summary>
        Text,
    }

    /// <summary>
    /// 一条暂存内容（S3 图片 / 2026-10-26 新增文本）
    /// 实现 INotifyPropertyChanged：Pinned 变化时卡片上的图钉标记实时刷新
    /// </summary>
    public class ShotItem : INotifyPropertyChanged
    {
        /// <summary>序号（从 1 递增，仅本会话）</summary>
        public int Index { get; set; }

        /// <summary>内容类型：图片 or 文本</summary>
        public ShotKind Kind { get; set; } = ShotKind.Image;

        private string? _text;

        /// <summary>文本内容（Kind=Text 时有效；重启后延迟从 .txt 读取）。
        /// 升级阶段5：改为通知属性——预览窗编辑保存后卡片摘要/字数实时刷新</summary>
        public string? Text
        {
            get => _text;
            set
            {
                _text = value;
                OnPropertyChanged(nameof(Text));
                OnPropertyChanged(nameof(SizeText)); // 字数跟着变
            }
        }

        private string _textPreview = "";

        /// <summary>卡片上显示的文字摘要（创建时算好，避免绑定反复计算）。升级阶段5：可通知更新</summary>
        public string TextPreview
        {
            get => _textPreview;
            set
            {
                _textPreview = value ?? "";
                OnPropertyChanged(nameof(TextPreview));
            }
        }

        /// <summary>是否文本条目</summary>
        public bool IsText => Kind == ShotKind.Text;

        /// <summary>卡片：图片分支可见性</summary>
        public System.Windows.Visibility ImageVisibility =>
            IsText ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

        /// <summary>卡片：文字分支可见性</summary>
        public System.Windows.Visibility TextVisibility =>
            IsText ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        /// <summary>全尺寸图（延迟从 PNG 解码，见 ShotStore.GetFullImage）</summary>
        public BitmapSource? Image { get; set; }

        /// <summary>缩略图（列表显示用）</summary>
        public BitmapSource? Thumbnail { get; set; }

        /// <summary>捕获时间</summary>
        public DateTime CapturedAt { get; set; } = DateTime.Now;

        private bool _pinned;

        /// <summary>恢复用内部方法：不走 setter（避免重写 PinnedAt），但要通知 UI</summary>
        internal void RestorePinned(bool pinned, DateTime pinnedAt)
        {
            _pinned = pinned;
            PinnedAt = pinnedAt;
            // 九次更新: 恢复后通知一次，保证颜色/提示/透明度绑定立即刷新
            OnPropertyChanged(nameof(Pinned));
            OnPropertyChanged(nameof(PinColor));
            OnPropertyChanged(nameof(PinToolTip));
        }

        /// <summary>是否固定（固定后：不被删除/清空/淘汰/午夜清理，且自动排在前面）</summary>
        public bool Pinned
        {
            get => _pinned;
            set
            {
                if (_pinned == value) return; // 值没变不折腾
                _pinned = value;
                if (value) PinnedAt = DateTime.Now; // 记录固定时间用于排序

                // 十次更新: 必须通知 Pinned 本身！
                // 卡片上"已固定恒显橙色"的 DataTrigger 绑的就是 {Binding Pinned}，
                // 之前漏发这一条 → 固定后移开鼠标图标就消失（本次问题的根因）。
                OnPropertyChanged(nameof(Pinned));
                OnPropertyChanged(nameof(PinVisibility));
                OnPropertyChanged(nameof(PinColor));
                OnPropertyChanged(nameof(PinToolTip));
            }
        }

        /// <summary>固定时间（排序用：越早固定越靠前）</summary>
        public DateTime PinnedAt { get; set; }

        // ============ 2026-11-14 固定卡片级联叠放 ============
        // 固定组按 PinnedAt 升序排在轨道最前，组内越靠右越是"最近固定"；
        // 布局层用负左边距让后续固定卡压住前一张、只露 13px 左缘（扑克牌式级联），
        // 最近固定的天然成为顶卡。偏移由 ShotStore.SyncOne 按镜像集合实际顺序统一计算。

        /// <summary>固定卡被压时露出的左缘宽度（px）。2026-11-14 起可设置（设置窗口），默认 13。</summary>
        internal static double CascadePeek = 13;

        /// <summary>槽宽常量（卡片布局槽，参与露边换算）</summary>
        internal const double CascadeSlotWidth = 141;

        /// <summary>级联偏移量：负左边距 = 8(常规左距) - (槽宽141 - 露边)。设置改变后经 ApplyCascadePeek 重算。
        /// 2026-11-14 起不再是 readonly。</summary>
        internal static System.Windows.Thickness TuckedMargin = new(-120, 10, 8, 0);
        internal static readonly System.Windows.Thickness NormalCardMargin = new(8, 10, 8, 0);

        /// <summary>应用新的级联露边宽度（设置窗口调用）：重算被压卡负边距。</summary>
        internal static void ApplyCascadePeek(double peek)
        {
            CascadePeek = peek;
            TuckedMargin = new System.Windows.Thickness(8 - (CascadeSlotWidth - peek), 10, 8, 0);
        }

        /// <summary>文字卡片正文字号（2026-11-14 可设置；新卡构造时读配置，改动经 ApplyTextFontSize 广播）。
        /// 行高跟随字号（约 1.35 倍，原 10.5/14 的比例）。</summary>
        private double _textFontSize = AppConfig.Current.TextCardFontSize;
        public double TextFontSize
        {
            get => _textFontSize;
            set
            {
                if (Math.Abs(_textFontSize - value) < 0.01) return;
                _textFontSize = value;
                OnPropertyChanged(nameof(TextFontSize));
                OnPropertyChanged(nameof(TextLineHeight));
            }
        }
        public double TextLineHeight => Math.Round(_textFontSize * 14.0 / 10.5, 1);

        /// <summary>卡片容器边距：普通 (8,10,8,0)；级联中被压的固定卡 (-120,10,8,0)。
        /// 仅值变化才通知（SyncOne 每次 CollectionChanged 都会跑，防绑定风暴）。</summary>
        private System.Windows.Thickness _cascadeMargin = new(8, 10, 8, 0);
        public System.Windows.Thickness CascadeMargin
        {
            get => _cascadeMargin;
            set
            {
                if (_cascadeMargin == value) return;
                _cascadeMargin = value;
                OnPropertyChanged(nameof(CascadeMargin));
            }
        }

        /// <summary>叠堆数量角标文字（仅固定组顶卡且数量≥2 时非空）</summary>
        private string? _pinBadgeText;
        public string? PinBadgeText
        {
            get => _pinBadgeText;
            set
            {
                if (_pinBadgeText == value) return;
                _pinBadgeText = value;
                OnPropertyChanged(nameof(PinBadgeText));
            }
        }

        /// <summary>叠堆数量角标可见性</summary>
        private System.Windows.Visibility _pinBadgeVisibility = System.Windows.Visibility.Collapsed;
        public System.Windows.Visibility PinBadgeVisibility
        {
            get => _pinBadgeVisibility;
            set
            {
                if (_pinBadgeVisibility == value) return;
                _pinBadgeVisibility = value;
                OnPropertyChanged(nameof(PinBadgeVisibility));
            }
        }

        /// <summary>2026-11-14：该固定卡是否为滚轮翻页选中的"掀开顶卡"。
        /// 卡片容器样式的 DataTrigger 据此抬 Panel.ZIndex（叠堆布局不动，仅换掀开的那张）。</summary>
        private bool _pinTop;
        public bool PinTop
        {
            get => _pinTop;
            set
            {
                if (_pinTop == value) return;
                _pinTop = value;
                OnPropertyChanged(nameof(PinTop));
            }
        }

        /// <summary>卡片右上角📌标记的可见性（2026-10-26: 只显示图标不放文字）</summary>
        public System.Windows.Visibility PinVisibility =>
            System.Windows.Visibility.Visible; // hover 才显示,由样式触发器控制,恒可见但透明度变化

        /// <summary>图钉颜色:已固定=橙色,未固定=灰色(hover 时显示)
        /// 十一次更新: 从"圆形色底"改为直接给图标上色（字体图钉字形支持 Foreground）</summary>
        public System.Windows.Media.Brush PinColor => Pinned ? PinOrange : PinGray;

        // 九次更新: 静态冻结画刷（原来每次访问都 new，绑定反复求值时浪费且可能闪烁）
        private static readonly System.Windows.Media.Brush PinOrange = Freeze(
            System.Windows.Media.Color.FromRgb(0xF9, 0x73, 0x16)); // 正橙色
        private static readonly System.Windows.Media.Brush PinGray = Freeze(
            System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E));

        private static System.Windows.Media.Brush Freeze(System.Windows.Media.Color c)
        {
            var b = new System.Windows.Media.SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        /// <summary>📌提示文字</summary>
        public string PinToolTip => Pinned ? "已固定（点击取消）" : "点击固定（排在最前，不被删除/清空）";

        /// <summary>自动保存成功的完整路径（null=尚未成功保存）；午夜清理时据此决定能否删缓存文件</summary>
        public string? AutoSavedPath { get; set; }

        /// <summary>磁盘缓存文件名（EnableDiskCache 时有效）</summary>
        public string? CacheFileName { get; set; }

        /// <summary>显示用：图片=宽x高，文本=字数</summary>
        public string SizeText => Kind == ShotKind.Text
            ? $"文本 · {Text?.Length ?? 0} 字"
            : (Image == null ? "?" : $"{Image.PixelWidth}x{Image.PixelHeight}");

        /// <summary>显示用：日期+时间（2026-10-26 修正：原来只有 HH:mm，现在带日期）</summary>
        public string TimeText => CapturedAt.ToString("MM-dd HH:mm:ss");

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 暂存架存储（S3）：
    /// - PNG 编码 / 解码
    /// - 内存列表（ObservableCollection 供 WPF 绑定）
    /// - 可选磁盘缓存（%LOCALAPPDATA%\ShotShelf\cache）
    /// </summary>
    public class ShotStore
    {
        /// <summary>暂存的所有内容（图片 + 文字，按捕获顺序；排序/固定/淘汰都以它为准）</summary>
        public ObservableCollection<ShotItem> Shots { get; } = new();

        // ============ 2026-10-26 十三次更新：图片行 / 文字行 ============
        // 界面上拆成上下两条轨道：上面一行只放图片、下面一行只放文字，
        // 每条轨道内部水平排列（超出横向滚动），不再混在一起换行成网格。
        //
        // 为什么不用 ListCollectionView 过滤：
        // 过滤视图对"源集合新增项是否符合 Filter"的处理依赖运行时内部行为，
        // 自己维护两个镜像集合更可控 —— Shots 的任何变化（Add/Remove/Move）
        // 都会触发 CollectionChanged，在这里统一把镜像集合校准成"与 Shots 同序的子集"。

        /// <summary>图片行数据源（Shots 中 Kind=Image 的项，顺序与 Shots 一致）</summary>
        public ObservableCollection<ShotItem> Images { get; } = new();

        /// <summary>文字行数据源（Shots 中 Kind=Text 的项，顺序与 Shots 一致）</summary>
        public ObservableCollection<ShotItem> Texts { get; } = new();

        /// <summary>上限，超出自动移除最旧的未钉选项</summary>
        public int MaxShots { get; set; } = 50;

        /// <summary>是否启用磁盘缓存</summary>
        public bool EnableDiskCache { get; set; } = true;

        /// <summary>缩略图最长边（可由 config 调整）</summary>
        private int _thumbSize = 160;

        /// <summary>设置缩略图尺寸（S7 由 config 注入）</summary>
        public void SetThumbSize(int size)
        {
            _thumbSize = Math.Clamp(size, 64, 512);
        }

        /// <summary>磁盘缓存目录（DeskKit 版：改用 DeskKit 目录，不与原 ShotShelf 共用缓存）</summary>
        public static string CacheDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskKit", "shelf_cache");

        private int _nextIndex = 1;

        public ShotStore()
        {
            // 十三次更新: 主集合任何变化（新增/删除/移动/排序）后，立即把两个行的镜像集合校准过来
            Shots.CollectionChanged += (_, __) => SyncViews();

            if (EnableDiskCache)
            {
                try { Directory.CreateDirectory(CacheDir); } catch { /* 只读环境等，忽略 */ }
            }
        }

        // ================= 两行分区的镜像同步（2026-10-26 十三次更新） =================

        /// <summary>应用新的级联露边宽度（2026-11-14 设置窗口"外观"页）：重算被压卡负边距并刷新布局。</summary>
        public void ApplyCascadePeek(double peek)
        {
            ShotItem.ApplyCascadePeek(peek);
            SyncViews();   // setter 值短路：露边没变的卡不会触发绑定刷新
        }

        /// <summary>应用新的文字卡片字号（2026-11-14 设置窗口"外观"页）：广播到全部文本卡。</summary>
        public void ApplyTextFontSize(double size)
        {
            foreach (var s in Shots) s.TextFontSize = size;
        }

        /// <summary>把 Images / Texts 两个镜像集合校准成 Shots 的同序子集</summary>
        private void SyncViews()
        {
            SyncOne(Images, ShotKind.Image);
            SyncOne(Texts, ShotKind.Text);
        }

        /// <summary>
        /// 把一个镜像集合校准成 desired（= Shots 里指定类型的项，保持原顺序）。
        /// 用 Remove / Insert / Move 做最小改动，而不是清空重建：
        /// 清空重建会让所有卡片容器重建、hover 状态丢失、滚动位置归零，拖动排序时肉眼可见地闪。
        /// </summary>
        private void SyncOne(ObservableCollection<ShotItem> target, ShotKind kind)
        {
            var desired = Shots.Where(s => s.Kind == kind).ToList();

            // ① 删掉已经不在 Shots 里（或类型变了）的项，从后往前删避免索引错位
            for (int i = target.Count - 1; i >= 0; i--)
            {
                if (!desired.Contains(target[i])) target.RemoveAt(i);
            }

            // ② 逐位校正：缺的插入、错的挪过来
            for (int i = 0; i < desired.Count; i++)
            {
                var item = desired[i];
                int cur = target.IndexOf(item);
                if (cur < 0) target.Insert(i, item);
                else if (cur != i) target.Move(cur, i);
            }

            // ③ 级联校准（2026-11-14 固定卡片叠放）：布局完全由自然顺序决定（第一张固定卡
            //    常规边距起头，其余被压露 13px 边缘），**不随滚轮翻页变化**；
            //    滚轮翻页只切换 PinTop（容器 ZIndex 掀到最上）与角标归属。
            //    属性 setter 内部带值变化短路，这里放心直接赋值。
            int pinnedCount = 0;
            foreach (var s in target) if (s.Pinned) pinnedCount++;

            var peers = target.Where(s => s.Pinned).ToList();
            var selTop = _pinTopSel != null && peers.Contains(_pinTopSel)
                ? _pinTopSel
                : peers.LastOrDefault();                      // 默认自然顶卡（最近固定）

            bool firstSeen = false;
            foreach (var item in target)
            {
                if (!item.Pinned)
                {
                    item.CascadeMargin = ShotItem.NormalCardMargin;
                    item.PinTop = false;
                    item.PinBadgeText = null;
                    item.PinBadgeVisibility = System.Windows.Visibility.Collapsed;
                    continue;
                }

                // 布局槽固定：组内第一张常规起头，其余被压（与选中谁无关）
                item.CascadeMargin = firstSeen ? ShotItem.TuckedMargin : ShotItem.NormalCardMargin;
                firstSeen = true;

                bool isTop = ReferenceEquals(item, selTop);
                item.PinTop = isTop && pinnedCount >= 2;      // 单张固定卡无需掀
                if (isTop && pinnedCount >= 2)
                {
                    item.PinBadgeText = pinnedCount.ToString();
                    item.PinBadgeVisibility = System.Windows.Visibility.Visible;
                }
                else
                {
                    item.PinBadgeText = null;
                    item.PinBadgeVisibility = System.Windows.Visibility.Collapsed;
                }
            }
        }

        /// <summary>
        /// 重启恢复（2026-10-26 新增 + 六次更新增强 + 十二次更新支持文本）：
        /// 扫描缓存目录里的 .png 与 .txt，按文件名（含时间戳）排序加载回暂存架；
        /// 然后读取 pins.json 把固定状态恢复。
        /// 文件名格式: yyyyMMdd_HHmmss_序号.png / .txt（Add 时生成）
        /// </summary>
        /// <returns>恢复的数量</returns>
        public int LoadFromCache(int thumbSize)
        {
            if (!Directory.Exists(CacheDir)) return 0;

            // 图片与文本一起恢复；去掉扩展名后排序（名字里含时间戳，即时间序）
            var files = Directory.GetFiles(CacheDir)
                .Where(f =>
                {
                    var e = Path.GetExtension(f).ToLowerInvariant();
                    return e == ".png" || e == ".txt";
                })
                .OrderBy(f => Path.GetFileNameWithoutExtension(f))
                .ToList();

            int restored = 0;

            foreach (var path in files)
            {
                try
                {
                    var name = Path.GetFileNameWithoutExtension(path);

                    // 解析文件名: yyyyMMdd_HHmmss_序号
                    DateTime captured = DateTime.Now;
                    int idx = 0;
                    var parts = name.Split('_');
                    if (parts.Length >= 3 &&
                        DateTime.TryParseExact(parts[0] + parts[1], "yyyyMMddHHmmss",
                            null, System.Globalization.DateTimeStyles.None, out var dt))
                    {
                        captured = dt;
                        int.TryParse(parts[^1], out idx);
                    }

                    var item = new ShotItem
                    {
                        Index = idx > 0 ? idx : _nextIndex++,
                        CapturedAt = captured,
                        CacheFileName = Path.GetFileName(path),
                    };

                    if (Path.GetExtension(path).ToLowerInvariant() == ".txt")
                    {
                        // 文本：直接读进内存（体积小），卡片摘要一并算好
                        item.Kind = ShotKind.Text;
                        item.Text = ReadTextFile(path);
                        item.TextPreview = BuildPreview(item.Text);
                    }
                    else
                    {
                        var bmp = LoadPng(path);
                        item.Kind = ShotKind.Image;
                        item.Image = bmp;
                        item.Thumbnail = MakeThumb(bmp, thumbSize);
                    }

                    // 2026-11-01 倒序排列：文件按时间升序遍历，逐个插到最前 → 恢复完新项在最左
                    Shots.Insert(0, item);
                    restored++;
                }
                catch
                {
                    // 坏文件/非本程序命名的文件，跳过
                }
            }

            // 恢复完把 _nextIndex 推到最大序号之后，避免新图序号与旧图重复
            var maxIdx = Shots.Count > 0 ? Shots.Max(s => s.Index) : 0;
            if (_nextIndex <= maxIdx) _nextIndex = maxIdx + 1;

            // ---- 恢复固定状态（2026-10-26: 修复"重启后大头针不见了"） ----
            LoadPins();

            // ---- 九次更新: 恢复后立即把固定项排到最前（原来漏了这步） ----
            SortPinnedFirst();

            return restored;
        }

        // ================= 固定状态持久化（2026-10-26 六次更新） =================
        // 存 CacheDir/pins.json: { "文件名": "固定时间o分秒ticks", ... }
        // 每次固定状态变化都重写（图量少，开销可忽略）

        private static string PinsFilePath => Path.Combine(CacheDir, "pins.json");

        /// <summary>把当前所有固定项写进 pins.json</summary>
        public void SavePins()
        {
            try
            {
                // 九次更新: 用 GroupBy 去重，避免极端情况下重复 key 抛异常导致整份 pins 保存失败
                var dict = Shots.Where(s => s.Pinned && s.CacheFileName != null)
                                .GroupBy(s => s.CacheFileName!)
                                .ToDictionary(g => g.Key, g => g.First().PinnedAt.Ticks);
                var json = System.Text.Json.JsonSerializer.Serialize(dict,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(PinsFilePath, json);
            }
            catch { /* 写失败不影响主功能 */ }
        }

        /// <summary>从 pins.json 恢复固定状态（启动时调用；找不到文件就忽略）</summary>
        public void LoadPins()
        {
            try
            {
                if (!File.Exists(PinsFilePath)) return;
                var json = File.ReadAllText(PinsFilePath);
                var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(json);
                if (dict == null) return;

                foreach (var it in Shots)
                {
                    if (it.CacheFileName != null && dict.TryGetValue(it.CacheFileName, out var ticks))
                    {
                        it.RestorePinned(true, new DateTime(ticks)); // 不走 setter，避免重写 PinnedAt
                    }
                }
            }
            catch { /* 坏文件忽略 */ }
        }

        /// <summary>固定状态变化后的统一入口：保存 pins 并重排序（固定项排前面）</summary>
        public void OnPinChanged()
        {
            SavePins();
            SortPinnedFirst();
        }

        /// <summary>重排：固定项在前（按固定时间），非固定项在后（保持原相对顺序）</summary>
        public void SortPinnedFirst()
        {
            // ObservableCollection 不支持整体排序,用 Move 逐个挪,界面动画友好
            // 稳定策略: 从后往前把固定项 Move 到最前（按 PinnedAt 降序处理 → 结果升序在前）
            var pinned = Shots.Where(s => s.Pinned)
                              .OrderByDescending(s => s.PinnedAt)
                              .ToList();
            foreach (var p in pinned)
            {
                Shots.Move(Shots.IndexOf(p), 0);
            }
        }

        /// <summary>2026-11-14 用户要求（五改）：滚轮翻页切换固定卡——
        /// 叠堆布局完全不动（不重排列表、不挪卡片），只切换"掀开哪一张"：
        /// 被选中的卡通过容器 Panel.ZIndex=100 抬到最上层完整露出，其余保持原位被压。
        /// 滚轮向下 = 往右切换（固定更晚的卡），向上 = 往左；**到头即停不循环**。
        /// item 仅用于判定悬停目标所属轨道。选择不持久化，重启回到自然顶卡（最近固定）。</summary>
        public void RotatePin(ShotItem item, bool toRight)
        {
            if (!item.Pinned) return;
            var peers = Shots.Where(s => s.Kind == item.Kind && s.Pinned)
                             .OrderBy(s => s.PinnedAt)
                             .ToList();
            if (peers.Count < 2) return;

            int sel = _pinTopSel != null && _pinTopSel.Pinned && peers.Contains(_pinTopSel)
                ? peers.IndexOf(_pinTopSel)
                : peers.Count - 1;                            // 默认自然顶卡（最近固定，右端）

            int next = toRight ? sel + 1 : sel - 1;           // 下=右（更新固定），上=左
            if (next < 0 || next >= peers.Count) return;      // 到头即停，不循环
            if (next == sel) return;                          // 没动就不刷
            _pinTopSel = peers[next];

            SyncViews();                                      // 只重算 ZIndex/角标，不动布局
        }

        /// <summary>滚轮翻页当前掀开的固定卡（null = 自然顶卡）</summary>
        private ShotItem? _pinTopSel;

        /// <summary>2026-11-14 用户要求：鼠标离开轨道后，掀开的固定卡恢复成默认顶卡（最近固定）。
        /// 没有翻页选择时是 no-op，不动布局。</summary>
        public void ResetPinSelection()
        {
            if (_pinTopSel == null) return;
            _pinTopSel = null;
            SyncViews();                                      // 角标/ZIndex 回到自然顶卡
        }

        /// <summary>2026-11-14 用户要求（hover 哪张显示哪张）：鼠标悬停固定卡 = 直接选中它
        /// （走 PinTop 同一通道，被埋的卡悬停露边时立即掀到最上）。非固定卡/重复进入是 no-op。</summary>
        public void HoverPin(ShotItem item)
        {
            if (!item.Pinned) return;
            if (ReferenceEquals(_pinTopSel, item)) return;
            _pinTopSel = item;
            SyncViews();
        }

        /// <summary>
        /// 新图入库：PNG 编码 →（可选）写盘 → 生成缩略图 → 加入列表 → 超限淘汰
        /// </summary>
        /// <param name="bmp">剪贴板来的原始位图</param>
        /// <returns>新加入的 ShotItem</returns>
        public ShotItem Add(BitmapSource bmp)
        {
            FreezeIfPossible(bmp); // S-M5：后台编码前提——保证位图已冻结可跨线程
            var item = new ShotItem
            {
                Index = _nextIndex++,
                Image = bmp,
                CapturedAt = DateTime.Now,
            };

            // ---- 可选磁盘缓存（PNG 落盘，防重启丢）----
            // S-M5（2026-11-13）：PNG 编码移后台线程（位图已 Freeze 可跨线程），
            // 4K 截图入库不再卡 UI；CacheFileName 后台完成后补记
            if (EnableDiskCache)
            {
                var name = $"{item.CapturedAt:yyyyMMdd_HHmmss}_{item.Index}.png";
                var path = Path.Combine(CacheDir, name);
                var bmpLocal = bmp;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        SavePng(bmpLocal, path);
                        item.CacheFileName = name;
                    }
                    catch { /* 落盘失败不影响内存暂存 */ }
                });
            }

            // ---- 缩略图（最长边可配，默认 160px） ----
            item.Thumbnail = MakeThumb(bmp, _thumbSize);

            // 2026-11-01 用户要求：倒序排列——新项插到最前（界面最左），再把固定项挪回最前
            Shots.Insert(0, item);
            SortPinnedFirst();
            EnforceLimit();
            return item;
        }

        /// <summary>
        /// 新文本入库（2026-10-26 十二次更新）：
        /// 写 UTF-8 .txt 缓存 → 算卡片摘要 → 加入列表 → 超限淘汰。
        /// 与图片共用同一套固定/排序/清理逻辑。
        /// </summary>
        /// <param name="text">剪贴板来的文字</param>
        public ShotItem Add(string text)
        {
            // 同文去重（2026-11-11 反馈：暂存架好多重复）：与任一现存文字条目内容相同则不再新增，
            // 但把已有卡片挪到最前（非固定项置顶）——聊天窗复制要进暂存架，用户能感知到卡片动了。
            // 连续复制同一段文字不会堆出重复卡。
            // S-M5：先比长度（string == 内部也短路，但显式长度过滤让长文本列表的扫描便宜得多）
            var existing = Shots.FirstOrDefault(s =>
                s.Kind == ShotKind.Text && s.Text!.Length == text.Length && s.Text == text);
            if (existing != null)
            {
                if (!existing.Pinned && Shots.IndexOf(existing) > 0)
                {
                    Shots.Move(Shots.IndexOf(existing), 0);
                    SortPinnedFirst();
                }
                return existing;
            }

            var item = new ShotItem
            {
                Index = _nextIndex++,
                Kind = ShotKind.Text,
                Text = text,
                TextPreview = BuildPreview(text),
                CapturedAt = DateTime.Now,
            };

            // ---- 可选磁盘缓存（.txt 落盘，防重启丢） ----
            if (EnableDiskCache)
            {
                try
                {
                    var name = $"{item.CapturedAt:yyyyMMdd_HHmmss}_{item.Index}.txt";
                    File.WriteAllText(Path.Combine(CacheDir, name), text, Utf8NoBom);
                    item.CacheFileName = name;
                }
                catch { /* 落盘失败不影响内存暂存 */ }
            }

            // 2026-11-01 用户要求：倒序排列——新文本同样插到最前，再把固定项挪回最前
            Shots.Insert(0, item);
            SortPinnedFirst();
            EnforceLimit();
            return item;
        }

        /// <summary>
        /// 确保图片有磁盘缓存文件（2026-11-11 发送到设备用）：有缓存直接返回路径；
        /// 缓存缺失（未开磁盘缓存/文件被清）则用内存位图补写一份。返回绝对路径；无图/失败 null。
        /// </summary>
        public string? EnsureCacheFile(ShotItem item)
        {
            if (item.Kind == ShotKind.Text || item.Image == null) return null;

            if (item.CacheFileName is not null)
            {
                var p = Path.Combine(CacheDir, item.CacheFileName);
                if (File.Exists(p)) return p;
            }
            try
            {
                var name = $"{item.CapturedAt:yyyyMMdd_HHmmss}_{item.Index}.png";
                SavePng(item.Image, Path.Combine(CacheDir, name));
                item.CacheFileName = name;
                return Path.Combine(CacheDir, name);
            }
            catch { return null; }
        }

        /// <summary>删除指定项（内存与磁盘一起删；固定项拒绝删除）</summary>
        public void Remove(ShotItem item, bool force = false)
        {
            // 2026-11-14：force=true 供"固定项删除确认后直接删"使用（Clear/ClearKind 仍跳过固定项）
            if (item.Pinned && !force) return;

            Shots.Remove(item);
            if (item.CacheFileName != null)
            {
                try { File.Delete(Path.Combine(CacheDir, item.CacheFileName)); } catch { }
            }
            SavePins(); // 同步 pins.json（该项被删了）
        }

        /// <summary>清空全部（固定项保留；返回是否全部清空）</summary>
        public void Clear()
        {
            foreach (var it in Shots.ToList()) Remove(it);
        }

        /// <summary>按类型清空（固定项保留）：2026-11-02 反馈——图片/文字分开清，
        /// 当前显示哪个轨道就只清哪一类，另一类不动</summary>
        public void ClearKind(ShotKind kind)
        {
            foreach (var it in Shots.Where(s => s.Kind == kind).ToList()) Remove(it);
        }

        /// <summary>
        /// 取全尺寸图：优先内存；内存被 GC 或重启后从磁盘 PNG 解码
        /// </summary>
        public BitmapSource? GetFullImage(ShotItem item)
        {
            if (item.Image != null) return item.Image;
            if (item.CacheFileName == null) return null;

            var path = Path.Combine(CacheDir, item.CacheFileName);
            if (!File.Exists(path)) return null;
            try
            {
                var bmp = LoadPng(path);
                item.Image = bmp; // 回填缓存
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>
        /// 取文本内容（2026-10-26 十二次更新）：优先内存；重启后从 .txt 读回并回填
        /// </summary>
        public string? GetText(ShotItem item)
        {
            if (item.Text != null) return item.Text;
            if (item.CacheFileName == null) return null;

            var path = Path.Combine(CacheDir, item.CacheFileName);
            if (!File.Exists(path)) return null;
            try
            {
                var text = ReadTextFile(path);
                item.Text = text;                    // 回填缓存
                item.TextPreview = BuildPreview(text);
                return text;
            }
            catch { return null; }
        }

        /// <summary>
        /// 更新文本条目内容（2026-11-02 升级阶段5）：
        /// 更新 Text/TextPreview（带变更通知 → 卡片摘要/字数实时刷新），
        /// 并重写本条目自己的缓存 .txt。
        /// 注意：不碰 txt_day 当日汇总日志——那是捕获时刻的历史记录，编辑不追溯。
        /// </summary>
        /// <returns>磁盘是否写成功（内存必然已更新；false 时调用方提示"仅内存生效"）</returns>
        public bool UpdateText(ShotItem item, string newText)
        {
            item.Text = newText;
            item.TextPreview = BuildPreview(newText);

            if (EnableDiskCache && item.CacheFileName != null)
            {
                try
                {
                    File.WriteAllText(Path.Combine(CacheDir, item.CacheFileName), newText, Utf8NoBom);
                }
                catch { return false; } // 写盘失败：内存已更新，调用方提示
            }
            return true;
        }

        /// <summary>
        /// 超上限时淘汰最旧的未固定项。
        /// 2026-11-01 倒序排列后，最旧的在列表末尾（原来是 FirstOrDefault）。
        /// </summary>
        private void EnforceLimit()
        {
            while (Shots.Count > MaxShots)
            {
                var victim = Shots.LastOrDefault(s => !s.Pinned);
                if (victim == null) break; // 全被固定了就不淘汰
                Remove(victim);
            }
        }

        /// <summary>
        /// 午夜自动清理（2026-10-26 新增）：
        /// 清掉所有【非固定】项。
        /// AutoSavedPath 已落盘的项直接删（文件安全在自动保存目录里）；
        /// 未自动保存过的项只删缓存文件，不动自动保存目录。
        /// </summary>
        /// <returns>清掉的数量</returns>
        public int ClearUnpinned()
        {
            var victims = Shots.Where(s => !s.Pinned).ToList();
            foreach (var it in victims)
            {
                // 只删自己的缓存文件;自动保存目录的文件是用户的"自动保存"成果,不动
                if (it.CacheFileName != null)
                {
                    try { File.Delete(Path.Combine(CacheDir, it.CacheFileName)); } catch { }
                }
                Shots.Remove(it);
            }
            return victims.Count;
        }

        // ================= 文本工具（2026-10-26 十二次更新） =================

        /// <summary>写 .txt 用 UTF-8（不带 BOM，记事本/VS Code 都能正常识别）</summary>
        private static readonly System.Text.Encoding Utf8NoBom =
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>读 .txt：自动识别 BOM，没 BOM 按 UTF-8</summary>
        public static string ReadTextFile(string path) => File.ReadAllText(path, System.Text.Encoding.UTF8);

        /// <summary>
        /// 生成卡片上显示的文字摘要。
        /// 保留原有换行（多行文本在卡片上更像"一段文字"），只是截断到 maxLen。
        /// </summary>
        public static string BuildPreview(string? text, int maxLen = 240)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var t = text.Trim();
            return t.Length <= maxLen ? t : t.Substring(0, maxLen) + "…";
        }

        // ================= PNG 编解码工具 =================

        /// <summary>位图编码为 PNG 并写文件</summary>
        public static void SavePng(BitmapSource bmp, string path)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
        }

        /// <summary>从 PNG 文件解码位图（冻结以便跨线程/防修改）</summary>
        public static BitmapSource LoadPng(string path)
        {
            using var fs = File.OpenRead(path);
            var dec = new PngBitmapDecoder(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = dec.Frames[0];
            frame.Freeze();
            return frame;
        }

        /// <summary>生成缩略图（保持宽高比，最长边 = maxEdge）</summary>
        public static TransformedBitmap MakeThumb(BitmapSource src, int maxEdge)
        {
            double scale = Math.Min(1.0, (double)maxEdge / Math.Max(src.PixelWidth, src.PixelHeight));
            var thumb = new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(scale, scale));
            thumb.Freeze(); // 冻结：列表绑定更稳、可跨线程
            return thumb;
        }

        /// <summary>把任意位图 Freeze 一下，避免 WPF 跨线程访问异常</summary>
        public static T FreezeIfPossible<T>(T bmp) where T : BitmapSource
        {
            if (bmp.CanFreeze) bmp.Freeze();
            return bmp;
        }
    }
}
