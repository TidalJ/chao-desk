using ChaoDesk.Host.Capture;
using ChaoDesk.Host.Input;

namespace ChaoDesk.Host.Tests;

public class ScrollStitcherTests
{
    private const int W = 40, H = 120;

    /// <summary>A tall "page": every row different, with some blank stretches like real pages.</summary>
    private static byte[] Page(int height)
    {
        var rng = new Random(3);
        var page = new byte[height * W * 4];
        for (int y = 0; y < height; y++)
        {
            bool blank = y % 50 is > 30 and < 38;
            byte v = (byte)rng.Next(256);
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                page[i] = blank ? (byte)255 : (byte)(v + x);
                page[i + 1] = blank ? (byte)255 : (byte)(v * 3 + x);
                page[i + 2] = blank ? (byte)255 : (byte)(y & 0xFF);
            }
        }
        return page;
    }

    private static byte[] Frame(byte[] page, int top)
    {
        var f = new byte[W * H * 4];
        Buffer.BlockCopy(page, top * W * 4, f, 0, f.Length);
        return f;
    }

    [Fact]
    public void StitchesScrolledFramesBackIntoThePage()
    {
        var page = Page(600);
        var s = new ScrollStitcher(W, H);
        int[] tops = [0, 45, 90, 170, 250, 300, 380, 470, 480];
        foreach (int top in tops) s.Add(Frame(page, top));
        Assert.Equal(480 + H, s.Height);
        Assert.Equal(page.AsSpan(0, s.Height * W * 4).ToArray(), s.Compose());
    }

    [Fact]
    public void AFrameThatDidNotMoveAddsNothing()
    {
        var page = Page(300);
        var s = new ScrollStitcher(W, H);
        s.Add(Frame(page, 0));
        Assert.Equal(0, s.Add(Frame(page, 0)));
        Assert.Equal(60, s.Add(Frame(page, 60)));
        Assert.Equal(H + 60, s.Height);
    }

    [Fact]
    public void UnrelatedContentIsNotStitched()
    {
        var s = new ScrollStitcher(W, H);
        s.Add(Frame(Page(300), 0));
        var other = new byte[W * H * 4];
        new Random(99).NextBytes(other);
        Assert.Equal(0, s.Add(other));
    }

    [Fact]
    public void FunctionKeysWorkAsHotkeysOnTheirOwn()
    {
        Assert.True(Hotkey.Parse("F1").IsUsable);
        Assert.Equal("Shift+F1", Hotkey.Parse("shift+f1").ToString());
        Assert.Equal("Ctrl+F1", Hotkey.Parse("Ctrl+F1").ToString());
    }
}
