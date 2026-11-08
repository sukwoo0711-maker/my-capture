using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureSelectorFallbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultIsRetainedNativeAndTheWpfVariantNeverCreatesNative(bool wpf) => StaTestHost.Run(() =>
    {
        using var test = new Fixture();
        Assert.True(test.Coordinator.UseNativeDibSelector);
        Assert.Equal(NativeSelectorChromeStrategy.Retained, test.Coordinator.NativeChromeStrategy);
        if (wpf) test.Coordinator.UseNativeDibSelector = ResidentProbeOptions.UsesNativeSelector(
            ResidentProbeOptions.ReadVariants(["--resident-variant", "selector-wpf"]));
        test.Start();
        test.WaitForReady();
        Assert.True(new CoordinatorSelector(test.Coordinator, native: !wpf).IsVisible);
        Assert.Equal(wpf ? 0 : 1, test.NativeCreates);
        Assert.Equal(wpf ? 1 : 0, test.WpfCreates);
        Assert.Equal(1, test.Acquisitions);
        Assert.Empty(test.Log.Warnings);
        Assert.Empty(test.Failures);
    });

    [Theory]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("prepare", false)]
    [InlineData("prepare", true)]
    [InlineData("prepare-frame", false)]
    [InlineData("prepare-frame", true)]
    [InlineData("show", false)]
    [InlineData("show", true)]
    [InlineData("input", false)]
    [InlineData("input", true)]
    [InlineData("chrome", false)]
    [InlineData("chrome", true)]
    public void NativeFailurePreservesTheFrameModeAndPracticeRequest(string phase, bool text) => StaTestHost.Run(() =>
    {
        using var test = new Fixture();
        test.FailNative(phase);
        test.Start(text);
        test.WaitForWpf();
        test.WaitForReady();
        var selector = new CoordinatorSelector(test.Coordinator, native: false);
        Assert.True(selector.IsVisible);
        Assert.Equal(text, selector.TextCaptureMode);
        Assert.Equal(1, test.NativeCreates);
        Assert.Equal(1, test.WpfCreates);
        Assert.Equal(1, test.Acquisitions);
        Assert.Single(test.Log.Warnings);
        Assert.Empty(test.Log.Errors);
        Assert.Empty(test.Failures);
        Assert.Equal(0, test.Ended);
        Assert.True(test.Coordinator.OwnsPracticeCapture(test.Target));
        Assert.DoesNotContain(test.Signals, signal => signal.Kind == PracticeEventKind.CaptureCancelled);
        Assert.Single(test.Signals, signal => signal.Kind == PracticeEventKind.SelectorReady);
        test.AssertNativeReleased();

        var region = new RectD(4, 5, 30, 20);
        selector.CompleteSelection(region);
        PinHistoryTestSupport.Await(test.Coordinator.LastTransitionForTest);
        CaptureSelectionCompletedEventArgs result = Assert.Single(text ? test.Texts : test.Images);
        Assert.Empty(text ? test.Images : test.Texts);
        Assert.Same(test.Frame, result.Frame);
        Assert.Equal(region, result.BitmapRegion);
        Assert.Equal(test.Target, result.PracticeTarget);
        Assert.Equal(text, result.TextCapture);
        Assert.Equal(1, test.Acquisitions);
        Assert.False(test.Coordinator.IsActive);
        Assert.False(test.Coordinator.OwnsPracticeCapture(test.Target));
        Assert.Equal(1, test.Ended);
        Assert.Equal(text ? 1 : 0, test.Signals.Count(signal => signal.Kind == PracticeEventKind.CaptureCancelled));
    });

    [Theory]
    [InlineData("create")]
    [InlineData("show")]
    [InlineData("input")]
    public void CancellingTheReplacementEndsTheOriginalPracticeRequest(string phase) => StaTestHost.Run(() =>
    {
        using var test = new Fixture();
        test.FailNative(phase);
        test.Start();
        test.WaitForWpf();
        test.WaitForReady();
        test.Coordinator.Cancel();
        Drain();
        Assert.False(test.Coordinator.IsActive);
        Assert.False(test.Coordinator.OwnsPracticeCapture(test.Target));
        Assert.Single(test.Signals, signal => signal.Kind == PracticeEventKind.SelectorReady);
        Assert.Single(test.Signals, signal => signal.Kind == PracticeEventKind.CaptureCancelled);
        Assert.Single(test.Log.Warnings);
        Assert.Empty(test.Failures);
        Assert.Empty(test.Images);
        Assert.Empty(test.Texts);
        Assert.Equal(1, test.Acquisitions);
        Assert.Equal(1, test.Ended);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UserCancellationNeverStartsTheFallback(bool afterShown) => StaTestHost.Run(() =>
    {
        using var test = new Fixture();
        test.Start(practice: false);
        if (afterShown) PinHistoryTestSupport.Await(test.Coordinator.LastPreparationForTest);
        test.Coordinator.Cancel();
        PinHistoryTestSupport.Await(test.Coordinator.LastPreparationForTest);
        Drain();
        Assert.False(test.Coordinator.IsActive);
        Assert.Equal(0, test.WpfCreates);
        Assert.Empty(test.Log.Warnings);
        Assert.Empty(test.Failures);
        Assert.Empty(test.Images);
        Assert.Empty(test.Texts);
        Assert.Equal(1, test.Ended);
        test.AssertNativeReleased();
    });

    [Theory]
    [InlineData("create")]
    [InlineData("prepare")]
    [InlineData("show")]
    [InlineData("input")]
    [InlineData("chrome")]
    public void FailureOfBothRenderersReleasesTheSessionAndAllowsRetry(string phase) => StaTestHost.Run(() =>
    {
        using var test = new Fixture();
        test.FailNative(phase);
        test.WpfFault = true;
        Exception? synchronousFailure = Record.Exception(() => test.Start());
        PinHistoryTestSupport.Await(test.Coordinator.LastPreparationForTest);
        if (phase is "create" or "prepare")
            Assert.Equal("Synthetic WPF selector failure", Assert.IsType<InvalidOperationException>(synchronousFailure).Message);
        else
        {
            Assert.Null(synchronousFailure);
            WindowDetectOverlayTests.PumpUntil(() => test.Failures.Count != 0);
            Assert.Equal("Synthetic WPF selector failure", Assert.Single(test.Failures).Message);
            Assert.Single(test.Log.Errors);
        }
        Assert.False(test.Coordinator.IsActive);
        Assert.Null(test.Coordinator.NativeOverlayForProbe);
        Assert.False(test.Wpf!.IsVisible);
        Assert.Single(test.Log.Warnings);
        Assert.False(test.Coordinator.OwnsPracticeCapture(test.Target));
        Assert.Single(test.Signals, signal => signal.Kind == PracticeEventKind.CaptureCancelled);
        Assert.Equal(1, test.Ended);
        test.AssertNativeReleased();

        test.NativeFault = null;
        test.WpfFault = false;
        test.Coordinator.ShowNativeSelector = native => native.Show();
        test.NewPracticeRequest();
        test.Start();
        test.WaitForReady();
        Assert.True(new CoordinatorSelector(test.Coordinator, native: true).IsVisible);
        Assert.Equal(2, test.NativeCreates);
        Assert.Equal(1, test.WpfCreates);
        test.Coordinator.Cancel();
        Assert.False(test.Coordinator.IsActive);
        Assert.False(test.Coordinator.OwnsPracticeCapture(test.Target));
        Assert.Single(test.Log.Warnings);
        Assert.Single(test.Signals, signal => signal.Kind == PracticeEventKind.CaptureCancelled);
        Assert.Equal(2, test.Ended);
    });

    [Theory]
    [InlineData("show")]
    [InlineData("input")]
    public void UserCancellationInsideAFailingCallbackDoesNotReopenASelector(string phase) => StaTestHost.Run(() =>
    {
        using var test = new Fixture();
        if (phase == "show") test.Coordinator.ShowNativeSelector = native =>
        {
            native.Show();
            test.Coordinator.Cancel();
            throw new InvalidOperationException("Synthetic failure after user cancellation");
        };
        else test.NativeFault = "cancel-input";
        test.Start();
        PinHistoryTestSupport.Await(test.Coordinator.LastPreparationForTest);
        WindowDetectOverlayTests.PumpUntil(() => !test.Coordinator.IsActive);
        Drain();
        Assert.Equal(0, test.WpfCreates);
        Assert.Empty(test.Log.Warnings);
        Assert.Empty(test.Failures);
        Assert.Single(test.Signals, signal => signal.Kind == PracticeEventKind.CaptureCancelled);
        Assert.Equal(1, test.Ended);
        Assert.False(test.Coordinator.OwnsPracticeCapture(test.Target));
        test.AssertNativeReleased();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellingWhileFallbackPreparesNeverShowsItOrReportsAnotherFailure(bool throwAfterCancel) => StaTestHost.Run(() =>
    {
        using var test = new Fixture();
        test.FailNative("input");
        var exclude = test.Coordinator.ApplyCaptureExclusion;
        test.Coordinator.ApplyCaptureExclusion = window =>
        {
            Assert.False(window.IsVisible);
            bool excluded = exclude(window);
            test.Coordinator.Cancel();
            if (throwAfterCancel) throw new InvalidOperationException("Synthetic callback failure after cancellation");
            return excluded;
        };
        test.Start();
        PinHistoryTestSupport.Await(test.Coordinator.LastPreparationForTest);
        WindowDetectOverlayTests.PumpUntil(() => !test.Coordinator.IsActive);
        Drain();
        Assert.NotNull(test.Wpf);
        Assert.False(test.Wpf.IsVisible);
        Assert.Null(test.Coordinator.NativeOverlayForProbe);
        Assert.Equal(1, test.WpfCreates);
        Assert.Equal(1, test.Acquisitions);
        Assert.Single(test.Log.Warnings);
        Assert.Empty(test.Log.Errors);
        Assert.Empty(test.Failures);
        Assert.Empty(test.Images);
        Assert.Empty(test.Texts);
        Assert.Single(test.Signals, signal => signal.Kind == PracticeEventKind.SelectorReady);
        Assert.Single(test.Signals, signal => signal.Kind == PracticeEventKind.CaptureCancelled);
        Assert.Equal(1, test.Ended);
        Assert.False(test.Coordinator.OwnsPracticeCapture(test.Target));
        test.AssertNativeReleased();
    });

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private sealed class Fixture : IDisposable
    {
        private readonly RectD _bounds = new(100, 100, 80, 60);
        private readonly MonitorInfo _monitor;
        private NativeCaptureOverlay? _configuredNative;
        internal Fixture()
        {
            _monitor = new MonitorInfo("capture-fallback-test", _bounds, _bounds, 96, true);
            BitmapSource bitmap = BitmapSource.Create(80, 60, 96, 96, PixelFormats.Bgra32, null, new byte[80 * 60 * 4], 80 * 4);
            bitmap.Freeze();
            Frame = new FrozenFrame(bitmap, _bounds, _monitor, 0);
            Coordinator = new CaptureOverlayCoordinator(
                new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
                new WindowCandidateService(NullLogger<WindowCandidateService>.Instance), Log,
                _ => { Interlocked.Increment(ref Acquisitions); return Frame; }, () => _bounds, _ => [])
            {
                OpenEditorAfterCapture = () => false,
                AutoDetectWindows = () => false,
                ReadWindowSources = null,
                ReadPageUrls = _ => Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
                    new Dictionary<IntPtr, BrowserDocumentSnapshot>()),
                SelectionPersistRequested = selection => { Images.Add(selection); return Task.CompletedTask; },
                TextCaptureRequested = Texts.Add,
            };
            Coordinator.CreateNativeSelector = (bounds, _, magnifier) =>
            {
                NativeCreates++;
                if (NativeFault == "create") throw NativeFailure();
                Native = new NativeCaptureOverlay(bounds, abortOnFocusLoss: false, showMagnifier: magnifier)
                {
                    ReadCursor = () => new PointD(110, 110),
                    ReadMonitors = () => [_monitor],
                };
                return Native;
            };
            Coordinator.ApplyNativeCaptureExclusion = _ =>
            {
                NativePreparations++;
                if (NativeFault == "prepare" || NativeFault == "prepare-frame" && NativePreparations == 2)
                    throw NativeFailure();
                NativeCaptureOverlay native = Assert.IsType<NativeCaptureOverlay>(Coordinator.NativeOverlayForProbe);
                if (!ReferenceEquals(native, _configuredNative))
                {
                    _configuredNative = native;
                    if (NativeFault == "input") native.InputReady += (_, _) => throw NativeFailure();
                    if (NativeFault == "chrome") native.InputReady += (_, _) => native.ReadMonitors = () => throw NativeFailure();
                    if (NativeFault == "cancel-input") native.InputReady += (_, _) =>
                    {
                        Coordinator.Cancel();
                        throw NativeFailure();
                    };
                }
                return true;
            };
            Coordinator.ApplyCaptureExclusion = window =>
            {
                if (!ReferenceEquals(Wpf, window)) WpfCreates++;
                Wpf = Assert.IsType<CaptureOverlayWindow>(window);
                var view = Assert.IsType<CaptureOverlayView>(window.Content);
                view.ReadCursor = () => new PointD(110, 110);
                view.ReadMonitors = () => [_monitor];
                if (WpfFault) throw new InvalidOperationException("Synthetic WPF selector failure");
                return true;
            };
            Coordinator.TransitionFailed += Failures.Add;
            Coordinator.OverlayClosed += (_, _) => Ended++;
            PracticeSignals.Result += OnPractice;
        }

        internal CaptureOverlayCoordinator Coordinator { get; }
        internal FrozenFrame Frame { get; }
        internal CaptureLog Log { get; } = new();
        internal PracticeCaptureTarget Target { get; private set; } = new(Guid.NewGuid(), Guid.NewGuid());
        internal NativeCaptureOverlay? Native { get; private set; }
        internal CaptureOverlayWindow? Wpf { get; private set; }
        internal string? NativeFault { get; set; }
        internal bool WpfFault { get; set; }
        internal int Acquisitions, NativeCreates, NativePreparations, WpfCreates, Ended;
        internal List<CaptureSelectionCompletedEventArgs> Images { get; } = [];
        internal List<CaptureSelectionCompletedEventArgs> Texts { get; } = [];
        internal List<PracticeEvent> Signals { get; } = [];
        internal List<Exception> Failures { get; } = [];
        private static Exception NativeFailure() => new InvalidOperationException("Synthetic native selector failure");
        private void OnPractice(PracticeEvent signal)
        {
            if (signal.SessionId == Target.SessionId && signal.RequestId == Target.RequestId) Signals.Add(signal);
        }
        internal void NewPracticeRequest()
        {
            Target = new PracticeCaptureTarget(Guid.NewGuid(), Guid.NewGuid());
            Signals.Clear();
        }
        internal void Start(bool text = false, bool practice = true) =>
            Coordinator.Start(false, false, false, showSelectionGrid: false, textCapture: text,
                practiceTarget: practice ? Target : null);

        internal void FailNative(string phase)
        {
            NativeFault = phase;
            if (phase == "show") Coordinator.ShowNativeSelector = native =>
            {
                native.Show();
                native.Close(); // A failed first native paint closes its HWND before throwing.
                throw NativeFailure();
            };
        }

        internal void WaitForWpf()
        {
            PinHistoryTestSupport.Await(Coordinator.LastPreparationForTest);
            WindowDetectOverlayTests.PumpUntil(() => Wpf?.IsVisible == true);
        }
        internal void WaitForReady()
        {
            PinHistoryTestSupport.Await(Coordinator.LastPreparationForTest);
            WindowDetectOverlayTests.PumpUntil(() => Signals.Any(signal => signal.Kind == PracticeEventKind.SelectorReady));
            Drain();
        }
        internal void AssertNativeReleased()
        {
            if (Native is not { } native) return;
            Assert.True(native.IsClosed);
            Assert.Equal(IntPtr.Zero, native.Handle);
            Assert.Equal(0, native.LiveOwnedNativeHandles);
        }
        public void Dispose()
        {
            Coordinator.Cancel();
            PinHistoryTestSupport.Await(Coordinator.LastPreparationForTest);
            Coordinator.Dispose();
            PracticeSignals.Result -= OnPractice;
        }
    }

    private sealed class CaptureLog : ILogger<CaptureOverlayCoordinator>
    {
        internal List<string> Warnings { get; } = [];
        internal List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
            if (logLevel == LogLevel.Error) Errors.Add(formatter(state, exception));
        }
    }
}
