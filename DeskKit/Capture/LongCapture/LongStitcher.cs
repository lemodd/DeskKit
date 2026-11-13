using System.Windows.Media.Imaging;

namespace MiniPix.Capture.LongCapture;

/// <summary>
/// 长截图拼接核心（2026-11-11 第二次重构：ShareX ScrollingCaptureManager 同款算法）。
///
/// 架构前提（这是与前两版失败实现的本质区别）：
/// 调用方（LongCaptureSession）以固定小步长【程序自动滚动】——帧间位移恒定且恒小于帧高，
/// 相邻两帧的重叠必然充足。因此用「锚定 + 最长连续近似相等行」即可精准对齐，
/// 不再需要签名评分 / 纯色守卫 / 上滚粗判那套启发式（手动自由滚动的被动观察注定脆弱）。
///
/// 算法（对照 ShareX CombineImages）：
///  1. 行比较忽略左右边缘 max(50, w/20) px——排除滚动条与固定侧栏对匹配的干扰；
///  2. 吸底固定带检测：新帧底部与累积图底部自底向上连续相等的行（吸底元素）。
///     检出后从累积图尾部裁掉、且永不追加——长图不重复吸底元素（比 ShareX 更干净）；
///  3. 锚定搜索：假设新帧第 cy 行与累积图最后一行重合，自底向上数最长连续近似相等行，
///     取首个最大者（cy 最大 = 滚动距离最小 = 追加最少 = 最保守，防重复内容页疯长）；
///  4. 只追加对齐行以下、固定带以上的新行。
/// </summary>
public sealed class LongStitcher
{
    /// <summary>拼接结果高度上限（物理 px），防止内存失控。</summary>
    public const int MaxTotalHeight = 12000;

    /// <summary>每通道字节差容忍（抗轻微渲染抖动；同源 BitBlt 帧内容稳定时差异为 0）。</summary>
    private const int PixelTolerance = 6;

    /// <summary>对齐可信的最低连续相等行数（比 ShareX 的 matchCount>0 更保守）。</summary>
    private const int MinMatchRows = 4;

    private readonly List<byte[]> _rows = new();
    private int _w, _h;
    private int _sideIgnore;                   // 行比较忽略的左右边缘像素数（滚动条/固定侧栏）
    private int _bottomBand;                   // 检出的吸底带高度（历史最大值）
    private int _tailBand;                     // 累积图尾部已裁掉的带行数

    public int TotalHeight => _rows.Count;
    public bool HasFrames => _rows.Count > 0;

    /// <summary>吸底带检测开关（2026-11-13 C-H1）：手动模式（自由滚动，帧序无保证）由
    /// 调用方关掉，避免把"用户没滚动"误判成吸底元素裁掉真实内容。</summary>
    public bool EnableBottomBandDetect { get; set; } = true;

    /// <summary>首帧初始化：整帧作为画布起点（若有吸底带，会在第一次 Append 时裁掉）。</summary>
    public void Init(int width, int height, byte[] frame)
    {
        _w = width; _h = height;
        _sideIgnore = Math.Min(Math.Max(50, _w / 20), _w / 3);
        _bottomBand = 0;
        _tailBand = 0;
        _rows.Clear();
        for (int y = 0; y < height; y++)
        {
            var row = new byte[width * 4];
            Array.Copy(frame, y * width * 4, row, 0, width * 4);
            _rows.Add(row);
        }
    }

    /// <summary>拼入一帧，返回追加的行数；-1 = 找不到可信对齐（调用方丢弃该帧，继续滚动）。</summary>
    public int Append(byte[] frame)
    {
        if (_rows.Count == 0) throw new InvalidOperationException("LongStitcher: call Init before Append.");
        // 2026-11-13 C-H1：吸底带检测加前提。帧与累积图尾部相同 = 页面没滚出新内容，
        // 此时底部 N 行"连续相等"是整帧相同造成的假象，绝不能当吸底带裁——
        // 否则长截图开头（首次滚动还没生效）和结尾（已到底继续拍）都会丢最多 h/4 的真实内容。
        // 只有真的滚动了（帧内出现新内容）才允许带检测增长。
        if (EnableBottomBandDetect && !FrameEqualsTail(frame))
            DetectAndTrimBottomBand(frame);
        int rows = _rows.Count;                    // 带裁剪可能减少行数，取裁剪后的值

        int hEff = _h - _bottomBand;               // 有效内容高度（扣除吸底带）
        if (hEff < MinMatchRows * 2) return 0;

        // ---- 锚定搜索：新帧第 cy 行 ↔ 累积图最后一行，自底向上数最长连续近似相等行 ----
        // 帧滚动距离 d 时正确对齐在 cy = hEff - 1 - d，追加行数 = d。
        int matchLimit = hEff / 2;
        int bestCy = -1, bestCount = 0;
        for (int cy = hEff - 1; cy >= 0 && bestCount < matchLimit; cy--)
        {
            int count = 0;
            for (int y = 0; cy - y >= 0 && count < matchLimit; y++)
            {
                if (!RowEqual(_rows[rows - 1 - y], frame, cy - y)) break;
                count++;
            }
            // 首个最大者：cy 最大 = 追加行数最少 = 最保守
            if (count > bestCount) { bestCount = count; bestCy = cy; }
        }

        if (bestCount < MinMatchRows) return -1;

        int append = hEff - 1 - bestCy;
        if (append <= 0) return 0;                 // 没滚出新内容（含微小回滚）
        AppendRows(frame, bestCy + 1, hEff);
        return append;
    }

    /// <summary>累积行 → BitmapSource（Bgra32，盖 DPI 章），Freeze 后返回。后台线程可调。</summary>
    public BitmapSource BuildImage(double dpiX, double dpiY)
    {
        int h = _rows.Count;
        if (h == 0 || _w == 0)
            throw new InvalidOperationException("LongStitcher: nothing captured.");

        var buf = new byte[_w * 4 * h];
        for (int y = 0; y < h; y++)
            Array.Copy(_rows[y], 0, buf, y * _w * 4, _w * 4);

        var img = BitmapSource.Create(_w, h, dpiX, dpiY, System.Windows.Media.PixelFormats.Bgra32, null, buf, _w * 4);
        img.Freeze();
        return img;
    }

    // ---------------- 内部实现 ----------------

    /// <summary>本帧是否与累积图尾部完全相同（= 页面没滚出新内容）。整帧 O(w·h) 比较，
    /// 与锚定搜索同量级，成本可忽略。</summary>
    private bool FrameEqualsTail(byte[] frame)
    {
        int rows = _rows.Count;
        int check = Math.Min(_h, rows);
        for (int y = 0; y < check; y++)
        {
            if (!RowEqual(_rows[rows - check + y], frame, y)) return false;
        }
        return true;
    }

    /// <summary>吸底带检测：新帧底部与累积图底部自底向上数连续相等行（ShareX AutoIgnoreBottomEdge）。
    /// 检出更大带时，把累积图尾部尚未裁掉的带行裁掉——保证锚定行是纯内容。
    /// 带高上限 h/4：真实吸底条（聊天输入框/cookie 栏）很少超过帧高 1/4，
    /// 同时把"页面未滚但画面微动"（hover 动画）时的误裁损失限制在可控范围。</summary>
    private void DetectAndTrimBottomBand(byte[] frame)
    {
        int rows = _rows.Count;
        int maxB = Math.Min(_h / 4, rows);
        int b = 0;
        for (int i = 0; i < maxB; i++)
        {
            if (!RowEqual(_rows[rows - 1 - i], frame, _h - 1 - i)) break;
            b++;
        }
        if (b > _bottomBand)
        {
            _bottomBand = b;
            while (_tailBand < _bottomBand && _rows.Count > MinMatchRows)
            {
                _rows.RemoveAt(_rows.Count - 1);
                _tailBand++;
            }
        }
    }

    private void AppendRows(byte[] frame, int fromRow, int toRowExclusive)
    {
        // 追加 [fromRow, toRowExclusive) 的行（正序，图从上往下生长）
        for (int y = fromRow; y < toRowExclusive && TotalHeight < MaxTotalHeight; y++)
        {
            var row = new byte[_w * 4];
            Array.Copy(frame, y * _w * 4, row, 0, _w * 4);
            _rows.Add(row);
        }
    }

    /// <summary>行比较：忽略左右 _sideIgnore 边缘，RGB 逐字节【精确相等】（ShareX 用 SequenceEqual 同款）。
    /// 固定小步长滚动下，静态内容跨帧逐像素不变（BitBlt 确定性渲染），
    /// 精确比较才不会因容差在平坦内容上产生 ±N 行的对齐漂移。</summary>
    private bool RowEqual(byte[] imgRow, byte[] frame, int frameRowIdx)
    {
        int start = _sideIgnore * 4;
        int end = (_w - _sideIgnore) * 4;
        int off = frameRowIdx * _w * 4;
        for (int i = start; i < end; i += 4)
        {
            if (imgRow[i] != frame[off + i] ||
                imgRow[i + 1] != frame[off + i + 1] ||
                imgRow[i + 2] != frame[off + i + 2])
                return false;
        }
        return true;
    }

    /// <summary>两帧抽样近似全等判断（到底检测用：连续两帧一致 = 页面已滚到底）。</summary>
    public static bool FramesEqual(byte[] a, byte[] b)
    {
        for (int i = 0; i < a.Length; i += 16) // 每 4 像素抽 1
        {
            if (Math.Abs(a[i] - b[i]) > PixelTolerance ||
                Math.Abs(a[i + 1] - b[i + 1]) > PixelTolerance ||
                Math.Abs(a[i + 2] - b[i + 2]) > PixelTolerance)
                return false;
        }
        return true;
    }
}
