using System.IO;
using System.Text;
using ChaoDesk.Host.Panel;

namespace ChaoDesk.Host.Tests;

public sealed class PanelCustomActionRunnerTests
{
    [Fact]
    public void ProgramPlanSeparatesFilenameFromArgumentsWithoutShellOrElevation()
    {
        var item = new PanelItem { Kind = "program", Target = @"C:\Tools & Stuff\Demo.exe", Arguments = "--name \"A&B\" --value %PATH%", WorkingDirectory = @"C:\Tools & Stuff" };
        Assert.True(PanelCustomActionRunner.TryCreatePlan(item, out var plan, out string error), error);
        Assert.Equal(item.Target, plan!.StartInfo!.FileName);
        Assert.Equal(item.Arguments, plan.StartInfo.Arguments);
        Assert.Equal(item.WorkingDirectory, plan.StartInfo.WorkingDirectory);
        Assert.False(plan.StartInfo.UseShellExecute);
        Assert.Empty(plan.StartInfo.Verb);
        Assert.Null(plan.TemporaryScriptPath);
    }

    [Fact]
    public void WebsitePlanUsesTheDefaultBrowserWithoutPassingCommandArguments()
    {
        Assert.True(PanelCustomActionRunner.TryCreatePlan(new PanelItem { Kind = "url", Target = "example.com/a?q=1&b=2", Arguments = "ignored" }, out var plan, out _));
        Assert.Equal("https://example.com/a?q=1&b=2", plan!.StartInfo!.FileName);
        Assert.True(plan.StartInfo.UseShellExecute);
        Assert.Empty(plan.StartInfo.Arguments);
        Assert.Empty(plan.StartInfo.Verb);
    }

    [Theory]
    [InlineData("file", @"Z:\Offline\report.txt")]
    [InlineData("folder", @"Z:\Offline\notes.v2")]
    public void FileAndFolderPlansOnlyOpenTheirOwnTargetAssociation(string kind, string target)
    {
        Assert.True(PanelCustomActionRunner.TryCreatePlan(new PanelItem
        {
            Kind = kind, Target = target, Arguments = "ignored", WorkingDirectory = "ignored", Engine = "cmd"
        }, out var plan, out string error), error);
        Assert.Equal(target, plan!.StartInfo!.FileName);
        Assert.True(plan.StartInfo.UseShellExecute);
        Assert.Empty(plan.StartInfo.Arguments);
        Assert.Empty(plan.StartInfo.WorkingDirectory);
        Assert.Empty(plan.Action.Engine);
        Assert.Null(plan.TemporaryScriptPath);
    }

    [Fact]
    public async Task WrongFileAndFolderTypesFailBeforeOpeningAnyAssociation()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ChaoDeskTypes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "owned.txt");
        try
        {
            File.WriteAllText(file, "This fixture is never opened.");
            await Assert.ThrowsAsync<FileNotFoundException>(() => PanelCustomActionRunner.ExecuteAsync(new PanelItem { Kind = "file", Target = folder }));
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => PanelCustomActionRunner.ExecuteAsync(new PanelItem { Kind = "folder", Target = file }));
        }
        finally { File.Delete(file); Directory.Delete(folder); }
    }

    [Fact]
    public void PowerShellPlanKeepsExactBodyOutsideTheCommandLineWithoutProfilesOrPolicyBypass()
    {
        string body = "  Write-Output '你好 & $value'\r\n\t# keep trailing spaces  ";
        Assert.True(PanelCustomActionRunner.TryCreatePlan(new PanelItem { Kind = "script", Engine = "powershell", Target = body }, out var plan, out _));
        var start = plan!.StartInfo!;
        Assert.Contains("-NoProfile", start.ArgumentList);
        Assert.Contains("-NonInteractive", start.ArgumentList);
        Assert.DoesNotContain("-ExecutionPolicy", start.ArgumentList);
        string bootstrap = Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList[^1]));
        Assert.Contains("$env:CHAODESK_ACTION_SCRIPT", bootstrap);
        Assert.Contains("[ScriptBlock]::Create", bootstrap);
        Assert.Contains("[System.Text.Encoding]::UTF8", bootstrap);
        Assert.DoesNotContain(body, bootstrap);
        Assert.Equal(body, plan.TemporaryScriptContent);
        Assert.Equal(".ps1", Path.GetExtension(plan.TemporaryScriptPath));
        Assert.Equal(plan.TemporaryScriptPath, start.Environment["CHAODESK_ACTION_SCRIPT"]);
        Assert.False(File.Exists(plan.TemporaryScriptPath));
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.True(start.RedirectStandardError);
        Assert.Empty(start.Verb);
    }

    [Fact]
    public void LongPowerShellBodiesDoNotIncreaseCommandLineLength()
    {
        string longBody = "# " + new string('界', 24000) + "\r\nWrite-Output 'owned long fixture'";
        Assert.True(longBody.Length > 20000);
        Assert.True(PanelCustomActionRunner.TryCreatePlan(new PanelItem { Kind = "script", Engine = "powershell", Target = "exit 0" }, out var shortPlan, out _));
        Assert.True(PanelCustomActionRunner.TryCreatePlan(new PanelItem { Kind = "script", Engine = "powershell", Target = longBody }, out var longPlan, out _));
        Assert.Equal(shortPlan!.StartInfo!.ArgumentList, longPlan!.StartInfo!.ArgumentList);
        Assert.True(longPlan.StartInfo.ArgumentList.Sum(argument => argument.Length + 1) < 1024);
        Assert.Equal(longBody, longPlan.TemporaryScriptContent);
        Assert.False(File.Exists(longPlan.TemporaryScriptPath));
    }

    [Fact]
    public void CmdPlanKeepsBodyOffTheCommandLineAndDoesNotCreateFiles()
    {
        string body = "echo literal & echo second\r\nexit /b 0";
        Assert.True(PanelCustomActionRunner.TryCreatePlan(new PanelItem { Kind = "script", Engine = "cmd", Target = body }, out var plan, out _));
        Assert.NotNull(plan!.TemporaryScriptPath);
        Assert.False(File.Exists(plan.TemporaryScriptPath));
        Assert.Equal(plan.TemporaryScriptPath, plan.StartInfo!.Environment["CHAODESK_ACTION_SCRIPT"]);
        Assert.DoesNotContain(body, plan.StartInfo.Arguments);
        Assert.DoesNotContain(plan.TemporaryScriptPath, plan.StartInfo.Arguments);
        Assert.EndsWith(body, plan.TemporaryScriptContent);
        Assert.False(plan.StartInfo.UseShellExecute);
        Assert.True(plan.StartInfo.CreateNoWindow);
    }

    [Fact]
    public void CommandPlanForcesCmdAndKeepsPipesAndMultilineBodyOffTheCommandLine()
    {
        const string body = "  echo first|findstr first && echo second\r\n\techo third  \r\n";
        Assert.True(PanelCustomActionRunner.TryCreatePlan(new PanelItem
        {
            Kind = "command", Target = body, Engine = "powershell", Arguments = "ignored", WorkingDirectory = @"C:\Owned Fixture"
        }, out var plan, out string error), error);
        Assert.Equal("command", plan!.Action.Kind);
        Assert.Equal("cmd", plan.Action.Engine);
        Assert.Equal(body, plan.Action.Target);
        Assert.Equal("cmd.exe", Path.GetFileName(plan.StartInfo!.FileName));
        Assert.Equal(@"C:\Owned Fixture", plan.StartInfo.WorkingDirectory);
        Assert.EndsWith(body, plan.TemporaryScriptContent);
        Assert.DoesNotContain(body, plan.StartInfo.Arguments);
        Assert.DoesNotContain(plan.TemporaryScriptPath!, plan.StartInfo.Arguments);
        Assert.False(plan.StartInfo.UseShellExecute);
        Assert.True(plan.StartInfo.RedirectStandardError);
        Assert.True(plan.StartInfo.CreateNoWindow);
        Assert.False(File.Exists(plan.TemporaryScriptPath));
    }

    [Theory]
    [InlineData("keys", "Ctrl+Shift+T")]
    [InlineData("text", "  hello\r\n你好  ")]
    public async Task InputPlansCannotSendAnythingThroughTheProcessRunner(string kind, string target)
    {
        Assert.True(PanelCustomActionRunner.TryCreatePlan(new PanelItem { Kind = kind, Target = target }, out var plan, out _));
        Assert.Null(plan!.StartInfo);
        if (kind == "text") Assert.Equal(target, plan.Text);
        else Assert.Equal(target, plan.Shortcut!.DisplayText);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PanelCustomActionRunner.ExecuteAsync(plan));
    }

    [Fact]
    public void BuiltInsCannotBecomeCustomProcesses()
    {
        Assert.False(PanelCustomActionRunner.TryCreatePlan(new PanelItem { ActionId = "restore", Target = "anything" }, out var plan, out _));
        Assert.Null(plan);
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("cmd")]
    public async Task OwnedFailingScriptReportsExitAndStderrAndCleansTemporaryFile(string engine)
    {
        string body = engine == "powershell" ? "# " + new string('x', 24000) + "\r\n[Console]::Error.WriteLine('owned fixture failed'); exit 7" : "echo owned fixture failed 1>&2\r\nexit /b 7";
        Assert.True(PanelCustomActionRunner.TryCreatePlan(new PanelItem { Kind = "script", Engine = engine, Target = body }, out var plan, out _));
        string folder = Path.Combine(Path.GetTempPath(), "ChaoDeskRunner-" + Guid.NewGuid().ToString("N") + " & %ComSpec%! 中文");
        try
        {
            // Exercise each fixed loader with metacharacters in the owned
            // fixture path, without running any saved user action.
            plan = RelocateOwnedScript(plan!, folder);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PanelCustomActionRunner.ExecuteAsync(plan!));
            Assert.Contains("7", error.Message);
            Assert.Contains("owned fixture failed", error.Message);
            if (plan!.TemporaryScriptPath != null) Assert.False(File.Exists(plan.TemporaryScriptPath));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder); }
    }

    [Fact]
    public async Task OwnedLongPowerShellScriptReadsUtf8AndCleansUpAfterSuccess()
    {
        string text = "玻璃😀";
        string units = string.Join(',', text.Select(character => (int)character));
        string body = "# " + new string('界', 24000) + "\r\n$value = '" + text + "'\r\n" +
            "if ((([char[]]$value | ForEach-Object { [int]$_ }) -join ',') -ne '" + units + "') { exit 9 }\r\n" +
            "[Console]::Out.WriteLine('owned long fixture succeeded'); exit 0";
        Assert.True(body.Length > 20000);
        Assert.True(PanelCustomActionRunner.TryCreatePlan(new PanelItem { Kind = "script", Engine = "powershell", Target = body }, out var plan, out _));
        string folder = Path.Combine(Path.GetTempPath(), "ChaoDeskRunner-" + Guid.NewGuid().ToString("N") + " 中文");
        try
        {
            plan = RelocateOwnedScript(plan!, folder);
            await PanelCustomActionRunner.ExecuteAsync(plan);
            Assert.False(File.Exists(plan.TemporaryScriptPath));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task OwnedCommandWaitsForPipesAndWorkingDirectoryAndAlwaysCleansUp(int exitCode)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ChaoDeskCommand-" + Guid.NewGuid().ToString("N") + " & literal! 中文");
        Directory.CreateDirectory(folder);
        string body = "echo owned-fixture|findstr /x owned-fixture >nul && echo pipeline succeeded\r\n" +
            "if errorlevel 1 exit /b 9\r\n" +
            "if /I not \"%CD%\"==\"%CHAODESK_FIXTURE_DIRECTORY%\" exit /b 8\r\n" +
            "echo owned command result 1>&2\r\nexit /b " + exitCode;
        try
        {
            Assert.True(PanelCustomActionRunner.TryCreatePlan(new PanelItem
            {
                Kind = "command", Target = body, Engine = "powershell", WorkingDirectory = folder
            }, out var plan, out string error), error);
            plan = RelocateOwnedScript(plan!, folder);
            plan.StartInfo!.Environment["CHAODESK_FIXTURE_DIRECTORY"] = folder;
            if (exitCode == 0) await PanelCustomActionRunner.ExecuteAsync(plan);
            else
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => PanelCustomActionRunner.ExecuteAsync(plan));
                Assert.Contains("7", failure.Message);
                Assert.Contains("owned command result", failure.Message);
            }
            Assert.False(File.Exists(plan.TemporaryScriptPath));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder); }
    }

    private static PanelCustomActionPlan RelocateOwnedScript(PanelCustomActionPlan plan, string folder)
    {
        string path = Path.Combine(folder, Path.GetFileName(plan.TemporaryScriptPath!));
        plan.StartInfo!.Environment["CHAODESK_ACTION_SCRIPT"] = path;
        return plan with { TemporaryScriptPath = path };
    }
}
