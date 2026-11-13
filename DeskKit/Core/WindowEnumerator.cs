using MiniPix.Native;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace MiniPix.Core;

/// <summary>Window candidate used for auto-snap during region selection.</summary>
public sealed record WindowHit(IntPtr Hwnd, Rect PxBounds, string Title);

public static class WindowEnumerator
{
    /// <summary>All visible top-level windows in physical virtual-desktop pixel coordinates, z-order top first.</summary>
    public static List<WindowHit> GetSnappableWindows()
    {
        var result = new List<WindowHit>();
        NativeMethods.EnumWindows((h, _) =>
        {
            TryAdd(h, result);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static void TryAdd(IntPtr h, List<WindowHit> result)
    {
        if (!NativeMethods.IsWindowVisible(h) || NativeMethods.IsIconic(h)) return;

        var ex = NativeMethods.GetWindowLong(h, NativeMethods.GWL_EXSTYLE);
        if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return;

        var title = GetTitle(h);
        if (string.IsNullOrEmpty(title) && (ex & NativeMethods.WS_EX_LAYERED) != 0) return;

        // DWM extended frame bounds (physical px); fallback to GetWindowRect via a self-pInvoke below
        if (!TryGetDwmBounds(h, out var bounds)) return;
        if (bounds.Width < 8 || bounds.Height < 8) return;

        result.Add(new WindowHit(h, bounds, title));
    }

    private static string GetTitle(IntPtr h)
    {
        int len = NativeMethods.GetWindowTextLength(h);
        if (len == 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        NativeMethods.GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool TryGetDwmBounds(IntPtr h, out Rect bounds)
    {
        if (NativeMethods.DwmGetWindowAttribute(h, (int)NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var r, Marshal.SizeOf<NativeMethods.RECT>()) == 0)
        {
            bounds = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return true;
        }
        bounds = Rect.Empty;
        return false;
    }
}
