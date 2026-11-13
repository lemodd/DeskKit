using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskKit.Transfer
{
    /// <summary>
    /// 连接授权门（2026-11-13 T-H1 修复）：陌生 IP 首次连接时在宿主机弹窗征求同意，
    /// 点「允许」后记住（持久化到 config LanApprovedIps），此后不再询问。
    /// 设计要点：
    /// - 本机 IP（HostIps：localhost / 本机网卡 / Tailscale 自连）始终放行，专供 deskkit 身份；
    /// - 「拒绝」只在本次运行内静默（不持久化）——拒绝≠永久封禁，设备下次连接会再问；
    /// - 同一 IP 的授权窗未关闭期间不重复弹（_pendingDialog 去重）；
    /// - 客户端零改动：浏览器/DeskKit 客户端本就在轮询，授权后下一拍自动恢复。
    /// </summary>
    public static class ConnectionGuard
    {
        private static readonly object _lock = new();
        private static readonly HashSet<string> _approved = new();
        private static readonly HashSet<string> _deniedSession = new();
        private static readonly HashSet<string> _pendingDialog = new();

        /// <summary>服务启动时装载持久化的已授权 IP 表。</summary>
        public static void Initialize(IEnumerable<string> approvedIps)
        {
            lock (_lock)
            {
                _approved.Clear();
                foreach (var ip in approvedIps) _approved.Add(ip);
            }
            MiniPix.Core.Log.Write($"connection guard: {_approved.Count} approved ip(s) loaded");
        }

        /// <summary>本机请求（NormalizeIp 后的 ip 落在 HostIps 内：localhost/本机网卡/Tailscale 自连）。</summary>
        public static bool IsLocalIp(string ipNorm, TransferService svc)
            => svc.HostIps.Contains(ipNorm);

        /// <summary>是否放行该（已规范化）IP。false 时调用方回等待页/403，并可能触发授权弹窗。</summary>
        public static bool Allow(string ipNorm, TransferService svc)
        {
            if (IsLocalIp(ipNorm, svc)) return true;
            lock (_lock)
            {
                if (_approved.Contains(ipNorm)) return true;
                if (_deniedSession.Contains(ipNorm)) return false; // 本会话已拒绝，静默
            }
            RequestApproval(ipNorm);
            return false;
        }

        private static void RequestApproval(string ipNorm)
        {
            bool show;
            lock (_lock) show = _pendingDialog.Add(ipNorm);
            if (!show) return; // 弹窗已在路上

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                lock (_lock) _pendingDialog.Remove(ipNorm);
                return;
            }
            dispatcher.BeginInvoke(() => ShowApprovalDialog(ipNorm));
        }

        private static void ShowApprovalDialog(string ipNorm)
        {
            try
            {
                var dlg = new System.Windows.Window
                {
                    Title = "DeskKit 连接请求",
                    Width = 400,
                    SizeToContent = System.Windows.SizeToContent.Height,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                    Topmost = true,
                    ResizeMode = System.Windows.ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    UseLayoutRounding = true,
                };
                var stack = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(20) };
                stack.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "有新设备请求连接到本机 DeskKit",
                    FontWeight = System.Windows.FontWeights.Bold,
                    FontSize = 15,
                });
                stack.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = ipNorm,
                    FontSize = 15,
                    Margin = new System.Windows.Thickness(0, 10, 0, 0),
                });
                stack.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "允许后，该设备可与本机互发文字、图片和文件。\n拒绝则本次运行内不再询问（该设备下次连接会再问）。",
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                    Foreground = System.Windows.Media.Brushes.Gray,
                    Margin = new System.Windows.Thickness(0, 8, 0, 0),
                });
                var row = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    Margin = new System.Windows.Thickness(0, 16, 0, 0),
                };
                var ok = new System.Windows.Controls.Button { Content = "允许", Width = 88, Height = 30, IsDefault = true };
                var no = new System.Windows.Controls.Button { Content = "拒绝", Width = 88, Height = 30, IsCancel = true, Margin = new System.Windows.Thickness(10, 0, 0, 0) };
                try
                {
                    var style = System.Windows.Application.Current?.TryFindResource("FlatBtn") as System.Windows.Style;
                    if (style != null) { ok.Style = style; no.Style = style; }
                }
                catch { /* 样式缺失不致命 */ }
                ok.Click += (_, _) => { Decide(ipNorm, true); dlg.Close(); };
                no.Click += (_, _) => { Decide(ipNorm, false); dlg.Close(); };
                row.Children.Add(ok);
                row.Children.Add(no);
                stack.Children.Add(row);
                dlg.Content = stack;
                dlg.ShowDialog();
            }
            catch (Exception ex)
            {
                MiniPix.Core.Log.Write("connection approval dialog failed: " + ex.Message);
            }
            finally
            {
                lock (_lock) _pendingDialog.Remove(ipNorm);
            }
        }

        private static void Decide(string ipNorm, bool allow)
        {
            lock (_lock)
            {
                if (allow)
                {
                    _approved.Add(ipNorm);
                    Persist();
                }
                else
                {
                    _deniedSession.Add(ipNorm);
                }
            }
            MiniPix.Core.Log.Write($"connection approval: {ipNorm} -> {(allow ? "ALLOW" : "DENY")}");
        }

        /// <summary>撤销某 IP 的授权（后续供传输设置"忘记已授权设备"用）。</summary>
        public static void Revoke(string ipNorm)
        {
            lock (_lock)
            {
                if (_approved.Remove(ipNorm)) Persist();
            }
        }

        /// <summary>已授权 IP 快照（设置界面展示用）。</summary>
        public static List<string> ApprovedSnapshot()
        {
            lock (_lock) return _approved.ToList();
        }

        private static void Persist()
        {
            try
            {
                var cfg = ShotShelf.AppConfig.Current;
                cfg.LanApprovedIps = ApprovedSnapshot();
                cfg.Save();
            }
            catch (Exception ex)
            {
                MiniPix.Core.Log.Write("connection guard persist failed: " + ex.Message);
            }
        }
    }
}
