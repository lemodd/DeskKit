using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;

namespace DeskKit.Transfer
{
    /// <summary>
    /// 传输中心：设备表 + 会话存储（2026-11-10 网页改版为微信式聊天）。
    /// 线程模型：
    ///   · 内部字典/会话表由 _lock 保护，服务线程（HTTP）可安全调用全部方法；
    ///   · Devices 是 UI 线程的 ObservableCollection（构造时捕获 Dispatcher），
    ///     所有对它的增改都经 _ui.BeginInvoke marshal，可直接绑 ListBox；
    ///   · 心跳/会话清扫用 DispatcherTimer（10s），LastSeen 超 30s 置离线，
    ///     会话条目 24h TTL（中转临时文件一并删除）。
    /// </summary>
    public sealed class TransferStore
    {
        private const int MaxChatEntries = 100;                       // 每对设备保留条数
        private static readonly TimeSpan ChatTtl = TimeSpan.FromHours(24);

        private readonly object _lock = new();
        private readonly Dictionary<string, List<ChatEntry>> _chats = new(); // "a|b"(字典序) → 按时间序
        private readonly Dictionary<string, DeviceItem> _byId = new();     // deviceId → 条目（与 Devices 同步）
        private readonly System.Windows.Threading.DispatcherTimer? _sweep;
        private System.Windows.Threading.Dispatcher? _ui;

        /// <summary>设备表（UI 线程集合，直接绑定设备标签 ListBox）</summary>
        public ObservableCollection<DeviceItem> Devices { get; } = new();

        /// <summary>任何状态变化（设备增删/离线/队列变动）后触发，UI 用来刷空态显隐</summary>
        public event Action? Changed;

        public TransferStore()
        {
            // 构造发生在 UI 线程（App.OnStartup → TransferService.Start），Dispatcher 现取
            _ui = Application.Current?.Dispatcher;
            if (_ui == null)
                _ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;

            _sweep = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromSeconds(10), System.Windows.Threading.DispatcherPriority.Background,
                (_, _) => { SweepOffline(); SweepChats(); }, _ui);
            _sweep.Start();
        }

        // ---------------- 设备注册 / 心跳 ----------------

        /// <summary>注册或续约（/api/state 轮询即心跳）。name 传 null 表示只续约不改名。
        /// 同 IP 去重（2026-11-10 反馈：同一台电脑开两个浏览器显示两张卡）：id 不在表里但
        /// 同 IP 已有会话（换浏览器/清了 localStorage）时接管旧条目，一台机器只留一张卡。
        /// create=false（纯心跳）：id 未知且无同 IP 条目时**不建卡**（否则 register 一失败就冒出空名卡），
        /// 返回 Name=null，调用方让客户端重新走 /api/register。
        /// 返回（生效 id, 生效名）——接管时生效 id 是旧条目的 id，调用方须改用它轮询。</summary>
        public (string Id, string? Name) RegisterOrTouch(string deviceId, string? name, string? type, string ip,
            bool create = true, bool adoptIp = true)
        {
            DeviceItem? item;
            bool added = false, adopted = false;
            lock (_lock)
            {
                if (!_byId.TryGetValue(deviceId, out item))
                {
                    // 同 IP 接管（一 IP 一卡）：同 IP 已有卡则沿用旧条目（含名字），不新建
                    var old = adoptIp && !string.IsNullOrEmpty(ip)
                        ? _byId.Values.FirstOrDefault(d => d.Ip == ip)
                        : null;
                    if (old != null)
                    {
                        item = old;      // 接管：沿用旧条目（含名字），不新建
                        adopted = true;
                    }
                    else if (!create)
                    {
                        return (deviceId, null); // 未知设备来心跳：不建卡，让客户端重新注册
                    }
                    else
                    {
                        item = new DeviceItem(deviceId);
                        _byId[deviceId] = item;
                        added = true;
                    }
                }
                item.LastSeen = DateTime.UtcNow;
                if (!adopted && !string.IsNullOrWhiteSpace(name)) item.Name = name!; // 接管时保留旧名（可能被用户改过）
                if (!string.IsNullOrWhiteSpace(type)) item.Type = type!;
                if (!string.IsNullOrWhiteSpace(ip)) item.Ip = ip;
            }

            _ui!.BeginInvoke(() =>
            {
                if (added && !Devices.Contains(item)) Devices.Add(item);
                item.IsOnline = true;
                RaiseChanged();
            });
            return (item.Id, item.Name);
        }

        /// <summary>心跳清扫：30s 无心跳置灰；离线超 30 分钟整卡移除（否则换过浏览器/重装后灰卡越积越多）。</summary>
        private void SweepOffline()
        {
            List<DeviceItem> expired, gone;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                expired = _byId.Values
                    .Where(d => d.IsOnline && (now - d.LastSeen).TotalSeconds > 30)
                    .ToList();
                gone = _byId.Values
                    .Where(d => !d.IsOnline && (now - d.LastSeen).TotalMinutes > 30)
                    .ToList();
                foreach (var d in gone) _byId.Remove(d.Id);
            }
            if (expired.Count == 0 && gone.Count == 0) return;
            foreach (var d in expired) d.IsOnline = false;
            _ui!.BeginInvoke(() =>
            {
                foreach (var d in gone) Devices.Remove(d);
                RaiseChanged();
            });
        }

        // ---------------- 外部镜像设备（2026-11-12 LanClient：远端服务器及其它设备） ----------------

        private readonly List<DeviceItem> _external = new(); // 镜像条目（不进 _byId，免被清扫）

        /// <summary>镜像远端设备（LanClient 专用）：只进 Devices 集合、不进 _byId，
        /// 因此 SweepOffline/SweepChats 不会清扫它，在线状态由 LanClient 自己刷新。
        /// 返回条目供调用方改在线状态/状态文字。</summary>
        public DeviceItem UpsertExternal(string id, string name, string type)
        {
            lock (_lock)
            {
                var item = _external.FirstOrDefault(d => d.Id == id);
                if (item == null)
                {
                    item = new DeviceItem(id);
                    _external.Add(item);
                }
                item.Name = name;
                item.Type = type;
                _ui!.BeginInvoke(() =>
                {
                    if (!Devices.Contains(item)) Devices.Add(item);
                    RaiseChanged();
                });
                return item;
            }
        }

        /// <summary>移除镜像设备（断开/忘记服务器时）。</summary>
        public void RemoveExternal(string id)
        {
            DeviceItem? item;
            lock (_lock)
            {
                item = _external.FirstOrDefault(d => d.Id == id);
                if (item == null) return;
                _external.Remove(item);
            }
            _ui!.BeginInvoke(() =>
            {
                Devices.Remove(item);
                RaiseChanged();
            });
        }

        /// <summary>该 id 是否是 LanClient 镜像设备（发送拦截用）。</summary>
        public bool IsExternal(string id)
        {
            lock (_lock) return _external.Any(d => d.Id == id);
        }

        // ---------------- 会话（2026-11-10 微信式聊天） ----------------

        /// <summary>一对设备的会话键（两个 id 字典序拼接，与收发方向无关）。</summary>
        private static string PairKey(string a, string b)
            => string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;

        /// <summary>追加一条会话消息（文字/文件/图片统一入口）。
        /// 来自 DeskKit 的文件用原路径（DeleteFile=false）；浏览器中转的 .staged 用 DeleteFile=true。
        /// 返回条目（调用方需要它的 Id 作下载键）。</summary>
        public ChatEntry AddChat(string fromId, string fromName, string toId,
            string kind, string text, string fileName, long size, string filePath, bool deleteFile)
        {
            var e = new ChatEntry(Guid.NewGuid().ToString("N"), fromId, fromName, toId,
                kind, text, fileName, size, filePath, deleteFile);
            lock (_lock)
            {
                var key = PairKey(fromId, toId);
                if (!_chats.TryGetValue(key, out var list))
                    _chats[key] = list = new List<ChatEntry>();
                list.Add(e);
                // 超限淘汰最旧（中转临时文件一并删）
                while (list.Count > MaxChatEntries)
                {
                    var old = list[0];
                    list.RemoveAt(0);
                    DeleteChatFile(old);
                }
            }
            // 设备卡状态行：接收方是远端设备才提示（deskkit 端有气泡提示，不占状态行）
            if (toId != "deskkit")
                SetStatus(toId, kind == "text" ? $"收到来自 {fromName} 的消息" : $"收到来自 {fromName} 的文件");
            return e;
        }

        /// <summary>拉取与 peer 的会话（按时间升序，最近 MaxChatEntries 条）。返回副本。</summary>
        public List<ChatEntry> GetChat(string meId, string peerId)
        {
            lock (_lock)
                return _chats.TryGetValue(PairKey(meId, peerId), out var l)
                    ? l.ToList()
                    : new List<ChatEntry>();
        }

        /// <summary>标记我与 peer 的会话中发给我的消息为已读。</summary>
        public void MarkRead(string meId, string peerId)
        {
            lock (_lock)
            {
                if (!_chats.TryGetValue(PairKey(meId, peerId), out var l)) return;
                foreach (var e in l)
                    if (e.ToId == meId && !e.Read) e.Read = true;
            }
        }

        /// <summary>侧栏会话摘要（/api/state 用）：含 peer 的所有会话 → 最后一条预览 + 未读数。</summary>
        public List<ConvSummary> GetConvSummaries(string meId)
        {
            var res = new List<ConvSummary>();
            lock (_lock)
            {
                foreach (var kv in _chats)
                {
                    var sep = kv.Key.IndexOf('|');
                    var a = kv.Key[..sep];
                    var b = kv.Key[(sep + 1)..];
                    if (a != meId && b != meId) continue;
                    var peer = a == meId ? b : a;
                    var l = kv.Value;
                    if (l.Count == 0) continue;
                    var last = l[^1];
                    int unread = 0;
                    foreach (var e in l)
                        if (e.ToId == meId && !e.Read) unread++;
                    res.Add(new ConvSummary(peer, PreviewOf(last), last.Ts, unread));
                }
            }
            return res.OrderByDescending(s => s.Ts).ToList();
        }

        /// <summary>按 id 找会话条目（须是 meId 的会话参与者，防止跨会话拿别人的文件）。null=无。</summary>
        public ChatEntry? FindChatEntry(string entryId, string meId)
        {
            lock (_lock)
            {
                foreach (var kv in _chats)
                {
                    var sep = kv.Key.IndexOf('|');
                    if (kv.Key[..sep] != meId && kv.Key[(sep + 1)..] != meId) continue;
                    foreach (var e in kv.Value)
                        if (e.Id == entryId) return e;
                }
            }
            return null;
        }

        /// <summary>会话 TTL 清扫（10s 定时器，UI 线程）：超 24h 的条目移除，中转临时文件一并删。</summary>
        private void SweepChats()
        {
            List<ChatEntry> expired = new();
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                foreach (var kv in _chats)
                {
                    var l = kv.Value;
                    int cut = 0;
                    while (cut < l.Count && now - l[cut].Ts > ChatTtl) cut++;
                    if (cut == 0) continue;
                    expired.AddRange(l.Take(cut));
                    l.RemoveRange(0, cut);
                }
            }
            foreach (var e in expired) DeleteChatFile(e);
            if (expired.Count > 0)
                MiniPix.Core.Log.Write($"lan: chat sweep {expired.Count} entry(ies) expired");
        }

        private static void DeleteChatFile(ChatEntry e)
        {
            if (!e.DeleteFile || e.FilePath.Length == 0) return;
            try { File.Delete(e.FilePath); } catch { }
        }

        /// <summary>侧栏预览文本。</summary>
        private static string PreviewOf(ChatEntry e) => e.Kind switch
        {
            "image" => "[图片]" + (e.FileName.Length > 0 ? " " + e.FileName : ""),
            "file" => "[文件] " + e.FileName,
            _ => e.Text.Length > 40 ? e.Text[..40] + "…" : e.Text,
        };

        /// <summary>设备在线人数（托盘菜单文本用）</summary>
        public int OnlineCount
        {
            get { lock (_lock) return _byId.Values.Count(d => d.IsOnline); }
        }

        /// <summary>设备列表快照（2026-11-13 T-M2）：HTTP 线程一律用快照，
        /// 禁止直接枚举 UI 的 ObservableCollection（并发注册/超时清卡时枚举会抛
        /// InvalidOperationException 让请求 500/崩线程）。锁内拷贝，元素只读使用。</summary>
        public List<DeviceItem> SnapshotDevices()
        {
            lock (_lock)
            {
                var list = new List<DeviceItem>(_byId.Values.Count + _external.Count);
                list.AddRange(_byId.Values);
                list.AddRange(_external);
                return list;
            }
        }

        // ---------------- 杂项 ----------------

        /// <summary>DeskKit 本机在设备列表里的显示名（配置名兜底计算机名）</summary>
        public static string DeskkitName =>
            string.IsNullOrWhiteSpace(ShotShelf.AppConfig.Current.LanDeviceName)
                ? Environment.MachineName
                : ShotShelf.AppConfig.Current.LanDeviceName;

        private void SetStatus(string deviceId, string text)
        {
            DeviceItem? item;
            lock (_lock) _byId.TryGetValue(deviceId, out item);
            if (item == null) return;
            _ui!.BeginInvoke(() =>
            {
                item.StatusText = text;
                RaiseChanged();
            });
        }

        private void RaiseChanged()
        {
            try { Changed?.Invoke(); }
            catch { /* UI 回调异常不影响服务 */ }
        }
    }
}
