// AnnotationEditor.cs — 预览窗注释编辑器（2026-11-02 升级阶段4）
// 纯代码控件（无 XAML），以 PinWindow 的注释绘制段为蓝本抽出，
// 不依赖截图状态机，可被任意"底图 + 覆盖画布"宿主复用（后期 OCR 也走这里）。
//
// 宿主接入方式（PreviewWindow 为例）：
//   1. 把本控件放进与底图 Image 同一格、且共用同一个 LayoutTransform 的 Grid 里
//      → 画布坐标 = 图片自然 DIP 坐标，形状随缩放自然缩放；
//   2. ToolbarView 放到任意行（窗口底部），空格键由宿主切换可见性；
//   3. 复制/另存时调 Composite(baseImage) 合成"原图 + 注释"。
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MiniPix.Output;

namespace ShotShelf.Annotation
{
    public class AnnotationEditor : Grid
    {
        private readonly BitmapSource _baseImage;   // 底图（马赛克取样 + 合成用）
        private readonly Canvas _canvas;            // 注释画布（坐标 = 图片自然 DIP）

        // ---- 工具状态 ----
        private AnnTool _tool = AnnTool.None;       // 当前选中工具
        private AnnTool _activeTool;                // 笔画中途锁定的工具
        private int _colorIdx;                      // 当前颜色（默认红）
        private int _widthIdx;                      // 线宽档位（细）
        private double AnnThickness => AnnDefaults.Thicknesses[_widthIdx];

        // ---- 绘制中状态 ----
        private UIElement? _drawing;
        private Point _down;
        private readonly List<Point> _penPoints = new();
        private Polyline? _pen;
        private System.Windows.Shapes.Rectangle? _mosaicTmp;
        private TextBox? _textBox;
        private int _nextNumber = 1;              // 编号工具：下一个要放的数字（撤销放号后回退）

        // ---- 已完成形状 / 重做栈 ----
        private readonly List<UIElement> _shapes = new();
        private readonly List<UIElement> _redo = new();

        // ---- 工具条 UI ----
        private readonly Dictionary<AnnTool, Button> _toolButtons = new();
        private Button? _tbColor, _tbWidth;
        private Popup? _colorPopup;

        /// <summary>注释内容变化（新增/撤销/重做/文字提交）后触发，宿主可刷新状态栏</summary>
        public event Action? StateChanged;

        /// <summary>马赛克取样比例覆盖值（源像素 / 显示 DIP）。null = 按画布自然尺寸自动计算</summary>
        private double? _kxOverride, _kyOverride;

        public AnnotationEditor(BitmapSource baseImage)
        {
            _baseImage = baseImage;

            // 画布铺满宿主 Grid（与底图同尺寸），透明背景不挡视觉。
            // 2026-11-02 UI 美化反馈：ClipToBounds——注释笔画不允许画出图片范围
            // （Canvas 默认不裁剪，拖到图片外笔画会飘到灰色背景上，很不协调）
            _canvas = new Canvas
            {
                Background = Brushes.Transparent,
                IsHitTestVisible = false, // 未选工具时不拦鼠标（缩放/平移照常）
                ClipToBounds = true,
            };
            ClipToBounds = true; // 编辑器根 Grid 也裁剪，双保险
            Children.Add(_canvas);

            // 绘制手势：左键 + 捕获鼠标（画出画布外也能继续跟踪）
            _canvas.MouseLeftButtonDown += OnCanvasDown;
            _canvas.MouseMove += OnCanvasMove;
            _canvas.MouseLeftButtonUp += OnCanvasUp;

            BuildToolbar();
        }

        // ================= 公共 API =================

        /// <summary>当前选中工具（None = 注释模式关闭）。设置时同步工具条高亮和画布命中</summary>
        public AnnTool SelectedTool
        {
            get => _tool;
            set { _tool = value; HighlightTool(); _canvas.IsHitTestVisible = value != AnnTool.None; }
        }

        /// <summary>内置工具条（宿主放到任意位置；默认收起）</summary>
        public FrameworkElement ToolbarView { get; private set; } = null!;

        /// <summary>开关工具条；收起时自动取消选中工具并提交未完成的文字</summary>
        public void SetToolbarVisible(bool visible)
        {
            ToolbarView.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
            {
                SelectedTool = AnnTool.None;
                if (_colorPopup is { IsOpen: true }) _colorPopup.IsOpen = false;
                CommitText();
            }
        }

        /// <summary>是否有已提交的注释（宿主复制/另存前判断，决定是否合成）</summary>
        public bool HasAnnotations
        {
            get { CommitText(); return _canvas.Children.Count > 0; }
        }

        /// <summary>
        /// 马赛克取样比例覆盖（源像素 / 显示 DIP）。
        /// 宿主画布若与底图不同布局（如画布不在缩放变换内），传入
        /// kx = PixelWidth / (ActualWidth * zoom)；画布与底图共用 LayoutTransform 时无需调用。
        /// </summary>
        public void SetSampleScale(double kx, double ky) { _kxOverride = kx; _kyOverride = ky; }

        /// <summary>撤销最后一笔（无注释时忽略）</summary>
        public void Undo()
        {
            CommitText();
            if (_colorPopup is { IsOpen: true }) _colorPopup.IsOpen = false;
            if (_canvas.Children.Count == 0) return;
            var last = _canvas.Children[^1];
            _canvas.Children.RemoveAt(_canvas.Children.Count - 1);
            _shapes.Remove(last);
            _redo.Add(last);
            // 2026-11-02 编号工具：撤销刚放的编号 → 序号回退（下次放号重用这个号）；
            // 画布清空（编号画完又全撤销）→ 序号归 1
            if (last is Grid { Tag: int nn } && nn == _nextNumber - 1) _nextNumber--;
            if (_canvas.Children.Count == 0) _nextNumber = 1;
            StateChanged?.Invoke();
        }

        /// <summary>重做最近一次撤销的笔画</summary>
        public void Redo()
        {
            if (_redo.Count == 0) return;
            var item = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _canvas.Children.Add(item);
            _shapes.Add(item);
            StateChanged?.Invoke();
        }

        /// <summary>
        /// 合成"原图 + 注释"的最终图（无注释时直接返回原图引用）。
        /// 画布按自然 DIP 尺寸光栅化，再缩放回底图像素尺寸 → 与显示缩放无关。
        /// </summary>
        public BitmapSource Composite(BitmapSource baseImage)
        {
            CommitText();
            if (_canvas.Children.Count == 0) return baseImage;
            double dispW = _canvas.ActualWidth, dispH = _canvas.ActualHeight;
            if (dispW < 1 || dispH < 1) return baseImage;
            int pw = baseImage.PixelWidth, ph = baseImage.PixelHeight;

            // 1) 注释画布 → 自然尺寸的透明位图
            var annBmp = new RenderTargetBitmap(
                (int)Math.Round(dispW), (int)Math.Round(dispH), 96, 96, PixelFormats.Pbgra32);
            annBmp.Render(_canvas);

            // 2) 原图 + 缩放后的注释 → 像素尺寸成品
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(baseImage, new Rect(0, 0, pw, ph));
                dc.PushTransform(new ScaleTransform(pw / dispW, ph / dispH));
                dc.DrawImage(annBmp, new Rect(0, 0, dispW, dispH));
                dc.Pop();
            }
            var outBmp = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            outBmp.Render(visual);
            return outBmp;
        }

        // ================= 工具条（纯代码，样式同截图/贴图 CapBtn） =================

        private void BuildToolbar()
        {
            // 2026-11-04 图标统一：文字按钮（现仅"线宽"用，细/中/粗文字比图标直观）
            Button Tb(string content, string tip, RoutedEventHandler click)
            {
                var b = new Button
                {
                    Content = content,
                    Width = 28, Height = 26,
                    FontSize = 13,
                    FontFamily = new FontFamily("Microsoft YaHei, Segoe UI"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)), // 2026-11-02：深灰字（浅色工具条配深色图标；原白色配深底）
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Focusable = false,           // 关键：不抢焦点，空格键才能留给宿主切换工具条
                    Cursor = Cursors.Hand,
                    ToolTip = tip,
                };
                b.Click += click;
                return b;
            }

            // 2026-11-04 图标统一：矢量图标按钮（Material Symbols Rounded，DeskKit.Icons 几何库）
            // 图标 Path 的 Fill 绑定按钮 Foreground → 颜色钮换色时图标自动跟随变色
            // 2026-11-12 四改：TbI 提为私有方法（AddHostButton 也要用），见类尾部

            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
            _tbPanel = sp;   // 2026-11-12 四改：留引用给 AddHostButton

            // 2026-11-04 线宽按钮 tooltip：显示当前档位（细/中/粗）
            static string WidthTip(int idx) => $"线宽：{new[] { "细", "中", "粗" }[idx]} (点击切换)";

            sp.Children.Add(_toolButtons[AnnTool.Rect] = TbI(DeskKit.Icons.Rect, "矩形 (R)", (_, _) => Toggle(AnnTool.Rect)));
            sp.Children.Add(_toolButtons[AnnTool.Ellipse] = TbI(DeskKit.Icons.Ellipse, "椭圆 (O)", (_, _) => Toggle(AnnTool.Ellipse)));
            sp.Children.Add(_toolButtons[AnnTool.Arrow] = TbI(DeskKit.Icons.Arrow, "箭头 (A)", (_, _) => Toggle(AnnTool.Arrow)));
            sp.Children.Add(_toolButtons[AnnTool.Pen] = TbI(DeskKit.Icons.Pen, "画笔 (D)", (_, _) => Toggle(AnnTool.Pen)));
            sp.Children.Add(_toolButtons[AnnTool.Text] = TbI(DeskKit.Icons.Text, "文字 (T，点击图上位置输入，Enter 完成)", (_, _) => Toggle(AnnTool.Text)));
            sp.Children.Add(_toolButtons[AnnTool.Mosaic] = TbI(DeskKit.Icons.Mosaic, "马赛克 (B)", (_, _) => Toggle(AnnTool.Mosaic)));
            // 2026-11-02 新增：带圈数字编号（点击放置 ①②③… 自动递增）
            sp.Children.Add(_toolButtons[AnnTool.Number] = TbI(DeskKit.Icons.Number, "数字编号：点击图上位置放一个带圈数字，自动 1、2、3… 递增", (_, _) => Toggle(AnnTool.Number)));
            // 2026-11-12 三修：颜色图标放大到 18 + stroke 加粗线宽
            _tbColor = TbI(DeskKit.Icons.Palette, "颜色", (_, _) => ShowColorPopup(), stroke: 0.6, size: 18);
            _tbColor.Foreground = new SolidColorBrush(AnnDefaults.Palette[_colorIdx]);
            // 2026-11-12 用户要求：颜色图标不跟随当前颜色（不再变红），固定与其他图标一致的灰色；
            // Foreground 仍记录当前颜色供换色逻辑使用，只解绑图标的 Fill/Stroke
            if (_tbColor.Content is Path cpColor)
            {
                System.Windows.Data.BindingOperations.ClearBinding(cpColor, Path.FillProperty);
                System.Windows.Data.BindingOperations.ClearBinding(cpColor, Path.StrokeProperty);
                var gray = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52));
                cpColor.Fill = gray;
                cpColor.Stroke = gray;
            }
            sp.Children.Add(_tbColor);
            // 2026-11-04 用户要求：线宽也用图标，当前档位在 tooltip 显示
            // 2026-11-12 二改：三档各用一个图标（横杠细/中/粗），切换时同步换图标
            _tbWidth = TbI(DeskKit.Icons.LineWidthSet[_widthIdx], WidthTip(_widthIdx), (_, _) =>
            {
                _widthIdx = (_widthIdx + 1) % AnnDefaults.Thicknesses.Length;
                _tbWidth.ToolTip = WidthTip(_widthIdx);
                if (_tbWidth.Content is Path p) p.Data = DeskKit.Icons.LineWidthSet[_widthIdx];
            });
            sp.Children.Add(_tbWidth);
            sp.Children.Add(TbI(DeskKit.Icons.Undo, "撤销 (Ctrl+Z)", (_, _) => Undo()));
            sp.Children.Add(TbI(DeskKit.Icons.Redo, "重做 (Ctrl+Y)", (_, _) => Redo()));
            sp.Children.Add(TbI(DeskKit.Icons.Send, "把标注图送入暂存架（新条目，原图保留）", (_, _) => RequestToggleToolbar?.Invoke()));

            ToolbarView = new Border
            {
                // 2026-11-02：改浅色与预览窗底部栏协调（原深色 #E6222222 + 蓝边，浮在截图上用；此处常驻浅色底部栏）
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(4),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE1)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(4),
                Child = sp,
                Visibility = Visibility.Collapsed,
            };
            // 点工具条空白不落到下层（防误触发滚动/缩放）
            ToolbarView.MouseLeftButtonDown += (_, e) => e.Handled = true;
        }

        /// <summary>宿主实现"收起工具条"（编辑器不拥有宿主布局）</summary>
        public event Action? RequestToggleToolbar;

        // ---- 2026-11-12 四改：工具条按钮工厂提为私有方法 + 宿主追加按钮入口 ----
        private Panel? _tbPanel;   // 工具条按钮容器（BuildToolbar 里赋值）

        /// <summary>矢量图标按钮（与注释工具同款风格）：Fill 绑 Foreground；
        /// stroke &gt; 0 时同时绑 Stroke（加粗）；size 单独调视觉大小</summary>
        private Button TbI(Geometry g, string tip, RoutedEventHandler click, double stroke = 0, double size = 15, bool strokeOnly = false)
        {
            var path = DeskKit.Icons.MakePath(g, size, stroke);
            var b = new Button
            {
                Width = 28, Height = 26,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Focusable = false,
                Cursor = Cursors.Hand,
                ToolTip = tip,
            };
            // 2026-11-12 strokeOnly：描边轮廓型图标（fill=none 的 SVG），Fill 不绑色防内部糊满，
            // 只把 Stroke 绑到 Foreground（常规图标仍 Fill+Stroke 双绑）
            if (strokeOnly)
                path.SetBinding(Path.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = b });
            else
            {
                path.SetBinding(Path.FillProperty, new System.Windows.Data.Binding("Foreground") { Source = b });
                if (stroke > 0)
                    path.SetBinding(Path.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = b });
            }
            b.Content = path;
            b.Click += click;
            return b;
        }

        /// <summary>宿主向注释工具条末尾追加自定义按钮（2026-11-12：预览窗把 OCR/旋转放进来，
        /// 与注释工具同排同风格——用户要求"放进去，跟截图的工具栏似的"）。须在 BuildToolbar 后调用。</summary>
        public Button AddHostButton(Geometry g, string tip, RoutedEventHandler click, double stroke = 0, double size = 15, bool strokeOnly = false)
        {
            var b = TbI(g, tip, click, stroke, size, strokeOnly);
            _tbPanel?.Children.Add(b);
            return b;
        }

        private void Toggle(AnnTool t) => SelectedTool = _tool == t ? AnnTool.None : t;

        private void HighlightTool()
        {
            foreach (var kv in _toolButtons)
                kv.Value.Background = kv.Key == _tool
                    ? new SolidColorBrush(Color.FromArgb(0x44, 0x1E, 0x88, 0xE5))
                    : Brushes.Transparent;
        }

        private void ShowColorPopup()
        {
            if (_colorPopup == null)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
                for (int i = 0; i < AnnDefaults.Palette.Length; i++)
                {
                    int idx = i;
                    var rect = new System.Windows.Shapes.Rectangle
                    {
                        Width = 20, Height = 20, Margin = new Thickness(2),
                        Fill = new SolidColorBrush(AnnDefaults.Palette[idx]),
                        Stroke = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
                        StrokeThickness = 1,
                        Cursor = Cursors.Hand,
                    };
                    rect.MouseLeftButtonUp += (_, e2) =>
                    {
                        _colorIdx = idx;
                        _tbColor!.Foreground = new SolidColorBrush(AnnDefaults.Palette[idx]);
                        _colorPopup!.IsOpen = false;
                        e2.Handled = true;
                    };
                    sp.Children.Add(rect);
                }
                _colorPopup = new Popup
                {
                    Child = new Border
                    {
                        Child = sp,
                        Background = Brushes.White,                                  // 2026-11-02：浅色弹出面板（原深色）
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE1)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                    },
                    PlacementTarget = _tbColor,
                    Placement = PlacementMode.Top,
                    StaysOpen = false,
                };
            }
            _colorPopup.IsOpen = !_colorPopup.IsOpen;
        }

        private Brush AnnBrush => new SolidColorBrush(AnnDefaults.Palette[_colorIdx]);

        // ================= 绘制（移植自 PinWindow，坐标为画布自然 DIP） =================

        private static Rect Norm(Point a, Point b) => new(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

        /// <summary>
        /// 箭头几何（2026-11-04 统一：三处共用 MiniPix.Core.ArrowGeom 三线开放式箭头）。
        /// 原本地实现头仅 线宽×4（细档 8px）视觉上像单侧 → 改为 ×6（细 12/中 24/粗 48），张角 30°。
        /// </summary>
        private Geometry ArrowGeometry(Point a, Point b)
            => MiniPix.Core.ArrowGeom.Build(a, b, AnnThickness);

        private void OnCanvasDown(object sender, MouseButtonEventArgs e)
        {
            if (_tool == AnnTool.None) return;
            if (_textBox != null) return; // 文字输入中：点击交给 LostFocus 提交

            var p = e.GetPosition(_canvas);
            _activeTool = _tool;
            _down = p;
            switch (_tool)
            {
                case AnnTool.Rect:
                    _drawing = new System.Windows.Shapes.Rectangle { Stroke = AnnBrush, StrokeThickness = AnnThickness, Fill = null };
                    break;
                case AnnTool.Ellipse:
                    _drawing = new System.Windows.Shapes.Ellipse { Stroke = AnnBrush, StrokeThickness = AnnThickness };
                    break;
                case AnnTool.Arrow:
                    // 2026-11-02 反馈：不要实心头，开放式三线箭头（头部大小随线宽档位，见 ArrowGeometry）
                    _drawing = new Path { Stroke = AnnBrush, StrokeThickness = AnnThickness, Data = ArrowGeometry(p, p) };
                    break;
                case AnnTool.Pen:
                    _penPoints.Clear();
                    _penPoints.Add(p);
                    _pen = new Polyline
                    {
                        Stroke = AnnBrush, StrokeThickness = AnnThickness,
                        StrokeLineJoin = PenLineJoin.Round,
                        Points = new PointCollection(_penPoints),
                    };
                    _drawing = _pen;
                    break;
                case AnnTool.Mosaic:
                    // 拖动中只画虚线框，松手时把框内区域像素化
                    // 2026-11-02 反馈：原 #AAEEEE 1px 虚线浅图上看不清 →
                    // 当前颜色 1.5px 虚线 + 22 透明白色填充，任何底色都醒目
                    _mosaicTmp = new System.Windows.Shapes.Rectangle
                    {
                        Stroke = AnnBrush,
                        StrokeThickness = 1.5,
                        StrokeDashArray = new DoubleCollection(new[] { 4.0, 2.5 }),
                        Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                    };
                    _drawing = _mosaicTmp;
                    break;
                case AnnTool.Text:
                    StartText(p);
                    return;
                case AnnTool.Number:
                    PlaceNumber(p);
                    return;
                default:
                    return;
            }
            _canvas.Children.Add(_drawing);
            _canvas.CaptureMouse();
            e.Handled = true;
        }

        private void OnCanvasMove(object sender, MouseEventArgs e)
        {
            if (_drawing == null) return;
            var p = e.GetPosition(_canvas);
            if (_activeTool == AnnTool.Pen && _pen != null)
            {
                _penPoints.Add(p);
                _pen.Points = new PointCollection(_penPoints);
            }
            else UpdateShape(p);
            e.Handled = true;
        }

        private void OnCanvasUp(object sender, MouseButtonEventArgs e)
        {
            if (_drawing == null) return;
            CompleteShape();
            e.Handled = true;
        }

        private void UpdateShape(Point p)
        {
            var r = Norm(_down, p);
            switch (_activeTool)
            {
                case AnnTool.Rect:
                case AnnTool.Mosaic:
                    if (_drawing is System.Windows.Shapes.Rectangle rc)
                    {
                        Canvas.SetLeft(rc, r.X); Canvas.SetTop(rc, r.Y);
                        rc.Width = r.Width; rc.Height = r.Height;
                    }
                    break;
                case AnnTool.Ellipse:
                    if (_drawing is System.Windows.Shapes.Ellipse el)
                    {
                        Canvas.SetLeft(el, r.X); Canvas.SetTop(el, r.Y);
                        el.Width = r.Width; el.Height = r.Height;
                    }
                    break;
                case AnnTool.Arrow:
                    if (_drawing is Path pa)
                        pa.Data = ArrowGeometry(_down, p);
                    break;
            }
        }

        private void CompleteShape()
        {
            _canvas.ReleaseMouseCapture();
            // 马赛克：虚线框 → 框内像素化图块
            if (_activeTool == AnnTool.Mosaic && _drawing is System.Windows.Shapes.Rectangle mrc)
            {
                var r = new Rect(Canvas.GetLeft(mrc), Canvas.GetTop(mrc), mrc.Width, mrc.Height);
                _canvas.Children.Remove(mrc);
                if (r.Width > 4 && r.Height > 4)
                {
                    var mimg = new Image
                    {
                        Source = MakeMosaicTile(r),
                        Width = r.Width,
                        Height = r.Height,
                        Stretch = Stretch.Fill,
                        IsHitTestVisible = false,
                    };
                    // 像素化关键：小图放大用最近邻插值（块状马赛克）
                    RenderOptions.SetBitmapScalingMode(mimg, BitmapScalingMode.NearestNeighbor);
                    Canvas.SetLeft(mimg, r.X); Canvas.SetTop(mimg, r.Y);
                    _canvas.Children.Add(mimg);
                    _shapes.Add(mimg);
                    _redo.Clear();
                    StateChanged?.Invoke();
                }
            }
            else
            {
                // 单击（几乎没拖动）的矩形/椭圆不算形状，丢弃
                bool tiny = _drawing is System.Windows.Shapes.Rectangle { Width: < 3, Height: < 3 }
                         || _drawing is System.Windows.Shapes.Ellipse { Width: < 3, Height: < 3 };
                if (_drawing != null && !tiny)
                {
                    _shapes.Add(_drawing);
                    _redo.Clear(); // 新笔画清空重做栈
                    StateChanged?.Invoke();
                }
                else if (_drawing != null)
                    _canvas.Children.Remove(_drawing);
            }
            _drawing = null;
            _pen = null;
            _mosaicTmp = null;
        }

        /// <summary>
        /// 生成 dipRect 区域的马赛克图块：从底图按"源像素/显示DIP"比例取样，
        /// 先缩小（去细节），显示时 NearestNeighbor 放大 → 马赛克。
        /// 比例默认 kx = PixelWidth / 画布自然宽度（画布与底图共用 LayoutTransform 时
        /// 与显示缩放无关）；宿主布局特殊时用 SetSampleScale 覆盖。
        /// </summary>
        private BitmapSource MakeMosaicTile(Rect dipRect)
        {
            double kx = _kxOverride ?? _baseImage.PixelWidth / Math.Max(1, _canvas.ActualWidth);
            double ky = _kyOverride ?? _baseImage.PixelHeight / Math.Max(1, _canvas.ActualHeight);
            int px = (int)Math.Clamp(Math.Round(dipRect.X * kx), 0, _baseImage.PixelWidth - 1);
            int py = (int)Math.Clamp(Math.Round(dipRect.Y * ky), 0, _baseImage.PixelHeight - 1);
            int pw = (int)Math.Clamp(Math.Round(dipRect.Width * kx), 1, _baseImage.PixelWidth - px);
            int ph = (int)Math.Clamp(Math.Round(dipRect.Height * ky), 1, _baseImage.PixelHeight - py);

            var crop = new CroppedBitmap(_baseImage, new Int32Rect(px, py, pw, ph));
            int fw = (int)Math.Max(1, Math.Round(pw / AnnDefaults.MosaicFactor));
            int fh = (int)Math.Max(1, Math.Round(ph / AnnDefaults.MosaicFactor));
            return new TransformedBitmap(crop, new ScaleTransform((double)fw / pw, (double)fh / ph));
        }

        // ---- 带圈数字编号（2026-11-02 新增） ----

        /// <summary>在点击位置放一个带圈数字（白底圆圈 + 当前颜色边框和数字），自动 1、2、3… 递增。
        /// 撤销刚放的编号时序号回退（见 Undo）；全部撤销完自动归 1。</summary>
        private void PlaceNumber(Point p)
        {
            int n = _nextNumber++;
            // 2026-11-04 反馈修复：原两位数只加宽（26→36×26）变成椭圆；改为直径随位数增大、字号微缩，始终保持正圆
            int digits = n.ToString().Length;
            double size = AnnDefaults.NumberSize + (digits - 1) * 5;   // 1位Ø26 / 2位Ø31 / 3位Ø36
            double fontSize = Math.Max(10, 14 - digits);               // 13 / 12 / 11
            var brush = AnnBrush;
            var grid = new Grid
            {
                Width = size,
                Height = size,
                Tag = n,                              // 记下号码，撤销时回退计数器用
                IsHitTestVisible = false,             // 放好后不挡下面的绘制
                Children =
                {
                    new System.Windows.Shapes.Ellipse
                    {
                        Stroke = brush,
                        StrokeThickness = 2.5,
                        Fill = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
                    },
                    new TextBlock
                    {
                        Text = n.ToString(),
                        FontSize = fontSize,
                        FontWeight = FontWeights.Bold,
                        Foreground = brush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };
            Canvas.SetLeft(grid, p.X - size / 2);
            Canvas.SetTop(grid, p.Y - size / 2);
            _canvas.Children.Add(grid);
            _shapes.Add(grid);
            _redo.Clear();
            StateChanged?.Invoke();
        }

        // ---- 文字 ----

        private void StartText(Point p)
        {
            CommitText(); // 上一条没提交先提交
            _textBox = new TextBox
            {
                MinWidth = 80,
                FontSize = AnnDefaults.FontSize,
                FontFamily = new FontFamily("Microsoft YaHei, Segoe UI"),
                Foreground = AnnBrush,
                // 2026-11-02 反馈：原来透明底+淡灰边+白色光标，浅图上完全看不见（以为没反应）→
                // 白色 85% 不透明底 + 当前颜色边框 + 同色光标，任何图上都醒目
                Background = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
                BorderBrush = AnnBrush,
                BorderThickness = new Thickness(1.5),
                Padding = new Thickness(4, 2, 4, 2),
                CaretBrush = AnnBrush,
            };
            Canvas.SetLeft(_textBox, p.X);
            Canvas.SetTop(_textBox, p.Y);
            _canvas.Children.Add(_textBox);
            _textBox.KeyDown += (_, e2) =>
            {
                // 2026-11-02 修复"T 不能输入"的真正根因：原来无条件 e2.Handled = true，
                // 而 WPF 里 KeyDown 被标记 Handled 后后续的 TextInput 不再产生 → 字符打不进去。
                // 只有 Enter/Esc 才标记（防触发宿主快捷键），普通按键一律放行。
                if (e2.Key == Key.Enter) { CommitText(); e2.Handled = true; }
                else if (e2.Key == Key.Escape)
                {
                    _canvas.Children.Remove(_textBox);
                    _textBox = null;
                    e2.Handled = true;
                }
            };
            _textBox.LostFocus += (_, _) =>
            {
                MiniPix.Core.Log.Write($"ann text: LostFocus (len={tbLen()})");
                // 2026-11-02 关键修复：不再立即提交！日志实锤创建瞬间有组件在 1~4ms 内抢焦点，
                // 直接提交会把刚建的空框删掉（= 用户看到的"不能输入"）。
                // 延后一拍核查：焦点被误抢 → 夺回来继续输入；焦点真没了 → 才提交。
                Dispatcher.BeginInvoke(DeferredTextFocusCheck,
                    System.Windows.Threading.DispatcherPriority.Input);
            };
            _textBox.GotFocus += (_, _) => MiniPix.Core.Log.Write("ann text: GotFocus");
            bool fok = _textBox.Focus();
            MiniPix.Core.Log.Write($"ann text: start at ({p.X:F0},{p.Y:F0}) syncFocus={fok}");

            // 同步 Focus() 在 MouseDown 处理器里可能被点击收尾逻辑重置——
            // 输入事件队列清空后再核查一次（见 DeferredTextFocusCheck）
            Dispatcher.BeginInvoke(DeferredTextFocusCheck,
                System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>
        /// 文字输入框焦点核查（Input 优先级延后执行）：
        /// 框还活着但焦点被误抢 → 夺回；确认拿不回 / 用户已点到别的窗口 → 提交收尾。
        /// 日志实锤（21:15）：syncFocus=True 后 1~4ms 内焦点即被未知组件抢走，
        /// 立即提交导致输入框闪没，用户以为"不能输入"。
        /// </summary>
        private void DeferredTextFocusCheck()
        {
            if (_textBox == null) { MiniPix.Core.Log.Write("ann text: check skip (already committed)"); return; }
            var win = Window.GetWindow(this);
            if (win != null && !win.IsActive) { MiniPix.Core.Log.Write("ann text: window inactive -> commit"); CommitText(); return; }
            if (!_textBox.IsKeyboardFocused)
            {
                Keyboard.Focus(_textBox);
                _textBox.Focus();
            }
            if (_textBox == null) return; // Keyboard.Focus 可能触发事件链提交了
            if (!_textBox.IsKeyboardFocused)
            {
                MiniPix.Core.Log.Write("ann text: refocus failed -> commit");
                CommitText();
            }
            else
            {
                MiniPix.Core.Log.Write("ann text: focus ok (recovered or kept)");
            }
        }

        /// <summary>诊断用：当前输入框文本长度（_textBox 可能已被置空）</summary>
        private string tbLen() => _textBox?.Text.Length.ToString() ?? "null";

        /// <summary>输入框 → TextBlock 固定在画布上（空文本直接丢弃）</summary>
        private void CommitText()
        {
            var tb = _textBox;
            _textBox = null;
            if (tb == null) return;
            _canvas.Children.Remove(tb);
            MiniPix.Core.Log.Write($"ann text: commit len={tb.Text.Length}");
            if (string.IsNullOrWhiteSpace(tb.Text)) return;
            var label = new TextBlock
            {
                Text = tb.Text,
                FontSize = tb.FontSize,
                FontFamily = tb.FontFamily,
                Foreground = tb.Foreground,
            };
            Canvas.SetLeft(label, Canvas.GetLeft(tb));
            Canvas.SetTop(label, Canvas.GetTop(tb));
            _canvas.Children.Add(label);
            _shapes.Add(label);
            StateChanged?.Invoke();
        }
    }
}
