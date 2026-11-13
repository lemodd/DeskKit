using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskKit
{
    /// <summary>
    /// 右键菜单图标（2026-11-12）：源 SVG 在 D:\DeskKit\assests，此处存其 path 数据。
    /// 两种输出：Wpf() → WPF ImageSource（窗口右键菜单）；Forms() → WinForms Image（托盘菜单）。
    /// 填充型图标走 GeometryDrawing.Brush，线性图标（图钉）走 Pen（描边）。
    /// </summary>
    public static class MenuIcons
    {
        /// <summary>图标主色（与标注工具条同款深灰蓝）</summary>
        private static readonly Color Fg = Color.FromRgb(0x44, 0x4A, 0x52);

        /// <summary>(path 数据, viewBox 边长, 描边宽 0=填充型)</summary>
        private static readonly Dictionary<string, (string D, double Vb, double Stroke)> Map = new()
        {
            // 区域和窗口截图.svg
            ["capture"] = ("M19 14h-2v3h-3v2h3v3h2v-3h3v-2h-3zM4 19h3v-2H5v-2H3v3a1 1 0 0 0 1 1M19 4a1 1 0 0 0-1-1h-3v2h2v2h2zM5 5h2V3H4a1 1 0 0 0-1 1v3h2zM3 9h2v4H3zm14 0h2v3h-2zM9 3h4v2H9zm0 14h3v2H9z", 24, 0),
            // 全屏截图.svg
            ["full"] = ("M17.5 14.5h-1.75q-.325 0-.537.213T15 15.25t.213.538t.537.212H18q.425 0 .713-.288T19 15v-2.25q0-.325-.213-.537T18.25 12t-.537.213t-.213.537zm-11-7h1.75q.325 0 .538-.213T9 6.75t-.213-.537T8.25 6H6q-.425 0-.712.288T5 7v2.25q0 .325.213.538T5.75 10t.538-.213t.212-.537zM4 19q-.825 0-1.412-.587T2 17V5q0-.825.588-1.412T4 3h16q.825 0 1.413.588T22 5v12q0 .825-.587 1.413T20 19h-4v1q0 .425-.288.713T15 21H9q-.425 0-.712-.288T8 20v-1zm0-2h16V5H4zm0 0V5z", 24, 0),
            // 剪切板内容贴图.svg
            ["pin"] = ("M20 11V5c0-1.103-.897-2-2-2h-3a1 1 0 0 0-1-1H8a1 1 0 0 0-1 1H4c-1.103 0-2 .897-2 2v13c0 1.103.897 2 2 2h7c0 1.103.897 2 2 2h7c1.103 0 2-.897 2-2v-7c0-1.103-.897-2-2-2m-9 2v5H4V5h3v2h8V5h3v6h-5c-1.103 0-2 .897-2 2m2 7v-7h7l.001 7z", 24, 0),
            // 对话框跳转.svg
            ["quickg"] = ("M7.71 3h6.79l.51.5v10l-.5.5h-13l-.5-.5v-11l.5-.5h5l.35.15zm6.28 10v-1.51l.01-4v-1.5H7.7l-.86.86l-.35.15H2v6zm-6.5-8h6.5l.01-.99H7.5l-.36-.15l-.85-.85H2v3h4.28l.86-.86zm2.29 4.07L8.42 7.7l.74-.69l2.22 2.22v.71l-2.29 2.21l-.7-.72l1.4-1.35H8.42a2 2 0 0 0-1.35.61A1.8 1.8 0 0 0 6.54 12h-1a2.76 2.76 0 0 1 .81-2a3 3 0 0 1 2-.93z", 16, 0),
            // 图钉.svg（线性描边型）
            ["topmost"] = ("M9.5 14.5L3 21M5 9.485l9.193 9.193l1.697-1.697l-.393-3.787l5.51-4.673l-5.85-5.85l-4.674 5.51l-3.786-.393z", 24, 1.5),
            // 混合网络_1794487219.svg（局域网传输：四节点网络，填充型；两角圆为显式子路径，EvenOdd 挖孔正确）
            ["lan"] = ("M26 22a3.96 3.96 0 0 0-2.02.566L17.414 16l6.567-6.567A3.95 3.95 0 0 0 26 10a4 4 0 1 0-4-4a3.95 3.95 0 0 0 .567 2.019L16 14.586L9.434 8.02A3.96 3.96 0 0 0 10 6a4 4 0 1 0-4 4a3.96 3.96 0 0 0 2.02-.566L14.586 16l-6.567 6.567A3.95 3.95 0 0 0 6 22a4 4 0 1 0 4 4a3.95 3.95 0 0 0-.567-2.019L16 17.414l6.566 6.566A3.96 3.96 0 0 0 22 26a4 4 0 1 0 4-4m0-18a2 2 0 1 1-2 2a2 2 0 0 1 2-2M6 28a2 2 0 1 1 2-2a2 2 0 0 1-2 2", 32, 0),
            // 帮助图标.svg
            ["help"] = ("M11.95 18q.525 0 .888-.363t.362-.887t-.362-.888t-.888-.362t-.887.363t-.363.887t.363.888t.887.362m-.9-3.85h1.85q0-.825.188-1.3t1.062-1.3q.65-.65 1.025-1.238T15.55 8.9q0-1.4-1.025-2.15T12.1 6q-1.425 0-2.312.75T8.55 8.55l1.65.65q.125-.45.563-.975T12.1 7.7q.8 0 1.2.438t.4.962q0 .5-.3.938t-.75.812q-1.1.975-1.35 1.475t-.25 1.825M12 22q-2.075 0-3.9-.787t-3.175-2.138T2.788 15.9T2 12t.788-3.9t2.137-3.175T8.1 2.788T12 2t3.9.788t3.175 2.137T21.213 8.1T22 12t-.788 3.9t-2.137 3.175t-3.175 2.138T12 22m0-2q3.35 0 5.675-2.325T20 12t-2.325-5.675T12 4T6.325 6.325T4 12t2.325 5.675T12 20m0-8", 24, 0),
            // 开机自启动.svg
            ["autostart"] = ("M25.542 8.354c-1.47-1.766-2.896-2.617-3.025-2.695a1.99 1.99 0 0 0-2.74.723c-.555.96-.24 2.194.706 2.763h.002l.003.003h.004c.102.062 1.124.73 2.08 1.925c1.003 1.26 1.933 3.017 1.937 5.438a9.122 9.122 0 0 1-2.64 6.438c-1.638 1.653-3.878 2.67-6.37 2.67c-2.49 0-4.73-1.017-6.368-2.67a9.105 9.105 0 0 1-2.64-6.437c.005-2.5.995-4.292 2.035-5.558a9.678 9.678 0 0 1 1.425-1.4c.19-.153.346-.264.445-.33l.104-.07l.013-.007l.004-.002a2.036 2.036 0 0 0 .705-2.763a1.991 1.991 0 0 0-2.74-.724c-.127.078-1.554.93-3.023 2.695c-1.463 1.75-2.975 4.51-2.97 8.157c0 7.263 5.825 13.152 13.01 13.155c7.186-.003 13.01-5.892 13.012-13.155c.004-3.648-1.507-6.407-2.97-8.158zM15.5 17.524a2.015 2.015 0 0 0 2.002-2.024V3.357c0-1.118-.897-2.024-2.002-2.024s-2.002.906-2.002 2.024V15.5c0 1.116.897 2.023 2.002 2.023z", 32, 0),
            // 打开保存目录.svg（viewBox 576x512，用宽做基准）
            ["folder"] = ("M384 480h48c11.4 0 21.9-6 27.6-15.9l112-192c5.8-9.9 5.8-22.1.1-32.1s-16.2-16-27.7-16H144c-11.4 0-21.9 6-27.6 15.9L48 357.1V96c0-8.8 7.2-16 16-16h117.5c4.2 0 8.3 1.7 11.3 4.7l26.5 26.5c21 21 49.5 32.8 79.2 32.8H416c8.8 0 16 7.2 16 16v32h48v-32c0-35.3-28.7-64-64-64H298.5c-17 0-33.3-6.7-45.3-18.7l-26.5-26.6c-12-12-28.3-18.7-45.3-18.7H64C28.7 32 0 60.7 0 96v320c0 35.3 28.7 64 64 64h23.7z", 576, 0),
            // 清空.svg
            ["clear"] = ("M5.75 3V1.5h4.5V3zm-1.5 0V1a1 1 0 0 1 1-1h5.5a1 1 0 0 1 1 1v2h2.5a.75.75 0 0 1 0 1.5h-.365l-.743 9.653A2 2 0 0 1 11.148 16H4.852a2 2 0 0 1-1.994-1.847L2.115 4.5H1.75a.75.75 0 0 1 0-1.5zm-.63 1.5h8.76l-.734 9.538a.5.5 0 0 1-.498.462H4.852a.5.5 0 0 1-.498-.462z", 16, 0),
            // 设置.svg（viewBox 416x432，用宽做基准）
            ["settings"] = ("m366 237l45 35q7 6 3 14l-43 74q-4 8-13 4l-53-21q-18 13-36 21l-8 56q-1 9-11 9h-85q-9 0-11-9l-8-56q-19-8-36-21l-53 21q-9 3-13-4L1 286q-4-8 3-14l45-35q-1-12-1-21t1-21L4 160q-7-6-3-14l43-74q5-8 13-4l53 21q18-13 36-21l8-56q2-9 11-9h85q10 0 11 9l8 56q19 8 36 21l53-21q9-3 13 4l43 74q4 8-3 14l-45 35q2 12 2 21t-2 21m-158.5 54q30.5 0 52.5-22t22-53t-22-53t-52.5-22t-52.5 22t-22 53t22 53t52.5 22", 416, 0),
            // 退出.svg
            ["exit"] = ("m21.207 11.793l-5.914 5.914l-1.414-1.414l3.5-3.5H7.793v-2h9.586l-3.5-3.5l1.414-1.414zm-11.414-7.5h-5v15h5v2h-7v-19h7z", 24, 0),
            // 关于.svg（circle 元素转为两段弧的整圆子路径；三点互不重叠，EvenOdd 填充正确）
            ["about"] = ("M9 10a1 1 0 0 1 2 0v4a1 1 0 1 1-2 0v-4Z M10 6a1 1 0 1 1-.001 2A1 1 0 1 1 10 6Z M2 10a8 8 0 1 0 16 0a8 8 0 0 0-16 0Zm14 0a6 6 0 1 1-12 0a6 6 0 0 1 12 0Z", 20, 0),
        };

        // ---------------- WPF：ImageSource（窗口右键菜单） ----------------

        /// <summary>按 key 取 WPF 图标（size 目标渲染边长；主色 #444A52）。</summary>
        public static ImageSource? Wpf(string key, double size = 15)
        {
            if (!Map.TryGetValue(key, out var def)) return null;
            var g = Geometry.Parse(def.D).Clone(); // Parse 返回 frozen 几何，须 Clone 才能设 Transform
            var scale = size / def.Vb;
            g.Transform = new ScaleTransform(scale, scale);
            var drawing = new GeometryDrawing { Geometry = g };
            if (def.Stroke > 0)
                drawing.Pen = new Pen(new SolidColorBrush(Fg), def.Stroke * scale)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                };
            else
                drawing.Brush = new SolidColorBrush(Fg);
            var img = new DrawingImage(drawing);
            img.Freeze();
            return img;
        }

        // ---------------- WinForms：Image（托盘菜单） ----------------

        /// <summary>按 key 取 WinForms 图标（PNG 位图，size 像素；流挂在 Tag 上保活）。
        /// leftPad/rightPad：左右透明留白（2026-11-13 二调：左 12 加大、右 0 收窄图标与文字间距）。</summary>
        public static System.Drawing.Image? Forms(string key, int size = 16, int leftPad = 12, int rightPad = 0)
        {
            var src = Wpf(key, size);
            if (src == null) return null;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawImage(src, new Rect(leftPad, 0, size, size)); // 图标画在画布中部，左右留透明空隙
            var rtb = new RenderTargetBitmap(leftPad + size + rightPad, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            var ms = new MemoryStream();
            enc.Save(ms);
            ms.Position = 0;
            var img = System.Drawing.Image.FromStream(ms);
            img.Tag = ms; // Image.FromStream 要求流在图像生命周期内保持打开
            return img;
        }

        /// <summary>透明占位位图（给无图标的条目补齐图标列宽，保持文字左对齐）。</summary>
        public static System.Drawing.Image Blank(int width, int height)
            => new System.Drawing.Bitmap(width, height);
    }
}
