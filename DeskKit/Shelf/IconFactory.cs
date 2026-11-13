using System;
using System.Drawing;
using System.IO;

namespace ShotShelf
{
    /// <summary>
    /// 图标提供器：
    /// 2026-11-12 用户自备图标 —— 读内嵌资源 Assets\DeskKit.ico（256x256，程序集内嵌，
    /// 无外部文件依赖，单文件发布也带着）。托盘用 32x32 帧；WPF 标题栏解码最大帧（清晰）。
    /// 内嵌资源缺失/损坏时回退旧 GDI+ 画法（深蓝圆角底+三条白线，2026-10 原版）。
    /// </summary>
    public static class IconFactory
    {
        private static Icon? _cached;
        private const string ResName = "DeskKit.Assets.DeskKit.ico";

        /// <summary>打开内嵌 ico 流；资源不存在返回 null。</summary>
        private static Stream? OpenIcoStream() =>
            typeof(IconFactory).Assembly.GetManifestResourceStream(ResName);

        /// <summary>获取 32x32 托盘图标（懒加载 + 缓存）</summary>
        public static Icon GetIcon()
        {
            if (_cached != null) return _cached;

            // 优先：内嵌 ico（GDI 会从 256 帧缩到 32，质量可接受）
            try
            {
                using var s = OpenIcoStream();
                if (s != null)
                {
                    _cached = new Icon(s, 32, 32);
                    return _cached;
                }
            }
            catch { /* 回退画法 */ }

            return BuildDrawnIcon();
        }

        /// <summary>WPF 窗口标题栏/任务栏图标：解码内嵌 ico 的最大帧（256x256 原图，清晰）。</summary>
        public static System.Windows.Media.ImageSource GetWpfIcon()
        {
            try
            {
                using var s = OpenIcoStream();
                if (s != null)
                {
                    var dec = new System.Windows.Media.Imaging.IconBitmapDecoder(
                        s, System.Windows.Media.Imaging.BitmapCreateOptions.None,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    System.Windows.Media.Imaging.BitmapFrame best = dec.Frames[0];
                    foreach (var f in dec.Frames)
                        if (f.PixelWidth > best.PixelWidth) best = f;
                    best.Freeze();
                    return best;
                }
            }
            catch { /* 回退 */ }

            // 回退：GDI 图标转 BitmapSource
            var hIcon = GetIcon().Handle;
            var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                hIcon, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }

        /// <summary>旧 GDI+ 画法（兜底）：深蓝圆角底 + 三条白色横线。</summary>
        private static Icon BuildDrawnIcon()
        {
            // 画布放大 8 倍再缩回，抗锯齿效果更好
            const int S = 32, K = 8;
            using var bmp = new Bitmap(S * K, S * K);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                // 深蓝圆角底
                using var bgBrush = new SolidBrush(Color.FromArgb(42, 74, 137));   // #2A4A89
                using var bgPath = RoundedRect(0, 0, S * K, S * K, 6 * K);
                g.FillPath(bgBrush, bgPath);

                // 三条白色圆头横线 = 暂存架的"层"
                using var linePen = new Pen(Color.White, 3 * K)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round,
                };
                g.DrawLine(linePen, 7 * K, 10 * K, 25 * K, 10 * K);
                g.DrawLine(linePen, 7 * K, 16 * K, 25 * K, 16 * K);
                g.DrawLine(linePen, 7 * K, 22 * K, 18 * K, 22 * K); // 最短一条，有层次感
            }

            // 缩回 32x32 并转 Icon
            using (var small = new Bitmap(bmp, S, S))
            {
                IntPtr hIcon = small.GetHicon();
                _cached = Icon.FromHandle(hIcon);
            }
            return _cached;
        }

        /// <summary>圆角矩形路径</summary>
        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(x, y, r * 2, r * 2, 180, 90);
            path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
            path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
