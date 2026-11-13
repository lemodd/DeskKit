using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using QRCoder;

namespace DeskKit.Transfer
{
    /// <summary>
    /// 局域网传输服务编排（2026-11-10）：
    /// 启动/停止、局域网 IP 探测、二维码生成、发送入队、接收落盘（重名 fff 变体 + .part 原子改名）。
    /// TransferService.Start() 整体 try/catch，失败返 null 不影响主程序（查 log.txt）。
    /// </summary>
    public sealed class TransferService : IDisposable
    {
        public static TransferService? Instance { get; private set; }

        /// <summary>本机收到文字（2026-11-10 文字互发，HTTP 线程触发）。
        /// MainWindow 订阅：marshal 到 UI 线程后存入"文"标签（与剪贴板文字同一入库链路）。</summary>
        public static event Action<string>? TextReceived;

        /// <summary>本机收到图片的事件参数：TempPath=上传临时文件（主窗 OnLoad 读入后自删）。</summary>
        public sealed record LanImageReceived(string TempPath, string FromId, string FromName, string FileName, long Size);

        /// <summary>本机收到图片：主窗订阅后直接入"图"标签（2026-11-10 反馈：不落"传输接收"）。</summary>
        private static Action<LanImageReceived>? _imageReceived;

        /// <summary>是否有主窗订阅图片事件（未订阅时 Controller 退回落"传输接收"目录）。</summary>
        public static bool ImageHasListener { get; private set; }

        public static event Action<LanImageReceived>? ImageReceived
        {
            add { _imageReceived += value; ImageHasListener = true; }
            remove { _imageReceived -= value; }
        }

        public TransferStore Store { get; } = new();
        private readonly LanServer _server = new();

        /// <summary>实际监听端口（配置端口被占自动 +1）</summary>
        public int Port => _server.ActualPort;
        /// <summary>本机首选局域网 IPv4（物理网卡优先，UDP connect 技巧锚定）</summary>
        public string LanIp { get; }
        /// <summary>Tailscale/CGNAT（100.64.0.0/10）网段 IPv4；未装 Tailscale 时 null（2026-11-10 反馈：任意网络可用）</summary>
        public string? TailscaleIp { get; }
        /// <summary>所有可访问的本机 IPv4（LanIp 在首位，TailscaleIp 在后）</summary>
        public IReadOnlyList<string> HostIps { get; }
        /// <summary>浏览器访问地址 http://ip:port/（首选物理网卡）</summary>
        public string Url => $"http://{LanIp}:{Port}/";
        /// <summary>Tailscale 访问地址（两端都装 Tailscale 时任意网络可用，含手机蜂窝）；无 Tailscale 时 null</summary>
        public string? TailscaleUrl => TailscaleIp is null ? null : $"http://{TailscaleIp}:{Port}/";

        /// <summary>按"客户端从哪个本机网卡进来"挑 URL（/api/state 按来源返回用）：
        /// 客户端能从这个地址连进来就必然能访问它。hostIp 空/回环 → 主 Url。</summary>
        public string UrlFor(string? hostIp)
            => string.IsNullOrWhiteSpace(hostIp) || hostIp is "127.0.0.1" or "::1"
                ? Url : $"http://{hostIp}:{Port}/";
        /// <summary>连接地址二维码 PNG（PngByteQRCode，无 System.Drawing 依赖）</summary>
        public byte[] QrPng { get; }

        /// <summary>浏览器→浏览器中转的临时落盘目录（领取后删除；.part/.staged 残留启动时清扫）</summary>
        public static string TempDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskKit", "transfer_tmp");

        /// <summary>接收文件保存目录（config LanReceiveDir 空 → 跟随自动保存目录，在其中建"传输接收"子文件夹；
        /// AutoSaveDir 也为空/不存在 → 兜底 %LOCALAPPDATA%\DeskKit\received）（2026-11-10 反馈：收到的文件要与图片落盘在同一个盘）</summary>
        public static string ReceiveDir
        {
            get
            {
                var dir = ShotShelf.AppConfig.Current.LanReceiveDir;
                if (!string.IsNullOrWhiteSpace(dir)) return dir;
                var auto = ShotShelf.AppConfig.Current.AutoSaveDir;
                if (!string.IsNullOrWhiteSpace(auto) && Directory.Exists(auto))
                    return Path.Combine(auto, "传输接收");
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeskKit", "received");
            }
        }

        private TransferService()
        {
            (LanIp, TailscaleIp, HostIps) = DetectHostIps();
            QrPng = MakeQr(Url);
            CleanTempDir();
        }

        /// <summary>启动服务（App.OnStartup 调用，UI 线程）。失败返回 null 并写日志。</summary>
        public static TransferService? Start()
        {
            try
            {
                var svc = new TransferService();
                // 端口候选：config LanPort>0 用它（+1×4 兜底）；0=自动——优先 80（浏览器输入 IP 即可，免写端口），
                // 被占（Windows 上 http.sys 常占 80）退 8080，再退 52123..（2026-11-10 反馈）
                int cfg = ShotShelf.AppConfig.Current.LanPort;
                int[] ports = cfg > 0
                    ? new[] { cfg, cfg + 1, cfg + 2, cfg + 3, cfg + 4 }
                    : new[] { 80, 8080, 52123, 52124, 52125 };
                if (!svc._server.Start(ports, c => TransferController.Handle(c, svc)))
                {
                    MiniPix.Core.Log.Write(
                        $"lan start FAILED: all candidate ports busy ({string.Join(",", ports)})");
                    return null;
                }
                Instance = svc;
                // 2026-11-13 T-H1 连接授权门：装载已授权 IP 表（陌生 IP 首连弹窗征求宿主同意）
                ConnectionGuard.Initialize(ShotShelf.AppConfig.Current.LanApprovedIps);
                MiniPix.Core.Log.Write($"lan: listening {svc.Url} (ts={(svc.TailscaleIp ?? "-")}, config LanPort={cfg})");
                return svc;
            }
            catch (Exception ex)
            {
                MiniPix.Core.Log.Write("lan start FAILED: " + ex);
                return null;
            }
        }

        public static void StopCurrent()
        {
            Instance?._server.Stop();
            Instance = null;
            MiniPix.Core.Log.Write("lan: stopped");
        }

        public void Dispose() => _server.Stop();

        // ---------------- 发送（DeskKit 端入口） ----------------

        /// <summary>图片扩展名（判定会话条目 kind=image，网页直接内嵌预览）。</summary>
        public static bool IsImageName(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
        }

        /// <summary>发文件到指定设备（拖拽到设备卡/卡片点击）：写会话，对端聊天里直接可看可下载。
        /// 用原文件路径；deleteFile=true 时（临时粘贴图）会话过期由 SweepChats 自动清理临时文件。
        /// 2026-11-12：目标为 LanClient 镜像设备（远程服务器/其 peers）→ 转发客户端上传。</summary>
        public void SendFilesTo(string deviceId, string[] paths, bool deleteFile = false)
        {
            if (paths == null || paths.Length == 0) return;
            var client = LanClient.Instance;
            if (client != null && client.IsMirror(deviceId))
            {
                client.SendFiles(deviceId, paths, deleteFile);
                return;
            }
            int n = 0;
            foreach (var p in paths)
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    var fi = new FileInfo(p);
                    var kind = IsImageName(fi.Name) ? "image" : "file";
                    Store.AddChat("deskkit", TransferStore.DeskkitName, deviceId,
                        kind, "", fi.Name, fi.Length, p, deleteFile: deleteFile);
                    n++;
                }
                catch (Exception ex)
                {
                    MiniPix.Core.Log.Write($"lan: send file fail [{p}]: {ex.Message}");
                }
            }
            MiniPix.Core.Log.Write($"lan: send {n} file(s) to {deviceId}");
        }

        /// <summary>发文字到指定设备（💬 输入窗）：写会话，对方网页聊天里出现。
        /// 2026-11-12：目标为 LanClient 镜像设备 → 转发客户端发送。</summary>
        public void SendTextTo(string deviceId, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();
            if (text.Length > 4000) text = text[..4000];
            var client = LanClient.Instance;
            if (client != null && client.IsMirror(deviceId))
            {
                client.SendText(deviceId, text);
                return;
            }
            Store.AddChat("deskkit", TransferStore.DeskkitName, deviceId, "text",
                text, "", 0, "", deleteFile: false);
            MiniPix.Core.Log.Write($"lan: send text ({text.Length} chars) to {deviceId}");
        }

        /// <summary>本机收到文字（/api/msg target=deskkit 时由 Controller 在写会话后调用）：
        /// 气泡提示 + 通知主窗存入"文"标签。会话条目由 Controller 统一写。</summary>
        public void ReceiveMessage(string text, string fromName)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();
            var preview = text.Length > 60 ? text[..60] + "…" : text;
            MiniPix.Core.Log.Write($"lan: received text ({text.Length} chars) from {fromName}");
            try { global::DeskKit.App.Notifier($"💬 {fromName}: {preview}"); } catch { }
            try { TextReceived?.Invoke(text); } catch { } // 主窗入库（内部自行 marshal）
        }

        /// <summary>本机收到图片（target=deskkit 且为图片扩展名）：气泡提示 + 通知主窗直接入"图"标签。
        /// 不经"传输接收"目录；聊天回看由主窗用暂存架缓存 png 补写会话。</summary>
        public void NotifyImageReceived(LanImageReceived e)
        {
            MiniPix.Core.Log.Write($"lan: received image {Path.GetFileName(e.FileName)} from {e.FromName}");
            try { global::DeskKit.App.Notifier($"🖼 {e.FromName} 发来图片，已存入图片"); } catch { }
            try { _imageReceived?.Invoke(e); } catch { } // 主窗入库（内部自行 marshal）
        }

        // ---------------- 接收（target=deskkit） ----------------

        /// <summary>收一个文件：流式写 ReceiveDir，重名 fff 变体兜底，气泡+日志。返回最终路径。</summary>
        public string SaveReceived(string fileName, Stream content, string fromName)
        {
            var dir = ReceiveDir;
            Directory.CreateDirectory(dir);

            var name = Path.GetFileName(fileName.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(name)) name = "file_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bin";

            // 重名兜底：name.ext → name_fff.ext → name_fff_2.ext（仿 TryAutoSave 模式）
            var baseName = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            var path = Path.Combine(dir, name);
            for (int n = 1; File.Exists(path); n++)
                path = Path.Combine(dir, baseName + "_fff" + (n > 1 ? "_" + n : "") + ext);

            // 先写 .part 再改名：中断不留下半截"成品"
            var partPath = path + ".part";
            try
            {
                using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    content.CopyTo(fs, 64 * 1024);
                File.Move(partPath, path);
            }
            catch
            {
                try { File.Delete(partPath); } catch { }
                throw;
            }

            var sizeText = new FileInfo(path).Length switch
            {
                >= 1024 * 1024 * 1024 => $"{new FileInfo(path).Length / 1024.0 / 1024 / 1024:F1} GB",
                >= 1024 * 1024 => $"{new FileInfo(path).Length / 1024.0 / 1024:F1} MB",
                >= 1024 => $"{new FileInfo(path).Length / 1024.0:F0} KB",
                _ => $"{new FileInfo(path).Length} B",
            };
            var msg = $"已收到 {name}（{sizeText}）";
            MiniPix.Core.Log.Write($"lan: received [{name}] from {fromName} -> {path}");
            try { global::DeskKit.App.Notifier(msg); } catch { }

            return path;
        }

        // ---------------- 基础设施 ----------------

        /// <summary>
        /// 多 IP 探测（2026-11-10 Tailscale 支持）：遍历 Up 网卡收集全部 IPv4——
        /// 非 100.64/10 的进物理候选，100.64/10 段取第一个作 TailscaleIp（Tailscale 虚拟网卡固定用该段；
        /// 运营商 CGNAT 即使误判也只影响展示分组，URL 选择按连接来源，与网段判定解耦）。
        /// 物理首选仍用 UDP connect 8.8.8.8:53 技巧（不发包）锚定默认路由网卡；
        /// 全失败回退候选表首位 / 127.0.0.1。
        /// </summary>
        private static (string LanIp, string? TailscaleIp, IReadOnlyList<string> All) DetectHostIps()
        {
            var physical = new List<string>();
            string? ts = null;
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != OperationalStatus.Up ||
                        n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var a in n.GetIPProperties().UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var s = a.Address.ToString();
                        if (s == "127.0.0.1" || physical.Contains(s)) continue;
                        if (IsCgnat(a.Address)) ts ??= s;
                        else physical.Add(s);
                    }
                }
            }
            catch { }

            string lan = "";
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Connect("8.8.8.8", 53);
                if (s.LocalEndPoint is IPEndPoint ep) lan = ep.Address.ToString();
            }
            catch { }
            // UDP 结果为空或落在 CGNAT 段（极少：默认路由不会指向 Tailscale）→ 用物理候选首位兜底
            if (string.IsNullOrEmpty(lan) || IsCgnat(IPAddress.Parse(lan)))
                lan = physical.Count > 0 ? physical[0] : (string.IsNullOrEmpty(lan) ? "127.0.0.1" : lan);

            var all = new List<string>();
            if (lan != "127.0.0.1") all.Add(lan);
            foreach (var ip in physical)
                if (ip != lan) all.Add(ip);
            if (ts != null) all.Add(ts);
            return (lan, ts, all);
        }

        /// <summary>100.64.0.0/10（CGNAT 段，Tailscale 虚拟网卡所在网段）</summary>
        private static bool IsCgnat(IPAddress a)
            => a.AddressFamily == AddressFamily.InterNetwork
               && a.GetAddressBytes() is var b && b.Length == 4
               && b[0] == 100 && b[1] >= 64 && b[1] <= 127;

        private static byte[] MakeQr(string url)
        {
            try
            {
                var gen = new QRCodeGenerator();
                var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
                return new PngByteQRCode(data).GetGraphic(6);
            }
            catch (Exception ex)
            {
                MiniPix.Core.Log.Write("lan: qr generate failed: " + ex.Message);
                return Array.Empty<byte>();
            }
        }

        /// <summary>清扫上次上传中断的 .part/.staged 残留</summary>
        private static void CleanTempDir()
        {
            try
            {
                if (!Directory.Exists(TempDir)) return;
                foreach (var f in Directory.GetFiles(TempDir, "*.part"))
                    try { File.Delete(f); } catch { }
                foreach (var f in Directory.GetFiles(TempDir, "*.staged"))
                    try { File.Delete(f); } catch { } // .staged 可能还有未领取的？不——已入队领取完才删，重启后队列必然为空，全清
            }
            catch (Exception ex)
            {
                MiniPix.Core.Log.Write("lan: clean temp failed: " + ex.Message);
            }
        }
    }
}
