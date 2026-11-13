using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DeskKit.Transfer
{
    /// <summary>
    /// multipart/form-data 解析器（2026-11-10）。
    /// 全程流式：文件 part 直接 64KB 缓冲落盘，绝不全读内存（防大文件爆 RAM）。
    /// 文件名解析：filename*（RFC5987）优先，裸 filename 按 UTF-8 兜底（Chrome/Edge 中文实际发 UTF-8）。
    /// </summary>
    public static class MultipartReader
    {
        public sealed class UploadFile
        {
            public required string FileName;
            public required string SavedPath;
            public required long Size;
        }

        public sealed class ParseResult
        {
            public Dictionary<string, string> Fields = new();
            public List<UploadFile> Files = new();
        }

        /// <summary>
        /// 解析 body。boundary 来自 Content-Type（不含首尾引号）。
        /// 每个文件 part 临时写到 tempDir 的 .part 文件，解析完统一改名——
        /// 调用方失败时只留下 .part 垃圾，下次启动清扫，不影响已有文件。
        /// </summary>
        public static ParseResult Parse(HttpByteStream body, long contentLength,
            string boundary, string tempDir)
        {
            var result = new ParseResult();
            var pat = Encoding.UTF8.GetBytes("\r\n--" + boundary);

            // 2026-11-13 T-M3/M4：全局总量守卫——body 消费总量超过声明 Content-Length
            // （+64KB 边界开销余量）立即抛异常断开，防止"声明 100 字节、实际发 10GB"
            // 的恶意客户端把磁盘/内存吃满。字段值另有 256KB 流式上限（超限截断丢弃）。
            var totals = new Totals
            {
                Cap = contentLength > 0 ? contentLength + 64 * 1024 : long.MaxValue,
            };

            // 首边界定位（两种情况）：
            //   ① body 直接以 "--boundary\r\n" 开头（curl/浏览器都这样）：
            //      ReadLine 已把边界行连同后面的 CRLF 一起消费，队列里直接就是 part 头部；
            //   ② 有 preamble：丢弃到第一个 "\r\n--boundary"，队列里是边界后的字节
            //      （"\r\n下一part头" 或 "--" 结束标记）→ 循环开头要再读分隔符。
            var first = body.ReadLine();
            if (first == null) throw new IOException("multipart: empty body");
            bool needDelimRead; // true = 队列以"边界后的分隔符"开头，循环开头要读 CRLF/--
            if (first.StartsWith("--" + boundary, StringComparison.Ordinal))
            {
                needDelimRead = false; // 边界行 + CRLF 已被 ReadLine 消费
            }
            else
            {
                if (!body.CopyUntil(pat, Stream.Null)) throw new IOException("multipart: no boundary found");
                needDelimRead = true;
            }

            while (true)
            {
                if (needDelimRead)
                {
                    // 边界后："\r\n"=下一 part，"--"=结束，脏数据尽力跳一行
                    int c1 = body.ReadByte(), c2 = body.ReadByte();
                    if (c1 == '-' && c2 == '-') break;                  // --boundary-- 结束
                    if (c1 == '\r' && c2 == '\n') { /* 正常下一 part */ }
                    else if (c1 < 0) break;                             // 流意外结束
                    else SkipLine(body);
                }
                // CopyUntil 匹配后队列同样是"边界后的分隔符"开头
                needDelimRead = true;

                // part 头部：到空行
                string? cd = null, ct = null;
                while (true)
                {
                    var h = body.ReadLine();
                    if (h == null || h.Length == 0) break;
                    if (h.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase)) cd = h;
                    else if (h.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase)) ct = h;
                }
                if (cd == null) throw new IOException("multipart: part missing Content-Disposition");

                var (fieldName, fileName) = ParseDisposition(cd);
                if (string.IsNullOrEmpty(fileName))
                {
                    // 普通字段：值 = 到下一边界为止（字段值流式限 256KB，超出直接截断丢弃）
                    using var valSink = new CountingStream(new MemoryStream(), totals, streamCap: 256 * 1024);
                    if (!body.CopyUntil(pat, valSink)) throw new IOException("multipart: EOF in field");
                    var val = Encoding.UTF8.GetString(((MemoryStream)valSink.Inner).ToArray());
                    if (!valSink.Truncated && val.Length <= 256 * 1024)
                        result.Fields[fieldName] = val.TrimEnd('\r', '\n');
                    // 超限截断的字段丢弃（与旧行为一致，防御恶意大字段）
                }
                else
                {
                    // 文件 part：流式落盘（守卫流超全局上限即抛，及时掐断）
                    Directory.CreateDirectory(tempDir);
                    var tmpPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".part");
                    long size = 0;
                    using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var guard = new CountingStream(fs, totals, streamCap: 0);
                        if (!body.CopyUntil(pat, guard)) throw new IOException("multipart: EOF in file part");
                        size = fs.Length;
                    }
                    var finalPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".staged");
                    File.Move(tmpPath, finalPath);
                    result.Files.Add(new UploadFile
                    {
                        FileName = fileName,
                        SavedPath = finalPath,
                        Size = size,
                    });
                }
                // 循环回顶部：读边界后的 CRLF / --（队列里已是边界后的字节）
            }
            return result;
        }

        private static void SkipLine(HttpByteStream body)
        {
            while (body.ReadByte() is int b && b >= 0 && b != '\n') { }
        }

        /// <summary>守卫计数流（2026-11-13 T-M3/M4）：CopyUntil 的 sink 包装。
        /// ① 全局总量超 Cap 抛异常（掐断声明与实际不符的恶意大包）；
        /// ② streamCap &gt; 0 时单流超限截断（字段值限 256KB，超出丢弃但继续消费保持协议同步）。
        /// 不拥有 inner，Dispose 不关闭内部流（调用方自己管）。</summary>
        private sealed class Totals { public long Cap; public long Total; }

        private sealed class CountingStream : Stream
        {
            public readonly Stream Inner;
            private readonly Totals _t;
            private readonly long _streamCap;
            private long _written;

            /// <summary>streamCap 截断发生过（字段值超限被丢弃的标志）</summary>
            public bool Truncated { get; private set; }

            public CountingStream(Stream inner, Totals t, long streamCap)
            {
                Inner = inner; _t = t; _streamCap = streamCap;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _t.Total += count;
                if (_t.Total > _t.Cap)
                    throw new IOException("multipart: body exceeds declared Content-Length");
                if (_streamCap > 0)
                {
                    if (_written >= _streamCap) { Truncated = true; return; } // 截断：丢弃但保持消费
                    if (_written + count > _streamCap)
                    {
                        int allow = (int)(_streamCap - _written);
                        Inner.Write(buffer, offset, allow);
                        _written += allow;
                        Truncated = true;
                        return;
                    }
                }
                Inner.Write(buffer, offset, count);
                _written += count;
            }

            // --- Stream 空壳（只支持写） ---
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _written;
            public override long Position { get => _written; set => throw new NotSupportedException(); }
            public override void Flush() => Inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        /// <summary>解析 Content-Disposition：name / filename* / filename</summary>
        private static (string name, string fileName) ParseDisposition(string cd)
        {
            string name = "", fileName = "";
            foreach (var seg in SplitParams(cd))
            {
                int eq = seg.IndexOf('=');
                if (eq <= 0) continue;
                var key = seg[..eq].Trim().ToLowerInvariant();
                var val = seg[(eq + 1)..].Trim().Trim('"');
                if (key == "name") name = val;
                else if (key == "filename*")
                {
                    // RFC5987: charset'lang'percent-encoded
                    var ticks = val.Split('\'');
                    if (ticks.Length == 3 && ticks[0].StartsWith("utf-8", StringComparison.OrdinalIgnoreCase))
                        fileName = HttpProcessor.UrlDecode(ticks[2]);
                    else if (fileName.Length == 0)
                        fileName = val; // 不认识的 charset，退化
                }
                else if (key == "filename" && fileName.Length == 0)
                    fileName = val; // UTF-8 兜底（filename* 优先，只有它没出现时才用）
            }
            // 防 path traversal：只取文件名部分
            fileName = Path.GetFileName(fileName.Replace('\\', '/'));
            return (name, fileName);
        }

        /// <summary>按分号拆参数（忽略引号内的分号）</summary>
        private static IEnumerable<string> SplitParams(string s)
        {
            bool inQuote = false;
            var sb = new StringBuilder();
            foreach (var c in s)
            {
                if (c == '"') inQuote = !inQuote;
                if (c == ';' && !inQuote) { yield return sb.ToString(); sb.Clear(); }
                else sb.Append(c);
            }
            yield return sb.ToString();
        }

        /// <summary>从 Content-Type 头抠 boundary（去引号）</summary>
        public static string? GetBoundary(string contentType)
        {
            foreach (var seg in contentType.Split(';'))
            {
                var t = seg.Trim();
                if (t.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
                    return t[9..].Trim('"');
            }
            return null;
        }
    }
}
