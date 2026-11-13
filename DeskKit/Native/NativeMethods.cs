using System.Runtime.InteropServices;

namespace MiniPix.Native;

internal static class NativeMethods
{
    public const int WM_HOTKEY = 0x0312;
    public const int MOD_CONTROL = 0x0002;
    public const int MOD_SHIFT = 0x0004;
    public const int MOD_ALT = 0x0001;
    public const int VK_SNAPSHOT = 0x2C;
    public const int VK_F1 = 0x70; // DeskKit 2026-10-30：区域截图热键改为 F1
    public const int VK_A = 0x41;
    public const int VK_C = 0x43;
    public const int VK_V = 0x56;
    public const int VK_G = 0x47; // DeskKit 合并新增：Ctrl+G 用
    public const int VK_S = 0x53; // DeskKit 升级阶段3：Ctrl+Alt+S 唤出/隐藏暂存架用
    public const int VK_D = 0x44; // DeskKit 升级阶段3：暂存架热键备用键位（Ctrl+Alt+D）
    public const int VK_F2 = 0x71; // DeskKit 2026-11-12：弹出/收回暂存架默认热键 F2
    public const int VK_1 = 0x31;  // DeskKit 2026-11-12：Alt+1/2/3 切换标签
    public const int VK_2 = 0x32;
    public const int VK_3 = 0x33;

    public const int WM_ENTERSIZEMOVE = 0x0231; // 升级阶段3：窗口进入移动/缩放循环（拖离贴边时解除）
    public const int WM_EXITSIZEMOVE = 0x0232;  // 升级阶段3：移动/缩放结束（判定是否贴边）

    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CVIRTUALSCREEN = 78;
    public const int SM_CVIRTUALHEIGHT = 79;
    public const int SM_CMONITORS = 80;

    public const uint MONITORINFOF_PRIMARY = 1;
    public const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nCount);
    [DllImport("user32.dll", SetLastError = true)] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ---- 长截图（2026-11-11）：遮罩窗口保持可见但被屏幕捕获忽略 + 滚轮转发 ----
    [DllImport("user32.dll", SetLastError = true)] public static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT Point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
    public const uint GA_ROOT = 2;
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
    /// <summary>窗口从屏幕捕获中排除（Win10 2004+）：BitBlt/截图 API 拍不到本窗口，拍到的是窗口底下的内容</summary>
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    public const int WM_MOUSEWHEEL = 0x020A;
    public const long WS_EX_TRANSPARENT = 0x00000020L;
    public const uint WDA_NONE = 0x0, WDA_EXCLUDEFROMCAPTURE = 0x11;

    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, int flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);
    [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    // ---- 2026-11-11 长截图：遮罩窗口在选区处挖洞（SetWindowRgn）----
    // 选区内无本窗口像素 → 用户直接看到真实页面、鼠标滚轮天然穿透到页面窗口、
    // BitBlt 拍选区不会拍到遮罩（替代 WDA_EXCLUDEFROMCAPTURE，后者会让 WPF 遮罩窗口在部分环境整个消失）。
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);
    [DllImport("gdi32.dll")] public static extern int CombineRgn(IntPtr hrgnDst, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int mode);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    public const int RGN_DIFF = 4;

    // ---- 2026-11-11 长截图自动滚动（ShareX ScrollingCapture 同款方案）：程序以固定小步长模拟滚轮 ----
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);

    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT
    {
        public uint type;                       // 0 = INPUT_MOUSE
        public MOUSEINPUT mi;
    }
    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;

    /// <summary>在光标当前位置注入一次滚轮事件（正值上滚、负值下滚，120 = 一刻度）。
    /// 作用于光标下的真实窗口（经挖洞命中页面），无需目标窗口拥有焦点。</summary>
    public static void SendMouseWheel(int delta)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = MOUSEEVENTF_WHEEL }
        };
        _ = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;

    /// <summary>不参与 Alt-Tab、不抢焦点（长截图悬浮条用：用户滚轮要继续作用于目标窗口）</summary>
    public const long WS_EX_NOACTIVATE = 0x08000000L;
    public const long WS_VISIBLE = 0x10000000L;
    public const long WS_CHILD = 0x40000000L;
    public const long WS_EX_LAYERED = 0x00080000L;
    public const uint SRCCOPY = 0x00CC0020;
    public const uint CAPTUREBLT = 0x40000000;
    public const int LOGPIXELSX = 88;
    public const int LOGPIXELSY = 90;

    /// <summary>Physical-pixel virtual-screen rectangle (x,y,w,h).</summary>
    public readonly record struct RectI(int X, int Y, int Width, int Height)
    {
        public int Left => X;
        public int Top => Y;
        public int Right => X + Width;
        public int Bottom => Y + Height;
    }

    public static RectI GetVirtualScreen() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CVIRTUALSCREEN),
        GetSystemMetrics(SM_CVIRTUALHEIGHT));
}
