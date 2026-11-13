using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using MiniPix.Native;

namespace MiniPix.Output;

public static class ClipboardUtil
{
    /// <summary>写图片到剪贴板（2026-11-13 C-M2：重试耗尽不再抛异常——返回 false 让调用方
    /// 提示并继续关闭窗口，避免截图窗因剪贴板被占用而卡死不关）。</summary>
    public static bool SetImage(BitmapSource image)
    {
        for (int i = 0; i < 3; i++)
        {
            try { Clipboard.SetImage(image); return true; }
            catch (System.Runtime.InteropServices.COMException) when (i < 2)
            {
                System.Threading.Thread.Sleep(80);
                Clipboard.Clear();
            }
            catch (Exception ex)
            {
                MiniPix.Core.Log.Write("clipboard SetImage FAIL: " + ex.Message);
                return false;
            }
        }
        MiniPix.Core.Log.Write("clipboard SetImage FAIL: retries exhausted (clipboard locked)");
        return false;
    }

    /// <summary>Put image on clipboard *and* as a file-group (explorer-ready), like PixPin.
    /// 2026-11-13 C-M2：重试耗尽返回 false 不抛异常。</summary>
    public static bool SetImageWithFile(BitmapSource image, string tempPath)
    {
        var full = Path.GetFullPath(tempPath);
        var data = new System.Windows.DataObject(true);
        data.SetImage(image);
        data.SetFileDropList(new System.Collections.Specialized.StringCollection { full });
        for (int i = 0; i < 3; i++)
        {
            try { Clipboard.SetDataObject(data, true); return true; }
            catch (System.Runtime.InteropServices.COMException) when (i < 2)
            {
                System.Threading.Thread.Sleep(80);
                Clipboard.Clear();
            }
            catch (Exception ex)
            {
                MiniPix.Core.Log.Write("clipboard SetImageWithFile FAIL: " + ex.Message);
                return false;
            }
        }
        MiniPix.Core.Log.Write("clipboard SetImageWithFile FAIL: retries exhausted");
        return false;
    }

    public static bool TryGetClipboardImage(out BitmapSource? image)
    {
        image = null;
        try
        {
            if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img != null)
                {
                    // clipboard DIB may be 32bpp BGRA with uninitialized alpha -> make opaque
                    var fmt = img.Format;
                    var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                        img, System.Windows.Media.PixelFormats.Bgr32, null, 0);
                    conv.Freeze();
                    image = conv;
                    return true;
                }
            }
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                foreach (var f in files)
                {
                    if (System.IO.Path.HasExtension(f) &&
                        new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff" }.Contains(
                            System.IO.Path.GetExtension(f).ToLowerInvariant()))
                    {
                        var bmp = new System.Windows.Media.Imaging.BitmapImage(new Uri(f));
                        bmp.Freeze();
                        image = bmp;
                        return true;
                    }
                }
            }
        }
        catch { }
        return false;
    }
}

public static class SaveUtil
{
    public static string NextFileName() =>
        $"MiniPix {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png";

    public static string NextScreenshotPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MiniPix");
        Directory.CreateDirectory(dir);
        var name = $"MiniPix {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png";
        return Path.Combine(dir, name);
    }

    public static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        encoder.Save(fs);
    }
}
