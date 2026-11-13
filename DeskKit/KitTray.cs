using System;
using System.IO;
using System.Windows.Forms; // NotifyIcon
using Menu = System.Windows.Forms;

namespace DeskKit
{
    /// <summary>
    /// DeskKit 统一托盘图标（三合一菜单）：
    /// - 左键单击：显示暂存架
    /// - 右键菜单：与暂存架窗口右键菜单**同一套条目**（2026-11-12 统一），仅多一条"显示暂存架"
    /// 图标沿用 ShotShelf 的"层架"画法（IconFactory，无外部文件依赖）。
    /// </summary>
    public sealed class KitTray : IDisposable
    {
        private readonly NotifyIcon _notify;

        public KitTray(Action showShelf,
                       Action captureRegion,
                       Action captureFullToClipboard,
                       Action pasteToPin,
                       Func<bool> getQuickGEnabled,
                       Action<bool> setQuickGEnabled,
                       Func<bool>? getLanEnabled = null,
                       Action<bool>? setLanEnabled = null,
                       Action exit = null!,
                       Action? clearAll = null,
                       Action? openSaveDir = null,
                       Func<bool>? getTopmost = null,
                       Action<bool>? setTopmost = null,
                       Action? openSettings = null)
        {
            _notify = new NotifyIcon
            {
                Icon = ShotShelf.IconFactory.GetIcon(),
                Text = "DeskKit 工具箱",
                Visible = true,
            };

            var menu = new Menu.ContextMenuStrip();
            // 2026-11-12 现代化：白底浅蓝 hover 自绘渲染器（与 WPF 右键菜单同一配色）
            menu.Renderer = new ModernMenuRenderer();
            menu.ShowImageMargin = true;
            menu.ShowCheckMargin = false; // 勾选画蓝色圆点进图标位（ModernMenuRenderer.OnRenderItemCheck）
            // 图标位图自带左右留白（Forms 默认 leftPad=12 + 16px 图标 + rightPad=0 = 28 宽），同步缩放目标避免拉伸
            menu.ImageScalingSize = new System.Drawing.Size(28, 16);
            menu.Font = new System.Drawing.Font("Segoe UI", 10F);

            // ---- 暂存架（托盘专属入口；窗口右键菜单无此条） ----
            var miShelf = new Menu.ToolStripMenuItem("显示暂存架")
            {
                // 无图标条目补一张同尺寸透明位图，让文字与下方带图标条目左对齐
                Image = DeskKit.MenuIcons.Blank(28, 16),
            };
            miShelf.Font = new System.Drawing.Font(miShelf.Font, System.Drawing.FontStyle.Bold);
            miShelf.Click += (_, _) => showShelf();
            menu.Items.Add(miShelf);
            menu.Items.Add(new Menu.ToolStripSeparator());

            // ---- 截图三件套（源 MiniPix） ----
            void Add(string text, Action a, string shortcut, string? icon = null)
            {
                var item = new Menu.ToolStripMenuItem(text) { ShortcutKeyDisplayString = shortcut };
                if (icon != null) item.Image = DeskKit.MenuIcons.Forms(icon);
                item.Click += (_, _) => a();
                menu.Items.Add(item);
            }
            Add("区域/窗口截图", captureRegion, "F1", "capture");
            Add("全屏截到剪贴板", captureFullToClipboard, "Ctrl+Shift+C", "full");
            Add("剪贴板内容贴图", pasteToPin, "Ctrl+Alt+V", "pin");
            menu.Items.Add(new Menu.ToolStripSeparator());

            // ---- 暂存架窗口操作（2026-11-12 统一：与窗口右键同款） ----
            if (clearAll != null)
            {
                var miClear = new Menu.ToolStripMenuItem("清空当前轨道") { Image = DeskKit.MenuIcons.Forms("clear") };
                miClear.Click += (_, _) => clearAll();
                menu.Items.Add(miClear);
            }
            if (openSaveDir != null)
            {
                var miDir = new Menu.ToolStripMenuItem("打开保存目录") { Image = DeskKit.MenuIcons.Forms("folder") };
                miDir.Click += (_, _) => openSaveDir();
                menu.Items.Add(miDir);
            }
            if (getTopmost != null && setTopmost != null)
            {
                var miTop = new Menu.ToolStripMenuItem("窗口置顶") { CheckOnClick = true, Image = DeskKit.MenuIcons.Forms("topmost") };
                miTop.CheckedChanged += (_, _) => setTopmost(miTop.Checked);
                menu.Opening += (_, _) => miTop.Checked = getTopmost();
                menu.Items.Add(miTop);
            }
            menu.Items.Add(new Menu.ToolStripSeparator());

            // ---- 功能开关：QuickG / 局域网传输（菜单打开时刷新端口/在线数状态文字） ----
            var miQuickG = new Menu.ToolStripMenuItem("快捷跳转")
            {
                CheckOnClick = true,
                Checked = getQuickGEnabled(),
                Image = DeskKit.MenuIcons.Forms("quickg"),
            };
            miQuickG.CheckedChanged += (_, _) => setQuickGEnabled(miQuickG.Checked);
            menu.Items.Add(miQuickG);

            Menu.ToolStripMenuItem? miLan = null;
            if (getLanEnabled != null && setLanEnabled != null)
            {
                miLan = new Menu.ToolStripMenuItem("网络传输")
                {
                    CheckOnClick = true,
                    Checked = getLanEnabled(),
                    Image = DeskKit.MenuIcons.Forms("lan"),
                };
                miLan.CheckedChanged += (_, _) => setLanEnabled(miLan.Checked);
                menu.Items.Add(miLan);
            }
            menu.Items.Add(new Menu.ToolStripSeparator());

            // ---- 程序配置 ----
            if (openSettings != null)
            {
                var miSettings = new Menu.ToolStripMenuItem("设置…") { Image = DeskKit.MenuIcons.Forms("settings") };
                miSettings.Click += (_, _) => openSettings();
                menu.Items.Add(miSettings);
            }

            // ---- 开机自启 ----
            var miAutoStart = new Menu.ToolStripMenuItem("开机自启")
            {
                CheckOnClick = true,
                Checked = AutoStartHelper.IsEnabled(),
                Image = DeskKit.MenuIcons.Forms("autostart"),
            };
            miAutoStart.CheckedChanged += (_, _) => AutoStartHelper.SetEnabled(miAutoStart.Checked);
            menu.Items.Add(miAutoStart);

            menu.Items.Add(new Menu.ToolStripSeparator());

            // ---- 帮助（2026-11-12：渲染 exe 旁 Help.md，随发布更新） ----
            var miHelp = new Menu.ToolStripMenuItem("帮助") { Image = DeskKit.MenuIcons.Forms("help") };
            miHelp.Click += (_, _) => App.ShowHelp();
            menu.Items.Add(miHelp);

            // ---- 关于 ----
            var miAbout = new Menu.ToolStripMenuItem("关于 DeskKit…") { Image = DeskKit.MenuIcons.Forms("about") };
            miAbout.Click += (_, _) => App.ShowAbout();
            menu.Items.Add(miAbout);

            menu.Items.Add(new Menu.ToolStripSeparator());
            var miExit = new Menu.ToolStripMenuItem("退出") { Image = DeskKit.MenuIcons.Forms("exit") };
            miExit.Click += (_, _) =>
            {
                _notify.Visible = false; // 先摘图标再退出，避免残影
                exit();
            };
            menu.Items.Add(miExit);

            // 菜单打开时刷新勾选/状态文字（局域网传输的端口/在线数、Ctrl+G 开关）
            menu.Opening += (_, _) =>
            {
                miQuickG.Checked = getQuickGEnabled();
                if (miLan != null && getLanEnabled != null)
                {
                miLan.Checked = getLanEnabled();
                }
            };

            // 条目上下加留白（默认 ~24px 偏挤，视觉上更接近现代菜单行高）
            foreach (Menu.ToolStripItem it in menu.Items)
                it.Padding = new Menu.Padding(2, 6, 4, 6);

            _notify.ContextMenuStrip = menu;

            // 左键单击 = 显示暂存架
            _notify.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) showShelf();
            };
        }

        /// <summary>气泡通知（原 MiniPix TrayManager.ShowBalloon）</summary>
        public void ShowBalloon(string msg) =>
            _notify.ShowBalloonTip(2000, "DeskKit", msg, ToolTipIcon.Info);

        public void Dispose()
        {
            _notify.Visible = false;
            _notify.Dispose();
        }
    }

    /// <summary>
    /// 托盘菜单现代风渲染器（2026-11-12）：白底 + 浅蓝 hover + 浅灰分隔线，
    /// 配色与暂存架 WPF 右键菜单（App.xaml 全局 ContextMenu 样式）保持一致。
    /// 只接管背景/勾选/分隔，文字仍走默认绘制（避免双重绘制发虚）。
    /// </summary>
    internal sealed class ModernMenuRenderer : Menu.ToolStripProfessionalRenderer
    {
        private static readonly System.Drawing.Color HoverBg   = System.Drawing.Color.FromArgb(0xE8, 0xF1, 0xFB);
        private static readonly System.Drawing.Color HoverEdge = System.Drawing.Color.FromArgb(0xD3, 0xE6, 0xF8);
        private static readonly System.Drawing.Color Accent    = System.Drawing.Color.FromArgb(0x1E, 0x88, 0xE5);

        public ModernMenuRenderer() : base(new ModernColorTable()) { }

        // hover 背景：纯浅蓝 + 同色系细边（替代默认立体渐变）；不调 base
        protected override void OnRenderMenuItemBackground(Menu.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected && !e.Item.Pressed) return;
            var g = e.Graphics;
            var rect = new System.Drawing.Rectangle(2, 2, e.Item.Size.Width - 4, e.Item.Size.Height - 4);
            using var bg = new System.Drawing.SolidBrush(HoverBg);
            g.FillRectangle(bg, rect);
            using var edge = new System.Drawing.Pen(HoverEdge);
            g.DrawRectangle(edge, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        }

        // 勾选态：蓝色小对勾画在条目右侧（图标由 OnRenderItemImage 统一绘制）
        protected override void OnRenderItemCheck(Menu.ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // 右侧画小对勾（cx 距右缘 24：对勾右侧留足空间，2026-11-13）
            int w = e.Item.Width, h = e.Item.Height;
            int cx = w - 24, cy = h / 2;
            using var pen = new System.Drawing.Pen(Accent, 2f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
            };
            g.DrawLines(pen, new[]
            {
                new System.Drawing.Point(cx - 5, cy),
                new System.Drawing.Point(cx - 1, cy + 4),
                new System.Drawing.Point(cx + 5, cy - 4),
            });
        }

        // 2026-11-12 关键修复：base.OnRenderItemImage 在 ShowCheckMargin=false 的下拉菜单里
        // 会给勾选项额外画系统蓝底方块（RenderCheckBackground，UseSystemColors 分支走
        // SystemPens.Highlight 描边，不受色表覆盖影响）。这里自己画图标、绕过该逻辑。
        protected override void OnRenderItemImage(Menu.ToolStripItemImageRenderEventArgs e)
        {
            if (e.Image == null) return;
            e.Graphics.DrawImage(e.Image, e.ImageRectangle);
        }

        private sealed class ModernColorTable : Menu.ProfessionalColorTable
        {
            public override System.Drawing.Color MenuItemSelected => HoverBg;
            public override System.Drawing.Color MenuItemBorder => HoverEdge;
            public override System.Drawing.Color MenuBorder => Hairline;
            public override System.Drawing.Color ToolStripDropDownBackground => System.Drawing.Color.White;
            public override System.Drawing.Color ImageMarginGradientBegin => System.Drawing.Color.White;
            public override System.Drawing.Color ImageMarginGradientMiddle => System.Drawing.Color.White;
            public override System.Drawing.Color ImageMarginGradientEnd => System.Drawing.Color.White;
            public override System.Drawing.Color SeparatorDark => SepLine;
            public override System.Drawing.Color SeparatorLight => SepLine;
            public override System.Drawing.Color MenuItemPressedGradientBegin => System.Drawing.Color.White;
            public override System.Drawing.Color MenuItemPressedGradientEnd => System.Drawing.Color.White;
            public override System.Drawing.Color MenuItemSelectedGradientBegin => HoverBg;
            public override System.Drawing.Color MenuItemSelectedGradientEnd => HoverBg;
            // 勾选背景全透明化（2026-11-12）：勾选项图标后不再出现系统蓝方块，勾选态只看右侧对勾
            public override System.Drawing.Color CheckBackground => System.Drawing.Color.White;
            public override System.Drawing.Color CheckSelectedBackground => System.Drawing.Color.White;
            public override System.Drawing.Color CheckPressedBackground => System.Drawing.Color.White;
        }

        private static readonly System.Drawing.Color Hairline = System.Drawing.Color.FromArgb(0xE4, 0xE6, 0xEA);
        private static readonly System.Drawing.Color SepLine  = System.Drawing.Color.FromArgb(0xED, 0xEF, 0xF2);
    }

    /// <summary>
    /// 开机自启（2026-11-06 双通道版）：
    /// 通道1 = 注册表 HKCU\...\Run（标准方式）；
    /// 通道2 = 启动文件夹快捷方式 shell:startup\DeskKit.lnk（注册表被杀软拦时的降级）。
    /// 背景：用户新电脑上写 Run 键被防护软件拒（UnauthorizedAccessException，见日志
    /// "autostart set FAIL: Attempted to perform an unauthorized operation"），
    /// 旧版静默吞掉导致"勾选了却不生效"，故加入快捷方式降级通道。
    /// </summary>
    public static class AutoStartHelper
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DeskKit";

        /// <summary>通道2：启动文件夹快捷方式路径（%APPDATA%\...\Start Menu\Programs\Startup）</summary>
        private static string ShortcutPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DeskKit.lnk");

        public static bool IsEnabled()
        {
            object regVal = null;
            try { regVal = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(ValueName); }
            catch { /* 查询失败按无处理，由快捷方式通道兜底 */ }
            bool viaShortcut = File.Exists(ShortcutPath);
            var enabled = regVal != null || viaShortcut;
            MiniPix.Core.Log.Write($"autostart query: {enabled} reg={regVal ?? "<null>"} lnk={viaShortcut}");
            return enabled;
        }

        public static void SetEnabled(bool on)
        {
            if (on)
            {
                // 首选注册表；被拦（UnauthorizedAccess 等）自动降级到启动文件夹快捷方式
                if (TryRegistryWrite(out var regErr))
                {
                    MiniPix.Core.Log.Write("autostart set ON via registry");
                    return;
                }
                MiniPix.Core.Log.Write("autostart registry blocked (" + regErr + "), fallback to Startup shortcut");
                TryShortcutWrite(out var lnkErr);
            }
            else
            {
                try
                {
                    Microsoft.Win32.Registry.CurrentUser
                        .CreateSubKey(RunKey)?.DeleteValue(ValueName, false);
                }
                catch { /* 删除失败不影响快捷方式通道 */ }
                try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }
                MiniPix.Core.Log.Write("autostart set OFF (registry + shortcut cleaned)");
            }
        }

        // ---- 通道1：注册表 ----

        private static bool TryRegistryWrite(out string error)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                var exe = Environment.ProcessPath ?? "DeskKit";
                key.SetValue(ValueName, $"\"{exe}\"");
                // 写后立即读回验证，确认注册表真的收到了
                var back = Microsoft.Win32.Registry.CurrentUser
                    .OpenSubKey(RunKey)?.GetValue(ValueName);
                if (back == null) { error = "verify read null"; return false; }
                error = "";
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        // ---- 通道2：启动文件夹快捷方式（WScript.Shell COM 创建 .lnk） ----

        private static void TryShortcutWrite(out string error)
        {
            try
            {
                var exe = Environment.ProcessPath ?? "DeskKit";
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) { error = "WScript.Shell COM 不可用"; return; }
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic lnk = shell.CreateShortcut(ShortcutPath);
                lnk.TargetPath = exe;
                lnk.WorkingDirectory = Path.GetDirectoryName(exe) ?? ".";
                lnk.Description = "DeskKit 桌面工具箱开机自启";
                lnk.Save();
                MiniPix.Core.Log.Write($"autostart set ON via shortcut: {ShortcutPath}");
                error = "";
            }
            catch (Exception ex)
            {
                error = ex.Message;
                MiniPix.Core.Log.Write("autostart shortcut FAIL: " + ex.Message);
            }
        }
    }
}
