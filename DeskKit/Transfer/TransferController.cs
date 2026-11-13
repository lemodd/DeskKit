using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DeskKit.Transfer
{
    /// <summary>
    /// 路由分发（2026-11-10 网页改版为微信式聊天）：
    ///   GET  /                    → WebUi 单页
    ///   GET  /qr.png              → 二维码 PNG
    ///   GET  /api/state?id=xxx    → 设备表 + 会话摘要（未读/最后一条）；轮询即心跳
    ///   POST /api/register?id=xxx → 设备注册/改名（body: {name, kind}）→ {id}
    ///   POST /api/msg             → 发文字（body: {from, target, text}）→ 写会话
    ///   POST /api/upload          → multipart（target/from + 文件 parts）→ 写会话
    ///   GET  /api/chat?id=&peer=  → 拉与 peer 的会话（最近 100 条，升序）
    ///   POST /api/read?id=&peer=  → 标记与 peer 的会话已读
    ///   GET  /api/file/{id}?id=   → 会话条目文件下载/图片预览（流式，中文文件名 RFC5987）
    /// 每请求一个连接，响应完即断（Connection: close）。
    /// </summary>
    public static class TransferController
    {
        private const long MaxJsonBody = 8 * 1024;           // register/msg 这类小 JSON
        private const long MaxUploadBody = 20L * 1024 * 1024 * 1024; // 上传上限 20GB（防滥用）

        private static byte[]? _favicon;
        /// <summary>网页 favicon = 程序图标（内嵌 DeskKit.ico 读一次缓存）。</summary>
        private static byte[] FaviconBytes()
        {
            if (_favicon != null) return _favicon;
            try
            {
                using var s = typeof(TransferController).Assembly
                    .GetManifestResourceStream("DeskKit.Assets.DeskKit.ico");
                if (s != null)
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        _favicon = ms.ToArray();
                    }
            }
            catch { }
            return _favicon ?? Array.Empty<byte>();
        }

        public static void Handle(TcpClient client, TransferService svc)
        {
            using (client)
            using (var ns = new NetworkStream(client.Client, ownsSocket: false)) // 响应完关连接
            {
                ns.ReadTimeout = 30_000;   // 上传大文件时每 64KB 一读，30s 无进展才断
                ns.WriteTimeout = 30_000;

                var bs = new HttpByteStream(ns);
                var req = HttpProcessor.ParseRequest(bs);
                if (req == null) return; // 非 HTTP/立即断开

                var ip = IpOf(client.Client.RemoteEndPoint as IPEndPoint);
                var hostIp = IpOf(client.Client.LocalEndPoint as IPEndPoint); // 服务端实际接收网卡 → 决定回给哪个 URL

                try
                {
                    Route(req, ns, bs, svc, ip, hostIp);
                }
                catch (Exception ex)
                {
                    MiniPix.Core.Log.Write($"lan route error {req.Method} {req.Path}: {ex.Message}");
                    try { HttpProcessor.WriteJson(ns, 400, "{\"error\":\"bad request\"}"); }
                    catch { }
                }
            }
        }

        private static void Route(HttpRequest req, NetworkStream ns, HttpByteStream bs,
            TransferService svc, string ip, string hostIp)
        {
            MiniPix.Core.Log.Write($"lan >> {req.Method} {req.Path} ip={ip}"); // BISECT-TEMP: 请求插桩

            // 2026-11-13 T-H1 连接授权门：本机直通；陌生 IP 首连弹窗征求宿主同意，
            // 允许后（记入 config）该 IP 的后续请求正常放行。
            var ipNorm = NormalizeIp(ip, svc);
            if (!ConnectionGuard.Allow(ipNorm, svc))
            {
                if (req.Method == "GET" && (req.Path == "/" || req.Path == "/qr.png"))
                    WritePendingPage(ns); // 浏览器首访：给等待页自动刷新，授权后自动进入
                else
                    HttpProcessor.WriteJson(ns, 403, "{\"needApproval\":true}"); // API 轮询：下一拍自动恢复
                return;
            }

            switch (req.Path)
            {
                case "/" when req.Method == "GET":
                    HttpProcessor.WriteResponse(ns, 200, "text/html; charset=utf-8",
                        Encoding.UTF8.GetBytes(WebUi.Html));
                    return;

                case "/qr.png" when req.Method == "GET":
                    HttpProcessor.WriteResponse(ns, 200, "image/png", svc.QrPng);
                    return;

                // 2026-11-12：网页 favicon = 程序图标（内嵌 DeskKit.ico 直出）
                case "/favicon.ico" when req.Method == "GET":
                    HttpProcessor.WriteResponse(ns, 200, "image/x-icon", FaviconBytes());
                    return;

                case "/api/state" when req.Method == "GET":
                    HandleState(req, ns, svc, ip, hostIp);
                    return;

                case "/api/register" when req.Method == "POST":
                    HandleRegister(req, bs, ns, svc, ip);
                    return;

                case "/api/msg" when req.Method == "POST":
                    HandleMsg(req, bs, ns, svc, ip);
                    return;

                case "/api/upload" when req.Method == "POST":
                    HandleUpload(req, bs, ns, svc, ip);
                    return;

                case "/api/chat" when req.Method == "GET":
                    HandleChat(req, ns, svc, ip);
                    return;

                case "/api/read" when req.Method == "POST":
                    HandleRead(req, ns, svc, ip);
                    return;

                case string p when p.StartsWith("/api/file/", StringComparison.Ordinal) && req.Method == "GET":
                    HandleDownload(req, p["/api/file/".Length..], ns, svc, ip);
                    return;

                default:
                    HttpProcessor.WriteJson(ns, 404, "{\"error\":\"not found\"}");
                    return;
            }
        }

        /// <summary>端点 → IPv4 字符串。IPAddress.Any 监听在 .NET 5+ 是 IPv6 双模 socket，
        /// IPv4 连接的端点是 ::ffff:a.b.c.d 映射形式，必须还原成纯 IPv4（来源 IP 去重/URL 拼接都依赖）。</summary>
        private static string IpOf(IPEndPoint? ep)
        {
            if (ep == null) return "";
            var a = ep.Address;
            if (a.AddressFamily == AddressFamily.InterNetworkV6) a = a.MapToIPv4();
            return a.ToString();
        }

        /// <summary>回环连接（电脑本机开 localhost 页面）映射到局域网 IP——否则同一台电脑
        /// 经 localhost 和经局域网地址两个入口的会话 IP 不同，同 IP 去重失效（2026-11-10）。</summary>
        private static string NormalizeIp(string ip, TransferService svc)
            => (ip == "127.0.0.1" || ip == "::1") ? svc.LanIp : ip;

        // ---------------- /api/state ----------------

        /// <summary>等价于 IsLocalIp(NormalizeIp(ip))：deskkit 伪身份只允许本机来源使用，
        /// 防止已授权的外部设备借 id=deskkit 读取宿主全部会话（T-H1 配套）。</summary>
        private static bool DeskkitAllowed(string ip, TransferService svc)
            => ConnectionGuard.IsLocalIp(NormalizeIp(ip, svc), svc);

        /// <summary>CSRF 防护（T-M1）：浏览器跨站 POST 一定带 Origin 且与本服务 Host 不同；
        /// 本机 DeskKit 客户端/curl 不带 Origin → 放行。返回 true = 跨站请求，拒绝。</summary>
        private static bool CrossSitePost(HttpRequest req)
        {
            var origin = req.Header("Origin");
            if (string.IsNullOrEmpty(origin)) return false;
            var host = req.Header("Host");
            if (string.IsNullOrEmpty(host)) return true;
            return !Uri.TryCreate(origin, UriKind.Absolute, out var u)
                || !string.Equals(u.Authority, host, StringComparison.OrdinalIgnoreCase);
        }

        private static void WritePendingPage(NetworkStream ns)
        {
            // 浏览器首访等待页：2 秒自动刷新，宿主点「允许」后自动进入正式界面（客户端零改动）
            var html = "<!doctype html><meta charset=utf-8>" +
                       "<meta http-equiv=refresh content=2>" +
                       "<body style=\"font-family:system-ui,sans-serif;display:flex;justify-content:center;" +
                       "align-items:center;height:90vh;color:#555\">" +
                       "<div style=\"text-align:center\"><div style=\"font-size:42px\">⏳</div>" +
                       "<div style=\"margin-top:12px\">等待宿主授权…<br>请在电脑上点击「允许」</div></div>";
            HttpProcessor.WriteResponse(ns, 200, "text/html; charset=utf-8",
                Encoding.UTF8.GetBytes(html));
        }

        private static void HandleState(HttpRequest req, NetworkStream ns, TransferService svc, string ip, string hostIp)
        {
            var deviceId = req.Query.TryGetValue("id", out var id) ? id : "";
            if (string.IsNullOrEmpty(deviceId))
            {
                HttpProcessor.WriteJson(ns, 400, "{\"error\":\"invalid id\"}");
                return;
            }
            // 2026-11-13 T-H1：deskkit（宿主身份）只允许本机来源使用，
            // 已授权外部设备不得借它读取宿主全部会话
            if (deviceId == "deskkit" && !DeskkitAllowed(ip, svc))
            {
                HttpProcessor.WriteJson(ns, 403, "{\"error\":\"forbidden\"}");
                return;
            }
            string cid = deviceId;
            if (deviceId != "deskkit")
            {
                // 轮询即心跳；同 IP 接管时返回生效 id（you），客户端下次改用它轮询；
                // 未知设备（重启后旧 localStorage id）不建卡，返回 rebind 让客户端重新注册
                var (touchedId, nm) = svc.Store.RegisterOrTouch(deviceId, null, null, NormalizeIp(ip, svc), create: false);
                if (nm == null)
                {
                    HttpProcessor.WriteJson(ns, 200, "{\"rebind\":true}");
                    return;
                }
                cid = touchedId;
            }
            // 2026-11-12 一 IP 一卡：id="deskkit" = 本机浏览器会话（注册时已发此身份），
            // 无卡可续约也不心跳，直接出状态（you=deskkit，会话摘要=本机用户的全部会话）

            var state = new
            {
                // 按客户端进来的网卡返回对应地址：走 Tailscale 网卡连入 → 给 100.x 地址（任意网络可用）
                url = svc.UrlFor(hostIp),
                port = svc.Port,
                you = cid,
                devices = svc.Store.SnapshotDevices() // 2026-11-13 T-M2：快照，禁止直接枚举 UI 集合
                    .Select(d => new { id = d.Id, name = d.Name, type = d.Type, online = d.IsOnline })
                    .Concat(new[]
                    {
                        new { id = "deskkit", name = TransferStore.DeskkitName,
                              type = "deskkit", online = true },
                    }),
                // 侧栏会话摘要：peer + 最后一条预览 + 未读数
                chats = svc.Store.GetConvSummaries(cid)
                    .Select(c => new { peer = c.Peer, last = c.Last, unread = c.Unread }),
            };
            HttpProcessor.WriteJson(ns, 200,
                JsonSerializer.Serialize(state, JsonOpts));
        }

        // ---------------- /api/register ----------------

        private static void HandleRegister(HttpRequest req, HttpByteStream bs, NetworkStream ns,
            TransferService svc, string ip)
        {
            if (CrossSitePost(req)) { HttpProcessor.WriteJson(ns, 403, "{\"error\":\"cross-site\"}"); return; }
            if (req.ContentLength is < 0 or > MaxJsonBody)
            {
                HttpProcessor.WriteJson(ns, 413, "{\"error\":\"body too large\"}");
                return;
            }
            var body = bs.ReadExact(req.ContentLength, MaxJsonBody);
            string name = "", kind = "pc";
            try
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
                if (doc.RootElement.TryGetProperty("name", out var n)) name = n.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("kind", out var k)) kind = k.GetString() ?? "pc";
            }
            catch { /* 坏 JSON 用默认值 */ }
            name = name.Trim();
            if (name.Length > 60) name = name[..60];
            if (name.Length == 0) name = "未命名设备";

            // 2026-11-12 一 IP 一卡：本机浏览器（localhost / 本机 LAN / Tailscale 自连）不建卡——
            // 本机在设备标签里已有静态"本机卡"。直接发 deskkit 身份：网页与本机 DeskKit 同属
            // 一个"用户"，会话挂 deskkit 名下，网页端看到的就是本机会话。
            var ipNorm = NormalizeIp(ip, svc);
            if (svc.HostIps.Contains(ipNorm))
            {
                HttpProcessor.WriteJson(ns, 200, JsonSerializer.Serialize(
                    new { id = "deskkit", name = TransferStore.DeskkitName }, JsonOpts));
                MiniPix.Core.Log.Write($"lan: own-machine browser (ip={ip}) takes deskkit identity");
                return;
            }

            // id：浏览器 localStorage 里带的（重连复用）或新分配
            var id = req.Query.TryGetValue("id", out var qid) && !string.IsNullOrEmpty(qid)
                ? qid : Guid.NewGuid().ToString("N");
            if (id == "deskkit") id = Guid.NewGuid().ToString("N"); // 保住本机伪设备名

            // 2026-11-12 免配对：DeskKit 客户端与网页同权接入，统一走同 IP 接管（一 IP 一卡）。
            // 本机自连已在上面 HostIps 分支拦截（deskkit 身份），不会错误接管。
            var (cid, finalName) = svc.Store.RegisterOrTouch(id, name, kind, NormalizeIp(ip, svc),
                create: true, adoptIp: true);
            MiniPix.Core.Log.Write($"lan: device registered id={cid} name={finalName} kind={kind} ip={ip}");
            HttpProcessor.WriteJson(ns, 200, JsonSerializer.Serialize(new { id = cid, name = finalName }, JsonOpts));
        }

        // ---------------- /api/msg（发文字） ----------------

        /// <summary>发文字。body: {from: 设备id, target: "deskkit"|设备id, text}。
        /// 统一写会话；target=deskkit 额外走 ReceiveMessage（气泡 + 主窗存"文"标签）。</summary>
        private static void HandleMsg(HttpRequest req, HttpByteStream bs, NetworkStream ns,
            TransferService svc, string ip)
        {
            if (CrossSitePost(req)) { HttpProcessor.WriteJson(ns, 403, "{\"error\":\"cross-site\"}"); return; }
            if (req.ContentLength is < 0 or > MaxJsonBody)
            {
                HttpProcessor.WriteJson(ns, 413, "{\"error\":\"body too large\"}");
                return;
            }
            string from = "", target = "deskkit", text = "";
            try
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bs.ReadExact(req.ContentLength, MaxJsonBody)));
                if (doc.RootElement.TryGetProperty("from", out var f)) from = f.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("target", out var t)) target = t.GetString() ?? "deskkit";
                if (doc.RootElement.TryGetProperty("text", out var x)) text = x.GetString() ?? "";
            }
            catch { /* 坏 JSON 按空处理 */ }
            text = text.Trim();
            if (text.Length == 0)
            {
                HttpProcessor.WriteJson(ns, 400, "{\"error\":\"empty text\"}");
                return;
            }
            // 2026-11-13 T-H1：禁止已授权外部设备伪造 from=deskkit（宿主身份）发消息
            if (from == "deskkit" && !DeskkitAllowed(ip, svc))
            {
                HttpProcessor.WriteJson(ns, 403, "{\"error\":\"forbidden\"}");
                return;
            }
            if (text.Length > 4000) text = text[..4000];

            // 发送方显示名：按上报的设备 id 查表（重连后 IP 可能变，id 是稳定键），查不到退化为 IP
            var fromName = NameOf(from, svc) ?? ip;
            var ok = target != from && IsValidTarget(target, svc);
            if (ok)
            {
                svc.Store.AddChat(from, fromName, target, "text", text, "", 0, "", deleteFile: false);
                if (target == "deskkit")
                    svc.ReceiveMessage(text, fromName);
            }
            MiniPix.Core.Log.Write($"lan: msg from={fromName} target={target} n={text.Length} ok={ok}");
            HttpProcessor.WriteJson(ns, ok ? 200 : 400,
                JsonSerializer.Serialize(new { ok }, JsonOpts));
        }

        // ---------------- /api/upload（发文件/图片） ----------------

        private static void HandleUpload(HttpRequest req, HttpByteStream bs, NetworkStream ns,
            TransferService svc, string ip)
        {
            if (CrossSitePost(req)) { HttpProcessor.WriteJson(ns, 403, "{\"error\":\"cross-site\"}"); return; }
            var ct = req.Header("Content-Type");
            var boundary = MultipartReader.GetBoundary(ct);
            if (boundary == null || req.ContentLength <= 0 || req.ContentLength > MaxUploadBody)
            {
                HttpProcessor.WriteJson(ns, 400, "{\"error\":\"expect multipart/form-data\"}");
                return;
            }

            var parsed = MultipartReader.Parse(bs, req.ContentLength, boundary, TransferService.TempDir);
            var target = parsed.Fields.TryGetValue("target", out var t) ? t : "deskkit";
            // 发送方 id（网页带上）：用于会话归属；没带则用 "ip:x" 伪 id（消息照收，只是归属粗糙）
            var from = parsed.Fields.TryGetValue("from", out var fr) && !string.IsNullOrWhiteSpace(fr)
                ? fr : "ip:" + ip;
            // 2026-11-13 T-H1：禁止已授权外部设备伪造 from=deskkit（宿主身份）投递
            if (from == "deskkit" && !DeskkitAllowed(ip, svc))
            {
                HttpProcessor.WriteJson(ns, 403, "{\"error\":\"forbidden\"}");
                return;
            }
            var fromName = NameOf(from, svc) ?? ip;

            // 目标合法性：deskkit 或已注册设备（防随手投递到不存在的 id）
            bool validTarget = target == "deskkit" ||
                svc.Store.SnapshotDevices().Any(d => d.Id == target); // T-M2：快照
            if (!validTarget && parsed.Files.Count > 0)
            {
                HttpProcessor.WriteJson(ns, 400, "{\"error\":\"unknown target\"}");
                return;
            }

            int saved = 0;
            foreach (var f in parsed.Files)
            {
                try
                {
                    var kind = TransferService.IsImageName(f.FileName) ? "image" : "file";
                    if (target == "deskkit")
                    {
                        // 图片（2026-11-10 反馈）：不落"传输接收"，直接进"图"标签（同本地复制链路）；
                        // 聊天回看由主窗用暂存架缓存 png 补写会话。临时文件由主窗 OnLoad 后自删。
                        // 主窗未订阅（无界面等）时退回旧行为：落接收目录。
                        if (kind == "image" && TransferService.ImageHasListener)
                        {
                            svc.NotifyImageReceived(new TransferService.LanImageReceived(
                                f.SavedPath, from, fromName, f.FileName, f.Size));
                        }
                        else
                        {
                            // using 块：确保复制完关闭句柄再删临时文件（否则 Delete 撞文件锁）
                            string finalPath;
                            using (FileStream fs = File.OpenRead(f.SavedPath))
                            {
                                finalPath = svc.SaveReceived(f.FileName, fs, fromName);
                            }
                            File.Delete(f.SavedPath); // SaveReceived 已复制，删临时
                            MarkOfWeb(finalPath); // 2026-11-13 T-M5：MotW，双击时 SmartScreen 拦一道
                            // 收进来的文件也进会话（电脑端气泡 + 网页回看都有据可查）
                            svc.Store.AddChat(from, fromName, "deskkit", kind, "",
                                f.FileName, f.Size, finalPath, deleteFile: false);
                        }
                    }
                    else
                    {
                        // 浏览器→浏览器中转：.staged 留作会话文件，TTL/超限时才删
                        MarkOfWeb(f.SavedPath); // T-M5：聊天窗点开的就是这个文件，同样打标
                        svc.Store.AddChat(from, fromName, target, kind, "",
                            f.FileName, f.Size, f.SavedPath, deleteFile: true);
                    }
                    saved++;
                }
                catch (Exception ex)
                {
                    MiniPix.Core.Log.Write($"lan upload save fail [{f.FileName}]: {ex.Message}");
                }
            }
            MiniPix.Core.Log.Write($"lan: upload n={saved} target={target} ip={ip}");
            HttpProcessor.WriteJson(ns, 200, JsonSerializer.Serialize(new { ok = true, saved }, JsonOpts));
        }

        // ---------------- /api/chat（拉会话） ----------------

        private static void HandleChat(HttpRequest req, NetworkStream ns, TransferService svc, string ip)
        {
            var me = req.Query.TryGetValue("id", out var id) ? id : "";
            var peer = req.Query.TryGetValue("peer", out var p) ? p : "";
            if (string.IsNullOrEmpty(me) || string.IsNullOrEmpty(peer))
            {
                HttpProcessor.WriteJson(ns, 400, "{\"error\":\"missing id/peer\"}");
                return;
            }
            if (me == "deskkit" && !DeskkitAllowed(ip, svc))
            {
                HttpProcessor.WriteJson(ns, 403, "{\"error\":\"forbidden\"}");
                return;
            }
            var entries = svc.Store.GetChat(me, peer)
                .Select(e => new
                {
                    id = e.Id,
                    mine = e.FromId == me,
                    from = e.FromId,
                    fromName = e.FromName,
                    kind = e.Kind,
                    text = e.Text,
                    name = e.FileName,
                    size = e.Size,
                });
            HttpProcessor.WriteJson(ns, 200,
                JsonSerializer.Serialize(new { entries }, JsonOpts));
        }

        // ---------------- /api/read（标记已读） ----------------

        private static void HandleRead(HttpRequest req, NetworkStream ns, TransferService svc, string ip)
        {
            var me = req.Query.TryGetValue("id", out var id) ? id : "";
            var peer = req.Query.TryGetValue("peer", out var p) ? p : "";
            if (string.IsNullOrEmpty(me) || string.IsNullOrEmpty(peer))
            {
                HttpProcessor.WriteJson(ns, 400, "{\"error\":\"missing id/peer\"}");
                return;
            }
            if (me == "deskkit" && !DeskkitAllowed(ip, svc))
            {
                HttpProcessor.WriteJson(ns, 403, "{\"error\":\"forbidden\"}");
                return;
            }
            svc.Store.MarkRead(me, peer);
            HttpProcessor.WriteJson(ns, 200, "{\"ok\":true}");
        }

        // ---------------- /api/file/{id}（会话文件下载/图片预览） ----------------

        private static void HandleDownload(HttpRequest req, string entryId, NetworkStream ns,
            TransferService svc, string ip)
        {
            var deviceId = req.Query.TryGetValue("id", out var id) ? id : "";
            if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(entryId))
            {
                HttpProcessor.WriteJson(ns, 400, "{\"error\":\"missing id\"}");
                return;
            }
            if (deviceId == "deskkit" && !DeskkitAllowed(ip, svc))
            {
                HttpProcessor.WriteJson(ns, 403, "{\"error\":\"forbidden\"}");
                return;
            }
            var entry = svc.Store.FindChatEntry(entryId, deviceId);
            if (entry == null || entry.FilePath.Length == 0 || !File.Exists(entry.FilePath))
            {
                HttpProcessor.WriteJson(ns, 404, "{\"error\":\"file gone\"}");
                return;
            }
            MiniPix.Core.Log.Write($"lan: file served [{entry.FileName}] to {deviceId}");
            // 中转文件不删——留在会话里可反复预览/下载，由会话 TTL/超限统一回收
            HttpProcessor.StreamFile(ns, entry.FilePath, entry.FileName);
        }

        // ---------------- 杂项 ----------------

        /// <summary>设备 id → 显示名（查不到 null）。</summary>
        private static string? NameOf(string deviceId, TransferService svc)
        {
            if (deviceId == "deskkit") return TransferStore.DeskkitName;
            return svc.Store.SnapshotDevices().FirstOrDefault(d => d.Id == deviceId)?.Name; // T-M2：快照
        }

        /// <summary>目标合法性：deskkit 或已注册设备。</summary>
        private static bool IsValidTarget(string target, TransferService svc)
            => target == "deskkit" || svc.Store.SnapshotDevices().Any(d => d.Id == target); // T-M2：快照

        /// <summary>写 Mark-of-the-Web（NTFS ADS: Zone.Identifier, ZoneId=3 Internet）。
        /// 2026-11-13 T-M5：外部设备传来的文件打标，Windows 打开时 SmartScreen 会拦截确认。
        /// 仅 NTFS 生效，失败静默（FAT32/异常路径不影响收文件主流程）。</summary>
        private static void MarkOfWeb(string path)
        {
            try
            {
                File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            }
            catch (Exception ex)
            {
                MiniPix.Core.Log.Write($"lan upload MotW skip [{path}]: {ex.Message}");
            }
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }
}
