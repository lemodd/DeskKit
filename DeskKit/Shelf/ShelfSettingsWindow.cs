using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace ShotShelf
{
    /// <summary>
    /// 暂存架设置窗口（2026-11-10 反馈二轮：不能只有贴边参数，加外观/暂存/传输页）。
    /// 纯代码建窗（TabControl 四页），非模态、单例：ShowOrActivate。
    /// 页1 外观：图/文/设备标签颜色 + 窗口背景色 + 预览背景色（色块点击弹系统取色器）
    /// 页2 贴边：留边宽度/检测范围/滑回延时/收起延时/动画时长（滑块）
    /// 页3 暂存：上限/缩略图尺寸/文本捕获/单条上限/磁盘缓存
    /// 页4 传输：入口按钮（复用 TransferSettingsWindow）
    /// 全部存 config.json；保存后经回调实时生效（个别项标注重启生效）。
    /// </summary>
    public sealed class ShelfSettingsWindow : Window
    {
        private static ShelfSettingsWindow? _wnd; // 单例

        private readonly Action _onApplied;          // 保存后回调（MainWindow 应用）
        private readonly Action _openTransferSettings; // 打开传输设置（复用既有窗口）

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
            ["PinBorderColor"] = Color.FromRgb(0x9A, 0xA0, 0xA6),   // 2026-11-14 贴图页
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
        // 2026-11-14 新功能：级联露边 / 文字卡片字号 / 贴图边框阴影
        private Slider _sCascadePeek, _sTextFont;
        private Slider _sPinBorder, _sPinShadowBlur, _sPinShadowDepth, _sPinShadowOpacity;
        private CheckBox _chkPinShadow;

        private ShelfSettingsWindow(Window owner, Action onApplied, Action openTransferSettings)
        {
            _onApplied = onApplied;
            _openTransferSettings = openTransferSettings;
            var cfg = AppConfig.Current;

            Title = "暂存架设置";
            Width = 480;
            Height = 420;
            MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Icon = IconFactory.GetWpfIcon();   // 2026-11-12 统一程序图标
            Owner = owner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA));
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
            var root = new Grid { Margin = new Thickness(12, 10, 12, 10) };
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
            btnRow.Children.Add(PrimaryBtn("保存", (_, _) => Save()));
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
            Closed += (_, _) => _wnd = null;
        }

        // ================= 页1 外观 =================

        private TabItem BuildAppearanceTab()
        {
            var p = NewPage();
            p.Children.Add(Note("点击色块弹出系统取色器；颜色立即存入配置并生效。"));
            p.Children.Add(ColorRow("TabImageColor", "「图」标签颜色"));
            p.Children.Add(ColorRow("TabTextColor", "「文」标签颜色"));
            p.Children.Add(ColorRow("TabDeviceColor", "「设备」标签颜色"));
            p.Children.Add(ColorRow("ShelfBackgroundColor", "窗口背景色"));
            _chkFollow = AddCheck(p, "背景跟随标签颜色（切图/文/设备跟着换）",
                AppConfig.Current.ShelfBgFollowTab, null);
            _chkFollow.Checked += (_, _) => { _swatches["ShelfBackgroundColor"].IsEnabled = false; _sTint.IsEnabled = true; };
            _chkFollow.Unchecked += (_, _) => { _swatches["ShelfBackgroundColor"].IsEnabled = true; _sTint.IsEnabled = false; };
            if (AppConfig.Current.ShelfBgFollowTab) _swatches["ShelfBackgroundColor"].IsEnabled = false; // 初始状态
            _sTint = AddSlider(p, "背景深浅度（跟随标签时的浓度）", 0, 100, 5, AppConfig.Current.ShelfBgTintPct, "%");
            _sTint.IsEnabled = AppConfig.Current.ShelfBgFollowTab;
            p.Children.Add(ColorRow("PreviewBackgroundColor", "预览大图背景色"));
            // 2026-11-14 新增：卡片外观参数
            _sCascadePeek = AddSlider(p, "固定卡片叠放露边宽度", 4, 40, 1, AppConfig.Current.CascadePeekPx, "px");
            _sTextFont = AddSlider(p, "文字卡片字号", 8, 16, 0.5, AppConfig.Current.TextCardFontSize, "px");
            return NewTab("外观", p);
        }

        private UIElement ColorRow(string key, string label)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(new TextBlock
            {
                Text = label,
                Width = 130,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)),
            });
            var swatch = new Button
            {
                Width = 56,
                Height = 24,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE1)),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = new SolidColorBrush(_colors[key]),
                Tag = key,
            };
            swatch.Click += (_, _) => PickColor(key);
            _swatches[key] = swatch;
            row.Children.Add(swatch);
            return row;
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
        }

        // ================= 页2 贴边 =================

        private TabItem BuildEdgeTab(AppConfig cfg)
        {
            var p = NewPage();
            p.Children.Add(Note("拖到屏幕边缘松手贴边、自动收起只留小边——这些参数控制整个流程。"));
            _sPeek = AddSlider(p, "留边宽度（收起后露出的小边）", 2, 12, 1, cfg.PeekThickness, "px");
            _sDetect = AddSlider(p, "贴边检测范围（松手时离边缘多近算贴上）", 6, 30, 1, cfg.SnapDetectPx, "px");
            _sLeave = AddSlider(p, "鼠标离开后滑回延时", 200, 2000, 50, cfg.LeaveHideDelayMs, "ms");
            _sSnapIn = AddSlider(p, "贴边后自动收起延时", 200, 2000, 50, cfg.SnapInDelayMs, "ms");
            _sAnim = AddSlider(p, "滑出/滑回动画时长", 100, 1000, 20, cfg.AnimMs, "ms");
            return NewTab("贴边", p);
        }

        // ================= 页3 暂存 =================

        private TabItem BuildStoreTab(AppConfig cfg)
        {
            var p = NewPage();

            // ---- 自动保存目录（2026-11-11 从右键菜单迁入；放页首避免被折叠藏住）----
            var dirRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 0) };
            dirRow.Children.Add(new TextBlock
            {
                Text = "自动保存目录",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)),
            });
            _txtSaveDir = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(cfg.AutoSaveDir) ? "未设置（截图不自动落盘）" : cfg.AutoSaveDir,
                FontSize = 11,
                MaxWidth = 200,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                ToolTip = string.IsNullOrWhiteSpace(cfg.AutoSaveDir) ? null : cfg.AutoSaveDir,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x82, 0x8C)),
            };
            dirRow.Children.Add(_txtSaveDir);
            p.Children.Add(dirRow);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            btnRow.Children.Add(SecondBtn("更改目录…", (_, _) => PickAutoSaveDir()));
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
            p.Children.Add(btnRow);
            p.Children.Add(Note("设置后每张截图/文字都自动另存一份到这里（按时间命名，永不自动删除）。"));

            _sMaxShots = AddSlider(p, "暂存上限（超出自动淘汰最旧未固定项）", 10, 500, 10, cfg.MaxShots, "条");
            _sThumb = AddSlider(p, "缩略图最长边（新入库的图生效）", 96, 320, 8, cfg.ThumbSize, "px");
            _chkText = AddCheck(p, "捕获剪贴板文字（关掉后只暂存图片）", cfg.EnableTextCapture, "重启生效");
            var maxLenRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            maxLenRow.Children.Add(new TextBlock
            {
                Text = "单条文字上限（字符）",
                Width = 170,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)),
            });
            _txtMaxLen = new TextBox
            {
                Text = cfg.MaxTextLength.ToString(),
                Width = 90,
                Height = 26,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            maxLenRow.Children.Add(_txtMaxLen);
            p.Children.Add(maxLenRow);
            _chkDisk = AddCheck(p, "磁盘缓存（重启后暂存不丢的关键，建议开）", cfg.EnableDiskCache, "重启生效");

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
            p.Children.Add(Note("长截图（框选后自动/手动滚动拼接）的滚动节奏。\n步长小+延时长 = 慢而稳，拼接重叠充足；步长大+延时短 = 快。"));
            _sLongNotches = AddSlider(p, "每步滚动量（滚轮刻度，1 刻度 ≈120px）", 1, 10, 1, cfg.LongScrollNotches, "刻度");
            _sLongDelay = AddSlider(p, "每步等待时间（等页面渲染稳定）", 200, 1500, 50, cfg.LongScrollDelayMs, "ms");
            return NewTab("长截图", p);
        }

        // ================= 页5 贴图（2026-11-14：边框颜色/粗细 + 阴影） =================

        private TabItem BuildPinTab(AppConfig cfg)
        {
            var p = NewPage();
            p.Children.Add(Note("贴图（钉在屏幕上的图片）的边框与阴影。设置保存后，之后新开的贴图生效，已打开的保持原样。"));
            p.Children.Add(ColorRow("PinBorderColor", "贴图边框颜色"));
            _sPinBorder = AddSlider(p, "贴图边框粗细（0=无边框）", 0, 10, 0.5, cfg.PinBorderThickness, "px");
            _chkPinShadow = AddCheck(p, "启用贴图阴影", cfg.PinShadowEnabled, null);
            _sPinShadowBlur = AddSlider(p, "阴影模糊半径", 0, 40, 1, cfg.PinShadowBlur, "px");
            _sPinShadowDepth = AddSlider(p, "阴影偏移（离贴图的距离）", 0, 20, 1, cfg.PinShadowDepth, "px");
            _sPinShadowOpacity = AddSlider(p, "阴影浓度", 0, 100, 5, cfg.PinShadowOpacityPct, "%");
            return NewTab("贴图", p);
        }

        // ================= 页6 快捷键（2026-11-12） =================

        private TextBox? _hkCapture, _hkShelf, _hkTab1, _hkTab2, _hkTab3;
        private readonly Dictionary<TextBox, Action<int, uint>> _hkApply = new();

        private TabItem BuildHotkeyTab(AppConfig cfg)
        {
            var p = NewPage();
            p.Children.Add(Note("点击输入框后直接按下想要的按键组合（可含 Ctrl/Alt/Shift），按下即生效。\nEsc 取消恢复原值。全局热键：纯字母数字键必须带修饰键，F1~F12 可单独用。\n注册失败（被占用/冲突）会自动回滚，不会保存坏值。"));
            int oCapVk = cfg.HotkeyCaptureVk, oCapMods = cfg.HotkeyCaptureMods;
            _hkCapture = HotkeyRow(p, "截图", oCapVk, (uint)oCapMods, (vk, mods) =>
                ApplyHotkeyWithRollback("截图",
                    () => { cfg.HotkeyCaptureVk = vk; cfg.HotkeyCaptureMods = (int)mods; },
                    () => { cfg.HotkeyCaptureVk = oCapVk; cfg.HotkeyCaptureMods = oCapMods; }));
            int oShelfVk = cfg.HotkeyShelfVk, oShelfMods = cfg.HotkeyShelfMods;
            _hkShelf = HotkeyRow(p, "弹出/收回暂存架", oShelfVk, (uint)oShelfMods, (vk, mods) =>
                ApplyHotkeyWithRollback("弹出/收回暂存架",
                    () => { cfg.HotkeyShelfVk = vk; cfg.HotkeyShelfMods = (int)mods; },
                    () => { cfg.HotkeyShelfVk = oShelfVk; cfg.HotkeyShelfMods = oShelfMods; }));
            int oT1Vk = cfg.HotkeyTab1Vk, oT1Mods = cfg.HotkeyTab1Mods;
            _hkTab1 = HotkeyRow(p, "切「图」标签", oT1Vk, (uint)oT1Mods, (vk, mods) =>
                ApplyHotkeyWithRollback("切「图」标签",
                    () => { cfg.HotkeyTab1Vk = vk; cfg.HotkeyTab1Mods = (int)mods; },
                    () => { cfg.HotkeyTab1Vk = oT1Vk; cfg.HotkeyTab1Mods = oT1Mods; }));
            int oT2Vk = cfg.HotkeyTab2Vk, oT2Mods = cfg.HotkeyTab2Mods;
            _hkTab2 = HotkeyRow(p, "切「文」标签", oT2Vk, (uint)oT2Mods, (vk, mods) =>
                ApplyHotkeyWithRollback("切「文」标签",
                    () => { cfg.HotkeyTab2Vk = vk; cfg.HotkeyTab2Mods = (int)mods; },
                    () => { cfg.HotkeyTab2Vk = oT2Vk; cfg.HotkeyTab2Mods = oT2Mods; }));
            int oT3Vk = cfg.HotkeyTab3Vk, oT3Mods = cfg.HotkeyTab3Mods;
            _hkTab3 = HotkeyRow(p, "切「设备」标签", oT3Vk, (uint)oT3Mods, (vk, mods) =>
                ApplyHotkeyWithRollback("切「设备」标签",
                    () => { cfg.HotkeyTab3Vk = vk; cfg.HotkeyTab3Mods = (int)mods; },
                    () => { cfg.HotkeyTab3Vk = oT3Vk; cfg.HotkeyTab3Mods = oT3Mods; }));
            p.Children.Add(Note("\n其余固定热键：Ctrl+Alt+A / Ctrl+Shift+C / Ctrl+Alt+C（截图）、Ctrl+Alt+V（贴图）、Ctrl+G（跳转）。"));
            return NewTab("快捷键", p);
        }

        /// <summary>一行 = 标签 + 热键输入框（只读，点击捕获按键组合）。按下组合立即写配置并重注册。</summary>
        private TextBox HotkeyRow(StackPanel p, string label, int vk, uint mods, Action<int, uint> apply)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            row.Children.Add(new TextBlock
            {
                Text = label,
                Width = 130,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)),
            });
            var box = new TextBox
            {
                Text = HotkeyText(vk, mods),
                Width = 150,
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
            row.Children.Add(box);
            p.Children.Add(row);
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

        // ================= 页6 传输 =================

        private TabItem BuildTransferTab()
        {
            var p = NewPage();
            p.Children.Add(Note("网络传输：手机/电脑浏览器访问本机网址互传文件。\n设备改名、端口、接收目录都在传输设置里。"));
            var btn = SecondBtn("打开传输设置…", (_, _) => _openTransferSettings());
            btn.HorizontalAlignment = HorizontalAlignment.Left;
            btn.Margin = new Thickness(0, 10, 0, 0);
            p.Children.Add(btn);
            return NewTab("传输", p);
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
            // 2026-11-14 新功能
            cfg.CascadePeekPx = Math.Round(_sCascadePeek.Value);
            cfg.TextCardFontSize = Math.Round(_sTextFont.Value * 2) / 2.0;   // 0.5 步进防浮点脏值
            cfg.PinBorderColor = Hex(_colors["PinBorderColor"]);
            cfg.PinBorderThickness = Math.Round(_sPinBorder.Value * 2) / 2.0;
            cfg.PinShadowEnabled = _chkPinShadow.IsChecked == true;
            cfg.PinShadowBlur = Math.Round(_sPinShadowBlur.Value);
            cfg.PinShadowDepth = Math.Round(_sPinShadowDepth.Value);
            cfg.PinShadowOpacityPct = (int)_sPinShadowOpacity.Value;

            cfg.Save();
            _onApplied();
            _txtMsg.Text = "已保存并生效";
            MiniPix.Core.Log.Write("shelf: settings saved (appearance/edge/store)");
        }

        private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>非模态单例打开；已开着就激活。</summary>
        public static void ShowOrActivate(Window owner, Action onApplied, Action openTransferSettings)
        {
            if (_wnd != null)
            {
                _wnd.Activate();
                return;
            }
            _wnd = new ShelfSettingsWindow(owner, onApplied, openTransferSettings);
            _wnd.Show();
        }

        // ================= 小部件工厂 =================

        private static StackPanel NewPage() => new() { Margin = new Thickness(14, 10, 14, 10) };

        private static TabItem NewTab(string header, UIElement content) => new()
        {
            Header = header,
            Content = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
        };

        private static TextBlock Note(string text) => new()
        {
            Text = text,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x82, 0x8C)),
            Margin = new Thickness(0, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap,
        };

        private static CheckBox AddCheck(StackPanel p, string label, bool isChecked, string note)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var chk = new CheckBox
            {
                Content = label,
                IsChecked = isChecked,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(chk);
            if (note != null)
                row.Children.Add(new TextBlock
                {
                    Text = $"（{note}）",
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x82, 0x8C)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                });
            p.Children.Add(row);
            return chk;
        }

        /// <summary>一行 = 标签 + 右侧当前值；下一行滑块。返回滑块。</summary>
        private static Slider AddSlider(StackPanel root, string label, double min, double max, double step, double value, string unit)
        {
            var lblRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            lblRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            lblRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            lblRow.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)),
            });
            var val = new TextBlock
            {
                Text = $"{value:0.#} {unit}",   // 0.#：0.5 步进滑块也能显示小数
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)),
            };
            Grid.SetColumn(val, 1);
            lblRow.Children.Add(val);
            root.Children.Add(lblRow);

            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = value,
                TickFrequency = step,
                IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 2, 0, 0),
            };
            slider.ValueChanged += (_, _) => val.Text = $"{slider.Value:0.#} {unit}";
            root.Children.Add(slider);
            return slider;
        }

        private static Button PrimaryBtn(string text, RoutedEventHandler onClick)
        {
            var b = new Button
            {
                Content = text,
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(12, 4, 12, 4),
                FontSize = 11.5,
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
            };
            b.Click += onClick;
            return b;
        }

        private static Button SecondBtn(string text, RoutedEventHandler onClick)
        {
            var b = new Button
            {
                Content = text,
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false,
                Margin = new Thickness(8, 0, 0, 0),
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
    }
}
