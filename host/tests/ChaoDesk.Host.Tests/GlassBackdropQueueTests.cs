using System.Buffers;
using ChaoDesk.Host.Panel;

namespace ChaoDesk.Host.Tests;

public class GlassBackdropQueueTests
{
    [Fact]
    public void NativeMoveConsumptionKeepsOnePendingRenderCallback()
    {
        var render = new Queue<Action>();
        var delivered = new List<int>();
        using var frames = new GlassBackdrop.FrameQueue(
            callback => { render.Enqueue(callback); return true; }, frame => delivered.Add(frame.X));

        // Native movement consumes each frame while Dispatcher Render is paused.
        // The already queued callback must retain its slot throughout the move.
        for (int index = 0; index < 100; index++)
        {
            frames.Publish(Frame(index));
            frames.PresentLatest();
            Assert.Single(render);
        }
        Assert.Equal(Enumerable.Range(0, 100), delivered);

        render.Dequeue()();
        Assert.Empty(render);
        Assert.Equal(100, delivered.Count); // Resuming Render cannot upload twice.

        frames.Publish(Frame(100));
        Assert.Single(render);
        render.Dequeue()();
        Assert.Equal(Enumerable.Range(0, 101), delivered);
    }

    [Fact]
    public void PausedRenderRetainsOnlyTheNewestFrame()
    {
        var render = new Queue<Action>();
        var delivered = new List<int>();
        using var frames = new GlassBackdrop.FrameQueue(
            callback => { render.Enqueue(callback); return true; }, frame => delivered.Add(frame.X));

        for (int index = 0; index < 100; index++) frames.Publish(Frame(index));

        Assert.Single(render);
        Assert.Empty(delivered);
        render.Dequeue()();
        Assert.Equal(new[] { 99 }, delivered);
        Assert.Empty(render);
    }

    [Fact]
    public void DisposePreventsQueuedManualAndFutureUploads()
    {
        var render = new Queue<Action>();
        int delivered = 0;
        using var frames = new GlassBackdrop.FrameQueue(
            callback => { render.Enqueue(callback); return true; }, _ => delivered++);

        frames.Publish(Frame(0));
        Assert.Single(render);
        frames.Dispose();
        frames.PresentLatest();
        render.Dequeue()();
        frames.Publish(Frame(1));
        frames.PresentLatest();

        Assert.Equal(0, delivered);
        Assert.Empty(render);
    }

    private static GlassBackdrop.Frame Frame(int id) => new(ArrayPool<byte>.Shared.Rent(4), 1, 1, id, 0);
}
