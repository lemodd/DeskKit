using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;             // 钩子回调里排队用（COM 不能在输入同步呼叫内发起）
using System.Windows.Media.Imaging;

namespace CtrlG
{
    /// <summary>
    /// 粘贴即存文件（2026-11-04 方案B，11:10 修订为"直接写盘"主路径）：
    /// 在资源管理器文件列表里按 Ctrl+V，把剪贴板里的纯图片/纯文字保存为文件。
    ///
    /// 【主路径·直接写盘】拦截 Ctrl+V 后：
    ///   1) 用 ShellCom/UIA 读取前台资源管理器的当前目录（复用 QuickG 现成能力）
    ///   2) 从剪贴板取内容（图片→PNG 字节，文字→UTF-8），直接 File.WriteAllBytes 到当前目录
    ///   3) 剪贴板内容完全不动，按键照常放行
    /// 首版曾用"虚拟文件格式"(FileGroupDescriptorW+FileContents) 转换剪贴板让 Explorer 粘贴，
    /// 实测转换成功但 Explorer 不认裸 HGLOBAL 虚拟文件（日志 11:19:02/11:19:22 两次转换成功、
    /// 粘贴无文件），故降级为兜底：仅当目录取不到时才走虚拟文件转换（万一 Explorer 认呢）。
    ///
    /// 安全设计：
    ///   - 只在 Explorer 文件列表焦点时触发（焦点在搜索框/地址栏的 Edit 控件则放行原样粘贴）
    ///   - 剪贴板已是文件(CF_HDROP/虚拟文件)时不干预
    ///   - 全程 try/catch + 500ms 限时，异常/超时一律放行原始按键，绝不卡输入
    ///   - 钩子装在 WPF UI 线程（自带消息泵），Stop() 在退出时摘除
    /// </summary>
    internal static class PasteAsFile
    {
        // ================= Win32 常量 =================
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int GA_PARENT = 1;
        private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_V = 0x56;
        private const uint GMEM_MOVEABLE = 0x0002;

        // 标准剪贴板格式号
        private const uint CF_UNICODETEXT = 13;
        private const uint CF_DIB = 8;
        private const uint CF_DIBV5 = 17;
        private const uint CF_HDROP = 15;

        // 注册格式（静态构造时注册一次）
        private static readonly uint _fmtFGD = RegisterClipboardFormat("FileGroupDescriptorW");
        private static readonly uint _fmtFC = RegisterClipboardFormat("FileContents");
        private static readonly uint _fmtPng = RegisterClipboardFormat("PNG");
        private static readonly uint _fmtDropEffect = RegisterClipboardFormat("Preferred DropEffect");

        private static readonly uint _selfPid = (uint)Environment.ProcessId;

        private static IntPtr _hook;
        private static readonly LowLevelKeyboardProc _proc = HookProc;   // 保存委托防 GC
        private static string _lastName = "";                            // 同一秒重名检测
        private static int _seq;                                         // 重名递增序号

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        // ================= P/Invoke =================

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern ushort GetAsyncKeyState(ushort vKey);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public uint cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public int rcCaretL, rcCaretT, rcCaretR, rcCaretB;
        }

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        private static extern uint EnumClipboardFormats(uint format);

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern UIntPtr GlobalSize(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterClipboardFormat(string lpszFormat);

        // ---- 2026-11-04 保存后可见反馈：刷新目录视图 + 选中文件 ----
        private const int SHCNE_UPDATEDIR = 0x00001000;
        private const uint SHCNF_PATHW = 0x0005;

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        /// <summary>通知 shell 目录内容已变化（外部写盘后 Explorer 视图不刷新时强制刷新）</summary>
        private static void NotifyFolderRefresh(string dir)
        {
            try
            {
                IntPtr p = Marshal.StringToHGlobalUni(dir);
                try { SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, p, IntPtr.Zero); }
                finally { Marshal.FreeHGlobal(p); }
            }
            catch { }
        }

        // ================= 生命周期 =================

        /// <summary>装低级键盘钩子（须在带消息泵的线程调用；App.OnStartup 的 UI 线程满足）</summary>
        public static void Start()
        {
            if (_hook != IntPtr.Zero) return;
            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
            Util.Log("paf hook=" + _hook + " fgd=" + _fmtFGD + " fc=" + _fmtFC);
        }

        public static void Stop()
        {
            if (_hook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_hook); } catch { }
                _hook = IntPtr.Zero;
                Util.Log("paf unhook");
            }
        }

        // ================= 键盘钩子 =================

        private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
                {
                    var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    // 纯 Ctrl+V（不带 Shift/Alt，避免干扰 Shift+Ctrl+V 等组合键）
                    if (k.vkCode == VK_V
                        && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
                        && (GetAsyncKeyState(VK_SHIFT) & 0x8000) == 0
                        && (GetAsyncKeyState(VK_MENU) & 0x8000) == 0)
                    {
                        CheckAndConvert();
                    }
                }
            }
            catch
            {
                // 钩子回调里绝不能抛异常
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        /// <summary>前台判定 + 焦点判定，全过才转换。所有检查都要快（在钩子线程上）。</summary>
        private static void CheckAndConvert()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return;

            GetWindowThreadProcessId(fg, out uint pid);
            if (pid == _selfPid) return;                       // 自己窗口里粘贴，绝不干预

            string cls = Native.ClassName(fg);
            if (cls != "CabinetWClass" && cls != "ExploreWClass") return;  // 只管资源管理器

            if (!IsExplorerFileView(fg, out string focusCls))
            {
                // 焦点在搜索框/地址栏等 Edit 控件——原样粘贴文字，记录一条便于诊断
                Util.Log("paf skip: focus not in fileview (focus=" + focusCls + ")");
                return;
            }

            // 关键（2026-11-04 修复"写错目录"）：钩子回调处于"输入同步呼叫"上下文，
            // 期间发起任何出站 COM 调用都会被拒（RPC_E_CANTCALLOUT_ININPUTSYNCCALL），
            // 导致 ShellCom 精确匹配失败、UIA 兜底读到多标签窗口里"隐藏标签"的地址栏。
            // 这里只排队，真正的取目录/读剪贴板/写盘在钩子返回后由消息循环执行（COM 合法）。
            // Explorer 对纯图/纯文字剪贴板在文件区的粘贴本来就无动作，晚几十毫秒写盘无副作用。
            QueuePasteAsFile(fg);
        }

        /// <summary>焦点链检查：从焦点控件向上走到前台窗口，
        /// 命中 DirectUIHWND/SHELLDLL_DefView（文件列表宿主）才算；
        /// 链上出现 Edit/RichEdit/TextBox 类（搜索框、地址栏输入框）则放弃——那里要正常粘贴文字。
        /// focusCls 返回焦点叶子控件类名（诊断 Win11 各版本类名差异用）。</summary>
        private static bool IsExplorerFileView(IntPtr fgRoot, out string focusCls)
        {
            focusCls = "";
            uint tid = GetWindowThreadProcessId(fgRoot, out _);
            var gti = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(tid, ref gti) || gti.hwndFocus == IntPtr.Zero) return false;
            focusCls = Native.ClassName(gti.hwndFocus);

            bool inFile = false;
            IntPtr h = gti.hwndFocus;
            while (h != IntPtr.Zero)
            {
                string c = Native.ClassName(h);
                if (c == "DirectUIHWND" || c == "SHELLDLL_DefView") inFile = true;
                if (c == "Edit" || c.StartsWith("RichEdit") || c.Contains("TextBox")) return false;
                if (h == fgRoot) break;
                h = GetAncestor(h, GA_PARENT);
            }
            return inFile;
        }

        // ================= 剪贴板读取（只读，不动剪贴板） =================

        /// <summary>读取某个格式为字节数组（需已 OpenClipboard）。失败返回 null。</summary>
        private static byte[] GetFmt(uint fmt)
        {
            IntPtr h = GetClipboardData(fmt);
            if (h == IntPtr.Zero) return null;
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try
            {
                int size = (int)GlobalSize(h);
                if (size <= 0) return null;
                var buf = new byte[size];
                Marshal.Copy(p, buf, 0, size);
                return buf;
            }
            finally { GlobalUnlock(h); }
        }

        /// <summary>读取剪贴板内容并判定类型（只读）：
        /// 成功时 data=文件字节（图片为 PNG、文字为 UTF8+BOM），name=建议文件名，keep=需保留的原始格式。
        /// 已是文件/虚拟文件、富文本等不适用情形返回 false。</summary>
        private static bool ReadClipboardContent(out byte[] data, out string name, out List<KeyValuePair<uint, byte[]>> keep)
        {
            data = null; name = null; keep = null;
            try
            {
                // OpenClipboard 可能被其它进程短暂占用，最多重试 3 次
                bool opened = false;
                for (int i = 0; i < 3 && !opened; i++)
                    opened = OpenClipboard(IntPtr.Zero);
                if (!opened) { Util.Log("paf open clip fail"); return false; }

                try
                {
                    // ---- 枚举当前格式，判定内容类型 ----
                    var fmts = new List<uint>();
                    uint f = 0;
                    while ((f = EnumClipboardFormats(f)) != 0) fmts.Add(f);
                    if (fmts.Count == 0) return false;

                    // 已是真实文件(CF_HDROP)或虚拟文件(FileGroupDescriptorW) → 不干预
                    if (fmts.Contains(CF_HDROP) || fmts.Contains(_fmtFGD)) return false;

                    bool isImage = fmts.Contains(CF_DIB) || fmts.Contains(CF_DIBV5) || fmts.Contains(_fmtPng);
                    bool isText = fmts.Contains(CF_UNICODETEXT);

                    keep = new List<KeyValuePair<uint, byte[]>>();

                    if (isImage)
                    {
                        // 优先直接用剪贴板自带的 PNG 格式（免重编码）；否则 DIB → PNG
                        byte[] png = fmts.Contains(_fmtPng) ? GetFmt(_fmtPng) : null;
                        if (png == null)
                        {
                            byte[] dib = fmts.Contains(CF_DIB) ? GetFmt(CF_DIB)
                                       : fmts.Contains(CF_DIBV5) ? GetFmt(CF_DIBV5) : null;
                            if (dib == null) return false;
                            png = DibToPng(dib);
                            if (png == null) return false;   // 解码失败（如 top-down DIB）→ 放行
                        }
                        if (png.Length > 64 * 1024 * 1024) return false; // 异常大图不处理
                        data = png;
                        name = NextName("pic_", ".png");
                    }
                    else if (isText)
                    {
                        byte[] raw = GetFmt(CF_UNICODETEXT);
                        if (raw == null || raw.Length < 2) return false;
                        string text = Encoding.Unicode.GetString(raw);
                        int z = text.IndexOf('\0');
                        if (z >= 0) text = text.Substring(0, z);
                        if (string.IsNullOrWhiteSpace(text) || text.Length > 4_000_000) return false; // 空白/超长不转
                        var enc8 = new UTF8Encoding(true);   // 带 BOM，记事本打开不乱码
                        byte[] pre = enc8.GetPreamble();
                        byte[] body = enc8.GetBytes(text);
                        data = new byte[pre.Length + body.Length];
                        Array.Copy(pre, data, pre.Length);
                        Array.Copy(body, 0, data, pre.Length, body.Length);
                        name = NextName("text_", ".txt");
                    }
                    else return false; // 既非纯图也非纯文字（富文本等）→ 不干预

                    return true;
                }
                finally { CloseClipboard(); }
            }
            catch (Exception ex)
            {
                Util.Log("paf read exc: " + ex.Message);
                return false;
            }
        }

        /// <summary>DIB 内存 → PNG 字节。失败返回 null（调用方放弃，放行原始按键）。
        /// 做法：补 BITMAPFILEHEADER 拼成 .bmp 字节流，交给 WPF BmpBitmapDecoder 解码、
        /// PngBitmapEncoder 编码——两者均可任意线程运行（无 STA 要求）。</summary>
        private static byte[] DibToPng(byte[] dib)
        {
            try
            {
                if (dib == null || dib.Length < 40) return null;
                int headerSize = BitConverter.ToInt32(dib, 0);
                ushort bpp = BitConverter.ToUInt16(dib, 14);
                uint compression = BitConverter.ToUInt32(dib, 16);
                uint clrUsed = BitConverter.ToUInt32(dib, 32);
                if (clrUsed == 0 && bpp <= 8) clrUsed = 1u << bpp;
                int offBits = 14 + headerSize + (int)clrUsed * 4;
                if (compression == 3 && headerSize == 40) offBits += 12; // BITFIELDS 三色掩码

                var ms = new MemoryStream(14 + dib.Length);
                var bw = new BinaryWriter(ms);
                bw.Write((byte)'B'); bw.Write((byte)'M');
                bw.Write(14 + dib.Length);       // bfSize
                bw.Write((short)0); bw.Write((short)0);
                bw.Write(offBits);               // bfOffBits
                bw.Write(dib);
                bw.Flush();

                var dec = new BmpBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                if (dec.Frames.Count == 0) return null;
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(dec.Frames[0]));
                var outMs = new MemoryStream();
                enc.Save(outMs);
                return outMs.ToArray();
            }
            catch (Exception ex)
            {
                Util.Log("paf dib2png fail: " + ex.Message);
                return null;
            }
        }

        // ================= 主流程 =================

        /// <summary>钩子回调里只排队：把前台窗口句柄封存，交给消息循环在"正常上下文"执行。
        /// 500ms 限时改为在执行端测量（排队本身是即时的）。</summary>
        private static void QueuePasteAsFile(IntPtr fgExplorer)
        {
            var sw = Stopwatch.StartNew();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                new Action(() => ExecutePasteAsFile(fgExplorer, sw)), DispatcherPriority.Normal);
        }

        /// <summary>执行端（消息循环里，COM 合法）：读剪贴板 → 严格解析前台资源管理器当前目录 →
        /// 直接写文件 → 刷新视图并选中。任何一步失败都明确提示，绝不写到可能错误的目录。</summary>
        private static void ExecutePasteAsFile(IntPtr fgExplorer, Stopwatch sw)
        {
            try
            {
                if (!Native.IsWindow(fgExplorer)) return;      // 窗口已关：放弃（极罕见）

                if (!ReadClipboardContent(out byte[] data, out string name, out var keep))
                    return; // 类型不适用/读失败——静默放行（Explorer 原粘贴也无动作）

                if (sw.ElapsedMilliseconds > 500) { Util.Log("paf too slow, abort"); return; }

                // ---- 严格解析目录：只认前台窗口（多标签时活动标签），识别不了绝不猜 ----
                string title = Native.WindowText(fgExplorer);
                string dir = null;
                if (ShellCom.TryGetPathStrict(fgExplorer, out string strictPath))
                    dir = strictPath;
                Util.Log("paf dir=" + (dir ?? "<null>") + " fgtitle=[" + title + "] (in " + sw.ElapsedMilliseconds + "ms)");

                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    // 宁可不保存也不写错地方（11:34 实测 UIA 兜底把文件写进隐藏标签的目录）
                    DeskKit.App.Notifier("粘贴即存失败：取不到当前目录（未保存）");
                    Util.Log("paf abort: no strict dir");
                    return;
                }

                string path = UniquePath(Path.Combine(dir, name));
                try
                {
                    File.WriteAllBytes(path, data);
                    // 可见反馈：刷新目录 → 在资源管理器中选中新文件（真粘贴同款效果）；选不中则气泡兜底
                    NotifyFolderRefresh(dir);
                    bool sel = ShellCom.TrySelectItem(fgExplorer, path);
                    Util.Log("paf saved " + path + " " + data.Length + "B in " + sw.ElapsedMilliseconds + "ms select=" + sel);
                    if (!sel) DeskKit.App.Notifier("已保存: " + path);
                }
                catch (Exception ex)
                {
                    Util.Log("paf write fail: " + ex.Message);
                    DeskKit.App.Notifier("粘贴即存写入失败: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                Util.Log("paf exc: " + ex.Message);
            }
        }

        /// <summary>目标文件已存在时加 _2、_3…（正常时间戳命名几乎不会撞）</summary>
        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path);
            string baseName = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; i < 100; i++)
            {
                string p = Path.Combine(dir, baseName + "_" + i + ext);
                if (!File.Exists(p)) return p;
            }
            return path;
        }

        // ================= 兜底：虚拟文件剪贴板转换（首版方案保留） =================

        /// <summary>把内容以"虚拟文件"格式写回剪贴板（FileGroupDescriptorW + FileContents）。
        /// 11:19 实测 Explorer 粘贴不认，仅作目录取不到时的兜底保留。
        /// 注意保留原始格式：Explorer 粘贴出文件，Word/聊天窗口仍能粘贴原图/原文字。</summary>
        private static void SetVirtualFileClipboard(byte[] data, string name, List<KeyValuePair<uint, byte[]>> keep)
        {
            try
            {
                if (!OpenClipboard(IntPtr.Zero)) { Util.Log("paf vf open fail"); return; }
                try
                {
                    EmptyClipboard();
                    IntPtr hFgd = AllocBytes(BuildFileGroupDescriptor(name, data.Length));
                    IntPtr hFc = AllocBytes(data);
                    IntPtr hDe = AllocBytes(new byte[] { 1, 0, 0, 0 }); // DROPEFFECT_COPY
                    // SetClipboardData 成功后所有权移交系统；失败必须自己 GlobalFree
                    if (hFgd != IntPtr.Zero) { if (SetClipboardData(_fmtFGD, hFgd) == IntPtr.Zero) { Util.Log("paf vf set fgd fail"); GlobalFree(hFgd); } }
                    if (hFc != IntPtr.Zero) { if (SetClipboardData(_fmtFC, hFc) == IntPtr.Zero) { Util.Log("paf vf set fc fail"); GlobalFree(hFc); } }
                    if (hDe != IntPtr.Zero) { if (SetClipboardData(_fmtDropEffect, hDe) == IntPtr.Zero) GlobalFree(hDe); }

                    foreach (var kv in keep)
                    {
                        IntPtr h = AllocBytes(kv.Value);
                        if (h != IntPtr.Zero) { if (SetClipboardData(kv.Key, h) == IntPtr.Zero) GlobalFree(h); }
                    }

                    // 诊断：回读剪贴板格式清单，确认写入是否成功
                    var after = new List<uint>();
                    uint f = 0;
                    while ((f = EnumClipboardFormats(f)) != 0) after.Add(f);
                    Util.Log("paf vf formats after: " + string.Join(",", after) +
                             " (fgd=" + after.Contains(_fmtFGD) + " fc=" + after.Contains(_fmtFC) + ")");
                }
                finally { CloseClipboard(); }
            }
            catch (Exception ex) { Util.Log("paf vf exc: " + ex.Message); }
        }

        /// <summary>构造 FILEGROUPDESCRIPTORW（1 个文件）：
        /// UINT cItems + FILEDESCRIPTORW(592 字节)。手工按偏移写，避免封送对齐问题。
        /// dwFlags = FD_ATTRIBUTES(0x4) | FD_FILESIZE(0x40) | FD_PROGRESSUI(0x4000)。</summary>
        private static byte[] BuildFileGroupDescriptor(string fileName, long fileSize)
        {
            if (fileName.Length > 259) fileName = fileName.Substring(0, 259);
            var buf = new byte[4 + 592];
            buf[0] = 1;          // cItems = 1
            int d = 4;           // FILEDESCRIPTORW 起点
            // dwFlags @ +0
            const uint flags = 0x4 | 0x40 | 0x4000;
            WriteU32(buf, d + 0, flags);
            // clsid(+4..19) / sizel(+20..27) / pointl(+28..35) 全零 = 忽略
            // dwFileAttributes @ +36 = FILE_ATTRIBUTE_NORMAL(0x80)
            buf[d + 36] = 0x80;
            // 三个 FILETIME(+40..63) 全零 = 时间未知
            // nFileSizeHigh @ +64 / nFileSizeLow @ +68
            long size = Math.Max(0, fileSize);
            WriteU32(buf, d + 64, (uint)(size >> 32));
            WriteU32(buf, d + 68, (uint)(size & 0xFFFFFFFF));
            // cFileName @ +72，260 个 WCHAR（UTF-16，null 结尾）
            byte[] nb = Encoding.Unicode.GetBytes(fileName);
            Array.Copy(nb, 0, buf, d + 72, Math.Min(nb.Length, 518));
            return buf;
        }

        private static void WriteU32(byte[] buf, int off, uint v)
        {
            buf[off] = (byte)v; buf[off + 1] = (byte)(v >> 8);
            buf[off + 2] = (byte)(v >> 16); buf[off + 3] = (byte)(v >> 24);
        }

        /// <summary>生成文件名 pic_/text_ + 时间戳；同一秒内重复转换则 _2、_3 递增</summary>
        private static string NextName(string prefix, string ext)
        {
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string name = prefix + ts + ext;
            if (name == _lastName)
            {
                _seq++;
                name = prefix + ts + "_" + (_seq + 1) + ext;
            }
            else
            {
                _lastName = name;
                _seq = 0;
            }
            return name;
        }

        // ================= HGLOBAL 工具 =================

        /// <summary>把字节数组放进新 HGLOBAL（调用方经 SetClipboardData 移交所有权）。失败返回 IntPtr.Zero。</summary>
        private static IntPtr AllocBytes(byte[] data)
        {
            if (data == null || data.Length == 0) return IntPtr.Zero;
            IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
            if (h == IntPtr.Zero) return IntPtr.Zero;
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) { GlobalFree(h); return IntPtr.Zero; }
            Marshal.Copy(data, 0, p, data.Length);
            GlobalUnlock(h);
            return h;
        }
    }
}
