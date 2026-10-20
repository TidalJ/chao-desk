using ChaoDesk.Host.Update;

namespace ChaoDesk.Host.Tests;

public class UpdateTests
{
    private const string Hash = "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae";

    [Theory]
    [InlineData("v0.3.0", "0.3.0")]
    [InlineData("0.3", "0.3.0")]
    [InlineData("V1.2.3-beta.1", "1.2.3")]
    [InlineData("2", "2.0.0")]
    [InlineData("1.2.3.4", "1.2.3")]
    public void ParsesVersions(string tag, string expected) => Assert.Equal(Version.Parse(expected), ReleaseFeed.ParseVersion(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    public void RejectsNonVersions(string? tag) => Assert.Null(ReleaseFeed.ParseVersion(tag));

    [Fact]
    public void ServiceTypeInitializes()
    {
        // Touches the static HttpClient too: its User-Agent once ran before the version was set and threw.
        Assert.True(UpdateService.Current > new Version(0, 0, 0));
        Assert.Equal(3, UpdateService.Current.ToString().Split('.').Length);
    }

    [Fact]
    public void NewerComparesAsExpected()
    {
        Assert.True(ReleaseFeed.ParseVersion("v0.10.0") > ReleaseFeed.ParseVersion("v0.9.1"));
        Assert.Equal(ReleaseFeed.Normalize(new Version(0, 3, 0, 0)), ReleaseFeed.ParseVersion("v0.3.0"));
    }

    private static string Manifest(string version = "0.3.3", string url = "https://github.com/o/r/releases/download/v0.3.3/ChaoDeskSetup-0.3.3.exe",
        string? sha = Hash, string installer = "ChaoDeskSetup-0.3.3.exe") => $$"""
        {
          "version": "{{version}}", "installer": "{{installer}}", "url": "{{url}}", "size": 32800000,
          {{(sha == null ? "" : $"\"sha256\": \"{sha}\",")}}
          "notes": "## What's new\r\n\r\n- One\r\n- Two\r\n",
          "page": "https://github.com/o/r/releases/tag/v0.3.3"
        }
        """;

    [Fact]
    public void ReadsTheManifest()
    {
        var r = ReleaseFeed.Parse(Manifest(sha: Hash.ToUpperInvariant()));
        Assert.NotNull(r);
        Assert.Equal(new Version(0, 3, 3), r!.Version);
        Assert.Equal("ChaoDeskSetup-0.3.3.exe", r.InstallerName);
        Assert.Equal(32800000, r.InstallerSize);
        Assert.Equal(Hash, r.Sha256);
        Assert.Equal("## What's new\n\n- One\n- Two", r.Notes);
        Assert.Equal("https://github.com/o/r/releases/tag/v0.3.3", r.PageUrl);
    }

    [Fact]
    public void RejectsIncompleteOrUnsafeManifests()
    {
        Assert.Null(ReleaseFeed.Parse(Manifest(sha: null)));         // nothing to check the download against
        Assert.Null(ReleaseFeed.Parse(Manifest(sha: "abc")));
        Assert.Null(ReleaseFeed.Parse(Manifest(version: "next")));
        Assert.Null(ReleaseFeed.Parse(Manifest(url: "http://github.com/o/r/setup.exe"))); // not https
        Assert.Null(ReleaseFeed.Parse(Manifest(installer: "..\\\\evil.exe")));            // not a plain file name
        Assert.Null(ReleaseFeed.Parse("[]"));
    }
}
