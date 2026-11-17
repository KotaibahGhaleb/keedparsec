using KeedParsecNative.Streaming.Pipelines;
using Xunit;

namespace KeedParsecNative.UnitTests;

public class LatestFrameWinsPipeTests
{
    [Fact]
    public void WriteThenRead_ReturnsFrame()
    {
        using var pipe = new LatestFrameWinsPipe<string>();
        pipe.Write("frame-1");
        Assert.True(pipe.TryRead(out var frame));
        Assert.Equal("frame-1", frame);
    }

    [Fact]
    public void MultipleWrites_KeepOnlyLatest()
    {
        using var pipe = new LatestFrameWinsPipe<string>();
        pipe.Write("old");
        pipe.Write("new");

        Assert.True(pipe.TryRead(out var frame));
        Assert.Equal("new", frame);
        Assert.Null(pipe.TryReadLatest());
        Assert.True(pipe.Dropped >= 1);
    }

    [Fact]
    public void Complete_DrainsThenSignalsEnd()
    {
        using var pipe = new LatestFrameWinsPipe<string>();
        pipe.Write("last");
        pipe.Complete();

        Assert.True(pipe.TryRead(out var frame));
        Assert.Equal("last", frame);
        Assert.False(pipe.TryRead(out _));
    }

    [Fact]
    public async Task BlockingQueue_EnforcesCapacity()
    {
        using var queue = new BoundedBlockingQueue<int>(capacity: 2);
        queue.Enqueue(1);
        queue.Enqueue(2);

        var enqueueTask = Task.Run(() => queue.Enqueue(3));
        await Task.Delay(50);
        Assert.False(enqueueTask.IsCompleted);

        Assert.True(queue.TryDequeue(out var item));
        Assert.Equal(1, item);
        Assert.True(await Task.WhenAny(enqueueTask, Task.Delay(TimeSpan.FromSeconds(2))) == enqueueTask);
    }
}
