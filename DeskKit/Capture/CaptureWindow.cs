using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MiniPix.Core;
using MiniPix.Native;
using MiniPix.Output;
using MiniPix.Pin;

namespace MiniPix.Capture;

public sealed partial class CaptureWindow : Window
{
    enum Phase { Selecting, Editing }
    enum Tool { None, Rect, Ellipse, Arrow, Pen, Text, Mosaic, Number }

    // --- captured source data (physical px) ---
    private BitmapSource _desktop = null!;
    private Point _originPx;
    private byte[] _pxData = Array.Empty<byte>();
    private int _pxW, _pxStride;

    private MonitorInfo _mon = null!;
    private List<WindowHit> _windows = new();
    private double _sx = 1, _sy = 1;

    // --- state ---
    private Phase _phase = Phase.Selecting;
    private Rect _sel = Rect.Empty;          // window-DIP space
    private bool _dragging;
    private Point _down;
    private Rect _snapPx = Rect.Empty;       // hovered snap bounds (virtual-desktop px)
    private Point _hoverDip;

    // --- editor ---
    private Tool _tool = Tool.None, _activeTool;
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xE0, 0x40, 0x3A), Color.FromRgb(0xF0, 0xA0, 0x20),
        Color.FromRgb(0xF5, 0xC5, 0x11), Color.FromRgb(0x33, 0xA9, 0x4C),
        Color.FromRgb(0x1E, 0x88, 0xE5), Color.FromRgb(0x00, 0x00, 0x00),
        Color.FromRgb(0xFF, 0xFF, 0xFF),
    };
    private int _colorIdx;
    private double[] _thicknesses = { 2, 4, 8 };
    private int _thicknessIdx = 0;   // 2026-11-12 用户要求：默认最小线宽
    private int _nextNumber = 1;   // 带圈数字编号计数器（2026-11-04 从预览注释移植；撤销时回退）
    private readonly List<UIElement> _shapes = new();
    private readonly List<UIElement> _redo = new();
    private UIElement? _drawing;
    private Polyline? _drawPen;
    private readonly List<Point> _penPoints = new();
    private TextBox? _textBox;
    private bool _inTextCommit;

    // ---- 2026-11-05 OCR 识文（v2 简化版）：编辑阶段点工具条按钮 → 直接识别当前选区 → 弹结果窗 ----
    // v1 的框选模式（_ocrMode/_ocrRect/_ocrHint/暗罩/拖框）已整体删除——用户反馈点 OCR 后全屏变灰像截图消失。

    // ---- 截图外观可调项（2026-10-30 用户要求集中配置） ----
    /// <summary>屏幕暗罩颜色：半透明灰色（原来是 0xB0 黑色）</summary>
    private static readonly Color DimColor = Color.FromArgb(0xA0, 0x60, 0x60, 0x60);
    /// <summary>自动识别的窗口是否显示罩（false = 只画边框，窗口内容保持原样）</summary>
    private const bool SnapWindowShowFill = false;
    /// <summary>自动识别窗口的边框颜色</summary>
    private static readonly Color SnapBorderColor = Color.FromRgb(0x1E, 0x88, 0xE5);
    /// <summary>自动识别窗口边框宽度</summary>
    private const double SnapBorderThickness = 2;

    // --- visual tree ---
    private Canvas _root = null!;
    private Image _bg = null!;
    private Path _dim = null!;
    private Rectangle _snapRect = null!;
    private Rectangle _selBorder = null!;
    private readonly List<Rectangle> _handles = new();
    private Rectangle _crossH = null!, _crossV = null!;
    private Border _magBorder = null!;
    private BitmapSource _monCrop = null!;
    private Canvas _magLayer = null!, _crossLayer = null!, _annLayer = null!, _handleLayer = null!;
    private Image _magImg = null!;
    private Canvas _magCross = null!;   // 2026-11-14 用户要求：放大镜中央十字准星
    private Border _sizeInfo = null!;   // 2026-11-12 选区尺寸框升级：宽/高输入框可编辑
    private System.Windows.Controls.TextBox _wBox = null!, _hBox = null!;
    // ---- 2026-10-30 放大镜信息框（用户参考截图样式：坐标 / 色块+rgb / 快捷键提示） ----
    private Border _magInfo = null!;
    private TextBlock _magPosText = null!, _magColorText = null!, _magHintText = null!;
    private System.Windows.Shapes.Rectangle _magSwatch = null!;
    private Color _lastSampleColor = Colors.Black;
    private bool _magHexFormat; // false = rgb(r, g, b)，true = #RRGGBB
    private Border _toolBar = null!;
    private Button _thickBtn = null!, _colorBtn = null!;
    private readonly Dictionary<Tool, Button> _toolButtons = new();
    private Popup? _colorPopup;
    // （v1 的 _ocrRect/_ocrHint 字段已随框选模式删除，无需占位）

    // ---- 2026-11-11 长截图（遮罩保持可见模式） ----
    /// <summary>长截图滚动模式：遮罩不隐藏，窗口在选区处挖洞（SetWindowRgn）——
    /// 选区内无本窗口像素，用户直接看到并交互真实页面（滚轮天然穿透，无需转发），
    /// BitBlt 拍选区拍到的是真实页面；遮罩/状态条在洞外正常显示与点击。</summary>
    private bool _longMode;
    private MiniPix.Capture.LongCapture.LongCaptureSession? _longSession;
    private Border _longBar = null!;       // 长截图状态条（状态文案 + 手动/自动/完成/取消）
    private TextBlock _longStatus = null!;
    private bool _longIsManual;            // 当前是否手动滚动模式
    private Rect _longSelPx;               // 进入长截图模式时记录的选区物理矩形（会话延迟到用户选模式才启动）

    // shape being dragged
    private Shape? _shapeTmp;
    private Rectangle? _mosaicTmp;
    private double _mosaicFactor = 6;

    /// <summary>Magnifier box size (DIP) and its zoom relative to physical pixels.</summary>
    private const int MagBox = 160;
    private const double MagZoom = 5;

    public void Run()
    {
        MiniPix.Core.Log.Write("capture Run: grabbing desktop");
        // 1) source data + window list before overlay shows
        (_desktop, _originPx) = CaptureEngine.CaptureVirtualDesktop();
        _pxW = _desktop.PixelWidth;
        _pxStride = _pxW * 4;
        var tmp = new byte[_pxStride * _desktop.PixelHeight];
        _desktop.CopyPixels(tmp, _pxStride, 0);
        _pxData = tmp;

        _mon = MonitorManager.AtPoint(CursorPhysical().X, CursorPhysical().Y);
        _windows = WindowEnumerator.GetSnappableWindows();

        // 2) window chrome
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "MiniPix 截图";
        Background = Brushes.Black;
        Cursor = Cursors.Arrow;
        Content = BuildRoot();

        Show();
        MiniPix.Core.Log.Write("capture Run: Show() done");
        var src = (HwndSource)PresentationSource.FromVisual(this)!;
        if (!_hooked) { _hooked = true; src.AddHook(WndProc); }
        ImeHelper.Disable(src.Handle);
        NativeMethods.SetWindowPos(src.Handle, NativeMethods.HWND_TOPMOST,
            (int)_mon.Int32Rect.X, (int)_mon.Int32Rect.Y,
            (int)_mon.Int32Rect.Width, (int)_mon.Int32Rect.Height,
            NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.BringWindowToTop(src.Handle);
        NativeMethods.SetForegroundWindow(src.Handle);
        double dpi = NativeMethods.GetDpiForWindow(src.Handle);
        _sx = dpi / 96.0;
        _sy = dpi / 96.0;

        var cropPx = CaptureEngine.Crop(_desktop, _originPx, _mon.PxRect);
        _monCrop = cropPx;
        _bg.Width = _mon.Int32Rect.Width / _sx;
        _bg.Height = _mon.Int32Rect.Height / _sy;
        _bg.Source = cropPx;
        _magImg.Source = cropPx;

        Keyboard.Focus(this);
        UpdateUi();
        MiniPix.Core.Log.Write($"capture Run: ready mon=({_mon.Int32Rect.X},{_mon.Int32Rect.Y},{_mon.Int32Rect.Width}x{_mon.Int32Rect.Height}) scale={_sx} cursor={CursorPhysical()} active={IsActive} fg={NativeMethods.GetForegroundWindow() == src.Handle}");
    }

    /// <summary>
    /// Raw VK codes are needed because an active CJK IME reports letter keys as
    /// Key.ImeProcessed instead of the physical key.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) _lastVk = wParam.ToInt32();

        // 2026-11-11 长截图：用户点页面后本窗口失焦，回来点状态条按钮的第一次点击
        // 会被系统吞掉只用于激活窗口（MA_ACTIVATEANDEAT）→ 表现为"按钮没反应"。
        // 改为 MA_ACTIVATE：激活但不吞鼠标，第一次点击就能触发按钮。
        const int WM_MOUSEACTIVATE = 0x0021;
        const int MA_ACTIVATE = 1;
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_ACTIVATE);
        }
        return IntPtr.Zero;
    }

    /// <summary>窗口关闭（含 Alt+F4）：停掉长截图会话轮询并清除窗口挖洞 region（窗口即将销毁，region 随之释放，显式清一次保险）。</summary>
    protected override void OnClosed(EventArgs e)
    {
        if (_longMode)
        {
            _longMode = false;
            _longSession?.Stop();
            _longSession = null;
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                NativeMethods.SetWindowRgn(hwnd, IntPtr.Zero, false);
            }
            catch { }
        }
        base.OnClosed(e);
    }

    /// <summary>Physical cursor position on the virtual desktop.</summary>
    private static Point CursorPhysical()
    {
        if (System.Windows.Forms.Cursor.Position is System.Drawing.Point pt)
            return new Point(pt.X, pt.Y);
        return new Point(0, 0);
    }

    // ================= UI build =================

    private Canvas BuildRoot()
    {
        // Canvas root so all overlay elements can be positioned absolutely (Canvas.Left/Top).
        _root = new Canvas();
        _bg = new Image { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_bg, BitmapScalingMode.NearestNeighbor);
        _root.Children.Add(_bg);

        _dim = new Path { Fill = new SolidColorBrush(DimColor) };
        _root.Children.Add(_dim);

        _snapRect = new Rectangle
        {
            Stroke = new SolidColorBrush(SnapBorderColor),
            StrokeThickness = SnapBorderThickness,
            // 2026-10-30 用户要求：自动识别的窗口不再显示罩，只留边框
            Fill = SnapWindowShowFill
                ? new SolidColorBrush(Color.FromArgb(0x33, 0x1E, 0x88, 0xE5))
                : null,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _root.Children.Add(_snapRect);

        _selBorder = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)),
            StrokeThickness = 1.5,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _root.Children.Add(_selBorder);

        _handleLayer = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        foreach (var h in new[] { 0, 1, 2, 3, 4, 5, 6, 7 })
        {
            var r = new Rectangle
            {
                Width = 7, Height = 7,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)),
                StrokeThickness = 1
            };
            _handles.Add(r);
            _handleLayer.Children.Add(r);
        }
        _root.Children.Add(_handleLayer);

        _annLayer = new Canvas { Background = null };
        _root.Children.Add(_annLayer);

        _crossLayer = new Canvas { IsHitTestVisible = false };
        var crossBrush = new SolidColorBrush(Color.FromArgb(0x90, 0x1E, 0x88, 0xE5));
        crossBrush.Freeze();
        _crossH = new Rectangle { Stroke = crossBrush, StrokeThickness = 1, Height = 1 };
        _crossV = new Rectangle { Stroke = crossBrush, StrokeThickness = 1, Width = 1 };
        _crossLayer.Children.Add(_crossH);
        _crossLayer.Children.Add(_crossV);
        _root.Children.Add(_crossLayer);

        // magnifier
        _magLayer = new Canvas { IsHitTestVisible = false };
        _magImg = new Image
        {
            Width = MagBox, Height = MagBox,
            Stretch = Stretch.Fill,
            Visibility = Visibility.Collapsed
        };
        RenderOptions.SetBitmapScalingMode(_magImg, BitmapScalingMode.NearestNeighbor);
        var magBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)),
            BorderThickness = new Thickness(2),
            Width = MagBox,
            Height = MagBox,
            Visibility = Visibility.Collapsed
        };
        _magBorder = magBorder;

        // 2026-11-14 十字准星：放大镜中央一小段（二改：细线 1px、长 24px，白线黑边）
        _magCross = new Canvas
        {
            Width = MagBox, Height = MagBox,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        var reticleWhite = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        reticleWhite.Freeze();
        var reticleBlack = new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0x00, 0x00));
        reticleBlack.Freeze();
        const double retHalf = 12;                        // 准星半长：总长 24px
        foreach (var (stroke, w) in new[] { (reticleBlack, 2.2), (reticleWhite, 1.0) })
        {
            _magCross.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = MagBox / 2.0 - retHalf, Y1 = MagBox / 2.0, X2 = MagBox / 2.0 + retHalf, Y2 = MagBox / 2.0,
                Stroke = stroke, StrokeThickness = w
            });
            _magCross.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = MagBox / 2.0, Y1 = MagBox / 2.0 - retHalf, X2 = MagBox / 2.0, Y2 = MagBox / 2.0 + retHalf,
                Stroke = stroke, StrokeThickness = w
            });
        }
        // 信息框（参考用户截图样式）：第一行坐标，第二行色块+颜色值，第三行快捷键提示
        // 2026-11-04 风格统一：原黑底白字 → 浅色白底 + 浅灰边 + 深灰字（与工具条同风格）
        var magPanel = new StackPanel { Orientation = Orientation.Vertical };
        _magPosText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)), FontSize = 16,
            Padding = new Thickness(6, 3, 6, 3),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var magColorLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _magSwatch = new System.Windows.Shapes.Rectangle
        {
            Width = 16, Height = 16, Margin = new Thickness(6, 3, 0, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Stroke = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)), StrokeThickness = 1
        };
        _magColorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)), FontSize = 16,
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        magColorLine.Children.Add(_magSwatch);
        magColorLine.Children.Add(_magColorText);
        _magHintText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98)), FontSize = 11,
            Padding = new Thickness(6, 1, 6, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Text = "Shift: 切换颜色格式\nC: 复制颜色值"
        };
        magPanel.Children.Add(_magPosText);
        magPanel.Children.Add(magColorLine);
        magPanel.Children.Add(_magHintText);
        _magInfo = new Border
        {
            Child = magPanel,
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE1)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Visibility = Visibility.Collapsed
        };
        _magLayer.Children.Add(_magImg);
        _magLayer.Children.Add(_magCross);      // 2026-11-14 十字准星：图片之上、蓝框之下
        _magLayer.Children.Add(magBorder);
        _magLayer.Children.Add(_magInfo);
        _root.Children.Add(_magLayer);

        // 2026-11-12 选区尺寸框：宽/高可直接输入（回车生效，锚定选区左上角，越界自动收敛到屏内）
        System.Windows.Controls.TextBox NumBox(string tip)
        {
            var tb = new System.Windows.Controls.TextBox
            {
                Width = 56, Height = 24, FontSize = 14, // 2026-11-12 用户要求：字号加大（11→14）
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                ToolTip = tip,
                Margin = new Thickness(3, 0, 3, 0),
            };
            tb.KeyDown += SizeBoxKeyDown;
            return tb;
        }
        _wBox = NumBox("选区宽（px）——输入后按回车生效");
        _hBox = NumBox("选区高（px）——输入后按回车生效");
        var sizePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        sizePanel.Children.Add(new TextBlock
        {
            Text = "宽", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, // 2026-11-12 随输入框字号加大
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98)),
            Margin = new Thickness(4, 0, 0, 0),
        });
        sizePanel.Children.Add(_wBox);
        sizePanel.Children.Add(new TextBlock
        {
            Text = "高", FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98)),
        });
        sizePanel.Children.Add(_hBox);
        sizePanel.Children.Add(new TextBlock
        {
            Text = "px 回车生效", FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98)),
            Margin = new Thickness(0, 0, 4, 0),
        });
        _sizeInfo = new Border
        {
            Child = sizePanel,
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE1)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Visibility = Visibility.Collapsed
        };
        _root.Children.Add(_sizeInfo);

        // 2026-11-05 v1 的 OCR 框选亮框/提示条已随框选模式删除（字段 _ocrRect 保留但不再构建）

        // toolbar
        _toolBar = BuildToolBar();
        _toolBar.Visibility = Visibility.Collapsed;
        _root.Children.Add(_toolBar);

        // 2026-11-11 长截图状态条（状态文案 + 完成/取消），复用工具条的定位逻辑
        _longBar = BuildLongBar();
        _root.Children.Add(_longBar);

        // mouse events on window content
        PreviewMouseDown += OnMouseDown;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseUp += OnMouseUp;
        PreviewMouseRightButtonUp += OnRightClick;
        PreviewMouseWheel += OnWheel;
        PreviewKeyDown += OnKeyDown;
        return _root;
    }

    private Border BuildToolBar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };

        // 2026-11-04 工具条统一：截图/贴图/预览三处完全一致 —— 浅色白底 + Material Symbols Rounded 矢量图标
        // （原深底 #E6222222 + 蓝边 + 文字符号 □○↗✎T▦；基准样式同 AnnotationEditor 预览工具条）
        // 文字按钮 Tb 仅供"细/中/粗"线宽用（文字比图标直观，用户确认）
        Button Tb(string content, string tip, Action act)
        {
            var b = new Button
            {
                Content = content,
                ToolTip = tip,
                Width = 28, Height = 26,
                FontSize = 13,
                FontFamily = new FontFamily("Microsoft YaHei, Segoe UI"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)), // 深灰图标字（浅色工具条配深色图标）
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Focusable = false,
                Cursor = Cursors.Hand
            };
            b.Click += (_, _e) => act();
            panel.Children.Add(b);
            return b;
        }

        // 图标按钮：Material 矢量 Path，Fill 绑 Foreground（颜色钮换色时图标自动跟随）
        // 2026-11-12 二修：stroke > 0 时同时绑定 Stroke 到 Foreground（OCR/长截图图标加粗）
        // 2026-11-12 三修：size 参数可单独调图标视觉大小（颜色/长截图图标偏小，放大到 18）
        Button TbI(Geometry g, string tip, Action act, double stroke = 0, double size = 15)
        {
            var path = DeskKit.Icons.MakePath(g, size, stroke);
            var b = new Button
            {
                ToolTip = tip,
                Width = 28, Height = 26,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Focusable = false,
                Cursor = Cursors.Hand
            };
            path.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding("Foreground") { Source = b });
            if (stroke > 0)
                path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = b });
            b.Content = path;
            b.Click += (_, _e) => act();
            panel.Children.Add(b);
            return b;
        }

        void ToolBtn(Tool t, Geometry g, string tip)
        {
            var b = TbI(g, tip, () => SetTool(_tool == t ? Tool.None : t));
            _toolButtons[t] = b;
        }

        ToolBtn(Tool.Rect, DeskKit.Icons.Rect, "矩形");
        ToolBtn(Tool.Ellipse, DeskKit.Icons.Ellipse, "椭圆");
        ToolBtn(Tool.Arrow, DeskKit.Icons.Arrow, "箭头");
        ToolBtn(Tool.Pen, DeskKit.Icons.Pen, "画笔");
        ToolBtn(Tool.Text, DeskKit.Icons.Text, "文字 (点击输入, Enter/失焦完成)");
        ToolBtn(Tool.Mosaic, DeskKit.Icons.Mosaic, "马赛克");
        // 2026-11-04 补齐：数字编号工具（原来只在预览注释有，工具条统一后三处一致）
        ToolBtn(Tool.Number, DeskKit.Icons.Number, "数字编号：点击图上位置放一个带圈数字，自动 1、2、3… 递增");

        // 2026-11-12 三修：颜色图标放大到 18（视觉偏小），线宽经 stroke 加大
        var colorBtn = TbI(DeskKit.Icons.Palette, "颜色", () => ToggleColorPopup(), stroke: 0.6, size: 18);
        _colorBtn = colorBtn;
        _colorBtn.Foreground = new SolidColorBrush(Palette[_colorIdx]);
        // 2026-11-12 用户要求：颜色图标不跟随当前颜色（不再变红），固定与其他图标一致的灰色；
        // Foreground 仍记录当前颜色供换色逻辑使用，只解绑图标的 Fill/Stroke
        if (colorBtn.Content is Path cpColor)
        {
            System.Windows.Data.BindingOperations.ClearBinding(cpColor, Path.FillProperty);
            System.Windows.Data.BindingOperations.ClearBinding(cpColor, Path.StrokeProperty);
            var gray = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52));
            cpColor.Fill = gray;
            cpColor.Stroke = gray;
        }

        // 2026-11-04 用户要求：线宽也用图标，当前档位在 tooltip 显示
        // 2026-11-12 二改：三档各用一个图标（横杠细/中/粗），切换时同步换图标
        static string WidthTip(int idx) => $"线宽：{new[] { "细", "中", "粗" }[idx]} (点击切换)";

        var thickBtn = TbI(DeskKit.Icons.LineWidthSet[_thicknessIdx], WidthTip(_thicknessIdx), () =>
        {
            _thicknessIdx = (_thicknessIdx + 1) % _thicknesses.Length;
            _thickBtn.ToolTip = WidthTip(_thicknessIdx);
            if (_thickBtn.Content is Path p) p.Data = DeskKit.Icons.LineWidthSet[_thicknessIdx];
            if (_tool != Tool.None) _activeTool = _tool;
        });
        _thickBtn = thickBtn;

        TbI(DeskKit.Icons.Undo, "撤销 (Ctrl+Z)", UndoAll);
        TbI(DeskKit.Icons.Redo, "重做", RedoAll);
        // 2026-11-05 OCR 识文：点后重新框选一块区域，只识别框内文字（复制剪贴板+气泡）
        // 2026-11-12 二修：stroke 加粗线宽
        TbI(DeskKit.Icons.Ocr, "OCR 识文：识别当前选区文字，弹出结果窗", StartOcr, stroke: 1.0);
        // 2026-11-11 长截图：点后挖洞保持遮罩，状态条选「手动/自动」开始滚动拼接
        // 2026-11-12 图标换成用户提供的 long.svg（四角框选），并加箭头子路径 + stroke 加粗
        TbI(DeskKit.Icons.Long, "长截图：程序自动滚动页面逐段拼接，到底自动完成", StartLongCapture, stroke: 1.1, size: 18);
        // 2026-11-14 吸管取色（用户要求+用户提供图标）：点击进入取色模式，光标变十字，
        // 再点截图任意位置采样该点颜色（HEX 入剪贴板 + 小气泡反馈），取完自动退出；Esc/再点按钮取消
        _pickBtn = TbI(DeskKit.Icons.Eyedropper, "吸管取色：实时显示鼠标处颜色 · 点按或 C 复制 · Shift 切 HEX/RGB · Esc 退出", TogglePickColor, stroke: 0.4);
        TbI(DeskKit.Icons.Pin, "贴图 (P)", () => Finish(pin: true));
        TbI(DeskKit.Icons.Save, "保存到文件 (S)", QuickSave);
        TbI(DeskKit.Icons.Copy, "复制到剪贴板 (Enter/双击)", () => Finish(copy: true));
        TbI(DeskKit.Icons.Close, "取消 (Esc/Esc)", Close);

        var border = new Border
        {
            Child = panel,
            // 2026-11-04 工具条统一：浅色白底 + 浅灰边（原深底 #E6222222 + 蓝边）
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE1)),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed
        };
        return border;
    }

    // ---- 2026-11-11 长截图状态条 ----

    /// <summary>长截图状态条：状态文案 + 完成/取消按钮（浅色工具条同款视觉）。</summary>
    private Border BuildLongBar()
    {
        _longStatus = new TextBlock
        {
            Text = "选手动或自动开始拼接",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Margin = new Thickness(8, 0, 4, 0),
        };
        Button Tb2(string label, string tip, Action act)
        {
            var b = new Button
            {
                Content = label,
                ToolTip = tip,
                Margin = new Thickness(4, 0, 0, 0),
                Padding = new Thickness(10, 4, 10, 4),
                Cursor = Cursors.Hand,
                Focusable = false, // 不抢焦点，Esc 仍由窗口键盘处理
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE1)),
                BorderThickness = new Thickness(1),
            };
            b.Click += (_, _) =>
            {
                MiniPix.Core.Log.Write($"longcap: bar button '{label}' clicked");
                act();
            };
            return b;
        }
        var manual = Tb2("手动", "自己滚动页面拼接（挖洞穿透，滚轮直达页面）", () => StartOrSwitchSession(true));
        var auto = Tb2("自动", "程序逐段自动滚动拼接，到底停下等你确认", () => StartOrSwitchSession(false));
        var finish = Tb2("完成", "结束拼接并保存（进暂存架+剪贴板）", () =>
        {
            if (_longSession != null) _longSession.Finish();
            else _longStatus.Text = "尚未开始——先点「手动」或「自动」";
        });
        var cancel = Tb2("取消", "回到编辑模式调整选区（Esc）", () => CancelLongCapture());

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_longStatus);
        panel.Children.Add(manual);
        panel.Children.Add(auto);
        panel.Children.Add(finish);
        panel.Children.Add(cancel);

        return new Border
        {
            Child = panel,
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE1)),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
        };
    }

    /// <summary>状态条定位：与编辑工具条同逻辑（选区下方优先，上方兜底）。</summary>
    private void PositionLongBar()
    {
        _longBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = _longBar.DesiredSize.Width;
        double x = Math.Clamp(_sel.X, 0, Math.Max(0, ActualWidth - w));
        double y = _sel.Bottom + 34 < ActualHeight ? _sel.Bottom + 8 : Math.Max(0, _sel.Y - 40);
        Canvas.SetLeft(_longBar, x);
        Canvas.SetTop(_longBar, y);
    }

    private void ToggleColorPopup()
    {
        if (_colorPopup == null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
            for (int i = 0; i < Palette.Length; i++)
            {
                int idx = i;
                var rect = new Rectangle
                {
                    Width = 20, Height = 20, Margin = new Thickness(2),
                    Fill = new SolidColorBrush(Palette[idx]),
                    Stroke = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
                    StrokeThickness = 1,
                    Cursor = Cursors.Hand
                };
                rect.MouseLeftButtonUp += (_, e2) =>
                {
                    _colorIdx = idx;
                    ApplyColorUi();
                    _colorPopup!.IsOpen = false;
                    e2.Handled = true;
                };
                sp.Children.Add(rect);
            }
            _colorPopup = new Popup
            {
                Child = new Border
                {
                    // 2026-11-04 工具条统一：颜色弹窗改浅色（原深底 #F0222222）
                    Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE1)),
                    Child = sp
                },
                PlacementTarget = _toolBar,
                Placement = PlacementMode.Bottom
            };
        }
        _colorPopup.IsOpen = !_colorPopup.IsOpen;
    }

    private void ApplyColorUi()
    {
        // the colour swatch button shows the current annotation colour
        _colorBtn.Foreground = new SolidColorBrush(Palette[_colorIdx]);
    }

    private void SetTool(Tool t)
    {
        MiniPix.Core.Log.Write($"SetTool {t} (was {_tool})");
        _tool = t;
        foreach (var kv in _toolButtons)
            kv.Value.Background = kv.Key == t && t != Tool.None
                ? new SolidColorBrush(Color.FromArgb(0x44, 0x1E, 0x88, 0xE5))
                : Brushes.Transparent;
        if (_tool != Tool.None) _activeTool = _tool;
    }

    // ================= coordinate helpers =================

    private Point DipToPx(Point dip) =>
        new(_mon.Int32Rect.X + dip.X * _sx, _mon.Int32Rect.Y + dip.Y * _sy);

    private Point PxToDip(Point px) =>
        new((px.X - _mon.Int32Rect.X) / _sx, (px.Y - _mon.Int32Rect.Y) / _sy);

    private Rect PxToDip(Rect px) =>
        new((px.X - _mon.Int32Rect.X) / _sx, (px.Y - _mon.Int32Rect.Y) / _sy,
            px.Width / _sx, px.Height / _sy);

    private static Rect Norm(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private Point GetPos(MouseEventArgs e) => e.GetPosition(this);

    /// <summary>True when the point lies over the floating toolbar.</summary>
    private bool HitToolBar(Point p)
    {
        if (_toolBar.Visibility != Visibility.Visible) return false;
        double x = Canvas.GetLeft(_toolBar), y = Canvas.GetTop(_toolBar);
        if (double.IsNaN(x) || double.IsNaN(y)) return false;
        return new Rect(x, y, _toolBar.ActualWidth, _toolBar.ActualHeight).Contains(p);
    }

    // ================= interaction =================

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 长截图模式：只放行状态条内部的点击（完成/取消按钮），其余全部拦截
        // （防止拖动选区、框新选区破坏拼接基准）。
        // 注意 handler 挂在 PreviewMouseDown（tunneling）：在这里 Handled 会阻止
        // MouseLeftButtonDown 冒泡到按钮 → Click 永不触发，所以必须精确按源放行。
        if (_longMode)
        {
            if (e.OriginalSource is DependencyObject src && _longBar.IsAncestorOf(src))
                return;                                  // 状态条内部：放行给按钮
            e.Handled = true;
            return;
        }
        // 2026-11-12 尺寸输入框内部点击放行：输入框悬在选区边界上方（选区外），
        // 不放行会走"选区外 → 开始新框选"分支，把当前选区清空（表现为一点就变成最大）
        if (e.OriginalSource is DependencyObject szSrc && _sizeInfo.IsAncestorOf(szSrc))
            return;
        if (e.ChangedButton != MouseButton.Left) return;
        var p = GetPos(e);
        _down = p;

        // 2026-11-14 吸管取色：取色模式点截图任意位置 = 采样该点颜色。
        // 工具条内点击放行（还要能用按钮退出取色/切其他功能）
        if (_picking)
        {
            if (_phase == Phase.Editing && HitToolBar(p)) return;
            PickColorAt(p);
            e.Handled = true;
            return;
        }

        if (_phase == Phase.Editing && HitToolBar(p)) return;

        // 2026-11-12 遮罩内双击 = 复制按钮（tooltip"Enter/双击"承诺的兑现）；
        // 需在 HitToolBar 之后（工具条内双击不触发）且在选区内
        if (_phase == Phase.Editing && e.ClickCount == 2 && _sel.Contains(p))
        {
            Finish(copy: true);
            return;
        }

        if (_phase == Phase.Editing && _tool != Tool.None && !_inTextCommit)
        {
            StartShape(p);
            return;
        }

        if (_phase == Phase.Editing)
        {
            var edge = HitSelectionHandle(p);
            if (edge != -99)
            {
                _dragging = true;
                _startSelEdge = edge;
                _startSel = _sel;
                return;
            }
            if (_sel.Contains(p))
            {
                _dragging = true;
                _startSelEdge = -2; // move
                _startSel = _sel;
                return;
            }
            // outside selection => start new region
            _dragging = true;
            _startSelEdge = -99;
            _startSel = Rect.Empty;
            _sel = Rect.Empty;
            _phase = Phase.Selecting;
            SetTool(Tool.None);
            HideEditorChrome();
            return;
        }

        _dragging = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_longMode) return; // 长截图模式：不做任何选区交互（状态条按钮 hover 不受影响）

        var p = GetPos(e);
        _hoverDip = p;

        if (_phase == Phase.Selecting)
        {
            if (_dragging)
            {
                _sel = Norm(_down, p);
                _snapPx = Rect.Empty;
            }
            else
            {
                _snapPx = FindSnapPx(DipToPx(p));
                _sel = Rect.Empty;
            }
            UpdateHoverUi(p);
        }
        else if (_picking)
        {
            // 2026-11-14 吸管模式：放大镜+颜色值实时跟随鼠标（用户要求与框选阶段一致）
            UpdateHoverUi(p);
        }
        else if (_dragging && _tool == Tool.None)
        {
            MoveOrResizeSelection(p);
        }
        else if (_drawing != null && _tool == Tool.Pen && _drawPen != null)
        {
            _penPoints.Add(ToCanvasDip(p));
            _drawPen.Points = new PointCollection(_penPoints);
        }
        else if (_drawing != null)
        {
            UpdateShape(p);
        }

        if (_phase == Phase.Editing && _tool == Tool.None)
            UpdateCursorForHandle(p);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_longMode) return; // 长截图模式：无选区交互
        var p = GetPos(e);
        if (_phase == Phase.Selecting)
        {
            if (_dragging && (_sel.Width * _sel.Height < 36 || _sel.Width < 6))
            {
                // click: snap to hovered window, else whole monitor
                _sel = _snapPx.IsEmpty ? new Rect(0, 0, ActualWidth, ActualHeight)
                                       : PxToDip(_snapPx);
            }
            _dragging = false;
            if (_sel.Width >= 6 && _sel.Height >= 6) EnterEditing();
            else { _sel = Rect.Empty; UpdateUi(); }
            return;
        }

        if (_tool != Tool.None && _drawing != null)
        {
            CompleteShape(p);
            return;
        }
        _dragging = false;
        _startSelEdge = 0;
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_longMode) { e.Handled = true; return; } // 长截图模式：右键不再关闭整窗
        if (_tool != Tool.None) { SetTool(Tool.None); return; }
        Close();
        e.Handled = true;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        // 2026-11-11 长截图：选区已挖洞，选区内滚轮根本不进本窗口（天然穿透到页面窗口）。
        // 这里只可能收到洞外（暗罩区）的滚轮，无事可做，标记已处理即可。
        if (_longMode) e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        Key k = e.Key;
        if (k == Key.ImeProcessed && _lastVk != 0) k = KeyInterop.KeyFromVirtualKey(_lastVk);
        MiniPix.Core.Log.Write($"key {e.Key} -> {k} (vk={_lastVk}) phase={_phase} tool={_tool}");
        if (e.OriginalSource is TextBox) return;

        // 长截图模式：Esc 取消回编辑模式，其余键全部拦截（防止 P/S/Enter 等误触发完成动作）
        if (_longMode)
        {
            e.Handled = true;
            if (k == Key.Escape) CancelLongCapture();
            return;
        }

        // 2026-11-14 吸管取色模式：Shift 切格式 / C 复制当前值 / Esc 退出取色（不关窗）
        if (_picking)
        {
            if (k == Key.LeftShift || k == Key.RightShift)
            {
                _magHexFormat = !_magHexFormat;
                _magColorText.Text = FormatSampleColor(_lastSampleColor);
                e.Handled = true;
                return;
            }
            if (k == Key.C)
            {
                // 2026-11-14：直接复制信息框正在显示的值（所见即所得），
                // 不重新采样——鼠标在按 C 前若移动过，重采样会与显示值不一致
                string text = _magColorText.Text;
                try { Clipboard.SetText(text); } catch { /* 剪贴板被占不崩 */ }
                MiniPix.Core.Log.Write($"pick color: {text}");
                ShowPickToast(_hoverDip, _lastSampleColor, text);
                StopPick();
                e.Handled = true;
                return;
            }
            StopPick();
            e.Handled = true;
            return;
        }

        if (k == Key.Escape) { Close(); return; }

        if (_phase == Phase.Selecting)
        {
            if (k == Key.Enter)
            {
                _sel = !_snapPx.IsEmpty ? PxToDip(_snapPx) : new Rect(0, 0, ActualWidth, ActualHeight);
                _dragging = false;
                EnterEditing();
            }
            // 2026-10-30 取色快捷键（选区阶段）：
            else if (k == Key.LeftShift || k == Key.RightShift)
            {
                _magHexFormat = !_magHexFormat;
                _magColorText.Text = FormatSampleColor(_lastSampleColor);
                e.Handled = true;
            }
            else if (k == Key.C)
            {
                // 选区阶段 C = 复制颜色值（进入编辑阶段后 C 仍是"复制截图"）
                try { Clipboard.SetText(FormatSampleColor(_lastSampleColor)); } catch { /* 剪贴板被占时不崩 */ }
                e.Handled = true;
            }
            return;
        }

        // 2026-11-13 C-M6：先判 Ctrl+Shift（Redo）再判纯 Ctrl（Undo）。
        // 旧写法 bool ctrl = Modifiers == Control 在 Shift 同按时为 false，
        // Ctrl+Shift+Z 两个分支都进不去（重做快捷键形同虚设）
        var mods = Keyboard.Modifiers;
        bool ctrlOnly = mods == ModifierKeys.Control;
        bool ctrlShift = mods == (ModifierKeys.Control | ModifierKeys.Shift);
        switch (k)
        {
            case Key.Enter: Finish(copy: true); break;
            case Key.P: Finish(pin: true); break;
            case Key.S when ctrlOnly: SaveAs(); break;
            case Key.S: QuickSave(); break;
            case Key.C when !ctrlOnly: Finish(copy: true); break;
            case Key.Z when ctrlShift: RedoAll(); break;
            case Key.Y when ctrlOnly:
            case Key.Z when ctrlOnly: UndoAll(); break;
            case Key.R: SetTool(Tool.Rect); break;
            case Key.O: SetTool(Tool.Ellipse); break;
            case Key.A: SetTool(Tool.Arrow); break;
            case Key.D: SetTool(Tool.Pen); break;
            case Key.T: SetTool(Tool.Text); break;
            case Key.B: SetTool(Tool.Mosaic); break;
            case Key.N: SetTool(Tool.Number); break; // 2026-11-04：数字编号
        }
    }

    // ================= snap / hover / ui =================

    private Rect FindSnapPx(Point px)
    {
        foreach (var w in _windows)
        {
            var r = w.PxBounds;
            if (r.X >= _mon.Int32Rect.X && r.X + r.Width <= _mon.Int32Rect.X + _mon.Int32Rect.Width &&
                r.Y >= _mon.Int32Rect.Y && r.Y + r.Height <= _mon.Int32Rect.Y + _mon.Int32Rect.Height)
            { }
            else continue;
            if (r.Contains(px) && r.Width < _mon.Int32Rect.Width) return r;
        }
        if (_mon.Int32Rect.Contains(px)) return _mon.Int32Rect;
        return Rect.Empty;
    }

    private void UpdateHoverUi(Point dip)
    {
        _crossH.Width = ActualWidth;
        _crossV.Height = ActualHeight;
        Canvas.SetLeft(_crossH, 0); Canvas.SetTop(_crossH, Math.Round(dip.Y));
        Canvas.SetLeft(_crossV, Math.Round(dip.X)); Canvas.SetTop(_crossV, 0);
        _crossH.Visibility = _crossV.Visibility =
            _phase == Phase.Selecting || _picking ? Visibility.Visible : Visibility.Collapsed;

        // magnifier follows cursor (2026-11-14：吸管模式也显示——实时颜色值 + C/Shift 快捷键)
        if (_phase == Phase.Selecting || _picking)
        {
            var px = DipToPx(dip);
            int pxX = (int)Math.Round(px.X), pxY = (int)Math.Round(px.Y);
            int rw = Math.Max(1, (int)Math.Round(MagBox * _sx / MagZoom));
            int rh = Math.Max(1, (int)Math.Round(MagBox * _sy / MagZoom));
            int relX = Math.Clamp(pxX - (int)_mon.Int32Rect.X - rw / 2, 0,
                Math.Max(0, _monCrop.PixelWidth - rw));
            int relY = Math.Clamp(pxY - (int)_mon.Int32Rect.Y - rh / 2, 0,
                Math.Max(0, _monCrop.PixelHeight - rh));
            _magImg.Source = new CroppedBitmap(_monCrop, new Int32Rect(relX, relY, rw, rh));

            double mx = Math.Clamp(dip.X + 16, 0, Math.Max(0, ActualWidth - MagBox));
            double my = Math.Clamp(dip.Y + 16, 0, Math.Max(0, ActualHeight - MagBox - 20));
            Canvas.SetLeft(_magImg, mx); Canvas.SetTop(_magImg, my);
            Canvas.SetLeft(_magBorder, mx); Canvas.SetTop(_magBorder, my);
            Canvas.SetLeft(_magCross, mx); Canvas.SetTop(_magCross, my);   // 准星随放大镜
            Canvas.SetLeft(_magInfo, mx); Canvas.SetTop(_magInfo, my + MagBox);
            _magImg.Visibility = _magBorder.Visibility = _magCross.Visibility = _magInfo.Visibility = Visibility.Visible;

            var c = SamplePhysical(pxX, pxY);
            _lastSampleColor = c;
            _magPosText.Text = $"({pxX}, {pxY})";
            _magColorText.Text = FormatSampleColor(c);
            _magSwatch.Fill = new SolidColorBrush(c);
        }
        else
        {
            _magImg.Visibility = _magBorder.Visibility = _magCross.Visibility = _magInfo.Visibility = Visibility.Collapsed;
        }
        UpdateUi();
    }

    /// <summary>当前取色显示格式：rgb(r, g, b) 或 #RRGGBB（Shift 切换，2026-10-30）</summary>
    private string FormatSampleColor(Color c) => _magHexFormat
        ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
        : $"rgb({c.R}, {c.G}, {c.B})";

    /// <summary>Colour of a physical-desktop pixel, taken from the frozen capture.</summary>
    private Color SamplePhysical(int pxX, int pxY)
    {
        int x = pxX - (int)_originPx.X, y = pxY - (int)_originPx.Y;
        if (x < 0 || y < 0 || x >= _pxW || y >= _pxData.Length / _pxStride)
            return Colors.Black;
        int i = y * _pxStride + x * 4;          // BGRA
        return Color.FromRgb(_pxData[i + 2], _pxData[i + 1], _pxData[i]);
    }

    // ================= 吸管取色（2026-11-14 用户要求+用户提供图标） =================

    private bool _picking;                  // 取色模式：光标十字，下一击采样颜色
    private Button _pickBtn = null!;        // 工具条上的吸管按钮（选中高亮用）
    private Border? _pickToast;             // 取色结果小气泡（1.5s 自动消失）

    private void TogglePickColor()
    {
        if (!_picking)
        {
            _picking = true;
            Cursor = Cursors.Cross;
            _pickBtn.Background = new SolidColorBrush(Color.FromArgb(0x44, 0x1E, 0x88, 0xE5));
            UpdateHoverUi(_hoverDip);         // 立即显示放大镜+颜色值（不等鼠标移动）
            MiniPix.Core.Log.Write("pick color: on");
        }
        else StopPick();
    }

    private void StopPick()
    {
        _picking = false;
        Cursor = Cursors.Arrow;
        _pickBtn.Background = Brushes.Transparent;
        if (_pickToast != null) { _root.Children.Remove(_pickToast); _pickToast = null; }
        // 吸管专属显示收起（放大镜/准星/信息框/十字线只在取色与框选阶段出现）
        _crossH.Visibility = _crossV.Visibility = Visibility.Collapsed;
        _magImg.Visibility = _magBorder.Visibility = _magCross.Visibility = _magInfo.Visibility = Visibility.Collapsed;
    }

    /// <summary>采样 dip 处像素（取自冻结截图 _pxData，不受遮罩影响），按当前格式复制 + 气泡反馈，取完退出。</summary>
    private void PickColorAt(Point dip)
    {
        var px = DipToPx(dip);
        var c = SamplePhysical((int)Math.Round(px.X), (int)Math.Round(px.Y));
        string text = FormatSampleColor(c);   // 2026-11-14：复制当前显示格式（Shift 切换）
        try { Clipboard.SetText(text); } catch { /* 剪贴板被占不崩 */ }
        MiniPix.Core.Log.Write($"pick color: {text}");
        ShowPickToast(dip, c, text);
        StopPick();
    }

    private void ShowPickToast(Point dip, Color c, string hex)
    {
        if (_pickToast != null) _root.Children.Remove(_pickToast);
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = 12, Height = 12, RadiusX = 2, RadiusY = 2,
            Fill = new SolidColorBrush(c),
            Stroke = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE1)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text = hex, FontSize = 11.5, Margin = new Thickness(5, 0, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x38, 0x3F)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        _pickToast = new Border
        {
            Child = sp, Background = Brushes.White,
            CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 4, 7, 4),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE1)),
            BorderThickness = new Thickness(1),
        };
        _root.Children.Add(_pickToast);
        Canvas.SetLeft(_pickToast, Math.Clamp(dip.X + 10, 0, Math.Max(0, ActualWidth - 130)));
        Canvas.SetTop(_pickToast, Math.Clamp(dip.Y - 30, 0, Math.Max(0, ActualHeight - 30)));
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        t.Tick += (_, _) => { if (_pickToast != null) { _root.Children.Remove(_pickToast); _pickToast = null; } t.Stop(); };
        t.Start();
    }

    private void UpdateUi()
    {
        // 2026-11-05 v2：OCR 改为一键识别（点按钮直接出结果窗），框选模式的暗罩分支已删除
        var hole = Rect.Empty;
        if (_phase == Phase.Selecting)
        {
            if (!_sel.IsEmpty) hole = _sel;
            else if (!_snapPx.IsEmpty) hole = PxToDip(_snapPx);
            if (!_snapPx.IsEmpty)
            {
                var d = PxToDip(_snapPx);
                Canvas.SetLeft(_snapRect, d.X); Canvas.SetTop(_snapRect, d.Y);
                _snapRect.Width = d.Width; _snapRect.Height = d.Height;
            }
            _snapRect.Visibility = _sel.IsEmpty && !_snapPx.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            _selBorder.Visibility = Visibility.Collapsed;
            if (_sel.IsEmpty)
            {
                _sizeInfo.Visibility = Visibility.Collapsed;
            }
            else
            {
                PositionSizeInfo();
            }
        }
        else
        {
            hole = _sel;
            _snapRect.Visibility = Visibility.Collapsed;
        }

        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        Geometry dim = full;
        if (!hole.IsEmpty)
        {
            var holeG = new RectangleGeometry(hole);
            dim = new CombinedGeometry(GeometryCombineMode.Exclude, full, holeG);
        }
        _dim.Data = dim;

        if (_phase == Phase.Editing)
        {
            _selBorder.Visibility = Visibility.Visible;
            Canvas.SetLeft(_selBorder, _sel.X); Canvas.SetTop(_selBorder, _sel.Y);
            _selBorder.Width = _sel.Width; _selBorder.Height = _sel.Height;
            // 长截图模式：只保留选区边框，编辑手柄/尺寸信息/工具条不显示
            if (_longMode)
            {
                PositionLongBar();
                return;
            }
            _handleLayer.Visibility = Visibility.Visible;
            LayoutHandles();
            PositionToolBar();
            PositionSizeInfo();
        }
    }

    // ================= 2026-11-12 选区宽高可设置 =================

    /// <summary>尺寸框跟随选区位置并刷新数值（用户正在输入时不覆盖文本）</summary>
    private void PositionSizeInfo()
    {
        Canvas.SetLeft(_sizeInfo, _sel.X);
        Canvas.SetTop(_sizeInfo, Math.Max(0, _sel.Y - 28));
        if (!_wBox.IsFocused && !_hBox.IsFocused)
        {
            _wBox.Text = ((int)Math.Round(_sel.Width * _sx)).ToString();
            _hBox.Text = ((int)Math.Round(_sel.Height * _sy)).ToString();
        }
        _sizeInfo.Visibility = Visibility.Visible;
    }

    private void SizeBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplySizeBoxes(); e.Handled = true; }
        else if (e.Key == Key.Escape)   // Esc：放弃输入恢复原值（TextBox 聚焦时窗口不收 Esc，这里自己处理）
        {
            _wBox.Text = ((int)Math.Round(_sel.Width * _sx)).ToString();
            _hBox.Text = ((int)Math.Round(_sel.Height * _sy)).ToString();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    /// <summary>按输入值重设选区（遮罩开口）：锚定左上角，最小 8px，越界自动收敛到屏内</summary>
    private void ApplySizeBoxes()
    {
        if (_sel.IsEmpty) return;
        bool okW = int.TryParse(_wBox.Text.Trim(), out var wPx);
        bool okH = int.TryParse(_hBox.Text.Trim(), out var hPx);
        double w = okW ? wPx / _sx : _sel.Width;
        double h = okH ? hPx / _sy : _sel.Height;
        w = Math.Clamp(w, 8, Math.Max(8, ActualWidth - _sel.X));
        h = Math.Clamp(h, 8, Math.Max(8, ActualHeight - _sel.Y));
        _sel = new Rect(_sel.X, _sel.Y, w, h);
        Keyboard.ClearFocus();
        UpdateUi();
    }

    private void LayoutHandles()
    {
        (double x, double y, double w, double h) = (_sel.X, _sel.Y, _sel.Width, _sel.Height);
        Point[] pts =
        {
            new(x, y), new(x + w / 2, y), new(x + w, y), new(x + w, y + h / 2),
            new(x + w, y + h), new(x + w / 2, y + h), new(x, y + h), new(x, y + h / 2),
        };
        for (int i = 0; i < 8; i++)
        {
            Canvas.SetLeft(_handles[i], pts[i].X - 3.5);
            Canvas.SetTop(_handles[i], pts[i].Y - 3.5);
        }
    }

    private void PositionToolBar()
    {
        _toolBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = _toolBar.DesiredSize.Width;
        double x = Math.Clamp(_sel.X, 0, Math.Max(0, ActualWidth - w));
        double y = _sel.Bottom + 46 < ActualHeight ? _sel.Bottom + 8 : Math.Max(0, _sel.Y - 42);
        Canvas.SetLeft(_toolBar, x);
        Canvas.SetTop(_toolBar, y);
    }

    // ================= selection move/resize =================

    private int _startSelEdge;    // -2 move, >=0 handle, -99 none
    private Rect _startSel;

    private int HitSelectionHandle(Point p)
    {
        for (int i = 0; i < 8; i++)
        {
            var h = _handles[i];
            var r = new Rect(Canvas.GetLeft(h) - 2, Canvas.GetTop(h) - 2, 11, 11);
            if (r.Contains(p)) return i;
        }
        return -99;
    }

    private void MoveOrResizeSelection(Point p)
    {
        var d = p - _down;
        var s = _startSel;
        double nx = s.X, ny = s.Y, nw = s.Width, nh = s.Height;
        int e = _startSelEdge;
        if (e == -2) { nx += d.X; ny += d.Y; }
        else if (e >= 0)
        {
            // 2026-11-13 C-H3 重写：按手柄角色直接判定动哪条边。
            // 手柄编号：0=NW 1=N 2=NE 3=E 4=SE 5=S 6=SW 7=W。
            // 旧实现多个分支串行累加，N/NE/E/SE/W 五个手柄边映射错乱
            // （如 E 会同时动左边和高度、N 会拉宽度）。
            bool left   = e is 0 or 6 or 7;
            bool right  = e is 2 or 3 or 4;
            bool top    = e is 0 or 1 or 2;
            bool bottom = e is 4 or 5 or 6;
            if (left)   { nx += d.X; nw -= d.X; }
            if (right)  { nw += d.X; }
            if (top)    { ny += d.Y; nh -= d.Y; }
            if (bottom) { nh += d.Y; }
            // 最小尺寸：动左/上边时锚定对边，避免选区"粘"在最小值上继续漂移
            if (nw < 10) { nw = 10; if (left) nx = s.Right - 10; }
            if (nh < 10) { nh = 10; if (top) ny = s.Bottom - 10; }
        }
        nx = Math.Clamp(nx, 0, ActualWidth - nw);
        ny = Math.Clamp(ny, 0, ActualHeight - nh);
        _sel = new Rect(nx, ny, nw, nh);
        UpdateUi();
    }

    private void UpdateCursorForHandle(Point p)
    {
        var hit = HitSelectionHandle(p);
        Cursor = hit switch
        {
            0 or 4 => Cursors.SizeNWSE, 2 or 6 => Cursors.SizeNESW,
            1 or 5 => Cursors.SizeNS, 3 or 7 => Cursors.SizeWE,
            -99 => _sel.Contains(p) ? Cursors.SizeAll : Cursors.Arrow,
            _ => Cursors.Arrow
        };
    }

    // ================= annotations =================

    private Point ToCanvasDip(Point dip) => dip; // canvas is full-window, identical coordinate space

    private Brush StrokeBrush => new SolidColorBrush(Palette[_colorIdx]);
    private double StrokeWidth => _thicknesses[_thicknessIdx];

    private void StartShape(Point p)
    {
        _activeTool = _tool;
        switch (_tool)
        {
            case Tool.Rect:
                _shapeTmp = new Rectangle { Stroke = StrokeBrush, StrokeThickness = StrokeWidth, Fill = null };
                _annLayer.Children.Add(_shapeTmp);
                _drawing = _shapeTmp;
                break;
            case Tool.Ellipse:
                _shapeTmp = new Ellipse { Stroke = StrokeBrush, StrokeThickness = StrokeWidth };
                _annLayer.Children.Add(_shapeTmp);
                _drawing = _shapeTmp;
                break;
            case Tool.Arrow:
                _shapeTmp = new Path { Stroke = StrokeBrush, StrokeThickness = StrokeWidth };
                _annLayer.Children.Add(_shapeTmp);
                _drawing = _shapeTmp;
                break;
            case Tool.Pen:
                _penPoints.Clear();
                _penPoints.Add(ToCanvasDip(p));
                _drawPen = new Polyline { Stroke = StrokeBrush, StrokeThickness = StrokeWidth, StrokeLineJoin = PenLineJoin.Round };
                _drawPen.Points = new PointCollection(_penPoints);
                _annLayer.Children.Add(_drawPen);
                _drawing = _drawPen;
                break;
            case Tool.Mosaic:
                // 2026-11-12 用户反馈：原 #AAEEEE 1px 浅色虚线在浅底上看不见（"框选不显范围"）
                // → 与 AnnotationEditor 一致：当前颜色 1.5px 虚线 + 22 透明白填充，任何底色都醒目
                _mosaicTmp = new Rectangle
                {
                    Stroke = StrokeBrush,
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection(new[] { 4.0, 2.5 }),
                    Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))
                };
                _annLayer.Children.Add(_mosaicTmp);
                _drawing = _mosaicTmp;
                break;
            case Tool.Text:
                StartText(p);
                break;
            case Tool.Number:
                // 2026-11-04 移植：点击放置带圈数字，非拖动形状（_drawing 保持 null，不进 CompleteShape）
                PlaceNumber(p);
                break;
        }
    }

    /// <summary>带圈数字编号（2026-11-04 从预览注释 AnnotationEditor.PlaceNumber 移植，样式/字号一致）：
    /// 白底圆圈 + 当前颜色边框和数字，自动 1、2、3… 递增；撤销刚放的编号时序号回退（见 UndoAll）。</summary>
    private void PlaceNumber(Point p)
    {
        int n = _nextNumber++;
        // 2026-11-04 反馈修复：原两位数只加宽（26→36×26）变成椭圆；改为直径随位数增大、字号微缩，始终保持正圆
        int digits = n.ToString().Length;
        double size = ShotShelf.Annotation.AnnDefaults.NumberSize + (digits - 1) * 5;   // 1位Ø26 / 2位Ø31 / 3位Ø36
        double fontSize = Math.Max(10, 14 - digits);               // 13 / 12 / 11
        var brush = StrokeBrush;
        var grid = new Grid
        {
            Width = size,
            Height = size,
            Tag = n,                              // 记下号码，撤销时回退计数器用
            IsHitTestVisible = false,             // 放好后不挡下面的绘制
            Children =
            {
                new Ellipse
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
        _annLayer.Children.Add(grid);
        _shapes.Add(grid);
        _redo.Clear();
    }

    private void UpdateShape(Point p)
    {
        var r = Norm(_down, p);
        switch (_activeTool)
        {
            case Tool.Rect:
            case Tool.Mosaic:
                if (_drawing is Rectangle rc)
                {
                    Canvas.SetLeft(rc, r.X); Canvas.SetTop(rc, r.Y);
                    rc.Width = r.Width; rc.Height = r.Height;
                }
                break;
            case Tool.Ellipse:
                if (_drawing is Ellipse el)
                {
                    Canvas.SetLeft(el, r.X); Canvas.SetTop(el, r.Y);
                    el.Width = r.Width; el.Height = r.Height;
                }
                break;
            case Tool.Arrow:
                if (_drawing is Path path)
                    // 2026-11-04 统一：三线开放式箭头 + 头随线宽（原"杆+封口三角"头最大 14px，视觉像单侧）
                    path.Data = MiniPix.Core.ArrowGeom.Build(_down, p, StrokeWidth);
                break;
        }
    }

        // 2026-11-13：原 BuildArrowGeometry（杆+封口三角旧箭头，2026-11-04 起弃用）已删除，
        // 统一用 MiniPix.Core.ArrowGeom.Build（三线开放式 + 头随线宽）。

    private void CompleteShape(Point p)
    {
        if (_activeTool == Tool.Mosaic && _drawing is Rectangle rc)
        {
            var r = new Rect(Canvas.GetLeft(rc), Canvas.GetTop(rc), rc.Width, rc.Height);
            if (r.Width > 4 && r.Height > 4)
            {
                var src = MakeMosaic(r);
                var img = new Image
                {
                    Source = src,
                    Width = r.Width,
                    Height = r.Height,
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(img, r.X); Canvas.SetTop(img, r.Y);
                _annLayer.Children.Remove(rc);
                _annLayer.Children.Add(img);
                _shapes.Add(img);
            }
            else _annLayer.Children.Remove(rc);
        }
        else if (_drawing != null && _activeTool != Tool.Pen)
        {
            _shapes.Add(_drawing);
        }
        else if (_drawPen != null && _penPoints.Count > 1)
        {
            _shapes.Add(_drawPen);
        }
        _drawing = null;
        _shapeTmp = null;
        _drawPen = null;
        _redo.Clear();
    }

    private BitmapSource MakeMosaic(Rect dipRect)
    {
        var pxRect = new Rect(
            _mon.Int32Rect.X + dipRect.X * _sx,
            _mon.Int32Rect.Y + dipRect.Y * _sy,
            dipRect.Width * _sx, dipRect.Height * _sy);
        var crop = CaptureEngine.Crop(_desktop, _originPx, pxRect);
        int fw = (int)Math.Max(1, Math.Round(crop.PixelWidth / _mosaicFactor));
        int fh = (int)Math.Max(1, Math.Round(crop.PixelHeight / _mosaicFactor));
        var small = new WriteableBitmap(fw, fh, 96, 96, PixelFormats.Bgra32, null);
        // downsample nearest
        var srcStride = crop.PixelWidth * 4;
        var srcBuf = new byte[srcStride * crop.PixelHeight];
        crop.CopyPixels(srcBuf, srcStride, 0);
        var dstBuf = new byte[fw * 4 * fh];
        for (int y = 0; y < fh; y++)
        for (int x = 0; x < fw; x++)
        {
            int sy = Math.Min(crop.PixelHeight - 1, (int)(y * (double)crop.PixelHeight / fh));
            int sx = Math.Min(crop.PixelWidth - 1, (int)(x * (double)crop.PixelWidth / fw));
            int s = (sy * srcStride + sx * 4), d = (y * fw + x) * 4;
            dstBuf[d] = srcBuf[s]; dstBuf[d + 1] = srcBuf[s + 1];
            dstBuf[d + 2] = srcBuf[s + 2]; dstBuf[d + 3] = srcBuf[s + 3];
        }
        small.WritePixels(new Int32Rect(0, 0, fw, fh), dstBuf, fw * 4, 0);
        small.Freeze();

        // upscale nearest back to original pixel size
        int OW = crop.PixelWidth, OH = crop.PixelHeight;
        var outBuf = new byte[OW * 4 * OH];
        for (int y = 0; y < OH; y++)
        for (int x = 0; x < OW; x++)
        {
            int bx = Math.Min(fw - 1, (int)(x * (double)fw / OW));
            int by = Math.Min(fh - 1, (int)(y * (double)fh / OH));
            int s = (by * fw + bx) * 4, d = (y * OW + x) * 4;
            outBuf[d] = dstBuf[s];
            outBuf[d + 1] = dstBuf[s + 1];
            outBuf[d + 2] = dstBuf[s + 2];
            outBuf[d + 3] = dstBuf[s + 3];
        }
        var wb = new WriteableBitmap(OW, OH, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, OW, OH), outBuf, OW * 4, 0);
        wb.Freeze();
        return wb;
    }

    private bool _hooked;
    private int _lastVk;

    private void StartText(Point p)
    {
        ImeHelper.Enable(new WindowInteropHelper(this).Handle);

        _textBox = new TextBox
        {
            Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)),
            Foreground = StrokeBrush,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xEE, 0xEE, 0xEE)),
            FontSize = Math.Max(14, StrokeWidth * 6),
            MinWidth = 40,
            CaretBrush = Brushes.White
        };
        Canvas.SetLeft(_textBox, p.X); Canvas.SetTop(_textBox, p.Y);
        _annLayer.Children.Add(_textBox);
        _inTextCommit = true;
        _textBox.Focus();
        _textBox.KeyDown += (_, e2) =>
        {
            if (e2.Key == Key.Enter || e2.Key == Key.Escape) CommitText();
        };
        _textBox.LostFocus += (_, _e) => CommitText();
    }

    private void CommitText()
    {
        _inTextCommit = false;
        ImeHelper.Disable(new WindowInteropHelper(this).Handle);
        if (_textBox == null) return;
        var tb = _textBox;
        _textBox = null;
        string text = tb.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            _annLayer.Children.Remove(tb);
            return;
        }
        var txb = new TextBlock
        {
            Text = text,
            Foreground = tb.Foreground,
            FontSize = tb.FontSize
        };
        double x = Canvas.GetLeft(tb), y = Canvas.GetTop(tb);
        _annLayer.Children.Remove(tb);
        Canvas.SetLeft(txb, x); Canvas.SetTop(txb, y + 2);
        _annLayer.Children.Add(txb);
        _shapes.Add(txb);
    }

    private void UndoAll()
    {
        if (_shapes.Count == 0) return;
        var last = _shapes[^1];
        _shapes.RemoveAt(_shapes.Count - 1);
        _annLayer.Children.Remove(last);
        _redo.Add(last);
        // 2026-11-04：撤销的是带圈编号时，序号回退（与预览注释一致）
        if (last is Grid g && g.Tag is int n && n == _nextNumber - 1) _nextNumber = n;
    }

    private void RedoAll()
    {
        if (_redo.Count == 0) return;
        var last = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _shapes.Add(last);
        _annLayer.Children.Add(last);
    }

    // ================= enter/finish editing =================

    private void EnterEditing()
    {
        CommitText();
        _phase = Phase.Editing;
        SetTool(Tool.None);
        _crossH.Visibility = _crossV.Visibility = Visibility.Collapsed;
        _magImg.Visibility = _magBorder.Visibility = Visibility.Collapsed;
        _magInfo.Visibility = Visibility.Collapsed;
        _snapRect.Visibility = Visibility.Collapsed;
        _toolBar.Visibility = Visibility.Visible;
        UpdateUi();
    }

    private void HideEditorChrome()
    {
        _toolBar.Visibility = Visibility.Collapsed;
        _selBorder.Visibility = Visibility.Collapsed;
        _handleLayer.Visibility = Visibility.Collapsed;
        _sizeInfo.Visibility = Visibility.Collapsed;
    }

    // ================= 2026-11-05 OCR 识文（v2 简化版：一键识别选区 → 结果弹窗） =================
    // v1 框选方案（StartOcrRegion/CancelOcr/EndOcrDrag/OcrPxRect/_ocrMode 暗罩拖框）已删除，
    // 完整旧实现见 backup_20261105_before_ocr_simplify/DeskKit_Capture_CaptureWindow.cs

    /// <summary>工具条 OCR 按钮：直接识别当前选区的原始屏像素（不含注释层，"识别屏幕文字"语义），结果弹窗展示</summary>
    private void StartOcr()
    {
        if (_phase != Phase.Editing || _sel.IsEmpty) return;
        CommitText();                      // 若文字输入未提交先落定
        var crop = CaptureEngine.Crop(_desktop, _originPx, SelectionPxRect());
        DeskKit.Ocr.OcrService.RecognizeToWindowAsync(crop, this);   // 2026-11-05：提示条居中截图窗
    }

    private Rect SelectionPxRect()
    {
        var r = new Rect(
            _mon.Int32Rect.X + _sel.X * _sx,
            _mon.Int32Rect.Y + _sel.Y * _sy,
            _sel.Width * _sx, _sel.Height * _sy);
        var vs = NativeMethods.GetVirtualScreen();
        r.Intersect(new Rect(vs.X, vs.Y, vs.Width, vs.Height));
        return new Rect(Math.Round(r.X), Math.Round(r.Y),
            Math.Round(r.Width), Math.Round(r.Height));
    }

    // ================= 2026-11-11 长截图（手动滚动拼接，遮罩保持可见） =================

    /// <summary>工具条"长截图"按钮：遮罩不隐藏，窗口在选区处挖洞（SetWindowRgn）。
    /// 选区内没有本窗口像素 → 用户直接看到真实页面，滚轮/鼠标天然穿透到页面窗口
    /// （无需任何转发代码），BitBlt 拍选区拍到的就是真实页面。
    /// 状态条显示在选区外（洞外的暗罩上），可正常点击；完成 → 进暂存架+复制剪贴板；
    /// 取消 → 清洞回到编辑模式。</summary>
    private void StartLongCapture()
    {
        if (_phase != Phase.Editing || _sel.IsEmpty || _longMode) return;
        CommitText();                      // 若文字输入未提交先落定

        var selPx = SelectionPxRect();     // 虚拟桌面物理矩形
        if (selPx.IsEmpty || selPx.Width < 10 || selPx.Height < 10) return;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        ApplySelectionHole(hwnd, selPx);   // 挖洞：选区内完全透出真实页面

        _longMode = true;
        _longSelPx = selPx;
        _longSession = null;                               // 会话延迟到用户点「手动/自动」才创建启动
        MiniPix.Core.Log.Write($"longcap: enter mode (region hole) selPx={selPx}");

        // 切 UI：编辑工具条/手柄/尺寸信息隐藏；状态条显示（选区外）
        _toolBar.Visibility = Visibility.Collapsed;
        _handleLayer.Visibility = Visibility.Collapsed;
        _sizeInfo.Visibility = Visibility.Collapsed;
        if (_colorPopup != null) _colorPopup.IsOpen = false;
        _longBar.Visibility = Visibility.Visible;
        PositionLongBar();                                 // 先显示再定位（Collapsed 时 DesiredSize=0）
    }

    /// <summary>点「手动/自动」：尚未启动则按所选模式创建并启动会话；
    /// 已在拼接中则切换滚动方式（手动↔自动随时切，不重来）。</summary>
    private void StartOrSwitchSession(bool manual)
    {
        _longIsManual = manual;
        if (_longSession == null)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            _longSession = new MiniPix.Capture.LongCapture.LongCaptureSession(_longSelPx, _mon, hwnd, manual,
                onStatus: s => _longStatus.Text = s,               // 状态条进度
                onCompleted: img =>
                {
                    // 完成（UI 线程）：先清洞恢复遮罩，再进暂存架 + 复制剪贴板
                    ExitLongMode();
                    global::DeskKit.App.AddImageToShelf(img);
                    try
                    {
                        global::DeskKit.App.CopyImageToClipboardSuppressed(img);
                        global::DeskKit.App.Notifier($"长截图完成 {img.PixelWidth}×{img.PixelHeight}，已进暂存架和剪贴板");
                    }
                    catch (Exception ex)
                    {
                        // 剪贴板被占用等：暂存架已收到，只提示复制失败
                        MiniPix.Core.Log.Write("longcap: clipboard copy failed: " + ex.Message);
                        global::DeskKit.App.Notifier($"长截图完成 {img.PixelWidth}×{img.PixelHeight}，已进暂存架（复制剪贴板失败）");
                    }
                    Close();                   // 此时才真正关闭 → CaptureController._active 清空
                },
                onCancelled: () => ExitLongMode());            // 取消：清洞回到编辑模式，可重新长截图
            _longSession.Start();
            MiniPix.Core.Log.Write($"longcap: session launched (manual={manual})");
        }
        else
        {
            _longSession.SetManual(manual);
            MiniPix.Core.Log.Write($"longcap: switched mode (manual={manual})");
        }
    }

    /// <summary>把窗口 region 设为"整窗口 − 选区矩形"：选区处挖出真洞。
    /// SetWindowRgn 成功后 region 归系统所有，无需（也不应）DeleteObject。</summary>
    private void ApplySelectionHole(IntPtr hwnd, Rect selPx)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var wr)) return;
        IntPtr all = NativeMethods.CreateRectRgn(0, 0, wr.Right - wr.Left, wr.Bottom - wr.Top);
        IntPtr hole = NativeMethods.CreateRectRgn(
            (int)(selPx.X - wr.Left), (int)(selPx.Y - wr.Top),
            (int)(selPx.Right - wr.Left), (int)(selPx.Bottom - wr.Top));
        NativeMethods.CombineRgn(all, all, hole, NativeMethods.RGN_DIFF);
        NativeMethods.DeleteObject(hole);
        // SetWindowRgn 成功后 region 归系统所有；失败（返回 0）所有权未转移，须手动释放
        if (NativeMethods.SetWindowRgn(hwnd, all, true) == 0)
            NativeMethods.DeleteObject(all);
    }

    /// <summary>清除窗口挖洞 region（窗口恢复完整覆盖，长截图前状态）。</summary>
    private static void ClearSelectionHole(IntPtr hwnd)
    {
        NativeMethods.SetWindowRgn(hwnd, IntPtr.Zero, true);
    }

    /// <summary>退出长截图模式：清除挖洞 region + 隐藏状态条。
    /// 关窗口时直接 Close；否则恢复编辑 UI（选区不变，可重新点长截图）。</summary>
    private void ExitLongMode()
    {
        if (!_longMode) return;
        _longMode = false;
        _longSession = null;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        try { ClearSelectionHole(hwnd); }
        catch { }

        _longBar.Visibility = Visibility.Collapsed;

        // 恢复编辑 UI：工具条 + 手柄 + 尺寸信息（选区保持不变）
        _toolBar.Visibility = Visibility.Visible;
        UpdateUi();
    }

    /// <summary>取消长截图（Esc / 取消按钮）：丢弃拼接结果，回编辑模式。
    /// 会话尚未启动时直接退（用户只是想调整遮罩，没产生任何数据）。</summary>
    private void CancelLongCapture()
    {
        if (_longSession == null) { ExitLongMode(); return; }
        _longSession.Cancel();    // 会话 onCancelled 回调里调 ExitLongMode
    }

    /// <summary>Composite final image = screen crop + annotations.</summary>
    private BitmapSource RenderSelection()
    {
        CommitText();
        var pxSel = SelectionPxRect();
        var crop = CaptureEngine.Crop(_desktop, _originPx, pxSel);
        double dipW = pxSel.Width / _sx, dipH = pxSel.Height / _sy;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(crop, new Rect(0, 0, dipW, dipH));
            // annotations live in the full-screen overlay canvas; map the selection
            // region of that visual into the output rect
            var ann = new VisualBrush(_annLayer)
            {
                Stretch = Stretch.None,
                ViewboxUnits = BrushMappingMode.Absolute,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = _sel,
                Viewport = new Rect(0, 0, dipW, dipH),
            };
            dc.DrawRectangle(ann, null, new Rect(0, 0, dipW, dipH));
        }
        int w = Math.Max(1, (int)pxSel.Width), h = Math.Max(1, (int)pxSel.Height);
        var rt = new RenderTargetBitmap(w, h, _sx * 96, _sy * 96, PixelFormats.Pbgra32);
        rt.Render(dv);
        rt.Freeze();
        return rt;
    }

    private void Finish(bool copy = false, bool pin = false)
    {
        var img = RenderSelection();
        // 2026-10-30 用户要求：截图完成即进暂存架（复制/贴图与否都进）。
        // 若同时复制，随后剪贴板事件是同一张图，暂存架 2 秒指纹去重会挡掉，不会收两张
        global::DeskKit.App.AddImageToShelf(img);
        if (copy)
        {
            // 2026-10-30 二次修复：不再用抑制标志（那会把截图进暂存架的正路也堵死），
            // 直接写剪贴板，"同图两次通知"由暂存架的图像指纹去重解决
            // 2026-11-13 C-M2：SetImage 不再抛异常；失败气泡提示，窗口照常关闭
            if (MiniPix.Output.ClipboardUtil.SetImage(img))
                global::DeskKit.App.Notifier("已复制到剪贴板");
            else
                global::DeskKit.App.Notifier("复制失败：剪贴板被其它程序占用（图片已在暂存架）");
        }
        if (pin)
        {
            var pxSel = SelectionPxRect();
            // 2026-11-04 完全重合：传截图屏 DPI scale（位图 DPI 已含它），同屏钉住时缩放=1，位置尺寸与选区逐像素一致
            MiniPix.Core.Log.Write($"CAP pin selPx={pxSel} _sx={_sx:0.###} imgPx={img.PixelWidth}x{img.PixelHeight} imgDpi=({img.DpiX:0.#},{img.DpiY:0.#})");
            PinWindow.Show(img, new Point(pxSel.X, pxSel.Y), _sx);
        }
        Close();
    }

    private void QuickSave()
    {
        // 2026-11-04 用户要求：保存按钮改为弹出另存对话框选位置（原静默存到自动目录）
        SaveAs();
    }

    private void SaveAs()
    {
        var img = RenderSelection();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG 图片|*.png",
            FileName = SaveUtil.NextFileName(),
            DefaultExt = ".png"
        };
        if (dlg.ShowDialog() == true)
        {
            global::DeskKit.App.AddImageToShelf(img); // 2026-11-04：保存的截图同样进暂存架（原 QuickSave 语义保留）
            SaveUtil.SavePng(img, dlg.FileName);
            global::DeskKit.App.Notifier("已保存: " + dlg.FileName);
            Close();
        }
    }
}
