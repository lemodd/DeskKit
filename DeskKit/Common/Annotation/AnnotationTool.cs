using System.Windows.Media;

namespace ShotShelf.Annotation
{
    /// <summary>注释工具类型（与截图/贴图工具条一致，2026-11-02 升级阶段4）</summary>
    public enum AnnTool
    {
        None,     // 未选工具（滚轮缩放/平移正常，画布不拦截鼠标）
        Rect,     // 矩形
        Ellipse,  // 椭圆
        Arrow,    // 箭头
        Pen,      // 画笔
        Mosaic,   // 马赛克
        Text,     // 文字
        Number,   // 带圈数字编号（点击放置 ①②③…，自动递增；2026-11-02 新增）
    }

    /// <summary>
    /// 注释绘制默认常量（可调项集中在这里）。
    /// 数值与截图/贴图工具条保持一致，来源 PinWindow/CaptureWindow。
    /// </summary>
    public static class AnnDefaults
    {
        /// <summary>7 色调色板（红 橙 黄 绿 蓝 黑 白）</summary>
        public static readonly Color[] Palette =
        {
            Color.FromRgb(0xE0, 0x40, 0x3A),
            Color.FromRgb(0xF0, 0xA0, 0x20),
            Color.FromRgb(0xF5, 0xC5, 0x11),
            Color.FromRgb(0x33, 0xA9, 0x4C),
            Color.FromRgb(0x1E, 0x88, 0xE5),
            Color.FromRgb(0x00, 0x00, 0x00),
            Color.FromRgb(0xFF, 0xFF, 0xFF),
        };

        /// <summary>线宽三档（细/中/粗）</summary>
        public static readonly double[] Thicknesses = { 2, 4, 8 };

        /// <summary>文字字号</summary>
        public const double FontSize = 16;

        /// <summary>马赛克强度（缩得越小块越大）</summary>
        public const double MosaicFactor = 6;

        /// <summary>带圈数字编号：圆圈直径（DIP，两位数时宽自动加 10）</summary>
        public const double NumberSize = 26;

        /// <summary>带圈数字编号：字号</summary>
        public const double NumberFontSize = 13;
    }
}
