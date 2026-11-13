using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Clipboard = System.Windows.Clipboard; // WPF 版（避开 WinForms 同名类）

namespace ShotShelf
{
    /// <summary>
    /// 剪贴板监听器（S2，2026-10-26 十二次更新支持文本）：
    /// 用 Win32 AddClipboardFormatListener 注册窗口消息 WM_CLIPBOARDUPDATE，
    /// 系统剪贴板有新内容时立即收到通知（比轮询干净）。
    ///
    /// 读取优先级：图片 &gt; 文本。
    ///   截图软件复制 → 位图（DIB/DIBV5/PNG 均兼容）；
    ///   复制文字（网页/Word/记事本）→ 纯文本，此时通常没有位图格式。
    ///
    /// 用法：new ClipboardWatcher(window).ImageReceived / .TextReceived += ...
    /// </summary>
    public class ClipboardWatcher : IDisposable
    {
        // ---- Win32 API 声明 ----
        private const int WM_CLIPBOARDUPDATE = 0x031D;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        private readonly HwndSource _hwndSource;
        private readonly IntPtr _hwnd;             // 2026-11-13 S-M2：构造时缓存句柄（Dispose 时窗口可能已销毁，RootVisual 取不到）
        private bool _disposed;
        private int _reading;                      // 2026-11-13 S-M1：0=空闲 1=后台读取中
        private int _pending;                      // 读取期间又来了新通知（读完补读一轮）

        /// <summary>剪贴板里出现新位图时触发（后台无法直接触发，均在 UI 线程消息循环里）</summary>
        public event Action<BitmapSource>? ImageReceived;

        /// <summary>剪贴板里出现新文本时触发（2026-10-26 新增）</summary>
        public event Action<string>? TextReceived;

        /// <summary>
        /// 单条文本最大接收长度（2026-10-26）：
        /// 超长文本（整本书、大段日志）截断后入库，避免缓存文件过大拖慢启动恢复。
        /// 由 MainWindow 从 config.MaxTextLength 注入。
        /// </summary>
        public int MaxTextLength { get; set; } = 200_000;

        public ClipboardWatcher(Window window)
        {
            // 拿到 WPF 窗口背后的 Win32 句柄，把自己挂进消息处理链
            var helper = new WindowInteropHelper(window);
            helper.EnsureHandle();
            _hwndSource = HwndSource.FromHwnd(helper.Handle)
                ?? throw new InvalidOperationException("无法获取窗口句柄");

            _hwndSource.AddHook(WndProc);
            _hwnd = helper.Handle;
            AddClipboardFormatListener(_hwnd);
        }

        // ---- 消息处理 ----
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE)
            {
                handled = true;
                QueueRead();
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 2026-11-13 S-M1：剪贴板读取移到后台 STA 线程。OLE 剪贴板（Clipboard.ContainsImage/
        /// GetImage/GetText）被云剪贴板、输入法面板占用时内部自带 10×100ms 重试，
        /// 在 UI 线程调用一次就是"卡死数秒"；移到后台线程后 UI 永不卡，兼容性不变。
        /// 串行化：读取期间来了新通知记 _pending，读完补读一轮（拿最新内容），不乱序不丢。
        /// </summary>
        private void QueueRead()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
            {
                System.Threading.Interlocked.Exchange(ref _pending, 1);
                return;
            }
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    TryReadCore();
                    // 补读一轮：工作期间有新通知，剪贴板当前内容为准（再读一次即可，无需排队历史帧）
                    if (System.Threading.Interlocked.Exchange(ref _pending, 0) == 1)
                        TryReadCore();
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _reading, 0);
                    // 竞窗兜底：置 0 前瞬间又来了通知（pending 已置位但补读已完成）→ 再走一遍队列
                    if (System.Threading.Interlocked.Exchange(ref _pending, 0) == 1)
                        QueueRead();
                }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>
        /// 从剪贴板读取内容（图片优先，其次文本）。后台 STA 线程执行。
        /// 注意：剪贴板里的内容也可能是"本程序自己复制回去的"，
        /// 由 MainWindow 的抑制计数器 + 指纹比对判断是否要收（避免回环）。
        /// </summary>
        private void TryReadCore()
        {
            BitmapSource? bmp = null;
            string? text = null;
            // 带一轮降级重试（OLE 内部重试 + 外层 120ms 兜底，共约 1.2s 窗口）
            for (int attempt = 0; attempt < 2 && bmp == null && text == null; attempt++)
            {
                try
                {
                    // ---- 1. 图片优先 ----
                    if (Clipboard.ContainsImage())
                    {
                        var img = Clipboard.GetImage();
                        if (img != null)
                        {
                            img.Freeze(); // 后台线程读取 → 冻结后才能安全跨线程回 UI
                            bmp = img;
                        }
                    }

                    // ---- 2. 其次文本 ----
                    if (bmp == null && Clipboard.ContainsText())
                    {
                        var t = Clipboard.GetText();
                        if (string.IsNullOrWhiteSpace(t)) return; // 纯空白不收
                        if (MaxTextLength > 0 && t.Length > MaxTextLength)
                            t = t.Substring(0, MaxTextLength);
                        text = t;
                    }
                }
                catch (Exception ex)
                {
                    if (attempt == 0) { System.Threading.Thread.Sleep(120); continue; }
                    // 剪贴板被其它进程持续锁住等场景，静默放弃（下次 WM_CLIPBOARDUPDATE 会再来）
                    System.Diagnostics.Debug.WriteLine($"读取剪贴板内容失败: {ex.Message}");
                    return;
                }
            }

            if (bmp == null && text == null) return;
            // 回 UI 线程触发事件（消费方操作 UI 集合/抑制标志都在 UI 线程）
            _hwndSource.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (bmp != null) ImageReceived?.Invoke(bmp);
                else if (text != null) TextReceived?.Invoke(text);
            }));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                // 2026-11-13 S-M2：用构造时缓存的句柄（旧实现从 RootVisual 重建
                // WindowInteropHelper，窗口关闭后 RootVisual 为 null 抛异常被吞，
                // 监听器实际从未被移除）
                RemoveClipboardFormatListener(_hwnd);
            }
            catch { /* 窗口已销毁等情况忽略 */ }
            _hwndSource.RemoveHook(WndProc);
        }
    }
}
