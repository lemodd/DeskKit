using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeskKit.Transfer
{
    /// <summary>
    /// 传输设置窗口（2026-11-10 反馈：本机卡齿轮点开看详细设置+改本机名）。
    /// 纯代码建窗（参照 OcrResultWindow 白底浅色风格），非模态、单例：ShowOrActivate。
    /// 内容：设备名称（可改，存 config LanDeviceName）、服务网址+复制、端口策略说明、接收目录+打开。
    /// 2026-11-12 免配对：另一台 DeskKit 填网址直连（同浏览器权限，一 IP 一卡），无配对码。
    /// </summary>
    public sealed class TransferSettingsWindow : Window
    {
        private static TransferSettingsWindow? _wnd; // 单例

        private readonly TextBox _txtName;
        private readonly TextBlock _txtUrl, _txtPort, _txtDir, _txtMsg;
        private TextBox _txtServerUrl = null!;    // 客户端：服务器地址
        private Button _btnConnect = null!;
        private Button _btnDisconnect = null!;
        private Button _btnForget = null!;
        private TextBlock _txtClientStatus = null!;
        private StackPanel _clientCtlRow = null!;
        private Action _clientStateHandler = null!;
        private readonly Action<string?> _onRenamed; // 改名后回调（MainWindow 刷新本机卡）

        private TransferSettingsWindow(Window owner, Action<string?> onRenamed)
        {
            _onRenamed = onRenamed;
            var svc = TransferService.Instance!;

            Title = "传输设置";
            Width = 430;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Icon = ShotShelf.IconFactory.GetWpfIcon();   // 2026-11-12 统一程序图标
            Owner = owner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA));
            WindowStyle = WindowStyle.ToolWindow;
            UseLayoutRounding = true;

            var root = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };

            // ---- 设备名称 ----
            root.Children.Add(Label("设备名称（显示在其它设备的列表里）"));
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            _txtName = new TextBox
            {
                Text = TransferStore.DeskkitName,
                Width = 240,
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                MaxLength = 60,
            };
            nameRow.Children.Add(_txtName);
            nameRow.Children.Add(PrimaryBtn("保存名称", (_, _) =>
            {
                var name = _txtName.Text.Trim();
                if (name.Length == 0)
                {
                    _txtMsg.Text = "名称不能为空";
                    return;
                }
                ShotShelf.AppConfig.Current.LanDeviceName = name;
                ShotShelf.AppConfig.Current.Save();
                _onRenamed(name);
                _txtMsg.Text = "已保存";
                MiniPix.Core.Log.Write($"lan: device renamed to [{name}]");
            }));
            nameRow.Children.Add(SecondBtn("恢复默认", (_, _) =>
            {
                ShotShelf.AppConfig.Current.LanDeviceName = "";
                ShotShelf.AppConfig.Current.Save();
                _txtName.Text = TransferStore.DeskkitName; // 读回（=计算机名）
                _onRenamed(TransferStore.DeskkitName);
                _txtMsg.Text = "已恢复计算机名";
            }));
            root.Children.Add(nameRow);

            // ---- 服务网址 ----
            root.Children.Add(Label("服务网址（手机/电脑浏览器访问）"));
            var urlRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var urlCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _txtUrl = new TextBlock
            {
                Text = svc.Url,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)),
            };
            urlCol.Children.Add(_txtUrl);
            // Tailscale 地址（2026-11-10：两端装 Tailscale 时任意网络可用，含手机蜂窝）
            if (svc.TailscaleUrl != null)
                urlCol.Children.Add(new TextBlock
                {
                    Text = "Tailscale：" + svc.TailscaleUrl,
                    FontSize = 11.5,
                    Margin = new Thickness(0, 3, 0, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)),
                    ToolTip = "两端都装了 Tailscale 时，任意网络（含手机蜂窝流量）可用此地址",
                });
            urlRow.Children.Add(urlCol);
            urlRow.Children.Add(SecondBtn("复制", (_, _) =>
            {
                try
                {
                    Clipboard.SetText(string.Join(Environment.NewLine,
                        svc.HostIps.Select(ip => $"http://{ip}:{svc.Port}/")));
                    _txtMsg.Text = "网址已复制";
                }
                catch { }
            }));
            root.Children.Add(urlRow);

            // ---- 端口 ----
            root.Children.Add(Label("端口"));
            _txtPort = new TextBlock
            {
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)),
                Margin = new Thickness(0, 2, 0, 0),
            };
            root.Children.Add(_txtPort);

            // ---- 接收目录 ----
            root.Children.Add(Label("接收文件的保存目录"));
            var dirRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            _txtDir = new TextBlock
            {
                Text = TransferService.ReceiveDir,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)),
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 280,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = TransferService.ReceiveDir,
            };
            dirRow.Children.Add(_txtDir);
            dirRow.Children.Add(SecondBtn("打开目录", (_, _) =>
            {
                try { Process.Start("explorer.exe", TransferService.ReceiveDir); } catch { }
            }));
            root.Children.Add(dirRow);

            // ---- 连接到其它电脑的 DeskKit（2026-11-12 客户端半边，免配对：填网址直连） ----
            root.Children.Add(Label("连接到其它电脑的 DeskKit（填对方服务网址）"));
            var connRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            _txtServerUrl = new TextBox
            {
                Text = ShotShelf.AppConfig.Current.LanClientServerUrl,
                Width = 200,
                Height = 26,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "对方 DeskKit「传输设置」窗顶部显示的服务网址，如 192.168.1.5:80 或 100.x.x.x:80",
            };
            _btnConnect = SecondBtn("连接", (_, _) =>
            {
                var url = _txtServerUrl.Text.Trim();
                if (url.Length == 0)
                {
                    _txtClientStatus.Text = "请填对方的服务网址（见对方「传输设置」窗顶部）";
                    return;
                }
                _btnConnect.IsEnabled = false;
                EnsureClient().Connect(url);
            });
            connRow.Children.Add(WithHint(_txtServerUrl, "对方服务网址，如 192.168.1.5:80"));
            connRow.Children.Add(_btnConnect);
            root.Children.Add(connRow);

            _txtClientStatus = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0xA9, 0xA0)),
                Margin = new Thickness(0, 6, 0, 0),
                MinHeight = 15,
            };
            root.Children.Add(_txtClientStatus);

            var ctlRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            _btnDisconnect = SecondBtn("断开", (_, _) => LanClient.Instance?.Disconnect(forgetServer: false));
            _btnForget = SecondBtn("忘记服务器", (_, _) => LanClient.Instance?.Disconnect(forgetServer: true));
            ctlRow.Children.Add(_btnDisconnect);
            ctlRow.Children.Add(_btnForget);
            root.Children.Add(ctlRow);
            _clientCtlRow = ctlRow;
            RefreshClientArea();

            // 状态变化实时刷新（连接/断开/重连）；关窗退订
            _clientStateHandler = () => Dispatcher.BeginInvoke(RefreshClientArea);
            LanClient.ClientChanged += _clientStateHandler;
            Closed += (_, _) => LanClient.ClientChanged -= _clientStateHandler;

            // ---- 状态/提示行 ----
            _txtMsg = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0xA9, 0xA0)),
                Margin = new Thickness(0, 10, 0, 0),
                MinHeight = 15,
            };
            root.Children.Add(_txtMsg);
            root.Children.Add(new TextBlock
            {
                Text = "提示：同一 WiFi 或两端装 Tailscale（任意网络，含手机蜂窝）均可访问；首次启动如弹防火墙授权请点\"允许\"。",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x82, 0x8C)),
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });

            Content = root;

            // 端口说明：固定值 vs 自动
            int cfg = ShotShelf.AppConfig.Current.LanPort;
            _txtPort.Text = cfg > 0
                ? $"固定 {cfg}（config LanPort，被占自动 +1）"
                : $"自动：当前 {svc.Port}（候选顺序 80 → 8080 → 52123，网址免端口取决于是否落在 80）";

            Closed += (_, _) => _wnd = null;
        }

        /// <summary>取 LanClient 实例（懒创建：第一次"连接"时才需要）。</summary>
        private static LanClient EnsureClient() => LanClient.GetOrCreate();

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
            var g = new Grid { Width = box.Width + box.Margin.Left + box.Margin.Right };
            box.Margin = new Thickness();
            g.Children.Add(box);
            g.Children.Add(hintTb);
            return g;
        }

        /// <summary>按客户端连接状态刷新 UI（连接按钮可用性 / 断开·忘记显隐 / 状态文字）。</summary>
        private void RefreshClientArea()
        {
            var client = LanClient.Instance;
            var connected = client?.IsConnected == true;
            _btnConnect.IsEnabled = !connected;
            _btnDisconnect.Visibility = client == null ? Visibility.Collapsed : Visibility.Visible;
            _btnForget.Visibility = client != null && !string.IsNullOrEmpty(
                ShotShelf.AppConfig.Current.LanClientServerUrl) ? Visibility.Visible : Visibility.Collapsed;
            _txtClientStatus.Text = client?.StatusText
                ?? "未连接 — 填对方 DeskKit「传输设置」窗顶部显示的服务网址，点「连接」即可（无需配对码）";
        }

        /// <summary>打开或激活已开的设置窗口（非模态单例）。</summary>
        public static void ShowOrActivate(Window owner, Action<string?> onRenamed)
        {
            if (_wnd != null)
            {
                _wnd.Activate();
                return;
            }
            _wnd = new TransferSettingsWindow(owner, onRenamed);
            _wnd.Show();
        }

        private static TextBlock Label(string text) => new()
        {
            Text = text,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x82, 0x8C)),
            Margin = new Thickness(0, 12, 0, 0),
        };

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
