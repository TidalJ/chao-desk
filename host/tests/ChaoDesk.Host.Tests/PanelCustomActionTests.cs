using System.IO;
using System.Text.Json;
using ChaoDesk.Host.Panel;
using ChaoDesk.Host.Settings;

namespace ChaoDesk.Host.Tests;

public class PanelCustomActionTests
{
    [Fact]
    public void LoadingLegacyAndUnavailableActionsPreservesTheirIdentityOrderAndContents()
    {
        var legacy = JsonSerializer.Deserialize<PanelItem>("""
            {"Id":"legacy","Name":"Offline tool","Target":"Z:\\Disconnected\\tool.exe"}
            """)!;
        const string text = "  Hello\r\n世界\r\n  ";
        const string script = "  $message = 'hello'\r\nWrite-Output $message\r\n";
        var items = PanelLayout.Normalize([
            legacy,
            new() { Id = "program", Name = "Offline app", Kind = "program", Target = @"Z:\Offline\app.lnk", Arguments = " --one \"two words\" ", WorkingDirectory = @"Z:\Offline" },
            new() { Id = "text", Name = "Greeting", Kind = "text", Target = text },
            new() { Id = "script", Name = "Script", Kind = "script", Target = script, Engine = "powershell" },
            new() { Id = "future", Name = "Future", Kind = "future-kind", Target = "opaque payload" },
            new() { Id = "builtin", ActionId = "clipboard", Kind = "script", Target = "Do not dispatch this" }
        ]);

        Assert.Equal(new[] { "legacy", "program", "text", "script", "future", "builtin" }, items.Select(x => x.Id));
        Assert.Equal("shortcut", items[0].Kind);
        Assert.Equal(legacy.Target, items[0].Target);
        Assert.Equal(" --one \"two words\" ", items[1].Arguments);
        Assert.Equal(@"Z:\Offline", items[1].WorkingDirectory);
        Assert.Equal(text, items[2].Target);
        Assert.Equal(script, items[3].Target);
        Assert.False(PanelCustomAction.TryNormalize(items[4], out _, out string unsupported));
        Assert.Equal("InvalidKind", unsupported);
        Assert.False(PanelCustomAction.TryNormalize(items[5], out _, out string builtIn));
        Assert.Equal("BuiltInAction", builtIn);
    }

    [Fact]
    public void FiveKindsRoundTripWithLiteralArgumentsAndUnmodifiedMultilinePayloads()
    {
        using var files = new TestFiles();
        string program = Path.Combine(files.Directory, "test app.lnk");
        File.WriteAllText(program, "This fixture is never launched.");
        const string arguments = " --name \"two words\" --literal \"& $(example)\" ";
        const string text = "  First line\r\n\r\n第二行\t  ";
        const string script = "@echo off\r\n  echo 中文 & rem literal script body\r\n";
        var settings = new AppSettings { PanelItems = [] };
        var layout = new PanelLayout(settings.PanelItems, items => settings.PanelItems = items);
        Assert.True(layout.SaveCustom(null, new string('N', 40), "program", '"' + program + '"', arguments, files.Directory));
        Assert.True(layout.SaveCustom(null, "Docs", "url", "example.com/help?q=glass"));
        Assert.True(layout.SaveCustom(null, "Reopen tab", "keys", "Ctrl+Shift+T"));
        Assert.True(layout.SaveCustom(null, "Greeting", "text", text));
        Assert.True(layout.SaveCustom(null, "Batch", "script", script, "", files.Directory, "cmd"));
        string path = Path.Combine(files.Directory, "settings.json");
        settings.Save(path);
        var saved = AppSettings.Load(path).PanelItems;

        Assert.Equal(layout.Items.Select(x => x.Id), saved.Select(x => x.Id));
        Assert.Equal(new[] { "program", "url", "keys", "text", "script" }, saved.Select(x => x.Kind));
        Assert.Equal(32, saved[0].Name.Length);
        Assert.Equal(program, saved[0].Target);
        Assert.Equal(arguments, saved[0].Arguments);
        Assert.Equal(files.Directory, saved[0].WorkingDirectory);
        Assert.Equal("https://example.com/help?q=glass", saved[1].Target);
        Assert.Equal("Ctrl+Shift+T", saved[2].Target);
        Assert.Equal(text, saved[3].Target);
        Assert.Equal(script, saved[4].Target);
        Assert.Equal("cmd", saved[4].Engine);
        Assert.Equal(files.Directory, saved[4].WorkingDirectory);
    }

    [Fact]
    public void FileFolderAndCommandRoundTripWithTheirOwnKindsAndApplicableFields()
    {
        using var files = new TestFiles();
        string file = Path.Combine(files.Directory, "report.v1.txt");
        string folder = Path.Combine(files.Directory, "notes.v2");
        File.WriteAllText(file, "This fixture is never opened.");
        Directory.CreateDirectory(folder);
        string firstLine = "echo " + new string('x', 40) + " && echo second";
        string command = "  " + firstLine + "  \r\n\techo 中文 | findstr 中文\r\n  ";
        var settings = new AppSettings { PanelItems = [] };
        var layout = new PanelLayout([], items => settings.PanelItems = items);
        Assert.True(layout.SaveCustom(null, "", "file", file, "ignored\r\narguments", "not a directory", "unknown"));
        Assert.True(layout.SaveCustom(null, "", "folder", folder, "ignored", "not a directory", "powershell"));
        Assert.True(layout.SaveCustom(null, "", "command", command, "ignored", files.Directory, "powershell"));
        string path = Path.Combine(files.Directory, "settings.json");
        settings.Save(path);
        var saved = AppSettings.Load(path).PanelItems;

        Assert.Equal(layout.Items.Select(item => item.Id), saved.Select(item => item.Id));
        Assert.Equal(new[] { "file", "folder", "command" }, saved.Select(item => item.Kind));
        Assert.Equal(file, saved[0].Target);
        Assert.Equal(folder, saved[1].Target);
        Assert.Equal("notes.v2", saved[1].Name);
        Assert.All(saved.Take(2), item =>
        {
            Assert.Empty(item.Arguments);
            Assert.Empty(item.WorkingDirectory);
            Assert.Empty(item.Engine);
        });
        Assert.Equal(command, saved[2].Target);
        Assert.Equal(firstLine[..32], saved[2].Name);
        Assert.Equal("cmd", saved[2].Engine);
        Assert.Empty(saved[2].Arguments);
        Assert.Equal(files.Directory, saved[2].WorkingDirectory);
    }

    [Fact]
    public void FileAndFolderSavesRejectTheWrongTypeAndMissingTargets()
    {
        using var files = new TestFiles();
        string file = Path.Combine(files.Directory, "owned.txt");
        File.WriteAllText(file, "This fixture is never opened.");
        int saves = 0;
        var layout = new PanelLayout([], _ => saves++);
        Assert.False(layout.SaveCustom(null, "Wrong file", "file", files.Directory, "", "", "", out string error));
        Assert.Equal("FileNotFound", error);
        Assert.False(layout.SaveCustom(null, "Wrong folder", "folder", file, "", "", "", out error));
        Assert.Equal("FolderNotFound", error);
        string missing = Path.Combine(files.Directory, "missing");
        Assert.False(layout.SaveCustom(null, "Missing file", "file", missing, "", "", "", out error));
        Assert.Equal("FileNotFound", error);
        Assert.False(layout.SaveCustom(null, "Missing folder", "folder", missing, "", "", "", out error));
        Assert.Equal("FolderNotFound", error);
        Assert.Empty(layout.Items);
        Assert.Equal(0, saves);
    }

    [Fact]
    public void LoadingOfflineFilesFoldersAndCommandsPreservesTheirOrderAndPayload()
    {
        using var files = new TestFiles();
        string missingFile = Path.Combine(files.Directory, "offline.txt");
        string missingFolder = Path.Combine(files.Directory, "offline.folder");
        const string command = "  echo first && echo second\r\n\techo third | findstr third\r\n";
        var items = PanelLayout.Normalize([
            new() { Id = "file", Name = "Offline file", Kind = "file", Target = missingFile, Arguments = "unused", Engine = "cmd", WorkingDirectory = "unused" },
            new() { Id = "folder", Name = "Offline folder", Kind = "folder", Target = missingFolder, Arguments = "unused", Engine = "powershell", WorkingDirectory = "unused" },
            new() { Id = "command", Name = "Command", Kind = "command", Target = command, Engine = "powershell", WorkingDirectory = missingFolder }
        ]);
        Assert.Equal(new[] { "file", "folder", "command" }, items.Select(item => item.Id));
        Assert.Equal(missingFile, items[0].Target);
        Assert.Equal(missingFolder, items[1].Target);
        Assert.All(items.Take(2), item =>
        {
            Assert.True(PanelCustomAction.TryNormalize(item, out _, out _));
            Assert.Empty(item.Arguments);
            Assert.Empty(item.WorkingDirectory);
            Assert.Empty(item.Engine);
        });
        Assert.Equal(command, items[2].Target);
        Assert.Equal("cmd", items[2].Engine);
        Assert.Equal(missingFolder, items[2].WorkingDirectory);
        Assert.True(PanelCustomAction.TryNormalize(items[2], out _, out _));
        Assert.False(PanelCustomAction.TryNormalize(items[2], out _, out string error, requireExistingPaths: true));
        Assert.Equal("WorkingDirectoryNotFound", error);
    }

    [Theory]
    [InlineData("url", "javascript:alert(1)", "powershell", "InvalidUrl")]
    [InlineData("url", "file:///C:/Windows/notepad.exe", "powershell", "InvalidUrl")]
    [InlineData("url", "mailto:person@example.com", "powershell", "InvalidUrl")]
    [InlineData("url", "https://", "powershell", "InvalidUrl")]
    [InlineData("url", "https://example.com/bad path", "powershell", "InvalidUrl")]
    [InlineData("program", "relative.exe", "powershell", "InvalidPath")]
    [InlineData("program", "C:\\Apps\\bad?.exe", "powershell", "InvalidPath")]
    [InlineData("program", "C:\\Apps\\app.exe:stream", "powershell", "InvalidPath")]
    [InlineData("file", "https://example.com/report.txt", "powershell", "InvalidPath")]
    [InlineData("folder", "relative.folder", "powershell", "InvalidPath")]
    [InlineData("keys", "Ctrl+", "powershell", "InvalidKeys")]
    [InlineData("keys", "Ctrl+Ctrl+A", "powershell", "InvalidKeys")]
    [InlineData("keys", "Ctrl+A+B", "powershell", "InvalidKeys")]
    [InlineData("text", " \r\n ", "powershell", "EmptyTarget")]
    [InlineData("command", " \r\n ", "powershell", "EmptyTarget")]
    [InlineData("script", "Write-Output 1", "unknown", "InvalidScriptEngine")]
    [InlineData("script", "Write-Output 1", "", "InvalidScriptEngine")]
    [InlineData("other", "payload", "powershell", "InvalidKind")]
    public void InvalidInputNeverAddsOrPersistsAnAction(string kind, string target, string engine, string expectedError)
    {
        int saves = 0;
        var layout = new PanelLayout([], _ => saves++);
        Assert.False(layout.SaveCustom(null, "Invalid", kind, target, "", "", engine, out string error));
        Assert.Equal(expectedError, error);
        Assert.Empty(layout.Items);
        Assert.Equal(0, saves);
    }

    [Fact]
    public void InvalidEditDoesNotMutateTheSavedActionAndSuccessfulEditKeepsItsSlot()
    {
        int saves = 0;
        var layout = new PanelLayout([], _ => saves++);
        Assert.True(layout.SaveCustom(null, "First", "text", "original"));
        Assert.True(layout.SaveCustom(null, "Second", "url", "example.com"));
        PanelItem original = layout.Items[0];
        string[] order = layout.Items.Select(x => x.Id).ToArray();
        Assert.False(layout.SaveCustom(original.Id, "Broken", "url", "javascript:alert(1)", "", "", "powershell", out _));
        Assert.Same(original, layout.Items[0]);
        Assert.Equal("First", original.Name);
        Assert.Equal("original", original.Target);
        Assert.Equal(2, saves);

        Assert.True(layout.SaveCustom(original.Id, "  New name  ", "script", "  Write-Output 1\r\n"));
        Assert.Equal(order, layout.Items.Select(x => x.Id));
        Assert.Equal("New name", layout.Items[0].Name);
        Assert.Equal("  Write-Output 1\r\n", layout.Items[0].Target);
        Assert.Equal(3, saves);
        Assert.True(layout.AddAction("clipboard"));
        string builtInId = layout.Items.Last().Id;
        Assert.False(layout.SaveCustom(builtInId, "Replace", "text", "replacement", "", "", "powershell", out string error));
        Assert.Equal("NotFound", error);
        Assert.Equal("clipboard", layout.Items.Last().ActionId);
    }

    [Fact]
    public void SavingChecksExistenceWithoutMakingLoadingDestructive()
    {
        using var files = new TestFiles();
        string missing = Path.Combine(files.Directory, "missing.exe");
        var item = new PanelItem { Id = "offline", Name = "Missing app", Kind = "program", Target = missing };
        Assert.Single(PanelLayout.Normalize([item]));
        Assert.True(PanelCustomAction.TryNormalize(item, out _, out _));
        var layout = new PanelLayout([]);
        Assert.False(layout.SaveCustom(null, item.Name, item.Kind, missing, "", "", "powershell", out string error));
        Assert.Equal("PathNotFound", error);
        Assert.False(layout.SaveCustom(null, "Script", "script", "Write-Output 1", "", missing, "powershell", out error));
        Assert.Equal("WorkingDirectoryNotFound", error);
        Assert.False(layout.SaveCustom(null, "Script", "script", "Write-Output 1", "", "relative", "powershell", out error));
        Assert.Equal("InvalidWorkingDirectory", error);
        Assert.True(layout.SaveCustom(null, "Folder", "program", files.Directory));
        Assert.Equal(files.Directory, layout.Items[0].Target);
        Assert.False(layout.SaveCustom(null, "Folder", "program", files.Directory, "first\r\nsecond", "", "powershell", out error));
        Assert.Equal("InvalidArguments", error);
        Assert.True(layout.SaveCustom(null, "", "text", "  \r\n  Actual text\r\n"));
        Assert.Equal("Actual text", layout.Items[1].Name);
    }

    private sealed class TestFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "PanelCustomActionTests-" + Guid.NewGuid().ToString("N"));
        public TestFiles() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
