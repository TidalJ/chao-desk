using System.IO;
using System.Text;
using ChaoDesk.Host.Preview;

namespace ChaoDesk.Host.Tests;

public class TextFilesTests
{
    [Fact]
    public void DecodesBomsUtf8AndFallsBackToAnsi()
    {
        Assert.Equal("héllo", TextFiles.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("héllo")]));
        Assert.Equal("你好", TextFiles.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("你好")]));
        Assert.Equal("你好", TextFiles.Decode(Encoding.UTF8.GetBytes("你好")));
        // Invalid UTF-8 is decoded with the system ANSI code page instead of producing replacement characters.
        Assert.DoesNotContain('\uFFFD', TextFiles.Decode([0xC4, 0xE3, 0xBA, 0xC3]));
    }

    [Fact]
    public void TellsTextFromBinary()
    {
        Assert.True(TextFiles.LooksLikeText(Encoding.UTF8.GetBytes("plain text\nline 2")));
        Assert.False(TextFiles.LooksLikeText([0x4D, 0x5A, 0x90, 0x00, 0x03]));
        Assert.True(TextFiles.LooksLikeText([0xFF, 0xFE, 0x41, 0x00]));
        Assert.True(TextFiles.HasTextExtension(@"C:\x\Program.cs"));
        Assert.False(TextFiles.HasTextExtension(@"C:\x\photo.jpg"));
    }

    [Fact]
    public void RecognisesTheDownloadedFromTheInternetMark()
    {
        string path = Path.Combine(Path.GetTempPath(), $"chaodesk-motw-{Guid.NewGuid():N}.docx");
        File.WriteAllText(path, "x");
        try
        {
            Assert.False(PreviewPane.FromInternet(path));
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            Assert.True(PreviewPane.FromInternet(path));
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=1\r\n"); // intranet
            Assert.False(PreviewPane.FromInternet(path));
        }
        finally { File.Delete(path); }
    }

    private static string MakeZip(string ext, params (string Name, string Xml)[] parts)
    {
        string path = Path.Combine(Path.GetTempPath(), $"chaodesk-office-{Guid.NewGuid():N}{ext}");
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (name, xml) in parts)
            using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(xml);
        return path;
    }

    [Fact]
    public void ExtractsWordTextIncludingConsecutiveRuns()
    {
        string path = MakeZip(".docx", ("word/document.xml",
            "<w:document xmlns:w=\"w\"><w:body><w:p><w:r><w:t>Hello</w:t></w:r><w:r><w:t> world</w:t></w:r></w:p><w:p><w:r><w:t>第二段</w:t><w:tab/><w:t>tab</w:t></w:r></w:p></w:body></w:document>"));
        try { Assert.Equal("Hello world\n第二段\ttab\n", OfficeText.Extract(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExtractsSlidesInOrderAndSheetRows()
    {
        string pptx = MakeZip(".pptx",
            ("ppt/slides/slide10.xml", "<p:sld xmlns:p=\"p\" xmlns:a=\"a\"><a:p><a:r><a:t>Ten</a:t></a:r></a:p></p:sld>"),
            ("ppt/slides/slide2.xml", "<p:sld xmlns:p=\"p\" xmlns:a=\"a\"><a:p><a:r><a:t>Two</a:t></a:r></a:p></p:sld>"));
        string xlsx = MakeZip(".xlsx",
            ("xl/sharedStrings.xml", "<sst><si><t>Name</t></si><si><t>Qty</t></si></sst>"),
            ("xl/worksheets/sheet1.xml", "<worksheet><sheetData><row><c t=\"s\"><v>0</v></c><c t=\"s\"><v>1</v></c></row><row><c t=\"inlineStr\"><is><t>Apple</t></is></c><c><v>3</v></c></row></sheetData></worksheet>"));
        try
        {
            string slides = OfficeText.Extract(pptx)!;
            Assert.True(slides.IndexOf("Two") < slides.IndexOf("Ten"));
            Assert.Equal("Name\tQty\r\nApple\t3\r\n".Replace("\r\n", Environment.NewLine), OfficeText.Extract(xlsx));
        }
        finally { File.Delete(pptx); File.Delete(xlsx); }
    }

    [Fact]
    public void FormatsSizes()
    {
        Assert.Equal("512 B", QuickLookWindow.FormatSize(512));
        Assert.Equal("1.5 KB", QuickLookWindow.FormatSize(1536));
        Assert.Equal("2 MB", QuickLookWindow.FormatSize(2 * 1048576));
    }
}
