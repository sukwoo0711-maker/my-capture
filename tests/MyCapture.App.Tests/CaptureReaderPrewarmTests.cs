using MyCapture.App.Capture;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureReaderPrewarmTests
{
    [Fact]
    public async Task StartsBothReadersAgainstOneWindowSnapshot_AndWaitsForBoth()
    {
        IReadOnlyList<WindowCandidate> windows = [];
        var page = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int snapshots = 0, readers = 0;
        Task warm = CaptureReaderPrewarm.RunAsync(() => { snapshots++; return windows; },
            seen => { Assert.Same(windows, seen); readers++; return page.Task; },
            seen => { Assert.Same(windows, seen); readers++; return app.Task; });
        Assert.Equal(1, snapshots);
        Assert.Equal(2, readers);
        Assert.False(warm.IsCompleted);
        page.SetResult();
        Assert.False(warm.IsCompleted);
        app.SetResult();
        await warm;
    }

    [Fact]
    public async Task ObservesFailureWithoutSkippingOtherReader()
    {
        bool appStarted = false;
        Task warm = CaptureReaderPrewarm.RunAsync(() => [],
            _ => throw new InvalidOperationException("Reader failed"),
            _ => { appStarted = true; return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => warm);
        Assert.True(appStarted);
    }

    [Fact]
    public async Task MissingOptionalApplicationReaderStillWarmsPages()
    {
        bool read = false;
        await CaptureReaderPrewarm.RunAsync(() => [],
            _ => { read = true; return Task.CompletedTask; }, null);
        Assert.True(read);
    }
}
