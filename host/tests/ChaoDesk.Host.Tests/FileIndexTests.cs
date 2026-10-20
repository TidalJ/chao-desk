using System.IO;
using ChaoDesk.Host.Files;

namespace ChaoDesk.Host.Tests;

public class FileIndexTests
{
    private const int C = 2; // volume index of C:
    private const uint Dir = 0x10, Hidden = 0x2;
    private const long Root = 0x0005000000000005;

    /// <summary>C:\Users\me\Documents\report 2024.docx and friends.</summary>
    private static FileIndex Sample()
    {
        var index = new FileIndex();
        using var w = index.BeginWrite();
        w.BeginVolume(C, 100);
        // Children arrive before their parents on purpose (MFT order is not tree order).
        w.Upsert(C, 40, 30, 0, "report 2024.docx");
        w.Upsert(C, 41, 30, 0, "budget.xlsx");
        w.Upsert(C, 42, 31, 0, "holiday.mp4");
        w.Upsert(C, 43, 31, Hidden, "thumbs.db");
        w.Upsert(C, 10, Root, Dir, "Users");
        w.Upsert(C, 20, 10, Dir, "me");
        w.Upsert(C, 30, 20, Dir, "Documents");
        w.Upsert(C, 31, 20, Dir, "Videos");
        w.Upsert(C, 50, Root, Dir, "Windows");
        w.Upsert(C, 51, 50, 0, "report.log");
        w.EndVolume(C);
        return index;
    }

    private static FileSearchResult Find(FileIndex index, string query, bool hidden = false) => index.Search(FileQuery.Parse(query), 50, hidden);

    [Fact]
    public void BuildsFullPaths()
    {
        var hit = Assert.Single(Find(Sample(), "budget").Hits);
        Assert.Equal(@"C:\Users\me\Documents\budget.xlsx", hit.Path);
        Assert.False(hit.IsDirectory);
    }

    [Fact]
    public void AllWordsMustMatch()
    {
        var index = Sample();
        Assert.Equal(2, Find(index, "report").Total);
        Assert.Single(Find(index, "report 2024").Hits);
        Assert.Empty(Find(index, "report 2023").Hits);
    }

    [Fact]
    public void RanksUserFoldersAboveWindows()
    {
        var hits = Find(Sample(), "report").Hits;
        Assert.Equal("report 2024.docx", hits[0].Name);
        Assert.Equal("report.log", hits[1].Name);
    }

    [Fact]
    public void FiltersByExtensionWildcardKindAndPath()
    {
        var index = Sample();
        Assert.Equal("holiday.mp4", Assert.Single(Find(index, "ext:mp4;mkv").Hits).Name);
        Assert.Equal("budget.xlsx", Assert.Single(Find(index, "*.xlsx").Hits).Name);
        Assert.Equal("report 2024.docx", Assert.Single(Find(index, "rep*.docx").Hits).Name);
        Assert.Equal(5, Find(index, "folder:").Total); // Users, me, Documents, Videos, Windows
        Assert.Equal("report.log", Assert.Single(Find(index, @"windows\ report").Hits).Name);
        Assert.Equal("Documents", Assert.Single(Find(index, "folder: doc").Hits).Name);
    }

    [Fact]
    public void HiddenFilesOnlyWhenAsked()
    {
        var index = Sample();
        Assert.Empty(Find(index, "thumbs").Hits);
        Assert.Single(Find(index, "thumbs", hidden: true).Hits);
    }

    [Fact]
    public void FollowsRenamesMovesAndDeletes()
    {
        var index = Sample();
        using (var w = index.BeginWrite())
        {
            w.Upsert(C, 41, 31, 0, "budget 2025.xlsx"); // renamed and moved to Videos
            w.Delete(C, 42);
            w.Upsert(C, 60, 30, 0, "new note.txt");
        }
        Assert.Equal(@"C:\Users\me\Videos\budget 2025.xlsx", Assert.Single(Find(index, "budget").Hits).Path);
        Assert.Empty(Find(index, "holiday").Hits);
        Assert.Single(Find(index, "note").Hits);
    }

    [Fact]
    public void ExcludedFoldersHideTheirContents()
    {
        var index = Sample();
        index.SetExcludedFolders([@"C:\Users\me\Documents\"]);
        Assert.Empty(Find(index, "budget").Hits);
        Assert.Single(Find(index, "holiday").Hits);
        index.SetExcludedFolders([]);
        Assert.Single(Find(index, "budget").Hits);
    }

    [Fact]
    public void SurvivesCompactionAfterManyDeletes()
    {
        var index = new FileIndex();
        using (var w = index.BeginWrite())
        {
            w.BeginVolume(C, 10);
            w.Upsert(C, 1, Root, Dir, "keep");
            for (long i = 0; i < 120_000; i++) w.Upsert(C, 100 + i, 1, 0, $"file{i}.tmp");
            w.EndVolume(C);
        }
        using (var w = index.BeginWrite())
            for (long i = 0; i < 119_000; i++) w.Delete(C, 100 + i);
        Assert.Equal(1_001, index.Count);
        var hit = Find(index, "file119999").Hits.Single();
        Assert.Equal(@"C:\keep\file119999.tmp", hit.Path);
        using (var w = index.BeginWrite()) w.Upsert(C, 100 + 119_500, 1, 0, "renamed.tmp");
        Assert.Single(Find(index, "renamed").Hits);
    }

    [Fact]
    public void SavedIndexLoadsBackAndKeepsFollowingChanges()
    {
        var index = Sample();
        string path = Path.Combine(Path.GetTempPath(), $"chaodesk-index-{Guid.NewGuid():N}.bin");
        try
        {
            bool ran = false;
            index.Save(path, () => ran = true);
            Assert.True(ran);

            var loaded = new FileIndex();
            Assert.True(loaded.TryLoad(path));
            Assert.Equal(index.Count, loaded.Count);
            Assert.Equal(@"C:\Users\me\Documents\budget.xlsx", Assert.Single(Find(loaded, "budget").Hits).Path);
            // The record map came back too: journal changes keep applying by reference number.
            using (var w = loaded.BeginWrite())
            {
                w.Delete(C, 41);
                w.Upsert(C, 70, 31, 0, "new clip.mp4");
            }
            Assert.Empty(Find(loaded, "budget").Hits);
            Assert.Equal(@"C:\Users\me\Videos\new clip.mp4", Assert.Single(Find(loaded, "new clip").Hits).Path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DamagedSavedIndexIsRejected()
    {
        string path = Path.Combine(Path.GetTempPath(), $"chaodesk-index-{Guid.NewGuid():N}.bin");
        try
        {
            Sample().Save(path, () => { });
            var bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
            var index = new FileIndex();
            Assert.False(index.TryLoad(path));
            Assert.Equal(0, index.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CheckpointsRoundTripAndIgnoreGarbage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"chaodesk-cp-{Guid.NewGuid():N}.txt");
        try
        {
            IndexCheckpoints.Write(path, new Dictionary<char, IndexCheckpoints.Entry> { ['C'] = new(123456789012, 42), ['D'] = new(7, 0) });
            File.AppendAllText(path, "garbage line\nE -1 5\n");
            var read = IndexCheckpoints.Read(path);
            Assert.Equal(2, read.Count);
            Assert.Equal(new IndexCheckpoints.Entry(123456789012, 42), read['C']);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReloadingAVolumeReplacesIt()
    {
        var index = Sample();
        using (var w = index.BeginWrite())
        {
            w.BeginVolume(C, 10);
            w.Upsert(C, 7, Root, 0, "only.txt");
            w.EndVolume(C);
        }
        Assert.Equal(1, index.Count);
        Assert.Empty(Find(index, "budget").Hits);
    }
    [Fact]
    public void SkipsWhatIsInsideNodeModulesAndWinSxS()
    {
        var index = new FileIndex();
        index.SetSkipJunk(true);
        using (var w = index.BeginWrite())
        {
            w.BeginVolume(C, 100);
            w.Upsert(C, 10, Root, Dir, "app");
            w.Upsert(C, 11, 10, Dir, "node_modules");
            w.Upsert(C, 12, 11, Dir, "left-pad");
            w.Upsert(C, 13, 12, 0, "index.js");
            w.Upsert(C, 14, 10, 0, "index.js");
            w.Upsert(C, 50, Root, Dir, "Windows");
            w.Upsert(C, 51, 50, Dir, "WinSxS");
            w.Upsert(C, 52, 51, 0, "notepad.exe");
            w.Upsert(C, 53, 50, 0, "notepad.exe");
            w.EndVolume(C);
        }
        Assert.Equal(@"C:\app\index.js", Assert.Single(Find(index, "index.js").Hits).Path);
        Assert.Equal(@"C:\Windows\notepad.exe", Assert.Single(Find(index, "notepad").Hits).Path);
        Assert.Single(Find(index, "node_modules").Hits); // the folder itself stays findable

        // Live changes inside a skipped folder are not kept either; a new node_modules is skipped at once.
        using (var w = index.BeginWrite())
        {
            w.Upsert(C, 15, 12, 0, "readme.md");
            w.Upsert(C, 20, 10, Dir, "web");
            w.Upsert(C, 21, 20, Dir, "node_modules");
            w.Upsert(C, 22, 21, 0, "react.js");
            w.Upsert(C, 23, 20, 0, "readme.md");
        }
        Assert.Equal(@"C:\app\web\readme.md", Assert.Single(Find(index, "readme").Hits).Path);
        Assert.Empty(Find(index, "react").Hits);

        // Turning it off asks for the drives to be read again.
        Assert.True(index.SetSkipJunk(false));
    }
}

public class IndexProtocolTests
{
    [Fact]
    public void WriterProducesTheDocumentedLayout()
    {
        var ms = new MemoryStream();
        var w = new IndexWriter(ms);
        w.Hello();
        w.Entry(2, 40, 30, 0x10, "报告.docx");
        w.Delete(2, 41);
        w.Flush();
        ms.Position = 0;
        var r = new BinaryReader(ms);
        Assert.Equal(IndexProtocol.Hello, r.ReadByte());
        Assert.Equal(IndexProtocol.Version, r.ReadInt32());
        Assert.Equal(IndexProtocol.Entry, r.ReadByte());
        Assert.Equal(2, r.ReadByte());
        Assert.Equal(40, r.ReadInt64());
        Assert.Equal(30, r.ReadInt64());
        Assert.Equal(0x10u, r.ReadUInt32());
        int len = r.ReadUInt16();
        Assert.Equal("报告.docx", new string(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(r.ReadBytes(len * 2))));
        Assert.Equal(IndexProtocol.Delete, r.ReadByte());
    }
}
