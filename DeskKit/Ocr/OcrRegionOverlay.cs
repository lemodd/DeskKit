// OcrRegionOverlay.cs — 可复用 OCR 框选覆盖层（2026-11-05 OCR 功能）
// Canvas 子类，供预览窗/贴图窗复用：放进"与底图同一缩放上下文"的容器 Grid 里
// （自动拉伸到图片显示尺寸），激活后接管鼠标：暗罩 + 亮框拖选。
// 松手抛 RegionSelected（坐标 = 本画布 DIP），宿主用 kx = 底图.PixelWidth / ActualWidth
// 换算回图像像素 —— 与注释马赛克 MakeMosaicTile 同款公式，zoom/DPI 一并消化。
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DeskKit.Ocr
{
    public sealed class OcrRegionOverlay : Canvas
    {
        private readonly Path _dim;        // 全区暗罩（拖动时在选框处挖洞）
        private readonly Rectangle _rect;  // 亮框（蓝边）
        private bool _dragging;
        private Point _down;

        /// <summary>松手且框有效时触发（画布 DIP 坐标，未归一化前的宽高已保证为正）</summary>
        public event Action<Rect>? RegionSelected;

        /// <summary>Esc/右键取消时触发（宿主借它做收尾，如恢复提示文字）</summary>
        public event Action? Canceled;

        /// <summary>是否处于框选激活状态（宿主用它守卫滚轮/拖窗/快捷键等手势）</summary>
        public bool Active { get; private set; }

        public OcrRegionOverlay()
        {
            Visibility = Visibility.Collapsed;
            IsHitTestVisible = false;
            ClipToBounds = true;

            _dim = new Path
            {
                Fill = new SolidColorBrush(OcrDefaults.DimColor),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _rect = new Rectangle
            {
                Stroke = new SolidColorBrush(OcrDefaults.SelBorderColor),
                StrokeThickness = OcrDefaults.SelBorderThickness,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            Children.Add(_dim);
            Children.Add(_rect);

            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            MouseRightButtonUp += (_, e) => { Cancel(); e.Handled = true; };
        }

        /// <summary>激活框选：铺满宿主、显示全暗罩、十字光标。宿主须保证此时布局已完成。</summary>
        public void Begin()
        {
            Active = true;
            _dragging = false;
            _rect.Visibility = Visibility.Collapsed;
            // 宿主若刚改变布局，先强制测量，否则 ActualWidth 可能还是旧值/0
            UpdateLayout();
            RefreshDim();
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;
            Cursor = Cursors.Cross;
        }

        /// <summary>取消框选：隐藏并恢复，触发 Canceled 事件。</summary>
        public void Cancel()
        {
            bool was = Active;
            Active = false;
            Visibility = Visibility.Collapsed;
            IsHitTestVisible = false;
            Cursor = null;
            if (was) Canceled?.Invoke();
        }

        /// <summary>按当前显示尺寸重铺暗罩（宿主尺寸变化时也调）</summary>
        private void RefreshDim()
        {
            double w = Math.Max(1, ActualWidth), h = Math.Max(1, ActualHeight);
            SetCurrentValue(WidthProperty, w);   // Canvas 在 Grid 里默认不自动填满时兜底
            SetCurrentValue(HeightProperty, h);
            _dim.Data = new RectangleGeometry(new Rect(0, 0, w, h));
            _dim.Visibility = Visibility.Visible;
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (!Active) return;
            _down = e.GetPosition(this);
            _dragging = true;
            CaptureMouse();
            e.Handled = true;   // 全程吞事件：贴图窗防拖窗、预览窗防中键平移的关键
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!Active || !_dragging) return;
            var p = e.GetPosition(this);
            var r = Normalized(_down, p);

            // 亮框
            _rect.Visibility = Visibility.Visible;
            SetLeft(_rect, r.X);
            SetTop(_rect, r.Y);
            _rect.Width = r.Width;
            _rect.Height = r.Height;

            // 暗罩挖洞（Exclude 组合几何，风格同 CaptureWindow.UpdateUi 的选区挖洞）
            double w = Math.Max(1, ActualWidth), h = Math.Max(1, ActualHeight);
            _dim.Data = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(0, 0, w, h)),
                new RectangleGeometry(r));
            e.Handled = true;
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!Active || !_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            e.Handled = true;

            var r = Normalized(_down, e.GetPosition(this));
            // 框太小（画布 DIP 尺寸 < 最小像素的一半，粗略防误点）：直接取消不发请求
            if (r.Width < OcrDefaults.MinRegionPx / 2.0 || r.Height < OcrDefaults.MinRegionPx / 2.0)
            {
                Cancel();
                return;
            }
            Active = false;              // UI 立即恢复（宿主随后的识别是异步的，不阻塞）
            Visibility = Visibility.Collapsed;
            IsHitTestVisible = false;
            Cursor = null;
            RegionSelected?.Invoke(r);
        }

        /// <summary>两点转正矩形（宽高恒正）</summary>
        private static Rect Normalized(Point a, Point b)
        {
            double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
            return new Rect(x, y, Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        }
    }
}
