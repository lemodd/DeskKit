using System.Windows;
using System.Windows.Media;

namespace MiniPix.Core
{
    /// <summary>
    /// 三线开放式箭头几何（2026-11-04 统一：截图/贴图/预览三处共用同一实现）。
    /// 三条线：杆（a→b）+ 两条倒刺（从尖端 b 向后张开），不填充、无封口线。
    /// 头部大小随线宽：thickness×6，下限 12 上限 56（细=12 / 中=24 / 粗=48），
    /// 张角约 30°（后向 0.85 / 侧向 0.5）——原各处自带的版本头只有 6~14px，视觉上像"单侧/无头"。
    /// </summary>
    public static class ArrowGeom
    {
        public static Geometry Build(Point a, Point b, double thickness)
        {
            var v = b - a;
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            if (len < 0.01) return Geometry.Empty;
            var u = new Vector(v.X / len, v.Y / len);   // 杆方向单位向量
            var n = new Vector(-u.Y, u.X);              // 法向
            double head = Math.Clamp(thickness * 6, 12, 56);
            var pL = b - u * (head * 0.85) + n * (head * 0.5); // 左倒刺端点
            var pR = b - u * (head * 0.85) - n * (head * 0.5); // 右倒刺端点
            var g = new GeometryGroup();
            g.Children.Add(new LineGeometry(a, b));
            g.Children.Add(new LineGeometry(b, pL));
            g.Children.Add(new LineGeometry(b, pR));
            return g;
        }
    }
}
