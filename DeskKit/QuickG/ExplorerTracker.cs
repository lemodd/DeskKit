using System;

namespace CtrlG
{
    /// <summary>
    /// 跟踪最近一次处于活动状态的资源管理器窗口，供 Ctrl+G 读取其当前目录。
    /// </summary>
    internal sealed class ExplorerTracker : IDisposable
    {
        private static readonly string[] ExplorerWindowClasses =
        {
            "CabinetWClass", // 普通资源管理器窗口
            "ExploreWClass", // 旧式文件夹窗口
        };

        private static readonly string[] DialogWindowClasses =
        {
            "#32770", // 传统通用对话框
            "Any",    // Vista+ IFileDialog（打开/另存为）
        };

        private IntPtr _hookForeground;
        private IntPtr _hookFocus;

        // 保存委托，防止被 GC 回收（托管回调会被原生代码调用）
        private readonly Native.WinEventDelegate _foregroundProc;
        private readonly Native.WinEventDelegate _focusProc;

        // ---- DeskKit 合并改动：改用专用消息泵线程装 hook ----
        // 原因：同样的 hook 代码在 WinForms 宿主（原 CtrlG）里回调正常，
        // 在 WPF 宿主（DeskKit）里回调一条都不送达（实测 0 个 WinEvent）。
        // 改为后台线程跑经典 GetMessage 消息循环，WinEvent 回调在该线程送达。
        private Thread? _pumpThread;
        private uint _pumpThreadId;
        private volatile bool _pumpRunning;

        // 最近一次活动的资源管理器窗口：泵线程写 / UI 线程读，用 Interlocked
        private long _lastExplorerWindowLong;
        private int _evtCount; // DeskKit 诊断：统计 WinEvent 回调是否真的到达

        public ExplorerTracker()
        {
            _foregroundProc = (h, ev, hwnd, idObj, idChild, pid, tid) => OnEvent(hwnd);
            _focusProc = (h, ev, hwnd, idObj, idChild, pid, tid) => OnEvent(hwnd);
        }

        public void Start()
        {
            const uint flags = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;

            _foregroundProcSelf = this; // 钉住 this，防 GC 意外（委托字段已保，双保险）

            _pumpRunning = true;
            _pumpThread = new Thread(() =>
            {
                try
                {
                    _hookForeground = Native.SetWinEventHook(
                        Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                        IntPtr.Zero, _foregroundProc, 0, 0, flags);

                    _hookFocus = Native.SetWinEventHook(
                        Native.EVENT_OBJECT_FOCUS, Native.EVENT_OBJECT_FOCUS,
                        IntPtr.Zero, _focusProc, 0, 0, flags);

                    _pumpThreadId = Native.GetCurrentThreadId();
                    Util.Log("pump thread hooks fg=" + _hookForeground + " focus=" + _hookFocus);

                    // 经典消息循环：WinEvent 回调在这里送达
                    while (_pumpRunning)
                    {
                        int r = Native.GetMessage(out Native.MSG msg, IntPtr.Zero, 0, 0);
                        if (r <= 0) break; // WM_QUIT 或错误
                    }
                }
                catch (Exception ex)
                {
                    Util.Log("pump thread exc: " + ex.Message);
                }
                Util.Log("pump thread exit");
            });
            _pumpThread.SetApartmentState(ApartmentState.STA);
            _pumpThread.IsBackground = true;
            _pumpThread.Name = "CtrlG-WinEvent-Pump";
            _pumpThread.Start();
        }

        // 引用自身，确保委托链与实例在整个生命周期内不会被意外回收
        private static ExplorerTracker? _foregroundProcSelf;

        private void OnEvent(IntPtr hwnd)
        {
            try
            {
                // DeskKit 诊断：前 30 个事件记录原始值，确认回调是否到达 WPF 宿主
                _evtCount++;
                if (_evtCount <= 30)
                    Util.Log("winevent#" + _evtCount + " hwnd=" + hwnd);

                IntPtr root = Native.GetAncestor(hwnd, Native.GA_ROOT);
                if (root == IntPtr.Zero) return;

                string cls = Native.ClassName(root);
                if (Array.Exists(ExplorerWindowClasses, c => c == cls))
                {
                    System.Threading.Interlocked.Exchange(ref _lastExplorerWindowLong, (long)root);
                    Util.Log("track explorer hwnd=" + root + " cls=" + cls);
                    return;
                }

                // 打开的对话框可能属于资源管理器（如 Explorer 里点“另存为”），
                // 记录其宿主资源管理器窗口。
                if (Array.Exists(DialogWindowClasses, c => c == cls))
                {
                    IntPtr owner = Native.GetAncestor(root, Native.GA_ROOTOWNER);
                    if (owner != IntPtr.Zero &&
                        Array.Exists(ExplorerWindowClasses, c => Native.ClassName(owner) == c))
                    {
                        System.Threading.Interlocked.Exchange(ref _lastExplorerWindowLong, (long)owner);
                    }
                }
            }
            catch
            {
                // 事件回调里绝不能抛异常
            }
        }

        public IntPtr LastExplorerWindow => (IntPtr)System.Threading.Interlocked.Read(ref _lastExplorerWindowLong);

        /// <summary>兜底找窗（2026-11-03 修复"时好时坏"）：
        /// WinEvent hook 只能看到安装之后发生的事件——若 DeskKit 启动后用户没有再
        /// 切换/点击过资源管理器（比如启动前 Explorer 就是前台，或直接在对话框里按 Ctrl+G），
        /// 跟踪器里永远是 0。这里按 Z 序枚举顶层窗口，第一个可见的 CabinetWClass
        /// 就是"最近使用"的资源管理器窗口。</summary>
        public static IntPtr FindTopmostExplorer()
        {
            IntPtr found = IntPtr.Zero;
            try
            {
                Native.EnumWindows((h, _) =>
                {
                    if (!Native.IsWindowVisible(h)) return true;   // 继续枚举
                    string cls = Native.ClassName(h);
                    if (cls == "CabinetWClass" || cls == "ExploreWClass")
                    {
                        found = h;                                  // Z 序最靠前 = 最近使用
                        return false;                               // 停止枚举
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }

        /// <summary>
        /// 获取当前资源管理器目录（优先 COM，兜底 UIA）。
        /// </summary>
        public string GetCurrentPath()
        {
            IntPtr hwnd = LastExplorerWindow;
            if (hwnd != IntPtr.Zero && !Native.IsWindow(hwnd)) hwnd = IntPtr.Zero;

            // 2026-11-03 修复"时好时坏"：跟踪器为空/已失效时，现场按 Z 序兜底找最近的资源管理器
            if (hwnd == IntPtr.Zero)
            {
                hwnd = FindTopmostExplorer();
                if (hwnd != IntPtr.Zero)
                    Util.Log("tracker empty -> zorder fallback hwnd=" + hwnd);
            }

            Util.Log("GetCurrentPath hwnd=" + hwnd);

            string title = hwnd != IntPtr.Zero ? Native.WindowText(hwnd) : null;
            bool c = ShellCom.TryGetPath(hwnd, title, out string path);
            Util.Log("shellcom=" + c + " path=" + (path ?? "<null>"));
            if (c && !string.IsNullOrEmpty(path)) return path;
            if (hwnd != IntPtr.Zero && UiaExplorer.TryGetPath(hwnd, out path))
            {
                Util.Log("uia path=" + path);
                return path;
            }
            return null;
        }

        public void Dispose()
        {
            // 先通知泵线程退出（PostThreadMessage WM_QUIT），再摘 hook
            _pumpRunning = false;
            if (_pumpThreadId != 0)
            {
                try { Native.PostThreadMessage(_pumpThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero); }
                catch { }
                _pumpThreadId = 0;
            }
            if (_hookForeground != IntPtr.Zero) { Native.UnhookWinEvent(_hookForeground); _hookForeground = IntPtr.Zero; }
            if (_hookFocus != IntPtr.Zero) { Native.UnhookWinEvent(_hookFocus); _hookFocus = IntPtr.Zero; }
            try { _pumpThread?.Join(500); } catch { }
        }
    }
}
