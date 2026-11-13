// OcrResultWindow.cs — OCR 识别结果弹窗（2026-11-05 OCR 简化版）
// v1 框选方案用户不满意（点 OCR 后全屏变灰像截图消失），v2 改为：
// 点 OCR 按钮 → 直接识别 → 弹出本窗展示文字。文字可选中（局部复制 Ctrl+C），
// 底部「复制全部」一键全复制。浅色风格与工具条/信息条统一。
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeskKit.Ocr
{
    public sealed class OcrResultWindow : Window
    {
        /// <summary>可调项：结果窗默认尺寸（可拉伸，最小尺寸见 MinWidth/MinHeight）</summary>
        private const double DefaultW = 460, DefaultH = 400;

        public OcrResultWindow(string text)
        {
            Title = "OCR 识别结果";
            Width = DefaultW; Height = DefaultH;
            MinWidth = 300; MinHeight = 200;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;                 // 不被贴图/截图窗口压住
            ShowInTaskbar = false;
            Icon = ShotShelf.IconFactory.GetWpfIcon();   // 2026-11-12 统一程序图标
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            FontFamily = new FontFamily("Microsoft YaHei UI");

            var txt = new TextBox
            {
                Text = text,
                IsReadOnly = false,                      // 2026-11-05 用户要求：可编辑（顺手改错字/删多余行再复制）
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontSize = 14,
                Padding = new Thickness(4),
                IsTabStop = false,
            };

            var btnCopy = new Button
            {
                Content = "复制全部",
                Width = 88, Height = 28,
                FontSize = 13,
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            btnCopy.Click += (_, _) =>
            {
                // 2026-11-05 二次修复：OLE 剪贴板（Clipboard.SetDataObject）内部有 10×100ms 重试 + flush
                // 后置步骤，被剪贴板管理器占用时又卡又误报失败（数据其实已放上）。
                // 改为直接走 Win32 剪贴板 API：同步写入、无 flush、重试只锁 OpenClipboard（毫秒级），不卡不死。
                if (TryCopyAll(txt.Text)) btnCopy.Content = "已复制 ✓";
                else
                {
                    MiniPix.Core.Log.Write("ocr result copy fail: win32 set 失败");
                    btnCopy.Content = "复制失败";
                }
            };

            var btnClose = new Button
            {
                Content = "关闭",
                Width = 64, Height = 28,
                FontSize = 13,
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x52)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDC, 0xE1)),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            btnClose.Click += (_, _) => Close();

            var charInfo = new TextBlock
            {
                Text = $"{text.Length} 字 · 可编辑，改完直接复制",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98)),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var bottom = new Grid { Margin = new Thickness(10, 8, 10, 10) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            Grid.SetColumn(btnCopy, 0);
            Grid.SetColumn(charInfo, 1);
            Grid.SetColumn(btnClose, 3);
            charInfo.Margin = new Thickness(10, 0, 10, 0);
            bottom.Children.Add(btnCopy);
            bottom.Children.Add(charInfo);
            bottom.Children.Add(btnClose);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(txt, 0);
            Grid.SetRow(bottom, 1);
            root.Children.Add(txt);
            root.Children.Add(bottom);

            Content = root;

            // 打开即全选，方便直接 Ctrl+C；焦点给文本框让键盘滚动/复制可用
            Loaded += (_, _) => { txt.Focus(); txt.SelectAll(); };
        }

        /// <summary>
        /// 剪贴板写入（Win32 直写版，2026-11-05 二次修复）：
        /// WPF 的 OLE 剪贴板（SetDataObject/SetText）= OleSetClipboard + OleFlushClipboard，
        /// flush 是后置步骤且内部自带 10×100ms 重试，被云剪贴板/剪贴板管理器占用时：
        /// 卡 UI 数秒 + 抛异常，但数据其实已放上（所以"显示失败，其实成功"）。
        /// 改用 Win32 API 同步直写：OpenClipboard 重试（每次仅 30ms）→ Empty → SetClipboardData(CF_UNICODETEXT)。
        /// SetClipboardData 成功即数据已由系统接管，无延迟渲染、无 flush，状态就是最终状态。
        /// 全失败时退回 WPF Clipboard.SetDataObject（不带 flush）做最后兜底。
        /// </summary>
        private static bool TryCopyAll(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            // Win32 路径：最多重试 10 次 OpenClipboard（占用通常是毫秒级，实际 1~2 次就成功）
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (!OpenClipboard(IntPtr.Zero)) { System.Threading.Thread.Sleep(30); continue; }
                try
                {
                    if (!EmptyClipboard()) return false;

                    // 分配全局内存：文本 + 终结符，每字符 2 字节（UTF-16）
                    IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)((text.Length + 1) * 2));
                    if (hMem == IntPtr.Zero) return false;

                    IntPtr ptr = GlobalLock(hMem);
                    if (ptr == IntPtr.Zero) { GlobalFree(hMem); return false; }
                    try
                    {
                        System.Runtime.InteropServices.Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                        System.Runtime.InteropServices.Marshal.WriteInt16(ptr, text.Length * 2, 0); // 终结符
                    }
                    finally { GlobalUnlock(hMem); }

                    // 成功后内存归系统所有（不可再 GlobalFree）；失败则自己释放
                    if (SetClipboardData(CF_UNICODETEXT, hMem) != IntPtr.Zero) return true;
                    GlobalFree(hMem);
                    return false;
                }
                finally { CloseClipboard(); }
            }

            // Win32 全失败（理论上几乎不会）：退回 WPF OLE，不带 flush（数据对象由本进程持有，粘贴可用）
            try { Clipboard.SetDataObject(text); return true; }
            catch (Exception ex) { MiniPix.Core.Log.Write("ocr copy fallback fail: " + ex.Message); return false; }
        }

        // ---- Win32 剪贴板 P/Invoke（CF_UNICODETEXT 直写，2026-11-05）----
        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool CloseClipboard();

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool EmptyClipboard();

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);
    }
}
