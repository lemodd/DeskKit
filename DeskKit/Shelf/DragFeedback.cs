using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
// 项目开了 UseWindowsForms + ImplicitUsings，SDK 会自动 global using System.Drawing，
// 于是 Size / Point / Image 三个类型在 WPF 与 GDI+ 之间重名，这里统一消歧义。
using Size = System.Windows.Size;
using Point = System.Windows.Point;
using Image = System.Windows.Controls.Image;
using Color = System.Windows.Media.Color;

namespace ShotShelf
{
    /// <summary>
    /// 拖动排序时"松手会插到哪儿"的提示状态（2026-10-26 新增）。
    ///
    /// 用法：作为附加属性挂在 ListBoxItem 容器上（None / Left / Right）。
    /// 卡片模板里的两条蓝色竖线通过 DataTrigger 监听它，
    /// 分别在卡片左边缘 / 右边缘亮起来，指示插入位置。
    /// </summary>
    public static class DragHint
    {
        public const string None = "None";
        public const string Left = "Left";
        public const string Right = "Right";

        public static readonly DependencyProperty InsertSideProperty =
            DependencyProperty.RegisterAttached(
                "InsertSide",
                typeof(string),
                typeof(DragHint),
                new PropertyMetadata(None));

        public static string GetInsertSide(DependencyObject d) => (string)d.GetValue(InsertSideProperty);

        public static void SetInsertSide(DependencyObject d, string value) => d.SetValue(InsertSideProperty, value);
    }

    /// <summary>
    /// 插入指示竖线（2026-11-02 升级阶段2 修复：拖动排序看不到蓝线）。
    /// 【已弃用】2026-11-02 二次修复起不再使用：AdornerLayer 渲染顺序跟随被 adorn 元素的
    /// 视觉树层级（而非添加顺序），独立 Adorner 压不过幽灵；已由 DragFeedbackAdorner
    /// （幽灵+蓝线同一 Adorner）取代。类保留备查。
    ///
    /// 为什么不用卡片模板里的 MarkLeft/MarkRight：
    /// 拖动时幽灵卡片（AdornerLayer 顶层、112x88、以鼠标为中心）几乎必然压住
    /// 目标卡片边缘的指示线——线距鼠标最多半个卡片宽（约 57px），在幽灵覆盖范围内，
    /// 被半透明幽灵盖住基本看不见（原版 ShotShelf 就这样，一直没被发现）。
    /// 改成独立 Adorner 且在幽灵之后加入层（后加入者在层内更上层渲染），
    /// 全高蓝线直接压在幽灵上方，拖到哪儿都看得清。
    /// 注意：坐标以"被 adorn 的元素"（主窗口内容 Grid）为原点，
    /// 调用方用 TranslatePoint 换算好再传进来。
    /// </summary>
    public sealed class InsertCaretAdorner : Adorner
    {
        private readonly VisualCollection _children;
        private readonly Border _bar;
        private readonly TranslateTransform _pos = new();

        // 竖线外观（想调样式改这里）：4px 宽、圆角、蓝色渐变、淡投影
        private const double BarWidth = 4;

        public InsertCaretAdorner(UIElement adornedElement) : base(adornedElement)
        {
            IsHitTestVisible = false; // 不吃鼠标事件，绝不能干扰拖拽
            ClipToBounds = false;     // 允许画到 adorned element 之外（卡片 hover 放大越界时也能盖住）

            _bar = new Border
            {
                Width = BarWidth,
                CornerRadius = new CornerRadius(2),
                Background = new LinearGradientBrush(
                    Color.FromRgb(0x2D, 0x7F, 0xF9),
                    Color.FromRgb(0x5B, 0xA8, 0xFF), 90),
                Opacity = 0.95,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 6,
                    ShadowDepth = 0,
                    Opacity = 0.55,
                    Color = Colors.Black,
                },
                Visibility = Visibility.Collapsed,
            };

            _children = new VisualCollection(this);
            _children.Add(_bar);

            // 位移统一挂这一个 transform，MoveTo 只改 X/Y
            RenderTransform = _pos;
        }

        /// <summary>把竖线摆到 (x,y)（相对被 adorn 元素的坐标），并设置线高（跟随目标卡片高度）</summary>
        public void MoveTo(double x, double y, double height)
        {
            _bar.Height = Math.Max(24, height);
            _bar.Visibility = Visibility.Visible;
            _pos.X = x - BarWidth / 2; // 让线居中骑在给定的边缘坐标上
            _pos.Y = y;
        }

        /// <summary>收掉竖线（没有合法插入位置时）</summary>
        public void Hide() => _bar.Visibility = Visibility.Collapsed;

        protected override int VisualChildrenCount => _children.Count;

        protected override Visual GetVisualChild(int index) => _children[index];

        protected override Size MeasureOverride(Size constraint)
        {
            _bar.Measure(new Size(BarWidth, double.PositiveInfinity));
            return new Size(BarWidth, _bar.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _bar.Arrange(new Rect(0, 0, BarWidth, Math.Max(4, _bar.Height)));
            return finalSize;
        }
    }

    /// <summary>
    /// 拖动排序组合反馈 Adorner（2026-11-02 二次修复"拖动时一直看不到蓝线"）。
    ///
    /// 根因：阶段2 曾用独立的 InsertCaretAdorner（adorn RootGrid）+ 幽灵（adorn ListBox），
    /// 假设"后加入 AdornerLayer 的渲染在上层"——实际 WPF 的 AdornerLayer 渲染顺序
    /// 跟随被 adorn 元素的视觉树层级，而非添加顺序：幽灵 adorn 的 ListBox 在树里更深，
    /// 于是幽灵永远盖在蓝线上面；而插入线位置必然落在幽灵 112x88 覆盖范围内
    /// （距鼠标最多半个卡宽），结果蓝线从未露过面。
    ///
    /// 修复：幽灵 + 蓝线放进同一个 Adorner 的同一个 VisualCollection——
    /// 集合内严格按添加顺序渲染，蓝线后加，必定在幽灵上方。
    /// 坐标系统一为"被 adorn 的 RootGrid"（与装饰层同原点，都在窗口客户区左上角）。
    /// </summary>
    public sealed class DragFeedbackAdorner : Adorner
    {
        private readonly VisualCollection _children;
        private readonly UIElement _ghost;
        private readonly Border _caret;
        private readonly TranslateTransform _ghostPos = new();
        private readonly TranslateTransform _caretPos = new();

        // 幽灵尺寸（跟缩小后的卡片一致）
        private const double GhostWidth = 112;
        private const double GhostHeight = 88;

        // 蓝线外观：4px 宽、圆角、蓝色渐变、淡投影
        private const double CaretWidth = 4;

        public DragFeedbackAdorner(UIElement adornedElement, ImageSource? source, string? text = null)
            : base(adornedElement)
        {
            IsHitTestVisible = false; // 不吃鼠标事件，绝不能干扰拖拽
            ClipToBounds = false;     // 允许画到 adorned element 之外

            if (source != null)
            {
                // 图片条目：拎缩略图
                _ghost = new Image
                {
                    Source = source,
                    Width = GhostWidth,
                    Height = GhostHeight,
                    Stretch = Stretch.Uniform,
                    Opacity = 0.72,
                    IsHitTestVisible = false,
                    Effect = GhostShadow(),
                };
            }
            else
            {
                // 文字条目：拎一张"纸条"，上面带文字预览
                _ghost = new Border
                {
                    Width = GhostWidth,
                    Height = GhostHeight,
                    Background = new SolidColorBrush(Color.FromRgb(0xFB, 0xFB, 0xF8)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xCE)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(5, 4, 5, 4),
                    Opacity = 0.8,
                    IsHitTestVisible = false,
                    Effect = GhostShadow(),
                    Child = new TextBlock
                    {
                        Text = text ?? "",
                        FontSize = 8,
                        LineHeight = 10,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                        TextWrapping = TextWrapping.Wrap,
                        IsHitTestVisible = false,
                    },
                };
            }
            ((FrameworkElement)_ghost).RenderTransform = _ghostPos;

            // 插入指示蓝线：必须加在幽灵之后 → 渲染在幽灵上方（本次修复的关键）
            _caret = new Border
            {
                Width = CaretWidth,
                CornerRadius = new CornerRadius(2),
                Background = new LinearGradientBrush(
                    Color.FromRgb(0x2D, 0x7F, 0xF9),
                    Color.FromRgb(0x5B, 0xA8, 0xFF), 90),
                Opacity = 0.95,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 6,
                    ShadowDepth = 0,
                    Opacity = 0.55,
                    Color = Colors.Black,
                },
                Visibility = Visibility.Collapsed,
                RenderTransform = _caretPos,
            };

            _children = new VisualCollection(this);
            _children.Add(_ghost);
            _children.Add(_caret); // 顺序即渲染顺序：蓝线在上
        }

        /// <summary>淡淡的投影，让幽灵明显"浮"在卡片上方</summary>
        private static DropShadowEffect GhostShadow() => new()
        {
            BlurRadius = 14,
            ShadowDepth = 3,
            Direction = 315,
            Opacity = 0.35,
            Color = Colors.Black,
        };

        /// <summary>幽灵移到鼠标位置（参数为 RootGrid 坐标），以鼠标为中心</summary>
        public void MoveGhost(Point p)
        {
            _ghostPos.X = p.X - GhostWidth / 2;
            _ghostPos.Y = p.Y - GhostHeight / 2;
        }

        /// <summary>插入蓝线摆到 (x,y)（RootGrid 坐标），height 跟随目标卡片高度</summary>
        public void ShowCaret(double x, double y, double height)
        {
            _caret.Height = Math.Max(24, height);
            _caret.Visibility = Visibility.Visible;
            _caretPos.X = x - CaretWidth / 2; // 线居中骑在边缘坐标上
            _caretPos.Y = y;
        }

        /// <summary>收掉插入蓝线（没有合法插入位置时）</summary>
        public void HideCaret() => _caret.Visibility = Visibility.Collapsed;

        protected override int VisualChildrenCount => _children.Count;

        protected override Visual GetVisualChild(int index) => _children[index];

        protected override Size MeasureOverride(Size constraint)
        {
            _ghost.Measure(new Size(GhostWidth, GhostHeight));
            _caret.Measure(new Size(CaretWidth, double.PositiveInfinity));
            return Size.Empty; // 位置全靠 RenderTransform，自身不占布局空间
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _ghost.Arrange(new Rect(0, 0, GhostWidth, GhostHeight));
            _caret.Arrange(new Rect(0, 0, CaretWidth, Math.Max(4, _caret.Height)));
            return finalSize;
        }
    }

    /// <summary>
    /// 拖动时跟随鼠标的半透明"幽灵卡片"（2026-10-26 新增）。
    /// 【已弃用】2026-11-02 二次修复起不再使用：已并入 DragFeedbackAdorner（同 Adorner 内
    /// 保证蓝线渲染在幽灵上方）。类保留备查。
    ///
    /// 为什么用 Adorner 而不是 Popup：
    /// DragDrop.DoDragDrop 会接管消息循环，Popup 在其间刷新不可靠；
    /// Adorner 直接挂在视觉树上，只要改 Transform 就立刻重绘。
    ///
    /// 十三次更新：尺寸跟着卡片一起缩到 2/3；文字条目拎起来时显示一小段文字，
    /// 不再是空白框。
    /// </summary>
    public sealed class DragGhostAdorner : Adorner
    {
        private readonly VisualCollection _children;
        private readonly UIElement _ghost;

        /// <summary>幽灵整体偏移（复用同一个 transform，拖动时高频改 X/Y 不产垃圾）</summary>
        private readonly TranslateTransform _offset = new();

        // 幽灵尺寸（跟缩小后的卡片一致，看起来就像把卡片拎起来了）
        private const double GhostWidth = 112;
        private const double GhostHeight = 88;

        public DragGhostAdorner(UIElement adornedElement, ImageSource? source, string? text = null)
            : base(adornedElement)
        {
            IsHitTestVisible = false;  // 幽灵绝不能吃到鼠标事件，否则拖拽会断
            ClipToBounds = false;      // 允许画到 adorned element 之外

            if (source != null)
            {
                // 图片条目：拎缩略图
                _ghost = new Image
                {
                    Source = source,
                    Width = GhostWidth,
                    Height = GhostHeight,
                    Stretch = Stretch.Uniform,
                    Opacity = 0.72,
                    IsHitTestVisible = false,
                    Effect = Shadow(),
                };
            }
            else
            {
                // 文字条目：拎一张"纸条"，上面带文字预览
                _ghost = new Border
                {
                    Width = GhostWidth,
                    Height = GhostHeight,
                    Background = new SolidColorBrush(Color.FromRgb(0xFB, 0xFB, 0xF8)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xCE)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(5, 4, 5, 4),
                    Opacity = 0.8,
                    IsHitTestVisible = false,
                    Effect = Shadow(),
                    Child = new TextBlock
                    {
                        Text = text ?? "",
                        FontSize = 8,
                        LineHeight = 10,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                        TextWrapping = TextWrapping.Wrap,
                        IsHitTestVisible = false,
                    },
                };
            }

            _children = new VisualCollection(this);
            _children.Add(_ghost);

            // 整块幽灵的位移统一挂在这里，MoveTo 只改 X/Y
            RenderTransform = _offset;
        }

        /// <summary>淡淡的投影，让幽灵明显"浮"在卡片上方</summary>
        private static DropShadowEffect Shadow() => new()
        {
            BlurRadius = 14,
            ShadowDepth = 3,
            Direction = 315,
            Opacity = 0.35,
            Color = Colors.Black,
        };

        protected override int VisualChildrenCount => _children.Count;

        protected override Visual GetVisualChild(int index) => _children[index];

        protected override Size MeasureOverride(Size constraint)
        {
            _ghost.Measure(new Size(GhostWidth, GhostHeight));
            return new Size(GhostWidth, GhostHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _ghost.Arrange(new Rect(0, 0, GhostWidth, GhostHeight));
            return finalSize;
        }

        /// <summary>
        /// 把幽灵移到鼠标位置。参数为相对 AdornerLayer 的坐标；
        /// 幽灵以鼠标为中心，视觉上等于"拎着卡片走"。
        /// </summary>
        public void MoveTo(Point layerPoint)
        {
            _offset.X = layerPoint.X - GhostWidth / 2;
            _offset.Y = layerPoint.Y - GhostHeight / 2;
        }
    }
}
