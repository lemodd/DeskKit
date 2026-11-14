using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MiniPix.Output;

namespace MiniPix.Pin;

public partial class PinWindow : Window
{
    private double _zoom = 1.0;          // user zoom factor (1 = physical 100%)
    private double _monitorScale = 1.0;  // DPI scale of the monitor this pin lives on
    private double _srcScale = 1.0;      // 2026-11-04 来源屏 DPI scale（截图屏 _sx；剪贴板图=1）
    private byte _alpha = 255;
    private bool _dragging;
    private Point _downScreen;
    private double _startLeft, _startTop;
    private readonly BitmapSource _image;
    private Point? _initialPx;

    // ============ 2026-10-30 注释工具（用户要求：空格调出截图同款工具条） ============
    private enum AnnTool { None, Rect, Ellipse, Arrow, Pen, Mosaic, Text, Number }

    private AnnTool _annTool = AnnTool.None;        // 当前选中的注释工具
    private AnnTool _annActiveTool;                 // 正在绘制的工具（笔画中途锁定）
    private static readonly Color[] AnnPalette =
    {
        Color.FromRgb(0xE0, 0x40, 0x3A), Color.FromRgb(0xF0, 0xA0, 0x20),
        Color.FromRgb(0xF5, 0xC5, 0x11), Color.FromRgb(0x33, 0xA9, 0x4C),
        Color.FromRgb(0x1E, 0x88, 0xE5), Color.FromRgb(0x00, 0x00, 0x00),
        Color.FromRgb(0xFF, 0xFF, 0xFF),
    };
    private int _annColorIdx;                       // 当前颜色（默认红）
    private int _nextAnnNumber = 1;                 // 带圈数字编号计数器（2026-11-04 从预览注释移植；撤销时回退）
    private readonly double[] _annThicknesses = { 2, 4, 8 };   // 线宽三档（同截图）
    private int _annWidthIdx = 0;                   // 当前线宽档位
    private double AnnThickness => _annThicknesses[_annWidthIdx];
    private const double AnnFontSize = 16;          // 文字字号（可调项）
    private const double AnnMosaicFactor = 6;       // 马赛克强度（同截图）

    private UIElement? _annDrawing;                 // 正在画的形状
    private readonly List<UIElement> _annShapes = new(); // 已完成的注释形状
    private readonly List<UIElement> _annRedo = new();   // 撤销后可重做的形状
    private Point _annDown;                         // 笔画起点（画布坐标）
    private readonly List<Point> _annPenPoints = new();
    private Polyline? _annPen;
    private System.Windows.Shapes.Rectangle? _mosaicTmp; // 马赛克拖动中的虚线框
    private TextBox? _annTextBox;                   // 文字输入框（提交后变 TextBlock）
    private readonly Dictionary<AnnTool, Button> _annToolButtons = new();
    private Popup? _annColorPopup;

    // ---- 2026-11-05 OCR 识文（v2 简化版）：工具条点 OCR → 直接识别整张合成图 → 弹结果窗 ----
    // v1 的框选覆盖层（OcrRegionOverlay/_ocrMode 及各手势守卫）已删除，旧实现见 backup_20261105_before_ocr_simplify

    /// <summary>Show a bitmap pinned at (px,py) physical-pixel virtual-desktop coords.</summary>
    /// <param name="sourceScale">来源屏 DPI scale：截图钉图传截图屏的 _sx（位图 DPI 已含屏幕 DPI）；
    /// 剪贴板等外源图默认 1.0（位图 DPI=96）。缩放系数 = _zoom × sourceScale / 目标屏scale，
    /// 同屏钉截图时 = 1，物理尺寸与选区完全一致。</param>
    public static PinWindow Show(BitmapSource image, Point pxTopLeft, double sourceScale = 1.0)
    {
        var win = new PinWindow(image) { _srcScale = sourceScale };
        win._initialPx = pxTopLeft;
        // 2026-11-04 修复"钉住时图片放大/错位"：先按目标屏设好缩放再 Show，
        // 保证首次布局尺寸就正确（原顺序 Show→PlacePhysical 改 LayoutTransform，
        // SizeToContent 可能不重算，导致窗口尺寸与图片不符）
        win.PreparePhysical(pxTopLeft);
        win.Show();
        win.PlacePhysical(pxTopLeft);
        win.Activate();
        return win;
    }

    private PinWindow(BitmapSource image)
    {
        _image = image;
        InitializeComponent();
        PinImage.Source = image;
        PreviewMouseWheel += OnWheel;
        KeyDown += OnKeyDown;
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        BuildContextMenu();
        CollectToolButtons();

        // 2026-11-05 v2：OCR 改为一键识别，v1 的 OcrRegionOverlay 初始化已删除

        // 2026-10-30 用户要求：被钉住的图片也要放进暂存架。
        // 走 AddExternalImage（与剪贴板同款指纹去重，防止"截图后紧接着钉住"收两张）
        global::DeskKit.App.AddImageToShelf(image);
    }

    /// <summary>Position window using physical px coords of the virtual desktop.</summary>
    private void PlacePhysical(Point pxTopLeft)
    {
        var mon = Core.MonitorManager.AtPoint(pxTopLeft.X, pxTopLeft.Y);
        _monitorScale = mon.ScaleX;
        ApplyZoom();
        // 2026-11-04 完全重合：RootBorder 有 1 DIP 黑边框，图片内容在窗口 (1,1) 处，
        // 左上各减 1 DIP 让图片与截图选区逐像素对齐（原实现偏移 1px）
        Left = mon.Int32Rect.X + (pxTopLeft.X - mon.Int32Rect.X) / mon.ScaleX - 1;
        Top = mon.Int32Rect.Y + (pxTopLeft.Y - mon.Int32Rect.Y) / mon.ScaleY - 1;

        // 2026-11-04 诊断：钉图重合问题排查（布局完成后输出关键数字）
        Dispatcher.BeginInvoke(() =>
        {
            var src = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(this);
            double wndScale = src?.CompositionTarget.TransformToDevice.M11 ?? 0;
            MiniPix.Core.Log.Write(
                $"PIN px=({pxTopLeft.X:0},{pxTopLeft.Y:0}) monRect={mon.Int32Rect} monScale={mon.ScaleX:0.###} " +
                $"wndScale={wndScale:0.###} srcScale={_srcScale:0.###} zoom={_zoom:0.###} " +
                $"imgPx={_image.PixelWidth}x{_image.PixelHeight} imgDpi=({_image.DpiX:0.#},{_image.DpiY:0.#}) " +
                $"imgDip={PinImage.ActualWidth:0.#}x{PinImage.ActualHeight:0.#} winDip={ActualWidth:0.#}x{ActualHeight:0.#} " +
                $"left={Left:0.##} top={Top:0.##}");
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Show 前按目标屏预设缩放（保证首次布局尺寸正确）</summary>
    private void PreparePhysical(Point pxTopLeft)
    {
        var mon = Core.MonitorManager.AtPoint(pxTopLeft.X, pxTopLeft.Y);
        _monitorScale = mon.ScaleX;
        ApplyZoom();
    }

    public void SetAlpha(byte alpha)
    {
        _alpha = alpha;
        Opacity = alpha / 255.0;
    }

    private void ApplyZoom()
    {
        // 2026-11-04 完全重合：缩放系数 = 用户缩放 × 来源屏scale / 目标屏scale。
        // 截图位图 DPI = 来源屏 DPI（RenderSelection 用 _sx*96 创建），
        // WPF 自然显示 = px/来源scale DIP；同屏时 ×(_srcScale/_monitorScale)=1
        // → 物理 = 选区像素原样，不放大不缩小。剪贴板图（DPI=96）同屏时 = 1/目标scale（原行为）。
        double s = _zoom * _srcScale / _monitorScale;
        PinImage.LayoutTransform = new ScaleTransform(s, s);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Alt)
        {
            int a = _alpha + (e.Delta > 0 ? 40 : -40);
            SetAlpha((byte)Math.Clamp(a, 20, 255));
        }
        else if (_annTool == AnnTool.None) // 注释工具选中时禁用缩放，避免已画的形状错位（2026-11-05 v2：OCR 守卫随框选模式删除）
        {
            _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.1 : 0.9), 0.1, 10.0);
            ApplyZoom();
        }
        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 2026-10-30 用户要求：双击贴图直接关闭（注释工具选中时双击是画图，不关）
        if (e.ClickCount == 2 && _annTool == AnnTool.None)
        {
            Close();
            e.Handled = true;
            return;
        }

        // 注释模式：选了工具就画图，不拖动窗口
        if (PinToolBar.Visibility == Visibility.Visible && _annTool != AnnTool.None && _annTextBox == null)
        {
            StartAnnShape(e.GetPosition(AnnCanvas));
            e.Handled = true;
            return;
        }

        _dragging = true;
        _downScreen = PointToScreen(e.GetPosition(this));
        _startLeft = Left;
        _startTop = Top;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_annDrawing != null)
        {
            var p = e.GetPosition(AnnCanvas);
            if (_annActiveTool == AnnTool.Pen && _annPen != null)
            {
                _annPenPoints.Add(p);
                _annPen.Points = new PointCollection(_annPenPoints);
            }
            else UpdateAnnShape(p);
            return;
        }
        if (!_dragging) return;
        var sp = PointToScreen(e.GetPosition(this));
        // screen deltas are in device px; Left/Top are in DIP
        var src = PresentationSource.FromVisual(this)?.CompositionTarget;
        double dpiX = src?.TransformToDevice.M11 ?? 1.0, dpiY = src?.TransformToDevice.M22 ?? 1.0;
        Left = _startLeft + (sp.X - _downScreen.X) / dpiX;
        Top = _startTop + (sp.Y - _downScreen.Y) / dpiY;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_annDrawing != null)
        {
            CompleteAnnShape();
            return;
        }
        _dragging = false;
        ReleaseMouseCapture();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_annTextBox != null) return; // 输入文字时按键全归 TextBox

        double step = Keyboard.Modifiers == ModifierKeys.Control ? 10 : 1;
        switch (e.Key)
        {
            case Key.Escape:
                // 工具条开着时先收工具条（含退出注释状态），否则关闭贴图
                if (PinToolBar.Visibility == Visibility.Visible) ToggleToolBar();
                else Close();
                break;
            case Key.Space: ToggleToolBar(); break; // 2026-10-30：空格切换注释工具条
            case Key.Enter: global::DeskKit.App.CopyImageToClipboardSuppressed(CompositeImage()); break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control: global::DeskKit.App.CopyImageToClipboardSuppressed(CompositeImage()); break;
            case Key.S when Keyboard.Modifiers == ModifierKeys.Control: SavePng(); break;
            case Key.Left: Left -= step; break;
            case Key.Right: Left += step; break;
            case Key.Up: Top -= step; break;
            case Key.Down: Top += step; break;
            case Key.D0 or Key.NumPad0: TbZoom_Click(this, e); break;
            case Key.OemPlus or Key.Add: ZoomBy(0.1); break;
            case Key.OemMinus or Key.Subtract: ZoomBy(-0.1); break;
            // ---- 2026-10-30 与截图工具条一致的工具/撤销快捷键（同时会自动展开工具条） ----
            case Key.R: ShowToolbarAndTool(AnnTool.Rect); break;
            case Key.O: ShowToolbarAndTool(AnnTool.Ellipse); break;
            case Key.A: ShowToolbarAndTool(AnnTool.Arrow); break;
            case Key.D: ShowToolbarAndTool(AnnTool.Pen); break;
            case Key.T: ShowToolbarAndTool(AnnTool.Text); break;
            case Key.B: ShowToolbarAndTool(AnnTool.Mosaic); break;
            case Key.N: ShowToolbarAndTool(AnnTool.Number); break; // 2026-11-04：数字编号
            case Key.Z when Keyboard.Modifiers == ModifierKeys.Control: AnnUndo(); break;
            case Key.Y when Keyboard.Modifiers == ModifierKeys.Control: AnnRedo(); break;
            case Key.S: TbSave_Click(this, e); break; // 2026-11-04：S 同保存按钮，弹出另存对话框选位置
        }
    }

    /// <summary>快捷键选工具：工具条没开就先展开（同截图里按 R/O/A… 直接进入标注状态）</summary>
    private void ShowToolbarAndTool(AnnTool t)
    {
        if (PinToolBar.Visibility != Visibility.Visible) PinToolBar.Visibility = Visibility.Visible;
        SetAnnTool(_annTool == t ? AnnTool.None : t);
    }

    private void ZoomBy(double d)
    {
        _zoom = Math.Clamp(_zoom + d, 0.1, 10);
        ApplyZoom();
    }

    public void SavePng()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg",
            FileName = SaveUtil.NextFileName(),
            DefaultExt = ".png"
        };
        if (dlg.ShowDialog() == true)
            SaveUtil.SavePng(CompositeImage(), dlg.FileName); // 2026-10-30：保存含注释
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();
        MenuItem Mi(string text, Action action)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _e) => action();
            return item;
        }
        menu.Items.Add(Mi("复制  (Enter/Ctrl+C)", () => global::DeskKit.App.CopyImageToClipboardSuppressed(CompositeImage())));
        menu.Items.Add(Mi("另存为… (Ctrl+S)", SavePng));
        menu.Items.Add(Mi("注释工具条  (空格)", ToggleToolBar));
        menu.Items.Add(Mi("关闭  (Esc/双击)", Close));
        ContextMenu = menu;
        MouseRightButtonUp += (_, e) =>
        {
            menu.PlacementTarget = this;
            menu.IsOpen = true;
            e.Handled = true;
        };
    }

    // ================= 注释工具条 =================

    private void CollectToolButtons()
    {
        _annToolButtons[AnnTool.Rect] = TbRect;
        _annToolButtons[AnnTool.Ellipse] = TbEllipse;
        _annToolButtons[AnnTool.Arrow] = TbArrow;
        _annToolButtons[AnnTool.Pen] = TbPen;
        _annToolButtons[AnnTool.Text] = TbText;
        _annToolButtons[AnnTool.Mosaic] = TbMosaic;
        // 2026-11-04 补齐：数字编号工具（原来只在预览注释有，工具条统一后三处一致）
        _annToolButtons[AnnTool.Number] = TbNumber;

        // 2026-11-04 图标统一：文字符号按钮 → Material Symbols Rounded 矢量图标（DeskKit.Icons 几何库）
        // 图标 Path 的 Fill 绑定按钮 Foreground → 深底白字不变，颜色钮换色时图标自动跟随
        // 线宽 TbWidth 保留"细/中/粗"文字（用户确认：文字比图标直观）
        SetIcon(TbRect, DeskKit.Icons.Rect);
        SetIcon(TbEllipse, DeskKit.Icons.Ellipse);
        SetIcon(TbArrow, DeskKit.Icons.Arrow);
        SetIcon(TbPen, DeskKit.Icons.Pen);
        SetIcon(TbText, DeskKit.Icons.Text);
        SetIcon(TbMosaic, DeskKit.Icons.Mosaic);
        SetIcon(TbNumber, DeskKit.Icons.Number);
        // 2026-11-12 三修：颜色图标放大到 18 + stroke 加粗线宽
        // 2026-11-14 四修：stroke 与截图工具条统一为 0.6
        SetIcon(TbColor, DeskKit.Icons.Palette, stroke: 0.6, size: 18);
        // 2026-11-12 用户要求：颜色图标不跟随当前标注颜色（不再变红），固定与其他图标一致的灰色；
        // Foreground 仍记录当前颜色供换色逻辑使用，只解绑图标的 Fill/Stroke
        // 2026-11-14 五修：白色是深底时代遗留，白底上隐形 → 改深灰（与截图工具条 2026-11-12 版一致）
        if (TbColor.Content is System.Windows.Shapes.Path cpColor)
        {
            System.Windows.Data.BindingOperations.ClearBinding(cpColor, System.Windows.Shapes.Path.FillProperty);
            System.Windows.Data.BindingOperations.ClearBinding(cpColor, System.Windows.Shapes.Path.StrokeProperty);
            var gray = System.Windows.Media.Color.FromRgb(0x44, 0x4A, 0x52);
            cpColor.Fill = new System.Windows.Media.SolidColorBrush(gray);
            cpColor.Stroke = new System.Windows.Media.SolidColorBrush(gray);
        }
        // 2026-11-04 用户要求：线宽也用图标（line_weight），当前档位在 tooltip 显示
        // 2026-11-12 二改：三档各用一个图标（横杠细/中/粗），初始显示当前档位图标
        SetIcon(TbWidth, DeskKit.Icons.LineWidthSet[_annWidthIdx]);
        TbWidth.ToolTip = $"线宽：{new[] { "细", "中", "粗" }[_annWidthIdx]} (点击切换)";
        SetIcon(TbUndo, DeskKit.Icons.Undo);
        SetIcon(TbRedo, DeskKit.Icons.Redo);
        SetIcon(TbCopy, DeskKit.Icons.Copy);
        SetIcon(TbOcr, DeskKit.Icons.Ocr, stroke: 1.0);   // 2026-11-05：OCR 识文；2026-11-12 二修：stroke 加粗线宽
        SetIcon(TbSave, DeskKit.Icons.Save);
        SetIcon(TbCloseX, DeskKit.Icons.Close);
    }

    /// <summary>把按钮 Content 换成矢量图标 Path（Fill 跟随 Foreground 自动变色）。
    /// 2026-11-12 二修：stroke &gt; 0 时同时绑定 Stroke 到 Foreground（图标加粗）；
    /// 三修：size 可单独调图标视觉大小</summary>
    private static void SetIcon(System.Windows.Controls.Button b, Geometry g, double stroke = 0, double size = 15)
    {
        var path = DeskKit.Icons.MakePath(g, size, stroke);
        path.SetBinding(System.Windows.Shapes.Path.FillProperty,
            new System.Windows.Data.Binding("Foreground") { Source = b });
        if (stroke > 0)
            path.SetBinding(System.Windows.Shapes.Path.StrokeProperty,
                new System.Windows.Data.Binding("Foreground") { Source = b });
        b.Content = path;
    }

    /// <summary>空格键切换工具条；收起时退出注释状态</summary>
    private void ToggleToolBar()
    {
        if (PinToolBar.Visibility == Visibility.Visible)
        {
            PinToolBar.Visibility = Visibility.Collapsed;
            SetAnnTool(AnnTool.None);
            if (_annColorPopup != null) _annColorPopup.IsOpen = false;
            CommitAnnText();
        }
        else
        {
            PinToolBar.Visibility = Visibility.Visible;
        }
    }

    /// <summary>点工具条空白处不触发贴图拖动/画图</summary>
    private void ToolBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void SetAnnTool(AnnTool t)
    {
        _annTool = t;
        foreach (var kv in _annToolButtons)
            kv.Value.Background = kv.Key == t
                ? new SolidColorBrush(Color.FromArgb(0x44, 0x1E, 0x88, 0xE5))
                : Brushes.Transparent;
    }

    private Brush AnnBrush => new SolidColorBrush(AnnPalette[_annColorIdx]);

    private void TbRect_Click(object sender, RoutedEventArgs e) => SetAnnTool(_annTool == AnnTool.Rect ? AnnTool.None : AnnTool.Rect);
    private void TbEllipse_Click(object sender, RoutedEventArgs e) => SetAnnTool(_annTool == AnnTool.Ellipse ? AnnTool.None : AnnTool.Ellipse);
    private void TbArrow_Click(object sender, RoutedEventArgs e) => SetAnnTool(_annTool == AnnTool.Arrow ? AnnTool.None : AnnTool.Arrow);
    private void TbPen_Click(object sender, RoutedEventArgs e) => SetAnnTool(_annTool == AnnTool.Pen ? AnnTool.None : AnnTool.Pen);
    private void TbText_Click(object sender, RoutedEventArgs e) => SetAnnTool(_annTool == AnnTool.Text ? AnnTool.None : AnnTool.Text);
    private void TbMosaic_Click(object sender, RoutedEventArgs e) => SetAnnTool(_annTool == AnnTool.Mosaic ? AnnTool.None : AnnTool.Mosaic);
    // 2026-11-04 补齐：数字编号工具（同截图/预览）
    private void TbNumber_Click(object sender, RoutedEventArgs e) => SetAnnTool(_annTool == AnnTool.Number ? AnnTool.None : AnnTool.Number);

    /// <summary>线宽三档切换（细/中/粗，同截图工具条）；2026-11-12 二改：图标随档位同步更换</summary>
    private void TbWidth_Click(object sender, RoutedEventArgs e)
    {
        _annWidthIdx = (_annWidthIdx + 1) % _annThicknesses.Length;
        TbWidth.ToolTip = $"线宽：{new[] { "细", "中", "粗" }[_annWidthIdx]} (点击切换)";
        if (TbWidth.Content is System.Windows.Shapes.Path p) p.Data = DeskKit.Icons.LineWidthSet[_annWidthIdx];
    }

    private void TbUndo_Click(object sender, RoutedEventArgs e) => AnnUndo();
    private void TbRedo_Click(object sender, RoutedEventArgs e) => AnnRedo();
    private void TbDone_Click(object sender, RoutedEventArgs e) => ToggleToolBar();
    private void TbZoom_Click(object sender, RoutedEventArgs e) { _zoom = 1; ApplyZoom(); }
    private void TbCloseX_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>「复」：复制合成后的图（原 MiniPix 复制语义，防回环）</summary>
    private void TbCopy_Click(object sender, RoutedEventArgs e) =>
        global::DeskKit.App.CopyImageToClipboardSuppressed(CompositeImage());

    /// <summary>「保」：弹出另存对话框选保存位置（2026-11-04 用户要求；原静默存到自动截图目录）</summary>
    private void TbSave_Click(object sender, RoutedEventArgs e) => SavePng();

    private void TbColor_Click(object sender, RoutedEventArgs e)
    {
        if (_annColorPopup == null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
            for (int i = 0; i < AnnPalette.Length; i++)
            {
                int idx = i;
                var rect = new System.Windows.Shapes.Rectangle
                {
                    Width = 20, Height = 20, Margin = new Thickness(2),
                    Fill = new SolidColorBrush(AnnPalette[idx]),
                    Stroke = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
                    StrokeThickness = 1,
                    Cursor = Cursors.Hand
                };
                rect.MouseLeftButtonUp += (_, e2) =>
                {
                    _annColorIdx = idx;
                    TbColor.Foreground = new SolidColorBrush(AnnPalette[idx]); // 色钮显示当前颜色
                    _annColorPopup!.IsOpen = false;
                    e2.Handled = true;
                };
                sp.Children.Add(rect);
            }
            _annColorPopup = new Popup
            {
                Child = new Border
                {
                    Child = sp,
                    // 2026-11-04 工具条统一：颜色弹窗改浅色（原深底 #E6222222）
                    Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE1)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4)
                },
                PlacementTarget = TbColor,
                Placement = PlacementMode.Top,
                StaysOpen = false
            };
        }
        _annColorPopup.IsOpen = !_annColorPopup.IsOpen;
    }

    private void AnnUndo()
    {
        CommitAnnText();
        if (_annColorPopup is { IsOpen: true }) _annColorPopup.IsOpen = false;
        if (AnnCanvas.Children.Count == 0) return;
        var last = AnnCanvas.Children[^1];
        AnnCanvas.Children.RemoveAt(AnnCanvas.Children.Count - 1);
        _annShapes.Remove(last);
        _annRedo.Add(last);
        // 2026-11-04：撤销的是带圈编号时，序号回退（与预览注释一致）
        if (last is Grid g && g.Tag is int n && n == _nextAnnNumber - 1) _nextAnnNumber = n;
    }

    private void AnnRedo()
    {
        if (_annRedo.Count == 0) return;
        var item = _annRedo[^1];
        _annRedo.RemoveAt(_annRedo.Count - 1);
        AnnCanvas.Children.Add(item);
        _annShapes.Add(item);
    }

    // ================= 注释绘制（移植自 CaptureWindow，坐标为画布 DIP） =================

    private static Rect AnnNorm(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    // 原本地"杆+封口三角"实现（头最大 14px 视觉像单侧）已于 2026-11-04 弃用，
    // 统一改用 MiniPix.Core.ArrowGeom.Build（三线开放式 + 头随线宽），见调用处 AnnArrowGeometry。

    /// <summary>带圈数字编号（2026-11-04 从预览注释 AnnotationEditor.PlaceNumber 移植，样式/字号一致）：
    /// 白底圆圈 + 当前颜色边框和数字，自动 1、2、3… 递增；撤销刚放的编号时序号回退（见 AnnUndo）。</summary>
    private void PlaceAnnNumber(Point p)
    {
        int n = _nextAnnNumber++;
        // 2026-11-04 反馈修复：原两位数只加宽（26→36×26）变成椭圆；改为直径随位数增大、字号微缩，始终保持正圆
        int digits = n.ToString().Length;
        double size = ShotShelf.Annotation.AnnDefaults.NumberSize + (digits - 1) * 5;   // 1位Ø26 / 2位Ø31 / 3位Ø36
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
        AnnCanvas.Children.Add(grid);
        _annShapes.Add(grid);
        _annRedo.Clear();
    }

    private void StartAnnShape(Point p)
    {
        _annActiveTool = _annTool;
        _annDown = p;
        switch (_annTool)
        {
            case AnnTool.Rect:
                _annDrawing = new System.Windows.Shapes.Rectangle
                {
                    Stroke = AnnBrush, StrokeThickness = AnnThickness, Fill = null
                };
                break;
            case AnnTool.Ellipse:
                _annDrawing = new System.Windows.Shapes.Ellipse
                {
                    Stroke = AnnBrush, StrokeThickness = AnnThickness
                };
                break;
            case AnnTool.Arrow:
                _annDrawing = new System.Windows.Shapes.Path
                {
                    Stroke = AnnBrush, StrokeThickness = AnnThickness,
                    Data = MiniPix.Core.ArrowGeom.Build(p, p, AnnThickness)
                };
                break;
            case AnnTool.Pen:
                _annPenPoints.Clear();
                _annPenPoints.Add(p);
                _annPen = new Polyline
                {
                    Stroke = AnnBrush, StrokeThickness = AnnThickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    Points = new PointCollection(_annPenPoints)
                };
                _annDrawing = _annPen;
                break;
            case AnnTool.Mosaic:
                // 拖动中只画虚线框，松手时把框内区域马赛克化（同截图逻辑）
                _mosaicTmp = new System.Windows.Shapes.Rectangle
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(0xAA, 0xEE, 0xEE, 0xEE)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection(new[] { 3.0, 2.0 })
                };
                _annDrawing = _mosaicTmp;
                break;
            case AnnTool.Text:
                StartAnnText(p);
                return;
            case AnnTool.Number:
                // 2026-11-04 移植：点击放置带圈数字，非拖动形状（_annDrawing 保持 null）
                PlaceAnnNumber(p);
                return;
            default:
                return;
        }
        AnnCanvas.Children.Add(_annDrawing);
        CaptureMouse(); // 画出窗口外也能继续跟踪
    }

    private void UpdateAnnShape(Point p)
    {
        var r = AnnNorm(_annDown, p);
        switch (_annActiveTool)
        {
            case AnnTool.Rect:
            case AnnTool.Mosaic:
                if (_annDrawing is System.Windows.Shapes.Rectangle rc)
                {
                    Canvas.SetLeft(rc, r.X); Canvas.SetTop(rc, r.Y);
                    rc.Width = r.Width; rc.Height = r.Height;
                }
                break;
            case AnnTool.Ellipse:
                if (_annDrawing is System.Windows.Shapes.Ellipse el)
                {
                    Canvas.SetLeft(el, r.X); Canvas.SetTop(el, r.Y);
                    el.Width = r.Width; el.Height = r.Height;
                }
                break;
            case AnnTool.Arrow:
                if (_annDrawing is System.Windows.Shapes.Path pa)
                    pa.Data = MiniPix.Core.ArrowGeom.Build(_annDown, p, AnnThickness);
                break;
        }
    }

    private void CompleteAnnShape()
    {
        ReleaseMouseCapture();
        // 马赛克：把虚线框换成框内像素化图块（同截图 CompleteShape）
        if (_annActiveTool == AnnTool.Mosaic && _annDrawing is System.Windows.Shapes.Rectangle mrc)
        {
            var r = new Rect(Canvas.GetLeft(mrc), Canvas.GetTop(mrc), mrc.Width, mrc.Height);
            AnnCanvas.Children.Remove(mrc);
            if (r.Width > 4 && r.Height > 4)
            {
                var mimg = new Image
                {
                    Source = MakeMosaicTile(r),
                    Width = r.Width,
                    Height = r.Height,
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false
                };
                // 像素化关键：小图放大时用最近邻插值（同截图的块状马赛克）
                RenderOptions.SetBitmapScalingMode(mimg, BitmapScalingMode.NearestNeighbor);
                Canvas.SetLeft(mimg, r.X); Canvas.SetTop(mimg, r.Y);
                AnnCanvas.Children.Add(mimg);
                _annShapes.Add(mimg);
            }
        }
        else
        {
            // 单击（几乎没拖动）的矩形/椭圆不算形状，丢弃
            bool tiny = _annDrawing is System.Windows.Shapes.Rectangle { Width: < 3, Height: < 3 }
                     || _annDrawing is System.Windows.Shapes.Ellipse { Width: < 3, Height: < 3 };
            if (_annDrawing != null && !tiny)
            {
                _annShapes.Add(_annDrawing);
                _annRedo.Clear(); // 新笔画清空重做栈（同截图）
            }
            else if (_annDrawing != null)
                AnnCanvas.Children.Remove(_annDrawing);
        }
        _annDrawing = null;
        _annPen = null;
        _mosaicTmp = null;
    }

    /// <summary>
    /// 生成贴图内 dipRect 区域的马赛克图块：从原图按显示比例取像素，
    /// 先缩小（减少细节），Image 显示时用 NearestNeighbor 放大 → 马赛克效果。
    /// </summary>
    private BitmapSource MakeMosaicTile(Rect dipRect)
    {
        double kx = _image.PixelWidth / AnnCanvas.ActualWidth;
        double ky = _image.PixelHeight / AnnCanvas.ActualHeight;
        int px = (int)Math.Clamp(Math.Round(dipRect.X * kx), 0, _image.PixelWidth - 1);
        int py = (int)Math.Clamp(Math.Round(dipRect.Y * ky), 0, _image.PixelHeight - 1);
        int pw = (int)Math.Clamp(Math.Round(dipRect.Width * kx), 1, _image.PixelWidth - px);
        int ph = (int)Math.Clamp(Math.Round(dipRect.Height * ky), 1, _image.PixelHeight - py);

        var crop = new CroppedBitmap(_image, new Int32Rect(px, py, pw, ph));
        int fw = (int)Math.Max(1, Math.Round(pw / AnnMosaicFactor));
        int fh = (int)Math.Max(1, Math.Round(ph / AnnMosaicFactor));
        return new TransformedBitmap(crop, new ScaleTransform((double)fw / pw, (double)fh / ph));
    }

    private void StartAnnText(Point p)
    {
        CommitAnnText(); // 若上一条还没提交先提交
        _annTextBox = new TextBox
        {
            MinWidth = 60,
            FontSize = AnnFontSize,
            FontFamily = new FontFamily("Microsoft YaHei, Segoe UI"),
            Foreground = AnnBrush,
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xEE, 0xEE, 0xEE)),
            BorderThickness = new Thickness(1),
            CaretBrush = Brushes.White
        };
        Canvas.SetLeft(_annTextBox, p.X);
        Canvas.SetTop(_annTextBox, p.Y);
        AnnCanvas.Children.Add(_annTextBox);
        _annTextBox.Focus();
        _annTextBox.KeyDown += (_, e2) =>
        {
            // 2026-11-02 修复：原来无条件 e2.Handled=true 会拦掉 TextInput 导致打不进字
            //（KeyDown 被 Handled 后 WPF 不再产生 TextInput）。只拦 Enter/Esc 防冒泡。
            if (e2.Key == Key.Enter) { CommitAnnText(); e2.Handled = true; }
            else if (e2.Key == Key.Escape) { AnnCanvas.Children.Remove(_annTextBox); _annTextBox = null; e2.Handled = true; }
        };
        _annTextBox.LostFocus += (_, _) => CommitAnnText();
    }

    /// <summary>把输入框转成 TextBlock 固定在画布上（空文本直接丢弃）</summary>
    private void CommitAnnText()
    {
        var tb = _annTextBox;
        _annTextBox = null;
        if (tb == null) return;
        AnnCanvas.Children.Remove(tb);
        if (string.IsNullOrWhiteSpace(tb.Text)) return;
        var label = new TextBlock
        {
            Text = tb.Text,
            FontSize = tb.FontSize,
            FontFamily = tb.FontFamily,
            Foreground = tb.Foreground
        };
        Canvas.SetLeft(label, Canvas.GetLeft(tb));
        Canvas.SetTop(label, Canvas.GetTop(tb));
        AnnCanvas.Children.Add(label);
        _annShapes.Add(label);
    }

    // ================= 2026-11-05 OCR 识文（v2 简化版：一键识别 → 结果弹窗） =================

    /// <summary>工具条 OCR 按钮：清注释选中 → 直接识别整张合成图（含注释，与复制语义一致）→ 结果弹窗</summary>
    private void TbOcr_Click(object sender, RoutedEventArgs e)
    {
        SetAnnTool(AnnTool.None);            // 关注释工具命中，防识别中误画
        var bmp = CompositeImage();          // 无注释时原样返回 _image
        if (bmp == null) return;
        DeskKit.Ocr.OcrService.RecognizeToWindowAsync(bmp, this);   // 2026-11-05：提示条居中贴图
    }

    /// <summary>
    /// 合成"原图 + 注释"的最终图（无注释时直接返回原图）。
    /// 画布按显示尺寸光栅化后缩放回图片像素尺寸。
    /// </summary>
    private BitmapSource CompositeImage()
    {
        CommitAnnText();
        if (AnnCanvas.Children.Count == 0) return _image;
        double dispW = AnnCanvas.ActualWidth, dispH = AnnCanvas.ActualHeight;
        if (dispW < 1 || dispH < 1) return _image;
        int pw = _image.PixelWidth, ph = _image.PixelHeight;

        // 1) 注释画布 → 显示尺寸的透明位图
        var annBmp = new RenderTargetBitmap(
            (int)Math.Round(dispW), (int)Math.Round(dispH), 96, 96, PixelFormats.Pbgra32);
        annBmp.Render(AnnCanvas);

        // 2) 原图 + 缩放后的注释 → 像素尺寸的成品
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(_image, new Rect(0, 0, pw, ph));
            dc.PushTransform(new ScaleTransform(pw / dispW, ph / dispH));
            dc.DrawImage(annBmp, new Rect(0, 0, dispW, dispH));
            dc.Pop();
        }
        var outBmp = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        outBmp.Render(visual);
        outBmp.Freeze(); // 2026-11-13 C-M3：冻结后可安全跨线程（复制到剪贴板/落盘后台线程）
        return outBmp;
    }
}
