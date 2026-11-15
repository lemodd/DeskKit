using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace ShotShelf
{
    /// <summary>
    /// 暂存架设置窗口（2026-11-10 反馈二轮：不能只有贴边参数，加外观/暂存/传输页）。
    /// 纯代码建窗（TabControl 七页），非模态、单例：ShowOrActivate。
    /// 页1 外观：颜色 / 卡片显示；页2 贴边；页3 暂存：目录 / 容量与缓存；
    /// 页4 长截图；页5 贴图：边框 / 阴影；页6 快捷键；页7 传输（内嵌本机服务+连接其它电脑）。
    /// 2026-11-15：无保存按钮——任何改动 500ms 防抖后自动保存并实时生效
    /// （快捷键页自带按下即生效+失败回滚；全部存 config.json）。
    /// 2026-11-15 三轮：分组卡片式布局（仿 Fluent 设置）——窗口 520×600，
    /// 每页若干「小标题 + 圆角白卡片」，卡片内一行一项：左标签右控件，同类项归组。
    /// 2026-11-15 四轮：传输设置不再弹独立窗口，原 TransferSettingsWindow 内容
    /// （设备名/服务网址/端口/接收目录/连接其它电脑）全部内嵌「传输」页。
    /// </summary>
    public sealed class ShelfSettingsWindow : Window
    {
        private static ShelfSettingsWindow? _wnd; // 单例

        private readonly Action _onApplied;          // 保存后回调（MainWindow 应用）
        private readonly Action<string?> _onRenamed; // 传输页改设备名后回调（MainWindow 刷新本机卡）

        private readonly TextBlock _txtMsg;

        // 外观页：色块按钮（点击弹取色器）+ 当前色
        private readonly Dictionary<string, Button> _swatches = new();
        private readonly Dictionary<string, Color> _colors = new();
        private static readonly Dictionary<string, Color> DefColors = new()
        {
            ["TabImageColor"] = Color.FromRgb(0xF2, 0x8C, 0x28),
            ["TabTextColor"] = Color.FromRgb(0x1F, 0xA9, 0xA0),
            ["TabDeviceColor"] = Color.FromRgb(0x5C, 0x6B, 0xC0),
            ["ShelfBackgroundColor"] = Color.FromRgb(0xF4, 0xF5, 0xF7),
            ["PreviewBackgroundColor"] = Color.FromRgb(0xEC, 0xED, 0xEF),
            ["PinBorderColor"] = Color.FromRgb(0x9A, 0xA0, 0xA6),   // 贴图页
        };

        // 贴边页滑块（解构赋值，不能 readonly）
        private Slider _sPeek, _sDetect, _sLeave, _sSnapIn, _sAnim;
        // 暂存页控件
        private Slider _sMaxShots, _sThumb;
        private CheckBox _chkText, _chkDisk;
        private TextBox _txtMaxLen;
        private TextBlock _txtSaveDir = null!;   // 自动保存目录当前路径显示
        // 长截图页滑块
        private Slider _sLongNotches, _sLongDelay;
        // 外观页：背景跟随标签颜色
        private CheckBox _chkFollow;
        private Slider _sTint;
        // 级联露边 / 文字卡片字号 / 贴图边框阴影
        private Slider _sCascadePeek, _sTextFont;
        private Slider _sPinBorder, _sPinShadowBlur, _sPinShadowDepth, _sPinShadowOpacity;
        private CheckBox _chkPinShadow;
        // 2026-11-15 传输设置内嵌进「传输」标签页（不再弹独立窗口）
        private TextBox _txtDevName = null!;
        private TextBlock _txtLanUrl = null!, _txtClientStatus = null!, _txtReceiveDir = null!;
        private TextBox _txtServerUrl = null!;
        private Button _btnConnect = null!, _btnDisconnect = null!, _btnForget = null!;
        private StackPanel _clientCtlRow = null!;
        private Action? _clientStateHandler;

        private ShelfSettingsWindow(Window owner, Action onApplied, Action<string?> onRenamed)
        {
            _onApplied = onApplied;
            _onRenamed = onRenamed;
            // 2026-11-15 用户要求：设置不再有"保存"按钮——任何改动自动保存、自动生效。
            // 500ms 防抖：拖滑块/改颜色时值连续变化，停手后统一落盘一次。
            _autoSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500),
            };
            _autoSaveTimer.Tick += (_, _) => { _autoSaveTimer.Stop(); Save(); };
            var cfg = AppConfig.Current;

            Title = "暂存架设置";
            Width = 520;    // 2026-11-15 三轮：收窄窗口，滑块不需要通栏宽
            Height = 600;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Icon = IconFactory.GetWpfIcon();   // 2026-11-12 统一程序图标
            Owner = owner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF6));
            UseLayoutRounding = true;

            // ---- 读入当前颜色 ----
            foreach (var key in DefColors.Keys)
            {
                try
                {
                    var c = (Color?)ColorConverter.ConvertFromString(
                        key == "PreviewBackgroundColor" ? cfg.PreviewBackgroundColor
                        : key == "ShelfBackgroundColor" ? cfg.ShelfBackgroundColor
                        : key == "TabImageColor" ? cfg.TabImageColor
                        : key == "TabTextColor" ? cfg.TabTextColor
                        : key == "PinBorderColor" ? cfg.PinBorderColor
                        : cfg.TabDeviceColor);
                    _colors[key] = c ?? DefColors[key];
                }
                catch { _colors[key] = DefColors[key]; }
            }

            // ---- 主体：标签页 + 底部按钮行 ----
            var root = new Grid { Margin = new Thickness(14, 10, 14, 10) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var tabs = new TabControl { BorderThickness = new Thickness(0, 1, 0, 0), Background = Brushes.Transparent };
            tabs.Items.Add(BuildAppearanceTab());
            tabs.Items.Add(BuildEdgeTab(cfg));
            tabs.Items.Add(BuildStoreTab(cfg));
            tabs.Items.Add(BuildLongTab(cfg));
            tabs.Items.Add(BuildPinTab(cfg));
            tabs.Items.Add(BuildHotkeyTab(cfg));
            tabs.Items.Add(BuildTransferTab());
            Grid.SetRow(tabs, 0);
            root.Children.Add(tabs);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            // 2026-11-15：保存按钮已移除——所有改动自动保存自动生效
            btnRow.Children.Add(SecondBtn("恢复默认", (_, _) =>
            {
                _sPeek.Value = 4; _sDetect.Value = 12; _sLeave.Value = 600; _sSnapIn.Value = 600; _sAnim.Value = 600;
                _sMaxShots.Value = 50; _sThumb.Value = 160; _txtMaxLen.Text = "200000";
                _chkText.IsChecked = true; _chkDisk.IsChecked = true;
                foreach (var key in DefColors.Keys) SetColor(key, DefColors[key]);
                _chkFollow.IsChecked = false;
                _sTint.Value = 12;
                _sCascadePeek.Value = 13; _sTextFont.Value = 10.5;
                _sPinBorder.Value = 1; _chkPinShadow.IsChecked = true;
                _sPinShadowBlur.Value = 8; _sPinShadowDepth.Value = 2; _sPinShadowOpacity.Value = 40;
                _sLongNotches.Value = 2; _sLongDelay.Value = 500;
                ResetHotkeys();
                Save();
            }));
            btnRow.Children.Add(SecondBtn("关闭", (_, _) => Close()));
            Grid.SetRow(btnRow, 1);
            root.Children.Add(btnRow);

            _txtMsg = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
                Margin = new Thickness(0, 6, 0, 0),
            };
            Grid.SetRow(_txtMsg, 2);
            root.Children.Add(_txtMsg);

            Content = root;
            Closed += (_, _) =>
            {
                _wnd = null;
                _autoSaveTimer.Stop();     // 关窗后不再自动保存
                if (_clientStateHandler != null)
                    global::DeskKit.Transfer.LanClient.ClientChanged -= _clientStateHandler; // 传输页退订
            };
        }

        /// <summary>2026-11-15 自动保存防抖定时器（500ms）。</summary>
        private readonly System.Windows.Threading.DispatcherTimer _autoSaveTimer;

        /// <summary>任何设置控件变化都进这里：停表重来，停手 500ms 后统一保存生效。</summary>
        private void ScheduleAutoSave()
        {
            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }

        // ================= 页1 外观 =================

        private TabItem BuildAppearanceTab()
        {
            var p = NewPage();

            // ---- 卡片：颜色（所有颜色项归在一起）----
            var cColor = Card(p, "颜色");
            cColor.Children.Add(Note("点击色块弹出系统取色器；改动自动保存并立即生效。"));
            cColor.Children.Add(ColorRow("TabImageColor", "「图」标签颜色"));
            cColor.Children.Add(ColorRow("TabTextColor", "「文」标签颜色"));
            cColor.Children.Add(ColorRow("TabDeviceColor", "「设备」标签颜色"));
            cColor.Children.Add(ColorRow("ShelfBackgroundColor", "窗口背景色"));
            cColor.Children.Add(ColorRow("PreviewBackgroundColor", "预览大图背景色"));
            _chkFollow = AddCheckRow(cColor, "背景跟随标签颜色", "切图/文/设备时窗口背景跟着换",
                AppConfig.Current.ShelfBgFollowTab);
            _chkFollow.Checked += (_, _) => { _swatches["ShelfBackgroundColor"].IsEnabled = false; _sTint.IsEnabled = true; };
            _chkFollow.Unchecked += (_, _) => { _swatches["ShelfBackgroundColor"].IsEnabled = true; _sTint.IsEnabled = false; };
            if (AppConfig.Current.ShelfBgFollowTab) _swatches["ShelfBackgroundColor"].IsEnabled = false; // 初始状态
            _sTint = AddSliderRow(cColor, "背景深浅度", "跟随标签时的浓度", 0, 100, 5, AppConfig.Current.ShelfBgTintPct, "%");

            // ---- 卡片：卡片显示 ----
            var cCard = Card(p, "卡片显示");
            _sCascadePeek = AddSliderRow(cCard, "固定卡片叠放露边宽度", "叠放时每张卡露出的边条", 4, 40, 1, AppConfig.Current.CascadePeekPx, "px");
            _sTextFont = AddSliderRow(cCard, "文字卡片字号", null, 8, 16, 0.5, AppConfig.Current.TextCardFontSize, "px");

            return NewTab("外观", p);
        }

        private UIElement ColorRow(string key, string label)
        {
            var swatch = new Button
            {
                Width = 52,
                Height = 22,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE1)),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = new SolidColorBrush(_colors[key]),
                Tag = key,
            };
            swatch.Click += (_, _) => PickColor(key);
            _swatches[key] = swatch;
            return Row(label, null, swatch);
        }

        private void PickColor(string key)
        {
            using var dlg = new WinForms.ColorDialog
            {
                Color = System.Drawing.Color.FromArgb(_colors[key].R, _colors[key].G, _colors[key].B),
                FullOpen = true,
            };
            if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;
            SetColor(key, Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
        }

        private void SetColor(string key, Color c)
        {
            _colors[key] = c;
            _swatches[key].Background = new SolidColorBrush(c);
            ScheduleAutoSave();                               // 2026-11-15 自动保存
        }

        // ================= 页2 贴边 =================

        private TabItem BuildEdgeTab(AppConfig cfg)
        {
            var p = NewPage();
            p.Children.Add(Note("拖到屏幕边缘松手贴边、自动收起只留小边——以下参数控制整个流程。"));
            var c = Card(p, "贴边行为");
            _sPeek = AddSliderRow(c, "留边宽度", "收起后露出的小边", 2, 12, 1, cfg.PeekThickness, "px");
            _sDetect = AddSliderRow(c, "贴边检测范围", "松手时离边缘多近算贴上", 6, 30, 1, cfg.SnapDetectPx, "px");
            _sLeave = AddSliderRow(c, "滑回延时", "鼠标离开后等多久滑回", 200, 2000, 50, cfg.LeaveHideDelayMs, "ms");
            _sSnapIn = AddSliderRow(c, "收起延时", "贴边后等多久自动收起", 200, 2000, 50, cfg.SnapInDelayMs, "ms");
            _sAnim = AddSliderRow(c, "动画时长", "滑出/滑回动画", 100, 1000, 20, cfg.AnimMs, "ms");
            return NewTab("贴边", p);
        }

        // ================= 页3 暂存 =================

        private TabItem BuildStoreTab(AppConfig cfg)
        {
            var p = NewPage();

            // ---- 卡片：自动保存目录（2026-11-11 从右键菜单迁入）----
            var cDir = Card(p, "自动保存目录");
            var dirRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 2) };
            _txtSaveDir = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(cfg.AutoSaveDir) ? "未设置（截图不自动落盘）" : cfg.AutoSaveDir,
                FontSize = 11,
                MaxWidth = 300,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = string.IsNullOrWhiteSpace(cfg.AutoSaveDir) ? null : cfg.AutoSaveDir,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x82, 0x8C)),
            };
            dirRow.Children.Add(_txtSaveDir);
            cDir.Children.Add(dirRow);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
            btnRow.Children.Add(SecondBtn("更改目录…", (_, _) => PickAutoSaveDir(), first: true));
            btnRow.Children.Add(SecondBtn("打开目录", (_, _) =>
            {
                var dir = AppConfig.Current.AutoSaveDir;
                MiniPix.Core.Log.Write($"opendir: from settings, dir='{dir}'");
                if (!string.IsNullOrWhiteSpace(dir) && System.IO.Directory.Exists(dir))
                {
                    try
                    {
                        System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
                        return;
                    }
                    catch (Exception ex) { MiniPix.Core.Log.Write("opendir: failed: " + ex.Message); }
                }
                global::DeskKit.App.Notifier("目录不存在，请先「更改目录」");
            }));
            cDir.Children.Add(btnRow);
            cDir.Children.Add(Note("每张截图/文字都自动另存一份到这里（按时间命名，永不自动删除）。"));

            // ---- 卡片：容量与缓存 ----
            var cCap = Card(p, "容量与缓存");
            _sMaxShots = AddSliderRow(cCap, "暂存上限", "超出自动淘汰最旧未固定项", 10, 500, 10, cfg.MaxShots, "条");
            _sThumb = AddSliderRow(cCap, "缩略图最长边", "对新入库的图生效", 96, 320, 8, cfg.ThumbSize, "px");
            _chkText = AddCheckRow(cCap, "捕获剪贴板文字", "关掉后只暂存图片（重启生效）", cfg.EnableTextCapture);
            var maxLenRow = Row("单条文字上限", "超出截断（字符）", new TextBox
            {
                Text = cfg.MaxTextLength.ToString(),
                Width = 80,
                Height = 26,
                VerticalContentAlignment = VerticalAlignment.Center,
            });
            cCap.Children.Add(maxLenRow);
            _txtMaxLen = (TextBox)((Grid)maxLenRow).Children[1];
            _txtMaxLen.TextChanged += (_, _) => ScheduleAutoSave();   // 2026-11-15 自动保存
            _chkDisk = AddCheckRow(cCap, "磁盘缓存", "重启后暂存不丢的关键，建议开（重启生效）", cfg.EnableDiskCache);

            return NewTab("暂存", p);
        }

        /// <summary>选自动保存目录：立即写配置并生效（原右键菜单"设置自动保存目录"逻辑迁入）。</summary>
        private void PickAutoSaveDir()
        {
            var cfg = AppConfig.Current;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择自动保存目录",
                CheckFileExists = false,
                ValidateNames = false,
                FileName = "选择这个文件夹", // 提示用户直接点"打开"
            };
            if (!string.IsNullOrWhiteSpace(cfg.AutoSaveDir) && System.IO.Directory.Exists(cfg.AutoSaveDir))
                dlg.InitialDirectory = cfg.AutoSaveDir;
            if (dlg.ShowDialog(this) != true) return;

            var dir = System.IO.Path.GetDirectoryName(dlg.FileName);
            if (string.IsNullOrEmpty(dir)) return;
            try { System.IO.Directory.CreateDirectory(dir); } catch { }

            cfg.AutoSaveDir = dir;
            cfg.Save();
            _txtSaveDir.Text = dir;
            _txtSaveDir.ToolTip = dir;
            MiniPix.Core.Log.Write($"shelf: autosave dir -> {dir}");
        }

        // ================= 页4 长截图 =================

        private TabItem BuildLongTab(AppConfig cfg)
        {
            var p = NewPage();
            var c = Card(p, "滚动节奏");
            c.Children.Add(Note("长截图（框选后自动/手动滚动拼接）的滚动节奏：\n步长小+延时长 = 慢而稳，拼接重叠充足；步长大+延时短 = 快。"));
            _sLongNotches = AddSliderRow(c, "每步滚动量", "滚轮刻度，1 刻度 ≈ 120px", 1, 10, 1, cfg.LongScrollNotches, "刻度");
            _sLongDelay = AddSliderRow(c, "每步等待时间", "等页面渲染稳定", 200, 1500, 50, cfg.LongScrollDelayMs, "ms");
            return NewTab("长截图", p);
        }

        // ================= 页5 贴图（边框颜色/粗细 + 阴影） =================

        private TabItem BuildPinTab(AppConfig cfg)
        {
            var p = NewPage();
            p.Children.Add(Note("改动自动保存，对所有贴图（含已打开的）立即生效。"));

            // ---- 卡片：边框 ----
            var cBorder = Card(p, "边框");
            cBorder.Children.Add(ColorRow("PinBorderColor", "贴图边框颜色"));
            _sPinBorder = AddSliderRow(cBorder, "贴图边框粗细", "0 = 无边框", 0, 10, 0.5, cfg.PinBorderThickness, "px");

            // ---- 卡片：阴影 ----
            var cShadow = Card(p, "阴影");
            _chkPinShadow = AddCheckRow(cShadow, "启用贴图阴影", null, cfg.PinShadowEnabled);
            _sPinShadowBlur = AddSliderRow(cShadow, "模糊半径", null, 0, 40, 1, cfg.PinShadowBlur, "px");
            _sPinShadowDepth = AddSliderRow(cShadow, "偏移", "阴影离贴图的距离", 0, 20, 1, cfg.PinShadowDepth, "px");
            _sPinShadowOpacity = AddSliderRow(cShadow, "浓度", null, 0, 100, 5, cfg.PinShadowOpacityPct, "%");

            return NewTab("贴图", p);
        }

        // ================= 页6 快捷键（2026-11-12） =================

        private TextBox? _hkCapture, _hkShelf, _hkTab1, _hkTab2, _hkTab3;
        private readonly Dictionary<TextBox, Action<int, uint>> _hkApply = new();

        private TabItem BuildHotkeyTab(AppConfig cfg)
        {
            var p = NewPage();
            p.Children.Add(Note("点击输入框后直接按下想要的按键组合（可含 Ctrl/Alt/Shift），按下即生效。\nEsc 取消恢复原值。纯字母数字键必须带修饰键，F1~F12 可单独用；注册失败自动回滚。"));

            var c = Card(p, "全局热键");
            int oCapVk = cfg.HotkeyCaptureVk, oCapMods = cfg.HotkeyCaptureMods;
            _hkCapture = HotkeyRow(c, "截图", oCapVk, (uint)oCapMods, (vk, mods) =>
                ApplyHotkeyWithRollback("截图",
                    () => { cfg.HotkeyCaptureVk = vk; cfg.HotkeyCaptureMods = (int)mods; },
                    () => { cfg.HotkeyCaptureVk = oCapVk; cfg.HotkeyCaptureMods = oCapMods; }));
            int oShelfVk = cfg.HotkeyShelfVk, oShelfMods = cfg.HotkeyShelfMods;
            _hkShelf = HotkeyRow(c, "弹出/收回暂存架", oShelfVk, (uint)oShelfMods, (vk, mods) =>
                ApplyHotkeyWithRollback("弹出/收回暂存架",
                    () => { cfg.HotkeyShelfVk = vk; cfg.HotkeyShelfMods = (int)mods; },
                    () => { cfg.HotkeyShelfVk = oShelfVk; cfg.HotkeyShelfMods = oShelfMods; }));
            int oT1Vk = cfg.HotkeyTab1Vk, oT1Mods = cfg.HotkeyTab1Mods;
            _hkTab1 = HotkeyRow(c, "切「图」标签", oT1Vk, (uint)oT1Mods, (vk, mods) =>
                ApplyHotkeyWithRollback("切「图」标签",
                    () => { cfg.HotkeyTab1Vk = vk; cfg.HotkeyTab1Mods = (int)mods; },
                    () => { cfg.HotkeyTab1Vk = oT1Vk; cfg.HotkeyTab1Mods = oT1Mods; }));
            int oT2Vk = cfg.HotkeyTab2Vk, oT2Mods = cfg.HotkeyTab2Mods;
            _hkTab2 = HotkeyRow(c, "切「文」标签", oT2Vk, (uint)oT2Mods, (vk, mods) =>
                ApplyHotkeyWithRollback("切「文」标签",
                    () => { cfg.HotkeyTab2Vk = vk; cfg.HotkeyTab2Mods = (int)mods; },
                    () => { cfg.HotkeyTab2Vk = oT2Vk; cfg.HotkeyTab2Mods = oT2Mods; }));
            int oT3Vk = cfg.HotkeyTab3Vk, oT3Mods = cfg.HotkeyTab3Mods;
            _hkTab3 = HotkeyRow(c, "切「设备」标签", oT3Vk, (uint)oT3Mods, (vk, mods) =>
                ApplyHotkeyWithRollback("切「设备」标签",
                    () => { cfg.HotkeyTab3Vk = vk; cfg.HotkeyTab3Mods = (int)mods; },
                    () => { cfg.HotkeyTab3Vk = oT3Vk; cfg.HotkeyTab3Mods = oT3Mods; }));

            p.Children.Add(Note("\n其余固定热键：Ctrl+Alt+A / Ctrl+Shift+C / Ctrl+Alt+C（截图）、Ctrl+Alt+V（贴图）、Ctrl+G（跳转）。"));
            return NewTab("快捷键", p);
        }

        /// <summary>一行 = 标签 + 热键输入框（只读，点击捕获按键组合）。按下组合立即写配置并重注册。</summary>
        private TextBox HotkeyRow(StackPanel p, string label, int vk, uint mods, Action<int, uint> apply)
        {
            var box = new TextBox
            {
                Text = HotkeyText(vk, mods),
                Width = 140,
                Height = 26,
                IsReadOnly = true,
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "点击后按下新按键组合；Esc 取消",
                Background = Brushes.White,
            };
            box.Tag = box.Text;   // 原文本（Esc/失焦恢复用）
            box.PreviewKeyDown += HotkeyBox_PreviewKeyDown;
            box.GotFocus += (_, _) => box.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xDD));
            box.LostFocus += (_, _) => { box.Background = Brushes.White; box.Text = (string)box.Tag; };
            _hkApply[box] = apply;
            p.Children.Add(Row(label, null, box));
            return box;
        }

        private void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            var box = (TextBox)sender;
            e.Handled = true;
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            // 纯修饰键按下不算组合，等主键
            if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
                or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
                or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
                or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin)
                return;
            if (key == System.Windows.Input.Key.Escape) { System.Windows.Input.Keyboard.ClearFocus(); return; }   // 失焦恢复原文
            int vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
            if (vk <= 0) return;
            uint mods = 0;
            var m = System.Windows.Input.Keyboard.Modifiers;
            if (m.HasFlag(System.Windows.Input.ModifierKeys.Control)) mods |= MiniPix.Native.NativeMethods.MOD_CONTROL;
            if (m.HasFlag(System.Windows.Input.ModifierKeys.Alt)) mods |= MiniPix.Native.NativeMethods.MOD_ALT;
            if (m.HasFlag(System.Windows.Input.ModifierKeys.Shift)) mods |= MiniPix.Native.NativeMethods.MOD_SHIFT;
            // 安全护栏：纯字母/数字键不带修饰键注册成全局热键会吃掉系统所有正常输入，拒绝
            bool isFn = key is >= System.Windows.Input.Key.F1 and <= System.Windows.Input.Key.F24;
            if (mods == 0 && !isFn)
            {
                box.Text = "需加 Ctrl/Alt/Shift（F 键可单用）";
                return;
            }
            box.Text = HotkeyText(vk, mods);
            box.Tag = box.Text;
            if (_hkApply.TryGetValue(box, out var apply)) apply(vk, mods);
            System.Windows.Input.Keyboard.ClearFocus();
        }

        /// <summary>虚拟键码+修饰键 → 显示文本（D1 → 1）</summary>
        private static string HotkeyText(int vk, uint mods)
        {
            if (vk <= 0) return "未设置";
            var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk);
            string name = key.ToString();
            if (name.Length == 2 && name[0] == 'D' && name[1] >= '0' && name[1] <= '9') name = name[1..];
            var m = "";
            if ((mods & MiniPix.Native.NativeMethods.MOD_CONTROL) != 0) m += "Ctrl+";
            if ((mods & MiniPix.Native.NativeMethods.MOD_ALT) != 0) m += "Alt+";
            if ((mods & MiniPix.Native.NativeMethods.MOD_SHIFT) != 0) m += "Shift+";
            return m + name;
        }

        /// <summary>重注册全部用户热键（配置 → HotkeyManager）；返回是否全部成功（S-M7 回滚判定用）</summary>
        private bool ApplyHotkeysToApp()
        {
            bool ok = ((global::DeskKit.App)System.Windows.Application.Current).ApplyUserHotkeys();
            if (ok) _txtMsg.Text = "快捷键已生效";
            else _txtMsg.Text = "部分快捷键注册失败（被占用或互相冲突），详见日志";
            MiniPix.Core.Log.Write($"shelf: hotkeys applied ok={ok}");
            return ok;
        }

        /// <summary>S-M7（2026-11-13）：热键先试注册、成功才落盘；失败回滚配置和输入框，
        /// 并重注册旧键——旧实现失败值照样写 config，重启后热键一直是坏的。</summary>
        private void ApplyHotkeyWithRollback(string name, Action setNew, Action setOld)
        {
            setNew();
            var app = (global::DeskKit.App)System.Windows.Application.Current;
            if (app.ApplyUserHotkeys())
            {
                ShotShelf.AppConfig.Current.Save();
                _txtMsg.Text = "快捷键已生效";
            }
            else
            {
                setOld();
                app.ApplyUserHotkeys(); // 恢复旧热键注册
                _txtMsg.Text = $"「{name}」注册失败（被占用或冲突），已保留原快捷键";
                MiniPix.Core.Log.Write($"shelf: hotkey '{name}' register failed, rolled back config");
            }
        }

        /// <summary>五个可调热键恢复默认（F1 / F2 / Alt+1/2/3）并立即生效</summary>
        private void ResetHotkeys()
        {
            var cfg = AppConfig.Current;
            cfg.HotkeyCaptureVk = 0x70; cfg.HotkeyCaptureMods = 0;
            cfg.HotkeyShelfVk = 0x71; cfg.HotkeyShelfMods = 0;
            cfg.HotkeyTab1Vk = MiniPix.Native.NativeMethods.VK_1; cfg.HotkeyTab1Mods = MiniPix.Native.NativeMethods.MOD_ALT;
            cfg.HotkeyTab2Vk = MiniPix.Native.NativeMethods.VK_2; cfg.HotkeyTab2Mods = MiniPix.Native.NativeMethods.MOD_ALT;
            cfg.HotkeyTab3Vk = MiniPix.Native.NativeMethods.VK_3; cfg.HotkeyTab3Mods = MiniPix.Native.NativeMethods.MOD_ALT;
            cfg.Save();
            void Set(TextBox? b, int vk, uint mods) { if (b != null) { b.Text = HotkeyText(vk, mods); b.Tag = b.Text; } }
            Set(_hkCapture, cfg.HotkeyCaptureVk, (uint)cfg.HotkeyCaptureMods);
            Set(_hkShelf, cfg.HotkeyShelfVk, (uint)cfg.HotkeyShelfMods);
            Set(_hkTab1, cfg.HotkeyTab1Vk, (uint)cfg.HotkeyTab1Mods);
            Set(_hkTab2, cfg.HotkeyTab2Vk, (uint)cfg.HotkeyTab2Mods);
            Set(_hkTab3, cfg.HotkeyTab3Vk, (uint)cfg.HotkeyTab3Mods);
            ApplyHotkeysToApp();
        }

        // ================= 页7 传输（2026-11-15 内嵌：不再弹独立窗口） =================

        private TabItem BuildTransferTab()
        {
            var p = NewPage();
            var svc = global::DeskKit.Transfer.TransferService.Instance!;

            // ---- 卡片：本机服务 ----
            var cSrv = Card(p, "本机服务");
            cSrv.Children.Add(Note("手机/电脑浏览器访问下方网址，即可与本机互传文件。"));

            // 设备名称：TextBox + 保存/恢复默认
            var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
            _txtDevName = new TextBox
            {
                Text = global::DeskKit.Transfer.TransferStore.DeskkitName,
                Width = 96,
                Height = 26,
                VerticalContentAlignment = VerticalAlignment.Center,
                MaxLength = 60,
            };
            namePanel.Children.Add(_txtDevName);
            namePanel.Children.Add(Btn("保存", (_, _) =>
            {
                var name = _txtDevName.Text.Trim();
                if (name.Length == 0) { _txtMsg.Text = "名称不能为空"; return; }
                AppConfig.Current.LanDeviceName = name;
                AppConfig.Current.Save();
                _onRenamed(name);
                _txtMsg.Text = "设备名已保存";
                MiniPix.Core.Log.Write($"lan: device renamed to [{name}]");
            }));
            namePanel.Children.Add(Btn("默认", (_, _) =>
            {
                AppConfig.Current.LanDeviceName = "";
                AppConfig.Current.Save();
                _txtDevName.Text = global::DeskKit.Transfer.TransferStore.DeskkitName; // 读回（=计算机名）
                _onRenamed(global::DeskKit.Transfer.TransferStore.DeskkitName);
                _txtMsg.Text = "已恢复计算机名";
            }));
            cSrv.Children.Add(Row("设备名称", "显示在其它设备的列表里", namePanel));

            // 服务网址 + 复制（Tailscale 地址可选）
            var urlPanel = new StackPanel();
            var urlRow = new StackPanel { Orientation = Orientation.Horizontal };
            _txtLanUrl = new TextBlock
            {
                Text = svc.Url,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)),
            };
            urlRow.Children.Add(_txtLanUrl);
            urlRow.Children.Add(Btn("复制", (_, _) =>
            {
                try
                {
                    Clipboard.SetText(string.Join(Environment.NewLine,
                        svc.HostIps.Select(ip => $"http://{ip}:{svc.Port}/")));
                    _txtMsg.Text = "网址已复制";
                }
                catch { }
            }));
            urlPanel.Children.Add(urlRow);
            if (svc.TailscaleUrl != null)
                urlPanel.Children.Add(new TextBlock
                {
                    Text = "Tailscale：" + svc.TailscaleUrl,
                    FontSize = 10.5,
                    Margin = new Thickness(0, 3, 0, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)),
                    ToolTip = "两端都装了 Tailscale 时，任意网络（含手机蜂窝流量）可用此地址",
                });
            cSrv.Children.Add(Row("服务网址", "浏览器访问这个地址", urlPanel));

            // 端口说明（只读信息，灰色小字）
            int lanPort = AppConfig.Current.LanPort;
            cSrv.Children.Add(Row("端口", lanPort > 0
                ? $"固定 {lanPort}（被占自动 +1）"
                : $"自动：当前 {svc.Port}（候选 80 → 8080 → 52123）", new Border()));

            // 接收目录
            var dirPanel = new StackPanel { Orientation = Orientation.Horizontal };
            _txtReceiveDir = new TextBlock
            {
                Text = global::DeskKit.Transfer.TransferService.ReceiveDir,
                FontSize = 11,
                MaxWidth = 120,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = global::DeskKit.Transfer.TransferService.ReceiveDir,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)),
            };
            dirPanel.Children.Add(_txtReceiveDir);
            dirPanel.Children.Add(Btn("打开", (_, _) =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", global::DeskKit.Transfer.TransferService.ReceiveDir); }
                catch { }
            }));
            cSrv.Children.Add(Row("接收目录", "对方发来的文件存这里", dirPanel));

            // ---- 卡片：连接其它电脑 ----
            var cConn = Card(p, "连接其它电脑");
            var connPanel = new StackPanel { Orientation = Orientation.Horizontal };
            _txtServerUrl = new TextBox
            {
                Text = AppConfig.Current.LanClientServerUrl,
                Width = 100,
                Height = 26,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "对方 DeskKit「传输设置」显示的服务网址，如 192.168.1.5:80 或 100.x.x.x:80",
            };
            connPanel.Children.Add(WithHint(_txtServerUrl, "如 192.168.1.5:80"));
            _btnConnect = Btn("连接", (_, _) =>
            {
                var url = _txtServerUrl.Text.Trim();
                if (url.Length == 0) { _txtClientStatus.Text = "请填对方的服务网址"; return; }
                _btnConnect.IsEnabled = false;
                global::DeskKit.Transfer.LanClient.GetOrCreate().Connect(url);
            });
            connPanel.Children.Add(_btnConnect);
            cConn.Children.Add(Row("服务器网址", "填对方 DeskKit 的服务网址", connPanel));

            _txtClientStatus = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0xA9, 0xA0)),
                Margin = new Thickness(0, 4, 0, 2),
                MinHeight = 15,
                TextWrapping = TextWrapping.Wrap,
            };
            cConn.Children.Add(_txtClientStatus);
            _clientCtlRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
            _btnDisconnect = Btn("断开", (_, _) => global::DeskKit.Transfer.LanClient.Instance?.Disconnect(forgetServer: false), first: true);
            _btnForget = Btn("忘记服务器", (_, _) => global::DeskKit.Transfer.LanClient.Instance?.Disconnect(forgetServer: true));
            _clientCtlRow.Children.Add(_btnDisconnect);
            _clientCtlRow.Children.Add(_btnForget);
            cConn.Children.Add(_clientCtlRow);
            cConn.Children.Add(Note("免配对：填对方网址直连即可，无需配对码。\n同一 WiFi 或两端装 Tailscale（任意网络）均可；首次启动如弹防火墙授权请点\"允许\"。"));

            RefreshTransferClientArea();

            // 连接状态变化实时刷新；关窗时由 Closed 统一退订
            _clientStateHandler = () => Dispatcher.BeginInvoke(RefreshTransferClientArea);
            global::DeskKit.Transfer.LanClient.ClientChanged += _clientStateHandler;

            return NewTab("传输", p);
        }

        /// <summary>按客户端连接状态刷新传输页 UI（连接按钮可用性 / 断开·忘记显隐 / 状态文字）。</summary>
        private void RefreshTransferClientArea()
        {
            var client = global::DeskKit.Transfer.LanClient.Instance;
            var connected = client?.IsConnected == true;
            _btnConnect.IsEnabled = !connected;
            _btnDisconnect.Visibility = client == null ? Visibility.Collapsed : Visibility.Visible;
            _btnForget.Visibility = client != null && !string.IsNullOrEmpty(
                AppConfig.Current.LanClientServerUrl) ? Visibility.Visible : Visibility.Collapsed;
            _txtClientStatus.Text = client?.StatusText
                ?? "未连接 — 填对方服务网址点「连接」即可";
        }

        /// <summary>TextBox 加灰字水印提示：内容为空时显示 hint，一输入即消失（不挡鼠标）。</summary>
        private static Grid WithHint(TextBox box, string hint)
        {
            var hintTb = new TextBlock
            {
                Text = hint,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA1, 0xAA)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 1),
                IsHitTestVisible = false,
            };
            void Refresh() => hintTb.Visibility = box.Text.Length == 0
                ? Visibility.Visible : Visibility.Collapsed;
            box.TextChanged += (_, _) => Refresh();
            Refresh();
            var g = new Grid { Width = box.Width };
            box.Margin = new Thickness();
            g.Children.Add(box);
            g.Children.Add(hintTb);
            return g;
        }

        // ================= 保存 =================

        private void Save()
        {
            var cfg = AppConfig.Current;
            // 外观
            cfg.TabImageColor = Hex(_colors["TabImageColor"]);
            cfg.TabTextColor = Hex(_colors["TabTextColor"]);
            cfg.TabDeviceColor = Hex(_colors["TabDeviceColor"]);
            cfg.ShelfBackgroundColor = Hex(_colors["ShelfBackgroundColor"]);
            cfg.ShelfBgFollowTab = _chkFollow.IsChecked == true;
            cfg.ShelfBgTintPct = (int)_sTint.Value;
            cfg.PreviewBackgroundColor = Hex(_colors["PreviewBackgroundColor"]);
            // 贴边
            cfg.PeekThickness = Math.Round(_sPeek.Value);
            cfg.SnapDetectPx = Math.Round(_sDetect.Value);
            cfg.LeaveHideDelayMs = (int)_sLeave.Value;
            cfg.SnapInDelayMs = (int)_sSnapIn.Value;
            cfg.AnimMs = (int)_sAnim.Value;
            // 暂存
            cfg.MaxShots = (int)_sMaxShots.Value;
            cfg.ThumbSize = (int)_sThumb.Value;
            cfg.EnableTextCapture = _chkText.IsChecked == true;
            cfg.EnableDiskCache = _chkDisk.IsChecked == true;
            if (int.TryParse(_txtMaxLen.Text.Trim(), out var maxLen) && maxLen > 0)
                cfg.MaxTextLength = maxLen;
            // 长截图
            cfg.LongScrollNotches = (int)_sLongNotches.Value;
            cfg.LongScrollDelayMs = (int)_sLongDelay.Value;
            // 卡片显示 / 贴图
            cfg.CascadePeekPx = Math.Round(_sCascadePeek.Value);
            cfg.TextCardFontSize = Math.Round(_sTextFont.Value * 2) / 2.0;   // 0.5 步进防浮点脏值
            cfg.PinBorderColor = Hex(_colors["PinBorderColor"]);
            cfg.PinBorderThickness = Math.Round(_sPinBorder.Value * 2) / 2.0;
            cfg.PinShadowEnabled = _chkPinShadow.IsChecked == true;
            cfg.PinShadowBlur = Math.Round(_sPinShadowBlur.Value);
            cfg.PinShadowDepth = Math.Round(_sPinShadowDepth.Value);
            cfg.PinShadowOpacityPct = (int)_sPinShadowOpacity.Value;

            cfg.Save();
            // 2026-11-14 晚间修订：贴图边框/阴影对"已经打开的贴图"也立即生效（用户要求）
            global::MiniPix.Pin.PinWindow.ApplyStyleToAll();
            _onApplied();
            _txtMsg.Text = "已自动保存并生效";
            MiniPix.Core.Log.Write("shelf: settings saved (appearance/edge/store)");
        }

        private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>非模态单例打开；已开着就激活。</summary>
        public static void ShowOrActivate(Window owner, Action onApplied, Action<string?> onRenamed)
        {
            if (_wnd != null)
            {
                _wnd.Activate();
                return;
            }
            _wnd = new ShelfSettingsWindow(owner, onApplied, onRenamed);
            _wnd.Show();
        }

        // ================= 小部件工厂（2026-11-15 三轮：分组卡片式） =================

        private static StackPanel NewPage() => new() { Margin = new Thickness(16, 12, 16, 12) };

        /// <summary>分组卡片：小标题 + 圆角白底卡片，返回卡片内容面板（往里 Add 行即可）。</summary>
        private static StackPanel Card(StackPanel page, string title)
        {
            page.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x7C)),
                Margin = new Thickness(2, 4, 0, 6),
            });
            var body = new StackPanel();
            page.Children.Add(new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 4, 14, 8),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE6, 0xEA)),
                BorderThickness = new Thickness(1),
                Child = body,
                Margin = new Thickness(0, 0, 0, 14),
            });
            return body;
        }

        /// <summary>
        /// 设置行：左 = 标签（可带灰色小注），右 = 控件（右对齐）。
        /// 全部行共用此骨架，滑块/色块/复选框/输入框纵向对齐。
        /// </summary>
        private static Grid Row(string label, string? note, FrameworkElement ctrl)
        {
            var g = new Grid { Margin = new Thickness(0, 7, 0, 7) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x48)),
            });
            if (note != null)
                left.Children.Add(new TextBlock
                {
                    Text = note,
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA1, 0xAA)),
                    Margin = new Thickness(0, 1, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
            g.Children.Add(left);

            ctrl.VerticalAlignment = VerticalAlignment.Center;
            ctrl.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(ctrl, 1);
            g.Children.Add(ctrl);
            return g;
        }

        /// <summary>滑块行：右 = 滑块（固定中等宽度）+ 右对齐数值。返回滑块。</summary>
        private Slider AddSliderRow(StackPanel card, string label, string? note,
            double min, double max, double step, double value, string unit)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = value,
                TickFrequency = step,
                IsSnapToTickEnabled = true,
                Width = 160,
                VerticalAlignment = VerticalAlignment.Center,
            };
            panel.Children.Add(slider);
            var val = new TextBlock
            {
                Text = $"{value:0.#} {unit}",   // 0.#：0.5 步进滑块也能显示小数
                Width = 52,
                TextAlignment = TextAlignment.Right,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)),
            };
            panel.Children.Add(val);
            slider.ValueChanged += (_, _) => val.Text = $"{slider.Value:0.#} {unit}";
            // 2026-11-15 自动保存：拖动停手 500ms 后统一落盘生效
            slider.ValueChanged += (_, _) => ScheduleAutoSave();
            card.Children.Add(Row(label, note, panel));
            return slider;
        }

        /// <summary>复选框行：左 = 标签+小注，右 = 复选框。</summary>
        private CheckBox AddCheckRow(StackPanel card, string label, string? note, bool isChecked)
        {
            var chk = new CheckBox { IsChecked = isChecked, VerticalAlignment = VerticalAlignment.Center };
            // 2026-11-15 自动保存：勾选变化即触发（防抖统一落盘）
            chk.Checked += (_, _) => ScheduleAutoSave();
            chk.Unchecked += (_, _) => ScheduleAutoSave();
            card.Children.Add(Row(label, note, chk));
            return chk;
        }

        /// <summary>
        /// 2026-11-15 四轮：现代化滚动条——隐藏原生滚动条，自绘 5px 圆角悬浮窄条贴右缘；
        /// 滚动时淡入，停手 800ms 后淡出（仿 macOS overlay），不占布局宽度。
        /// </summary>
        private static TabItem NewTab(string header, UIElement content)
        {
            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,   // 隐藏原生滚动条
                Background = Brushes.Transparent,
            };
            sv.Content = content;

            var bar = new Border
            {
                Width = 5,
                CornerRadius = new CornerRadius(2.5),
                Background = new SolidColorBrush(Color.FromArgb(0x50, 0x55, 0x5B, 0x64)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Height = 0,
                Opacity = 0,
                IsHitTestVisible = false,
            };
            var host = new Grid();
            host.Children.Add(sv);
            host.Children.Add(bar);

            // 停手 800ms 后淡出
            var fade = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            fade.Tick += (_, _) =>
            {
                fade.Stop();
                bar.BeginAnimation(OpacityProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
            };
            sv.ScrollChanged += (_, _) =>
            {
                if (sv.ScrollableHeight <= 0)
                {
                    bar.BeginAnimation(OpacityProperty, null);
                    bar.Opacity = 0;
                    return;
                }
                double track = host.ActualHeight - 8;
                if (track < 40) return;
                double ratio = sv.ViewportHeight / (sv.ViewportHeight + sv.ScrollableHeight);
                bar.Height = Math.Max(30, track * ratio);
                double y = 4 + (track - bar.Height) * (sv.VerticalOffset / sv.ScrollableHeight);
                bar.Margin = new Thickness(0, y, 2, 0);
                bar.BeginAnimation(OpacityProperty, null);
                bar.Opacity = 0.9;
                fade.Stop();
                fade.Start();
            };

            return new TabItem { Header = header, Content = host };
        }

        private static TextBlock Note(string text) => new()
        {
            Text = text,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x91, 0x9B)),
            Margin = new Thickness(2, 2, 0, 6),
            TextWrapping = TextWrapping.Wrap,
        };

        private static Button SecondBtn(string text, RoutedEventHandler onClick, bool first = false)
        {
            var b = new Button
            {
                Content = text,
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false,
                Margin = new Thickness(first ? 0 : 8, 0, 0, 0),
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 11.5,
                Background = Brushes.White,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE1)),
                BorderThickness = new Thickness(1),
            };
            b.Click += onClick;
            return b;
        }

        /// <summary>行内小按钮（传输页用：保存/复制/打开/连接等，比 SecondBtn 更紧凑）。</summary>
        private static Button Btn(string text, RoutedEventHandler onClick, bool first = false)
        {
            var b = new Button
            {
                Content = text,
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false,
                Margin = new Thickness(first ? 0 : 6, 0, 0, 0),
                Padding = new Thickness(9, 3, 9, 3),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.White,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE1)),
                BorderThickness = new Thickness(1),
            };
            b.Click += onClick;
            return b;
        }
    }
}
