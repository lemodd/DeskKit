using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation; // 升级阶段3：滑出/滑回动画
using System.Windows.Media;           // 2026-11-10 设置窗口外观页：SolidColorBrush/Color
using System.Windows.Media.Imaging;
using MessageBox = System.Windows.MessageBox; // WPF 版（避开 WinForms 同名类）
using Clipboard = System.Windows.Clipboard;   // WPF 版（避开 WinForms 同名类）
using DataObject = System.Windows.DataObject; // WPF 版（避开 WinForms 同名类）
using ListBox = System.Windows.Controls.ListBox; // WPF 版（UseWindowsForms 下与 WinForms.ListBox 重名）

namespace ShotShelf
{
    /// <summary>
    /// 主窗口：缩略图网格 + 工具栏 + 状态栏。
    /// S1: 骨架。S2: 剪贴板监听。S3: 入库。S4: 按钮。S5: 预览。S6: 托盘常驻。
    /// </summary>
    public partial class MainWindow : Window
    {
        private ClipboardWatcher? _watcher;
        private readonly ShotStore _store = new();

        // 2026-11-15：点击卡片恢复为预览窗，不再经此处转贴图（贴图/便签改为"拖出暂存架"生成），
        // 原"卡片→贴图"登记表 _itemPins 随之移除
        private bool _realExit = false; // S6: 区分"最小化到托盘"与真正退出（托盘菜单退出时置 true）
        // DeskKit 合并改动：原 TrayIcon 字段删除 —— 托盘由 DeskKit 统一管理（KitTray）
        private readonly AppConfig _config;
        private System.Windows.Threading.DispatcherTimer? _midnightTimer; // 2026-10-26: 午夜清理非固定项

        /// <summary>长文本自动保存文件名追加的文本开头字数（2026-11-05，10→20 用户反馈不够区分）</summary>
        private const int TextNameSuffixChars = 20;

        public MainWindow()
        {
            InitializeComponent();

            // ---- 2026-11-15 新功能：拖拽出暂存架 = 变便签 ----
            // GiveFeedback / QueryContinueDrag 全程在拖拽源上触发，挂在两条轨道上；
            // GiveFeedback 里负责"架外悬浮幽灵 + 自定义光标"，QueryContinue 里记 Esc 取消
            foreach (var l in new[] { LstShots, LstTexts })
            {
                DragDrop.AddGiveFeedbackHandler(l, OnListGiveFeedback);
                DragDrop.AddQueryContinueDragHandler(l, OnListQueryContinueDrag);
            }

            // ---- 右键菜单图标（2026-11-12）：矢量 SVG 路径（源 D:\DeskKit\assests）----
            SetMenuIcon(MenuCapture, "capture");
            SetMenuIcon(MenuFull, "full");
            SetMenuIcon(MenuPin, "pin");
            SetMenuIcon(MenuQuickG, "quickg");
            SetMenuIcon(MenuLan, "lan");
            SetMenuIcon(MenuClear, "clear");
            SetMenuIcon(MenuOpenDir, "folder");
            SetMenuIcon(MenuTopmost, "topmost");
            SetMenuIcon(MenuSettings, "settings");
            SetMenuIcon(MenuAutoStart, "autostart");
            SetMenuIcon(MenuHelp, "help");
            SetMenuIcon(MenuAbout, "about");
            SetMenuIcon(MenuExit, "exit");

            // ---- S7: 读配置（2026-11-05 改用全局共享实例，App 托盘开关与本窗口共用，防止互相覆盖保存） ----
            _config = AppConfig.Current;
            _store.MaxShots = _config.MaxShots;
            _store.EnableDiskCache = _config.EnableDiskCache;
            _store.SetThumbSize(_config.ThumbSize);

            // ---- 外观（2026-11-10 设置窗口"外观"页：标签颜色/窗口背景，存 config 可调） ----
            ApplyAppearance();

            // ---- S2: 剪贴板监听（2026-10-26 十二次更新：图片 + 文本） ----
            _watcher = new ClipboardWatcher(this);
            _watcher.ImageReceived += OnClipboardImage;
            _watcher.MaxTextLength = _config.MaxTextLength;

            // 文本捕获可在 config.json 里用 EnableTextCapture 关闭
            if (_config.EnableTextCapture)
                _watcher.TextReceived += OnClipboardText;

            // ---- S3: 列表绑定 ----
            // 十三次更新: 上条轨道放图片、下条轨道放文字，各绑一个镜像集合
            // （两个集合都是 _store.Shots 的同序子集，由 ShotStore 自动维护）
            LstShots.ItemsSource = _store.Images;
            LstTexts.ItemsSource = _store.Texts;

            // ---- 2026-11-14 设置窗口新功能：级联露边/文字字号按配置初始化 ----
            // 必须在任何卡片入列之前（ShotItem 静态边距 + 既有卡实例的字号）
            ShotItem.ApplyCascadePeek(_config.CascadePeekPx);
            _store.ApplyTextFontSize(_config.TextCardFontSize);

            // ---- 2026-11-14 滚轮翻页收尾：鼠标离开轨道就把掀开的固定卡恢复成默认顶卡 ----
            LstShots.MouseLeave += (_, _) => _store.ResetPinSelection();
            LstTexts.MouseLeave += (_, _) => _store.ResetPinSelection();

            // ---- 2026-11-10 文字互发：手机发来的文字直接入"文"标签新建卡片 ----
            // （与剪贴板文字同一入库链路：摘要/落盘/插最前/超限淘汰 + 自动跳到文标签）
            DeskKit.Transfer.TransferService.TextReceived += OnLanTextReceived;
            DeskKit.Transfer.TransferService.ImageReceived += OnLanImageReceived;

            // ---- 2026-10-26: 重启恢复上次关闭前的截图 ----
            if (_config.EnableDiskCache)
            {
                var restored = _store.LoadFromCache(_config.ThumbSize);
                if (restored > 0)
                    UpdateStatus($"已恢复上次关闭前的 {restored} 项内容");
                else
                    UpdateStatus();
            }
            else
            {
                UpdateStatus();
            }

            // ---- 2026-10-26: 自动保存目录初始化 ----
            InitAutoSaveDir();

            // ---- 2026-10-26: 午夜定时器（每天 0 点清空非固定项） ----
            StartMidnightTimer();

            // ---- 2026-11-02 升级阶段3：贴边自动隐藏 + 位置持久化（定时器 + 保存防抖挂接） ----
            InitEdgeSnap();

            // ---- S6: 托盘 ----
            // DeskKit 合并改动：托盘改由 DeskKit.KitTray 统一创建，这里不再建自己的托盘
            // 窗口标题栏图标：2026-11-12 改用内嵌 DeskKit.ico 的最大帧（原 GDI+ 画法版本保留在 IconFactory 兜底）
            Icon = IconFactory.GetWpfIcon();
        }

        // ============ 2026-10-26: 自动保存 ============

        /// <summary>
        /// 初始化自动保存目录:
        /// config.json 里 AutoSaveDir 非空 → 用它(不存在则创建);
        /// 失败(无权限/盘不在) → 显示状态并禁用自动保存。
        /// 十七次更新：界面上不再显示目录文字（只留一个「设置目录」按钮），
        /// 当前路径改放在按钮的 ToolTip 里，鼠标悬停才看，平时不占地方。
        /// 2026-11-02 升级阶段1：按钮移入右键菜单，路径提示改为状态栏显示（原 ToolTip 版注释保留）。
        /// </summary>
        private void InitAutoSaveDir()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_config.AutoSaveDir))
                {
                    Directory.CreateDirectory(_config.AutoSaveDir);
                    UpdateStatus($"自动保存目录：{_config.AutoSaveDir}");
                }
                else
                {
                    UpdateStatus("尚未设置自动保存目录（右键 → 设置… → 暂存页）");
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"自动保存目录无效：{_config.AutoSaveDir}（{ex.Message}）");
            }
            // 原按钮 ToolTip 版（2026-11-02 升级阶段1 注释保留）：
            //BtnSetAutoSaveDir.ToolTip = $"当前自动保存目录：\n{_config.AutoSaveDir}\n（点击可更改）";
            //BtnSetAutoSaveDir.ToolTip = "尚未设置自动保存目录（点击选择）";
            //BtnSetAutoSaveDir.ToolTip = $"自动保存目录无效：{_config.AutoSaveDir}\n{ex.Message}\n（点击重新选择）";
        }

        /// <summary>点「设置目录」按钮:选目录 → 写 config → 立即生效
        /// 十七次更新：对话框初始位置 = 当前已设置的目录，省得每次从"此电脑"翻起。</summary>
        private void BtnSetAutoSaveDir_Click(object sender, RoutedEventArgs e)
        {
            // Vista+ 目录选择对话框（WPF 内置，无需第三方库）
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择自动保存目录",
                CheckFileExists = false,
                ValidateNames = false,
                FileName = "选择这个文件夹", // 提示用户直接点"打开"
            };

            // 打开时定位到当前目录（目录已被删掉/盘不在就退回默认位置）
            var current = _config.AutoSaveDir;
            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                dlg.InitialDirectory = current;

            if (dlg.ShowDialog(this) != true) return;

            // 从完整伪造文件路径里抠出目录部分
            var dir = System.IO.Path.GetDirectoryName(dlg.FileName);
            if (string.IsNullOrEmpty(dir)) return;

            _config.AutoSaveDir = dir;
            _config.Save();
            InitAutoSaveDir();
            UpdateStatus($"自动保存目录已设为 {dir}");
        }

        /// <summary>
        /// 自动保存一条内容到 AutoSaveDir。
        /// 图片: AutoSaveDir\pic_yyyyMMdd\pic_yyyyMMdd_HHmmss_序号.png（2026-11-03 起按天分目录）
        /// 文本: 短文本（<1K）追加进当日汇总 txt_day_yyyyMMdd.txt（带时间戳）；
        ///       长文本（≥1K）单独存 txt_yyyyMMdd_HHmmss_序号.txt（UTF-8）
        /// （十三次更新: 前缀由统一的 shot_ 改为 pic_ / txt_；2026-11-01: 短文本并入当日汇总文件）
        /// 成功 → item.AutoSavedPath 记录路径（午夜清理时只清列表不清这文件；短文本共用汇总文件不记录）
        /// </summary>
        private void TryAutoSave(ShotItem item)
        {
            if (string.IsNullOrWhiteSpace(_config.AutoSaveDir)) return;

            try
            {
                if (item.Kind == ShotKind.Text)
                {
                    // ---- 文本自动保存 ----
                    var text = _store.GetText(item);
                    if (text == null) return;

                    // 2026-11-01 用户要求：短文本（UTF-8 字节数 < 1024）不单独生成文件，
                    // 追加进当日汇总文件 txt_day_yyyyMMdd.txt（每条带时间戳头 + 空行分隔）；
                    // ≥1K 的照旧单独存 .txt。
                    if (System.Text.Encoding.UTF8.GetByteCount(text) < 1024)
                    {
                        var dname = $"txt_day_{DateTime.Now:yyyyMMdd}.txt";
                        var dpath = System.IO.Path.Combine(_config.AutoSaveDir, dname);
                        var entry = $"[{item.CapturedAt:yyyy-MM-dd HH:mm:ss}]\r\n{text}\r\n\r\n";
                        System.IO.File.AppendAllText(dpath, entry,
                            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                        UpdateStatus($"短文本已记入 {dname}");
                        return;
                    }

                    // 2026-11-05 用户要求：文件名末尾追加文本前 10 个字（净化后），
                    // 在资源管理器里不打开文件就能认出内容。如 txt_20261105_094500_1_今天下午的会议安.txt
                    string suffix = TextNameSuffix(text);
                    var tname = $"txt_{item.CapturedAt:yyyyMMdd_HHmmss}_{item.Index}{suffix}.txt";
                    var tpath = System.IO.Path.Combine(_config.AutoSaveDir, tname);
                    if (File.Exists(tpath))
                        tpath = System.IO.Path.Combine(_config.AutoSaveDir,
                            $"txt_{item.CapturedAt:yyyyMMdd_HHmmssfff}_{item.Index}{suffix}.txt");

                    System.IO.File.WriteAllText(tpath, text,
                        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    item.AutoSavedPath = tpath;
                    return;
                }

                var bmp = _store.GetFullImage(item);
                if (bmp == null) return;

                // 2026-11-03 用户要求：图片按天分目录保存——
                // AutoSaveDir\pic_20261103\pic_20261103_HHmmss_序号.png
                // 目录名用捕获日期（与文件名前缀一致，跨午夜补存也归到捕获当天）
                var dayDir = System.IO.Path.Combine(_config.AutoSaveDir,
                    $"pic_{item.CapturedAt:yyyyMMdd}");
                Directory.CreateDirectory(dayDir); // 已存在时不抛异常

                var name = $"pic_{item.CapturedAt:yyyyMMdd_HHmmss}_{item.Index}.png";
                var path = System.IO.Path.Combine(dayDir, name);
                if (File.Exists(path))
                    path = System.IO.Path.Combine(dayDir,
                        $"pic_{item.CapturedAt:yyyyMMdd_HHmmssfff}_{item.Index}.png");

                // S-M5（2026-11-13）：PNG 编码落盘移后台线程（位图已 Freeze），4K 图入库不卡 UI
                var bmpLocal = bmp;
                var pathLocal = path;
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { ShotStore.SavePng(bmpLocal, pathLocal); item.AutoSavedPath = pathLocal; }
                    catch (Exception ex) { MiniPix.Core.Log.Write("autosave fail: " + ex.Message); }
                });
            }
            catch (Exception ex)
            {
                // 自动保存失败不影响暂存功能,状态栏提示一下
                UpdateStatus($"自动保存失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 取文本前 20 个字（TextNameSuffixChars）作文件名后缀（2026-11-05 用户要求），如 "_今天下午的会议安排"。
        /// 净化规则：换行/制表符压成空格、去掉 Windows 文件名非法字符（\/:*?"&lt;&gt;| 及控制符）、
        /// 掐头去尾空白和点（Windows 文件名不能以点/空格结尾）、压缩连续空格。
        /// 净化后为空（比如开头全是符号/换行）则返回空串，文件名退回原格式。
        /// 按"字符"数截取，emoji 等代理对可能被截半，截半产生的无效字符也会被净化掉。
        /// </summary>
        private static string TextNameSuffix(string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return "";
                var sb = new System.Text.StringBuilder();
                foreach (char c in text.Take(TextNameSuffixChars))
                {
                    if (char.IsWhiteSpace(c)) sb.Append(' ');
                    else if (c >= 0x20 && !System.IO.Path.GetInvalidFileNameChars().Contains(c)) sb.Append(c);
                }
                // 合并连续空格 + 去首尾空格和点
                string s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), " {2,}", " ").Trim(' ', '.');
                return s.Length > 0 ? "_" + s : "";
            }
            catch { return ""; } // 任何意外都不影响保存主流程
        }

        // ============ 2026-10-26: 午夜清理 ============

        /// <summary>启动"每天 0 点清空非固定项"定时器</summary>
        private DateTime _lastSweepDate = DateTime.Now.Date; // 2026-11-13 S-M6：日期变化判定
        private void StartMidnightTimer()
        {
            _midnightTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(1), // 每分钟查一次"日期是否已变"
            };
            _midnightTimer.Tick += (_, _) =>
            {
                // S-M6：旧判定 Hour==0 && Minute==0 只有恰好那一分钟在线才触发——
                // 睡眠跨零点醒来已是 00:05 就整晚不清。改为"日期变了就清"，醒来第一个 tick 必中
                if (DateTime.Now.Date != _lastSweepDate)
                {
                    _lastSweepDate = DateTime.Now.Date;
                    var n = _store.ClearUnpinned();
                    if (n > 0)
                    {
                        UpdateStatus($"已过午夜，自动清除了 {n} 项未固定内容（自动保存的文件不受影响）");
                    }
                }
            };
            _midnightTimer.Start();
        }

        /// <summary>
        /// S6 修复:自我写入防回环标志。
        /// 本程序自己往剪贴板写图时,WM_CLIPBOARDUPDATE 也会通知到自己,
        /// 若不忽略会把刚复制的图又收进暂存架(且 DIB+PNG 两次写入会收两张)。
        /// 写入前置 true,写完并排空消息队列后置回 false。
        /// 2026-11-13 S-H3：bool 改 Interlocked 计数器——bool 在嵌套抑制
        /// （如复制图片操作内部又触发文本复制）时会提前解除，暴露回环窗口；
        /// 计数器进/出严格配对，最内层解除才真正放行。
        /// </summary>
        private int _suppressDepth;

        /// <summary>抑制期间写入剪贴板的图像指纹（ImageSignature）。迟到通知指纹一致才忽略，
        /// 不一致（抑制窗口期用户真的复制了新图）照常入库——裸 bool 会把新图错杀。</summary>
        private string? _suppressImageSig;

        /// <summary>抑制期间写入剪贴板的文本内容。同上，一致才忽略。</summary>
        private string? _suppressText;

        private void EnterSuppress() => System.Threading.Interlocked.Increment(ref _suppressDepth);

        /// <summary>解除一层抑制，并清掉对应的指纹记录（最外层解除时回环窗口关闭）</summary>
        private void ExitSuppress()
        {
            _suppressImageSig = null;
            _suppressText = null;
            System.Threading.Interlocked.Decrement(ref _suppressDepth);
        }

        /// <summary>供预览窗等其它窗口设置：true 进入抑制、false 解除一层（调用方须 try/finally 配对）</summary>
        public bool SuppressCapture
        {
            get => Volatile.Read(ref _suppressDepth) > 0;
            set { if (value) EnterSuppress(); else ExitSuppress(); }
        }

        // ---- 2026-10-30 双图去重：图像指纹 ----
        // WPF 的 SetImage 写 DIB+PNG 双格式会触发两次 WM_CLIPBOARDUPDATE。
        // 不能用 _suppressCapture 挡外部截图（那会把 DeskKit 截图进暂存架的正路堵死），
        // 改为指纹去重：短时间窗口内同一张图只收一次。
        private string? _lastImageSig;
        private DateTime _lastImageSigTime = DateTime.MinValue;

        /// <summary>
        /// 图像指纹：缩到 24x24 Bgra32 后对像素字节求 SHA256。
        /// 用来识别"DIB+PNG 双格式产生的同图重复通知"——两次通知读到的像素相同。
        /// </summary>
        private static string ImageSignature(BitmapSource bmp)
        {
            try
            {
                double w = Math.Max(bmp.PixelWidth, 1), h = Math.Max(bmp.PixelHeight, 1);
                var scaled = new TransformedBitmap(bmp, new System.Windows.Media.ScaleTransform(24.0 / w, 24.0 / h));
                var conv = new FormatConvertedBitmap(scaled, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                conv.Freeze();
                var px = new byte[24 * 24 * 4];
                conv.CopyPixels(px, 24 * 4, 0);
                using var sha = System.Security.Cryptography.SHA256.Create();
                return Convert.ToHexString(sha.ComputeHash(px));
            }
            catch
            {
                // 指纹失败就退化为尺寸串（此时可能漏去重，但不会崩）
                return bmp.PixelWidth + "x" + bmp.PixelHeight;
            }
        }

        /// <summary>外部模块（贴图 Pin）直接入库一张图（2026-10-30 用户要求：钉住的图也进暂存架）。
        /// 与剪贴板路径共用 OnClipboardImage（含指纹去重 + 自动保存），避免重复卡片。</summary>
        public void AddExternalImage(BitmapSource bmp) => OnClipboardImage(bmp);

        /// <summary>剪贴板来了新图 → 入库（S3）→ 自动保存（2026-10-26）</summary>
        private void OnClipboardImage(BitmapSource bmp)
        {
            // 自己复制回剪贴板的图不回收,避免"复制→捕获→复制"死循环
            // （2026-11-13 S-H3：抑制期内还要比对指纹——是刚写出去的那张才忽略，
            //   抑制窗口里用户真实复制的新图不能错杀）
            ShotStore.FreezeIfPossible(bmp); // 剪贴板来的图必须 Freeze 否则绑定时可能崩
            string sig = ImageSignature(bmp);
            if (_suppressDepth > 0 && sig == _suppressImageSig) return;

            // ---- 双图去重（2026-10-30）：2 秒内与上一张指纹相同的图只收一次 ----
            if (sig == _lastImageSig && (DateTime.UtcNow - _lastImageSigTime).TotalMilliseconds < 2000)
            {
                MiniPix.Core.Log.Write("shelf dedupe: identical image within 2s, skipped");
                return;
            }
            _lastImageSig = sig;
            _lastImageSigTime = DateTime.UtcNow;

            var item = _store.Add(bmp);
            TryAutoSave(item); // 自动保存（未设置目录时内部直接跳过）
            UpdateStatus($"已捕获第 {item.Index} 张图");
            ActivateTab(images: true); // 2026-11-02 升级阶段2：新图入库自动跳到"图"标签
        }

        /// <summary>剪贴板来了新文本 → 入库 → 自动保存（2026-10-26 十二次更新）</summary>
        private void OnClipboardText(string text)
        {
            // 自己复制回剪贴板的文字同样不回收（S-H3：内容一致才算回环）
            if (_suppressDepth > 0 && text == _suppressText) return;

            var item = _store.Add(text);
            TryAutoSave(item);
            UpdateStatus($"已捕获第 {item.Index} 条文字（{text.Length} 字）");
            ActivateTab(images: false); // 2026-11-02 升级阶段2：新文字入库自动跳到"文"标签
        }

        /// <summary>手机/浏览器发来的文字入库（2026-11-10 文字互发）：
        /// HTTP 线程触发，marshal 到 UI 线程后走剪贴板文字同一条链路。</summary>
        private void OnLanTextReceived(string text)
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var item = _store.Add(text);
                    TryAutoSave(item);
                    UpdateStatus($"收到「{item.Index}」条消息（{text.Length} 字），已存入文字");
                    ActivateTab(images: false); // 新卡片插最前，跳到文标签立即可见
                }
                catch (Exception ex)
                {
                    MiniPix.Core.Log.Write("shelf: lan text store failed: " + ex.Message);
                }
            });
        }

        /// <summary>手机/浏览器发来的图片入库（2026-11-10 反馈：不落"传输接收"，同本地复制）：
        /// 用上传临时文件加载位图，走剪贴板图片同一条入库链路（缓存/自动保存/跳"图"标签）；
        /// 完成后用暂存架磁盘缓存 png 补写聊天会话，并删除临时文件。</summary>
        private void OnLanImageReceived(DeskKit.Transfer.TransferService.LanImageReceived e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    // 先整读进内存再从流解码：UriSource 方式句柄释放有延迟，会导致
                    // finally 里的临时文件删除失败（2026-11-10 冒烟实测残留 {GUID}.png）
                    var bytes = File.ReadAllBytes(e.TempPath);
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.StreamSource = new MemoryStream(bytes);
                    bmp.EndInit();
                    bmp.Freeze();

                    var item = _store.Add(bmp);
                    TryAutoSave(item);
                    UpdateStatus($"收到图片，已存入图片（第 {item.Index} 张）");
                    ActivateTab(images: true); // 新卡片插最前，跳到图标签立即可见

                    // 聊天回看：引用暂存架缓存 png（不再复制进"传输接收"）
                    if (item.CacheFileName != null)
                    {
                        DeskKit.Transfer.TransferService.Instance?.Store.AddChat(
                            e.FromId, e.FromName, "deskkit", "image", "", e.FileName, e.Size,
                            System.IO.Path.Combine(ShotStore.CacheDir, item.CacheFileName),
                            deleteFile: false);
                    }
                }
                catch (Exception ex)
                {
                    MiniPix.Core.Log.Write("shelf: lan image store failed [" + e.TempPath + "]: " + ex.Message);
                }
                finally
                {
                    try { File.Delete(e.TempPath); }
                    catch (Exception dex)
                    {
                        MiniPix.Core.Log.Write("shelf: lan image temp delete failed [" + e.TempPath + "]: " + dex.Message);
                    }
                }
            });
        }

        /// <summary>卡片右键（2026-11-11 反馈）：动态构建"发送到设备"菜单并弹出。
        /// e.Handled 阻止冒泡——否则会弹出主窗右键菜单。</summary>
        private void OnCardContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            if (fe.DataContext is not ShotItem item) return;
            e.Handled = true;

            var svc = DeskKit.Transfer.TransferService.Instance;
            var devices = svc?.Store.Devices.Where(d => d.IsOnline).ToList();

            var menu = new ContextMenu { PlacementTarget = fe };
            if (devices is not { Count: > 0 })
            {
                menu.Items.Add(new MenuItem { Header = "（无已连接设备）", IsEnabled = false });
            }
            else
            {
                foreach (var dev in devices)
                {
                    var d = dev; // 闭包捕获
                    var mi = new MenuItem { Header = $"发送到「{dev.Name}」" };
                    mi.Click += (_, __) => SendShotToDevice(item, d);
                    menu.Items.Add(mi);
                }
                if (devices.Count > 1)
                {
                    menu.Items.Add(new Separator());
                    var all = new MenuItem { Header = "发送到全部设备" };
                    all.Click += (_, __) => devices.ForEach(d => SendShotToDevice(item, d));
                    menu.Items.Add(all);
                }
            }
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        /// <summary>把一张图片卡片/一条文字卡片发给指定设备：图片发磁盘缓存原图，文字直接发文本。
        /// 都会写进网页聊天会话，手机端气泡可见。</summary>
        private void SendShotToDevice(ShotItem item, DeskKit.Transfer.DeviceItem dev)
        {
            var svc = DeskKit.Transfer.TransferService.Instance;
            if (svc == null)
            {
                global::DeskKit.App.Notifier("传输服务未启动，无法发送");
                return;
            }

            if (item.Kind == ShotKind.Text)
            {
                var text = _store.GetText(item);
                if (string.IsNullOrEmpty(text))
                {
                    global::DeskKit.App.Notifier("文字内容读取失败");
                    return;
                }
                svc.SendTextTo(dev.Id, text);
            }
            else
            {
                var path = _store.EnsureCacheFile(item);
                if (path == null)
                {
                    global::DeskKit.App.Notifier("图片不可用（缓存缺失）");
                    return;
                }
                svc.SendFilesTo(dev.Id, new[] { path });
            }
            UpdateStatus($"已发送给「{dev.Name}」，等它查看");
        }

        // ============ 2026-11-02 升级阶段2：左侧竖排标签切换单轨道 ============
        // 原"图片+文字上下两条水平轨道"改为"左侧竖排标签 + 单轨道"：
        // 两条轨道 Border（PnlImageTrack / PnlTextTrack）重叠在同一格，
        // 靠 Visibility 互斥切换；列表数据源、卡片模板、拖拽排序、单击预览零改动。
        // 可调项：标签颜色在 MainWindow.xaml 的 TabImageBrush（橙）/ TabTextBrush（蓝绿）集中配置。
        // 2026-11-10：新增第三标签"设备"（局域网文件传输），二值逻辑重构为三态枚举；
        // 旧 bool 签名保留转发（图/文入库自动跳签的两处调用不用改）。

        private void OnTabImage_Checked(object sender, RoutedEventArgs e) => SwitchTab(showImages: true);
        private void OnTabText_Checked(object sender, RoutedEventArgs e) => SwitchTab(showImages: false);
        private void OnTabDevices_Checked(object sender, RoutedEventArgs e) => SwitchTab(ShelfTab.Devices);

        private enum ShelfTab { Images, Texts, Devices }

        /// <summary>按标签选中状态切换三条轨道的显隐（互斥）。</summary>
        private void SwitchTab(ShelfTab tab)
        {
            // XAML 初始化保护：TabImage 的 IsChecked="True" 在解析到它自身时就会触发 Checked，
            // 此时排在它后面的 PnlImageTrack/PnlTextTrack 还没创建，直接返回即可
            // （初始默认就是显示图片轨道，无需切换）。
            if (PnlImageTrack == null || PnlTextTrack == null || PnlDeviceTrack == null) return;

            PnlImageTrack.Visibility = tab == ShelfTab.Images ? Visibility.Visible : Visibility.Collapsed;
            PnlTextTrack.Visibility = tab == ShelfTab.Texts ? Visibility.Visible : Visibility.Collapsed;
            PnlDeviceTrack.Visibility = tab == ShelfTab.Devices ? Visibility.Visible : Visibility.Collapsed;
            if (tab == ShelfTab.Devices) EnsureDeviceTab(); // 懒初始化：首次切到设备标签才绑数据/生成二维码
            // 2026-11-10 外观选项：背景跟随标签颜色 → 切签即换浅色调背景
            ApplyBackgroundFollowTab(tab);
        }

        /// <summary>新内容入库时自动跳到对应标签（图→图签 / 文→文签），省得用户自己找。
        /// 目标标签已选中时 Checked 不会再触发，直接调 SwitchTab 兜底。</summary>
        private void ActivateTab(ShelfTab tab)
        {
            var rb = tab switch
            {
                ShelfTab.Images => TabImage,
                ShelfTab.Texts => TabText,
                _ => TabDevices,
            };
            if (rb.IsChecked == true)
                SwitchTab(tab);
            else
                rb.IsChecked = true; // 触发 Checked → SwitchTab
        }

        /// <summary>旧二值签名（图/文入库跳签兼容转发）。</summary>
        private void SwitchTab(bool showImages) => SwitchTab(showImages ? ShelfTab.Images : ShelfTab.Texts);
        private void ActivateTab(bool images) => ActivateTab(images ? ShelfTab.Images : ShelfTab.Texts);

        // ============ 2026-11-10：设备标签（局域网文件传输） ============
        // 设备列表绑定 TransferStore.Devices（UI 线程 ObservableCollection，服务线程改动经 Dispatcher marshal）。
        // 发送两条路：拖 FileDrop 到设备卡 / 点齿轮弹多选文件框 → SendFilesTo 只入队路径，对端轮询取走。

        private bool _deviceTabInited;

        /// <summary>首次切到设备标签时：绑设备表（本机卡排第一）。详细设置走本机卡齿轮。</summary>
        private void EnsureDeviceTab()
        {
            if (_deviceTabInited) return;
            _deviceTabInited = true;

            var svc = DeskKit.Transfer.TransferService.Instance;
            if (svc == null)
            {
                MiniPix.Core.Log.Write("shelf: device tab opened but transfer service is down");
                return;
            }

            // 本机伪设备排第一（2026-11-10 反馈：要能看到自己+IP，且与远端设备同一行横排）。
            // CompositeCollection：本机静态项 + 远端设备动态集合，远端增删仍实时生效。
            _selfDevice = new DeskKit.Transfer.DeviceItem("deskkit")
            {
                Name = DeskKit.Transfer.TransferStore.DeskkitName,
                // 双 IP（2026-11-10 Tailscale）：本机卡同时显示局域网 + Tailscale 地址；无 Tailscale 单行
                Ip = svc.TailscaleIp == null ? svc.LanIp : $"{svc.LanIp} · {svc.TailscaleIp}",
                Type = "deskkit",
                IsOnline = true,
                StatusText = $"本机 · 端口 {svc.Port}",
            };
            var sources = new System.Windows.Data.CompositeCollection();
            sources.Add(new System.Windows.Data.CollectionContainer
            {
                Collection = new System.Collections.ObjectModel.ObservableCollection<DeskKit.Transfer.DeviceItem> { _selfDevice },
            });
            sources.Add(new System.Windows.Data.CollectionContainer { Collection = svc.Store.Devices });
            LstDevices.ItemsSource = sources;
        }

        private DeskKit.Transfer.DeviceItem? _selfDevice;

        /// <summary>拖文件到设备卡：只认 FileDrop（资源管理器文件）。</summary>
        private void OnDeviceDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDeviceDrop(object sender, DragEventArgs e)
        {
            var dev = DeviceUnderMouse(sender, e);
            if (dev == null) return;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                SendToDevice(dev, paths);
            e.Handled = true;
        }

        /// <summary>从事件源向上找设备卡对应的 DeviceItem（命中卡片任意子元素都能定位）。</summary>
        private static DeskKit.Transfer.DeviceItem? DeviceUnderMouse(object sender, DragEventArgs e)
        {
            if (sender is not ListBox list) return null;
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null)
            {
                if (dep is System.Windows.Controls.ContentPresenter cp &&
                    cp.DataContext is DeskKit.Transfer.DeviceItem di)
                    return di;
                if (dep == list) return null; // 走出卡片还没命中 = 点在列表空白处
                dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
            }
            return null;
        }

        /// <summary>整卡点击（2026-11-12 反馈）：本机卡=传输设置窗口；其它任何设备（网页端/别的
        /// 电脑的 DeskKit 程序）= 聊天窗口（文字/图片/文件都在聊天窗里发，也可直接拖文件到卡上）。</summary>
        private void OnDeviceCardClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not DeskKit.Transfer.DeviceItem dev) return;
            // 本机卡 = 传输设置窗口（看网址/端口/接收目录 + 改本机名）
            if (dev.Id == "deskkit")
            {
                OpenTransferSettings();
                e.Handled = true;
                return;
            }
            // 💬 小按钮（Preview 隧道事件父级先收到，按模板内名字识别让行）
            if (HitNamedElement(e.OriginalSource, "BtnMsg"))
            {
                OpenMessageWindow(dev);
                e.Handled = true;
                return;
            }
            // 2026-11-12：点设备卡 = 打开聊天窗口（原为选文件发送；聊天窗里 ＋/拖拽/粘贴 都能发文件）
            OpenMessageWindow(dev);
            e.Handled = true;
        }

        /// <summary>OriginalSource 沿可视树向上找指定名字的元素（设备卡模板内命中测试用）。</summary>
        private static bool HitNamedElement(object originalSource, string name)
        {
            for (var dep = originalSource as System.Windows.DependencyObject; dep != null;
                 dep = System.Windows.Media.VisualTreeHelper.GetParent(dep))
                if (dep is System.Windows.FrameworkElement fe && fe.Name == name) return true;
            return false;
        }

        /// <summary>💬：弹设备聊天窗（2026-11-11 反馈：升级为简易聊天窗口，实现网页版功能）。
        /// 离线也允许打开——消息入会话后对方上线即收。</summary>
        private void OpenMessageWindow(DeskKit.Transfer.DeviceItem dev)
        {
            var svc = DeskKit.Transfer.TransferService.Instance;
            if (svc == null)
            {
                global::DeskKit.App.Notifier("传输服务未启动，无法发送");
                return;
            }
            DeskKit.Transfer.DeviceChatWindow.ShowOrActivate(this, dev, svc);
        }

        /// <summary>发送文件到设备（入队不读内存，气泡反馈；失败（服务没起）气泡提示）。</summary>
        private void SendToDevice(DeskKit.Transfer.DeviceItem dev, string[] paths)
        {
            if (dev.Id == "deskkit") return; // 拖到本机卡上：不能给自己发，忽略
            var svc = DeskKit.Transfer.TransferService.Instance;
            if (svc == null)
            {
                global::DeskKit.App.Notifier("传输服务未启动，无法发送");
                return;
            }
            if (!dev.IsOnline)
            {
                global::DeskKit.App.Notifier($"「{dev.Name}」不在线，等它打开页面后再发");
                return;
            }
            svc.SendFilesTo(dev.Id, paths);
            global::DeskKit.App.Notifier($"已发送 {paths.Length} 个文件给「{dev.Name}」，等它取走");
        }

        // ============ 2026-11-02 升级阶段3：贴边自动隐藏 + 位置持久化 ============
        // 机制（方案见 plan_upgrade_20261102.md 阶段3）：
        //   · 松手贴边：拖动结束（WM_EXITSIZEMOVE）时窗口贴着屏幕上缘/右缘/左缘 8px 内 → 记为贴边态
        //   · 自动隐藏：贴边态下滑出屏幕只留 PeekThickness 小边；隐藏态定时轮询鼠标，
        //     进入边缘 HoverBandThickness 判定带 → 滑出；完整显示后鼠标离开 300ms → 滑回
        //   · 拖离即解除：WM_ENTERSIZEMOVE 时先弹回完整位置并清除贴边态，松手重新判定
        //   · 热键 Ctrl+Alt+S：贴边态在滑出/滑回间切换；非贴边态显示/隐藏窗口
        //   · 位置持久化：移动/缩放 500ms 防抖存 config.json；启动恢复，屏外坐标回退默认

        // ---- 可调项集中（想微调改这里） ----
        // 2026-11-10 反馈"留边太宽"+右键设置对话框：原 const 改为 AppConfig 驱动（设置窗口实时可调）
        private static double SnapDetectPx => ShotShelf.AppConfig.Current.SnapDetectPx;   // 松手时距边缘多少像素内判为贴上
        private static double PeekThickness => ShotShelf.AppConfig.Current.PeekThickness; // 隐藏时露出来的小边宽度
        private const int HidePollMs = 25;             // 贴边态鼠标轮询间隔（50→25ms：响应更快）
        private static int LeaveHideDelayMs => ShotShelf.AppConfig.Current.LeaveHideDelayMs; // 完整显示后鼠标离开多久滑回
        private static int AnimMs => ShotShelf.AppConfig.Current.AnimMs;                // 滑出/滑回动画时长
        private const int SaveDebounceMs = 500;        // 位置保存防抖
        private static int SnapInDelayMs => ShotShelf.AppConfig.Current.SnapInDelayMs;  // 松手贴边后多久自动滑入（留小边）
        private const int MoveEndDelayMs = 150;        // 移动事件停多久判定为"松手"（纯WPF方案）

        private string _snapEdge = "None";        // 当前贴边：None / Top / Right / Left（2026-11-10 加 Left）
        private bool _edgeHidden;                 // 是否处于"滑出隐藏（只留小边）"状态
        private bool _animating;                  // 滑动动画进行中（期间暂停判定与保存）
        private bool _manualSizeBySnap;           // SizeToContent→Manual 是否由贴边引起（解除时恢复）
        private bool _topmostForced;              // 贴边期间是否由贴边逻辑置顶中（解除贴边时恢复原 Topmost）
        private bool _topmostRestore;             // 置顶前的 Topmost 值
        private bool _userTopmostOff;             // 用户在贴边态手动取消了置顶（尊重选择，轮询不再重申）
        private double _workTopDip, _workRightDip, _workLeftDip; // 贴边所靠显示器的工作区边界（DIP）
        private DateTime? _mouseOutsideSince;      // 弹出态鼠标离开窗口的时刻
        private bool _bandArmed = true;            // 隐藏态弹出"武装"标志：滑入后鼠标若仍在判定带内不弹出，
                                                   // 离开判定带一次后才允许 hover 弹出（防止贴边后原地弹跳）
        private System.Windows.Interop.HwndSource? _hwndSource;
        private System.Windows.Threading.DispatcherTimer? _hideTimer;    // 贴边态鼠标轮询
        private System.Windows.Threading.DispatcherTimer? _saveTimer;    // 位置保存防抖
        private System.Windows.Threading.DispatcherTimer? _snapInTimer;  // 松手贴边后延迟自动滑入
        private System.Windows.Threading.DispatcherTimer? _moveEndTimer; // 移动停止判定（松手检测）

        /// <summary>构造函数里调用：挂移动/缩放 → 松手判定 + 保存防抖，建定时器。
        /// 升级阶段3修复②：弃用 WM_ENTERSIZEMOVE/EXITSIZEMOVE（实测 DragMove 时钩子收不到），
        /// 改为纯 WPF 方案：LocationChanged/SizeChanged 连续触发=拖动中，停 150ms=松手。</summary>
        private void InitEdgeSnap()
        {
            EnsureTimers();
            LocationChanged += (_, _) => OnWindowMoving();
            SizeChanged += (_, _) => OnWindowMoving();
        }

        private void EnsureTimers()
        {
            if (_hideTimer == null)
            {
                _hideTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(HidePollMs),
                };
                _hideTimer.Tick += OnHideTimerTick;
            }
            if (_saveTimer == null)
            {
                _saveTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(SaveDebounceMs),
                };
                _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveWindowStateNow(); };
            }
            if (_snapInTimer == null)
            {
                // 一次性定时器：松手贴边后延迟 SnapInDelayMs 自动滑入（留小边）。
                // 计划验收标准就是"松手→滑出留 4px 小边"，不等鼠标离开。
                _snapInTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(SnapInDelayMs),
                };
                _snapInTimer.Tick += (_, _) =>
                {
                    _snapInTimer.Stop();
                    if (_snapEdge != "None" && !_edgeHidden) SlideInEdge();
                };
            }
            if (_moveEndTimer == null)
            {
                // 一次性定时器：移动/缩放事件停 MoveEndDelayMs 没再来 → 视为松手 → 判定贴边
                _moveEndTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(MoveEndDelayMs),
                };
                _moveEndTimer.Tick += (_, _) => { _moveEndTimer.Stop(); DetectSnapAfterMove(); };
            }
        }

        /// <summary>拖动/缩放进行中（LocationChanged/SizeChanged 触发）。
        /// 贴边态下：正隐藏先弹回完整位置（用户抓住了小边在拖）；停 150ms 没动静 → 松手判定。</summary>
        private void OnWindowMoving()
        {
            if (_animating) return; // 滑动动画引起的坐标变化不算拖动
            if (_edgeHidden && _snapEdge != "None")
            {
                BeginAnimation(LeftProperty, null);
                BeginAnimation(TopProperty, null);
                _edgeHidden = false;
                var p = EdgeShownPos();
                Left = p.X;
                Top = p.Y;
            }
            _snapInTimer?.Stop(); // 一旦重新拖动，取消尚未触发的自动滑入
            // 每次拖动手势开始记一条日志（_moveEndTimer 未在跑 = 距上次"松手"已过 150ms）
            if (_moveEndTimer == null || !_moveEndTimer.IsEnabled)
                MiniPix.Core.Log.Write($"snap: drag begin at ({Left:F0},{Top:F0})");
            _moveEndTimer?.Stop();
            _moveEndTimer?.Start();
            ScheduleWindowStateSave();
        }

        /// <summary>窗口当前是否处于"贴边且滑出隐藏"状态（App 层查询用）</summary>
        public bool IsEdgeHidden => _snapEdge != "None" && _edgeHidden;

        /// <summary>Ctrl+Alt+S：非贴边窗口=显示/隐藏；贴边窗口=滑出/滑回切换</summary>
        public void ToggleShelfViaHotkey()
        {
            if (!IsVisible) { Show(); Activate(); return; }
            if (_snapEdge != "None")
            {
                if (_edgeHidden) PopOutEdge(activate: true);
                else SlideInEdge();
                return;
            }
            Hide();
        }

        /// <summary>托盘/外部要求"显示暂存架"：若正贴边隐藏先滑出，保证完整可见</summary>
        public void EnsureFullyVisible()
        {
            if (IsVisible && _snapEdge != "None" && _edgeHidden)
                PopOutEdge(activate: false); // App.ShowShelf 随后会自己 Activate
        }

        /// <summary>全局热键切标签（2026-11-12 快捷键设置）：0=图 1=文 2=设备。
        /// 窗口隐藏或贴边收起时先完整唤出，让用户能看到切完的结果。</summary>
        public void ActivateTabByIndex(int index)
        {
            var tab = index switch
            {
                0 => ShelfTab.Images,
                1 => ShelfTab.Texts,
                _ => ShelfTab.Devices,
            };
            if (!IsVisible || (_snapEdge != "None" && _edgeHidden))
                ToggleShelfViaHotkey();
            ActivateTab(tab);
        }

        // ---------------- 贴边判定与状态切换 ----------------

        /// <summary>设置贴边状态（None/Top/Right）。None=解除并恢复高度自适应；其余=定尺寸+贴齐+启动轮询</summary>
        private void SetSnapEdge(string edge)
        {
            if (edge == _snapEdge) return;

            if (edge != "None")
            {
                // 记住所靠显示器的工作区边界（DIP）
                double m = GetDpiScale();
                var wa = GetWorkAreaPx(GetHwndSource()?.Handle ?? IntPtr.Zero);
                if (m <= 0) m = 1.0;
                _workTopDip = wa.Top / m;
                _workRightDip = wa.Right / m;
                _workLeftDip = wa.Left / m;

                // SizeToContent=Height 与贴边滑动冲突：贴边期间定死尺寸，解除时恢复自适应
                if (SizeToContent != SizeToContent.Manual)
                {
                    Width = ActualWidth > 0 ? ActualWidth : Width;
                    Height = ActualHeight > 0 ? ActualHeight : Height;
                    SizeToContent = SizeToContent.Manual;
                    _manualSizeBySnap = true;
                }

                _snapEdge = edge;
                // 2026-11-02 反馈：贴边期间（含隐藏只留小边）全程置顶——
                // 否则其他软件全屏时会把小边盖住，哪也点不到。解除贴边时恢复原置顶值。
                if (!Topmost) { _topmostRestore = Topmost; Topmost = true; _topmostForced = true; }
                // 校正到贴边完整显示位（从 config 恢复时窗口可能不在边缘）
                var shown = EdgeShownPos();
                Left = shown.X;
                Top = shown.Y;
                _hideTimer?.Start();
                // 松手贴边 → 600ms 后自动滑入留小边（计划验收标准）。
                // 滑入时若鼠标仍停在判定带内，靠 _bandArmed 防止原地弹回。
                _bandArmed = false;
                _snapInTimer?.Stop();
                _snapInTimer?.Start();
            }
            else
            {
                _snapEdge = "None";
                _hideTimer?.Stop();
                _snapInTimer?.Stop();
                if (_edgeHidden) _edgeHidden = false;
                UpdatePeekStrip();
                if (_topmostForced) { Topmost = _topmostRestore; _topmostForced = false; }
                _userTopmostOff = false; // 重新贴边时恢复"全程置顶"的默认行为
                if (_manualSizeBySnap)
                {
                    _manualSizeBySnap = false;
                    SizeToContent = SizeToContent.Height; // 恢复高度自适应（宽度保持用户当前值）
                }
            }
            SaveWindowStateNow();
        }

        // （原 OnWindowMoveBegin/WndProcHook 已删：WM_ENTERSIZEMOVE/EXITSIZEMOVE 方案实测无效，
        //   升级阶段3修复②改用 OnWindowMoving + _moveEndTimer 纯 WPF 判定，见 InitEdgeSnap 注释）

        /// <summary>拖动/缩放结束（移动事件停 150ms）：贴着上缘→Top，贴着右缘→Right，贴着左缘→Left，否则 None。
        /// 纯 WPF 方案：由 _moveEndTimer 触发，不再依赖 WM_EXITSIZEMOVE。</summary>
        private void DetectSnapAfterMove()
        {
            if (_animating || _edgeHidden) { MiniPix.Core.Log.Write($"snap detect SKIP: animating={_animating} edgeHidden={_edgeHidden}"); return; }
            if (!_config.EnableEdgeSnap) { MiniPix.Core.Log.Write("snap detect SKIP: EnableEdgeSnap=false"); SetSnapEdge("None"); return; }
            double m = GetDpiScale();
            var src = GetHwndSource();
            if (m <= 0 || src == null) { MiniPix.Core.Log.Write($"snap detect SKIP: m={m:F2} hwndSource={(src == null ? "null" : "ok")}"); return; }
            var wa = GetWorkAreaPx(src.Handle);
            double wx = Left * m, wy = Top * m;            // 窗口位置（DIP → 物理px）
            double ww = ActualWidth * m;
            string edge = "None";
            if (wy <= wa.Top + SnapDetectPx) edge = "Top";
            else if (wx + ww >= wa.Right - SnapDetectPx) edge = "Right";
            else if (wx <= wa.Left + SnapDetectPx) edge = "Left"; // 2026-11-10 用户要求：左缘也支持贴边
            // 诊断日志：贴边判定每次松手都会写一条，用户反馈"没效果"时看 log.txt 即可定位
            MiniPix.Core.Log.Write(
                $"snap detect: edge={edge} winPx=({wx:F0},{wy:F0},{ww:F0}x{ActualHeight * m:F0}) " +
                $"work=({wa.Left},{wa.Top},{wa.Right},{wa.Bottom}) m={m:F2}");
            SetSnapEdge(edge);
        }

        // ---------------- 滑出 / 滑回 ----------------

        /// <summary>滑出（从只留小边 → 完整显示）。activate=是否顺带抢焦点（热键弹出抢，hover 弹出不抢）</summary>
        private void PopOutEdge(bool activate)
        {
            if (_snapEdge == "None" || !_edgeHidden || _animating) return;
            _edgeHidden = false;
            _mouseOutsideSince = null;
            _snapInTimer?.Stop(); // hover 弹出时取消尚未触发的自动滑入
            UpdatePeekStrip();
            // 置顶已由 SetSnapEdge 在贴边期间全程保证（轮询还会重申），这里不再临时切换
            AnimateWindowTo(EdgeShownPos());
            if (activate) Activate();
        }

        /// <summary>滑回（完整显示 → 只留 Peek 小边）。
        /// 滑回后解除武装：鼠标若仍停在判定带内不会立刻弹回，离开判定带一次才重新允许弹出。</summary>
        private void SlideInEdge()
        {
            if (_snapEdge == "None" || _edgeHidden || _animating) return;
            _edgeHidden = true;
            _bandArmed = false;
            UpdatePeekStrip();
            // 隐藏态必须保持置顶（小边被盖住就找不到了），不再恢复 Topmost
            AnimateWindowTo(EdgeHiddenPos());
        }

        /// <summary>贴边隐藏时的"彩色小边"显隐/方向（2026-11-10 反馈：三段=三个标签色）。
        /// Left/Right 贴边竖条（靠左/靠右），Top 贴边横条；随 _edgeHidden 变化调用。</summary>
        private void UpdatePeekStrip()
        {
            bool show = _edgeHidden && _snapEdge != "None";
            PeekStrip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
            bool vert = _snapEdge is "Left" or "Right";
            PeekVert.Visibility = vert ? Visibility.Visible : Visibility.Collapsed;
            PeekHorz.Visibility = vert ? Visibility.Collapsed : Visibility.Visible;
            // ★ 小边必须贴"滑入后仍留在屏幕内的那条边"：
            //   Left 贴边窗口向左滑出，屏幕上留的是窗口右缘 8px；Right 反之（留左缘）；
            //   Top 留的是窗口底缘。之前全部放反（贴在滑出屏幕的那条边），条根本不可见，
            //   用户看到的仍是灰色未选中标签 → "只显示一个标签是彩色的"。
            if (vert)
                PeekVert.HorizontalAlignment = _snapEdge == "Left"
                    ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            else
                PeekHorz.VerticalAlignment = VerticalAlignment.Bottom;
        }

        /// <summary>贴边态"完整显示"位置</summary>
        private (double X, double Y) EdgeShownPos() => _snapEdge switch
        {
            "Top" => (Left, _workTopDip),
            "Right" => (_workRightDip - ActualWidth, Top),
            "Left" => (_workLeftDip, Top), // 2026-11-10 左缘贴边
            _ => (Left, Top),
        };

        /// <summary>贴边态"只留小边"位置</summary>
        private (double X, double Y) EdgeHiddenPos() => _snapEdge switch
        {
            "Top" => (Left, _workTopDip - ActualHeight + PeekThickness),
            "Right" => (_workRightDip - PeekThickness, Top),
            "Left" => (_workLeftDip - ActualWidth + PeekThickness, Top), // 2026-11-10 左缘贴边
            _ => (Left, Top),
        };

        /// <summary>Left/Top 双属性并行动画（120ms 缓出），结束后清动画并把最终值写死</summary>
        private void AnimateWindowTo((double X, double Y) target, Action? onDone = null)
        {
            _animating = true;
            int remaining = 2;
            // 2026-11-02 反馈：EaseOut 起步太冲（0 速直接最大加速度），不优雅。
            // 改 EaseInOut：缓起缓停，配合 600ms 时长动作如丝。
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var animX = new DoubleAnimation(target.X, TimeSpan.FromMilliseconds(AnimMs)) { EasingFunction = ease };
            var animY = new DoubleAnimation(target.Y, TimeSpan.FromMilliseconds(AnimMs)) { EasingFunction = ease };
            void Done(object? s, EventArgs e)
            {
                if (--remaining > 0) return;
                BeginAnimation(LeftProperty, null); // 清掉动画保持值
                BeginAnimation(TopProperty, null);
                Left = target.X;
                Top = target.Y;
                _animating = false;
                onDone?.Invoke();
            }
            animX.Completed += Done;
            animY.Completed += Done;
            BeginAnimation(LeftProperty, animX);
            BeginAnimation(TopProperty, animY);
        }

        // ---------------- 隐藏态/弹出态鼠标轮询 ----------------

        private void OnHideTimerTick(object? sender, EventArgs e)
        {
            if (_snapEdge == "None" || _animating) return;
            // S-M4（2026-11-13）：弹出态轮询放宽到 100ms——只为"离开 LeaveHideDelayMs 滑回"计时，
            // 精度足够；隐藏态保持 25ms（碰小边弹出要跟手）。窗口不可见（热键隐藏）直接跳过。
            // 贴边静置（弹出态挂着不动）时 tick 开销从 40 次/秒降到 10 次/秒。
            var want = TimeSpan.FromMilliseconds(_edgeHidden ? HidePollMs : 100);
            if (_hideTimer != null && _hideTimer.Interval != want) _hideTimer.Interval = want;
            if (!IsVisible) return;
            ReassertTopmost(); // 贴边期间持续保证 TOPMOST（全屏软件常把一次性置顶顶掉）
            double m = GetDpiScale();
            if (m <= 0) return;
            var p = System.Windows.Forms.Cursor.Position; // 物理像素
            double mx = p.X / m, my = p.Y / m;            // → DIP

            if (_edgeHidden)
            {
                // 隐藏态：鼠标碰到露出来的小边（PeekThickness + 2 容差）才弹出。
                // 2026-11-03 反馈修复：原来用 HoverBandThickness(32px) 判定带，
                // 鼠标还没到小边就弹出了；现在只有真正碰到 8px 小边附近才触发。
                // _bandArmed：滑入那一刻鼠标多半还在判定带里，必须先离开一次才允许弹出，
                // 否则"贴边→滑入→鼠标还在带内→立刻弹出"会原地弹跳。
                double popBand = Math.Max(2, PeekThickness - 2); // 留边越窄判定带按比例收窄（保底 2px）
                bool inBand = _snapEdge == "Top"
                    ? my <= _workTopDip + popBand &&
                      mx >= Left - 10 && mx <= Left + ActualWidth + 10
                    : _snapEdge == "Left"    // 2026-11-10 左缘小边弹出判定
                    ? mx <= _workLeftDip + popBand &&
                      my >= Top - 10 && my <= Top + ActualHeight + 10
                    : mx >= _workRightDip - popBand &&
                      my >= Top - 10 && my <= Top + ActualHeight + 10;
                if (!inBand) { _bandArmed = true; return; }
                if (_bandArmed) PopOutEdge(activate: false);
            }
            else
            {
                // 弹出态：鼠标离开窗口 600ms → 滑回。
                // 2026-11-02 修复：原来还有 || IsActive 条件——点击窗口后焦点留在窗口上，
                // 鼠标离开也不收回（得去别处点一下才行）。现在只看鼠标位置：在窗口内就不缩，
                // 离开就计时缩回（激活态不再阻挡，正在用=鼠标在里面）。
                bool inside = mx >= Left - 2 && mx <= Left + ActualWidth + 2 &&
                              my >= Top - 2 && my <= Top + ActualHeight + 2;
                if (inside) { _mouseOutsideSince = null; return; }
                _mouseOutsideSince ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - _mouseOutsideSince.Value).TotalMilliseconds >= LeaveHideDelayMs)
                    SlideInEdge();
            }
        }

        // ---------------- 贴边态置顶重申（2026-11-02 反馈） ----------------
        // 问题：其他软件全屏（如浏览器/播放器最大化全屏）后，把只留小边的暂存架盖住，
        //       鼠标推到边缘也碰不到它；手动设置窗口置顶也常被顶掉（WPF Topmost 只置位一次）。
        // 方案：① 贴边期间全程 Topmost=true（SetSnapEdge 置位，解除恢复）；
        //       ② 隐藏/弹出轮询每拍检查原生 WS_EX_TOPMOST 样式，丢了就 SetWindowPos 补回。

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        private static readonly IntPtr HwndTopmost = new(-1);
        private const uint SwpNoMove = 0x0002, SwpNoSize = 0x0001, SwpNoActivate = 0x0010;
        private const int GwlExStyle = -20;
        private const int WsExTopmost = 0x00000008;

        /// <summary>检查并重申 TOPMOST 样式（不抢焦点）。用户手动取消置顶时尊重选择不重申。</summary>
        private void ReassertTopmost()
        {
            if (_userTopmostOff) return;
            var src = GetHwndSource();
            if (src == null) return;
            int ex = GetWindowLong(src.Handle, GwlExStyle);
            if ((ex & WsExTopmost) != 0) return;
            SetWindowPos(src.Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            MiniPix.Core.Log.Write("snap: reassert topmost (style lost, restored)");
        }

        // ---------------- 位置持久化 ----------------

        private void ScheduleWindowStateSave()
        {
            if (_animating || _edgeHidden) return; // 动画/隐藏中的坐标不是逻辑位置，不保存
            _saveTimer?.Stop();
            _saveTimer?.Start();
        }

        /// <summary>立即把窗口位置/尺寸/贴边状态写入 config.json（失败静默）</summary>
        private void SaveWindowStateNow()
        {
            if (_animating || _edgeHidden) return;
            try
            {
                _config.WindowLeft = (int)Math.Round(Left);
                _config.WindowTop = (int)Math.Round(Top);
                _config.WindowWidth = (int)Math.Round(ActualWidth > 0 ? ActualWidth : Width);
                _config.WindowHeight = (int)Math.Round(ActualHeight > 0 ? ActualHeight : Height);
                _config.SnapEdge = _snapEdge;
                _config.Save();
            }
            catch { /* 只读目录等，忽略 */ }
        }

        // ---------------- DPI / 工作区 ----------------

        // （原 WndProcHook 已删：消息方案无效，见 InitEdgeSnap 注释）

        /// <summary>窗口 DPI 缩放系数（DIP → 物理px）</summary>
        private double GetDpiScale()
        {
            var src = System.Windows.PresentationSource.FromVisual(this);
            return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        }

        /// <summary>
        /// 取窗口的 HwndSource（2026-11-02 诊断修复）：
        /// 实测本机 OnSourceInitialized 里 HwndSource.FromVisual 返回 null（原因不明，可能与
        /// WindowChrome/SizeToContent 组合有关），导致贴边判定一直被 _hwndSource==null 拦死。
        /// 改为三重兜底：缓存 → PresentationSource 现取 → WindowInteropHelper 句柄直取。
        /// </summary>
        private System.Windows.Interop.HwndSource? GetHwndSource()
        {
            if (_hwndSource != null) return _hwndSource;
            _hwndSource = System.Windows.PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
            if (_hwndSource == null)
            {
                var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (h != IntPtr.Zero)
                    _hwndSource = System.Windows.Interop.HwndSource.FromHwnd(h);
            }
            return _hwndSource;
        }

        /// <summary>窗口所在显示器的工作区（物理px，含任务栏剔除）。MonitorFromWindow 支持多显示器。</summary>
        private (int Left, int Top, int Right, int Bottom) GetWorkAreaPx(IntPtr hwnd)
        {
            var hMon = MiniPix.Native.NativeMethods.MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new MiniPix.Native.NativeMethods.MONITORINFOEX();
            mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MiniPix.Native.NativeMethods.MONITORINFOEX>();
            if (hMon != IntPtr.Zero && MiniPix.Native.NativeMethods.GetMonitorInfo(hMon, ref mi))
                return (mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);
            // 兜底：主屏工作区（注意是 DIP，非 100% 缩放下略有偏差，仅极端情况触发）
            var w = SystemParameters.WorkArea;
            return ((int)w.Left, (int)w.Top, (int)(w.Left + w.Width), (int)(w.Top + w.Height));
        }


        /// <summary>状态消息（2026-11-02 反馈：状态栏已移除，改写日志保留诊断信息）</summary>
        private void UpdateStatus(string? message = null)
        {
            var msg = message != null
                ? $"{message} · 共 {_store.Shots.Count} 项"
                : $"共 {_store.Shots.Count} 项";
            MiniPix.Core.Log.Write($"status: {msg}");
        }

        // ============ 工具栏按钮事件 ============
        // 2026-11-02 升级阶段1（用户要求）：标题栏和工具栏全部移除，按钮功能并入窗口右键菜单。
        // 原 Btn*_Click 处理器保留（XAML 里的按钮已注释，这些处理器现在只被右键菜单调用）。

        /// <summary>触发区域截图（原工具栏「📷 截图」/ 现右键菜单「截图」共用）：
        /// 隐藏本窗口 → 延迟 200ms 等窗口从屏幕消失 → 启动截图（等同 F1）。</summary>
        private void CaptureNow()
        {
            Hide();
            // 等窗口真正从屏幕消失（含合成器收尾）再启动截图，避免截到窗口残影
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                global::DeskKit.App.TriggerRegionCapture();
            };
            t.Start();
        }

        /// <summary>原工具栏「📷 截图」按钮处理器（现由右键菜单 MenuCapture_Click 调用）</summary>
        private void BtnCapture_Click(object sender, RoutedEventArgs e) => CaptureNow();

        // ============ 2026-11-02 升级阶段1：窗口右键菜单 + 空白处拖动窗口 ============

        /// <summary>右键菜单「📷 截图」</summary>
        private void MenuCapture_Click(object sender, RoutedEventArgs e) => CaptureNow();

        /// <summary>右键菜单「🗑 清空全部」（确认逻辑与原工具栏按钮共用）</summary>
        private void MenuClear_Click(object sender, RoutedEventArgs e) => BtnClear_Click(sender, e);

        /// <summary>右键菜单「📂 打开保存目录」：资源管理器打开自动保存目录。
        /// 未设置/目录不在时提示（设置入口已迁到 设置 → 暂存页）。
        /// 2026-11-11 加全链路日志+成功提示：此前用户反馈"点了没反应"，日志里又无异常，
        /// 加日志定位（点击是否到达、目录值、打开结果）。</summary>
        private void MenuOpenDir_Click(object sender, RoutedEventArgs e)
        {
            var dir = _config.AutoSaveDir;
            MiniPix.Core.Log.Write($"opendir: clicked, dir='{dir}'");
            if (string.IsNullOrWhiteSpace(dir))
            {
                global::DeskKit.App.Notifier("尚未设置自动保存目录（右键 → 设置… → 暂存页）");
                return;
            }
            if (!Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex)
                {
                    MiniPix.Core.Log.Write($"opendir: create dir failed: {ex.Message}");
                    global::DeskKit.App.Notifier($"保存目录不可用：{dir}\n{ex.Message}");
                    return;
                }
            }
            try
            {
                // explorer.exe 显式调用（比 ProcessStartInfo+UseShellExecute 更稳，失败会抛可捕获的异常）
                System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
                MiniPix.Core.Log.Write("opendir: explorer launched ok");
                global::DeskKit.App.Notifier($"已打开保存目录：{dir}");
            }
            catch (Exception ex)
            {
                MiniPix.Core.Log.Write("opendir: explorer launch failed: " + ex);
                global::DeskKit.App.Notifier($"打开目录失败：{ex.Message}");
            }
        }

        /// <summary>右键菜单「📍 窗口置顶」勾选切换（IsCheckable 菜单项，Click 时 IsChecked 已翻转）</summary>
        /// <summary>右键"设置…"：打开设置对话框（非模态单例），保存后实时生效。
        /// 2026-11-15：传输设置已内嵌进设置窗口「传输」页，这里只传"设备改名"回调。</summary>
        private void MenuSettings_Click(object sender, RoutedEventArgs e)
        {
            ShelfSettingsWindow.ShowOrActivate(this, ApplyShelfParams, name =>
            {
                if (_selfDevice != null && name != null) _selfDevice.Name = name;
            });
        }

        /// <summary>打开传输设置窗口（设备标签本机卡与设置对话框共用）。</summary>
        private void OpenTransferSettings()
        {
            DeskKit.Transfer.TransferSettingsWindow.ShowOrActivate(this, name =>
            {
                if (_selfDevice != null && name != null) _selfDevice.Name = name;
            });
        }

        /// <summary>设置保存后实时应用：外观（标签色/背景）+ 暂存参数 + 贴边参数（重定位/刷新小边）。</summary>
        private void ApplyShelfParams()
        {
            ApplyAppearance();
            _store.MaxShots = _config.MaxShots;
            _store.SetThumbSize(_config.ThumbSize);
            _watcher.MaxTextLength = _config.MaxTextLength;
            // 2026-11-14 新功能：级联露边 + 文字卡片字号实时生效
            _store.ApplyCascadePeek(_config.CascadePeekPx);
            _store.ApplyTextFontSize(_config.TextCardFontSize);
            UpdatePeekStrip();
            if (_snapEdge == "None" || _animating) return;
            AnimateWindowTo(_edgeHidden ? EdgeHiddenPos() : EdgeShownPos());
        }

        /// <summary>应用外观配置（2026-11-10 设置窗口"外观"页）：替换标签画刷资源 + 窗口背景。
        /// XAML 里标签/小边全部用 DynamicResource 引用，替换 Window.Resources 条目即全局刷新。</summary>
        private void ApplyAppearance()
        {
            Resources["TabImageBrush"] = BrushFromHex(_config.TabImageColor, Color.FromRgb(0xF2, 0x8C, 0x28));
            Resources["TabTextBrush"] = BrushFromHex(_config.TabTextColor, Color.FromRgb(0x1F, 0xA9, 0xA0));
            Resources["TabDeviceBrush"] = BrushFromHex(_config.TabDeviceColor, Color.FromRgb(0x5C, 0x6B, 0xC0));
            Background = _config.ShelfBgFollowTab
                ? new SolidColorBrush(Lighten(SelectedTabColor(), 1 - _config.ShelfBgTintPct / 100.0)) // 跟随标签：浓度可调
                : BrushFromHex(_config.ShelfBackgroundColor, Color.FromRgb(0xF4, 0xF5, 0xF7));
        }

        /// <summary>窗口背景跟随标签颜色（2026-11-10 外观选项）：背景 = 选中标签色的浅版。
        /// SwitchTab 切签时调用；未开启跟随则不动。</summary>
        private void ApplyBackgroundFollowTab(ShelfTab tab)
        {
            if (!_config.ShelfBgFollowTab) return;
            var key = tab switch
            {
                ShelfTab.Texts => "TabTextBrush",
                ShelfTab.Devices => "TabDeviceBrush",
                _ => "TabImageBrush",
            };
            if (Resources[key] is SolidColorBrush brush)
                Background = new SolidColorBrush(Lighten(brush.Color, 1 - _config.ShelfBgTintPct / 100.0));
        }

        /// <summary>当前选中标签的画刷色（ApplyAppearance 换算背景用）。</summary>
        private Color SelectedTabColor()
        {
            var key = TabDevices.IsChecked == true ? "TabDeviceBrush"
                : TabText.IsChecked == true ? "TabTextBrush"
                : "TabImageBrush";
            return Resources[key] is SolidColorBrush b ? b.Color : Colors.Gray;
        }

        /// <summary>向白色混合 f 比例（f=1 → 全白；f=0 → 原色）。跟随标签背景的浓度由此换算。</summary>
        private static Color Lighten(Color c, double f) => Color.FromRgb(
            (byte)Math.Round(c.R + (255 - c.R) * f),
            (byte)Math.Round(c.G + (255 - c.G) * f),
            (byte)Math.Round(c.B + (255 - c.B) * f));

        private static SolidColorBrush BrushFromHex(string hex, Color fallback)
        {
            try
            {
                var b = (SolidColorBrush?)new BrushConverter().ConvertFromString(hex);
                if (b != null) { b.Freeze(); return b; }
            }
            catch { /* 非法 hex 用兜底色 */ }
            return new SolidColorBrush(fallback);
        }
        private void MenuTopmost_Click(object sender, RoutedEventArgs e)
        {
            Topmost = MenuTopmost.IsChecked;
            // 2026-11-02 反馈：贴边期间默认全程置顶。用户手动取消时尊重选择（轮询不再重申）；
            // 手动勾选视为显式置顶（解除贴边后也保留，不走"恢复"逻辑）。
            _userTopmostOff = !MenuTopmost.IsChecked;
            if (MenuTopmost.IsChecked) _topmostForced = false;
            // 2026-11-05 持久化：记住用户选择，重启后恢复
            _config.ShelfTopmost = MenuTopmost.IsChecked;
            _config.Save();
            UpdateStatus(Topmost ? "窗口已置顶，取消勾选可还原" : "窗口已取消置顶");
        }

        /// <summary>给菜单条目挂矢量图标（MenuIcons，key 见 Common/MenuIcons.cs）。
        /// 注意必须包一层 Image：ImageSource 不是 Visual，ContentPresenter 会把它渲染成 ToString() 文字。</summary>
        private static void SetMenuIcon(System.Windows.Controls.MenuItem mi, string key)
        {
            var src = DeskKit.MenuIcons.Wpf(key);
            if (src == null) return;
            mi.Icon = new System.Windows.Controls.Image { Source = src, Width = 16, Height = 16 };
        }

        /// <summary>菜单打开前刷新勾选态（置顶/截图开关/局域网状态文字/清空菜单项文字）</summary>
        private void OnWindowMenuOpened(object sender, RoutedEventArgs e)
        {
            MenuTopmost.IsChecked = Topmost;
            MenuQuickG.IsChecked = global::DeskKit.App.QuickGEnabled;
            MenuLan.IsChecked = global::DeskKit.App.LanEnabled;
            MenuAutoStart.IsChecked = global::DeskKit.AutoStartHelper.IsEnabled();
            // 2026-11-02 反馈：清空菜单项文字跟随当前轨道（图片/文字分开清）
            MenuClear.Header = TabText.IsChecked == true ? "清空文字" : "清空图片";
        }

        /// <summary>右键菜单「⏻ 退出」（与托盘退出共用 ExitApp）</summary>
        private void MenuExit_Click(object sender, RoutedEventArgs e) => ExitApp();

        // ============ 2026-11-12 统一菜单：补齐与托盘一致的条目 ============

        /// <summary>右键菜单「🖥 全屏截到剪贴板」</summary>
        private void MenuFull_Click(object sender, RoutedEventArgs e) => global::DeskKit.App.CaptureFullNow();

        /// <summary>右键菜单「📌 剪贴板内容贴图」</summary>
        private void MenuPin_Click(object sender, RoutedEventArgs e) => global::DeskKit.App.PastePinNow();

        /// <summary>右键菜单「⌨ Ctrl+G 对话框跳转」开关（IsChecked 点击时已翻转）</summary>
        private void MenuQuickG_Click(object sender, RoutedEventArgs e)
            => global::DeskKit.App.SetQuickG(MenuQuickG.IsChecked);

        /// <summary>右键菜单「🌐 局域网传输」开关</summary>
        private void MenuLan_Click(object sender, RoutedEventArgs e)
            => global::DeskKit.App.SetLan(MenuLan.IsChecked);

        /// <summary>右键菜单「🚀 开机自启」开关</summary>
        private void MenuAutoStart_Click(object sender, RoutedEventArgs e)
        {
            global::DeskKit.AutoStartHelper.SetEnabled(MenuAutoStart.IsChecked);
            UpdateStatus(MenuAutoStart.IsChecked ? "已设置开机自启" : "已取消开机自启");
        }

        /// <summary>右键菜单「❓ 帮助」：渲染程序目录 Help.md</summary>
        private void MenuHelp_Click(object sender, RoutedEventArgs e) => global::DeskKit.App.ShowHelp();

        /// <summary>右键菜单「💡 关于」</summary>
        private void MenuAbout_Click(object sender, RoutedEventArgs e) => global::DeskKit.App.ShowAbout();

        // ---- 托盘菜单调用的公开入口（KitTray 经 App 转发到这里） ----

        /// <summary>托盘「清空当前轨道」：先亮出窗口再走确认流程。</summary>
        public void ClearAllFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            MenuClear_Click(this, new RoutedEventArgs());
        }

        /// <summary>托盘「打开保存目录」。</summary>
        public void OpenSaveDirFromTray() => MenuOpenDir_Click(this, new RoutedEventArgs());

        /// <summary>托盘读置顶状态。</summary>
        public bool IsTopmost => Topmost;

        /// <summary>托盘切换置顶（复用菜单处理器，保持持久化/状态提示一致）。</summary>
        public void SetTopmostFromTray(bool on)
        {
            MenuTopmost.IsChecked = on;
            MenuTopmost_Click(this, new RoutedEventArgs());
        }

        /// <summary>托盘「设置…」。</summary>
        public void OpenSettingsFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            MenuSettings_Click(this, new RoutedEventArgs());
        }

        /// <summary>空白处按住左键拖动窗口（2026-11-02 升级阶段1 用户要求，替代原标题栏拖动）。
        /// 命中检测：按在卡片（ListBoxItem）/ 按钮（ButtonBase）/ 滚动条（ScrollBar）上时
        /// 一律不拖窗口，交给各自的手势（排序 / 点击 / 滚动）；走到根都没命中 → DragMove()。
        /// 与现有手势无冲突：拖拽排序要求按在卡片上且有选中项，单击预览要求命中 ListBoxItem，
        /// 空白处（卡片行之间、状态栏、窗口边缘内圈）二者天然不触发。</summary>
        private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
        {
            // 只响应左键（右键已有 ContextMenu，中键留给别的用途）
            if (e.ChangedButton != MouseButton.Left || e.LeftButton != MouseButtonState.Pressed) return;

            var src = e.OriginalSource as DependencyObject;
            while (src != null)
            {
                if (src is System.Windows.Controls.Primitives.ButtonBase) return; // 卡片内按钮 / 菜单按钮
                if (src is ListBoxItem) return;                                   // 卡片本体（排序/预览手势）
                if (src is System.Windows.Controls.Primitives.ScrollBar) return;  // 滚动条
                if (src is System.Windows.Controls.TextBox) return;               // 文本类控件（防将来加）
                if (src is Window) break;                                         // 走到根了 → 是空白
                src = System.Windows.Media.VisualTreeHelper.GetParent(src);
            }

            try { DragMove(); } catch { /* 非按下状态等异常忽略 */ }
        }

        // 十五次更新: 复制/另存/固定/删除 四个按钮已从工具栏移除
        // （这些操作在卡片 hover 时就地给按钮，工具栏只留 清空/置顶/退出）。

        /// <summary>当前选中项（两个列表互斥，同一时刻只有一个列表有选中项）</summary>
        private ShotItem? _selected;

        /// <summary>程序内部同步选中状态时置 true，避免两个列表互相清空成死循环</summary>
        private bool _syncingSelection;

        /// <summary>某项该属于哪条轨道</summary>
        private ListBox ListFor(ShotItem item) => item.Kind == ShotKind.Text ? LstTexts : LstShots;

        /// <summary>某条轨道是否与给定类型匹配（图片只能排在图片行内）</summary>
        private bool ListMatchesKind(ListBox list, ShotKind kind)
            => ReferenceEquals(list, LstTexts) == (kind == ShotKind.Text);

        /// <summary>选中某项（同时清掉另一条轨道上的选中，保证高亮唯一）</summary>
        private void SelectItem(ShotItem item)
        {
            var list = ListFor(item);
            var other = ReferenceEquals(list, LstShots) ? LstTexts : LstShots;

            _syncingSelection = true;
            try
            {
                if (other.SelectedItem != null) other.SelectedItem = null;
                list.SelectedItem = item;
            }
            finally
            {
                _syncingSelection = false;
            }

            _selected = item;
        }

        /// <summary>复制图片条目到剪贴板（卡片按钮调用；文本另走 CopyTextToClipboard）。
        /// 2026-11-12 返回是否成功，卡片按钮据此做绿色对勾/红色闪烁反馈</summary>
        private bool CopyItemToClipboard(ShotItem item)
        {
            var bmp = _store.GetFullImage(item);
            if (bmp == null) { UpdateStatus("图片数据丢失，无法复制"); return false; }

            try
            {
                // 防回环:这次写入是自己写的,监听器收到通知时直接忽略
                // （S-H3：计数器 + 指纹，见 _suppressDepth 注释）
                _suppressImageSig = ImageSignature(bmp);
                EnterSuppress();

                // 同时放 DIB 与 PNG 两种格式，提高不同软件的粘贴兼容性
                var data = new DataObject();
                data.SetImage(bmp);
                // PNG 格式：部分现代软件优先识别
                using var ms = new System.IO.MemoryStream();
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                enc.Save(ms);
                ms.Position = 0;
                data.SetData("PNG", ms); // (format, stream)

                // 2026-11-12：带重试（他方剪贴板管理器短暂占用 CLIPBRD_E_CANT_OPEN）
                global::DeskKit.App.ClipboardSetWithRetry(data);

                // 关键:SetDataObject 返回后 WM_CLIPBOARDUPDATE 可能还在消息队列里,
                // 必须 PushFrame 排空队列,让挂起的通知先跑完(被 _suppressCapture 挡掉),
                // 再解除抑制。否则解除后迟到的通知会把图又收进去。
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(() => { }));

                UpdateStatus($"第 {item.Index} 项已复制，可去其它软件粘贴");
                return true;
            }
            catch (Exception ex)
            {
                UpdateStatus($"复制失败: {ex.Message}");
                return false;
            }
            finally
            {
                ExitSuppress();
            }
        }

        /// <summary>
        /// 把文本条目写回剪贴板（2026-10-26 十二次更新）。
        /// 2026-11-12 二修：UI 线程重试 300ms 拿不到剪贴板锁（文本 100% 失败且卡 UI），
        /// 改走 App.CopyTextToClipboard 后台 STA 线程（12×120ms≈1.4s 重试窗口），UI 不卡；
        /// 结果在 UI 线程回调 done(true/false)，驱动绿勾/红闪反馈。防回环仍用 _suppressCapture。
        /// </summary>
        private void CopyTextToClipboard(ShotItem item, Action<bool> done)
        {
            var text = _store.GetText(item);
            if (text == null) { UpdateStatus("文本内容丢失，无法复制"); done(false); return; }

            _suppressText = text; // 回环比对用的内容指纹（S-H3）
            EnterSuppress();
            global::DeskKit.App.CopyTextToClipboard(text,
                onFailed: () =>
                {
                    ExitSuppress();
                    UpdateStatus("复制失败: 剪贴板被其它程序持续占用");
                    done(false);
                },
                onOk: () =>
                {
                    // 排空消息队列：让迟到的 WM_CLIPBOARDUPDATE 在抑制期内被忽略
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                        new Action(() => { }));
                    ExitSuppress();
                    UpdateStatus($"第 {item.Index} 条文字已复制（{text.Length} 字）");
                    done(true);
                });
        }

        /// <summary>另存指定项（图片→PNG，文本→TXT；卡片按钮调用）</summary>
        private void SaveItemAs(ShotItem item)
        {
            if (item.Kind == ShotKind.Text)
            {
                SaveTextAs(item);
                return;
            }

            var bmp = _store.GetFullImage(item);
            if (bmp == null) { UpdateStatus("图片数据丢失，无法另存"); return; }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "另存截图",
                Filter = "PNG 图片|*.png",
                FileName = $"pic_{item.CapturedAt:yyyyMMdd_HHmmss}.png",
            };
            if (dlg.ShowDialog(this) == true)
            {
                try
                {
                    ShotStore.SavePng(bmp, dlg.FileName);
                    UpdateStatus($"已保存到 {dlg.FileName}");
                }
                catch (Exception ex)
                {
                    UpdateStatus($"保存失败: {ex.Message}");
                }
            }
        }

        /// <summary>另存文本为 .txt（UTF-8 无 BOM，2026-10-26 十二次更新）</summary>
        private void SaveTextAs(ShotItem item)
        {
            var text = _store.GetText(item);
            if (text == null) { UpdateStatus("文本内容丢失，无法另存"); return; }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "另存文本",
                Filter = "文本文件|*.txt|所有文件|*.*",
                FileName = $"txt_{item.CapturedAt:yyyyMMdd_HHmmss}.txt",
            };
            if (dlg.ShowDialog(this) == true)
            {
                try
                {
                    System.IO.File.WriteAllText(dlg.FileName, text,
                        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    UpdateStatus($"已保存到 {dlg.FileName}");
                }
                catch (Exception ex)
                {
                    UpdateStatus($"保存失败: {ex.Message}");
                }
            }
        }

        /// <summary>卡片右上角📌点击（十次更新：改用隧道阶段事件，确保一定生效）</summary>
        private void PinButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if ((sender as FrameworkElement)?.DataContext is not ShotItem item) return;

            e.Handled = true; // 吃掉事件：不让它继续冒泡触发 ListBox 的选中/拖拽/双击
            TogglePin(item);
        }

        /// <summary>切换固定状态 + 持久化 + 固定项排前</summary>
        private void TogglePin(ShotItem item)
        {
            item.Pinned = !item.Pinned;
            _store.OnPinChanged(); // 写 pins.json + 固定项自动排前

            // 九次更新: 固定后卡片会移到最前，自动滚到可见位置，避免"点了图不见了"
            if (item.Pinned)
            {
                SelectItem(item);
                ListFor(item).ScrollIntoView(item);
            }

            UpdateStatus(item.Pinned ? $"第 {item.Index} 项已固定（排到最前，不会被删除/清空/午夜清理）"
                                     : $"第 {item.Index} 项已取消固定");
        }

        // ---- 卡片 hover 工具条（十次更新：隧道事件，避免 Click 被拖拽逻辑吞掉） ----

        private void CardSave_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if ((sender as FrameworkElement)?.DataContext is not ShotItem item) return;
            e.Handled = true;
            SelectItem(item);
            SaveItemAs(item);
        }

        private void CardCopy_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if ((sender as FrameworkElement)?.DataContext is not ShotItem item) return;
            e.Handled = true;
            SelectItem(item);
            var btn = sender as Button;

            if (item.Kind == ShotKind.Text)
            {
                // 2026-11-12 二修：文本后台线程写剪贴板（UI 不卡），结果回调再反馈绿勾/红闪
                CopyTextToClipboard(item, ok => FlashCopyFeedback(btn, ok));
            }
            else
            {
                bool ok = CopyItemToClipboard(item);
                FlashCopyFeedback(btn, ok); // 成功→绿对勾 900ms / 失败→红 500ms
            }
        }

        /// <summary>复制按钮点击反馈：把按钮里的 Path 换成对勾（绿=成功 / 红=失败），到时还原。
        /// 闪烁未结束又点一次时沿用最初原样，避免把对勾当成"原图"存回去。</summary>
        private void FlashCopyFeedback(Button? btn, bool ok)
        {
            if (btn?.Content is not System.Windows.Shapes.Path p) return;

            Geometry origData;
            Brush origFill;
            if (btn.Tag is System.Windows.Threading.DispatcherTimer pending &&
                pending.Tag is Tuple<Geometry, Brush> saved)
            {
                pending.Stop(); // 上一轮还没还原
                origData = saved.Item1;
                origFill = saved.Item2;
            }
            else
            {
                origData = p.Data;
                origFill = p.Fill;
            }

            if (ok)
            {
                // Material check 图标（960 系坐标，与卡片其它图标一致）
                p.Data = Geometry.Parse("M382-106 191-297l57-57 134 134 351-351 57 58-408 407Z");
                p.Fill = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
            }
            else
            {
                p.Fill = new SolidColorBrush(Color.FromRgb(0xD9, 0x3B, 0x3B));
            }

            var t = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(ok ? 900 : 500) };
            t.Tag = Tuple.Create(origData, origFill);
            btn.Tag = t;
            t.Tick += (_, _) =>
            {
                t.Stop();
                p.Data = origData;
                p.Fill = origFill;
                if (ReferenceEquals(btn.Tag, t)) btn.Tag = null;
            };
            t.Start();
        }

        private void CardDelete_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if ((sender as FrameworkElement)?.DataContext is not ShotItem item) return;
            e.Handled = true;
            SelectItem(item);
            DeleteItem(item);
        }

        /// <summary>删除指定项（卡片按钮调用；2026-11-14 用户要求：固定项不再强制先取消固定，
        /// 改为弹一次确认，确认后直接删除）</summary>
        private void DeleteItem(ShotItem item)
        {
            if (item.Pinned)
            {
                var r = MessageBox.Show(this,
                    $"第 {item.Index} 项已固定，确定要删除吗？",
                    "删除固定项", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }

            _store.Remove(item, force: true);
            if (ReferenceEquals(_selected, item)) _selected = null; // 删掉的项不能再当作选中项
            UpdateStatus($"第 {item.Index} 项已删除");
        }

        /// <summary>清空全部（固定项保留）</summary>
        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            // 2026-11-02 反馈：图片/文字分开清——当前显示哪个轨道就只清哪一类，
            // 另一类不动（原来"清空全部"两个轨道一起清，容易误删）
            bool isTextTab = TabText.IsChecked == true;
            var kind = isTextTab ? ShotKind.Text : ShotKind.Image;
            var label = isTextTab ? "文字" : "图片";

            var pinnedCount = _store.Shots.Count(s => s.Kind == kind && s.Pinned);
            var clearable = _store.Shots.Count(s => s.Kind == kind) - pinnedCount;
            if (clearable == 0)
            {
                UpdateStatus(pinnedCount > 0 ? $"全部 {pinnedCount} 项{label}都是固定的，无法清空" : $"{label}轨道已经是空的");
                return;
            }

            var msg = pinnedCount > 0
                ? $"确定清空 {clearable} 项未固定的{label}？（{pinnedCount} 项固定的会保留）"
                : $"确定清空全部 {clearable} 项{label}？";
            var r = MessageBox.Show(this, msg, "清空确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            _store.ClearKind(kind);
            if (_selected != null && !_store.Shots.Contains(_selected)) _selected = null;
            UpdateStatus($"已清空 {clearable} 项{label}" + (pinnedCount > 0 ? $"（保留 {pinnedCount} 项固定的）" : ""));
        }

        // ============ 两条轨道的交互事件 ============
        // 十三次更新: 界面拆成"图片行 + 文字行"两个 ListBox，同一套处理器挂两处，
        // 用 sender 区分是哪条轨道，不再写死 LstShots。

        /// <summary>单击卡片 → 打开预览窗（2026-11-01 用户要求：双击改单击）。
        /// 排除：拖拽手势（_dragHappened）、卡片上的按钮（📌/💾/📋/🗑）、滚动条等非卡片区域。</summary>
        private void OnListClick(object sender, MouseButtonEventArgs e)
        {
            // 本次手势发生过拖拽（或正在拖）→ 不当点击处理
            if (_isDragging || _dragHappened) return;

            // 命中检测：必须点在卡片（ListBoxItem）本身，且不是卡片里的按钮
            var src = e.OriginalSource as DependencyObject;
            bool onItem = false;
            while (src != null)
            {
                if (src is System.Windows.Controls.Primitives.ButtonBase) return; // 点了卡片按钮
                if (src is ListBoxItem) { onItem = true; break; }
                if (src is ListBox) break; // 点在滚动条/列表空白处
                src = System.Windows.Media.VisualTreeHelper.GetParent(src);
            }
            if (!onItem) return;

            if (sender is not ListBox list || list.SelectedItem is not ShotItem item) return;

            // 2026-11-15 用户要求：点击恢复为预览窗（图片/文字都进预览）；
            // 2026-11-14 曾把图片单击改成直接转贴图，现在贴图/便签统一改为"拖出暂存架"生成，
            // 点击回归预览（图片可缩放，文字可全选复制）。
            var preview = new PreviewWindow(item, _store, _config) { Owner = this };
            preview.Show();
        }

        // 2026-11-01: 双击预览改为单击预览，原双击处理器注释保留
        ///// <summary>双击卡片 → 打开预览窗（图片可缩放/中键拖动，文字可全选复制）</summary>
        //private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
        //{
        //    // 点滚动条等非卡片区域不触发
        //    if (sender is not ListBox list || list.SelectedItem is not ShotItem item) return;
        //
        //    var preview = new PreviewWindow(item, _store) { Owner = this };
        //    preview.Show();
        //}

        /// <summary>两条轨道的选中状态互斥：一边选中就清掉另一边，保证高亮只有一个</summary>
        private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingSelection) return;      // 程序内部同步中，不再回环
            if (sender is not ListBox list) return;

            var other = ReferenceEquals(list, LstShots) ? LstTexts : LstShots;

            if (list.SelectedItem is ShotItem item)
            {
                _selected = item;

                if (other.SelectedItem != null)
                {
                    _syncingSelection = true;
                    try { other.SelectedItem = null; }
                    finally { _syncingSelection = false; }
                }
            }

            // 选中变化时把状态栏切回计数显示
            UpdateStatus();
        }

        // ============ 2026-10-26 十四次更新：滚轮横向滚动 ============

        private ScrollViewer? _shotsScrollViewer;
        private ScrollViewer? _textsScrollViewer;

        /// <summary>
        /// 鼠标滚轮 → 横向滚动。
        /// 两条轨道都是"水平单行 + 超出横向滚动"，滚轮默认的纵向滚动在这里没用，
        /// 所以把滚轮的上下意图翻译成左右移动；本行内容没超出宽度时完全不拦（交回默认）。
        /// </summary>
        /// 2026-11-14 用户要求：鼠标悬停在固定卡片上时，滚轮循环切换级联叠堆顶卡——
        /// 滚轮向下 = 往右切换（固定更晚的卡），向上 = 往左；到头即停不循环；该轨道固定数 ≥ 2 才接管，
        /// 否则放行给下面的横向滚动手势。
        /// </summary>
        /// <summary>2026-11-14 hover 哪张显示哪张：悬停固定卡 = 直接选中（掀到最上）。
        /// 悬停被埋卡的露边时立即完整露出，而不是只放大还被顶卡压着；
        /// 非固定卡忽略。离开轨道由 MouseLeave → ResetPinSelection 恢复默认。</summary>
        private void OnCardMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is System.Windows.Controls.ListBoxItem lbi &&
                lbi.DataContext is ShotItem item)
            {
                _store.HoverPin(item);
            }
        }

        private bool TryPinWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject d) return false;
            var src = d;
            while (src != null && src is not ListBoxItem)
                src = System.Windows.Media.VisualTreeHelper.GetParent(src);
            if (src is not ListBoxItem lbi || lbi.DataContext is not ShotItem item || !item.Pinned) return false;

            int pinnedCount = _store.Shots.Count(s => s.Kind == item.Kind && s.Pinned);
            if (pinnedCount < 2) return false;

            _store.RotatePin(item, toRight: e.Delta < 0);
            e.Handled = true;
            return true;
        }

        /// <summary>2026-11-14 用户要求（二改）：按住 Ctrl 时，在暂存架框架内任意位置滚轮 = 上下切换三个标签，
    /// 下=下一个（图→文→设备），上=上一个；到头即停不循环。
    /// 不按 Ctrl 时一切照旧：卡片上滚轮走固定卡轮换/横向滚动，空白处滚轮无动作。</summary>
    private void OnRootPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if ((System.Windows.Input.Keyboard.Modifiers & ModifierKeys.Control) == 0) return;   // 不按 Ctrl：放行

        System.Windows.Controls.RadioButton[] tabs = { TabImage, TabText, TabDevices };
        int cur = Array.FindIndex(tabs, t => t.IsChecked == true);
        if (cur < 0) return;
        int next = cur + (e.Delta < 0 ? 1 : -1);          // 下=下一个，上=上一个
        if (next < 0 || next >= tabs.Length) return;      // 到头即停，不循环
        tabs[next].IsChecked = true;                      // 触发对应 Checked 处理器切轨道
        e.Handled = true;
    }

    private void OnListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (TryPinWheel(sender, e)) return; // 固定卡滚轮轮换优先
            if (sender is not ListBox list) return;

            ScrollViewer? sv;
            if (ReferenceEquals(list, LstShots))
                sv = _shotsScrollViewer ??= FindChild<ScrollViewer>(list);
            else
                sv = _textsScrollViewer ??= FindChild<ScrollViewer>(list);

            if (sv == null || sv.ScrollableWidth <= 0) return; // 没超出，滚轮不拦

            // e.Delta: 向上滚为正 → 内容往左；向下滚为负 → 内容往右
            var target = sv.HorizontalOffset - e.Delta;
            sv.ScrollToHorizontalOffset(Math.Clamp(target, 0, sv.ScrollableWidth));
            e.Handled = true;
        }

        /// <summary>在视觉树里找第一个指定类型的子元素（要拿 ListBox 模板内部的 ScrollViewer）</summary>
        private static T? FindChild<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null) return null;

            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T hit) return hit;

                var deep = FindChild<T>(child);
                if (deep != null) return deep;
            }
            return null;
        }

        /// <summary>窗口置顶开关（2026-10-26 新增）：
        /// 点一下窗口 Topmost=true（按钮高亮提示"已置顶"），再点取消。
        /// 方便边截图边对照其它窗口。</summary>
        // 2026-11-02 升级阶段1：置顶按钮移入右键菜单（IsCheckable），原按钮版处理器注释保留。
        // 原「已置顶」浅黄底效果随按钮一起移除；置顶状态在状态栏与菜单勾选态里体现。
        //private void BtnTopmost_Click(object sender, RoutedEventArgs e)
        //{
        //    Topmost = !Topmost;
        //    BtnTopmost.Content = Topmost ? "📍 已置顶" : "📍 置顶";
        //    if (Topmost)
        //    {
        //        BtnTopmost.Background = new System.Windows.Media.SolidColorBrush(
        //            System.Windows.Media.Color.FromRgb(0xFF, 0xF3, 0xCD));
        //    }
        //    else
        //    {
        //        BtnTopmost.ClearValue(BackgroundProperty);
        //    }
        //    UpdateStatus(Topmost ? "窗口已置顶，再点一次取消" : "窗口已取消置顶");
        //}

        /// <summary>彻底退出（2026-10-26 新增）：关窗口 + 摘托盘 + 结束进程。
        /// 与点 X（缩到托盘）不同，这是真正的退出。</summary>
        private void BtnExit_Click(object sender, RoutedEventArgs e) => ExitApp();

        /// <summary>
        /// 彻底退出程序（窗口「退出」按钮与托盘「退出」共用同一条路径）。
        /// 二十六次更新修复：以前这里只 Close()，但 App 用的是
        /// ShutdownMode=OnExplicitShutdown —— 关掉窗口不会结束 Application，
        /// 结果托盘图标没了、进程还挂在任务管理器里（僵尸 ShotShelf.exe）。
        /// 现在：标记真退出 → 关窗（OnClosed 释放剪贴板监听与托盘）→ 显式 Shutdown。
        /// </summary>
        public void ExitApp()
        {
            _realExit = true;

            try { Close(); } catch { /* 关闭异常不影响退出 */ }

            System.Windows.Application.Current?.Shutdown();
        }

        /// <summary>窗口关闭时释放监听器与托盘</summary>
        protected override void OnClosed(EventArgs e)
        {
            _watcher?.Dispose();
            // DeskKit 合并改动：原 _tray?.Dispose() 删除（托盘归 KitTray 管，由 App 统一释放）

            // 升级阶段3：退出前最后一次保存窗口状态（隐藏态/动画中则跳过，沿用上次好值）
            SaveWindowStateNow();

            // 兜底：窗口真的关掉了就一定结束进程，别再留后台
            System.Windows.Application.Current?.Shutdown();

            base.OnClosed(e);
        }

        // ============ 2026-11-02 升级阶段3：启动恢复窗口状态 ============

        /// <summary>窗口句柄创建完成：恢复上次位置/尺寸/贴边态。
        /// （原在此处挂 WndProc 监听 WM_ENTERSIZEMOVE，实测 DragMove 时收不到，已弃用。
        ///   _hwndSource 仍保留：GetWorkAreaPx 需要 hwnd 定位所在显示器。）
        /// 坐标超出虚拟屏（20px 容差）时放弃恢复，用系统默认位置。</summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            // 2026-11-02 诊断修复：本机实测 FromVisual 在此处返回 null，改走 GetHwndSource 三重兜底
            _hwndSource = GetHwndSource();
            MiniPix.Core.Log.Write($"OnSourceInitialized: hwndSource={(_hwndSource == null ? "NULL" : "0x" + _hwndSource.Handle.ToString("X"))}");

            try
            {
                // 恢复宽度（窄于 MinWidth 视为无效值）
                if (_config.WindowWidth >= MinWidth)
                    Width = _config.WindowWidth;

                // 恢复位置（虚拟屏范围内才用，容差 20px；DIP 坐标）
                if (_config.WindowLeft != int.MinValue && _config.WindowTop != int.MinValue)
                {
                    double vsL = SystemParameters.VirtualScreenLeft, vsT = SystemParameters.VirtualScreenTop;
                    double vsR = vsL + SystemParameters.VirtualScreenWidth;
                    double vsB = vsT + SystemParameters.VirtualScreenHeight;
                    double l = _config.WindowLeft, t = _config.WindowTop;
                    if (l >= vsL - 20 && l <= vsR - 100 && t >= vsT - 20 && t <= vsB - 80)
                    {
                        Left = l;
                        Top = t;
                    }
                }

                // 恢复贴边态：等布局完成（ActualWidth 可用）再套，Loaded 优先级即可
                if (_config.EnableEdgeSnap &&
                    (_config.SnapEdge == "Top" || _config.SnapEdge == "Right" || _config.SnapEdge == "Left"))
                    Dispatcher.BeginInvoke(new Action(() => SetSnapEdge(_config.SnapEdge)),
                        System.Windows.Threading.DispatcherPriority.Loaded);

                // 2026-11-05 持久化：恢复上次"窗口置顶"选择（原来重启必丢）
                Topmost = _config.ShelfTopmost;
                _userTopmostOff = !_config.ShelfTopmost;   // 与贴边置顶逻辑的约定保持一致：手动取消过就不再强制置顶
            }
            catch (Exception ex)
            {
                MiniPix.Core.Log.Write("restore window state failed: " + ex);
            }
        }

        // ============ S6: 托盘常驻 ============

        /// <summary>点 X 不退出，最小化到托盘（真正的退出走托盘菜单）</summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_realExit)
            {
                e.Cancel = true;
                Hide(); // 藏起来，托盘图标还在
                return;
            }
            base.OnClosing(e);
        }

        /// <summary>托盘菜单"清空未固定"入口（固定项保留，不弹确认——托盘操作视为已确认）</summary>
        public void TrayClearAll()
        {
            var n = _store.ClearUnpinned();
            if (_selected != null && !_store.Shots.Contains(_selected)) _selected = null;
            UpdateStatus(n > 0 ? $"已清空 {n} 项未固定内容" : "没有可清空的未固定内容");
        }

        // ============ 2026-10-26: 卡片拖动排序 ============
        // 十一次更新：拖动时给出三重视觉反馈，解决"拖动毫无反应、松手才知道效果"
        //   ① 幽灵卡片：半透明缩略图跟随鼠标（DragGhostAdorner）
        //   ② 插入指示线：目标卡片左/右边缘亮起蓝色竖线（DragHint 附加属性）
        //   ③ 源卡片半透明 + 落位缩放动画
        // 十三次更新：只在同一条轨道内排序（图片行 ↔ 图片行，文字行 ↔ 文字行），
        //   拖到另一条轨道上时不接受（类型不同，换过去没意义）。

        private System.Windows.Point _dragStartPoint;
        private bool _isDragging;
        private bool _pressedOnButton; // 九次更新: 按下点是否在按钮上（是则禁止拖拽，避免抢点击）
        private bool _dragHappened;    // 2026-11-01: 本次按下后是否真发生过拖拽（单击预览用它排除拖拽手势）

        // ---- 2026-11-15 新功能：拖拽出暂存架 = 变便签；拖到固定堆 = 固定 ----
        private bool _dropHandled;     // 本次 OLE 拖拽是否已被窗口内目标处理（排序/固定），否则松手在架外 = 生成便签
        private bool _dragCancelled;   // 拖拽中按了 Esc（取消，不生成便签）
        private Window? _outGhost;     // 拖出暂存架时跟随鼠标的悬浮幽灵（Adorner 出不了窗口，用独立无边框窗）

        private DragFeedbackAdorner? _dragFeedback;                       // 幽灵+插入蓝线组合 Adorner（2026-11-02 二次修复）
        private System.Windows.Documents.AdornerLayer? _adornerLayer;      // 反馈 Adorner 所在的装饰层
        private ListBoxItem? _sourceContainer;                             // 被拖动的源卡片容器
        private ListBoxItem? _hintContainer;                               // 当前显示插入线的容器

        /// <summary>记录按下位置（拖动排序的起点判定）</summary>
        public void OnListPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            _isDragging = false;
            _dragHappened = false; // 2026-11-01: 新手势开始，重置拖拽标记
            _dropHandled = false;  // 2026-11-15: 新拖拽手势，重置"已被窗口内处理"标记
            _dragCancelled = false;

            // 九次更新: 判断按下点是否落在卡片内的按钮（📌/💾/📋/🗑）上。
            // 若是，本次手势只当点击处理，绝不启动拖拽——否则轻微手抖会吞掉 Click 事件。
            _pressedOnButton = IsButtonUnder(e.OriginalSource as DependencyObject);
        }

        /// <summary>向上查找祖先，判断是否命中 Button（含按钮内部的内容元素）</summary>
        private static bool IsButtonUnder(DependencyObject? element)
        {
            while (element != null)
            {
                if (element is System.Windows.Controls.Primitives.ButtonBase) return true;
                // 撞到 ListBoxItem 还没遇到 Button 就说明点在卡片空白/图片上
                if (element is ListBoxItem) return false;
                element = System.Windows.Media.VisualTreeHelper.GetParent(element);
            }
            return false;
        }

        /// <summary>移动超过阈值且按在列表项上 → 开始拖拽（DoDragDrop 阻塞直至松开）</summary>
        public void OnListPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _isDragging) return;

            // 九次更新: 按在按钮上时不做拖拽（修复"点📌没反应"）
            if (_pressedOnButton) return;

            var diff = e.GetPosition(null) - _dragStartPoint;
            // 水平或垂直移动超过 8px 才算拖拽，避免误触
            if (Math.Abs(diff.X) < 8 && Math.Abs(diff.Y) < 8) return;

            if (sender is not ListBox list) return;

            // 只在列表项上发起拖拽（空白处拖动无意义）
            if (list.SelectedItem is not ShotItem dragged) return;

            _isDragging = true;
            _dragHappened = true; // 2026-11-01: 标记本次手势发生过拖拽，松开时不再当单击处理
            _dropHandled = false;
            _dragCancelled = false;
            try
            {
                StartDragGhost(list, dragged); // 十一次更新: 拎起幽灵卡片

                // 把选中的 ShotItem 作为拖拽数据
                DragDrop.DoDragDrop(list, dragged, System.Windows.DragDropEffects.Move);
            }
            finally
            {
                EndDragGhost(); // 十一次更新: 收拾幽灵与插入线
                CloseOutGhost(); // 2026-11-15: 收掉架外悬浮幽灵
                Mouse.OverrideCursor = null;  // 2026-11-15: 还原架外自定义光标
                _isDragging = false;
                _pressedOnButton = false;

                // 2026-11-15 新功能：松手在两条轨道之外（桌面/别的窗口）且没被窗口内目标处理、
                // 也没按 Esc 取消 → 就地生成便签：图片=贴图（PinWindow），文字=文字便签（TextNoteWindow）
                if (!_dropHandled && !_dragCancelled)
                {
                    var p = System.Windows.Forms.Cursor.Position;
                    if (!IsPointOverLists(p))
                        CreateStickyFromDrag(dragged, p);
                }
                _dropHandled = false;
                _dragCancelled = false;
            }
        }

        // ---------------- 拖动视觉反馈（十一次更新） ----------------

        /// <summary>创建并显示跟随鼠标的幽灵卡片，同时把源卡片调成半透明</summary>
        private void StartDragGhost(ListBox list, ShotItem item)
        {
            try
            {
                MiniPix.Core.Log.Write($"shelf: drag ghost start isText={item.IsText}");

                // 2026-11-02 二次修复：幽灵+蓝线合并为同一个 Adorner（adorn RootGrid）。
                // 原方案幽灵（adorn ListBox）与蓝线（adorn RootGrid）分开，AdornerLayer 渲染顺序
                // 跟随被 adorn 元素的树层级 → 蓝线永远在幽灵下面被盖住。合并后同一
                // VisualCollection 内按添加顺序渲染，蓝线必在幽灵上方。
                _adornerLayer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(RootGrid);
                if (_adornerLayer == null)
                {
                    MiniPix.Core.Log.Write("shelf: drag ghost ABORT - adorner layer null");
                    return;
                }

                // 图片项拎缩略图，文字项拎一段文字预览（十三次更新）
                var src = item.Thumbnail as System.Windows.Media.ImageSource;
                _dragFeedback = new DragFeedbackAdorner(RootGrid, src, item.IsText ? item.TextPreview : null);
                _adornerLayer.Add(_dragFeedback);

                // 立刻摆到鼠标位置，避免第一帧从窗口左上角"飞"过来
                //（RootGrid 与装饰层同原点：都在窗口客户区左上角，坐标可直接互换）
                _dragFeedback.MoveGhost(Mouse.GetPosition(RootGrid));

                // 2026-11-15: 预创建"拖出暂存架"用的悬浮幽灵（Adorner 出不了窗口），
                // 并立刻在屏幕外预显示——OLE 拖拽是模态消息循环，循环中途 Show() 新窗口
                // 不可靠（实测架外幽灵从未出现）；先 Show 停屏幕外、架外只移位置最稳。
                EnsureOutGhost(item);
                PreShowOutGhost();

                // 源卡片弱化，视觉上表示"这张正被搬走"
                if (list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
                {
                    _sourceContainer = container;
                    container.Opacity = 0.35;
                }
            }
            catch (Exception ex)
            {
                // 视觉反馈是锦上添花，任何异常都不能影响排序本身（但记下来便于排查）
                MiniPix.Core.Log.Write("shelf: drag ghost failed: " + ex);
            }
        }

        /// <summary>移除幽灵、恢复源卡片、清掉插入线</summary>
        private void EndDragGhost()
        {
            try
            {
                SetInsertHint(null, false);

                // 2026-11-02 二次修复：幽灵+蓝线在同一 Adorner 里，一并移除
                if (_dragFeedback != null && _adornerLayer != null)
                    _adornerLayer.Remove(_dragFeedback);
                _dragFeedback = null;

                _adornerLayer = null;

                if (_sourceContainer != null)
                {
                    _sourceContainer.Opacity = 1.0;
                    _sourceContainer = null;
                }
            }
            catch { }
        }

        // ================= 2026-11-15 新功能：拖出暂存架 = 变便签 =================

        /// <summary>拖拽全程反馈：架外显示悬浮幽灵 + 全局替换光标（盖掉桌面上的 🚫），架内恢复默认。</summary>
        private void OnListGiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            if (!_isDragging) return;
            var p = System.Windows.Forms.Cursor.Position;
            if (IsPointOverLists(p))
            {
                HideOutGhost();
                Mouse.OverrideCursor = null;    // 架内：默认光标 + 窗口内插入线/固定堆提示照常
                e.UseDefaultCursors = true;
                return;
            }

            // 架外：悬浮幽灵跟随 + 自定义光标
            MoveOutGhost(p);
            Mouse.OverrideCursor = System.Windows.Input.Cursors.SizeAll;
            e.UseDefaultCursors = false;
            e.Handled = true;
        }

        /// <summary>拖拽中按 Esc = 取消（不生成便签）。</summary>
        private void OnListQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
        {
            if (e.EscapePressed) _dragCancelled = true;
        }

        /// <summary>光标（物理像素）是否在两条轨道列表范围内。</summary>
        private bool IsPointOverLists(System.Drawing.Point px)
        {
            foreach (var list in new[] { LstShots, LstTexts })
            {
                if (list.Visibility != Visibility.Visible || list.ActualWidth < 4) continue;
                var tl = list.PointToScreen(new System.Windows.Point(0, 0));
                var br = list.PointToScreen(new System.Windows.Point(list.ActualWidth, list.ActualHeight));
                if (px.X >= tl.X && px.X <= br.X && px.Y >= tl.Y && px.Y <= br.Y) return true;
            }
            return false;
        }

        /// <summary>预创建架外悬浮幽灵（独立无边框窗——Adorner 出不了主窗口）。</summary>
        private void EnsureOutGhost(ShotItem item)
        {
            if (_outGhost != null) return;
            UIElement content;
            if (!item.IsText && item.Thumbnail != null)
            {
                content = new System.Windows.Controls.Image
                {
                    Source = item.Thumbnail,
                    Width = 112,
                    Height = 88,
                    Stretch = Stretch.Uniform,
                };
            }
            else
            {
                content = new TextBlock
                {
                    Text = item.TextPreview,
                    FontSize = 9,
                    LineHeight = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                    MaxHeight = 70,
                };
            }

            var body = new Border
            {
                Background = item.IsText
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xA8))   // 文字 = 便签黄
                    : new SolidColorBrush(Color.FromRgb(0xFB, 0xFB, 0xF8)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xBC)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5),
                Child = content,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 12,
                    ShadowDepth = 2,
                    Opacity = 0.35,
                },
            };
            var panel = new StackPanel();
            panel.Children.Add(body);
            panel.Children.Add(new TextBlock
            {
                Text = "松手 → 贴成便签",
                FontSize = 10,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x30, 0x30, 0x30)),
                Padding = new Thickness(4, 1, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(2, 2, 0, 0),
            });

            _outGhost = new Window
            {
                AllowsTransparency = true,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                IsHitTestVisible = false,   // 幽灵绝不参与命中/落点判定
                Background = Brushes.Transparent,
                Topmost = true,
                SizeToContent = SizeToContent.WidthAndHeight,
                Content = panel,
                Opacity = 0.92,
            };
        }

        /// <summary>拖拽开始前先把幽灵窗显示出来（停在屏幕外）。
        /// OLE 拖拽是模态消息循环，循环中途 Show() 新窗口不可靠；先 Show、后移动最稳。</summary>
        private void PreShowOutGhost()
        {
            if (_outGhost == null || _outGhost.IsVisible) return;
            _outGhost.Left = -30000; _outGhost.Top = 0;   // 先停屏幕外，架外才移回光标处
            _outGhost.Show();                              // ShowActivated=false，不抢焦点
            _outGhost.UpdateLayout();                      // 量出 ActualWidth/Height 供居中
            MiniPix.Core.Log.Write($"shelf: outghost pre-shown size={_outGhost.ActualWidth:0}x{_outGhost.ActualHeight:0}");
        }

        /// <summary>架外幽灵跟随光标（物理像素 → 所在屏 DIP，换算同 PinWindow.PlacePhysical）。
        /// 2026-11-15 二次修复：以光标为中心（与窗内幽灵一致），不再挂在光标右下角。</summary>
        private void MoveOutGhost(System.Drawing.Point px)
        {
            if (_outGhost == null) return;
            var mon = MiniPix.Core.MonitorManager.AtPoint(px.X, px.Y);
            var x = mon.Int32Rect.X + (px.X - mon.Int32Rect.X) / mon.ScaleX;
            var y = mon.Int32Rect.Y + (px.Y - mon.Int32Rect.Y) / mon.ScaleY;

            // 幽灵窗未量出尺寸时用预估兜底（112 图 + 10 padding + 1 边框，加提示条）
            var w = _outGhost.ActualWidth >= 10 ? _outGhost.ActualWidth : 122;
            var h = _outGhost.ActualHeight >= 10 ? _outGhost.ActualHeight : 120;
            _outGhost.Left = x - w / 2;
            _outGhost.Top = y - h / 2;
            if (!_outGhost.IsVisible) PreShowOutGhost();
        }

        private void HideOutGhost()
        {
            if (_outGhost != null && _outGhost.IsVisible)
            {
                // 不用 Hide()：模态拖拽循环里再 Show 不可靠，直接停回屏幕外
                _outGhost.Left = -30000; _outGhost.Top = 0;
            }
        }

        private void CloseOutGhost()
        {
            HideOutGhost();
            _outGhost?.Close();
            _outGhost = null;
        }

        /// <summary>架外松手：图片 → 贴图（PinWindow，光标处）；文字 → 文字便签（TextNoteWindow）。
        /// 均不回写暂存架（卡片本来就在架上）。</summary>
        private void CreateStickyFromDrag(ShotItem item, System.Drawing.Point px)
        {
            try
            {
                if (item.IsText)
                {
                    var text = _store.GetText(item) ?? item.Text ?? "";
                    ShotShelf.TextNoteWindow.ShowNote(
                        text, item, _store, new System.Windows.Point(px.X, px.Y));
                    UpdateStatus("已生成文字便签（可直接编辑，改动回写暂存架）");
                }
                else
                {
                    var img = _store.GetFullImage(item);
                    if (img == null)
                    {
                        UpdateStatus("图片缓存丢失，无法生成贴图便签");
                        return;
                    }

                    // 2026-11-15：贴图以光标为中心（PinWindow.Show 的点是"图片内容左上角"，
                    // 传"光标 - 图片物理尺寸一半"即可居中；DIP→物理按所在屏缩放换算）
                    var mon = MiniPix.Core.MonitorManager.AtPoint(px.X, px.Y);
                    var dpiX = img.DpiX > 0 ? img.DpiX : 96;
                    var dpiY = img.DpiY > 0 ? img.DpiY : 96;
                    var halfPxX = img.PixelWidth * 96.0 / dpiX * mon.ScaleX / 2;
                    var halfPxY = img.PixelHeight * 96.0 / dpiY * mon.ScaleY / 2;
                    MiniPix.Pin.PinWindow.Show(
                        img, new System.Windows.Point(px.X - halfPxX, px.Y - halfPxY), addToShelf: false);
                    UpdateStatus("已贴成图片便签");
                }
            }
            catch (Exception ex)
            {
                MiniPix.Core.Log.Write($"shelf: drag-out sticky failed: {ex.Message}");
                UpdateStatus("生成便签失败，详见日志");
            }
        }

        /// <summary>拖动经过列表：更新幽灵位置 + 计算插入指示线。
        /// 2026-11-15：悬停在固定堆（任一固定卡）上时改为"固定"语义——不显示插入线，
        /// 状态栏提示"松手 = 固定到固定堆"。</summary>
        private void OnListDragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (sender is not ListBox list) return;

            if (e.Data.GetData(typeof(ShotItem)) is not ShotItem dragged)
            {
                e.Effects = System.Windows.DragDropEffects.None;
                e.Handled = true;
                return;
            }

            // 十三次更新：拖到另一条轨道上不接受（图片只能在图片行里排，文字同理）
            if (!ListMatchesKind(list, dragged.Kind))
            {
                e.Effects = System.Windows.DragDropEffects.None;
                SetInsertHint(null, false);
                e.Handled = true;
                return;
            }

            e.Effects = System.Windows.DragDropEffects.Move;

            // 幽灵跟着鼠标走（组合 Adorner：坐标换算到 RootGrid，与装饰层同原点）
            _dragFeedback?.MoveGhost(e.GetPosition(RootGrid));

            var pos = e.GetPosition(list);
            var container = FindContainerAt(list, pos);
            bool overPile = !dragged.Pinned &&
                            container?.Content is ShotItem t && t.Pinned;

            if (overPile)
            {
                // 悬停在固定堆上：松手=固定，不显示插入位置
                SetInsertHint(null, false);
                UpdateStatus("松手 = 固定到固定堆");
            }
            else
            {
                // 实时提示"松手会插到哪儿"
                UpdateInsertHint(list, pos, dragged);
            }

            e.Handled = true;
        }

        /// <summary>拖出列表范围：把插入线收掉，避免残留</summary>
        private void OnListDragLeave(object sender, System.Windows.DragEventArgs e)
        {
            SetInsertHint(null, false);
        }

        /// <summary>
        /// 计算并设置插入指示线。
        /// 判定规则：鼠标落在目标卡片的左半 → 插到它前面（左侧亮）；右半 → 插到它后面（右侧亮）。
        /// </summary>
        private void UpdateInsertHint(ListBox list, System.Windows.Point pos, ShotItem? dragged)
        {
            var container = FindContainerAt(list, pos);

            // 空白处 / 自己身上 / 固定项 → 不给提示（反正也不会移动）
            if (container == null || dragged == null ||
                container.Content is not ShotItem target ||
                ReferenceEquals(target, dragged) ||
                dragged.Pinned || target.Pinned)
            {
                SetInsertHint(null, false);
                return;
            }

            var origin = container.TranslatePoint(new System.Windows.Point(0, 0), list);
            bool after = pos.X > origin.X + container.ActualWidth / 2;
            SetInsertHint(container, after);
        }

        /// <summary>把指示线设到指定容器的左/右侧（自动清掉上一个容器的）。
        /// 2026-11-02 二次修复：蓝线由组合 Adorner（DragFeedbackAdorner）负责，
        /// 在同一 VisualCollection 里渲染在幽灵上方，拖动时始终可见。</summary>
        private void SetInsertHint(ListBoxItem? container, bool after)
        {
            if (_hintContainer != null && !ReferenceEquals(_hintContainer, container))
                DragHint.SetInsertSide(_hintContainer, DragHint.None);

            _hintContainer = container;
            if (container == null)
            {
                _dragFeedback?.HideCaret();
                return;
            }

            DragHint.SetInsertSide(container, after ? DragHint.Right : DragHint.Left);

            // 插入蓝线：目标卡片左/右边缘换算到 RootGrid 坐标系（组合 Adorner 的坐标空间）
            if (_dragFeedback != null)
            {
                var edgeX = after ? container.ActualWidth : 0; // 左插=卡片左缘，右插=卡片右缘
                var pt = container.TranslatePoint(new System.Windows.Point(edgeX, 0), RootGrid);
                _dragFeedback.ShowCaret(pt.X, pt.Y, container.ActualHeight);
            }
        }

        /// <summary>松手落在另一个卡片上 → 把拖动的项移动到目标位置</summary>
        public void OnListDrop(object sender, System.Windows.DragEventArgs e)
        {
            if (sender is not ListBox list) return;
            if (e.Data.GetData(typeof(ShotItem)) is not ShotItem dragged) return;

            // 松手瞬间先把提示收干净，避免残留到拖拽结束之后
            SetInsertHint(null, false);

            // 不同轨道的类型不匹配 → 静默忽略（DragOver 已把光标标成禁止）
            if (!ListMatchesKind(list, dragged.Kind)) return;

            var pos = e.GetPosition(list);
            var container = FindContainerAt(list, pos);

            if (container?.Content is not ShotItem target) return;
            if (ReferenceEquals(target, dragged)) return;

            // ---- 2026-11-15 新功能：未固定卡拖到固定堆（任一固定卡）上松手 = 固定 ----
            // 固定组按 PinnedAt 排序，新固定的自然成为堆顶卡
            if (target.Pinned)
            {
                if (dragged.Pinned)
                {
                    UpdateStatus("固定项的位置不参与拖动排序");
                    return;
                }
                dragged.Pinned = true;
                _store.OnPinChanged();
                _dropHandled = true;
                SelectItem(dragged);
                UpdateStatus("已固定到固定堆");
                return;
            }

            // 固定项不参与拖动换位（保持它们位置稳定）
            if (dragged.Pinned)
            {
                UpdateStatus("固定项的位置不参与拖动排序");
                return;
            }

            // 排序始终落在主集合 Shots 上；两个镜像集合会跟着同步，
            // 因此这里用的是"整表索引"，同类型项之间的相对次序就是轨道里的次序。
            var all = _store.Shots;
            int oldIdx = all.IndexOf(dragged);
            int tgtIdx = all.IndexOf(target);
            if (oldIdx < 0 || tgtIdx < 0) return;

            // 与指示线同一套判定：落在目标左半=插到它前面，右半=插到它后面
            var origin = container.TranslatePoint(new System.Windows.Point(0, 0), list);
            bool after = pos.X > origin.X + container.ActualWidth / 2;

            // 目标插入位：先算出"插到目标的哪一边"，再修正移除自身导致的索引前移
            int insertAt = tgtIdx + (after ? 1 : 0);
            if (oldIdx < insertAt) insertAt--;

            if (insertAt == oldIdx) return; // 位置没变，不动
            if (insertAt < 0) insertAt = 0;
            if (insertAt >= all.Count) insertAt = all.Count - 1;

            all.Move(oldIdx, insertAt); // ObservableCollection.Move → 界面自动刷新
            _dropHandled = true;        // 2026-11-15: 窗口内已处理，松手后不再生成便签
            SelectItem(dragged);
            PlayLandAnimation(list, dragged);  // 落位回弹，明确"已经放到这里了"
            UpdateStatus($"已移动到第 {insertAt + 1} 位");
        }

        /// <summary>落位动画：卡片轻微放大后回弹到原尺寸，给一个"放稳了"的反馈</summary>
        private void PlayLandAnimation(ListBox list, ShotItem item)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (list.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container) return;

                container.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                var scale = new System.Windows.Media.ScaleTransform(1.06, 1.06);
                container.RenderTransform = scale;

                var anim = new System.Windows.Media.Animation.DoubleAnimation(
                    1.06, 1.0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                    },
                };
                scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, anim);
                scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, anim);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>坐标 → 该列表里的 ListBoxItem 容器（拖动逻辑统一用它）</summary>
        private ListBoxItem? FindContainerAt(ListBox list, System.Windows.Point pos)
        {
            var element = list.InputHitTest(pos) as DependencyObject;
            while (element != null && element is not ListBoxItem)
                element = System.Windows.Media.VisualTreeHelper.GetParent(element);
            return element as ListBoxItem;
        }

        /// <summary>坐标 → 该处的 ShotItem（通过 ListBoxItem 容器反查）</summary>
        private ShotItem? GetItemAt(ListBox list, System.Windows.Point pos)
            => FindContainerAt(list, pos)?.Content as ShotItem;
    }
}
