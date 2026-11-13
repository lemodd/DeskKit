using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeskKit.Transfer
{
    /// <summary>
    /// DeskKit↔DeskKit 客户端（2026-11-12 设备互联，免配对版）：
    /// 以"桌面浏览器"身份接入另一台 DeskKit 的传输服务器（复用 /api/* 端点，无 token/配对码，
    /// 填网址即连）。设备 id 首次注册时由服务器分配并持久化，之后一直复用。
    /// 状态机：Idle → Connecting → Connected ⇄ Retrying（指数退避 2/4/8…30s）。
    /// 主循环 1s 一拍：/api/state 心跳（rebind 自动重注册）+ 设备表镜像（TransferStore.UpsertExternal，
    /// 设备标签自动显示"远程服务器"卡和对方的其它设备）+ 逐 peer 拉会话增量；
    /// 收到文字/图片复用 ReceiveMessage/NotifyImageReceived 链路自动进暂存架。
    /// 发送：TransferService.SendTextTo/SendFilesTo 拦截镜像 id 转发到这里 → POST /api/msg、/api/upload。
    /// </summary>
    public sealed class LanClient : IDisposable
    {
        public static LanClient? Instance { get; private set; }

        /// <summary>取实例，没有则创建（设置窗第一次"连接"时调用）。</summary>
        public static LanClient GetOrCreate() => Instance ??= new LanClient();

        /// <summary>服务器在本机设备表里的镜像卡 id（对应服务器的 "deskkit" 伪设备）。</summary>
        public const string RemoteDeviceId = "lanclient";

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1); // 与网页轮询一致

        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
        // 2026-11-13 修复：声明即初始化。StartAuto（开机自动重连）不经 StopLoop 直入 StartLoop，
        // 旧实现 _cts 为 null，StartLoop 首行 _cts!.Token 抛 NRE 且被 Task.Run 吞掉——自动重连从未生效过
        private CancellationTokenSource _cts = new();
        private readonly object _stateLock = new();

        // ---- 连接态（_stateLock 保护） ----
        private string _status = "未连接";          // 设置窗/设备卡显示
        private string _state = "Idle";             // Idle/Connecting/Connected/Retrying

        // ---- 服务器侧凭据（运行时镜像 config） ----
        private string _baseUrl = "";               // http://ip:port（无尾斜杠）
        private string _myId = "";                  // 服务器分配的稳定 id（首次注册分配，持久化复用）
        private string _serverName = "";

        // ---- 消息去重 ----
        private readonly HashSet<string> _seenIds = new();       // 服务器端条目 id
        private readonly List<(string Peer, string Kind, string Text, string FileName, long Size)> _pendingSends = new();
        private readonly object _dedupLock = new();
        private readonly HashSet<string> _mirrorIds = new();     // 本机镜像卡 id（断开时统一移除）

        /// <summary>连接状态变化（UI 线程触发；设置窗/设备卡订阅刷新）。</summary>
        public event Action? StateChanged;

        /// <summary>任意 LanClient 实例的状态变化中继（静态，设置窗订阅用；单例场景等价于 StateChanged）。</summary>
        public static event Action? ClientChanged;

        public bool IsConnected { get { lock (_stateLock) return _state == "Connected"; } }
        public string StatusText { get { lock (_stateLock) return _status; } }
        public string ServerName { get { lock (_stateLock) return _serverName; } }

        /// <summary>该 id 是否是本机镜像的远端设备（发送拦截用）。</summary>
        public bool IsMirror(string localId)
        {
            lock (_dedupLock) return _mirrorIds.Contains(localId);
        }

        private void SetState(string state, string status)
        {
            bool changed;
            lock (_stateLock)
            {
                changed = _state != state || _status != status;
                _state = state;
                _status = status;
            }
            if (changed)
            {
                MiniPix.Core.Log.Write($"lanclient: {state} - {status}");
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    try { StateChanged?.Invoke(); } catch { }
                    try { ClientChanged?.Invoke(); } catch { }
                });
            }
        }

        // ---------------- 生命周期 ----------------

        /// <summary>App.OnStartup 调用：config 里有服务器地址才自动重连（没有则保持 Idle）。</summary>
        public static void StartAuto()
        {
            var cfg = ShotShelf.AppConfig.Current;
            if (string.IsNullOrWhiteSpace(cfg.LanClientServerUrl))
                return;

            var c = new LanClient
            {
                _baseUrl = cfg.LanClientServerUrl.TrimEnd('/'),
                _myId = cfg.LanClientDeviceId,
                _serverName = string.IsNullOrWhiteSpace(cfg.LanClientServerName)
                    ? "远程服务器" : cfg.LanClientServerName,
            };
            Instance = c;
            c.StartLoop(auto: true);
            MiniPix.Core.Log.Write($"lanclient: auto-reconnect to {c._baseUrl}");
        }

        /// <summary>设置窗"连接"时调用：用已保存的地址直接重连。没有已保存地址返回 false。</summary>
        public static bool ReconnectFromConfig()
        {
            var cfg = ShotShelf.AppConfig.Current;
            if (string.IsNullOrWhiteSpace(cfg.LanClientServerUrl)) return false;
            if (Instance != null)
            {
                Instance.Disconnect(forgetServer: false); // 停掉旧循环
                Instance.Dispose(); // 2026-11-13：旧实例 HttpClient 一并释放（重连泄漏 socket/句柄）
                Instance = null;
            }
            StartAuto();
            return true;
        }

        /// <summary>设置窗"连接"：注册接入 → 进主循环。状态经 StatusText/StateChanged 反馈。</summary>
        public void Connect(string baseUrl)
        {
            // 重复连接先断旧会话
            StopLoop();

            var url = baseUrl.Trim().TrimEnd('/');
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "http://" + url;
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
                url = $"{u.Scheme}://{u.Host}:{u.Port}";
            _baseUrl = url;
            _myId = ShotShelf.AppConfig.Current.LanClientDeviceId; // 上次注册分配的 id（可能为空）

            SetState("Connecting", "正在连接…");
            Task.Run(async () =>
            {
                try
                {
                    // 注册接入（无配对码）：服务器返回稳定 id（同 IP 已有卡时接管该卡的 id）
                    var resp = await PostJsonAsync(
                        $"{_baseUrl}/api/register?id={Uri.EscapeDataString(_myId)}",
                        JsonSerializer.Serialize(new { name = TransferStore.DeskkitName, kind = "deskkit" }));
                    if (resp == null)
                    {
                        SetState("Connecting", "无法连接服务器，请检查地址");
                        ResetToIdle();
                        return;
                    }
                    string id;
                    using (var doc = JsonDocument.Parse(resp))
                    {
                        if (!doc.RootElement.TryGetProperty("id", out var idEl))
                        {
                            var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
                            SetState("Connecting", string.IsNullOrEmpty(err) ? "连接失败" : "连接失败：" + err);
                            ResetToIdle();
                            return;
                        }
                        id = idEl.GetString() ?? "";
                    }
                    if (id == "deskkit")
                    {
                        // 指向了本机自己的服务（本机浏览器身份）——连接自己没有意义
                        SetState("Connecting", "这是本机的服务地址，无需连接");
                        ResetToIdle();
                        return;
                    }
                    _myId = id;

                    // 持久化（重启自动重连）
                    var cfg = ShotShelf.AppConfig.Current;
                    cfg.LanClientServerUrl = _baseUrl;
                    cfg.LanClientDeviceId = _myId;
                    cfg.Save();
                    MiniPix.Core.Log.Write($"lanclient: connected to {_baseUrl} id={_myId}");

                    StartLoop(auto: false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    MiniPix.Core.Log.Write("lanclient: connect failed: " + ex.Message);
                    SetState("Connecting", "连接失败：" + ex.Message);
                    ResetToIdle();
                }
            });
        }

        /// <summary>断开连接。forgetServer=true 同时清除记住的服务器。</summary>
        public void Disconnect(bool forgetServer)
        {
            StopLoop();
            if (forgetServer)
            {
                var cfg = ShotShelf.AppConfig.Current;
                cfg.LanClientServerUrl = "";
                cfg.LanClientDeviceId = "";
                cfg.LanClientServerName = "";
                cfg.Save();
            }
            SetState("Idle", forgetServer ? "已断开并忘记服务器" : "已断开");
        }

        public void Dispose()
        {
            StopLoop();
            _http.Dispose();
        }

        private void ResetToIdle() { /* 状态已由 SetState 表达，保留占位以便扩展 */ }

        // ---------------- 主循环 ----------------

        private void StopLoop()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = new CancellationTokenSource();
            RemoveAllMirrors(); // 清掉"远程服务器"与 peers 镜像卡（重连会重建）
        }

        /// <summary>进入轮询主循环（后台线程）。auto=true = 启动自动重连；false = 刚连接成功。</summary>
        private void StartLoop(bool auto)
        {
            Task.Run(async () =>
            {
                var ct = _cts.Token;
                int fail = auto ? 1 : 0;
                int backoffMs = 0;
                SetState("Retrying", auto ? "正在连接服务器…" : "已连接，同步中…");

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var ok = await PollOnce(ct);
                        if (ok)
                        {
                            fail = 0;
                            backoffMs = 0;
                            if (StatusText != "已连接")
                                SetState("Connected", "已连接");
                        }
                        else
                        {
                            // rebind 已在 PollOnce 内处理；这里按失败计
                            fail++;
                        }
                        await Task.Delay(PollInterval, ct);
                        // 失败退避（成功后不加）
                        if (fail > 0)
                        {
                            backoffMs = Math.Min((int)Math.Pow(2, Math.Min(fail, 5)) * 1000, 30_000);
                            SetState("Retrying", $"连接中断，{backoffMs / 1000} 秒后重试（第 {fail} 次）");
                            MarkMirrorsOffline();
                            await Task.Delay(backoffMs, ct);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        fail++;
                        MiniPix.Core.Log.Write($"lanclient: poll error: {ex.Message}");
                        SetState("Retrying", $"连接中断，稍后重试（第 {fail} 次）");
                        MarkMirrorsOffline();
                        try { await Task.Delay(Math.Min((int)Math.Pow(2, Math.Min(fail, 5)) * 1000, 30_000), ct); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }, _cts.Token);
        }

        /// <summary>一拍：心跳 + 设备表镜像 + 会话增量。返回 false = 需按失败处理（rebind 已内部消化）。</summary>
        private async Task<bool> PollOnce(CancellationToken ct)
        {
            var resp = await _http.GetStringAsync(
                $"{_baseUrl}/api/state?id={Uri.EscapeDataString(_myId)}", ct);
            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;

            // 服务器重启清内存 → 重新注册（id 复用）
            if (root.TryGetProperty("rebind", out var rb) && rb.GetBoolean())
            {
                MiniPix.Core.Log.Write("lanclient: server rebind, re-registering");
                await RegisterAsync(ct);
                return false; // 下一拍拿全量
            }

            // ---- 设备表镜像 ----
            var serverRenamed = false;
            if (root.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array)
            {
                // 镜像清漏：远端列表里消失的卡（如同 IP 归并被并掉的）要同步移除本地镜像
                List<string> prev;
                lock (_dedupLock) prev = _mirrorIds.ToList();
                var current = new HashSet<string>();
                foreach (var d in devices.EnumerateArray())
                {
                    var id = d.GetProperty("id").GetString() ?? "";
                    if (id == _myId) continue; // 自己（服务器侧的卡）不镜像
                    var name = d.GetProperty("name").GetString() ?? "";
                    var type = d.GetProperty("type").GetString() ?? "pc";
                    var online = d.TryGetProperty("online", out var on) && on.GetBoolean();

                    if (id == "deskkit")
                    {
                        // 服务器本机：改名时同步"远程服务器"卡名
                        if (name != _serverName)
                        {
                            _serverName = name;
                            var cfg = ShotShelf.AppConfig.Current;
                            cfg.LanClientServerName = name;
                            cfg.Save();
                        }
                        serverRenamed = true;
                        var card = MirrorServerCard();
                        if (card != null) card.IsOnline = online;
                        current.Add(RemoteDeviceId);
                        continue;
                    }
                    var item = TransferService.Instance?.Store.UpsertExternal(id, name, type);
                    if (item != null) item.IsOnline = online;
                    current.Add(id);
                }
                foreach (var gone in prev)
                    if (!current.Contains(gone))
                    {
                        try { TransferService.Instance?.Store.RemoveExternal(gone); } catch { }
                        lock (_dedupLock) _mirrorIds.Remove(gone);
                    }
                lock (_dedupLock) foreach (var id in current) _mirrorIds.Add(id); // 本轮在册（HashSet 去重）
            }
            var server = MirrorServerCard();
            if (server != null)
            {
                server.IsOnline = true;
                if (serverRenamed) server.Name = _serverName;
            }

            // ---- 会话增量（摘要 → 逐 peer 拉新条目） ----
            if (root.TryGetProperty("chats", out var chats) && chats.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in chats.EnumerateArray())
                {
                    var peer = c.GetProperty("peer").GetString() ?? "";
                    var unread = c.TryGetProperty("unread", out var u) ? u.GetInt32() : 0;
                    var peerLocal = MapRemoteToLocal(peer);
                    await PullPeer(peer, peerLocal, ct);
                    if (unread > 0)
                    {
                        try
                        {
                            await _http.PostAsync($"{_baseUrl}/api/read?id={_myId}&peer={peer}",
                                new ByteArrayContent([]), ct); // 同步已读，服务器端未读数不消不掉
                        }
                        catch { }
                    }
                }
            }
            return true;
        }

        /// <summary>向服务器注册接入（连接 / rebind 共用）。同 IP 已有卡时接管其 id，本地同步更新。</summary>
        private async Task RegisterAsync(CancellationToken ct)
        {
            var resp = await PostJsonAsync(
                $"{_baseUrl}/api/register?id={Uri.EscapeDataString(_myId)}",
                JsonSerializer.Serialize(new { name = TransferStore.DeskkitName, kind = "deskkit" }));
            if (resp == null) return;
            try
            {
                using var doc = JsonDocument.Parse(resp);
                var id = doc.RootElement.GetProperty("id").GetString() ?? "";
                if (id.Length > 0 && id != "deskkit" && id != _myId)
                {
                    _myId = id; // 同 IP 接管：换用既有卡的 id，立即持久化
                    var cfg = ShotShelf.AppConfig.Current;
                    cfg.LanClientServerUrl = _baseUrl;
                    cfg.LanClientDeviceId = _myId;
                    cfg.Save();
                }
            }
            catch { }
        }

        /// <summary>确保"远程服务器"卡存在并返回（镜像到本机设备标签）。
        /// 本机局域网服务关闭（TransferService.Instance=null）时返回 null：无设备标签可挂，仅收发照常。</summary>
        private DeviceItem? MirrorServerCard()
        {
            lock (_dedupLock) _mirrorIds.Add(RemoteDeviceId);
            return TransferService.Instance?.Store.UpsertExternal(RemoteDeviceId, _serverName, "deskkit");
        }

        private void MarkMirrorsOffline()
        {
            try
            {
                foreach (var id in _mirrorIds.ToList())
                {
                    var item = TransferService.Instance?.Store.Devices.FirstOrDefault(d => d.Id == id);
                    if (item != null) item.IsOnline = false;
                }
            }
            catch { }
        }

        /// <summary>移除全部镜像设备卡（断开/重连前清场）。</summary>
        private void RemoveAllMirrors()
        {
            List<string> ids;
            lock (_dedupLock)
            {
                ids = _mirrorIds.ToList();
                _mirrorIds.Clear();
            }
            var store = TransferService.Instance?.Store;
            if (store == null) return;
            foreach (var id in ids)
            {
                try { store.RemoveExternal(id); } catch { }
            }
        }

        /// <summary>服务器侧 id → 本机镜像 id（服务器伪设备 deskkit → lanclient）。</summary>
        private static string MapRemoteToLocal(string serverId)
            => serverId == "deskkit" ? RemoteDeviceId : serverId;

        /// <summary>本机镜像 id → 服务器侧 target（lanclient → deskkit）。</summary>
        private static string MapLocalToRemote(string localId)
            => localId == RemoteDeviceId ? "deskkit" : localId;

        // ---------------- 收消息 ----------------

        /// <summary>拉取与 peer 的会话并处理增量条目。</summary>
        private async Task PullPeer(string peerServerId, string peerLocalId, CancellationToken ct)
        {
            var resp = await _http.GetStringAsync(
                $"{_baseUrl}/api/chat?id={_myId}&peer={peerServerId}", ct);
            using var doc = JsonDocument.Parse(resp);
            if (!doc.RootElement.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array) return;

            foreach (var e in entries.EnumerateArray())
            {
                var entryId = e.GetProperty("id").GetString() ?? "";
                lock (_dedupLock)
                    if (!_seenIds.Add(entryId)) continue;
                ProcessEntry(e, peerLocalId, entryId);
            }
            // 防集合无限增长
            lock (_dedupLock)
                if (_seenIds.Count > 4000) _seenIds.Clear();
        }

        private void ProcessEntry(JsonElement e, string peerLocalId, string entryId)
        {
            bool mine = e.TryGetProperty("mine", out var m) && m.GetBoolean();
            var fromId = e.GetProperty("from").GetString() ?? "";
            var fromName = e.GetProperty("fromName").GetString() ?? "";
            var kind = e.GetProperty("kind").GetString() ?? "text";
            var text = e.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            var fileName = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var size = e.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            var svc = TransferService.Instance;

            if (mine)
            {
                // 自己发的回显：与发送时的待确认签名比对，命中只记 seen 不重复镜像
                lock (_dedupLock)
                {
                    var idx = _pendingSends.FindIndex(p =>
                        p.Peer == peerLocalId && p.Kind == kind && p.Text == text &&
                        p.FileName == fileName && p.Size == size);
                    if (idx >= 0) { _pendingSends.RemoveAt(idx); return; }
                }
                // 未命中（理论少见）：补镜像为本机发出的消息
                svc?.Store.AddChat("deskkit", TransferStore.DeskkitName, peerLocalId,
                    kind, text, fileName, size, "", deleteFile: false);
                return;
            }

            // ---- 收到对方消息 ----
            var localFromId = MapRemoteToLocal(fromId);
            switch (kind)
            {
                case "text":
                    // 镜像会话（聊天窗可回看）+ 复用接收链路（气泡 + 自动进"文"标签）
                    svc?.Store.AddChat(localFromId, fromName, "deskkit", "text",
                        text, "", 0, "", deleteFile: false);
                    svc?.ReceiveMessage(text, fromName);
                    break;

                case "image":
                    // 下载临时 png → 复用主窗入库链路（进"图"标签，主窗自己补写镜像会话）
                    var imgPath = Path.Combine(TransferService.TempDir,
                        Guid.NewGuid().ToString("N") + Path.GetExtension(fileName));
                    if (DownloadToFile($"/api/file/{entryId}", imgPath))
                    {
                        if (TransferService.ImageHasListener)
                        {
                            svc?.NotifyImageReceived(new TransferService.LanImageReceived(
                                imgPath, localFromId, fromName, fileName, size));
                            // 主窗 OnLanImageReceived 会 AddChat 镜像（用暂存架缓存 png）
                        }
                        else
                        {
                            // 无主窗（少见）：落接收目录 + 手动镜像
                            try
                            {
                                string final;
                                using (var fs = File.OpenRead(imgPath))
                                    final = svc!.SaveReceived(fileName, fs, fromName);
                                File.Delete(imgPath);
                                svc.Store.AddChat(localFromId, fromName, "deskkit", "image",
                                    "", fileName, size, final, deleteFile: false);
                            }
                            catch { try { File.Delete(imgPath); } catch { } }
                        }
                    }
                    else
                    {
                        lock (_dedupLock) _seenIds.Remove(entryId); // 下载失败下轮重试
                    }
                    break;

                default: // file
                    try
                    {
                        string final;
                        using (var resp2 = _http.GetStreamAsync(
                            $"{_baseUrl}/api/file/{entryId}?id={_myId}").Result)
                        using (var ms = new MemoryStream())
                        {
                            resp2.CopyTo(ms); // SaveReceived 需要可重读流
                            ms.Position = 0;
                            final = svc!.SaveReceived(fileName, ms, fromName);
                        }
                        svc?.Store.AddChat(localFromId, fromName, "deskkit", "file",
                            "", fileName, size, final, deleteFile: false);
                    }
                    catch (Exception ex)
                    {
                        MiniPix.Core.Log.Write($"lanclient: file download fail [{fileName}]: {ex.Message}");
                        lock (_dedupLock) _seenIds.Remove(entryId);
                    }
                    break;
            }
        }

        private bool DownloadToFile(string apiPath, string destPath)
        {
            try
            {
                using var stream = _http.GetStreamAsync(
                    $"{_baseUrl}{apiPath}?id={_myId}").Result;
                using var fs = File.Create(destPath);
                stream.CopyTo(fs, 64 * 1024);
                return true;
            }
            catch (Exception ex)
            {
                MiniPix.Core.Log.Write($"lanclient: download fail {apiPath}: {ex.Message}");
                try { File.Delete(destPath); } catch { }
                return false;
            }
        }

        // ---------------- 发消息（TransferService 拦截转发到这里） ----------------

        /// <summary>发文字到镜像设备（peerLocalId：lanclient=服务器 / 服务器侧设备 GUID）。</summary>
        public void SendText(string peerLocalId, string text)
        {
            if (!IsConnected) { try { global::DeskKit.App.Notifier("未连接远程服务器，无法发送"); } catch { } return; }
            text = text.Trim();
            var target = MapLocalToRemote(peerLocalId);
            // 先记待确认签名（防回显重复镜像），再发
            lock (_dedupLock) _pendingSends.Add((peerLocalId, "text", text, "", 0));
            Task.Run(async () =>
            {
                try
                {
                    var resp = await PostJsonAsync($"{_baseUrl}/api/msg",
                        JsonSerializer.Serialize(new { from = _myId, target, text }));
                    if (resp == null) throw new Exception("发送失败");
                    TransferService.Instance?.Store.AddChat("deskkit", TransferStore.DeskkitName,
                        peerLocalId, "text", text, "", 0, "", deleteFile: false);
                }
                catch (Exception ex)
                {
                    lock (_dedupLock)
                        _pendingSends.RemoveAll(p => p.Peer == peerLocalId && p.Kind == "text" && p.Text == text);
                    MiniPix.Core.Log.Write($"lanclient: send text fail: {ex.Message}");
                    try { global::DeskKit.App.Notifier("发送失败：" + ex.Message); } catch { }
                }
            });
        }

        /// <summary>发文件/图片到镜像设备（multipart 上传，与网页同端点）。</summary>
        public void SendFiles(string peerLocalId, string[] paths, bool deleteFile = false)
        {
            if (!IsConnected) { try { global::DeskKit.App.Notifier("未连接远程服务器，无法发送"); } catch { } return; }
            var target = MapLocalToRemote(peerLocalId);
            Task.Run(async () =>
            {
                int n = 0;
                foreach (var p in paths)
                {
                    try
                    {
                        if (!File.Exists(p)) continue;
                        var fi = new FileInfo(p);
                        var kind = TransferService.IsImageName(fi.Name) ? "image" : "file";
                        lock (_dedupLock) _pendingSends.Add((peerLocalId, kind, "", fi.Name, fi.Length));

                        using var content = new MultipartFormDataContent();
                        content.Add(new StringContent(_myId), "from");
                        content.Add(new StringContent(target), "target");
                        await using (var fs = File.OpenRead(p))
                        {
                            var fileContent = new StreamContent(fs);
                            fileContent.Headers.ContentType = new("application/octet-stream");
                            content.Add(fileContent, "file", fi.Name);
                            var resp = await _http.PostAsync($"{_baseUrl}/api/upload", content);
                            resp.EnsureSuccessStatusCode();
                        }
                        TransferService.Instance?.Store.AddChat("deskkit", TransferStore.DeskkitName,
                            peerLocalId, kind, "", fi.Name, fi.Length, p, deleteFile: deleteFile);
                        n++;
                    }
                    catch (Exception ex)
                    {
                        lock (_dedupLock)
                            _pendingSends.RemoveAll(x => x.Peer == peerLocalId && x.FileName == Path.GetFileName(p));
                        MiniPix.Core.Log.Write($"lanclient: send file fail [{p}]: {ex.Message}");
                        try { global::DeskKit.App.Notifier("文件发送失败：" + Path.GetFileName(p)); } catch { }
                    }
                }
                if (n > 0)
                    MiniPix.Core.Log.Write($"lanclient: sent {n} file(s) to {peerLocalId}");
            });
        }

        /// <summary>POST JSON 小请求（带 Content-Type）。失败返回 null。</summary>
        private async Task<string?> PostJsonAsync(string url, string json)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await _http.PostAsync(url, content);
                return await resp.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                MiniPix.Core.Log.Write($"lanclient: post {url} failed: {ex.Message}");
                return null;
            }
        }
    }
}
