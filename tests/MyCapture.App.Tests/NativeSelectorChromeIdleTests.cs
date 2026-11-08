using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using static MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

public sealed class NativeSelectorChromeIdleTests
{
    private static readonly RectD Bounds = new(0, 0, 640, 360);

    [Fact]
    public void TrimSkipsRetainedWindowsTheApplicationAlreadyClosed() => StaTestHost.Run(() =>
    {
        // Application shutdown closes every window before the dispatcher raises ShutdownStarted, whose
        // handler trims the cache. Trimming must not touch the destroyed windows.
        try
        {
            using var owner = Owner();
            NativeSelectorChrome chrome = NativeSelectorChromeCache.Acquire(owner, true, false);
            chrome.Show();
            PumpUntil(() => owner.IsChromeReady);
            NativeSelectorChromeCache.Release(chrome);
            owner.Close();
            IntPtr[] handles = Handles(chrome);
            Assert.Equal(handles.Length, NativeSelectorChromeCache.RetainedWindowCount);
            foreach (Window window in chrome.Windows.ToArray()) window.Close();
            Assert.All(handles, handle => Assert.False(NativeSelectorInterop.IsWindow(handle)));

            NativeSelectorChromeCache.Trim();

            Assert.Equal(0, NativeSelectorChromeCache.RetainedWindowCount);
            Assert.Empty(chrome.Windows);
        }
        finally
        {
            NativeSelectorChromeCache.Trim();
        }
    });

    [Fact]
    public void IdleCollectionPreservesDetachedWindowsAndTheNextLeaseRendersAgain() => StaTestHost.Run(() =>
    {
        var marks = new List<string>();
        Action<string>? previousMark = SelectorTimeline.Mark;
        SelectorTimeline.Mark = marks.Add;
        try
        {
            using var owner = Owner();
            IntPtr oldOwner = owner.Handle;
            using var chrome = NativeSelectorChromeCache.Acquire(owner, true, false);
            chrome.Show();
            PumpUntil(() => owner.IsChromeReady);
            AssertChromeWitness(marks);
            IntPtr[] handles = Handles(chrome);
            var image = Assert.IsType<Image>(Assert.IsType<Border>(chrome.Windows.Last().Content).Child);
            NativeSelectorChromeCache.Release(chrome);
            owner.Close();
            NativeSelectorChromeCache.TrimForIdle();
            NativeSelectorChromeCache.TrimForIdle();

            Assert.Equal(4, NativeSelectorChromeCache.RetainedWindowCount);
            Assert.True(chrome.IsDetached);
            Assert.Null(owner.Frame);
            Assert.All(handles, handle => Assert.True(NativeSelectorInterop.IsWindow(handle)));
            Assert.All(chrome.Windows, window =>
            {
                Assert.False(window.IsVisible);
                Assert.Null(window.Content);
                Assert.NotEqual(oldOwner, new WindowInteropHelper(window).Owner);
            });
            var pixels = new byte[15 * 15 * 4];
            Assert.IsType<WriteableBitmap>(image.Source).CopyPixels(pixels, 60, 0);
            Assert.All(pixels, value => Assert.Equal((byte)0, value));
            Assert.Equal(1, marks.Count(mark => mark == "native-chrome-cache-kept-on-idle"));

            using var nextOwner = Owner();
            marks.Clear();
            Assert.Same(chrome, NativeSelectorChromeCache.Acquire(nextOwner, true, false));
            Assert.Equal(handles, Handles(chrome));
            Assert.False(nextOwner.IsChromeReady);
            Assert.Contains("native-chrome-cache-hit", marks);
            Assert.Contains("native-chrome-cache-hit-after-idle", marks);
            chrome.Show();
            PumpUntil(() => nextOwner.IsChromeReady);
            AssertChromeWitness(marks);
            NativeSelectorChromeCache.Release(chrome);
            NativeSelectorChromeCache.Trim();
            Assert.Equal(0, NativeSelectorChromeCache.RetainedWindowCount);
            Assert.All(handles, handle => Assert.False(NativeSelectorInterop.IsWindow(handle)));

            marks.Clear();
            using var finalOwner = Owner();
            using var fresh = NativeSelectorChromeCache.Acquire(finalOwner, true, false);
            Assert.NotSame(chrome, fresh);
            Assert.Contains("native-chrome-cache-miss", marks);
            Assert.DoesNotContain("native-chrome-cache-hit-after-idle", marks);
        }
        finally
        {
            NativeSelectorChromeCache.Trim();
            SelectorTimeline.Mark = previousMark;
        }
    });

    [Theory]
    // Windows clamps one window to the screen, so every retained window is enlarged; together they
    // exceed MaxRetainedSurfaceBytes (16 MiB) on any screen of at least 1024 x 768.
    [InlineData(2049.0, false)]
    [InlineData(2049.0, true)]
    [InlineData(double.NaN, false)]
    [InlineData(double.NaN, true)]
    public void InvalidOrOversizedSurfaceIsReleasedOnReturnOrIdle(double size, bool resizeAfterReturn) => StaTestHost.Run(() =>
    {
        try
        {
            using var owner = Owner();
            using var chrome = NativeSelectorChromeCache.Acquire(owner, true, false);
            chrome.Show();
            PumpUntil(() => owner.IsChromeReady);
            IntPtr[] handles = Handles(chrome);
            if (resizeAfterReturn) NativeSelectorChromeCache.Release(chrome);
            foreach (Window window in chrome.Windows)
            {
                window.Width = size;
                window.Height = size;
            }
            if (resizeAfterReturn) NativeSelectorChromeCache.TrimForIdle();
            else NativeSelectorChromeCache.Release(chrome);
            Assert.Equal(0, NativeSelectorChromeCache.RetainedWindowCount);
            Assert.All(handles, handle => Assert.False(NativeSelectorInterop.IsWindow(handle)));
        }
        finally { NativeSelectorChromeCache.Trim(); }
    });

    [Fact]
    public void EmptyIdleCollectionDoesNotCreateWindowsOrMarks() => StaTestHost.Run(() =>
    {
        var marks = new List<string>();
        Action<string>? previousMark = SelectorTimeline.Mark;
        SelectorTimeline.Mark = marks.Add;
        try
        {
            NativeSelectorChromeCache.TrimForIdle();
            NativeSelectorChromeCache.TrimForIdle();
            Assert.Equal(0, NativeSelectorChromeCache.RetainedWindowCount);
            Assert.Empty(marks);
        }
        finally { SelectorTimeline.Mark = previousMark; }
    });

    [Fact]
    public void SurfaceBudgetRoundsPhysicalDimensionsAndIncludesEveryWindow()
    {
        long bytes = 0;
        Assert.True(NativeSelectorChromeCache.TryAddSurfaceBytes(100.1, 50.1, 1.25, 1.25, ref bytes));
        Assert.Equal(126L * 63 * 4, bytes);
        long remainingPixels = (NativeSelectorChromeCache.MaxRetainedSurfaceBytes - bytes) / 4;
        Assert.True(NativeSelectorChromeCache.TryAddSurfaceBytes(remainingPixels, 1, 1, 1, ref bytes));
        Assert.Equal(NativeSelectorChromeCache.MaxRetainedSurfaceBytes, bytes);
        Assert.False(NativeSelectorChromeCache.TryAddSurfaceBytes(1, 1, 1, 1, ref bytes));
        Assert.Equal(NativeSelectorChromeCache.MaxRetainedSurfaceBytes, bytes);
    }

    [Theory]
    [InlineData(double.NaN, 1, 1, 1)]
    [InlineData(1, double.PositiveInfinity, 1, 1)]
    [InlineData(1, 1, double.NaN, 1)]
    [InlineData(1, 1, 1, double.NegativeInfinity)]
    [InlineData(0, 1, 1, 1)]
    [InlineData(1, -1, 1, 1)]
    [InlineData(1, 1, 0, 1)]
    [InlineData(1, 1, 1, -1)]
    [InlineData(2049, 2049, 1, 1)]
    [InlineData(double.MaxValue, 1, 2, 1)]
    public void InvalidSurfaceMeasurementCannotEnterTheBudget(double width, double height, double scaleX, double scaleY)
    {
        long bytes = 16;
        Assert.False(NativeSelectorChromeCache.TryAddSurfaceBytes(width, height, scaleX, scaleY, ref bytes));
        Assert.Equal(16, bytes);
    }

    private static void AssertChromeWitness(List<string> marks)
    {
        int ready = marks.IndexOf("native-chrome-ready");
        Assert.Equal(1, marks.Count(mark => mark == "native-chrome-ready"));
        foreach (string window in new[] { "bar", "size", "guide", "magnifier" })
        {
            string prefix = $"native-chrome-{window}-";
            int started = marks.IndexOf(prefix + "show-start");
            int shown = marks.IndexOf(prefix + "show-complete");
            int placed = marks.IndexOf(prefix + "placed");
            int rendered = marks.IndexOf(prefix + "rendered");
            Assert.Equal(1, marks.Count(mark => mark == prefix + "rendered"));
            Assert.True(started >= 0 && started < shown && shown < placed && placed < rendered && rendered < ready,
                $"The {window} witness must show, place and render before chrome readiness: {string.Join(", ", marks)}");
        }
    }

    private static NativeCaptureOverlay Owner()
    {
        BitmapSource bitmap = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgr32, null,
            Enumerable.Repeat((byte)100, 640 * 360 * 4).ToArray(), 640 * 4);
        bitmap.Freeze();
        var owner = new NativeCaptureOverlay(Bounds, false, true)
        {
            ReadCursor = () => new PointD(100, 120),
            ReadMonitors = () => [new MonitorInfo("test", Bounds, Bounds, 96, true)],
        };
        owner.AttachFrame(new FrozenFrame(bitmap, Bounds, null, 0));
        return owner;
    }

    private static IntPtr[] Handles(NativeSelectorChrome chrome) =>
        chrome.Windows.Select(window => new WindowInteropHelper(window).Handle).ToArray();
}
