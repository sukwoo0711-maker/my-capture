using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Recording;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingSelectorFallbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultIsRetainedNativeAndExplicitWpfNeverCreatesNative(bool wpf) => StaTestHost.Run(() =>
    {
        using var test = new Fixture();
        Assert.True(test.Coordinator.UseNativeDibSelector);
        Assert.Equal(NativeSelectorChromeStrategy.Retained, test.Coordinator.NativeChromeStrategy);
        if (wpf) test.Coordinator.UseNativeDibSelector = false;
        test.StartAndWait();
        var selector = new RecordingSelector(test.Coordinator, native: !wpf);
        Assert.True(selector.IsVisible);
        Assert.Equal(wpf ? 0 : 1, test.NativeCreates);
        Assert.Equal(wpf ? 1 : 0, test.WpfCreates);
        Assert.Empty(test.Log.Warnings);
    });

    [Theory]
    [InlineData("create")]
    [InlineData("prepare")]
    [InlineData("show")]
    [InlineData("input")]
    [InlineData("chrome")]
    public void NativeFailureKeepsTheFramePresetAndCompletion(string phase) => StaTestHost.Run(() =>
    {
        using var test = new Fixture();
        test.FailNative(phase);
        test.Coordinator.Toggle();
        // A fallback belongs to the original capture even if settings change while it prepares.
        test.Settings.PresetWidth = 30;
        test.Settings.PresetHeight = 30;
        test.Coordinator.SelectionDesktopBounds = () => new RectD(500, 500, 64, 48);
        test.WaitForWpf();
        var selector = new RecordingSelector(test.Coordinator, native: false);
        Assert.True(selector.IsVisible);
        Assert.True(selector.RecordingSelection);
        Assert.True(selector.OffersOtherActions);
        Assert.Equal(new System.Drawing.Size(18, 12), selector.PresetSize);
        Assert.Equal(1, test.Acquisitions);
        Assert.Equal(1, test.NativeCreates);
        Assert.Equal(1, test.WpfCreates);
        Assert.Single(test.Log.Warnings);
        Assert.Empty(test.Failures);
        Assert.Empty(test.Log.Errors);
        Assert.Equal(0, test.Ended);
        if (test.Native is { } old) { Assert.True(old.IsClosed); Assert.Equal(IntPtr.Zero, old.Handle); }
        selector.CompleteSelection(new RectD(3, 2, 18, 12));
        Assert.Equal(new RectD(103, 102, 18, 12), Assert.Single(test.Regions));
        Assert.False(test.Coordinator.IsActive);
        Assert.Equal(1, test.Ended);
        Assert.Equal(1, test.Acquisitions);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FallbackKeepsCancellationAndTheCaptureSwitch(bool switchToCapture) => StaTestHost.Run(() =>
    {
        using var test = new Fixture();
        test.FailNative("show");
        test.Coordinator.Toggle();
        test.WaitForWpf();
        var selector = new RecordingSelector(test.Coordinator, native: false);
        if (switchToCapture)
        {
            selector.ModeBar.CaptureKindButton.IsChecked = true;
            PinHistoryTestSupport.Await(test.Coordinator.LastSelectorActionForTest);
            Assert.False(Assert.Single(test.CaptureRequests));
        }
        else
        {
            test.Coordinator.CancelRegionSelection();
            Assert.Empty(test.CaptureRequests);
        }
        Assert.False(selector.IsVisible);
        Assert.False(test.Coordinator.IsActive);
        Assert.Empty(test.Regions);
        Assert.Empty(test.Failures);
        Assert.Single(test.Log.Warnings);
        Assert.Equal(1, test.Ended);
        Assert.Equal(1, test.Acquisitions);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UserCancellationNeverFallsBack(bool afterShown) => StaTestHost.Run(() =>
    {
        using var test = new Fixture();
        test.Coordinator.Toggle();
        if (afterShown) PinHistoryTestSupport.Await(test.Coordinator.LastSelectionPreparation);
        test.Coordinator.CancelRegionSelection();
        PinHistoryTestSupport.Await(test.Coordinator.LastSelectionPreparation);
        Assert.False(test.Coordinator.IsActive);
        Assert.Equal(0, test.WpfCreates);
        Assert.Empty(test.Log.Warnings);
        Assert.Empty(test.Failures);
        Assert.Equal(1, test.Ended);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellingWhileFallbackPreparesNeverShowsItOrReportsAnotherFailure(bool throwAfterCancel) => StaTestHost.Run(() =>
    {
        using var test = new Fixture();
        test.FailNative("input");
        var exclude = test.Coordinator.ExcludeSelectionWindow;
        CaptureOverlayWindow? fallback = null;
        test.Coordinator.ExcludeSelectionWindow = window =>
        {
            fallback = Assert.IsType<CaptureOverlayWindow>(window);
            Assert.False(window.IsVisible);
            bool excluded = exclude(window);
            test.Coordinator.CancelRegionSelection();
            if (throwAfterCancel) throw new InvalidOperationException("Synthetic callback failure after cancellation");
            return excluded;
        };
        test.StartAndWait();
        WindowDetectOverlayTests.PumpUntil(() => !test.Coordinator.IsActive);
        Assert.NotNull(fallback);
        Assert.False(fallback.IsVisible);
        Assert.Null(RecordingSelector.Current(test.Coordinator));
        Assert.Equal(1, test.WpfCreates);
        Assert.Equal(1, test.Acquisitions);
        Assert.Equal(1, test.Ended);
        Assert.Single(test.Log.Warnings);
        Assert.Empty(test.Log.Errors);
        Assert.Empty(test.Failures);
        Assert.Empty(test.Regions);
    });

    [Theory]
    [InlineData("create")]
    [InlineData("show")]
    public void AFailedFallbackReportsOnceAndTheNextSelectionCanOpen(string phase) => StaTestHost.Run(() =>
    {
        using var test = new Fixture();
        test.FailNative(phase);
        test.Coordinator.ExcludeSelectionWindow = _ => throw new InvalidOperationException("Synthetic WPF failure");
        test.Coordinator.Toggle();
        PinHistoryTestSupport.Await(test.Coordinator.LastSelectionPreparation);
        Assert.Equal("Synthetic WPF failure", Assert.Single(test.Failures).Message);
        Assert.Single(test.Log.Warnings);
        Assert.Single(test.Log.Errors);
        Assert.False(test.Coordinator.IsActive);
        Assert.Equal(1, test.Ended);
        test.NativeFault = null;
        test.Coordinator.ShowNativeSelector = native => native.Show();
        test.StartAndWait();
        Assert.True(new RecordingSelector(test.Coordinator, native: true).IsVisible);
        test.Coordinator.CancelRegionSelection();
        Assert.Equal(2, test.Ended);
        Assert.Single(test.Log.Warnings);
        Assert.Single(test.Failures);
    });

    [Fact]
    public void DisplayChangeStillFailsBothRenderersWithoutRecapturing() => StaTestHost.Run(() =>
    {
        using var test = new Fixture();
        test.Coordinator.SelectionDesktopBounds = () => new RectD(99, 100, 64, 48);
        test.StartAndWait();
        Assert.Single(test.Failures);
        Assert.Single(test.Log.Warnings);
        Assert.Single(test.Log.Errors);
        Assert.Equal(1, test.Acquisitions);
        Assert.Equal(1, test.Ended);
        Assert.False(test.Coordinator.IsActive);
    });

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mc-record-fallback-" + Guid.NewGuid().ToString("N"));
        private readonly RectD _bounds = new(100, 100, 64, 48);
        private readonly MonitorInfo _monitor;
        internal Fixture()
        {
            _monitor = new MonitorInfo("recording-test", _bounds, _bounds, 96, true);
            BitmapSource bitmap = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, new byte[64 * 48 * 4], 64 * 4);
            bitmap.Freeze();
            var frame = new FrozenFrame(bitmap, _bounds, _monitor, 0);
            AppPaths paths = AppPaths.CreateForRoot(_root);
            var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
            Coordinator = new RegionRecordingCoordinator(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
                paths, () => Settings, Log, library)
            {
                AcquireSelectionFrame = () => { Interlocked.Increment(ref Acquisitions); return frame; },
                SelectionDesktopBounds = () => _bounds,
                ShowSelectionGrid = () => false,
                OpenControlsForTest = Regions.Add,
            };
            Coordinator.CreateNativeSelector = bounds =>
            {
                NativeCreates++;
                if (NativeFault == "create") throw Failure();
                return Native = new NativeCaptureOverlay(bounds, abortOnFocusLoss: false, showMagnifier: true);
            };
            Coordinator.ExcludeNativeSelectionWindow = _ =>
            {
                NativeCaptureOverlay native = Assert.IsType<NativeCaptureOverlay>(Coordinator.NativeOverlayForProbe);
                native.ReadCursor = () => new PointD(110, 110);
                native.ReadMonitors = () => [_monitor];
                if (NativeFault == "prepare") throw Failure();
                if (NativeFault == "input") native.InputReady += (_, _) => throw Failure();
                if (NativeFault == "chrome") native.InputReady += (_, _) => native.ReadMonitors = () => throw Failure();
                return true;
            };
            Coordinator.ExcludeSelectionWindow = window =>
            {
                WpfCreates++;
                var view = Assert.IsType<CaptureOverlayView>(window.Content);
                view.ReadCursor = () => new PointD(110, 110);
                view.ReadMonitors = () => [_monitor];
                return true;
            };
            Coordinator.CaptureSelectionRequested = () => CaptureRequests.Add(Coordinator.IsActive);
            Coordinator.SelectionPreparationFailed += Failures.Add;
            Coordinator.SessionEnded += (_, _) => Ended++;
            Assert.False(Directory.Exists(_root));
        }

        internal RegionRecordingCoordinator Coordinator { get; }
        internal RecordingSettings Settings { get; } = new() { PresetWidth = 18, PresetHeight = 12 };
        internal RecordingLog Log { get; } = new();
        internal NativeCaptureOverlay? Native { get; private set; }
        internal string? NativeFault { get; set; }
        internal int Acquisitions, NativeCreates, WpfCreates, Ended;
        internal List<RectD> Regions { get; } = [];
        internal List<bool> CaptureRequests { get; } = [];
        internal List<Exception> Failures { get; } = [];
        private static Exception Failure() => new InvalidOperationException("Synthetic native selector failure");

        internal void FailNative(string phase)
        {
            NativeFault = phase;
            if (phase == "show") Coordinator.ShowNativeSelector = native =>
            {
                native.Show();
                native.Close(); // Native Show itself closes before throwing on a failed first paint.
                throw Failure();
            };
        }
        internal void StartAndWait() { Coordinator.Toggle(); PinHistoryTestSupport.Await(Coordinator.LastSelectionPreparation); }
        internal void WaitForWpf()
        {
            PinHistoryTestSupport.Await(Coordinator.LastSelectionPreparation);
            WindowDetectOverlayTests.PumpUntil(() => RecordingSelector.Current(Coordinator) is CaptureOverlayWindow { IsVisible: true });
        }
        public void Dispose()
        {
            Coordinator.CancelRegionSelection();
            PinHistoryTestSupport.Await(Coordinator.LastSelectionPreparation);
            Assert.False(Directory.Exists(_root));
        }
    }

    private sealed class RecordingLog : ILoggerFactory, ILogger
    {
        internal List<string> Warnings { get; } = [];
        internal List<string> Errors { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
            if (logLevel == LogLevel.Error) Errors.Add(formatter(state, exception));
        }
    }
}
