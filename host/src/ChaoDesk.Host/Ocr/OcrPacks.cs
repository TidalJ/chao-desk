using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using ChaoDesk.Host.Localization;
using Serilog;

namespace ChaoDesk.Host.Ocr;

/// <summary>A downloadable file of a language pack, checked by its SHA-256.</summary>
public sealed record PackFile(string Url, string Sha256, long Size);

/// <summary>
/// A PP-OCRv5 recognition model for one script, sharing the built-in detector. <paramref name="Latin"/>: it reads
/// Latin letters (so it is also tried on lines the main model read as Latin). <paramref name="RightToLeft"/>: its
/// text runs right to left (Arabic). <see cref="Name"/> and <see cref="Covers"/> are in the interface language.
/// </summary>
public sealed record OcrPack(string Id, string EnglishName, string EnglishCovers, PackFile Model, PackFile Dictionary, bool Latin = false, bool RightToLeft = false)
{
    public string Name => Loc.T(EnglishName);
    public string Covers => Loc.T(EnglishCovers);
    public long Size => Model.Size + Dictionary.Size;
}

/// <summary>
/// The language packs: the built-in model (simplified and traditional Chinese, English, pinyin, Japanese — shipped in
/// the app's ocr folder) and the optional PP-OCRv5 models PaddleOCR publishes per script (RapidOCR's ONNX exports on
/// ModelScope). Added packs are downloaded to %LOCALAPPDATA%\ChaoDesk\ocr\&lt;id&gt; and can be removed again.
/// </summary>
public static class OcrPacks
{
    public const string BuiltInId = "main";
    private const string Source = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/master/";

    private static OcrPack Pack(string id, string name, string covers, string modelSha, long modelSize, string dictSha, long dictSize, bool latin = false, bool rtl = false) =>
        new(id, name, covers,
            new PackFile($"{Source}onnx/PP-OCRv5/rec/{id}_PP-OCRv5_rec_mobile.onnx", modelSha, modelSize),
            new PackFile($"{Source}paddle/PP-OCRv5/rec/{id}_PP-OCRv5_rec_mobile/ppocrv5_{id}_dict.txt", dictSha, dictSize),
            latin, rtl);

    /// <summary>The built-in model; it is not downloaded (its files are part of the app).</summary>
    public static readonly OcrPack BuiltIn = new(BuiltInId, "Chinese, English, Japanese", "Simplified and traditional Chinese, English, pinyin and Japanese, also mixed in one line",
        new PackFile("", "", 16_631_306), new PackFile("", "", 74_012), Latin: true);

    /// <summary>The packs that can be added, in the order the settings list them.</summary>
    public static readonly IReadOnlyList<OcrPack> Optional =
    [
        Pack("latin", "Latin-script languages", "French, German, Spanish, Italian, Portuguese, Dutch, Polish, Czech, Turkish, Vietnamese and more",
            "b20bd37c168a570f583afbc8cd7925603890efbcdc000a59e22c269d160b5f5a", 7_904_513, "3c0a8a79b612653c25f765271714f71281e4e955962c153e272b7b8c1d2b13ff", 1_634, latin: true),
        Pack("korean", "Korean", "Korean (Hangul), with English",
            "cd6e2ea50f6943ca7271eb8c56a877a5a90720b7047fe9c41a2e541a25773c9b", 13_488_748, "a88071c68c01707489baa79ebe0405b7beb5cca229f4fc94cc3ef992328802d7", 47_451),
        Pack("eslav", "Russian, Ukrainian, Belarusian", "East Slavic languages in Cyrillic script",
            "08705d6721849b1347d26187f15a5e362c431963a2a62bfff4feac578c489aab", 7_911_802, "3e95f1581557162870cacdba5af91a4c6be2890710d395b0c3c7578e7ee5e6eb", 1_663),
        Pack("cyrillic", "Other Cyrillic-script languages", "Serbian, Bulgarian, Macedonian, Mongolian, Kazakh and more",
            "90f761b4bfcce0c8c561c0cb5c887b0971d3ec01c32164bdf7374a35b0982711", 8_074_092, "db40aa52ceb112055be80c694afdf655d5d2c4f7873704524cc16a447ca913ba", 2_781),
        Pack("arabic", "Arabic script", "Arabic, Persian, Urdu, Uyghur and more (read right to left)",
            "c1192e632d0baa9146ae5b756a0e635e3dc63c1733737ebfd1629e87144e9295", 8_023_828, "7f92f7dbb9b75a4787a83bfb4f6d14a8ab515525130c9d40a9036f61cf6999e9", 2_369, rtl: true),
        Pack("th", "Thai", "Thai",
            "de541dd83161c241ff426f7ecfd602a0ba77d686cf3ab9a6c255ea82fd08006e", 7_915_294, "57f5406f94bb6688fb7077f7be65f08bbd71cecf48c01ea26c522cb5c4836b7a", 1_767),
        Pack("el", "Greek", "Greek",
            "b4368bccd557123c702b7549fee6cd1e94b581337d1c9b65310f109131542b7f", 7_832_350, "31defc62c0c3ad3674a82da6192226a2ba98ef4ff014a7045cb88d59f9c3de31", 1_103),
        Pack("devanagari", "Devanagari script", "Hindi, Marathi, Nepali, Sanskrit and more",
            "d6f0a906580e3fa6b324a318718f1f31f268b6ea8ef985f91c2012a37f52c91e", 7_940_361, "09c7440bfc5477e5c41052304b6b185aff8c4a5e8b2b4c23c1c706f6fe1ee9fc", 1_943),
        Pack("ta", "Tamil", "Tamil",
            "a42448808b7dea87597336f12438935f40353f1949e8360acd9e06b4da21bfe1", 7_909_926, "85b541352ae18dc6ba6d47152d8bf8adff6b0266e605d2eef2990c1bf466117b", 1_723),
        Pack("te", "Telugu", "Telugu",
            "a3690451b50028a09a3316a1274f7c05728151ea3f8fd392696397a7fefcbd92", 7_923_102, "42f83f5d3fdb50778e4fa5b66c58d99a59ab7792151c5e74f34b8ffd7b61c9d6", 1_831),
    ];

    public static IEnumerable<OcrPack> All => Optional.Prepend(BuiltIn);

    public static OcrPack? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>Where added packs live (tests point it elsewhere).</summary>
    public static string Root { get; set; } = Path.Combine(AppPaths.DataRoot, "ocr");

    /// <summary>A pack's model and dictionary paths (the built-in one's are in the app's ocr folder).</summary>
    public static (string Model, string Dictionary) PathsOf(OcrPack pack) => pack.Id == BuiltInId
        ? (Path.Combine(PaddleOcr.ModelFolder, PaddleOcr.RecognizerFile), Path.Combine(PaddleOcr.ModelFolder, PaddleOcr.DictionaryFile))
        : (Path.Combine(Root, pack.Id, "rec.onnx"), Path.Combine(Root, pack.Id, "dict.txt"));

    public static bool IsInstalled(OcrPack pack)
    {
        var (model, dict) = PathsOf(pack);
        return File.Exists(model) && File.Exists(dict);
    }

    /// <summary>The installed packs, built-in first.</summary>
    public static IReadOnlyList<OcrPack> Installed => All.Where(IsInstalled).ToList();

    /// <summary>Raised (on any thread) after a pack was added or removed.</summary>
    public static event Action? Changed;

    private static readonly HttpClient Http = CreateClient();

    /// <summary>ModelScope's CDN answers 403 to requests without a User-Agent (HttpClient sends none by default).</summary>
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ChaoDesk/{typeof(OcrPacks).Assembly.GetName().Version?.ToString(3) ?? "1"}");
        return client;
    }

    /// <summary>Downloads a pack (progress 0–1) and checks both files; nothing is kept if anything fails.</summary>
    public static async Task InstallAsync(OcrPack pack, IProgress<double>? progress, CancellationToken token)
    {
        if (pack.Id == BuiltInId) return;
        string folder = Path.Combine(Root, pack.Id), staging = folder + ".part";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        try
        {
            long done = 0, total = pack.Size;
            foreach (var (file, name) in new[] { (pack.Model, "rec.onnx"), (pack.Dictionary, "dict.txt") })
            {
                string path = Path.Combine(staging, name);
                using (var response = await Http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    response.EnsureSuccessStatusCode();
                    await using var input = await response.Content.ReadAsStreamAsync(token);
                    await using var output = File.Create(path);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(buffer, token)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                        done += read;
                        progress?.Report(Math.Min(1, (double)done / total));
                    }
                }
                string hash;
                await using (var check = File.OpenRead(path)) hash = Convert.ToHexString(await SHA256.HashDataAsync(check, token));
                if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{name} of the {pack.EnglishName} pack did not match its checksum");
            }
            PaddleOcr.ReleaseShared(); // the next recognition loads the new pack
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.Move(staging, folder);
            Log.Information("OCR language pack {Pack} added", pack.Id);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) Log.Warning(ex, "Could not add the OCR language pack {Pack}", pack.Id);
            try { Directory.Delete(staging, true); } catch (IOException) { }
            throw;
        }
        Changed?.Invoke();
    }

    public static void Remove(OcrPack pack)
    {
        if (pack.Id == BuiltInId) return;
        PaddleOcr.ReleaseShared(); // its model file may be open
        string folder = Path.Combine(Root, pack.Id);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Log.Information("OCR language pack {Pack} removed", pack.Id);
        Changed?.Invoke();
    }
}
