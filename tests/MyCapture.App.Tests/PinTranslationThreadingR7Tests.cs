using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Pinning;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTranslationThreadingR7Tests
{
    [Fact]
    public void PixelScanRunsAwayFromDispatcherAndLetsQueuedWorkProceed() => StaTestHost.Run(() =>
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        int caller = Environment.CurrentManagedThreadId, worker = caller;
        using var uiWork = new ManualResetEventSlim();
        var recognized = PinTranslationOverlayTests.Recognition(PinTranslationOverlayTests.Line("Save the document", 20, 20, 260));
        Task<IReadOnlyList<PinOverlayBlock>> blocks = PinTranslationService.BuildOverlayBlocksAsync(recognized,
            PinTranslationOverlayTests.Bitmap(1920, 1080), CancellationToken.None, () =>
            {
                worker = Environment.CurrentManagedThreadId;
                Assert.False(dispatcher.CheckAccess());
                Assert.True(uiWork.Wait(TimeSpan.FromSeconds(5)), "The UI dispatcher was blocked by block building.");
            });
        _ = dispatcher.BeginInvoke(new Action(uiWork.Set));
        PumpUntil(blocks);
        Assert.NotEqual(caller, worker);
        Assert.NotEmpty(blocks.GetAwaiter().GetResult());
    });

    [Fact]
    public void UnfrozenPictureIsCopiedBeforeTheBackgroundPipelineUsesIt() => StaTestHost.Run(() =>
    {
        var mutable = new WriteableBitmap(600, 160, 96, 96, PixelFormats.Bgra32, null);
        var recognized = PinTranslationOverlayTests.Recognition(PinTranslationOverlayTests.Line("Save the document", 20, 20, 260));
        var service = new PinTranslationService(new PinTranslationOverlayTests.FakeTranslator(source => source));
        Task<PinTranslationResult?> pending = service.TranslateRecognizedAsync(recognized,
            PinContent.FromImage(mutable), CancellationToken.None);
        PumpUntil(pending);
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(pending.GetAwaiter().GetResult());
        Assert.False(mutable.IsFrozen);
        Assert.True(result.Content.Image.IsFrozen);
        Assert.NotSame(mutable, result.Content.Image);
        Assert.Equal(mutable.PixelWidth, result.Content.Image.PixelWidth);
    });

    [Fact]
    public void ForeignThreadRequestsSnapshotOnTheOwningDispatcher() => StaTestHost.Run(() =>
    {
        var mutable = new WriteableBitmap(40, 30, 96, 96, PixelFormats.Bgra32, null);
        Task<BitmapSource> pending = Task.Run(() => PinTranslationService.FreezeOverlaySourceAsync(mutable, CancellationToken.None));
        PumpUntil(pending);
        BitmapSource snapshot = pending.GetAwaiter().GetResult();
        Assert.True(snapshot.IsFrozen);
        Assert.False(mutable.IsFrozen);
        Assert.NotSame(mutable, snapshot);
        Assert.Equal(40, snapshot.PixelWidth);
        Assert.Equal(30, snapshot.PixelHeight);
    });

    [Fact]
    public async Task FrozenPictureIsReusedAndCancellationStopsBeforeTheScan()
    {
        BitmapSource frozen = PinTranslationOverlayTests.Bitmap(40, 30);
        Assert.Same(frozen, await PinTranslationService.FreezeOverlaySourceAsync(frozen, CancellationToken.None));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool scanned = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PinTranslationService.BuildOverlayBlocksAsync(
            PinTranslationOverlayTests.Recognition(PinTranslationOverlayTests.Line("Save document", 0, 0, 30)),
            frozen, canceled.Token, () => scanned = true));
        Assert.False(scanned);
    }

    private static void PumpUntil(Task pending)
    {
        if (pending.IsCompleted) return;
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Send,
            (_, _) => frame.Continue = false, dispatcher);
        _ = pending.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        timeout.Stop();
        Assert.True(pending.IsCompleted, "The background operation did not finish while the dispatcher was pumping.");
    }
}
