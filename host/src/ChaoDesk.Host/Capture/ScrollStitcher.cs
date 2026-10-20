namespace ChaoDesk.Host.Capture;

/// <summary>
/// Builds a long screenshot from frames of the same area taken while it scrolls. Each new frame is matched
/// against the previous one by row hashes to find how far the content moved; only the newly revealed rows are
/// appended. Pure logic (unit-tested); frames are 32-bit BGRX, top-down, all the same size.
/// </summary>
public sealed class ScrollStitcher
{
    public const int MaxHeight = 30000;

    private readonly int _width;
    private readonly int _height;
    private readonly List<byte[]> _strips = new();
    private ulong[]? _previous;
    private int _total;

    public ScrollStitcher(int width, int height)
    {
        _width = width;
        _height = height;
    }

    public int Height => _total;
    public bool IsFull => _total >= MaxHeight;

    /// <summary>
    /// Adds a frame; returns how many new rows it contributed (0: the content did not move — the end, or the
    /// frame could not be matched, in which case it is ignored).
    /// </summary>
    public int Add(byte[] frame)
    {
        var hashes = RowHashes(frame);
        if (_previous == null)
        {
            _strips.Add(frame);
            _total = _height;
            _previous = hashes;
            return _height;
        }
        int shift = FindShift(_previous, hashes);
        if (shift <= 0) return 0;
        int rows = Math.Min(shift, MaxHeight - _total);
        if (rows <= 0) return 0;
        var strip = new byte[rows * _width * 4];
        Buffer.BlockCopy(frame, (_height - shift) * _width * 4, strip, 0, strip.Length);
        _strips.Add(strip);
        _total += rows;
        _previous = hashes;
        return rows;
    }

    /// <summary>The stitched image's pixels (width × <see cref="Height"/>).</summary>
    public byte[] Compose()
    {
        var all = new byte[_total * _width * 4];
        int offset = 0;
        foreach (var s in _strips)
        {
            Buffer.BlockCopy(s, 0, all, offset, Math.Min(s.Length, all.Length - offset));
            offset += s.Length;
        }
        return all;
    }

    /// <summary>
    /// The scroll distance d such that rows [0, h−d) of the new frame equal rows [d, h) of the previous one, chosen
    /// as the best match over the rows that carry content (blank rows say nothing). A fixed header or footer only
    /// lowers the match ratio. 0 when nothing moved or nothing matches well enough.
    /// </summary>
    internal static int FindShift(ulong[] prev, ulong[] cur)
    {
        int h = prev.Length;
        if (cur.Length != h || h < 8) return 0;
        if (prev.AsSpan().SequenceEqual(cur)) return 0;
        int best = 0;
        double bestScore = 0;
        for (int d = 1; d < h - 4; d++)
        {
            int overlap = h - d, matched = 0, informative = 0;
            for (int r = 0; r < overlap; r++)
            {
                ulong a = cur[r];
                if (a == cur[Math.Max(0, r - 1)] && r > 0) continue; // runs of identical rows (blank areas) are not evidence
                informative++;
                if (a == prev[r + d]) matched++;
            }
            if (informative < 4) continue;
            double score = (double)matched / informative;
            if (score > bestScore + 1e-9) { bestScore = score; best = d; }
        }
        return bestScore >= 0.7 ? best : 0;
    }

    /// <summary>One hash per row, ignoring the rightmost 24 px (scroll bars move while the content scrolls).</summary>
    internal ulong[] RowHashes(byte[] frame)
    {
        int usable = Math.Max(1, _width - Math.Min(24, _width / 4));
        var hashes = new ulong[_height];
        for (int y = 0; y < _height; y++)
        {
            ulong h = 14695981039346656037UL;
            int row = y * _width * 4;
            for (int x = 0; x < usable * 4; x += 4)
            {
                h = (h ^ frame[row + x]) * 1099511628211UL;
                h = (h ^ frame[row + x + 1]) * 1099511628211UL;
                h = (h ^ frame[row + x + 2]) * 1099511628211UL;
            }
            hashes[y] = h;
        }
        return hashes;
    }
}
