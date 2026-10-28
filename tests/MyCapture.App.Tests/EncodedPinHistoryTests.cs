using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Pinning;
using MyCapture.Core.Capture;
using MyCapture.Core.Settings;
using MyCapture.Platform.Imaging;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class EncodedPinHistoryTests(ITestOutputHelper output)
{
    [Fact]
    public void ClosedHistoryReleasesDecodedPixelsAfterEncodingAndMenuReclose() => StaTestHost.Run(() =>
    {
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance);
        try
        {
            WeakReference<BitmapSource> original = CreateClosedPin(manager);
            PinHistoryTestSupport.Await(manager.LastClosedEncodingForTest);
            DrainAndCollect();
            Assert.False(original.TryGetTarget(out _));
            Assert.Equal(1, manager.ClosedCount);

            PinWindow keeper = manager.PinImage(Image(40, 20));
            WeakReference<BitmapSource> reopened = MenuRestoreAndClose(manager, keeper);
            PinHistoryTestSupport.Await(manager.LastClosedEncodingForTest);
            DrainAndCollect();
            Assert.False(reopened.TryGetTarget(out _));
            Assert.Equal(1, manager.ClosedCount);
            Assert.NotNull(manager.LastRestoreOperationForTest);
        }
        finally { manager.CloseAllForExit(); }
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<BitmapSource> CreateClosedPin(PinManager manager)
    {
        PinWindow pin = manager.PinImage(Image());
        var reference = new WeakReference<BitmapSource>(pin.PinnedContent.Image);
        pin.Close();
        pin.CompleteExit();
        return reference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<BitmapSource> MenuRestoreAndClose(PinManager manager, PinWindow keeper)
    {
        keeper.RefreshRestoreClosedMenuItem();
        Assert.IsType<MenuItem>(keeper.RestoreClosedMenuItemForTest)
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        PinHistoryTestSupport.Await(manager.LastRestoreOperationForTest!);
        PinWindow reopened = manager.SnapshotOpenPinWindows().Single(pin => pin != keeper);
        var reference = new WeakReference<BitmapSource>(reopened.PinnedContent.Image);
        reopened.Close();
        reopened.CompleteExit();
        return reference;
    }

    private static void DrainAndCollect()
    {
        PinHistoryTestSupport.Await(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    [Fact]
    public void PngResolutionRoundingDoesNotRerenderTextOnTheSameMonitor() => StaTestHost.Run(() =>
    {
        PinContent content = PinContent.FromText(Image(), "original text", false);
        PinContent restored = EncodedPinContent.Encode(content).Decode();
        Assert.Equal(content.RasterDpiX, restored.RasterDpiX);
        Assert.Equal(content.RasterDpiY, restored.RasterDpiY);
        Assert.Same(restored, restored.RenderedAt(1));
        PinHistoryTestSupport.EqualPixels(content.Image, restored.Image);
    });

    [Fact]
    public void QuickCloseAndReopenPreservesThePendingSourceLookup() => StaTestHost.Run(() =>
    {
        var pending = new TaskCompletionSource<CaptureProvenance>(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance);
        try
        {
            PinContent content = PinContent.FromImage(Image()) with { PendingProvenance = pending.Task };
            PinWindow pin = manager.RestoreSessionPin(new PinManager.ClosedPin(content, 200, 100, 1, 1, null));
            pin.Close();
            PinWindow reopened = Assert.IsType<PinWindow>(PinHistoryTestSupport.Restore(manager));
            Assert.Same(pending.Task, reopened.PinnedContent.PendingProvenance);
            CaptureProvenance resolved = CaptureProvenance.Create(url: "https://example.test/source");
            pending.SetResult(resolved);
            PinHistoryTestSupport.Await(reopened.PendingProvenanceUpdateForTest);
            Assert.Equal(resolved, reopened.Provenance);
        }
        finally { pending.TrySetResult(CaptureProvenance.Create()); manager.CloseAllForExit(); }
    });

    private static BitmapSource Image(int width = 200, int height = 100)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(i / 4 % width);
            pixels[i + 1] = 80;
            pixels[i + 2] = 160;
            pixels[i + 3] = 255;
        }
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    [Fact]
    public void EncodedContentReusesBytesAndPreservesPixelsAndText() => StaTestHost.Run(() =>
    {
        const string text = "한글\t123\r\n원문\t내용";
        PinContent content = PinContent.FromTablePicture(Image(), text);
        byte[] png = ImageCodec.EncodePng(content.Image);
        EncodedPinContent closed = EncodedPinContent.Encode(content, png);
        PinContent restored = closed.Decode();
        Assert.Same(png, closed.Png);
        Assert.Same(png, restored.EncodedPng);
        Assert.Equal(text, restored.OriginalText);
        Assert.Equal(content.Kind, restored.Kind);
        Assert.Equal(content.LayoutWidthDip, restored.LayoutWidthDip);
        Assert.Equal(content.LayoutHeightDip, restored.LayoutHeightDip);
        Assert.Equal(content.Provenance, restored.Provenance);
        Assert.True(restored.Image.IsFrozen);
        PinHistoryTestSupport.EqualPixels(content.Image, restored.Image);
        Assert.True(closed.RetainedBytes < content.EstimatedRetainedBytes);
    });

    [Fact]
    public void PngCacheReusesOnlyTheMatchingImageAndUrl() => StaTestHost.Run(() =>
    {
        using var cache = new ClipboardImageService.PngCache();
        BitmapSource image = Image();
        byte[] bytes = cache.GetOrEncode(image, "source", (source, _) => ImageCodec.EncodePng(source));
        Assert.Same(bytes, cache.TryGetEncoded(image, "source"));
        Assert.Null(cache.TryGetEncoded(image, "different"));
        Assert.Null(cache.TryGetEncoded(Image(), "source"));
        cache.Clear();
        Assert.Null(cache.TryGetEncoded(image, "source"));
    });

    [Fact]
    public void CloseEncodesInBackgroundAndReopenDecodesOffDispatcher() => StaTestHost.Run(() =>
    {
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance);
        using var release = new ManualResetEventSlim();
        int uiThread = Environment.CurrentManagedThreadId;
        int encodeThread = uiThread, decodeThread = uiThread;
        manager.EncodeClosedForTest = (content, bytes) =>
        {
            encodeThread = Environment.CurrentManagedThreadId;
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return EncodedPinContent.Encode(content, bytes);
        };
        manager.DecodeClosedForTest = encoded =>
        {
            decodeThread = Environment.CurrentManagedThreadId;
            return encoded.Decode();
        };
        try
        {
            manager.PinImage(Image()).Close();
            Assert.Equal(1, manager.ClosedCount);
            Assert.False(manager.LastClosedEncodingForTest.IsCompleted);
            Assert.True(manager.IsInteractionActive);
            Task<PinWindow?> reopen = manager.RestoreLastClosedAsync();
            Assert.False(reopen.IsCompleted);
            release.Set();
            PinWindow restored = Assert.IsType<PinWindow>(PinHistoryTestSupport.Await(reopen));
            PinHistoryTestSupport.Await(manager.LastClosedEncodingForTest);
            Assert.NotEqual(uiThread, encodeThread);
            Assert.NotEqual(uiThread, decodeThread);
            Assert.True(restored.PinnedContent.Image.IsFrozen);
            Assert.Equal(0, manager.ClosedRetainedBytes);
        }
        finally { release.Set(); manager.CloseAllForExit(); }
    });

    [Fact]
    public void ParallelRestoreRequestsPreserveOrderAndCloseAllCancelsPendingRestore() => StaTestHost.Run(() =>
    {
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance);
        try
        {
            manager.PinImage(Image(20, 10)).Close();
            manager.PinImage(Image(30, 10)).Close();
            Task<PinWindow?> newest = manager.RestoreLastClosedAsync();
            Task<PinWindow?> oldest = manager.RestoreLastClosedAsync();
            Assert.Equal(30, PinHistoryTestSupport.Await(newest)!.PinnedContent.Image.PixelWidth);
            Assert.Equal(20, PinHistoryTestSupport.Await(oldest)!.PinnedContent.Image.PixelWidth);
            manager.PinImage(Image(40, 10)).Close();
            Task<PinWindow?> canceled = manager.RestoreLastClosedAsync();
            manager.CloseAllForExit();
            Assert.Null(PinHistoryTestSupport.Await(canceled));
            Assert.Equal(0, manager.Count);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void FailedEncodingRemovesOnlyItsEntryAndLeavesNewerPinRestorable() => StaTestHost.Run(() =>
    {
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance);
        try
        {
            manager.EncodeClosedForTest = (content, bytes) => content.Image.PixelWidth == 20
                ? throw new InvalidOperationException("Test encoder failure") : EncodedPinContent.Encode(content, bytes);
            manager.PinImage(Image(20, 10)).Close();
            Task failed = manager.LastClosedEncodingForTest;
            manager.PinImage(Image(30, 10)).Close();
            PinHistoryTestSupport.Await(failed);
            PinHistoryTestSupport.Await(manager.LastClosedEncodingForTest);
            Assert.Equal(1, manager.ClosedCount);
            Assert.Equal(30, PinHistoryTestSupport.Restore(manager)!.PinnedContent.Image.PixelWidth);
            Assert.Equal(0, manager.ClosedRetainedBytes);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void DecodeCostForFullHdFixtureIsReported()
    {
        EncodedPinContent encoded = EncodedPinContent.Encode(PinContent.FromImage(Image(1920, 1080)));
        double[] samples = new double[10];
        for (int i = 0; i < samples.Length; i++)
        {
            var clock = Stopwatch.StartNew();
            PinContent decoded = encoded.Decode();
            samples[i] = clock.Elapsed.TotalMilliseconds;
            Assert.Equal(1920, decoded.Image.PixelWidth);
            Assert.Equal(1080, decoded.Image.PixelHeight);
        }
        Array.Sort(samples);
        output.WriteLine($"PNG decode only, synthetic 1920x1080, n=10: median={samples[5]:0.###} ms, max={samples[^1]:0.###} ms; encoded={encoded.Png.Length} bytes; decoded=8294400 bytes.");
    }
}
