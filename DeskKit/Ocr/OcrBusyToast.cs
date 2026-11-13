// OcrBusyToast.cs — OCR 识别中的屏幕提示条（2026-11-05）
// 背景：原来用 NotifyIcon.ShowBalloonTip 发"OCR 识别中…"，在 Win11 上会转成系统通知，
// 若应用通知被关/勿扰模式则完全不显示，用户点 OCR 后 1~3 秒无任何反馈，像"没反应"。
// 改为应用内自带 toast：屏幕下方居中一个小胶囊条，Topmost、不抢焦点，识别完成/失败自动消失。
using System;
using System.Windows;
using System.Windows.Media;

namespace DeskKit.Ocr
{
    public sealed class OcrBusyToast : Window
    {
        private readonly Window? _owner;    // 发起识别的窗口：提示条居中显示在它正中（2026-11-05 用户要求）

        private OcrBusyToast(string msg, Window? owner)
        {
            _owner = owner;
            AllowsTransparency = true;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // 2026-11-05 修复"提示占半个屏幕"：无边框窗口不设 SizeToContent 时走系统默认窗口尺寸，
            // 内容被拉伸成大块。必须显式按内容收缩，并限制最大宽度
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowInTaskbar = false;
            ShowActivated = false;              // 不抢焦点：不打断截图/贴图当前操作
            Topmost = true;
            Background = Brushes.Transparent;
            IsHitTestVisible = false;           // 纯提示，鼠标穿透

            Content = new System.Windows.Controls.Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x2B, 0x2F, 0x36)),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(18, 8, 18, 8),
                MaxWidth = 360,
                Child = new System.Windows.Controls.TextBlock
                {
                    Text = msg,
                    Foreground = Brushes.White,
                    FontSize = 13,
                    FontFamily = new FontFamily("Microsoft YaHei UI"),
                    TextWrapping = TextWrapping.Wrap,
                },
            };

            // 屏幕工作区下方居中（避开任务栏）
            Loaded += (_, _) => Position();
        }

        private void Position()
        {
            // 2026-11-05 用户要求：优先居中在发起识别的窗口正中（截图窗/贴图/预览）；
            // owner 拿不到尺寸（如已关闭/未布局）时退回屏幕工作区居中
            if (_owner != null && _owner.IsVisible &&
                _owner.ActualWidth > 0 && _owner.ActualHeight > 0)
            {
                // Window.Left/Top 是屏幕坐标（含多屏），直接在其矩形内居中
                Left = _owner.Left + (_owner.ActualWidth - ActualWidth) / 2;
                Top = _owner.Top + (_owner.ActualHeight - ActualHeight) / 2;
                return;
            }

            var wa = SystemParameters.WorkArea;
            Left = (wa.Width - ActualWidth) / 2 + wa.Left;
            Top = wa.Height / 2 - ActualHeight / 2 + wa.Top;
        }

        /// <summary>显示忙碌提示（UI 线程调用）。owner=发起识别的窗口（可空），提示居中其上。</summary>
        public static OcrBusyToast Show(string msg, Window? owner = null)
        {
            var t = new OcrBusyToast(msg, owner);
            t.Show();
            return t;
        }

        /// <summary>结束提示（识别完成/失败时调用，幂等）</summary>
        public void Dismiss()
        {
            try { Close(); } catch { /* 已关闭时忽略 */ }
        }
    }
}
