using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;

namespace ChaoDesk.Host.Ocr;

/// <summary>
/// Reads the text in a picture with PP-OCRv5 (<see cref="PaddleOcr"/>): the built-in model plus any language packs the
/// user added (<see cref="OcrPacks"/>). Free and offline. (Windows' own recogniser was dropped.)
/// </summary>
public static class TextRecognizer
{
    /// <summary>The engine's models are installed (they ship with the app).</summary>
    public static bool IsAvailable => PaddleOcr.IsAvailable;

    /// <summary>"auto" — every installed pack — or a pack id to read with that pack only.</summary>
    public static Task<RecognizedText> RecognizeAsync(BitmapSource image, string language, CancellationToken token = default)
    {
        if (!image.IsFrozen) { image = image.Clone(); image.Freeze(); }
        return Task.Run(() => Recognize(image, language, token), token);
    }

    /// <summary>The packs a language setting means: all installed (built-in first), or the one chosen while it is installed.</summary>
    public static IReadOnlyList<OcrPack> PacksFor(string language)
    {
        var installed = OcrPacks.Installed;
        return installed.FirstOrDefault(p => p.Id == language) is { } chosen ? [chosen] : installed;
    }

    private static RecognizedText Recognize(BitmapSource image, string language, CancellationToken token)
    {
        var started = Stopwatch.StartNew();
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);
        var packs = PacksFor(language);
        var lines = PaddleOcr.WithShared(e => e.Read(pixels, width, height, packs, token));
        if (lines == null) return RecognizedText.Empty(width, height, PaddleOcr.LoadError ?? Localization.Loc.T("text recognition is not available"));
        string engine = "PP-OCRv5" + (packs.Count > 1 || packs[0].Id != OcrPacks.BuiltInId ? " · " + string.Join(", ", packs.Select(p => p.Name)) : "");
        Log.Information("Recognised {Lines} lines in a {W}×{H} image ({Engine}) in {Ms} ms ({Timings})", lines.Count, width, height, engine, started.ElapsedMilliseconds, PaddleOcr.LastTimings);
        return new RecognizedText(lines, width, height, engine, started.ElapsedMilliseconds);
    }
}
