using System.Diagnostics;
using ChaoDesk.Host.Files;
using Xunit.Abstractions;

namespace ChaoDesk.Host.Tests;

/// <summary>Timing of a search over a few million names. Run on demand: dotnet test -c Release --filter Benchmark</summary>
public class FileIndexBenchmark(ITestOutputHelper output)
{
    [Fact(Skip = "benchmark; run on demand")]
    public void SearchThreeMillionNames()
    {
        var rng = new Random(1);
        string[] words = ["report", "photo", "invoice", "data", "backup", "project", "notes", "video", "music", "setup", "log", "config", "test", "draft", "final", "照片", "报告", "文档"];
        string[] exts = [".txt", ".jpg", ".png", ".docx", ".pdf", ".mp4", ".dll", ".js", ".json", ".cs"];
        var index = new FileIndex();
        var build = Stopwatch.StartNew();
        using (var w = index.BeginWrite())
        {
            w.BeginVolume(2, 3_000_000);
            for (long i = 0; i < 3_000_000; i++)
            {
                string name = $"{words[rng.Next(words.Length)]}_{rng.Next(100000)}{exts[rng.Next(exts.Length)]}";
                w.Upsert(2, 1000 + i, i < 50_000 ? 0x0005000000000005 : 1000 + rng.Next(50_000), i < 50_000 ? 0x10u : 0, name);
            }
            w.EndVolume(2);
        }
        output.WriteLine($"build: {build.ElapsedMilliseconds} ms, ~{index.MemoryBytes / 1048576} MB");
        foreach (string q in new[] { "pinyin", "report", "report 123", "ext:mp4", "照片", "*.json", "rep" })
        {
            FileSearchResult r = FileSearchResult.None;
            double best = double.MaxValue;
            for (int run = 0; run < 8; run++)
            {
                var sw = Stopwatch.StartNew();
                r = index.Search(FileQuery.Parse(q), 30, false);
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            output.WriteLine($"{q,-12} {r.Total,9:N0} hits  {best,7:F1} ms (best of 8)");
        }
    }
}
