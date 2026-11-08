using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using static MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

public sealed class NativeSelectorChromeTests
{
    private static readonly RectD Bounds = new(0, 0, 640, 360);

    [Fact]
    public void RepeatedLeasesReuseHandlesAndDetachOldOwnersAndPixels() => StaTestHost.Run(() =>
    {
        try
        {
            NativeSelectorChrome? previous = null;
            IntPtr[]? handles = null;
            for (int i = 0; i < 3; i++)
            {
                using var owner = Owner();
                var chrome = NativeSelectorChromeCache.Acquire(owner, true, false);
                if (previous is not null) Assert.Same(previous, chrome);
                Assert.False(owner.IsChromeReady);
                IntPtr[] current = Handles(chrome);
                if (handles is not null) Assert.Equal(handles, current);
                Assert.All(chrome.Windows, window => Assert.Equal(owner.Handle, new WindowInteropHelper(window).Owner));
                chrome.Show();
                PumpUntil(() => owner.IsChromeReady);
                Assert.True(chrome.CanRetain);
                var image = Assert.IsType<Image>(Assert.IsType<Border>(chrome.Windows.Last().Content).Child);
                NativeSelectorChromeCache.Release(chrome);
                Assert.True(chrome.IsDetached);
                Assert.Equal(4, NativeSelectorChromeCache.RetainedWindowCount);
                Assert.All(chrome.Windows, window =>
                {
                    Assert.False(window.IsVisible);
                    // ShowInTaskbar=false may substitute WPF's hidden helper owner for zero.
                    Assert.NotEqual(owner.Handle, new WindowInteropHelper(window).Owner);
                });
                var pixels = new byte[15 * 15 * 4];
                Assert.IsType<WriteableBitmap>(image.Source).CopyPixels(pixels, 60, 0);
                Assert.All(pixels, pixel => Assert.Equal((byte)0, pixel));
                owner.Close();
                Assert.Null(owner.Frame);
                Assert.All(current, hwnd => Assert.True(NativeSelectorInterop.IsWindow(hwnd)));
                previous = chrome;
                handles = current;
            }
            NativeSelectorChromeCache.Trim();
            Assert.Equal(0, NativeSelectorChromeCache.RetainedWindowCount);
            Assert.All(handles!, hwnd => Assert.False(NativeSelectorInterop.IsWindow(hwnd)));
        }
        finally { NativeSelectorChromeCache.Trim(); }
    });

    [Fact]
    public void UnrenderedLeaseIsDestroyedRatherThanReused() => StaTestHost.Run(() =>
    {
        using var owner = Owner();
        var chrome = NativeSelectorChromeCache.Acquire(owner, false, false);
        IntPtr[] handles = Handles(chrome);
        NativeSelectorChromeCache.Release(chrome);
        Assert.Equal(0, NativeSelectorChromeCache.RetainedWindowCount);
        Assert.All(handles, hwnd => Assert.False(NativeSelectorInterop.IsWindow(hwnd)));
    });

    [Fact]
    public void DispatcherShutdownReleasesTheCache()
    {
        IntPtr[] handles = [];
        StaTestHost.Run(() =>
        {
            using var owner = Owner();
            var chrome = NativeSelectorChromeCache.Acquire(owner, false, false);
            chrome.Show();
            PumpUntil(() => owner.IsChromeReady);
            handles = Handles(chrome);
            NativeSelectorChromeCache.Release(chrome);
            Assert.All(handles, hwnd => Assert.True(NativeSelectorInterop.IsWindow(hwnd)));
        });
        Assert.NotEmpty(handles);
        Assert.All(handles, hwnd => Assert.False(NativeSelectorInterop.IsWindow(hwnd)));
    }

    [Fact]
    public void CachedChromeDoesNotKeepItsPreviousSessionOrFrameAlive() => StaTestHost.Run(() =>
    {
        try
        {
            var references = CacheCompletedSession();
            Pump();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.Equal(4, NativeSelectorChromeCache.RetainedWindowCount);
            Assert.False(references.Owner.IsAlive);
            Assert.False(references.Frame.IsAlive);
        }
        finally { NativeSelectorChromeCache.Trim(); }
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Owner, WeakReference Frame) CacheCompletedSession()
    {
        using var owner = Owner();
        var chrome = NativeSelectorChromeCache.Acquire(owner, true, false);
        chrome.Show();
        PumpUntil(() => owner.IsChromeReady);
        NativeSelectorChromeCache.Release(chrome);
        return (new WeakReference(owner), new WeakReference(owner.Frame));
    }

    [Fact]
    public void ParallelChromeFailureJoinsPixelFillBeforeTheOwnerCanDispose() => StaTestHost.Run(() =>
    {
        var bitmap = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgr32, null, new byte[640 * 360 * 4], 640 * 4);
        bitmap.Freeze();
        using var owner = new NativeCaptureOverlay(Bounds, false, false)
        {
            ChromeStrategy = NativeSelectorChromeStrategy.Parallel,
            ReadMonitors = () => throw new InvalidOperationException("Test chrome failure"),
        };
        Assert.Throws<InvalidOperationException>(() => owner.AttachFrame(new FrozenFrame(bitmap, Bounds, null, 0)));
        NativeSelectorDib dib = owner.DisplayDib!;
        Assert.Equal(640L * 360, dib.PixelsUpdated);
        owner.Close();
        Assert.True(dib.IsDisposed);
        Assert.Equal(0, owner.LiveOwnedNativeHandles);
    });

    [Fact]
    public void ReboundBarOnlyChangesTheCurrentSession() => StaTestHost.Run(() =>
    {
        try
        {
            using var first = Owner();
            first.TextCaptureAvailable = true;
            var chrome = NativeSelectorChromeCache.Acquire(first, false, false);
            chrome.Show();
            PumpUntil(() => first.IsChromeReady);
            CaptureModeBar bar = first.ModeBar;
            NativeSelectorChromeCache.Release(chrome);
            using var second = Owner();
            second.TextCaptureAvailable = true;
            Assert.Same(chrome, NativeSelectorChromeCache.Acquire(second, false, false));
            Assert.Same(bar, second.ModeBar);
            bar.TextButton.IsChecked = true;
            Assert.Equal(CaptureSelectorMode.Text, second.Mode);
            Assert.Equal(CaptureSelectorMode.Capture, first.Mode);
            chrome.Dispose();
        }
        finally { NativeSelectorChromeCache.Trim(); }
    });

    [Theory]
    [InlineData("monitor")]
    [InlineData("magnifier")]
    [InlineData("language")]
    public void IncompatibleLeaseRebuildsChrome(string difference) => StaTestHost.Run(() =>
    {
        try
        {
            using var owner = Owner();
            var chrome = NativeSelectorChromeCache.Acquire(owner, true, false);
            chrome.Show();
            PumpUntil(() => owner.IsChromeReady);
            NativeSelectorChromeCache.Release(chrome);
            IntPtr[] handles = Handles(chrome);
            using var nextOwner = Owner();
            if (difference == "monitor")
                nextOwner.ReadMonitors = () => [new MonitorInfo("different", Bounds, new RectD(0, 0, 640, 320), 144, true)];
            using var culture = UiText.UseLanguage(difference == "language" ? (UiText.Culture.Name == "en" ? "ko-KR" : "en") : UiText.Culture.Name);
            var next = NativeSelectorChromeCache.Acquire(nextOwner, difference != "magnifier", false);
            try
            {
                Assert.NotSame(chrome, next);
                Assert.All(handles, hwnd => Assert.False(NativeSelectorInterop.IsWindow(hwnd)));
            }
            finally { next.Dispose(); }
        }
        finally { NativeSelectorChromeCache.Trim(); }
    });

    [Fact]
    public void HiddenDisplayBroadcastReleasesRetainedWindows() => StaTestHost.Run(() =>
    {
        using var owner = Owner();
        var chrome = NativeSelectorChromeCache.Acquire(owner, false, false);
        chrome.Show();
        PumpUntil(() => owner.IsChromeReady);
        NativeSelectorChromeCache.Release(chrome);
        IntPtr[] handles = Handles(chrome);
        _ = SendMessage(handles[0], 0x007E, IntPtr.Zero, IntPtr.Zero);
        PumpUntil(() => NativeSelectorChromeCache.RetainedWindowCount == 0);
        Assert.All(handles, hwnd => Assert.False(NativeSelectorInterop.IsWindow(hwnd)));
        Assert.False(owner.IsClosed); // A detached window never calls a former session.
    });

    [Fact]
    public void ParallelPreparationFinishesBeforeAttachReturnsAndCloseReleasesEverything() => StaTestHost.Run(() =>
    {
        using var owner = Owner(NativeSelectorChromeStrategy.Parallel);
        Assert.NotNull(owner.Chrome);
        Assert.False(owner.IsChromeReady);
        Assert.False(owner.IsReady);
        Assert.False(owner.IsVisible);
        Assert.Equal(1, owner.DibAllocations);
        Assert.Equal(640L * 360, owner.DisplayDib!.PixelsUpdated);
        IntPtr[] handles = Handles(owner.Chrome!);
        owner.Close();
        Assert.Equal(0, owner.LiveOwnedNativeHandles);
        Assert.All(handles, hwnd => Assert.False(NativeSelectorInterop.IsWindow(hwnd)));
    });

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void DibChromeUsesOnlyInteractiveAndMagnifierWindows(bool magnifier, int count) => StaTestHost.Run(() =>
    {
        using var owner = Owner(NativeSelectorChromeStrategy.Dib);
        using var chrome = new NativeSelectorChrome(owner, magnifier, false);
        Assert.Equal(count, chrome.Windows.Count);
        Assert.False(chrome.CanRetain);
        chrome.Show();
        PumpUntil(() => owner.IsChromeReady);
        Assert.True(owner.DisplayDib!.PixelsUpdated > 640L * 360);
    });

    [Theory]
    [InlineData("selector-chrome-fresh")]
    [InlineData("selector-chrome-parallel")]
    [InlineData("selector-chrome-dib")]
    public void ChromeVariantsRequireNativeAndOneStrategy(string strategy)
    {
        Assert.Throws<ArgumentException>(() => ResidentProbeOptions.ReadVariants(["--resident-variant", strategy]));
        var selected = ResidentProbeOptions.ReadVariants(["--resident-variant", "selector-native-dib," + strategy]);
        Assert.Equal(2, selected.Count);
        Assert.Contains(strategy, selected);
        string other = strategy == "selector-chrome-fresh" ? "selector-chrome-dib" : "selector-chrome-fresh";
        Assert.Throws<ArgumentException>(() => ResidentProbeOptions.ReadVariants(["--resident-variant", "selector-native-dib," + strategy + "," + other]));
    }

    [Fact]
    public void PointerFollowingChromeDoesNotRestackForAnUnchangedPointer() => StaTestHost.Run(() =>
    {
        // Restacking a topmost window makes Windows synthesize WM_MOUSEMOVE. When every mouse move
        // restacked the size chip and magnifier, a resting pointer fed the selector mouse moves, WPF's
        // input queue never emptied and ContentRendered (Input priority) never arrived on the desktop.
        using var owner = Owner();
        using var chrome = new NativeSelectorChrome(owner, true, false);
        chrome.Show();
        PumpUntil(() => owner.IsChromeReady);
        int positionChanges = 0;
        HwndSourceHook hook = (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message == 0x0046) positionChanges++; // WM_WINDOWPOSCHANGING
            return IntPtr.Zero;
        };
        HwndSource[] sources = Handles(chrome).Select(handle => HwndSource.FromHwnd(handle)!).ToArray();
        foreach (HwndSource source in sources) source.AddHook(hook);
        try
        {
            for (int i = 0; i < 20; i++) chrome.Update(new PointD(100, 120), Int32Rect.Empty);
            Assert.Equal(0, positionChanges);
            chrome.Update(new PointD(160, 180), Int32Rect.Empty);
            Assert.True(positionChanges > 0, "The size chip and magnifier still follow a moving pointer.");
        }
        finally
        {
            foreach (HwndSource source in sources) source.RemoveHook(hook);
        }
    });

    private static NativeCaptureOverlay Owner(NativeSelectorChromeStrategy strategy = NativeSelectorChromeStrategy.Retained)
    {
        BitmapSource bitmap = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgr32, null,
            Enumerable.Repeat((byte)100, 640 * 360 * 4).ToArray(), 640 * 4);
        bitmap.Freeze();
        var owner = new NativeCaptureOverlay(Bounds, false, true)
        {
            ChromeStrategy = strategy,
            ReadCursor = () => new PointD(100, 120),
            ReadMonitors = () => [new MonitorInfo("test", Bounds, Bounds, 96, true)],
        };
        owner.AttachFrame(new FrozenFrame(bitmap, Bounds, null, 0));
        return owner;
    }

    private static IntPtr[] Handles(NativeSelectorChrome chrome) => chrome.Windows.Select(window => new WindowInteropHelper(window).Handle).ToArray();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
