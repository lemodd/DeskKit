using System.Runtime.InteropServices;

namespace MiniPix.Native;

/// <summary>
/// Per-window IME control. The capture overlay closes the IME while it is active so a
/// Chinese/Japanese/Korean input method never swallows the shortcut keys (C, V, S, P...),
/// and re-opens it only while a text annotation is being typed.
/// </summary>
internal static class ImeHelper
{
    [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll")] static extern bool ImmSetOpenStatus(IntPtr hIMC, bool fOpen);
    [DllImport("imm32.dll")] static extern bool ImmGetOpenStatus(IntPtr hIMC);
    [DllImport("imm32.dll")] static extern void ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
    [DllImport("imm32.dll")] static extern bool ImmSetConversionStatus(IntPtr hIMC, int conversion, int sentence);
    [DllImport("imm32.dll")] static extern bool ImmGetConversionStatus(IntPtr hIMC, out int conversion, out int sentence);

    const int ALPHANUMERIC = 0x0000;
    const int NATIVE = 0x0001;
    const int SENTENCY_NONE = 0x0004;

    // 2026-11-13 C-M7：Disable 前记录的原始 IME 状态（开启/转换模式），
    // Enable 时恢复——否则用户原本的中文输入模式会被永久置成字母模式
    private static bool _saved;
    private static bool _wasOpen;
    private static int _conv, _sentence;

    public static void Disable(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        try
        {
            IntPtr ctx = ImmGetContext(hWnd);
            if (ctx == IntPtr.Zero) return;
            _wasOpen = ImmGetOpenStatus(ctx);
            ImmGetConversionStatus(ctx, out _conv, out _sentence);
            _saved = true;
            ImmSetOpenStatus(ctx, false);
            ImmSetConversionStatus(ctx, ALPHANUMERIC, SENTENCY_NONE);
            ImmReleaseContext(hWnd, ctx);
        }
        catch { /* imm is not present on some setups */ }
    }

    /// <summary>Restore whatever input mode the user had before we forced alphanumeric
    /// （2026-11-13 C-M7：恢复原转换模式，而不是只重开 IME 留在字母模式）.</summary>
    public static void Enable(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        try
        {
            IntPtr ctx = ImmGetContext(hWnd);
            if (ctx == IntPtr.Zero) return;
            if (_saved)
            {
                ImmSetConversionStatus(ctx, _conv, _sentence);
                ImmSetOpenStatus(ctx, _wasOpen);
                _saved = false;
            }
            else
            {
                ImmSetOpenStatus(ctx, true);
            }
            ImmReleaseContext(hWnd, ctx);
        }
        catch { }
    }
}
