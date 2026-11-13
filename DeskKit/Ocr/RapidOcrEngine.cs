// RapidOcrEngine.cs — RapidOCR(PP-OCRv5 ONNX) 引擎实现（2026-11-05 OCR v3.1）
// 背景：Windows.Media.Ocr 中文识别率低且字间插空格，用户反馈"达不到可用效果"。
// 引擎层当初就按 IOcrEngine 预留，本类即插即用替换，OcrService/三处 UI 不用动。
//
// 模型来源：NuGet RapidOcrNet 自带 det/cls（自动拷到 models\v5\），
// 中文 rec + 词典来自 RapidAI/RapidOCR v3.10.0（本项目 OcrModels\ → models\v5\）。
// 无兜底：模型缺失/推理失败直接抛异常 → OcrService 统一气泡报错（应用户要求删 Windows.Media.Ocr 省 24MB）。
//
// 线程模型（v3.1 修复 v3 的 VerifyAccess 崩溃）：
//   BitmapSource 有线程亲和性（未冻结时跨线程访问直接炸，v3 的 BitmapFrame.Create 就死在这）。
//   v3.1 改为：调用线程（UI）先 CopyPixels 拷出字节数组 → 后台线程只碰 byte[] 和 SkiaSharp。
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace DeskKit.Ocr
{
    public sealed class RapidOcrEngine : IOcrEngine
    {
        public string Name => "RapidOCR-PPv5";

        private static readonly object _initLock = new();
        private static RapidOcrNet.RapidOcr? _ocr;          // 懒加载，进程内复用（模型加载约 1~2 秒）

        /// <summary>输出目录 models\v5\ 下的模型路径（NuGet 自带 det/cls + 本项目带 rec/dict）</summary>
        private static string ModelPath(string file) =>
            Path.Combine(AppContext.BaseDirectory, "models", "v5", file);

        private static readonly string[] RequiredModels =
        {
            "ch_PP-OCRv5_mobile_det.onnx",
            "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
            "ch_PP-OCRv5_rec_mobile.onnx",
            "ppocrv5_dict.txt",
        };

        /// <summary>懒初始化：det/cls 用 NuGet 自带，rec/dict 用中文模型。模型缺失抛带指引的异常</summary>
        private static RapidOcrNet.RapidOcr GetEngine()
        {
            lock (_initLock)
            {
                if (_ocr != null) return _ocr;

                // 每次都重新检查文件（模型补上后不用重启就能恢复）
                var missing = Array.FindAll(RequiredModels, f => !File.Exists(ModelPath(f)));
                if (missing.Length > 0)
                    throw new FileNotFoundException(
                        "OCR 模型缺失: " + string.Join(", ", missing) +
                        "（应在 " + Path.Combine(AppContext.BaseDirectory, "models", "v5") + "）");

                var ocr = new RapidOcrNet.RapidOcr();
                ocr.InitModels(
                    detPath: ModelPath("ch_PP-OCRv5_mobile_det.onnx"),
                    clsPath: ModelPath("ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
                    recPath: ModelPath("ch_PP-OCRv5_rec_mobile.onnx"),
                    keysPath: ModelPath("ppocrv5_dict.txt"));
                _ocr = ocr;
                MiniPix.Core.Log.Write("ocr engine init ok: RapidOCR PP-OCRv5 ch");
                return _ocr;
            }
        }

        public async Task<OcrResult> RecognizeAsync(BitmapSource image, CancellationToken ct = default)
        {
            // ---- 第一步（调用线程，通常=UI）：把像素拷成纯字节数组 ----
            // BitmapSource 可能未冻结（跨线程访问会炸），CopyPixels 必须在它的属主线程完成。
            // 统一转 Bgra32：与 SKColorType.Bgra8888 字节布局一致，后台免转换。
            var conv = image.Format == PixelFormats.Bgra32
                ? image
                : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            conv.CopyPixels(px, stride, 0);
            MiniPix.Core.Log.Write($"ocr input: {w}x{h} srcFmt={image.Format}");

            // ---- 第二步（后台线程）：组 SKBitmap → ONNX 推理（CPU 密集，别占 UI） ----
            var text = await Task.Run(() =>
            {
                // 屏幕拷贝/剪贴板图的 alpha 通道可能是 0（GDI BitBlt 不写 alpha），
                // 强制不透明，避免整图被判成全透明黑导致检测为空（v3 曾疑似踩过）
                for (int i = 3; i < px.Length; i += 4) px[i] = 0xFF;

                var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
                using var bmp = new SKBitmap(info);
                Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = GetEngine().Detect(bmp, RapidOcrNet.RapidOcrOptions.Default);
                sw.Stop();
                MiniPix.Core.Log.Write($"ocr detect: blocks={r.TextBlocks.Length} time={sw.ElapsedMilliseconds}ms");
                return r.StrRes ?? "";
            }, ct);

            return new OcrResult { Text = text };
        }
    }
}
