using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChaoDesk.Host.Ocr;

namespace ChaoDesk.Host.Tests;

public class OcrLayoutTests
{
    private static RecognizedWord W(string text, double x, double y = 0, double w = -1, double h = 20) =>
        new(text, new Rect(x, y, w < 0 ? text.Length * 10 : w, h));

    private static RecognizedLine L(params RecognizedWord[] words) => new(words);

    private static RecognizedText T(params RecognizedLine[] lines) => new(lines, 1000, 1000, "test", 0);

    [Fact]
    public void JoinsChineseWithoutSpacesAndLatinWithSpaces()
    {
        // Windows returns every Chinese character as its own word.
        var line = L(W("你", 0, w: 20), W("好", 21, w: 20), W("世", 42, w: 20), W("界", 63, w: 20), W("2026", 95, w: 40), W("Hello", 150, w: 50), W("world", 207, w: 50));
        Assert.Equal("你好世界 2026 Hello world", OcrLayout.LineText(line));
    }

    [Fact]
    public void NoSpaceAroundChinesePunctuationEvenWithAGap()
    {
        var line = L(W("价", 0, w: 20), W("格", 21, w: 20), W("：", 52, w: 8), W("¥128.50", 70, w: 70), W("，", 141, w: 8), W("好", 160, w: 20));
        Assert.Equal("价格：¥128.50，好", OcrLayout.LineText(line));
    }

    [Fact]
    public void KeepsLinesAndMarksParagraphGaps()
    {
        var text = T(L(W("First", 0, 0)), L(W("second", 0, 25)), L(W("Third", 0, 80)));
        Assert.Equal("First\nsecond\n\nThird", OcrLayout.Compose(text, mergeLines: false).Text);
    }

    [Fact]
    public void MergesWrappedLinesIntoParagraphs()
    {
        var text = T(
            L(W("A", 0, 0, 10), W("long", 20, 0, 40), W("sen-", 70, 0, 230)),       // ends at the right edge, hyphenated
            L(W("tence", 0, 25, 50), W("goes", 60, 25, 240)),
            L(W("on.", 0, 50, 30)),                                                // ends early with a full stop
            L(W("新", 0, 75, 20), W("段", 21, 75, 280)),
            L(W("落", 0, 100, 20)));
        Assert.Equal("A long sentence goes on.\n新段落", OcrLayout.Compose(text, mergeLines: true).Text);
    }

    [Fact]
    public void SpansPointAtEachWord()
    {
        var text = T(L(W("Hello", 0, w: 50), W("world", 60, w: 50)), L(W("你", 0, 25, 20), W("好", 21, 25, 20)));
        var (composed, spans) = OcrLayout.Compose(text, mergeLines: false);
        Assert.Equal("Hello world\n你好", composed);
        Assert.Equal("world", composed.Substring(spans[1].Start, spans[1].Length));
        Assert.Equal("好", composed.Substring(spans[3].Start, spans[3].Length));
    }

    [Fact]
    public void SelectionKeepsReadingOrderAndLines()
    {
        var text = T(L(W("one", 0, w: 30), W("two", 40, w: 30), W("three", 80, w: 50)), L(W("four", 0, 25, 40)));
        var selected = new HashSet<(int, int)> { (0, 1), (0, 2), (1, 0) };
        Assert.Equal("two three\nfour", OcrLayout.SelectionText(text, selected));
    }

}

public class TextRecognizerTests
{
    private static BitmapSource Render(string text, double size, double shift = 0)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, Brushes.Black, 1.0);
        int w = (int)Math.Ceiling(ft.Width) + 40, h = (int)Math.Ceiling(ft.Height) + 30;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawText(ft, new Point(20 + shift, 15 + shift));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Rendered mixed Chinese and English text at small to large sizes (PP-OCRv5 when its models are built, else Windows).</summary>
    [Theory]
    [InlineData(13, 0)]
    [InlineData(14, 0.5)]
    [InlineData(15, 0)]
    [InlineData(15, 1)]
    [InlineData(16, 0.3)]
    [InlineData(18, 0)]
    [InlineData(20, 0)]
    [InlineData(26, 0)]
    [InlineData(40, 0)]
    public void ReadsMixedChineseAndEnglish(double size, double shift) => AssertReads(size, shift, icons: false);

    /// <summary>Coloured icons inside the lines are not read as characters (PP-OCRv5).</summary>
    [Theory]
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(22)]
    public void IconsAreNotReadAsText(double size)
    {
        if (!PaddleOcr.IsAvailable) return; // models not built here
        AssertReads(size, 0, icons: true);
    }

[Fact]
    public void RightToLeftTextIsPutInReadingOrder()
    {
        // The recogniser returns Arabic in visual order; Latin and digit runs keep their own direction.
        Assert.Equal("مرحبا 2026", PaddleOcr.ReverseRightToLeft("2026 ابحرم"));
        Assert.Equal("abc", PaddleOcr.ReverseRightToLeft("abc"));
    }

    /// <summary>
    /// Language packs read their scripts (French accents, Russian, Korean) next to the built-in model. Runs when
    /// CHAODESK_OCR_PACKS names a folder with downloaded packs as &lt;id&gt;_rec.onnx and &lt;id&gt;_dict.txt.
    /// </summary>
    [Theory]
    [InlineData("latin", "Le café coûte 3 € à Zürich", "Segoe UI")]
    [InlineData("eslav", "Привет мир, это тест 2026", "Segoe UI")]
    [InlineData("korean", "안녕하세요 세계 2026", "Malgun Gothic")]
    [InlineData("latin", "Hello world 你好世界 2026", "Microsoft YaHei UI")] // the Latin pack must not take the Chinese away
    [InlineData("korean", "价格 ¥128.50，email a@b.com", "Microsoft YaHei UI")]
    public void LanguagePacksReadTheirScripts(string pack, string sample, string font)
    {
        string? source = Environment.GetEnvironmentVariable("CHAODESK_OCR_PACKS");
        if (!PaddleOcr.IsAvailable || source == null || !File.Exists(Path.Combine(source, pack + "_rec.onnx"))) return;
        string root = Path.Combine(Path.GetTempPath(), $"chaodesk-packs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, pack));
        File.Copy(Path.Combine(source, pack + "_rec.onnx"), Path.Combine(root, pack, "rec.onnx"));
        File.Copy(Path.Combine(source, pack + "_dict.txt"), Path.Combine(root, pack, "dict.txt"));
        string previous = OcrPacks.Root;
        OcrPacks.Root = root;
        PaddleOcr.ReleaseShared();
        try
        {
            string? text = null;
            RunSta(() =>
            {
                var result = TextRecognizer.RecognizeAsync(RenderLine(sample, 24, font), "auto").GetAwaiter().GetResult();
                text = OcrLayout.Compose(result, mergeLines: false).Text;
            });
            Assert.True(text?.Replace('￥', '¥') == sample, $"read \"{text}\" for \"{sample}\""); // the full-width yen sign is the same sign
        }
        finally
        {
            PaddleOcr.ReleaseShared();
            OcrPacks.Root = previous;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// A pack really downloads, passes its checksums and is removed again (the CDN once answered 403 to requests
    /// without a User-Agent). Needs the network: runs when CHAODESK_NETWORK_TESTS=1.
    /// </summary>
    [Fact]
    public void LanguagePackDownloadsAndRemoves()
    {
        if (Environment.GetEnvironmentVariable("CHAODESK_NETWORK_TESTS") != "1") return;
        string root = Path.Combine(Path.GetTempPath(), $"chaodesk-download-{Guid.NewGuid():N}");
        string previous = OcrPacks.Root;
        OcrPacks.Root = root;
        try
        {
            var pack = OcrPacks.Find("el")!; // the smallest
            OcrPacks.InstallAsync(pack, null, CancellationToken.None).GetAwaiter().GetResult();
            Assert.True(OcrPacks.IsInstalled(pack));
            OcrPacks.Remove(pack);
            Assert.False(OcrPacks.IsInstalled(pack));
        }
        finally
        {
            OcrPacks.Root = previous;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static BitmapSource RenderLine(string text, double size, string font)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(font), size, Brushes.Black, 1.0);
        int w = (int)Math.Ceiling(ft.Width) + 40, h = (int)Math.Ceiling(ft.Height) + 30;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawText(ft, new Point(20, 15));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private const string Sample = "Hello world 你好世界 2026\n价格 ¥128.50，email a@b.com";

    private static void AssertReads(double size, double shift, bool icons)
    {
        if (!TextRecognizer.IsAvailable) return; // nothing to read with here
        string? text = null;
        RunSta(() =>
        {
            var image = icons ? RenderWithIcons(Sample, size) : Render(Sample, size, shift);
            var result = TextRecognizer.RecognizeAsync(image, "auto").GetAwaiter().GetResult();
            text = OcrLayout.Compose(result, mergeLines: false).Text.Replace('￥', '¥');
        });
        Assert.NotNull(text);
        foreach (string expected in new[] { "Hello world", "你好世界", "2026", "128.50", "a@b.com" })
            Assert.True(text!.Contains(expected), $"\"{expected}\" not in: {text.Replace("\n", " / ")}");
        if (icons) Assert.DoesNotContain("●", text);
    }

    private static void RunSta(Action work)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { work(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Exception("recognition failed", failure);
    }

    /// <summary>The sample with a coloured icon at the start of each line and a coloured dot after the first word.</summary>
    private static BitmapSource RenderWithIcons(string text, double size)
    {
        var lines = text.Split('\n');
        double lineHeight = size * 1.6;
        int w = (int)(size * 30), h = (int)(lineHeight * lines.Length + 30);
        var dv = new DrawingVisual();
        Brush[] colours = [Brushes.SteelBlue, Brushes.OrangeRed, Brushes.SeaGreen];
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            for (int i = 0; i < lines.Length; i++)
            {
                double x = 20, y = 15 + i * lineHeight;
                var glyph = new FormattedText("\uE80F", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe Fluent Icons"), size * 1.1, colours[i % 3], 1.0);
                dc.DrawText(glyph, new Point(x, y));
                x += glyph.Width + size * 0.5;
                var parts = lines[i].Split(' ', 2);
                var first = new FormattedText(parts[0] + " ", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, Brushes.Black, 1.0);
                dc.DrawText(first, new Point(x, y));
                x += first.WidthIncludingTrailingWhitespace;
                dc.DrawEllipse(colours[(i + 1) % 3], null, new Point(x + size * 0.45, y + size * 0.65), size * 0.42, size * 0.42);
                x += size * 1.3;
                var rest = new FormattedText(parts.Length > 1 ? parts[1] : "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, Brushes.Black, 1.0);
                dc.DrawText(rest, new Point(x, y));
            }
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
