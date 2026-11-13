using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MiniPix.Core;
using MiniPix.Native;

namespace MiniPix.Capture.LongCapture;

/// <summary>
/// 长截图会话编排（2026-11-11 第二次重构：自动滚动，ShareX ScrollingCapture 同款架构）。
///
/// 程序以固定小步长（滚轮 2 刻度，恒小于选区高度）驱动页面滚动，
/// 每步等 300ms 渲染稳定后抓屏拼接。帧间位移恒定可控 → 重叠匹配几乎必然成功——
/// 这是 ShareX 不产生重复/缺失的根本原因（用户自由滚动的被动观察无法保证这一点）。
///
/// 循环（对照 ShareX StartCapture）：抓屏 → 与上一帧一致则到底结束 → 拼接 → 滚动 → 等待。
/// 结束条件：① 连续两帧一致（滚到底）自动完成；② 用户点「完成」提前结束；
/// ③ 连续 3 帧匹配失败（滚太快/内容突变）以部分结果结束；④ 取消/Esc 丢弃。
/// </summary>
public sealed class LongCaptureSession
{
    private const int WheelDelta = 120;
    private const int StartDelayMs = 300;
    private const int MaxConsecutiveFailures = 3;

    // 滚动节奏从 config 读取（设置窗口"长截图"页可调；2026-11-11 反馈"慢一点"：默认延时 300→500ms）
    private readonly int _notches = Math.Clamp(ShotShelf.AppConfig.Current.LongScrollNotches, 1, 10);
    private readonly int _stepDelayMs = Math.Clamp(ShotShelf.AppConfig.Current.LongScrollDelayMs, 100, 3000);

    private readonly Rect _selPx;            // 选区（虚拟桌面物理坐标），固定不动
    private readonly MonitorInfo _mon;
    private readonly IntPtr _maskHwnd;       // 遮罩窗口句柄（激活目标窗口时排除自身）
    private readonly Action<string>? _onStatus;
    private readonly Action<BitmapSource> _onCompleted;
    private readonly Action _onCancelled;
    private readonly LongStitcher _stitcher = new();

    private CancellationTokenSource? _cts;
    private volatile bool _finishRequested;  // 用户点了「完成」
    private volatile bool _silentStop;       // 窗口已关闭：静默停止，不回调
    private volatile bool _manual;           // 手动模式：程序不滚，用户自己滚（挖洞天然穿透）
    private int _ended;                      // 0=运行中 1=已收尾（防回调重入）

    private Dispatcher? _ui;                 // UI 线程调度器（Start 时捕获）

    public LongCaptureSession(Rect selPx, MonitorInfo mon, IntPtr maskHwnd, bool startManual,
        Action<string>? onStatus,
        Action<BitmapSource> onCompleted, Action onCancelled)
    {
        _selPx = selPx;
        _mon = mon;
        _maskHwnd = maskHwnd;
        _manual = startManual;
        _onStatus = onStatus;
        _onCompleted = onCompleted;
        _onCancelled = onCancelled;
    }

    /// <summary>启动滚动循环（在 UI 线程调用）。模式由构造参数 startManual 决定。</summary>
    public void Start()
    {
        _ui = Dispatcher.CurrentDispatcher;
        _cts = new CancellationTokenSource();
        var cts = _cts;
        PostStatus(_manual ? "手动模式 · 自己滚动页面，点「完成」保存" : "自动滚动中…页面滚到底会停下等你确认");
        Log.Write($"longcap: session started manual={_manual} sel=({_selPx.X:0},{_selPx.Y:0} {_selPx.Width:0}x{_selPx.Height:0})");
        Task.Run(() => RunLoop(cts));
    }

    private void RunLoop(CancellationTokenSource cts)
    {
        var token = cts.Token;
        bool completed = false;
        try
        {
            int w = (int)Math.Round(_selPx.Width);
            int h = (int)Math.Round(_selPx.Height);

            // 光标移到选区中心 + 激活光标下的真实窗口（经挖洞命中页面）。
            // 关键：MouseWheelRouting=2（悬停滚动关闭）时滚轮只发给焦点窗口，
            // 页面不在前台则 SendInput 滚轮被遮罩吃掉、页面纹丝不动，
            // 帧间几乎相同 → 被误判"到底"秒结束（2026-11-11 实测根因；
            // ShareX 同款先 Activate 目标窗口再滚）。
            int cx = (int)(_selPx.X + _selPx.Width / 2), cy = (int)(_selPx.Y + _selPx.Height / 2);
            NativeMethods.SetCursorPos(cx, cy);
            try
            {
                var target = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = cx, Y = cy });
                if (target != IntPtr.Zero)
                {
                    var root = NativeMethods.GetAncestor(target, NativeMethods.GA_ROOT);
                    if (root != IntPtr.Zero && root != _maskHwnd)
                    {
                        bool ok = NativeMethods.SetForegroundWindow(root);
                        Log.Write($"longcap: activate target 0x{root:X} ok={ok}");
                    }
                }
            }
            catch (Exception ex) { Log.Write("longcap: activate failed: " + ex.Message); }
            Thread.Sleep(StartDelayMs);

            byte[]? prev = null;
            int failStreak = 0;
            bool bottomReached = false;

            while (!_finishRequested && !token.IsCancellationRequested && !_silentStop)
            {
                var frame = CaptureEngine.CaptureRect(_selPx);   // 选区已挖洞，拍到真实页面
                var px = new byte[w * h * 4];
                frame.CopyPixels(px, w * 4, 0);

                // 到底检测：本帧与上一帧一致 → 页面已滚到底（ShareX CompareLastTwoImages）。
                // 仅自动模式；且**不自动完成**——停在底部等用户确认（点完成保存 / 点手动继续），
                // 避免误判或没截完就关窗（2026-11-11 反馈"根本没机会点手动，也没载完"）。
                if (!_manual && !bottomReached && prev != null && LongStitcher.FramesEqual(prev, px))
                {
                    bottomReached = true;
                    Log.Write("longcap: bottom reached, auto scroll paused (waiting for finish/manual)");
                    PostStatus("已滚到页面底部 · 点「完成」保存，或点「手动」继续滚动");
                }

                int appended;
                if (!_stitcher.HasFrames)
                {
                    _stitcher.Init(w, h, px);
                    appended = h;
                    failStreak = 0;
                }
                else
                {
                    // 2026-11-13 C-H1：手动模式关掉吸底带检测（自由滚动下"帧相同"≠吸底）
                    _stitcher.EnableBottomBandDetect = !_manual;
                    appended = _stitcher.Append(px);
                    if (appended < 0)
                    {
                        appended = 0;
                        failStreak++;
                        Log.Write($"longcap: stitch unmatched (streak {failStreak}), frame dropped");
                        // 仅自动模式以"连续失配"提前收尾（且未到底）；手动模式快速滚动掉帧正常
                        if (!_manual && !bottomReached && failStreak >= MaxConsecutiveFailures)
                        {
                            Log.Write("longcap: finish partial (stitch failed repeatedly)");
                            break;
                        }
                    }
                    else failStreak = 0;
                }
                prev = px;

                if (_manual)
                {
                    PostStatus($"手动模式 · 自己滚动页面 · 已拼接 {_stitcher.TotalHeight} px");
                    Thread.Sleep(120);     // 高频抓帧跟随用户滚动
                }
                else if (bottomReached)
                {
                    Thread.Sleep(200);     // 已到底：不发滚轮，继续监听（用户点手动/页面变化都能跟上）
                }
                else
                {
                    PostStatus($"自动滚动中 · 已拼接 {_stitcher.TotalHeight} px");
                    // 固定小步长滚动（ShareX: SendMouseWheel(-120 * amount)，步长/延时可在设置里调）
                    NativeMethods.SendMouseWheel(-WheelDelta * _notches);
                    Thread.Sleep(_stepDelayMs);
                }
            }

            completed = _stitcher.HasFrames;
            if (completed)
            {
                Log.Write($"longcap: loop end (bottom={bottomReached} finishReq={_finishRequested}) height={_stitcher.TotalHeight}");
            }
        }
        catch (OperationCanceledException)
        {
            // 用户取消（Cancel()）：按取消收尾
        }
        catch (Exception ex)
        {
            Log.Write("longcap: loop failed: " + ex.Message);
        }

        if (_silentStop) return;                       // 窗口已关：静默退出，不回调
        if (Interlocked.CompareExchange(ref _ended, 1, 0) != 0) return;

        _ui!.BeginInvoke(() =>
        {
            if (completed)
            {
                try
                {
                    var img = _stitcher.BuildImage(_mon.DpiX, _mon.DpiY);
                    Log.Write($"longcap: finished {_stitcher.TotalHeight} px");
                    _onCompleted(img);
                    return;
                }
                catch (Exception ex)
                {
                    Log.Write("longcap: BuildImage failed: " + ex.Message);
                }
            }
            _onCancelled();                            // 无结果/异常：按取消收尾
        });
    }

    /// <summary>状态条文案（回 UI 线程）。</summary>
    private void PostStatus(string text)
    {
        var ui = _ui;
        if (ui == null) _onStatus?.Invoke(text);
        else ui.BeginInvoke(() => _onStatus?.Invoke(text));
    }

    /// <summary>切换自动/手动滚动（可在拼接过程中随时切换）。
    /// 手动模式：程序不发滚轮、不做到底判定（用户暂停≠到底），120ms 高频抓帧跟随。</summary>
    public void SetManual(bool manual)
    {
        _manual = manual;
        Log.Write($"longcap: mode -> {(manual ? "manual" : "auto")}");
    }

    /// <summary>用户点「完成」：停止滚动，以当前已拼内容收尾（循环在下一步抓屏前退出）。</summary>
    public void Finish()
    {
        if (_cts == null) return;
        _finishRequested = true;
    }

    /// <summary>窗口关闭等场景的静默清理：停循环，不触发任何回调。</summary>
    public void Stop()
    {
        _silentStop = true;
        _cts?.Cancel();
    }

    /// <summary>取消：丢弃全部结果，回编辑模式。</summary>
    public void Cancel()
    {
        if (Interlocked.CompareExchange(ref _ended, 1, 0) != 0) return;
        _silentStop = false;
        _cts?.Cancel();
        Log.Write("longcap: cancelled");
        if (_ui != null) _ui.BeginInvoke(_onCancelled);
        else _onCancelled();
    }
}
