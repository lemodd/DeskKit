using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DeskKit.Transfer
{
    /// <summary>
    /// 设备聊天窗（2026-11-11 反馈：💬 点开简易聊天窗口，实现网页版功能）：
    /// 历史气泡（文字/图片缩略/文件）、发送文字、1 秒轮询会话自动刷新 + 已读标记。
    /// 每设备一个单例窗（重复点 💬 激活已有窗）；Topmost 跟着贴边置顶的主窗走。
    /// 直读 TransferStore（同进程零 HTTP），与网页聊天共享同一份会话数据。
    /// </summary>
    public sealed class DeviceChatWindow : Window
    {
        private static readonly Dictionary<string, DeviceChatWindow> _open = new(); // deviceId → 窗

        private readonly string _deviceId;
        private readonly TransferService _svc;
        private readonly StackPanel _panel = new();          // 气泡容器
        private readonly ScrollViewer _scroll = new();       // 聊天区
        private readonly TextBox _box = new();               // 输入框
        private readonly TextBlock _headName = new();        // 头部名字+状态
        private readonly DispatcherTimer _timer;
        private string _sig = "";                            // 条目 id 拼接（去重防整页重建）
        private readonly Border _toast = new();              // 窗内提示条（托盘气泡在 Win11 常被专注模式吞掉）
        private DispatcherTimer? _toastTimer;

        /// <summary>打开（或激活）与某设备的聊天窗。</summary>
        public static void ShowOrActivate(Window owner, DeviceItem dev, TransferService svc)
        {
            if (_open.TryGetValue(dev.Id, out var w))
            {
                w.Activate();
                w._box.Focus();
                return;
            }
            var nw = new DeviceChatWindow(owner, dev, svc);
            _open[dev.Id] = nw;
            nw.Show();
        }

        private DeviceChatWindow(Window owner, DeviceItem dev, TransferService svc)
        {
            _deviceId = dev.Id;
            _svc = svc;

            Title = $"聊天 · {dev.Name}";
            Width = 360; Height = 500;
            MinWidth = 300; MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Icon = ShotShelf.IconFactory.GetWpfIcon();   // 2026-11-12 统一程序图标
            Topmost = true;
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7));
            Owner = owner;
            Closed += (_, __) => { _open.Remove(_deviceId); _timer.Stop(); };

            // ---- 头部 ----
            _headName.FontSize = 14;
            _headName.FontWeight = FontWeights.SemiBold;
            _headName.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x38, 0x3F));
            var head = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(12, 9, 12, 9),
                Child = _headName,
            };

            // ---- 聊天区 ----
            _panel.Margin = new Thickness(10, 8, 10, 4);
            _scroll.Content = _panel;
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.Background = Brushes.Transparent;
            _scroll.AllowDrop = true; // 拖文件进聊天区直接发送
            _scroll.DragOver += (s, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            _scroll.Drop += (s, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
                    SendPaths(paths);
                e.Handled = true;
            };

            // ---- 输入区（2026-11-11 反馈：微信式——输入框和工具条同在一个白底容器里） ----
            _box.AcceptsReturn = true;
            _box.TextWrapping = TextWrapping.Wrap;
            _box.FontSize = 13;
            _box.MinHeight = 42;
            _box.MaxHeight = 320; // 上限放大：配合上方拖拽把手可自由调高
            _box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _box.Padding = new Thickness(4, 2, 4, 0);
            _box.BorderThickness = new Thickness(0); // 无边框，容器统一描边
            _box.Background = Brushes.Transparent;
            _box.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
                {
                    e.Handled = true;
                    SendCurrent();
                    return;
                }
                // Ctrl+V：剪贴板是截图/图片 → 存临时 png 发送；是文件列表 → 直接发文件；文字走系统粘贴
                if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    var clip = Clipboard.GetDataObject();
                    if (clip != null && !clip.GetDataPresent(DataFormats.Text))
                    {
                        if (clip.GetDataPresent(DataFormats.FileDrop) &&
                            clip.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                        {
                            e.Handled = true;
                            SendPaths(files);
                            return;
                        }
                        if (clip.GetDataPresent(DataFormats.Bitmap))
                        {
                            e.Handled = true;
                            SendClipboardImage();
                            return;
                        }
                    }
                }
            };

            // ---- ＋ 附文件/图片按钮（工具条左侧；2026-11-11 反馈：加边框） ----
            var btnAttach = new Button
            {
                Content = "＋",
                Width = 30,
                Height = 30,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
                BorderThickness = new Thickness(1),
                Foreground = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)),
                FontSize = 15,
                Cursor = Cursors.Hand,
                ToolTip = "发送图片或文件（也可直接拖文件进聊天区，或 Ctrl+V 粘贴截图）",
            };
            btnAttach.Click += (_, _) => PickAndSend();

            var btn = new Button
            {
                Content = "发送",
                Height = 28,
                MinWidth = 64,
                Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x07, 0xC1, 0x60)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 13,
                Cursor = Cursors.Hand,
            };
            btn.Click += (_, _) => SendCurrent();

            // 工具条：＋ 在左，发送在右
            var tools = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(btnAttach, 0);
            Grid.SetColumn(btn, 2);
            tools.Children.Add(btnAttach);
            tools.Children.Add(btn);

            // 微信式容器：输入区 + 工具条叠放，容器统一圆角描边
            var inputBox = new Grid();
            inputBox.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            inputBox.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_box, 0);
            Grid.SetRow(tools, 1);
            inputBox.Children.Add(_box);
            inputBox.Children.Add(tools);
            var inputWrap = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(10, 0, 10, 10),
                Child = inputBox,
            };

            // ---- 输入框高度拖拽把手（2026-11-11 反馈：输入框高度可调，网页同款） ----
            var gripBar = new Border
            {
                Width = 44,
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var grip = new Border
            {
                Height = 10,
                Background = Brushes.Transparent, // 命中区大于可见条
                Cursor = Cursors.SizeNS,
                Child = gripBar,
                Margin = new Thickness(0, 2, 0, 0),
            };
            grip.MouseLeftButtonDown += (s, e) =>
            {
                if (s is Border b) b.CaptureMouse();
                grip.Tag = (e.GetPosition(this).Y, _box.ActualHeight);
            };
            grip.MouseMove += (s, e) =>
            {
                // Tag 只在有按下时写入，IsMouseCaptured 防误触
                if (s is not Border b || !b.IsMouseCaptured || b.Tag is not (double startY, double startH)) return;
                double dy = e.GetPosition(this).Y - startY;
                _box.Height = Math.Clamp(startH - dy, 42, 320); // 上拖=加高
            };
            grip.MouseLeftButtonUp += (s, e) =>
            {
                if (s is Border b) { b.ReleaseMouseCapture(); b.Tag = null; }
            };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(head, 0);
            Grid.SetRow(_scroll, 1);
            Grid.SetRow(grip, 2);
            Grid.SetRow(inputWrap, 3);
            root.Children.Add(head);
            root.Children.Add(_scroll);
            root.Children.Add(grip);
            root.Children.Add(inputWrap);

            // ---- 窗内 toast（覆盖层，默认隐藏；托盘气泡在 Win11 专注模式下不可见，弃用） ----
            _toast.Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0));
            _toast.CornerRadius = new CornerRadius(8);
            _toast.Padding = new Thickness(16, 9, 16, 9);
            _toast.HorizontalAlignment = HorizontalAlignment.Center;
            _toast.VerticalAlignment = VerticalAlignment.Center;
            _toast.Visibility = Visibility.Hidden;
            _toast.IsHitTestVisible = false;
            var toastText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 13,
            };
            _toast.Child = toastText;
            Grid.SetRowSpan(_toast, 4);
            root.Children.Add(_toast);
            Content = root;

            Loaded += (_, _) =>
            {
                Rebuild(force: true);
                _box.Focus();
            };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Rebuild(force: false);
            _timer.Start();
        }

        private void SendCurrent()
        {
            var text = _box.Text.Trim();
            if (text.Length == 0) return;
            if (text.Length > 4000) text = text[..4000];
            _box.Clear();
            _svc.SendTextTo(_deviceId, text);
            Rebuild(force: true); // 自己的消息立刻上屏（不等下一秒轮询）
        }

        /// <summary>写剪贴板并提示。2026-11-11 反馈：复制要照常进暂存架（用户要求）。
        /// 走 App.CopyTextToClipboard：后台 STA 线程写（UI 零阻塞、提示先行）、不抑制捕获，
        /// 文字由 ShotShelf 监听器正常入库；重复卡由 ShotStore.Add 的同文去重兜底。</summary>
        private void CopyBubbleText(string text)
        {
            Toast("已复制到剪贴板"); // 乐观提示：绝大多数重试一次内就成功
            global::DeskKit.App.CopyTextToClipboard(text,
                () => Toast("复制失败，请重试"));
        }

        /// <summary>窗内轻提示：居中黑条 1.4 秒后消失（托盘气泡在 Win11 专注模式下看不见）。</summary>
        private void Toast(string msg)
        {
            (_toast.Child as TextBlock)!.Text = msg;
            _toast.Visibility = Visibility.Visible;
            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
            _toastTimer.Tick += (_, _) => { _toast.Visibility = Visibility.Hidden; _toastTimer!.Stop(); };
            _toastTimer.Start();
        }

        /// <summary>📎 按钮：多选文件/图片对话框 → 发送。</summary>
        private void PickAndSend()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要发送的图片或文件",
                Multiselect = true,
            };
            if (dlg.ShowDialog(this) == true)
                SendPaths(dlg.FileNames);
        }

        /// <summary>发文件/图片进会话（图片自动按扩展名判定为 image，网页/本窗都显示缩略图）。</summary>
        private void SendPaths(string[] paths)
        {
            var ok = paths.Where(File.Exists).ToArray();
            if (ok.Length == 0) return;
            _svc.SendFilesTo(_deviceId, ok);
            Rebuild(force: true);
        }

        /// <summary>Ctrl+V 粘贴截图：剪贴板位图存临时 png → 发送（deleteFile=true，
        /// 会话 24h 过期时由 Store 的 SweepChats 统一清理临时文件，窗口关了也不漏）。</summary>
        private void SendClipboardImage()
        {
            try
            {
                if (Clipboard.GetImage() is not { } src) return;
                // BmpSource → PNG 编码到内存再落盘（PngEncoder 需要 32bpp）
                var converted = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(converted));
                string path = Path.Combine(Path.GetTempPath(), $"dkchat-{Guid.NewGuid():N}.png");
                using (var fs = File.Create(path))
                    enc.Save(fs);

                _svc.SendFilesTo(_deviceId, new[] { path }, deleteFile: true);
                Rebuild(force: true);
            }
            catch (Exception ex)
            {
                MiniPix.Core.Log.Write("chat: paste image failed: " + ex.Message);
            }
        }

        /// <summary>拉会话 → 有变化才重建气泡；打开期间顺手标已读 + 刷新在线状态。</summary>
        private void Rebuild(bool force)
        {
            var dev = _svc.Store.Devices.FirstOrDefault(d => d.Id == _deviceId);
            UpdateHeader(dev);

            var entries = _svc.Store.GetChat("deskkit", _deviceId);
            var sig = string.Concat(entries.Select(e => e.Id));
            if (!force && sig == _sig) return;
            _sig = sig;

            bool nearBottom = _scroll.ScrollableHeight <= 0 ||
                _scroll.VerticalOffset > _scroll.ScrollableHeight - 80;
            _panel.Children.Clear();
            foreach (var e in entries)
                _panel.Children.Add(BuildBubble(e));
            if (nearBottom) _scroll.ScrollToEnd();
            _svc.Store.MarkRead("deskkit", _deviceId); // 打开着就算已读（与网页一致）
        }

        private void UpdateHeader(DeviceItem? dev)
        {
            var name = dev?.Name ?? _deviceId[..Math.Min(6, _deviceId.Length)];
            var status = dev is { IsOnline: true } ? "在线" : "离线";
            _headName.Inlines.Clear();
            _headName.Inlines.Add(name + "  ");
            _headName.Inlines.Add(new Run(status)
            {
                FontSize = 11,
                FontWeight = FontWeights.Normal,
                Foreground = new SolidColorBrush(dev is { IsOnline: true }
                    ? Color.FromRgb(0x4C, 0xAF, 0x50)
                    : Color.FromRgb(0xB8, 0xB8, 0xB8)),
            });
        }

        /// <summary>单条气泡：自己靠右绿色，对方靠左白色；图片显示缩略（点击打开原图），
        /// 文件带打开；文字（2026-11-11 反馈）点击复制 + 右键菜单复制。</summary>
        private FrameworkElement BuildBubble(ChatEntry e)
        {
            bool mine = e.FromId == "deskkit";
            object content;
            var clickable = false;
            bool isText = e.Kind == "text";

            if (e.Kind == "image")
            {
                content = File.Exists(e.FilePath) ? MakeImageThumb(e.FilePath) : MakeGone("🖼 图片已过期");
                clickable = File.Exists(e.FilePath);
            }
            else if (e.Kind == "file")
            {
                var sp = new StackPanel { MaxWidth = 210 };
                sp.Children.Add(new TextBlock
                {
                    Text = "📄 " + (string.IsNullOrEmpty(e.FileName) ? "文件" : e.FileName),
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                });
                sp.Children.Add(new TextBlock
                {
                    // 收到的文件（2026-11-13 反馈）：点击打开所在目录，不直接打开文件
                    Text = FormatSize(e.Size) + " · " + (mine ? "点击打开" : "点击打开所在目录"),
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x6B, 0xC0)),
                    Margin = new Thickness(0, 2, 0, 0),
                });
                content = sp;
                clickable = File.Exists(e.FilePath);
            }
            else
            {
                content = new TextBlock
                {
                    Text = string.IsNullOrEmpty(e.Text) ? "（空消息）" : e.Text,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x19, 0x19, 0x19)),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 220,
                };
            }

            var bub = new Border
            {
                Background = mine
                    ? new SolidColorBrush(Color.FromRgb(0x95, 0xEC, 0x69))
                    : Brushes.White,
                BorderBrush = mine ? Brushes.Transparent
                    : new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)),
                BorderThickness = mine ? new Thickness(0) : new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(9, 6, 9, 6),
                Child = (UIElement)content,
                HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Cursor = clickable || isText ? Cursors.Hand : Cursors.Arrow,
                ToolTip = isText ? "点击复制文字" : null,
            };
            if (clickable)
            {
                bool isFile = e.Kind == "file";
                bub.MouseLeftButtonDown += (_, ev) =>
                {
                    // 收到的文件 → 资源管理器定位所在目录；自己发的文件 / 图片 → 照常打开
                    if (isFile && !mine) OpenContainingFolder(e.FilePath);
                    else OpenLocalFile(e.FilePath, mine);
                    ev.Handled = true;
                };
            }
            if (isText)
            {
                bub.MouseLeftButtonDown += (_, ev) => { CopyBubbleText(e.Text); ev.Handled = true; };
                // 右键菜单也给一个复制入口（想只复制部分文字时用右键方便）
                var menu = new ContextMenu();
                var mi = new MenuItem { Header = "复制" };
                mi.Click += (_, __) => CopyBubbleText(e.Text);
                menu.Items.Add(mi);
                bub.ContextMenu = menu;
            }

            var time = new TextBlock
            {
                Text = e.Ts.ToLocalTime().ToString("HH:mm"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
                Margin = new Thickness(2, 2, 2, 0),
                HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            var col = new StackPanel();
            col.Children.Add(bub);
            col.Children.Add(time);
            return new Border { Child = col, Margin = new Thickness(0, 3, 0, 3) };
        }

        /// <summary>图片缩略（解码到 200px 宽，OnLoad+Freeze 不占句柄）。</summary>
        private static FrameworkElement MakeImageThumb(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 200;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return new Image
                {
                    Source = bmp,
                    Width = 200,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
            }
            catch
            {
                return MakeGone("🖼 图片无法显示");
            }
        }

        private static TextBlock MakeGone(string text) => new()
        {
            Text = text,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
        };

        /// <summary>可执行/脚本类扩展名（2026-11-13 T-M5）：外部设备传来的这类文件不允许
        /// 点气泡直接执行，防止"手机传个 exe → 电脑误点即运行"。</summary>
        private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".bat", ".cmd", ".com", ".scr", ".lnk", ".pif",
            ".js", ".jse", ".vbs", ".vbe", ".wsf", ".ps1", ".hta", ".msi", ".jar",
        };

        /// <summary>在资源管理器中打开文件所在目录并选中该文件（收到的文件气泡点击）。</summary>
        private static void OpenContainingFolder(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
                {
                    Arguments = "/select,\"" + path + "\"",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MiniPix.Core.Log.Write("chat: open folder failed [" + path + "]: " + ex.Message);
            }
        }

        private static void OpenLocalFile(string path, bool mine)
        {
            // 只拦"别人传来的"；本机自己发出去的文件本来就是本机的，照常打开
            if (!mine && DangerousExtensions.Contains(System.IO.Path.GetExtension(path)))
            {
                MessageBox.Show("为防止误点执行，外部传来的可执行/脚本文件不允许直接打开。\n" +
                                "如确认安全，请到文件的保存位置手动运行。",
                    "已拦截", MessageBoxButton.OK, MessageBoxImage.Warning);
                MiniPix.Core.Log.Write("chat: blocked executable open [" + path + "]");
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MiniPix.Core.Log.Write("chat: open file failed [" + path + "]: " + ex.Message);
            }
        }

        private static string FormatSize(long n) => n switch
        {
            >= 1073741824 => (n / 1073741824.0).ToString("F1") + " GB",
            >= 1048576 => (n / 1048576.0).ToString("F1") + " MB",
            >= 1024 => (n / 1024.0).ToString("F0") + " KB",
            _ => n + " B",
        };
    }
}
