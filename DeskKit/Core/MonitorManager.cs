using System.Windows;

namespace MiniPix.Core;

/// <summary>One monitor, in physical-pixel virtual-desktop coordinates.</summary>
public sealed record MonitorInfo(IntPtr Handle, Rect Int32Rect, double DpiX, double DpiY)
{
    public double ScaleX => DpiX / 96.0;
    public double ScaleY => DpiY / 96.0;
    public Rect PxRect => new(Int32Rect.X, Int32Rect.Y, Int32Rect.Width, Int32Rect.Height);
    public bool IntersectsWith(Rect r) => Int32Rect.IntersectsWith(r);
}

public static class MonitorManager
{
    public static List<MonitorInfo> GetAll()
    {
        var list = new List<MonitorInfo>();
        Native.NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (hMon, _, _, _) =>
            {
                var mi = new Native.NativeMethods.MONITORINFOEX
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.NativeMethods.MONITORINFOEX>()
                };
                if (Native.NativeMethods.GetMonitorInfo(hMon, ref mi))
                {
                    // 2026-11-04 修复钉图放大/错位：参数应为 MDT_EFFECTIVE_DPI=0（含系统缩放的有效 DPI）。
                    // 原传 2 = MDT_RAW_DPI（面板物理 DPI，不随系统缩放，27寸1080p≈81），
                    // 导致 monScale=0.844、钉图缩放 1.185 倍且位置偏移（日志实锤）
                    Native.NativeMethods.GetDpiForMonitor(hMon, 0 /*MDT_EFFECTIVE_DPI*/, out uint dx, out uint dy);
                    list.Add(new MonitorInfo(hMon,
                        new Rect(mi.rcMonitor.Left, mi.rcMonitor.Top,
                            mi.rcMonitor.Right - mi.rcMonitor.Left,
                            mi.rcMonitor.Bottom - mi.rcMonitor.Top), dx, dy));
                }
                return true;
            }, IntPtr.Zero);
        return list;
    }

    /// <summary>Monitor (physical px) containing a point of the virtual desktop.</summary>
    public static MonitorInfo AtPoint(double px, double py)
    {
        var h = Native.NativeMethods.MonitorFromPoint(new Native.NativeMethods.POINT { X = (int)px, Y = (int)py }, 0);
        if (h == IntPtr.Zero) return GetAll().FirstOrDefault() ?? throw new InvalidOperationException("no monitor");
        // 2026-11-04 同上：MDT_EFFECTIVE_DPI=0（原传 2 = RAW_DPI 面板物理 DPI，错误）
        Native.NativeMethods.GetDpiForMonitor(h, 0, out uint dx, out uint dy);
        var mi = new Native.NativeMethods.MONITORINFOEX
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.NativeMethods.MONITORINFOEX>()
        };
        Native.NativeMethods.GetMonitorInfo(h, ref mi);
        return new MonitorInfo(h, new Rect(mi.rcMonitor.Left, mi.rcMonitor.Top,
            mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top), dx, dy);
    }
}
