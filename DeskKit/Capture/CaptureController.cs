using System.Windows;

namespace MiniPix.Capture;

public static class CaptureController
{
    private static CaptureWindow? _active;

    /// <summary>Start an interactive screen-grab over the monitor under the cursor.</summary>
    public static void StartRegionCapture()
    {
        if (_active != null) { MiniPix.Core.Log.Write("region capture: already active"); return; }
        MiniPix.Core.Log.Write("region capture begin");
        var win = new CaptureWindow();
        _active = win;
        win.Closed += (_, _) => { _active = null; MiniPix.Core.Log.Write("capture window closed"); };
        // 2026-11-13 C-H2 修复：Run() 第一步抓全虚拟桌面（4K 多屏约 66-130MB），
        // 显示前抛异常（OOM/GDI 失败）时窗口未创建、Closed 不触发，_active 将永久占用
        // → 此后每次热键静默失效。失败必须主动清位。
        try
        {
            win.Run();
        }
        catch (Exception ex)
        {
            _active = null;
            MiniPix.Core.Log.Write("region capture Run() failed: " + ex.Message);
            try { win.Close(); } catch { /* 未显示过的窗口 Close 可能再抛，忽略 */ }
        }
        MiniPix.Core.Log.Write("region capture window shown");
    }
}
