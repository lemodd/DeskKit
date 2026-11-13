// OcrService.cs — OCR 门面：可换引擎接口 + 统一的"识别→剪贴板→气泡"流程（2026-11-05 OCR 功能）
// 三处工具条（截图/预览/贴图）共用本类；引擎默认 Windows.Media.Ocr（系统自带零依赖），
// 将来接 RapidOCR 时实现 IOcrEngine 并替换 OcrService.Engine 即可，三处 UI 不用动。
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;               // Clipboard（WPF 版，STA 要求）
using System.Windows.Media;         // Color / ColorFromRgb（OcrDefaults 用）
using System.Windows.Media.Imaging; // BitmapSource

namespace DeskKit.Ocr
{
    /// <summary>OCR 识别结果（引擎无关，方便将来换 RapidOCR 时复用）</summary>
    public sealed class OcrResult
    {
        /// <summary>按行拼接的全文（\n 分行，首尾已 Trim）</summary>
        public string Text { get; init; } = "";

        /// <summary>逐行原文（未来做"按行复制/逐行对照"扩展用）</summary>
        public System.Collections.Generic.IReadOnlyList<string> Lines { get; init; } =
            Array.Empty<string>();

        /// <summary>是否识别到有效文字</summary>
        public bool HasText => Text.Length > 0;
    }

    /// <summary>
    /// 可替换的 OCR 引擎接口。实现类只管"图进字出"，剪贴板/气泡/日志统一在 OcrService 做。
    /// </summary>
    public interface IOcrEngine
    {
        /// <summary>引擎名（日志/气泡里区分来源）</summary>
        string Name { get; }

        /// <summary>识别位图中的文字。image 为任意格式的 BitmapSource，内部自行转换</summary>
        Task<OcrResult> RecognizeAsync(System.Windows.Media.Imaging.BitmapSource image,
            CancellationToken ct = default);
    }

    /// <summary>
    /// OCR 可调参数集中处（风格沿用 AnnDefaults / CaptureWindow 的 DimColor 做法）。
    /// </summary>
    public static class OcrDefaults
    {
        /// <summary>框选暗罩色（与截图选区 DimColor 一致：半透明灰 A0）</summary>
        public static readonly Color DimColor = Color.FromArgb(0xA0, 0x60, 0x60, 0x60);

        /// <summary>选框边色（与截图选区蓝框一致）</summary>
        public static readonly Color SelBorderColor = Color.FromRgb(0x1E, 0x88, 0xE5);

        /// <summary>选框边宽</summary>
        public const double SelBorderThickness = 1.5;

        /// <summary>最小有效框（图像像素）：松手时框小于此值视为误点，直接取消不识别</summary>
        public const int MinRegionPx = 8;

        /// <summary>进入识别时是否弹「OCR 识别中…」气泡（三处统一，最简单的忙碌反馈）</summary>
        public const bool ShowBusyBubble = true;
    }

    /// <summary>
    /// OCR 门面：三处 UI 调 RecognizeAndNotifyAsync 即可，识别/剪贴板/气泡/日志/防重入全在这。
    /// 全程 try/catch 绝不抛出——OCR 是锦上添花功能，任何失败都只影响一次识别，不影响窗口。
    /// </summary>
    public static class OcrService
    {
        /// <summary>当前引擎（2026-11-05 v3：主引擎换 RapidOCR PP-OCRv5，中文识别率大幅优于 Windows.Media.Ocr；
        /// RapidOcrEngine 内部已带 Windows OCR 兜底——模型缺失/推理失败自动降级。接其他引擎时替换此属性即可）</summary>
        public static IOcrEngine Engine { get; set; } = new RapidOcrEngine();

        /// <summary>识别忙标志：防止重复点击并发识别（各窗按钮可读它做双保险禁用）</summary>
        public static bool IsBusy { get; private set; }

        /// <summary>
        /// 【2026-11-05 v2 简化版，当前使用】识别 → 成功弹结果窗（可一键复制/选字局部复制）。
        /// 与 v1 的区别：不再自动写剪贴板，改由结果窗「复制全部」按钮触发（用户要求）。
        /// owner：发起识别的窗口（2026-11-05 加），"识别中"提示条居中显示在其正中；空则屏幕居中。
        /// 必须在 UI 线程调用；空结果/异常/无语言包一律气泡提示，不抛出。
        /// </summary>
        public static async void RecognizeToWindowAsync(BitmapSource crop, Window? owner = null)
        {
            if (IsBusy)
            {
                global::DeskKit.App.Notifier("OCR 正在识别，请稍候…");
                return;
            }
            IsBusy = true;
            // 2026-11-05 反馈修复：NotifyIcon 气泡在 Win11 可能被系统通知设置吞掉，
            // 改为应用内 toast（居中于发起识别的窗口正中）+ 等待光标，识别中必有可见反馈
            var toast = OcrBusyToast.Show("OCR 识别中…", owner);
            var prevCursor = System.Windows.Input.Mouse.OverrideCursor;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                var result = await Engine.RecognizeAsync(crop);
                result = new OcrResult
                {
                    Text = CleanCjkSpaces(result.Text),   // 2026-11-05 v3：清掉 CJK 字间空格
                    Lines = result.Lines,
                };
                MiniPix.Core.Log.Write("ocr " + Engine.Name + " done, " + result.Text.Length + " chars");

                if (!result.HasText)
                {
                    toast.Dismiss(); toast = null;
                    global::DeskKit.App.Notifier("未识别到文字");
                    return;
                }
                toast.Dismiss(); toast = null;
                new OcrResultWindow(result.Text).Show();
            }
            catch (Exception ex)
            {
                MiniPix.Core.Log.Write("ocr failed: " + ex);
                global::DeskKit.App.Notifier("OCR 失败: " + ex.Message);
            }
            finally
            {
                toast?.Dismiss();
                System.Windows.Input.Mouse.OverrideCursor = prevCursor;
                IsBusy = false;
            }
        }

        /// <summary>
        /// 2026-11-05 v3：清掉两个 CJK 字符之间的空格（Windows OCR 和部分引擎会在
        /// "很 多 品 牌" 之间插空格；中文里这些空格全无意义）。
        /// 只删"两侧都是 CJK（汉字/中文标点/全角字符）"的空白，中英混排的
        /// "L1 到 L5"、"16 GB" 等正常空格不受影响。
        /// </summary>
        public static string CleanCjkSpaces(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            static bool IsCjk(char c) =>
                (c >= 0x3000 && c <= 0x303F) ||   // CJK 标点（，。、《》…）
                (c >= 0x3400 && c <= 0x4DBF) ||   // CJK 扩展 A
                (c >= 0x4E00 && c <= 0x9FFF) ||   // CJK 基本区
                (c >= 0xF900 && c <= 0xFAFF) ||   // CJK 兼容
                (c >= 0xFF00 && c <= 0xFFEF);     // 全角字符（：？！ＡＢ１２）

            var sb = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c) && c != '\n' && sb.Length > 0)
                {
                    // 越过连续空白找下一个非空白字符：两侧都是 CJK 才删（连续空格一次全删干净）
                    int j = i + 1;
                    while (j < text.Length && text[j] != '\n' && char.IsWhiteSpace(text[j])) j++;
                    if (j < text.Length && IsCjk(sb[sb.Length - 1]) && IsCjk(text[j]))
                    {
                        i = j - 1;
                        continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        // 2026-11-13：v1 旧入口 RecognizeAndNotifyAsync（识别→复制剪贴板→气泡，已无调用方）已删除；
        // 三处 UI 统一走 RecognizeToWindowAsync（提示条居中窗口版）。
    }
}
