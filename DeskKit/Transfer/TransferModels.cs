using System;
using System.ComponentModel;

namespace DeskKit.Transfer
{
    /// <summary>
    /// 已连接设备（2026-11-10 局域网文件传输）。
    /// Id = 浏览器注册时分配的 GUID（DeskKit 本机伪设备固定 "deskkit"）。
    /// Type = "deskkit" / "phone" / "pc"（浏览器注册时由 UA 前端判别上报）。
    /// 设备表是 UI 线程创建的 ObservableCollection，属性变更走 INotifyPropertyChanged。
    /// </summary>
    public sealed class DeviceItem : INotifyPropertyChanged
    {
        private string _name = "";
        private string _ip = "";
        private string _type = "pc";
        private bool _isOnline;
        private string _statusText = "";

        public DeviceItem(string id) => Id = id;

        public string Id { get; }

        /// <summary>设备显示名（浏览器注册名 / DeskKit 本机名）</summary>
        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; Raise(nameof(Name)); } }
        }

        /// <summary>最近一次连接的来源 IP</summary>
        public string Ip
        {
            get => _ip;
            set { if (_ip != value) { _ip = value; Raise(nameof(Ip)); } }
        }

        /// <summary>deskkit / phone / pc</summary>
        public string Type
        {
            get => _type;
            set { if (_type != value) { _type = value; Raise(nameof(Type)); } }
        }

        /// <summary>在线状态（30 秒无心跳置灰，不删条目，重连即复用）</summary>
        public bool IsOnline
        {
            get => _isOnline;
            set { if (_isOnline != value) { _isOnline = value; Raise(nameof(IsOnline)); } }
        }

        /// <summary>卡片底部状态文字（"已发送 2 个，等待取走" / "已取走" 等）</summary>
        public string StatusText
        {
            get => _statusText;
            set { if (_statusText != value) { _statusText = value; Raise(nameof(StatusText)); } }
        }

        /// <summary>最近一次心跳时间（非绑定属性，线程安全由 TransferStore 锁保证）</summary>
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 会话消息条目（2026-11-10 网页改版为微信式聊天）。
    /// 一条消息存在"一对设备"的会话里（PairKey = 两个 id 排序后拼接），对象共享；
    /// Read 标记只看接收方（发送方自己的消息天然已读），所以一份对象两方通用。
    /// FilePath 两种来源：
    ///   ① DeskKit 端发送 = 原文件路径，DeleteFile=false（不动用户文件）；
    ///   ② 浏览器上传中转 = TempDir 里的 .staged 文件，DeleteFile=true（条目过期/挤掉时删除）。
    /// 内存态：DeskKit 重启即清空（传输工具取舍）；每对上限 100 条 + 24h TTL。
    /// </summary>
    public sealed class ChatEntry
    {
        public ChatEntry(string id, string fromId, string fromName, string toId,
            string kind, string text, string fileName, long size, string filePath, bool deleteFile)
        {
            Id = id; FromId = fromId; FromName = fromName; ToId = toId;
            Kind = kind; Text = text; FileName = fileName; Size = size;
            FilePath = filePath; DeleteFile = deleteFile;
        }

        public string Id { get; }
        /// <summary>发送方设备 id（"deskkit" = 电脑）</summary>
        public string FromId { get; }
        /// <summary>发送方显示名（冗余存一份，设备可能离线后被清扫）</summary>
        public string FromName { get; }
        /// <summary>接收方设备 id</summary>
        public string ToId { get; }
        /// <summary>text / file / image</summary>
        public string Kind { get; }
        /// <summary>文字内容（kind=text）</summary>
        public string Text { get; }
        /// <summary>文件/图片显示名</summary>
        public string FileName { get; }
        public long Size { get; }
        /// <summary>服务端文件路径（text 为空串）</summary>
        public string FilePath { get; }
        /// <summary>true = 中转临时文件，条目移除时一起删</summary>
        public bool DeleteFile { get; }
        /// <summary>接收方已读（/api/read 标记）</summary>
        public bool Read { get; set; }
        public DateTime Ts { get; } = DateTime.UtcNow;
    }

    /// <summary>会话列表摘要（侧栏条目）：最后一条预览 + 未读数。</summary>
    public sealed record ConvSummary(string Peer, string Last, DateTime Ts, int Unread);
}
