// Icons.cs — Material Symbols Rounded 图标几何库（2026-11-04 全程序统一）
// 路径数据取自 Google material-design-icons 官方仓库（Apache 2.0 免费商用），
// 与 icon.sucai999.com 同源；viewBox = 0 -960 960 960，用 Path Stretch=Uniform 显示。
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DeskKit
{
    public static class Icons
    {
        private static Geometry F(string d) { var g = Geometry.Parse(d); g.Freeze(); return g; }

        /// <summary>F 的旋转变体：绕 (12,12) 顺时针转 angle 度（24 系图标用）。
        /// 坑：Geometry.Parse 返回的几何体是只读的，必须 Clone 后才能设 Transform（2026-11-12 实锤）</summary>
        private static Geometry FRot(string d, double angle)
        {
            var g = Geometry.Parse(d).Clone();
            g.Transform = new RotateTransform(angle, 12, 12);
            g.Freeze();
            return g;
        }

        /// <summary>保存 (save)</summary>
        public static readonly Geometry Save = F("M200-120q-33 0-56.5-23.5T120-200v-560q0-33 23.5-56.5T200-840h447q16 0 30.5 6t25.5 17l114 114q11 11 17 25.5t6 30.5v447q0 33-23.5 56.5T760-120H200Zm560-526L646-760H200v560h560v-446ZM480-240q50 0 85-35t35-85q0-50-35-85t-85-35q-50 0-85 35t-35 85q0 50 35 85t85 35ZM280-560h280q17 0 28.5-11.5T600-600v-80q0-17-11.5-28.5T560-720H280q-17 0-28.5 11.5T240-680v80q0 17 11.5 28.5T280-560Zm-80-86v446-560 114Z");

        /// <summary>关闭 (close)</summary>
        public static readonly Geometry Close = F("M480-424 284-228q-11 11-28 11t-28-11q-11-11-11-28t11-28l196-196-196-196q-11-11-11-28t11-28q11-11 28-11t28 11l196 196 196-196q11-11 28-11t28 11q11 11 11 28t-11 28L536-480l196 196q11 11 11 28t-11 28q-11 11-28 11t-28-11L480-424Z");

        /// <summary>删除 (delete)</summary>
        public static readonly Geometry Delete = F("M280-120q-33 0-56.5-23.5T200-200v-520q-17 0-28.5-11.5T160-760q0-17 11.5-28.5T200-800h160q0-17 11.5-28.5T400-840h160q17 0 28.5 11.5T600-800h160q17 0 28.5 11.5T800-760q0 17-11.5 28.5T760-720v520q0 33-23.5 56.5T680-120H280Zm400-600H280v520h400v-520ZM400-280q17 0 28.5-11.5T440-320v-280q0-17-11.5-28.5T400-640q-17 0-28.5 11.5T360-600v280q0 17 11.5 28.5T400-280Zm160 0q17 0 28.5-11.5T600-320v-280q0-17-11.5-28.5T560-640q-17 0-28.5 11.5T520-600v280q0 17 11.5 28.5T560-280ZM280-720v520-520Z");

        /// <summary>另存下载 (download)</summary>
        public static readonly Geometry Download = F("M480-337q-8 0-15-2.5t-13-8.5L308-492q-12-12-11.5-28t11.5-28q12-12 28.5-12.5T365-549l75 75v-286q0-17 11.5-28.5T480-800q17 0 28.5 11.5T520-760v286l75-75q12-12 28.5-11.5T652-548q11 12 11.5 28T652-492L508-348q-6 6-13 8.5t-15 2.5ZM240-160q-33 0-56.5-23.5T160-240v-80q0-17 11.5-28.5T200-360q17 0 28.5 11.5T240-320v80h480v-80q0-17 11.5-28.5T760-360q17 0 28.5 11.5T800-320v80q0 33-23.5 56.5T720-160H240Z");

        /// <summary>长截图（2026-11-12 用户提供 long.svg：四角框选，viewBox 0 0 24 24，
        /// 2026-11-12 二修：中间加向下箭头子路径，配合 Stroke 加粗渲染）</summary>
        public static readonly Geometry Long = F("M6 7V4.616q0-.691.463-1.153T7.616 3H10v1H7.616q-.231 0-.424.192T7 4.615V7zm1.616 14q-.691 0-1.153-.462T6 19.385V17h1v2.385q0 .23.192.423t.423.192H10v1zM17 7V4.616q0-.231-.192-.424T16.384 4H14V3h2.385q.69 0 1.152.463T18 4.615V7zm-3 14v-1h2.385q.23 0 .423-.192t.192-.424V17h1v2.385q0 .69-.462 1.152T16.384 21zM12 8.2v6M9.7 11.9L12 14.2l2.3-2.3");

        /// <summary>复制 (content_copy)</summary>
        public static readonly Geometry Copy = F("M360-240q-33 0-56.5-23.5T280-320v-480q0-33 23.5-56.5T360-880h360q33 0 56.5 23.5T800-800v480q0 33-23.5 56.5T720-240H360Zm0-80h360v-480H360v480ZM200-80q-33 0-56.5-23.5T120-160v-520q0-17 11.5-28.5T160-720q17 0 28.5 11.5T200-680v520h400q17 0 28.5 11.5T640-120q0 17-11.5 28.5T600-80H200Zm160-240v-480 480Z");

        /// <summary>撤销 (undo)</summary>
        public static readonly Geometry Undo = F("M320-200q-17 0-28.5-11.5T280-240q0-17 11.5-28.5T320-280h244q63 0 109.5-40T720-420q0-60-46.5-100T564-560H312l76 76q11 11 11 28t-11 28q-11 11-28 11t-28-11L188-572q-6-6-8.5-13t-2.5-15q0-8 2.5-15t8.5-13l144-144q11-11 28-11t28 11q11 11 11 28t-11 28l-76 76h252q97 0 166.5 63T800-420q0 94-69.5 157T564-200H320Z");

        /// <summary>文字 (title)</summary>
        public static readonly Geometry Text = F("M420-680H260q-25 0-42.5-17.5T200-740q0-25 17.5-42.5T260-800h440q25 0 42.5 17.5T760-740q0 25-17.5 42.5T700-680H540v460q0 25-17.5 42.5T480-160q-25 0-42.5-17.5T420-220v-460Z");

        /// <summary>椭圆 (radio_button_unchecked)</summary>
        public static readonly Geometry Ellipse = F("M480-80q-83 0-156-31.5T197-197q-54-54-85.5-127T80-480q0-83 31.5-156T197-763q54-54 127-85.5T480-880q83 0 156 31.5T763-763q54 54 85.5 127T880-480q0 83-31.5 156T763-197q-54 54-127 85.5T480-80Zm0-80q134 0 227-93t93-227q0-134-93-227t-227-93q-134 0-227 93t-93 227q0 134 93 227t227 93Zm0-320Z");

        /// <summary>画笔 (edit)</summary>
        public static readonly Geometry Pen = F("M200-200h57l391-391-57-57-391 391v57Zm-40 80q-17 0-28.5-11.5T120-160v-97q0-16 6-30.5t17-25.5l505-504q12-11 26.5-17t30.5-6q16 0 31 6t26 18l55 56q12 11 17.5 26t5.5 30q0 16-5.5 30.5T817-647L313-143q-11 11-25.5 17t-30.5 6h-97Zm600-584-56-56 56 56Zm-141 85-28-29 57 57-29-28Z");

        /// <summary>矩形 (crop_square)</summary>
        public static readonly Geometry Rect = F("M200-120q-33 0-56.5-23.5T120-200v-560q0-33 23.5-56.5T200-840h560q33 0 56.5 23.5T840-760v560q0 33-23.5 56.5T760-120H200Zm0-80h560v-560H200v560Zm0 0v-560 560Z");

        /// <summary>箭头 (north_east)</summary>
        public static readonly Geometry Arrow = F("M680-624 244-188q-11 11-28 11t-28-11q-11-11-11-28t11-28l436-436H400q-17 0-28.5-11.5T360-720q0-17 11.5-28.5T400-760h320q17 0 28.5 11.5T760-720v320q0 17-11.5 28.5T720-360q-17 0-28.5-11.5T680-400v-224Z");

        /// <summary>线宽三档图标（2026-11-12 用户要求：单条横杠按细/中/粗不同厚度，
        /// 当前档位显示对应图标；960 系坐标，垂直居中于 -480）</summary>
        public static readonly Geometry[] LineWidthSet =
        {
            F("M120-486h720v36H120z"),    // 细（36u ≈ 0.6px@15，2026-11-12 用户反馈 24u 太浅发灰，加厚）
            F("M120-516h720v72H120z"),    // 中（72u ≈ 1.1px@15）
            F("M120-564h720v168H120z"),   // 粗（168u ≈ 2.6px@15）
        };

        /// <summary>编号 (looks_one)</summary>
        public static readonly Geometry Number = F("M480-600v280q0 17 11.5 28.5T520-280q17 0 28.5-11.5T560-320v-320q0-17-11.5-28.5T520-680h-80q-17 0-28.5 11.5T400-640q0 17 11.5 28.5T440-600h40ZM200-120q-33 0-56.5-23.5T120-200v-560q0-33 23.5-56.5T200-840h560q33 0 56.5 23.5T840-760v560q0 33-23.5 56.5T760-120H200Zm0-80h560v-560H200v560Zm0-560v560-560Z");

        /// <summary>送暂存架 (inventory_2)</summary>
        public static readonly Geometry Send = F("M200-80q-33 0-56.5-23.5T120-160v-451q-18-11-29-28.5T80-680v-120q0-33 23.5-56.5T160-880h640q33 0 56.5 23.5T880-800v120q0 23-11 40.5T840-611v451q0 33-23.5 56.5T760-80H200Zm0-520v440h560v-440H200Zm-40-80h640v-120H160v120Zm240 280h160q17 0 28.5-11.5T600-440q0-17-11.5-28.5T560-480H400q-17 0-28.5 11.5T360-440q0 17 11.5 28.5T400-400Zm80 20Z");

        /// <summary>重做 (redo)</summary>
        public static readonly Geometry Redo = F("M648-560H396q-63 0-109.5 40T240-420q0 60 46.5 100T396-280h244q17 0 28.5 11.5T680-240q0 17-11.5 28.5T640-200H396q-97 0-166.5-63T160-420q0-94 69.5-157T396-640h252l-76-76q-11-11-11-28t11-28q11-11 28-11t28 11l144 144q6 6 8.5 13t2.5 15q0 8-2.5 15t-8.5 13L628-428q-11 11-28 11t-28-11q-11-11-11-28t11-28l76-76Z");

        /// <summary>颜色（2026-11-12 用户提供 color.svg：油漆刷，viewBox 0 0 256 256，
        /// Stretch=Uniform 自适应，与其他图标混用无碍）</summary>
        public static readonly Geometry Palette = F("M229.23 26.77a30.05 30.05 0 0 0-42.43 0l-.32.34l-54.81 64.05l-12.11-12.1a22 22 0 0 0-31.11 0l-76.69 76.69a6 6 0 0 0 0 8.49l80 80a6 6 0 0 0 8.49 0l76.69-76.69a22 22 0 0 0 0-31.11l-12.1-12.11l64-54.81l.34-.32a30.05 30.05 0 0 0 .05-42.43M96 231.51L76.49 212l23.76-23.76a6 6 0 0 0-8.49-8.49L68 203.51L52.49 188l23.76-23.76a6 6 0 0 0-8.49-8.49L44 179.51L24.49 160L72 112.48L143.52 184ZM220.89 60.56l-68.78 58.87a6 6 0 0 0-2.1 4.33a6 6 0 0 0 1.76 4.47l16.68 16.69a10 10 0 0 1 0 14.15L152 175.51L80.49 104l16.44-16.45a10 10 0 0 1 14.15 0l16.69 16.68a6 6 0 0 0 8.8-.34l58.87-68.78a18 18 0 0 1 25.45 25.45");

        /// <summary>钉贴图 (push_pin)；2026-11-12 用户要求：向右倾斜 45°（绕中心顺时针旋转）</summary>
        public static readonly Geometry Pin = FRot("m16 12 2 2v2h-5v6l-1 1-1-1v-6H6v-2l2-2V5H7V3h10v2h-1Zm-7.15 2h6.3L14 12.85V5h-4v7.85ZM12 14Z", 45);

        /// <summary>马赛克（2026-11-12 用户提供 马赛克设计_1794445488.svg：棋盘格，viewBox 0 0 48 48）</summary>
        public static readonly Geometry Mosaic = F("M44 36h-8v8h8zm-16 0h-8v8h8zm-16 0H4v8h8zm32-16h-8v8h8zm-16 0h-8v8h8zm-16 0H4v8h8zM44 4h-8v8h8zM28 4h-8v8h8zM12 4H4v8h8zm8 8h-8v8h8zm0 16h-8v8h8zm16-16h-8v8h8zm0 16h-8v8h8z");

        /// <summary>OCR 识文（2026-11-12 用户提供 ocr.svg：文档扫描，viewBox 0 0 24 24）</summary>
        public static readonly Geometry Ocr = F("M5.25 3.5A1.75 1.75 0 0 0 3.5 5.25v3a.75.75 0 0 1-1.5 0v-3A3.25 3.25 0 0 1 5.25 2h3a.75.75 0 0 1 0 1.5zm0 17a1.75 1.75 0 0 1-1.75-1.75v-3a.75.75 0 0 0-1.5 0v3A3.25 3.25 0 0 0 5.25 22h3a.75.75 0 0 0 0-1.5zM20.5 5.25a1.75 1.75 0 0 0-1.75-1.75h-3a.75.75 0 0 1 0-1.5h3A3.25 3.25 0 0 1 22 5.25v3a.75.75 0 0 1-1.5 0zM18.75 20.5a1.75 1.75 0 0 0 1.75-1.75v-3a.75.75 0 0 1 1.5 0v3A3.25 3.25 0 0 1 18.75 22h-3a.75.75 0 0 1 0-1.5zM7 7.75A.75.75 0 0 1 7.75 7h8.5a.75.75 0 0 1 .75.75V9a.75.75 0 0 1-1.5 0v-.5h-2.75v7h1.5a.75.75 0 0 1 0 1.5h-4.5a.75.75 0 0 1 0-1.5h1.5v-7H8.5V9A.75.75 0 0 1 7 9z");

        /// <summary>顺时针旋转（2026-11-12 用户提供 图片右旋转_1794447106.svg，viewBox 0 0 20 20）</summary>
        /// <summary>顺时针旋转（2026-11-12 用户提供 右上旋转_1794447174.svg：圆角方块+箭头，
        /// stroke 轮廓型 fill=none，渲染需 strokeOnly 模式——Fill 不绑色、只绑 Stroke）</summary>
        public static readonly Geometry RotateCw = F("M7 15c0 3.3 0 4.95 1.025 5.975S10.7 22 14 22s4.95 0 5.975-1.025S21 18.3 21 15s0-4.95-1.025-5.975S17.3 8 14 8S9.05 8 8.025 9.025S7 11.7 7 15M9.58 2l1.346 1.11c.716.591 1.074.886 1.074 1.254h-1c-3.771 0-5.657 0-6.828 1.171S3 8.592 3 12.364V13");

        /// <summary>裁剪（2026-11-12 用户提供 图片裁剪_1794454849.svg：双角裁剪框，fill 型）</summary>
        public static readonly Geometry Crop = F("M88.231 71.062h-7.264V23.959h-.051c.003-.07.021-.135.021-.206a4.773 4.773 0 0 0-4.772-4.772H28.99v-7.068a2.693 2.693 0 0 0-2.693-2.693h-4.571a2.692 2.692 0 0 0-2.693 2.693v7.068h-7.264a2.693 2.693 0 0 0-2.693 2.693v4.571a2.692 2.692 0 0 0 2.693 2.693h7.264v47.103h.052c-.003.07-.021.135-.021.206a4.773 4.773 0 0 0 4.772 4.772H71.01v7.068a2.693 2.693 0 0 0 2.693 2.693h4.571a2.692 2.692 0 0 0 2.693-2.693v-7.068h7.264a2.693 2.693 0 0 0 2.693-2.693v-4.571a2.693 2.693 0 0 0-2.693-2.693m-59.241.001V28.937h42.02v42.125H28.99z");

        /// <summary>生成图标 Path：Fill 绑定宿主按钮的 Foreground（颜色钮变色/选中态自动跟随）。
        /// 2026-11-12 二修：stroke &gt; 0 时启用描边加粗（Fill+Stroke 同色叠加，视觉线宽加大，
        /// Stroke 画刷由调用方绑定到与 Fill 相同的 Foreground）。注意 WPF Stretch 变换只缩放几何、
        /// 不缩放 Pen，StrokeThickness 单位即最终显示像素。</summary>
        public static Path MakePath(Geometry g, double size = 15, double stroke = 0)
        {
            var p = new Path
            {
                Data = g, Stretch = Stretch.Uniform, Width = size, Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (stroke > 0)
            {
                p.StrokeThickness = stroke;
                p.StrokeLineJoin = PenLineJoin.Round;
                p.StrokeStartLineCap = PenLineCap.Round;
                p.StrokeEndLineCap = PenLineCap.Round;
            }
            return p;
        }
    }
}
