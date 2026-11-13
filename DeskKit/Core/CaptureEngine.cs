using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiniPix.Native;

namespace MiniPix.Core;

/// <summary>Grabs the whole virtual desktop (all monitors) into one bitmap at native resolution.</summary>
public static class CaptureEngine
{
    /// <summary>Capture entire virtual desktop. Bitmap is physical pixels; OriginPx = virtual screen origin.</summary>
    public static (BitmapSource Image, Point OriginPx) CaptureVirtualDesktop()
    {
        var vs = NativeMethods.GetVirtualScreen();
        var img = BltRect(vs.X, vs.Y, vs.Width, vs.Height);
        return (img, new Point(vs.X, vs.Y));
    }

    /// <summary>
    /// 长截图轮询抓屏（2026-11-11）：只 BitBlt 虚拟桌面坐标系里的一个矩形（选区），
    /// 避免每 180ms 抓整个虚拟桌面。pxRect 为虚拟桌面物理像素矩形。
    /// </summary>
    public static BitmapSource CaptureRect(Rect pxRect)
    {
        int x = (int)Math.Round(pxRect.X);
        int y = (int)Math.Round(pxRect.Y);
        int w = Math.Max(1, (int)Math.Round(pxRect.Width));
        int h = Math.Max(1, (int)Math.Round(pxRect.Height));
        return BltRect(x, y, w, h);
    }

    /// <summary>
    /// GDI 抓屏统一实现（2026-11-13 C-M1）：
    /// ① GetDC/CreateCompatibleDC/CreateCompatibleBitmap/BitBlt 返回值全部检查，
    ///    锁屏/安全桌面时抛语义异常（调用方捕获记日志），不再产出全黑垃圾帧；
    /// ② hBmp 的 DeleteObject 进 finally，CreateBitmapSourceFromHBitmap 抛异常不再泄漏 GDI 句柄。
    /// </summary>
    private static BitmapSource BltRect(int x, int y, int w, int h)
    {
        var hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero)
            throw new InvalidOperationException("GetDC(screen desktop) failed");
        var hdcMem = IntPtr.Zero;
        var hBmp = IntPtr.Zero;
        try
        {
            hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
            if (hdcMem == IntPtr.Zero)
                throw new InvalidOperationException("CreateCompatibleDC failed");
            hBmp = NativeMethods.CreateCompatibleBitmap(hdcScreen, w, h);
            if (hBmp == IntPtr.Zero)
                throw new InvalidOperationException($"CreateCompatibleBitmap({w}x{h}) failed");
            var old = NativeMethods.SelectObject(hdcMem, hBmp);
            if (old == IntPtr.Zero)
                throw new InvalidOperationException("SelectObject failed");
            if (!NativeMethods.BitBlt(hdcMem, 0, 0, w, h, hdcScreen, x, y, NativeMethods.SRCCOPY))
                throw new InvalidOperationException($"BitBlt({w}x{h}) failed (lock screen / secure desktop?)");
            NativeMethods.SelectObject(hdcMem, old);

            var img = Imaging.CreateBitmapSourceFromHBitmap(
                hBmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            img = MakeOpaque(img);
            img.Freeze();
            return img;
        }
        finally
        {
            if (hBmp != IntPtr.Zero) NativeMethods.DeleteObject(hBmp);
            if (hdcMem != IntPtr.Zero) NativeMethods.DeleteDC(hdcMem);
            if (hdcScreen != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    /// <summary>
    /// A screen-DC BitBlt gives 32bpp data whose alpha bits are all zero, so WPF would treat the
    /// whole image as transparent (crops and exports end up fully transparent / black).
    /// </summary>
    private static BitmapSource MakeOpaque(BitmapSource src)
    {
        var fmt = src.Format;
        if (fmt != PixelFormats.Bgra32 && fmt != PixelFormats.Pbgra32)
        {
            var converted = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            return converted;
        }
        int stride = src.PixelWidth * (fmt.BitsPerPixel / 8);
        var px = new byte[stride * src.PixelHeight];
        src.CopyPixels(px, stride, 0);
        for (int i = 3; i < px.Length; i += 4) px[i] = 0xFF;
        var res = BitmapSource.Create(src.PixelWidth, src.PixelHeight, src.DpiX, src.DpiY,
            PixelFormats.Bgra32, null, px, stride);
        res.Freeze();
        return res;
    }

    /// <summary>Crop a rectangle (physical px, virtual-desktop space) from the captured desktop bitmap.</summary>
    public static BitmapSource Crop(BitmapSource desktop, Point originPx, Rect cropPx)
    {
        int x = Math.Max(0, (int)Math.Round(cropPx.X - originPx.X));
        int y = Math.Max(0, (int)Math.Round(cropPx.Y - originPx.Y));
        int w = Math.Max(1, (int)Math.Round(cropPx.Width));
        int h = Math.Max(1, (int)Math.Round(cropPx.Height));
        if (x + w > desktop.PixelWidth) w = desktop.PixelWidth - x;
        if (y + h > desktop.PixelHeight) h = desktop.PixelHeight - y;
        var src = new CroppedBitmap(desktop, new Int32Rect(x, y, w, h));
        src.Freeze();
        return src;
    }

    /// <summary>Re-stamp a bitmap with the given DPI (pixel data unchanged).</summary>
    public static BitmapSource WithDpi(BitmapSource src, double dpiX, double dpiY)
    {
        var format = PixelFormats.Bgra32;
        var converted = src.Format == format ? src : new FormatConvertedBitmap(src, format, null, 0);
        int stride = converted.PixelWidth * format.BitsPerPixel / 8;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight,
            dpiX, dpiY, format, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
