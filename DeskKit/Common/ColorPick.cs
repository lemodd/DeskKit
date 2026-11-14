// ColorPick.cs — 吸管取色共用工具（2026-11-14）
// 截图/贴图工具条的吸管共用：像素采样 + HEX 格式化。
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskKit
{
    public static class ColorPick
    {
        /// <summary>取位图上 (pxX,pxY) 处像素颜色；越界/空图返回 false。
        /// 非 BGRA 格式先转 Bgra32（每次调用转换一次，取色是低频操作，够用）。</summary>
        public static bool TrySample(BitmapSource src, int pxX, int pxY, out Color c)
        {
            c = default;
            if (src is null) return false;
            if (pxX < 0 || pxY < 0 || pxX >= src.PixelWidth || pxY >= src.PixelHeight) return false;
            var conv = src.Format == PixelFormats.Bgra32 || src.Format == PixelFormats.Pbgra32
                ? src
                : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            var buf = new byte[4];
            conv.CopyPixels(new Int32Rect(pxX, pxY, 1, 1), buf, 4, 0);
            c = Color.FromArgb(buf[3], buf[2], buf[1], buf[0]);   // Bgra32 字节序：B G R A
            return true;
        }

        /// <summary>HEX 文本：不透明输出 #RRGGBB，带透明输出 #AARRGGBB</summary>
        public static string ToHex(Color c) =>
            c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
