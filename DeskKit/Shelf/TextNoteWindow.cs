using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ShotShelf
{
    /// <summary>
    /// 文字便签（2026-11-15 新功能：从暂存架把文字卡拖到桌面松手生成）。
    /// 仿便利贴：暖黄圆角卡片、投影、顶部窄条拖动、右上角悬停显示关闭；
    /// 内容可编辑，停手 600ms 回写暂存架条目（ShotStore.UpdateText，卡片摘要/字数实时刷新）。
    /// 仅本会话存在，不持久化（与贴图 PinWindow 一致）。
    /// </summary>
    public sealed class TextNoteWindow : Window
    {
        private static readonly List<TextNoteWindow> _open = new();

        private readonly TextBox _box;
        private readonly ShotItem? _item;
        private readonly ShotStore? _store;
        private readonly System.Windows.Threading.DispatcherTimer _saveDebounce;

        private TextNoteWindow(string text, ShotItem? item, ShotStore? store, Point pxTopLeft)
        {
            _item = item;
            _store = store;

            Width = 240;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = true;
            Topmost = true;                     // 便签默认浮在最前（与贴图一致）
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            UseLayoutRounding = true;

            // ---- 便利贴主体（留投影边距，仿 PinWindow 的处理） ----
            var body = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xA8)),   // 便利贴黄
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xD5, 0x7A)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(10),                    // 给投影留地方
                Effect = new DropShadowEffect
                {
                    BlurRadius = 16,
                    ShadowDepth = 3,
                    Direction = 315,
                    Opacity = 0.35,
                    Color = Colors.Black,
                },
            };

            var grid = new Grid { Margin = new Thickness(0) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });   // 拖动条
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });      // 文字

            // ---- 顶部拖动条：按住拖动窗口；右侧悬停显示 ✕ ----
            var grip = new Border
            {
                Background = Brushes.Transparent,           // 透明但可命中，负责 DragMove
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Cursor = System.Windows.Input.Cursors.SizeAll,
            };
            grip.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
            var close = new Button
            {
                Content = "✕",
                Width = 20,
                Height = 18,
                FontSize = 10,
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 2, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x7A, 0x30)),
                Opacity = 0.0,                              // 悬停便签时才浮现
            };
            close.Click += (_, _) => Close();
            body.MouseEnter += (_, _) => close.Opacity = 0.8;
            body.MouseLeave += (_, _) => close.Opacity = 0.0;
            grip.Child = close;
            Grid.SetRow(grip, 0);
            grid.Children.Add(grip);

            // ---- 正文：可编辑，自动换行；超高滚动 ----
            _box = new TextBox
            {
                Text = text,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x40, 0x20)),
                MinHeight = 90,
                MaxHeight = 380,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(8, 0, 8, 8),
            };
            Grid.SetRow(_box, 1);
            grid.Children.Add(_box);
            body.Child = grid;

            // 右键菜单：复制全部 / 便签设置（颜色/字号/不透明度）/ 置顶开关 / 关闭
            // 2026-11-15：设置项只作用于当前这张便签（便签不持久化，与贴图策略一致）
            var menu = new ContextMenu();
            var miCopy = new MenuItem { Header = "复制全部" };
            miCopy.Click += (_, _) => { try { Clipboard.SetText(_box.Text); } catch { } };
            menu.Items.Add(miCopy);

            // ---- 便签颜色…：系统取色器，边框自动取深 18%，深色时文字自动转白 ----
            var miColor = new MenuItem { Header = "便签颜色…" };
            miColor.Click += (_, _) =>
            {
                var cur = ((SolidColorBrush)body.Background).Color;
                using var dlg = new System.Windows.Forms.ColorDialog
                {
                    FullOpen = true,
                    Color = System.Drawing.Color.FromArgb(255, cur.R, cur.G, cur.B),
                };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                var c = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);

                static byte Darken(byte v) => (byte)Math.Max(0, v - (int)(v * 0.18));
                body.Background = new SolidColorBrush(c);
                body.BorderBrush = new SolidColorBrush(Color.FromRgb(Darken(c.R), Darken(c.G), Darken(c.B)));

                // 深色底自动切浅色文字（含 ✕ 按钮），浅色底恢复默认墨色
                double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                var fg = lum < 0.5 ? Colors.White : Color.FromRgb(0x4A, 0x40, 0x20);
                _box.Foreground = new SolidColorBrush(fg);
                close.Foreground = new SolidColorBrush(lum < 0.5 ? Color.FromRgb(0xF0, 0xF0, 0xF0) : Color.FromRgb(0x8A, 0x7A, 0x30));
            };
            menu.Items.Add(miColor);

            // ---- 文字字号：预设档位，单选 ----
            var miFont = new MenuItem { Header = "文字字号" };
            foreach (var s in new double[] { 12, 13, 14, 16, 18, 20 })
            {
                var mi = new MenuItem { Header = $"{s} px", IsCheckable = true, IsChecked = Math.Abs(_box.FontSize - s) < 0.1 };
                mi.Click += (_, _) =>
                {
                    _box.FontSize = s;
                    foreach (var o in miFont.Items) if (o is MenuItem other) other.IsChecked = ReferenceEquals(other, mi);
                };
                miFont.Items.Add(mi);
            }
            menu.Items.Add(miFont);

            // ---- 不透明度：整窗透明（含文字），预设档位，单选 ----
            var miOpacity = new MenuItem { Header = "不透明度" };
            foreach (var pct in new int[] { 100, 90, 80, 70, 60 })
            {
                var mi = new MenuItem { Header = $"{pct}%", IsCheckable = true, IsChecked = pct == 100 };
                mi.Click += (_, _) =>
                {
                    Opacity = pct / 100.0;
                    foreach (var o in miOpacity.Items) if (o is MenuItem other) other.IsChecked = ReferenceEquals(other, mi);
                };
                miOpacity.Items.Add(mi);
            }
            menu.Items.Add(miOpacity);

            menu.Items.Add(new Separator());
            var miTop = new MenuItem { Header = "取消置顶", IsCheckable = true, IsChecked = true };
            miTop.Click += (_, _) => Topmost = miTop.IsChecked;
            var miClose = new MenuItem { Header = "关闭便签" };
            miClose.Click += (_, _) => Close();
            menu.Items.Add(miTop);
            menu.Items.Add(miClose);
            ContextMenu = menu;

            Content = body;

            // ---- 编辑回写暂存架：停手 600ms 防抖 ----
            _saveDebounce = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600),
            };
            _saveDebounce.Tick += (_, _) =>
            {
                _saveDebounce.Stop();
                if (_item != null && _store != null && _store.Shots.Contains(_item))
                    _store.UpdateText(_item, _box.Text);
            };
            _box.TextChanged += (_, _) => _saveDebounce.Stop();
            _box.TextChanged += (_, _) => _saveDebounce.Start();

            // ---- 位置：物理像素 → 本屏 DIP（换算同 PinWindow.PlacePhysical），
            //      光标处为中心、水平居中，略往上提一点 ----
            var mon = MiniPix.Core.MonitorManager.AtPoint(pxTopLeft.X, pxTopLeft.Y);
            Left = mon.Int32Rect.X + (pxTopLeft.X - mon.Int32Rect.X) / mon.ScaleX - Width / 2;
            Top = mon.Int32Rect.Y + (pxTopLeft.Y - mon.Int32Rect.Y) / mon.ScaleY - 14;

            Closed += (_, _) => _open.Remove(this);
        }

        /// <summary>打开一张新便签（每次拖出都开一张新的，不去重）。</summary>
        public static void ShowNote(string text, ShotItem? item, ShotStore? store, Point pxTopLeft)
        {
            var w = new TextNoteWindow(text, item, store, pxTopLeft);
            _open.Add(w);
            w.Show();
        }
    }
}
