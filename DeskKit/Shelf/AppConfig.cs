using System;
using System.IO;
using System.Text.Json;

namespace ShotShelf
{
    /// <summary>
    /// 可调配置（S7 + 2026-10-26 扩展）：
    /// 文件位置 = exe 同目录 config.json；不存在则首次运行自动生成默认值。
    /// 用户可自行改这个文件后重启程序生效（自动保存目录也可在界面上改）。
    /// </summary>
    public class AppConfig
    {
        /// <summary>暂存上限（超出自动淘汰最旧的未固定项）</summary>
        public int MaxShots { get; set; } = 50;

        /// <summary>
        /// 缩略图最长边像素（列表里的卡片大小）。
        /// 2026-11-26：默认 160 → 280。卡片 115×111 DIP、图片铺满裁切，16:9 图在 160 时只有 90px 高，
        /// 显示时被放大 1.2 倍导致发虚；下限由 ShotStore.MinThumbSize（240）兜底。
        /// </summary>
        public int ThumbSize { get; set; } = 280;

        /// <summary>是否把图存到磁盘缓存目录（重启后暂存不丢的关键，建议开）</summary>
        public bool EnableDiskCache { get; set; } = true;

        /// <summary>
        /// 自动保存目录（2026-10-26 新增）：
        /// 非空时每张捕获的截图都自动另存一份 PNG 到这里（按时间命名，永不自动删除）。
        /// 空字符串 = 关闭自动保存。
        /// 文本条目同样会另存为 .txt。
        /// </summary>
        public string AutoSaveDir { get; set; } = "";

        /// <summary>
        /// 是否捕获剪贴板文本（2026-10-26 新增，默认开）：
        /// 关掉后只暂存图片，所有复制文字的剪贴板操作不再入库。
        /// </summary>
        public bool EnableTextCapture { get; set; } = true;

        /// <summary>
        /// 单条文本最大接收长度（2026-10-26 新增）：
        /// 超出部分直接截断（防止复制整本书/大段日志把缓存目录撑爆）。
        /// </summary>
        public int MaxTextLength { get; set; } = 200000;

        // ================= 2026-11-02 升级阶段3：窗口状态持久化 + 贴边自动隐藏 =================

        /// <summary>上次窗口左上角 X（DIP）。int.MinValue = 尚未保存过 → 用系统默认位置</summary>
        public int WindowLeft { get; set; } = int.MinValue;

        /// <summary>上次窗口左上角 Y（DIP）。int.MinValue = 尚未保存过</summary>
        public int WindowTop { get; set; } = int.MinValue;

        /// <summary>上次窗口宽度（DIP）。0 = 未保存 → 用 XAML 默认（980）</summary>
        public int WindowWidth { get; set; } = 0;

        /// <summary>上次窗口高度（DIP）。0 = 未保存（高度自适应模式下仅贴边时使用）</summary>
        public int WindowHeight { get; set; } = 0;

        /// <summary>贴边状态：None / Top / Right / Left（None = 未贴边）</summary>
        public string SnapEdge { get; set; } = "None";

        /// <summary>
        /// 预览窗口背景色（ARGB hex）（2026-11-02 升级阶段4；同日 UI 美化再调）：
        /// #2B2B2B → #808080 → 浅灰 #FFECEDEF（与浅色标题栏/底部栏同系）。
        /// </summary>
        public string PreviewBackgroundColor { get; set; } = "#FFECEDEF";

        /// <summary>
        /// 是否启用贴边自动隐藏（默认开）：
        /// 拖到屏幕上缘/右缘/左缘松手 → 贴边并滑出只留小边，鼠标靠近小边弹出。
        /// 关掉后拖到边缘不贴边（位置持久化不受影响）。
        /// </summary>
        public bool EnableEdgeSnap { get; set; } = true;

        // ================= 2026-11-05 设置持久化补漏：托盘/窗口级开关 =================

        /// <summary>Ctrl+G 对话框跳转开关（托盘菜单勾选项，2026-11-05 持久化：原来纯内存字段重启丢失）</summary>
        public bool QuickGEnabled { get; set; } = true;

        /// <summary>暂存架窗口置顶（右键菜单勾选项，2026-11-05 持久化：原来重启丢失）</summary>
        public bool ShelfTopmost { get; set; } = false;

        // ================= 2026-11-10 局域网文件传输 =================

        /// <summary>局域网传输服务开关（托盘菜单勾选项）。默认开：随 DeskKit 启动自动监听。</summary>
        public bool LanEnabled { get; set; } = true;

        /// <summary>传输服务监听端口（被占用时自动 +1 重试，实际端口以日志/托盘/设备标签显示为准）</summary>
        /// <summary>传输服务端口。0=自动：优先 80（网址免端口）→ 8080 → 52123..52125；>0=固定用该值（+1×4 兜底）</summary>
        public int LanPort { get; set; } = 0;

        /// <summary>本机在设备列表里显示的名字；空 = 用计算机名（Environment.MachineName）</summary>
        public string LanDeviceName { get; set; } = "";

        /// <summary>接收文件保存目录；空 = %LOCALAPPDATA%\DeskKit\received</summary>
        public string LanReceiveDir { get; set; } = "";

        // ================= 2026-11-12 设备互联：DeskKit 客户端（连其它电脑的 DeskKit，免配对） =================

        /// <summary>记住的服务器地址（http://ip:port）。空 = 未连接过。</summary>
        public string LanClientServerUrl { get; set; } = "";

        /// <summary>服务器分配的本机稳定设备 id（服务器重启 rebind 后重注册用）。</summary>
        public string LanClientDeviceId { get; set; } = "";

        /// <summary>服务器显示名（本机设备标签的"远程服务器"卡名）。</summary>
        public string LanClientServerName { get; set; } = "";

        /// <summary>已授权连接的设备 IP 表（2026-11-13 连接授权门：陌生 IP 首连弹窗，允许后记住不再问）。</summary>
        public List<string> LanApprovedIps { get; set; } = new();

        // ================= 2026-11-10 贴边参数可设置（右键"设置…"对话框） =================

        /// <summary>贴边隐藏时露出的小边宽度（DIP）。原固定 8，2026-11-10 反馈太宽 → 默认 4。</summary>
        public double PeekThickness { get; set; } = 4;

        /// <summary>松手时距屏幕边缘多少 DIP 内判为贴上（越大越容易贴）</summary>
        public double SnapDetectPx { get; set; } = 12;

        /// <summary>完整显示后鼠标离开多久滑回收起（ms）</summary>
        public int LeaveHideDelayMs { get; set; } = 600;

        /// <summary>松手贴边后多久自动滑入只留小边（ms）</summary>
        public int SnapInDelayMs { get; set; } = 600;

        /// <summary>滑出/滑回动画时长（ms）</summary>
        public int AnimMs { get; set; } = 600;

        // ================= 2026-11-10 外观可设置（设置窗口"外观"页） =================

        /// <summary>"图"标签颜色（hex）</summary>
        public string TabImageColor { get; set; } = "#F28C28";

        /// <summary>"文"标签颜色（hex）</summary>
        public string TabTextColor { get; set; } = "#1FA9A0";

        /// <summary>"设备"标签颜色（hex）</summary>
        public string TabDeviceColor { get; set; } = "#5C6BC0";

        /// <summary>窗口背景色（hex）</summary>
        public string ShelfBackgroundColor { get; set; } = "#F4F5F7";

        /// <summary>窗口背景跟随选中标签颜色（自动变浅，2026-11-10 反馈新增）：
        /// 开启后背景 = 当前标签色的浅色调（切图/文/设备标签时跟着变），忽略 ShelfBackgroundColor。</summary>
        public bool ShelfBgFollowTab { get; set; } = false;

        /// <summary>跟随标签时背景里标签色占比（%）：0=完全白，100=与标签同色。默认 12（很浅）。</summary>
        public int ShelfBgTintPct { get; set; } = 12;

        // ================= 2026-11-11 长截图自动滚动参数可设置（设置窗口"长截图"页） =================

        /// <summary>自动滚动每步步长（滚轮刻度数，1 刻度 ≈120px）。步长小=拼接重叠充足更稳，步长大=滚得快。</summary>
        public int LongScrollNotches { get; set; } = 2;

        /// <summary>自动滚动每步之后等待页面渲染稳定的时间（ms），越大越慢越稳。2026-11-11 反馈"慢一点"：默认从 300 提到 500。</summary>
        public int LongScrollDelayMs { get; set; } = 500;

        // ================= 2026-11-12 快捷键可设置（设置窗口"快捷键"页） =================
        // 存 Win32 虚拟键码 + RegisterHotKey 修饰键位（None=0 / Alt=1 / Ctrl=2 / Shift=4）。
        // 默认：截图 F1，弹出/收回暂存架 F2，Alt+1/2/3 切换 图/文/设备 标签。

        /// <summary>截图热键：虚拟键码（默认 F1=0x70）</summary>
        public int HotkeyCaptureVk { get; set; } = 0x70;

        /// <summary>截图热键：修饰键（默认无）</summary>
        public int HotkeyCaptureMods { get; set; } = 0;

        /// <summary>弹出/收回暂存架热键：虚拟键码（默认 F2=0x71）</summary>
        public int HotkeyShelfVk { get; set; } = 0x71;

        /// <summary>弹出/收回暂存架热键：修饰键（默认无）</summary>
        public int HotkeyShelfMods { get; set; } = 0;

        /// <summary>「图」标签热键：虚拟键码（默认 1=0x31）</summary>
        public int HotkeyTab1Vk { get; set; } = 0x31;

        /// <summary>「图」标签热键：修饰键（默认 Alt）</summary>
        public int HotkeyTab1Mods { get; set; } = 1;

        /// <summary>「文」标签热键：虚拟键码（默认 2=0x32）</summary>
        public int HotkeyTab2Vk { get; set; } = 0x32;

        /// <summary>「文」标签热键：修饰键（默认 Alt）</summary>
        public int HotkeyTab2Mods { get; set; } = 1;

        /// <summary>「设备」标签热键：虚拟键码（默认 3=0x33）</summary>
        public int HotkeyTab3Vk { get; set; } = 0x33;

        /// <summary>「设备」标签热键：修饰键（默认 Alt）</summary>
        public int HotkeyTab3Mods { get; set; } = 1;

        // ================= 2026-11-14 设置窗口新功能（级联露边/文字字号/贴图边框阴影） =================

        /// <summary>固定卡片级联叠放时被压卡片露出的左缘宽度（px）。原硬编码 13。</summary>
        public double CascadePeekPx { get; set; } = 13;

        /// <summary>文字卡片正文字号（px）。原硬编码 10.5。</summary>
        public double TextCardFontSize { get; set; } = 10.5;

        /// <summary>贴图边框颜色（hex）。默认与原硬编码一致 #FF9AA0A6。</summary>
        public string PinBorderColor { get; set; } = "#FF9AA0A6";

        /// <summary>贴图边框粗细（px，0=无边框）。原硬编码 1。</summary>
        public double PinBorderThickness { get; set; } = 1;

        /// <summary>是否启用贴图阴影（原恒开）。</summary>
        public bool PinShadowEnabled { get; set; } = true;

        /// <summary>贴图阴影模糊半径（px）。原硬编码 8。</summary>
        public double PinShadowBlur { get; set; } = 8;

        /// <summary>贴图阴影偏移深度（px）。原硬编码 2。</summary>
        public double PinShadowDepth { get; set; } = 2;

        /// <summary>贴图阴影浓度（%，0=透明 100=不透明）。原硬编码 40。</summary>
        public int PinShadowOpacityPct { get; set; } = 40;

        // ================= 读写 =================

        /// <summary>
        /// 全局共享配置实例（2026-11-05）：App（托盘开关）与 MainWindow（窗口状态）必须用同一份，
        /// 否则两边各自 Load+Save 会互相覆盖对方的修改。
        /// </summary>
        public static AppConfig Current { get; private set; } = Load();

        /// <summary>
        /// 配置文件路径。2026-11-06 从"exe 同目录"迁到 %LOCALAPPDATA%\DeskKit\：
        /// 之前放 exe 旁边，重发布/清空 publish 目录会把用户设置（窗口位置、开关）一起删掉。
        /// 与 shelf_cache、log.txt 同目录，程序文件与用户数据彻底分离。
        /// </summary>
        private static string ConfigPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskKit", "config.json");

        /// <summary>exe 同目录的旧配置（迁移源，2026-11-06 前的版本写这里）</summary>
        private static string LegacyConfigPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        /// <summary>加载配置；文件不存在/解析失败就用默认值</summary>
        public static AppConfig Load()
        {
            try
            {
                // 一次性迁移：新位置没有配置、exe 旁边有旧配置 → 拷过去（保住用户已有设置）
                if (!File.Exists(ConfigPath) && File.Exists(LegacyConfigPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                    File.Copy(LegacyConfigPath, ConfigPath);
                }

                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                    if (cfg != null)
                    {
                        // 回写一次：把版本升级后新增的配置项补进用户已有的 config.json
                        // （旧文件里没有 EnableTextCapture / MaxTextLength，用户就看不到这两个开关）
                        cfg.Save();
                        return cfg;
                    }
                }
            }
            catch (Exception ex)
            {
                // 2026-11-13 S-H2：坏文件先改名保留现场（.corrupt-时间戳，可人工抢救），
                // 再走默认值重建——直接覆盖会连"哪里坏了"的线索一起丢
                try
                {
                    if (File.Exists(ConfigPath))
                        File.Move(ConfigPath, ConfigPath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}");
                    MiniPix.Core.Log.Write($"config load failed, kept corrupt file: {ex.Message}");
                }
                catch { /* 连改名都失败就真没办法了，走默认值 */ }
            }

            var def = new AppConfig();
            def.Save(); // 首次运行生成模板文件
            return def;
        }

        /// <summary>保存当前配置（2026-11-13 S-H2 原子写：
        /// 先写 .tmp 再 File.Replace（自动留 .bak），断电/崩溃不会留下半截 JSON）</summary>
        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true,
                });
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                var tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(ConfigPath))
                    File.Replace(tmp, ConfigPath, ConfigPath + ".bak");
                else
                    File.Move(tmp, ConfigPath);
            }
            catch { /* 只读目录等，忽略 */ }
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
        };
    }
}
