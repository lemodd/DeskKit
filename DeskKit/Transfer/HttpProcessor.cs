using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace DeskKit.Transfer
{
    /// <summary>
    /// 一条 HTTP 请求（2026-11-10 局域网传输迷你服务器）。
    /// BodyStream 是带缓冲的字节流：头部之后剩余的字节（含粘包）都在里面，
    /// multipart/JSON 读 body 必须经它，绝不能直接读 NetworkStream。
    /// </summary>
    public sealed class HttpRequest
    {
        public string Method = "GET";
        public string Path = "/";
        public string RawQuery = "";
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public long ContentLength;
        public required HttpByteStream BodyStream;
        /// <summary>URL 解码后的查询串字典（?a=b&c=d）</summary>
        public Dictionary<string, string> Query = new();

        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : "";
    }

    /// <summary>
    /// 迷你 HTTP 协议处理：请求解析（不读 body）+ 三种响应写出。
    /// 每请求一个连接（Connection: close），避免 keep-alive 读错位——浏览器轮询是短连接，最省心。
    /// </summary>
    public static class HttpProcessor
    {
        private static readonly Encoding HeaderEnc = Encoding.UTF8;

        /// <summary>从字节流读出请求行+全部头部。失败返回 null（断开/非 HTTP）。</summary>
        public static HttpRequest? ParseRequest(HttpByteStream stream)
        {
            var line = stream.ReadLine();
            if (string.IsNullOrEmpty(line)) return null;

            // 请求行：METHOD /path?query HTTP/1.1
            var parts = line.Split(' ');
            if (parts.Length < 2) return null;
            var req = new HttpRequest { BodyStream = stream, Method = parts[0].ToUpperInvariant() };
            var target = parts[1];
            int qi = target.IndexOf('?');
            if (qi >= 0)
            {
                req.Path = UrlDecode(target[..qi]);
                req.RawQuery = target[(qi + 1)..];
            }
            else
            {
                req.Path = UrlDecode(target);
            }
            foreach (var kv in req.RawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = kv.IndexOf('=');
                if (eq > 0)
                    req.Query[UrlDecode(kv[..eq])] = UrlDecode(kv[(eq + 1)..]);
                else
                    req.Query[UrlDecode(kv)] = "";
            }

            // 头部：直到空行
            while (true)
            {
                line = stream.ReadLine();
                if (line == null) return null;          // 连接断了
                if (line.Length == 0) break;            // 空行 = 头结束
                int ci = line.IndexOf(':');
                if (ci <= 0) continue;
                req.Headers[line[..ci].Trim()] = line[(ci + 1)..].Trim();
            }

            req.ContentLength = long.TryParse(req.Header("Content-Length"), out var cl) ? cl : 0;
            return req;
        }

        // ---------------- 响应写出 ----------------

        public static void WriteResponse(NetworkStream s, int status, string contentType,
            byte[] body, params (string k, string v)[] extraHeaders)
        {
            var sb = new StringBuilder(256);
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(StatusText(status)).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            foreach (var (k, v) in extraHeaders) sb.Append(k).Append(": ").Append(v).Append("\r\n");
            sb.Append("\r\n");
            var head = HeaderEnc.GetBytes(sb.ToString());
            s.Write(head, 0, head.Length);
            s.Write(body, 0, body.Length);
            s.Flush();
        }

        public static void WriteJson(NetworkStream s, int status, string json) =>
            WriteResponse(s, status, "application/json; charset=utf-8", HeaderEnc.GetBytes(json));

        public static void WriteText(NetworkStream s, int status, string text) =>
            WriteResponse(s, status, "text/plain; charset=utf-8", HeaderEnc.GetBytes(text));

        /// <summary>文件流式下载（不整读内存）。中文文件名：RFC5987 filename* + ASCII 兜底。
        /// 图片按扩展名回真实 Content-Type + inline——octet-stream+attachment 会让手机浏览器
        /// 打不开也长按保存不了（2026-11-10 反馈）；mp4/pdf 同理 inline 可在线预览。</summary>
        public static void StreamFile(NetworkStream s, string path, string downloadName)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var ctype = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                // 2026-11-13 T-H2 修复：svg 按 octet-stream 下发（不进 image/* inline 白名单），
                // 防止上传含脚本的 .svg 被浏览器内联渲染造成存储型 XSS
                ".svg" => "application/octet-stream",
                ".mp4" => "video/mp4",
                ".pdf" => "application/pdf",
                _ => "application/octet-stream",
            };
            var inline = ctype.StartsWith("image/") || ctype == "video/mp4" || ctype == "application/pdf";
            var cd = (inline ? "inline" : "attachment")
                + $"; filename=\"{AsciiFallback(downloadName)}\"; filename*=UTF-8''{UrlEncodeRfc5987(downloadName)}";
            var head = HeaderEnc.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: {ctype}\r\n" +
                $"Content-Length: {fs.Length}\r\nConnection: close\r\n" +
                $"Cache-Control: no-store\r\nContent-Disposition: {cd}\r\n\r\n");
            s.Write(head, 0, head.Length);
            fs.CopyTo(s, 64 * 1024);
            s.Flush();
        }

        private static string StatusText(int status) => status switch
        {
            200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed", 413 => "Payload Too Large", 503 => "Service Unavailable",
            _ => "Unknown",
        };

        /// <summary>Content-Disposition ASCII 兜底名：非 ASCII 字符换 _</summary>
        private static string AsciiFallback(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name) sb.Append(c < 0x20 || c > 0x7E || c == '"' || c == '\\' ? '_' : c);
            return sb.Length == 0 ? "file" : sb.ToString();
        }

        /// <summary>RFC5987 编码（UTF-8 百分号编码，属性值里不再判别中文/ASCII）</summary>
        private static string UrlEncodeRfc5987(string name)
        {
            var bytes = Encoding.UTF8.GetBytes(name);
            var sb = new StringBuilder(bytes.Length * 3);
            foreach (var b in bytes)
            {
                // unreserved: A-Z a-z 0-9 - . _ ~
                bool safe = (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9')
                            || b == '-' || b == '.' || b == '_' || b == '~';
                if (safe) sb.Append((char)b); else sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        /// <summary>应用层 URL 解码（+ 也是空格）</summary>
        public static string UrlDecode(string s)
        {
            // 快路径：无 % 无 + 原样返回
            if (!s.Contains('%') && !s.Contains('+')) return s;
            var bytes = new List<byte>(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '%' && i + 2 < s.Length &&
                    HexVal(s[i + 1]) >= 0 && HexVal(s[i + 2]) >= 0)
                {
                    bytes.Add((byte)((HexVal(s[i + 1]) << 4) | HexVal(s[i + 2])));
                    i += 2;
                }
                else if (c == '+') bytes.Add((byte)' ');
                else bytes.Add(c > 255 ? (byte)'?' : (byte)c);
            }
            return HeaderEnc.GetString(bytes.ToArray());
        }

        private static int HexVal(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'A' and <= 'F' => c - 'A' + 10,
            >= 'a' and <= 'f' => c - 'a' + 10,
            _ => -1,
        };
    }

    /// <summary>
    /// HTTP 连接的字节流：内部 64KB 源缓冲 + CopyUntil 残留队列。
    /// 作用：① 行读取（头部解析）；② multipart 按边界切分时跨块匹配不丢字节。
    /// </summary>
    public sealed class HttpByteStream
    {
        private readonly Stream _src;
        private readonly byte[] _buf = new byte[64 * 1024];
        private int _bufLen, _bufPos;

        // CopyUntil 匹配到边界后，边界之后的残留字节放这里（下一 part 的头部/边界标记都从这里读）
        private byte[] _q = new byte[0];
        private int _qPos, _qLen;

        public HttpByteStream(Stream src) => _src = src;

        /// <summary>底层是否还有未读数据（缓冲/队列都空后向源试探性读 1 字节）</summary>
        private bool Fill()
        {
            if (_bufPos < _bufLen) return true;
            _bufPos = 0;
            _bufLen = _src.Read(_buf, 0, _buf.Length);
            return _bufLen > 0;
        }

        public int ReadByte()
        {
            if (_qPos < _qLen) return _q[_qPos++];
            if (Fill()) return _buf[_bufPos++];
            return -1;
        }

        /// <summary>读一行（到 \n 为止，去掉 \r），EOF 返回 null。头部解析用。</summary>
        public string? ReadLine(int maxLen = 256 * 1024)
        {
            var ms = new MemoryStream();
            while (true)
            {
                int b = ReadByte();
                if (b < 0) return ms.Length == 0 ? null : Encoding.UTF8.GetString(ms.ToArray());
                if (b == '\n') break;
                if (ms.Length < maxLen) ms.WriteByte((byte)b);
            }
            var text = Encoding.UTF8.GetString(ms.ToArray());
            return text.EndsWith("\r") ? text[..^1] : text;
        }

        /// <summary>把 body 精确读 n 字节（小型 body：JSON 注册请求用）</summary>
        public byte[] ReadExact(long count, long cap = 8 * 1024 * 1024)
        {
            if (count > cap) count = cap;
            var ms = new MemoryStream();
            var tmp = new byte[64 * 1024];
            while (ms.Length < count)
            {
                int want = (int)Math.Min(tmp.Length, count - ms.Length);
                int got = Read(tmp, 0, want);
                if (got <= 0) break;
                ms.Write(tmp, 0, got);
            }
            return ms.ToArray();
        }

        /// <summary>缓冲读（供 CopyTo/手动拷贝）</summary>
        public int Read(byte[] outBuf, int offset, int count)
        {
            if (_qPos < _qLen)
            {
                int n = Math.Min(count, _qLen - _qPos);
                Array.Copy(_q, _qPos, outBuf, offset, n);
                _qPos += n;
                return n;
            }
            if (!Fill()) return 0;
            int m = Math.Min(count, _bufLen - _bufPos);
            Array.Copy(_buf, _bufPos, outBuf, offset, m);
            _bufPos += m;
            return m;
        }

        /// <summary>
        /// 从流中拷贝字节到 sink，直到出现 pattern（含）为止。
        /// 找到=true（pattern 已消费，后续字节留队列）；EOF=false。
        /// 每轮先搜 carry（已有缓冲，可能已含完整边界），搜不到才向源读——
        /// 顺序反了会在"数据全在缓冲"时永远阻塞在 _src.Read。
        /// </summary>
        public bool CopyUntil(byte[] pattern, Stream sink)
        {
            int patLen = pattern.Length;
            var carry = new List<byte>(_qLen - _qPos + _bufLen - _bufPos + patLen);
            for (int i = _qPos; i < _qLen; i++) carry.Add(_q[i]);
            _qPos = _qLen = 0;
            _q = Array.Empty<byte>();
            // ★ 关键：把内部缓冲里未消费的字节也并入 carry——
            //   小 body 全部在 Fill() 缓冲里，直接读 _src 会永远阻塞（缓冲里有数据，源没新数据）
            for (int i = _bufPos; i < _bufLen; i++) carry.Add(_buf[i]);
            _bufPos = _bufLen = 0;

            var chunk = new byte[64 * 1024];
            while (true)
            {
                // ① 先搜已有 carry
                var window = carry.ToArray();
                int idx = IndexOf(window, pattern);
                if (idx >= 0)
                {
                    sink.Write(window, 0, idx);
                    int rest = window.Length - (idx + patLen);
                    _q = new byte[rest];
                    Array.Copy(window, idx + patLen, _q, 0, rest);
                    _qPos = 0; _qLen = rest;
                    return true;
                }
                // 未找到：留最后 patLen-1 字节（跨块边界前缀），其余进 sink
                int keep = Math.Min(patLen - 1, window.Length);
                if (window.Length > keep)
                    sink.Write(window, 0, window.Length - keep);
                carry.RemoveRange(0, window.Length - keep);

                // ② 再向源读一块
                int got;
                try { got = _src.Read(chunk, 0, chunk.Length); }
                catch (IOException) { got = 0; }   // 客户端断开 = EOF
                catch (SocketException) { got = 0; }
                if (got <= 0)
                {
                    if (carry.Count > 0) sink.Write(carry.ToArray(), 0, carry.Count);
                    return false;
                }
                for (int i = 0; i < got; i++) carry.Add(chunk[i]);
            }
        }

        /// <summary>简单子串搜索（窗口 ≤ 64KB+小 carry，朴素法足够）</summary>
        private static int IndexOf(byte[] hay, byte[] needle)
        {
            int n = hay.Length, m = needle.Length;
            if (m == 0 || n < m) return -1;
            for (int i = 0; i <= n - m; i++)
            {
                int j = 0;
                while (j < m && hay[i + j] == needle[j]) j++;
                if (j == m) return i;
            }
            return -1;
        }

        /// <summary>丢弃 body 剩余字节（防止下一条请求读到脏数据；本服务每请求一连接，其实可不调，保留防滥用）</summary>
        public void Drain(long remaining)
        {
            var tmp = new byte[8 * 1024];
            while (remaining > 0)
            {
                int got = Read(tmp, 0, (int)Math.Min(tmp.Length, remaining));
                if (got <= 0) break;
                remaining -= got;
            }
        }
    }
}
