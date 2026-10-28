using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Microsoft.Extensions.DependencyInjection;

namespace MyCapture.App;

/// <summary>
/// The resident probe's script (<see cref="ResidentProbeOptions"/>): the real resident app, its own
/// command handlers and windows, with synthetic desktop frames, an isolated data root and no
/// clipboard, shortcut, Run-key or update side effects.
/// </summary>
public partial class App
{
    private static ResidentProbeOptions? s_residentProbe;

    private static readonly FieldInfo ProbeActiveOverlay =
        typeof(CaptureOverlayCoordinator).GetField("_activeOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo ProbeActiveEditor =
        typeof(CaptureOverlayCoordinator).GetField("_activeEditor", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Dictionary<string, int> _probeBreakdownIndex = [];
    private readonly Dictionary<string, int> _probeReceiptIndex = [];

    private static readonly PropertyInfo? ProbeOperationName =
        typeof(DispatcherOperation).GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly Type? ProbeMediaContext =
        typeof(Visual).Assembly.GetType("System.Windows.Media.MediaContext");

    private static readonly MethodInfo? ProbeMediaContextFrom =
        ProbeMediaContext?.GetMethod("From", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, [typeof(Dispatcher)]);

    private static readonly MethodInfo? ProbeCompleteRender =
        ProbeMediaContext?.GetMethod("CompleteRender", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, Type.EmptyTypes);

    /// <summary>Reads the virtual desktop as the selector would, without its pixels.</summary>
    private static FrozenFrame AcquireProbeFrame(bool includeCursor)
    {
        long started = Stopwatch.GetTimestamp();
        FrozenFrame frame = ResidentProbeImages.DesktopFrame(MonitorEnumerator.GetVirtualDesktopBounds(), Environment.TickCount & 0xFF,
            s_residentProbe?.Has("frame-cached") == true);
        s_lastProbeAcquisitionMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return frame;
    }

    /// <summary>The synthetic grab's own time, part of every shortcut → selector timing.</summary>
    private static double s_lastProbeAcquisitionMs;

    /// <summary>
    /// Diagnostic variants of a deeper selector prewarm, run once at idle after launch:
    /// <c>prewarm-selector-hwnd</c> builds a hidden selector window and its HWND (never shown) and closes
    /// it. Reader prewarm is now the production default; the old <c>prewarm-readers</c> flag remains
    /// accepted for old probe scripts but does not run it a second time.
    /// </summary>
    private void RunProbePrewarmVariants(ResidentProbeOptions probe)
    {
        if (_overlay is null || _overlay.IsActive || _services is null) return;
        RectD desktop = MonitorEnumerator.GetVirtualDesktopBounds();
        if (probe.Has("prewarm-selector-hwnd"))
        {
            var window = new CaptureOverlayWindow(desktop, abortOnFocusLoss: false, showMagnifier: true);
            _ = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            window.Close();
        }
    }

    internal static void ApplyResidentProbeSettings(AppSettings settings)
    {
        settings.General.IsFirstRun = false;
        // An unanswered core-shortcut question would stay open for the whole run and change idle.
        settings.Hotkeys.CoreShortcutsAsked = true;
        settings.General.FirstCaptureTipShown = true;
        settings.General.AfterCapture = AfterCaptureAction.QuickAccess;
        settings.General.LaunchAtLogin = false;
        settings.General.PlayCaptureSound = false;
    }

    private async Task RunResidentProbeAsync(ResidentProbeOptions probe)
    {
        var recorder = new ResidentProbeRecorder(probe, Dispatcher);
        int exitCode = 0;
        PresentationDismissals.UseLegacySettleForProbe = probe.Has("legacy-capture-settle");
        CaptureFlight.UseLegacyBoundsForProbe = probe.Has("legacy-flight-bounds");
        // Diagnostic variant: no lifted capture flies into the card (CaptureFlight declines without layered windows).
        if (probe.Has("no-flight")) CaptureFlight.ReadLayeredSupport = () => false;
        // Diagnostic variants: chosen kinds of window render in software from their first frame.
        if (probe.Variants.Any(variant => variant.StartsWith("sw-", StringComparison.Ordinal)))
        {
            ResidentProbeWindowHook.SourceInitialized = window =>
            {
                bool software = window switch
                {
                    CaptureOverlayWindow => probe.Has("sw-selector"),
                    QuickAccessWindow => probe.Has("sw-card"),
                    CaptureFlightDecoration => probe.Has("sw-flight"),
                    AnnotationEditorWindow => probe.Has("sw-editor"),
                    _ => false,
                };
                if (software && System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(window).Handle)
                        is { CompositionTarget: { } target })
                    target.RenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            };
        }
        try
        {
            CaptureOverlayView.SuppressKeyPanelForProbe = probe.Has("no-key-panel");
            await Task.WhenAll(_initialLibraryRecoveryTask, _initialEditRecoveryTask);
            await recorder.SettleAsync("start", probe.StartSeconds);
            await recorder.AuditAsync("start");
            foreach (string scenario in probe.Scenarios)
            {
                try
                {
                    await RunResidentProbeScenarioAsync(probe, recorder, scenario);
                }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
                {
                    // One step that could not finish (a loaded desktop, a window that never showed) is
                    // reported, and the steps after it are still measured from a quiet app.
                    recorder.Fail(new InvalidOperationException($"Scenario {scenario} did not finish.", ex));
                    exitCode = 1;
                    _overlay?.Cancel();
                    _quickAccess?.CloseNow();
                    await Task.Delay(TimeSpan.FromSeconds(probe.Quick ? 2 : 10));
                }
            }
        }
        catch (Exception ex)
        {
            recorder.Fail(ex);
            exitCode = 1;
        }
        finally
        {
            CaptureOverlayView.SuppressKeyPanelForProbe = false;
        }

        try { recorder.Write(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { exitCode = 2; }
        recorder.Dispose();
        _pins?.CloseAll();
        Shutdown(exitCode);
    }

    private async Task RunResidentProbeScenarioAsync(ResidentProbeOptions probe, ResidentProbeRecorder recorder, string scenario)
    {
        switch (scenario)
        {
            case "capture":
                await ProbeCaptureToCardAsync(recorder, "capture", dwellMs: 1200);
                await recorder.SettleAsync("capture closed", probe.SettleSeconds);
                await recorder.AuditAsync("capture closed");
                break;
            case "editor-4k":
                await ProbeEditorAsync(recorder);
                await recorder.SettleAsync("editor-4k closed", probe.SettleSeconds);
                await recorder.AuditAsync("editor-4k closed");
                break;
            case "capture-30":
                for (int round = 1; round <= 30; round++)
                {
                    await ProbeCaptureToCardAsync(recorder, "capture-30", dwellMs: 400);
                    if (round % 10 == 0) recorder.Sample($"capture-30 after {round}", 0);
                }
                await recorder.SettleAsync("capture-30 closed", probe.SettleSeconds);
                await recorder.AuditAsync("capture-30 closed");
                break;
            case "library":
                await ProbeLibraryAsync(recorder);
                await recorder.SettleAsync("library closed", probe.SettleSeconds);
                await recorder.AuditAsync("library closed");
                break;
            case "ocr":
                await ProbeOcrAsync(recorder);
                await recorder.SettleAsync("ocr closed", probe.SettleSeconds);
                await recorder.AuditAsync("ocr closed");
                break;
            case "pins-10":
                await ProbePinsAsync(recorder);
                await recorder.SettleAsync("pins-10 closed", probe.SettleSeconds,
                    () => $"closed-pin history {(_pins?.ClosedRetainedBytes ?? 0) / 1048576.0:0.0} MB in {_pins?.ClosedCount ?? 0}");
                await recorder.AuditAsync("pins-10 closed");
                break;
            default:
                throw new ArgumentException("Unknown resident probe scenario: " + scenario);
        }
    }

    private CaptureOverlayWindow? ProbeOverlay => _overlay is null ? null : ProbeActiveOverlay.GetValue(_overlay) as CaptureOverlayWindow;

    private AnnotationEditorWindow? ProbeEditor => _overlay is null ? null : ProbeActiveEditor.GetValue(_overlay) as AnnotationEditorWindow;

    /// <summary>The capture shortcut, the selector's first frame, a region, the card, and the card closed.</summary>
    private async Task ProbeCaptureToCardAsync(ResidentProbeRecorder recorder, string name, int dwellMs)
    {
        double selectorMs = await ProbeShortcutToSelectorAsync(recorder, name);
        recorder.Time(name + ": shortcut → selector first frame", selectorMs);
        recorder.Time(name + ": synthetic desktop grab (inside the selector timing)", s_lastProbeAcquisitionMs);
        CaptureOverlayWindow overlay = ProbeOverlay ?? throw new InvalidOperationException("The selector did not open.");
        recorder.Track(name, "selector window", overlay);
        recorder.Track(name, "selector frame", (overlay.Content as CaptureOverlayView)?.Frame);

        long selected = 0;
        double cardMs = -1;
        List<ResidentProbeRecorder.Mark>? receiptMarks = s_residentProbe?.Has("receipt-breakdown") == true ? [] : null;
        int gen0 = 0, gen1 = 0, gen2 = 0;
        int collected0 = 0, collected1 = 0, collected2 = 0;
        Action<string>? previousReceiptMark = ReceiptTimeline.Mark;
        if (receiptMarks is not null)
        {
            gen0 = GC.CollectionCount(0);
            gen1 = GC.CollectionCount(1);
            gen2 = GC.CollectionCount(2);
            ReceiptTimeline.Mark = stage =>
            {
                if (cardMs >= 0) return;
                double at = Stopwatch.GetElapsedTime(selected).TotalMilliseconds;
                lock (receiptMarks) receiptMarks.Add(new ResidentProbeRecorder.Mark(stage, Math.Round(at, 2), Environment.CurrentManagedThreadId));
            };
        }
        selected = Stopwatch.GetTimestamp();
        EventHandler rendering = (_, _) =>
        {
            if (cardMs >= 0 || _quickAccess is not { IsVisible: true }) return;
            cardMs = Stopwatch.GetElapsedTime(selected).TotalMilliseconds;
            if (receiptMarks is not null)
            {
                collected0 = GC.CollectionCount(0) - gen0;
                collected1 = GC.CollectionCount(1) - gen1;
                collected2 = GC.CollectionCount(2) - gen2;
                lock (receiptMarks) receiptMarks.Add(new ResidentProbeRecorder.Mark(
                    "card-first-rendering", Math.Round(cardMs, 2), Environment.CurrentManagedThreadId));
            }
        };
        CompositionTarget.Rendering += rendering;
        try
        {
            overlay.CompleteSelection(new RectD(160, 120, 1280, 720));
            await ProbeWaitAsync(() => cardMs >= 0, 10_000, "card shown");
        }
        finally
        {
            CompositionTarget.Rendering -= rendering;
            if (receiptMarks is not null) ReceiptTimeline.Mark = previousReceiptMark;
        }
        recorder.Time(name + ": selection → card shown", cardMs);
        if (receiptMarks is not null)
        {
            int index = _probeReceiptIndex.TryGetValue(name, out int previous) ? previous + 1 : 1;
            _probeReceiptIndex[name] = index;
            lock (receiptMarks) recorder.AddReceiptBreakdown(new ResidentProbeRecorder.ReceiptBreakdown(
                name, index, Math.Round(cardMs, 2), collected0, collected1, collected2, receiptMarks.ToArray()));
        }
        recorder.Track(name, "card", _quickAccess);
        recorder.Track(name, "card image", _quickAccess?.PreviewForTest.Source);
        recorder.Track(name, "flight", _captureFlight);
        recorder.Track(name, "flight window", _captureFlight?.WindowForTest);

        await Task.Delay(dwellMs);
        _quickAccess?.RequestClose();
        await ProbeWaitAsync(() => _quickAccess is null && _overlay?.IsActive != true && _capturePersistenceTask.IsCompleted,
            10_000, "card closed");
    }

    /// <summary>
    /// The capture shortcut's handler until its first Rendering observation, matching the release
    /// baseline. The opt-in selector breakdown also waits for content and render-thread completion.
    /// </summary>
    private async Task<double> ProbeShortcutToSelectorAsync(ResidentProbeRecorder recorder, string name)
    {
        if (s_residentProbe?.Has("selector-breakdown") == true)
            return await ProbeShortcutToSelectorBreakdownAsync(recorder, name);

        // Match the release baseline: select on the first Rendering observation. Dispatcher
        // tracing, ContentRendered and CompleteRender change the phase at which selection
        // starts and therefore the subsequent selection-to-card measurement.
        long started = Stopwatch.GetTimestamp();
        double selectorMs = -1;
        EventHandler rendering = (_, _) =>
        {
            if (selectorMs < 0 && ProbeOverlay is { IsVisible: true, Content: CaptureOverlayView { Frame: not null } })
                selectorMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        };
        CompositionTarget.Rendering += rendering;
        try
        {
            HandleCaptureRequested();
            await ProbeWaitAsync(() => selectorMs >= 0, 10_000, "selector first frame");
        }
        finally { CompositionTarget.Rendering -= rendering; }
        return selectorMs;
    }

    private async Task<double> ProbeShortcutToSelectorBreakdownAsync(ResidentProbeRecorder recorder, string name)
    {
        long dismissed = _overlay?.LastPresentationDismissalTick ?? long.MinValue;
        double sinceDismissal = dismissed == long.MinValue ? -1 : Environment.TickCount64 - dismissed;
        var marks = new List<ResidentProbeRecorder.Mark>(32);
        long started = Stopwatch.GetTimestamp();
        SelectorTimeline.Mark = stage =>
        {
            double at = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            lock (marks) marks.Add(new ResidentProbeRecorder.Mark(stage, Math.Round(at, 2), Environment.CurrentManagedThreadId));
        };
        double selectorMs = -1;
        double? renderThreadCompleteMs = null;
        string? renderThreadCompleteError = null;
        bool renderMeasurementFinished = false;
        DispatcherOperation? renderWait = null;
        int ticks = 0;
        EventHandler rendering = (_, _) =>
        {
            if (selectorMs < 0 && ProbeOverlay is { IsVisible: true, Content: CaptureOverlayView { Frame: not null } })
            {
                selectorMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                SelectorTimeline.Mark?.Invoke("probe-first-rendering-tick");
                // At the first dispatcher turn after this render pass (Show returning): block until the
                // render thread has finished every committed frame, the first selector frame included.
                // The mark is then an upper bound for that frame's present.
                renderWait = Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                {
                    SelectorTimeline.Mark?.Invoke("render-wait-start");
                    try
                    {
                        if (ProbeCompleteRender is not { } completeRender
                            || ProbeMediaContextFrom?.Invoke(null, [Dispatcher]) is not { } context)
                        {
                            renderThreadCompleteError = "WPF MediaContext.CompleteRender is unavailable";
                            return;
                        }
                        completeRender.Invoke(context, null);
                        renderThreadCompleteMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        SelectorTimeline.Mark?.Invoke("render-thread-complete");
                    }
                    catch (Exception error) when (error is TargetInvocationException or MemberAccessException or InvalidOperationException)
                    {
                        renderThreadCompleteError = error.GetBaseException().Message;
                        SelectorTimeline.Mark?.Invoke("render-thread-complete-unavailable");
                    }
                    finally { renderMeasurementFinished = true; }
                }));
            }
            else if (selectorMs >= 0 && ticks++ < 12)
            {
                SelectorTimeline.Mark?.Invoke("rendering-tick");
            }
        };
        CompositionTarget.Rendering += rendering;
        // Every dispatcher operation of 2 ms or more on the way, by name and priority: what holds the
        // UI thread between the frame and the selector's ContentRendered.
        var operationStarts = new Dictionary<DispatcherOperation, long>();
        DispatcherHookEventHandler operationStarted = (_, e) => operationStarts[e.Operation] = Stopwatch.GetTimestamp();
        DispatcherHookEventHandler operationCompleted = (_, e) =>
        {
            if (!operationStarts.Remove(e.Operation, out long began)) return;
            double duration = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            if (duration < 2) return;
            double at = Stopwatch.GetElapsedTime(started, began).TotalMilliseconds;
            string operation = ProbeOperationName?.GetValue(e.Operation) as string ?? "?";
            SelectorTimeline.Mark?.Invoke($"op {operation} [{e.Operation.Priority}] from {at:0.0} for {duration:0.0}");
        };
        Dispatcher.Hooks.OperationStarted += operationStarted;
        Dispatcher.Hooks.OperationCompleted += operationCompleted;
        Dispatcher.Hooks.OperationAborted += operationCompleted;
        try
        {
            HandleCaptureRequested();
            if (ProbeOverlay is { } shownOverlay)
            {
                shownOverlay.Loaded += (_, _) => SelectorTimeline.Mark?.Invoke("window-loaded");
                if (shownOverlay.Content is FrameworkElement view)
                    view.Loaded += (_, _) => SelectorTimeline.Mark?.Invoke("view-loaded");
            }
            await ProbeWaitAsync(() => selectorMs >= 0, 10_000, "selector first frame");
            SelectorTimeline.Mark?.Invoke("probe-resumed");
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => SelectorTimeline.Mark?.Invoke("dispatcher-input-reached")));
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => SelectorTimeline.Mark?.Invoke("dispatcher-background-reached")));
            var waited = Stopwatch.StartNew();
            while (waited.ElapsedMilliseconds < 1000)
            {
                lock (marks) if (renderMeasurementFinished && marks.Exists(mark => mark.Name == "content-rendered")) break;
                await Task.Delay(5);
            }
        }
        finally
        {
            CompositionTarget.Rendering -= rendering;
            Dispatcher.Hooks.OperationStarted -= operationStarted;
            Dispatcher.Hooks.OperationCompleted -= operationCompleted;
            Dispatcher.Hooks.OperationAborted -= operationCompleted;
            if (renderWait?.Status == DispatcherOperationStatus.Pending) renderWait.Abort();
            SelectorTimeline.Mark = null;
        }
        int index = _probeBreakdownIndex.TryGetValue(name, out int previous) ? previous + 1 : 1;
        _probeBreakdownIndex[name] = index;
        if (renderThreadCompleteMs is { } completeMs)
            recorder.Time(name + ": shortcut → selector render thread complete (upper bound)", completeMs);
        lock (marks) recorder.AddBreakdown(new ResidentProbeRecorder.Breakdown(name, index, Math.Round(selectorMs, 2), sinceDismissal, marks.ToArray())
        {
            RenderThreadCompleteMs = renderThreadCompleteMs is { } measured ? Math.Round(measured, 2) : null,
            RenderThreadCompleteError = renderThreadCompleteError
                ?? (renderThreadCompleteMs is null ? "Render completion was not observed before the probe ended" : null),
        });
        return selectorMs;
    }

    /// <summary>A 3840 x 2160 capture opened in the editor, closed, then the next shortcut at once.</summary>
    private async Task ProbeEditorAsync(ResidentProbeRecorder recorder)
    {
        if (_overlay is null || _settings is null) throw new InvalidOperationException("The capture shell is unavailable.");
        AfterCaptureAction previous = _settings.General.AfterCapture;
        _settings.General.AfterCapture = AfterCaptureAction.Editor;
        try
        {
            DismissQuickAccessForCapture();
            RectD desktop = MonitorEnumerator.GetVirtualDesktopBounds();
            FrozenFrame frame = ResidentProbeImages.DesktopFrame(new RectD(desktop.Left, desktop.Top, 3840, 2160), 7,
                s_residentProbe?.Has("frame-cached") == true);
            long started = Stopwatch.GetTimestamp();
            double editorMs = -1;
            EventHandler rendering = (_, _) =>
            {
                if (editorMs < 0 && ProbeEditor is { IsVisible: true }) editorMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            };
            CompositionTarget.Rendering += rendering;
            try
            {
                recorder.Track("editor-4k", "4K frame", frame.Bitmap);
                if (!_overlay.StartWithSelection(frame, new RectD(0, 0, 3840, 2160), "Resident probe"))
                    throw new InvalidOperationException("The editor did not start.");
                frame = null!;
                await ProbeWaitAsync(() => editorMs >= 0, 60_000, "editor shown");
            }
            finally { CompositionTarget.Rendering -= rendering; }
            recorder.Time("editor-4k: capture → editor shown", editorMs);
            recorder.Track("editor-4k", "editor window", ProbeEditor);
            recorder.Track("editor-4k", "editor control", ProbeEditor?.Editor);
            await Task.Delay(1500);
            recorder.Sample("editor-4k open", 0);
            ProbeEditor?.Close();
            await ProbeWaitAsync(() => _overlay.IsActive != true && _capturePersistenceTask.IsCompleted, 15_000, "editor closed");
        }
        finally { _settings.General.AfterCapture = previous; }

        // The complaint: the next capture right after an editor closed.
        double selectorMs = await ProbeShortcutToSelectorAsync(recorder, "editor-4k next");
        recorder.Time("editor-4k: closed → next shortcut → selector first frame", selectorMs);
        _overlay.Cancel();
        await ProbeWaitAsync(() => _overlay.IsActive != true, 10_000, "selector cancelled");
    }

    private async Task ProbeLibraryAsync(ResidentProbeRecorder recorder)
    {
        long started = Stopwatch.GetTimestamp();
        double shownMs = -1;
        EventHandler rendering = (_, _) =>
        {
            if (shownMs < 0 && _galleryWindow is { IsVisible: true }) shownMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        };
        CompositionTarget.Rendering += rendering;
        try
        {
            HandleGalleryRequested();
            await ProbeWaitAsync(() => shownMs >= 0, 15_000, "library shown");
        }
        finally { CompositionTarget.Rendering -= rendering; }
        recorder.Time("library: open → shown", shownMs);
        recorder.Track("library", "library window", _galleryWindow);
        await Task.Delay(2500);
        recorder.Sample("library open", 0, $"{_queue?.Count ?? 0} captures");
        _galleryWindow?.Close();
        await ProbeWaitAsync(() => _galleryWindow is not { IsVisible: true }, 10_000, "library closed");
    }

    private async Task ProbeOcrAsync(ResidentProbeRecorder recorder)
    {
        if (_ocrPresenter is null || _settings is null) throw new InvalidOperationException("OCR is unavailable.");
        BitmapSource image = ResidentProbeImages.Text(1600, 900,
        [
            "Resident probe 1234567890",
            "The quick brown fox jumps over the lazy dog.",
            "static void Main(string[] args) { return; }",
            "MyCapture keeps only what is open.",
        ]);
        OcrSettings ocr = _settings.Ocr;
        long started = Stopwatch.GetTimestamp();
        _ocrPresenter.ShowRecognized(() => OcrRequestFactory.FromBitmap(image, ocr), "Resident probe");
        await ProbeWaitAsync(() => ProbeOcrWindow() is { IsVisible: true }, 15_000, "OCR window shown");
        recorder.Time("ocr: request → window shown", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        recorder.Track("ocr", "OCR image", image);
        recorder.Track("ocr", "OCR window", ProbeOcrWindow());
        await Task.Delay(4000);
        recorder.Sample("ocr open", 0, ocr.Quality.ToString());
        ProbeOcrWindow()?.Close();
        await ProbeWaitAsync(() => ProbeOcrWindow() is not { IsVisible: true }, 10_000, "OCR window closed");
    }

    private static OcrResultWindow? ProbeOcrWindow() =>
        Current?.Windows.OfType<OcrResultWindow>().FirstOrDefault();

    private async Task ProbePinsAsync(ResidentProbeRecorder recorder)
    {
        if (_pins is null) throw new InvalidOperationException("Pins are unavailable.");
        long started = Stopwatch.GetTimestamp();
        for (int index = 0; index < 5; index++) _pins.PinImage(ResidentProbeImages.Pattern(1920, 1080, index));
        for (int index = 0; index < 5; index++)
        {
            BitmapSource text = ResidentProbeImages.Text(900, 360, [$"Pinned text {index}", "SELECT * FROM captures;", "x = y + 1"]);
            _pins.PinRenderedText(text, $"Pinned text {index}\nSELECT * FROM captures;\nx = y + 1", isTable: false);
        }
        await ProbeWaitAsync(() => _pins.SnapshotOpenPinWindows().Count(pin => pin.IsVisible) >= 10, 15_000, "pins shown");
        recorder.Time("pins-10: create → all shown", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        foreach (var pin in _pins.SnapshotOpenPinWindows()) recorder.Track("pins-10", "pin window", pin);
        await Task.Delay(1500);
        recorder.Sample("pins-10 open", 0);
        _pins.CloseAll();
        await ProbeWaitAsync(() => _pins.Count == 0, 10_000, "pins closed");
    }

    private static async Task ProbeWaitAsync(Func<bool> done, int timeoutMs, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Resident probe timed out: " + what);
            await Task.Delay(5);
        }
    }
}
