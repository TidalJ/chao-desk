using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;

namespace ChaoDesk.Host.Ocr;

/// <summary>
/// PP-OCRv5 (mobile) — the recogniser of PaddleOCR, as used by Umi-OCR and RapidOCR — on ONNX Runtime. The built-in
/// model reads simplified and traditional Chinese, English, pinyin and Japanese, mixed in one line; language packs
/// (<see cref="OcrPacks"/>) add other scripts with the same detector. Two stages: DB text detection (a probability
/// map; connected regions grown by PaddleOCR's unclip ratio), so icons and pictures are left out before recognition;
/// then CTC recognition of each region at 48 px high. With several packs, each region is re-read by the packs that
/// may suit it and keeps the most confident reading. Measured against Windows' engine on the same rendered samples:
/// 1.5 % vs 3.4 % character errors on plain text, 1.6 % vs 9.6 % with small icons in the lines, ≈130 ms.
/// </summary>
public sealed class PaddleOcr : IDisposable
{
    public const string DetectorFile = "ppocrv5_mobile_det.onnx", RecognizerFile = "ppocrv5_mobile_rec.onnx", DictionaryFile = "ppocrv5_dict.txt";

    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f], Std = [0.229f, 0.224f, 0.225f];
    private const int RecHeight = 48;

    /// <summary>Below this mean character probability a region is offered to the other installed packs.</summary>
    private const float Confident = 0.9f;

    /// <summary>
    /// ONNX Runtime's memory arena and memory patterns stay off: with them one screenshot left +890 MB (+1170 MB with
    /// every pack) held until the engine was released; without, +100 MB, for ≈20 % more time.
    /// </summary>
    internal static bool UseArena = false;

    /// <summary>More threads did not help (2: 2.1 s, 4: 1.9 s, 8: 1.8 s, 12: 2.2 s on a 1467×730 screenshot).</summary>
    internal static int Threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>Below this a mostly-Latin region is offered to the Latin pack (accents the built-in model reads as plain letters).</summary>
    private const float SureLatin = 0.97f;

    private readonly SessionOptions _options;
    private readonly InferenceSession _det;
    private readonly string _detInput;
    private readonly Dictionary<string, Recognizer> _recognizers = new();

    /// <summary>One pack's recognition model and its dictionary (0 = CTC blank, last = space).</summary>
    private sealed class Recognizer(OcrPack pack, InferenceSession session, string[] dict) : IDisposable
    {
        public OcrPack Pack { get; } = pack;
        public InferenceSession Session { get; } = session;
        public string Input { get; } = session.InputMetadata.Keys.First();
        public string[] Dict { get; } = dict;
        public void Dispose() => Session.Dispose();
    }

    public PaddleOcr(string folder)
    {
        _options = new SessionOptions
        {
            IntraOpNumThreads = Threads,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableCpuMemArena = UseArena,
            EnableMemoryPattern = UseArena,
        };
        _det = new InferenceSession(Path.Combine(folder, DetectorFile), _options);
        _detInput = _det.InputMetadata.Keys.First();
    }

    public void Dispose()
    {
        _det.Dispose();
        lock (_recognizers)
        {
            foreach (var r in _recognizers.Values) r.Dispose();
            _recognizers.Clear();
        }
    }

    private Recognizer RecognizerFor(OcrPack pack)
    {
        lock (_recognizers)
        {
            if (_recognizers.TryGetValue(pack.Id, out var r)) return r;
            var (model, dictionary) = OcrPacks.PathsOf(pack);
            r = new Recognizer(pack, new InferenceSession(model, _options), ["", .. File.ReadAllLines(dictionary).Select(l => l.TrimEnd('\r')), " "]);
            _recognizers[pack.Id] = r;
            return r;
        }
    }

    /// <summary>Whether the built-in models are in <paramref name="folder"/>.</summary>
    public static bool ModelsPresent(string folder) =>
        File.Exists(Path.Combine(folder, DetectorFile)) && File.Exists(Path.Combine(folder, RecognizerFile)) && File.Exists(Path.Combine(folder, DictionaryFile));

    // ---- The shared instance: loaded on first use, released when idle ------------------------------------------

    private static readonly object Gate = new();
    private static PaddleOcr? _shared;
    private static Timer? _idle;

    public static string ModelFolder => Path.Combine(AppContext.BaseDirectory, "ocr");

    public static bool IsAvailable => ModelsPresent(ModelFolder);

    /// <summary>Why the engine could not be loaded, when it could not (for the text window).</summary>
    public static string? LoadError { get; private set; }

    /// <summary>Runs <paramref name="work"/> with the loaded engine (loading it if needed); null if it cannot load.</summary>
    public static T? WithShared<T>(Func<PaddleOcr, T> work) where T : class
    {
        PaddleOcr engine;
        lock (Gate)
        {
            if (_shared == null)
            {
                if (!IsAvailable) { LoadError = Localization.Loc.T("The text-recognition models are missing; reinstall Chao Desk."); return null; }
                try
                {
                    var started = Stopwatch.StartNew();
                    _shared = new PaddleOcr(ModelFolder);
                    LoadError = null;
                    Log.Information("PP-OCRv5 loaded in {Ms} ms", started.ElapsedMilliseconds);
                }
                catch (Exception ex) when (ex is OnnxRuntimeException or IOException or DllNotFoundException or TypeInitializationException or BadImageFormatException)
                {
                    Log.Error(ex, "PP-OCRv5 could not be loaded");
                    LoadError = Localization.Loc.F("The text-recognition engine could not start: {0}", ex.Message);
                    return null;
                }
            }
            engine = _shared;
            _idle ??= new Timer(_ => ReleaseShared());
            _idle.Change(TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan); // reloading takes ~0.1 s; the memory goes back soon
        }
        return work(engine); // InferenceSession.Run is thread-safe
    }

    /// <summary>Unloads the models (idle, or a language pack was added or removed).</summary>
    public static void ReleaseShared()
    {
        lock (Gate)
        {
            if (_shared == null) return;
            _shared.Dispose();
            _shared = null;
            Log.Information("PP-OCRv5 released");
        }
    }

    // ---- Recognition ----------------------------------------------------------------------------------------

    /// <summary>A recognised character: its x range in source pixels (from the ink) and whether a space came before it.</summary>
    private readonly record struct Glyph(char C, double X0, double X1, float Prob, bool SpaceBefore);

    private sealed record Region(int L, int T, int R, int B, int CoreT, int CoreB);

    /// <summary>
    /// One region as read by one pack: the characters with their CTC steps. <see cref="Coverage"/>: how much of the
    /// region's width the characters span — a model skips a script it does not know ("2026" out of a Korean line,
    /// confidently), so a sure reading of a fraction of the region is not a sure reading of the region.
    /// </summary>
    private sealed record Reading(Recognizer By, List<(char C, int Step, float P)> Emitted, double StepPx, double RegionWidth)
    {
        public float Confidence => Emitted.Count == 0 ? 0 : Emitted.Average(e => e.P);
        public double Coverage => Emitted.Count == 0 ? 0 : Math.Min(1, (Emitted[^1].Step - Emitted[0].Step + 1) * StepPx / (RegionWidth * 0.85));
        public string Text => new(Emitted.Select(e => e.C).ToArray());

        /// <summary>
        /// Better than <paramref name="other"/>: it reads clearly more characters (a model drops a script it does not
        /// know — the Latin pack drops the Chinese of a mixed line), or about as many with more confidence ("café"
        /// against "cafe").
        /// </summary>
        public bool Beats(Reading other)
        {
            int mine = Emitted.Count(e => e.C != ' '), theirs = other.Emitted.Count(e => e.C != ' ');
            if (mine > theirs + 1 || theirs > mine + 1) return mine * Confidence > theirs * other.Confidence;
            return Confidence > other.Confidence + 0.02f;
        }
    }

    private sealed record Line(Region Box, int InkTop, int InkBottom, List<Glyph> Glyphs, bool RightToLeft);

    /// <summary>
    /// BGRA pixels in; lines of words out (pixel coordinates of the input), in reading order. <paramref name="packs"/>:
    /// the first reads every region; the others re-read the regions it was unsure of or read only part of (and, for
    /// packs with Latin letters, the Latin ones); see <see cref="Reading.Beats"/> for which reading wins.
    /// </summary>
    /// <summary>Where the time of the last read went (detection, recognition, other packs, placing), for the log.</summary>
    public static string LastTimings { get; private set; } = "";

    public List<RecognizedLine> Read(byte[] bgra, int width, int height, IReadOnlyList<OcrPack> packs, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var regions = Detect(bgra, width, height);
        long detect = clock.ElapsedMilliseconds;
        token.ThrowIfCancellationRequested();
        if (regions.Count == 0 || packs.Count == 0) return [];
        var readings = Recognize(RecognizerFor(packs[0]), bgra, width, height, regions, token);
        long recognise = clock.ElapsedMilliseconds;
        foreach (var pack in packs.Skip(1))
        {
            var unsure = Enumerable.Range(0, regions.Count).Where(i =>
                readings[i] is not { } r || r.Confidence < Confident || r.Coverage < 0.7 || pack.Latin && MostlyLatin(r.Text) && r.Confidence < SureLatin).ToList();
            if (unsure.Count == 0) continue;
            var again = Recognize(RecognizerFor(pack), bgra, width, height, unsure.Select(i => regions[i]).ToList(), token);
            for (int k = 0; k < unsure.Count; k++)
                if (again[k] is { } r && (readings[unsure[k]] is not { } old || r.Beats(old))) readings[unsure[k]] = r;
        }
        long others = clock.ElapsedMilliseconds;
        var lines = new List<Line>();
        for (int i = 0; i < regions.Count; i++)
            if (readings[i] is { } r && Place(bgra, width, regions[i], r) is { } line) lines.Add(line);
        LastTimings = $"detect {detect} ms, recognise {recognise - detect} ms, other packs {others - recognise} ms, place {clock.ElapsedMilliseconds - others} ms, {regions.Count} regions";
        return ToLines(lines);
    }

    private static bool MostlyLatin(string text) => text.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') * 2 > text.Length;

    /// <summary>DB detection: the text probability map at 0.3, regions scoring ≥ 0.6, grown by area × 1.5 / perimeter.</summary>
    private List<Region> Detect(byte[] px, int w, int h)
    {
        double s = 1;
        if (Math.Min(w, h) < 64) s = 64.0 / Math.Min(w, h);
        if (Math.Max(w, h) * s > 4000) s = 4000.0 / Math.Max(w, h);
        int dw = Math.Max(32, (int)Math.Round(w * s / 32) * 32), dh = Math.Max(32, (int)Math.Round(h * s / 32) * 32);
        var data = new float[3 * dh * dw];
        int plane = dh * dw;
        Parallel.For(0, dh, y =>
        {
            double fy = (y + 0.5) * h / dh - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(fy), 0, h - 1), y1 = Math.Min(y0 + 1, h - 1);
            double ty = Math.Clamp(fy - y0, 0, 1);
            for (int x = 0; x < dw; x++)
            {
                double fx = (x + 0.5) * w / dw - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(fx), 0, w - 1), x1 = Math.Min(x0 + 1, w - 1);
                double tx = Math.Clamp(fx - x0, 0, 1);
                int a0 = (y0 * w + x0) * 4, a1 = (y0 * w + x1) * 4, b0 = (y1 * w + x0) * 4, b1 = (y1 * w + x1) * 4;
                for (int c = 0; c < 3; c++) // BGR, as PaddleOCR reads images
                {
                    double top = px[a0 + c] + (px[a1 + c] - px[a0 + c]) * tx, bottom = px[b0 + c] + (px[b1 + c] - px[b0 + c]) * tx;
                    data[c * plane + y * dw + x] = (float)(((top + (bottom - top) * ty) / 255.0 - Mean[c]) / Std[c]);
                }
            }
        });
        float[] prob;
        using (var results = _det.Run([NamedOnnxValue.CreateFromTensor(_detInput, new DenseTensor<float>(data, [1, 3, dh, dw]))]))
            prob = results.First().AsTensor<float>().ToArray();
        var label = new bool[dw * dh];
        var regions = new List<Region>();
        var stack = new Stack<int>();
        double kx = (double)w / dw, ky = (double)h / dh;
        for (int i = 0; i < dw * dh; i++)
        {
            if (label[i] || prob[i] < 0.3f) continue;
            int l = dw, t = dh, r = 0, b = 0, n = 0;
            double sum = 0;
            stack.Push(i);
            label[i] = true;
            while (stack.Count > 0)
            {
                int k = stack.Pop();
                int x = k % dw, y = k / dw;
                sum += prob[k];
                n++;
                if (x < l) l = x;
                if (x > r) r = x;
                if (y < t) t = y;
                if (y > b) b = y;
                if (x > 0 && !label[k - 1] && prob[k - 1] >= 0.3f) { label[k - 1] = true; stack.Push(k - 1); }
                if (x < dw - 1 && !label[k + 1] && prob[k + 1] >= 0.3f) { label[k + 1] = true; stack.Push(k + 1); }
                if (y > 0 && !label[k - dw] && prob[k - dw] >= 0.3f) { label[k - dw] = true; stack.Push(k - dw); }
                if (y < dh - 1 && !label[k + dw] && prob[k + dw] >= 0.3f) { label[k + dw] = true; stack.Push(k + dw); }
            }
            int bw = r - l + 1, bh = b - t + 1;
            if (sum / n < 0.6 || Math.Min(bw, bh) < 3) continue;
            double d = bw * bh * 1.5 / (2.0 * (bw + bh));
            regions.Add(new Region(
                (int)Math.Max(0, Math.Floor((l - d) * kx)), (int)Math.Max(0, Math.Floor((t - d) * ky)),
                (int)Math.Min(w - 1, Math.Ceiling((r + 1 + d) * kx)), (int)Math.Min(h - 1, Math.Ceiling((b + 1 + d) * ky)),
                (int)Math.Floor(t * ky), (int)Math.Min(h - 1, Math.Ceiling((b + 1) * ky))));
        }
        return regions;
    }

    /// <summary>CTC recognition of each region with one pack, in batches of similar shape (less padding).</summary>
    private static Reading?[] Recognize(Recognizer rec, byte[] px, int w, int h, List<Region> regions, CancellationToken token)
    {
        var result = new Reading?[regions.Count];
        var order = Enumerable.Range(0, regions.Count).OrderBy(i => (double)(regions[i].R - regions[i].L) / Math.Max(1, regions[i].B - regions[i].T)).ToList();
        const int Batch = 6;
        for (int start = 0; start < order.Count; start += Batch)
        {
            token.ThrowIfCancellationRequested();
            var batch = order.Skip(start).Take(Batch).ToList();
            int maxW = Math.Clamp(batch.Max(i => (int)Math.Ceiling(RecHeight * (double)(regions[i].R - regions[i].L + 1) / (regions[i].B - regions[i].T + 1))), 16, 3200);
            int n = batch.Count, plane = RecHeight * maxW;
            var data = new float[n * 3 * plane];
            var widths = new int[n];
            Parallel.For(0, n, bi =>
            {
                var box = regions[batch[bi]];
                int cw = box.R - box.L + 1, ch = box.B - box.T + 1;
                int rw = Math.Min(maxW, Math.Max(1, (int)Math.Ceiling(RecHeight * (double)cw / ch)));
                widths[bi] = rw;
                int baseIndex = bi * 3 * plane;
                for (int y = 0; y < RecHeight; y++)
                {
                    double fy = (y + 0.5) * ch / RecHeight - 0.5;
                    int y0 = Math.Clamp((int)Math.Floor(fy), 0, ch - 1), y1 = Math.Min(y0 + 1, ch - 1);
                    double ty = Math.Clamp(fy - y0, 0, 1);
                    for (int x = 0; x < rw; x++) // beyond rw stays 0: padding (grey after normalising)
                    {
                        double fx = (x + 0.5) * cw / rw - 0.5;
                        int x0 = Math.Clamp((int)Math.Floor(fx), 0, cw - 1), x1 = Math.Min(x0 + 1, cw - 1);
                        double tx = Math.Clamp(fx - x0, 0, 1);
                        int a0 = ((box.T + y0) * w + box.L + x0) * 4, a1 = ((box.T + y0) * w + box.L + x1) * 4;
                        int b0 = ((box.T + y1) * w + box.L + x0) * 4, b1 = ((box.T + y1) * w + box.L + x1) * 4;
                        for (int c = 0; c < 3; c++)
                        {
                            double top = px[a0 + c] + (px[a1 + c] - px[a0 + c]) * tx, bottom = px[b0 + c] + (px[b1 + c] - px[b0 + c]) * tx;
                            data[baseIndex + c * plane + y * maxW + x] = (float)(((top + (bottom - top) * ty) / 255.0 - 0.5) / 0.5);
                        }
                    }
                }
            });
            using var results = rec.Session.Run([NamedOnnxValue.CreateFromTensor(rec.Input, new DenseTensor<float>(data, [n, 3, RecHeight, maxW]))]);
            var tensor = (DenseTensor<float>)results.First().AsTensor<float>(); // [N, T, C], probabilities
            var probs = tensor.Buffer.Span;
            int steps = tensor.Dimensions[1], classes = tensor.Dimensions[2];
            for (int bi = 0; bi < n; bi++)
            {
                var box = regions[batch[bi]];
                var emitted = new List<(char C, int Step, float P)>();
                int previous = 0;
                for (int t = 0; t < steps; t++)
                {
                    var row = probs.Slice((bi * steps + t) * classes, classes);
                    int best = 0;
                    float bestP = row[0];
                    for (int c = 1; c < row.Length; c++)
                        if (row[c] > bestP) { bestP = row[c]; best = c; }
                    if (best != 0 && best != previous && best < rec.Dict.Length && rec.Dict[best].Length > 0) emitted.Add((rec.Dict[best][0], t, bestP));
                    previous = best;
                }
                if (emitted.Count > 0) result[batch[bi]] = new Reading(rec, emitted, (double)maxW / steps * (box.R - box.L + 1) / widths[bi], box.R - box.L + 1);
            }
        }
        return result;
    }

    /// <summary>
    /// Where each character is: the region's ink columns (in the detector's own band — the grown box can reach the
    /// next line), split at the widest empty run between neighbouring characters' time steps. A character whose ink
    /// is mostly saturated colour on a line of grey or black text is an icon read as a character, and is dropped.
    /// </summary>
    private static Line? Place(byte[] px, int w, Region box, Reading reading)
    {
        var emitted = reading.Emitted;
        double stepPx = reading.StepPx;
        int grow = Math.Max(1, (box.CoreB - box.CoreT) / 4);
        int T = Math.Max(box.T, box.CoreT - grow), B = Math.Min(box.B, box.CoreB + grow), L = box.L;
        int cw = box.R - L + 1, ch = B - T + 1;
        if (cw <= 0 || ch <= 0) return null;
        int Gray(int x, int y) { int o = (y * w + x) * 4; return (px[o] * 29 + px[o + 1] * 150 + px[o + 2] * 77) >> 8; }
        int Sat(int o) => Math.Max(px[o], Math.Max(px[o + 1], px[o + 2])) - Math.Min(px[o], Math.Min(px[o + 1], px[o + 2]));
        var border = new List<int>(cw * 2);
        for (int x = 0; x < cw; x++) { border.Add(Gray(L + x, T)); border.Add(Gray(L + x, B)); }
        border.Sort();
        int bg = border[border.Count / 2];
        var ink = new bool[cw * ch];
        var colInk = new int[cw];
        int inkTop = ch, inkBottom = -1;
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
                if (Math.Abs(Gray(L + x, T + y) - bg) > 48)
                {
                    ink[y * cw + x] = true;
                    colInk[x]++;
                    if (y < inkTop) inkTop = y;
                    if (y > inkBottom) inkBottom = y;
                }
        if (inkBottom < 0) return null;

        var centres = emitted.Select(e => (e.Step + 0.5) * stepPx).ToList();
        var cuts = new int[emitted.Count + 1];
        for (int i = 0; i + 1 < emitted.Count; i++)
        {
            int a = (int)Math.Clamp(centres[i], 0, cw - 1), b = (int)Math.Clamp(centres[i + 1], 0, cw - 1);
            int bestStart = -1, bestLen = 0, runStart = -1;
            for (int x = a; x <= b; x++)
            {
                if (colInk[x] == 0)
                {
                    if (runStart < 0) runStart = x;
                    if (x - runStart + 1 > bestLen) { bestLen = x - runStart + 1; bestStart = runStart; }
                }
                else runStart = -1;
            }
            cuts[i + 1] = bestLen > 0 ? bestStart + bestLen / 2 : (a + b) / 2;
        }
        // The first and last characters reach as far from their centre as the next cut (not to the box's edge,
        // where an icon the recogniser skipped may sit).
        double half = emitted.Count > 1 ? (centres[^1] - centres[0]) / (emitted.Count - 1) * 0.75 : cw;
        cuts[0] = (int)Math.Max(0, emitted.Count > 1 ? Math.Min(centres[0] - half, cuts[1] - (cuts[1] - centres[0]) * 2) : 0);
        cuts[^1] = (int)Math.Min(cw, emitted.Count > 1 ? Math.Max(centres[^1] + half, centres[^1] + (centres[^1] - cuts[^2])) : cw);

        var sats = new List<int>();
        for (int i = 0; i < cw * ch; i += 2) if (ink[i]) sats.Add(Sat(((T + i / cw) * w + L + i % cw) * 4));
        sats.Sort();
        bool greyText = sats.Count > 0 && sats[sats.Count / 2] < 30;

        var glyphs = new List<Glyph>();
        bool space = false;
        for (int i = 0; i < emitted.Count; i++)
        {
            if (emitted[i].C == ' ') { space = true; continue; }
            int x0 = Math.Max(0, cuts[i]), x1 = Math.Min(cw, Math.Max(cuts[i] + 1, cuts[i + 1]));
            int first = -1, last = -1;
            long count = 0, colourful = 0;
            for (int x = x0; x < x1; x++)
            {
                if (colInk[x] == 0) continue;
                if (first < 0) first = x;
                last = x;
                for (int y = 0; y < ch; y++)
                {
                    if (!ink[y * cw + x]) continue;
                    count++;
                    if (Sat(((T + y) * w + L + x) * 4) > 60) colourful++;
                }
            }
            if (greyText && count > 0 && colourful * 2 > count) { space = true; continue; } // an icon
            if (first < 0) first = last = (int)Math.Clamp(centres[i], 0, cw - 1);
            glyphs.Add(new Glyph(emitted[i].C, L + first, L + last + 1, emitted[i].P, space && glyphs.Count > 0));
            space = false;
        }
        return glyphs.Count == 0 ? null : new Line(box, T + inkTop, T + inkBottom, glyphs, reading.By.Pack.RightToLeft);
    }

    /// <summary>
    /// Regions on one row become one line. A CJK character is a word of its own; Latin characters form words split
    /// where the model put a space or at a wide gap (wider between two digits: a narrow "1" leaves wide side gaps).
    /// OcrLayout then decides the spaces between words from the real gaps. Right-to-left text (Arabic) is one word
    /// per region, put in reading order.
    /// </summary>
    private static List<RecognizedLine> ToLines(List<Line> lines)
    {
        var rows = new List<List<Line>>();
        foreach (var line in lines.OrderBy(l => (l.InkTop + l.InkBottom) / 2.0))
        {
            var row = rows.FirstOrDefault(r =>
            {
                var a = r[0];
                double overlap = Math.Min(a.InkBottom, line.InkBottom) - Math.Max(a.InkTop, line.InkTop);
                return overlap > 0.5 * Math.Min(a.InkBottom - a.InkTop, line.InkBottom - line.InkTop);
            });
            if (row == null) rows.Add([line]);
            else row.Add(line);
        }
        var result = new List<RecognizedLine>();
        foreach (var row in rows)
        {
            var words = new List<RecognizedWord>();
            bool rtl = row.All(l => l.RightToLeft);
            foreach (var line in rtl ? row.OrderByDescending(l => l.Box.L) : row.OrderBy(l => l.Box.L))
            {
                double top = line.InkTop, height = Math.Max(1, line.InkBottom - line.InkTop + 1);
                if (line.RightToLeft)
                {
                    string visual = new(line.Glyphs.Select(g => g.SpaceBefore ? " " + g.C : g.C.ToString()).SelectMany(s => s).ToArray());
                    words.Add(new RecognizedWord(ReverseRightToLeft(visual), new Rect(line.Glyphs[0].X0, top, Math.Max(1, line.Glyphs[^1].X1 - line.Glyphs[0].X0), height)));
                    continue;
                }
                var text = new System.Text.StringBuilder();
                double x0 = 0, x1 = 0;
                Glyph? previous = null;
                void Flush()
                {
                    if (text.Length > 0) words.Add(new RecognizedWord(text.ToString(), new Rect(x0, top, Math.Max(1, x1 - x0), height)));
                    text.Clear();
                }
                foreach (var g in line.Glyphs)
                {
                    double gap = previous is { } p ? g.X0 - p.X1 : 0;
                    bool digits = previous is { } q && char.IsDigit(q.C) && char.IsDigit(g.C);
                    bool split = previous == null || OcrLayout.IsCjk(g.C) || OcrLayout.IsCjk(previous.Value.C) || g.SpaceBefore || gap > height * (digits ? 0.6 : 0.35);
                    if (split) { Flush(); x0 = g.X0; }
                    text.Append(g.C);
                    x1 = g.X1;
                    previous = g;
                }
                Flush();
            }
            if (words.Count > 0) result.Add(new RecognizedLine(words));
        }
        return result;
    }

    /// <summary>
    /// The recogniser reads right-to-left text in visual order; this restores reading order, keeping runs of Latin
    /// letters, digits and simple symbols as they are (PaddleOCR's pred_reverse).
    /// </summary>
    internal static string ReverseRightToLeft(string visual)
    {
        var parts = new List<string>();
        var run = new System.Text.StringBuilder();
        foreach (char c in visual)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' || ":*./%+-".Contains(c)) { run.Append(c); continue; }
            if (run.Length > 0) { parts.Add(run.ToString()); run.Clear(); }
            parts.Add(c.ToString());
        }
        if (run.Length > 0) parts.Add(run.ToString());
        parts.Reverse();
        return string.Concat(parts).Trim();
    }
}
