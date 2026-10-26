using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;

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

    /// <summary>Reads the virtual desktop as the selector would, without its pixels.</summary>
    private static FrozenFrame AcquireProbeFrame(bool includeCursor)
    {
        long started = Stopwatch.GetTimestamp();
        FrozenFrame frame = ResidentProbeImages.DesktopFrame(MonitorEnumerator.GetVirtualDesktopBounds(), Environment.TickCount & 0xFF);
        s_lastProbeAcquisitionMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return frame;
    }

    /// <summary>The synthetic grab's own time, part of every shortcut → selector timing.</summary>
    private static double s_lastProbeAcquisitionMs;

    private static void ApplyResidentProbeSettings(AppSettings settings)
    {
        settings.General.IsFirstRun = false;
        settings.General.FirstCaptureTipShown = true;
        settings.General.AfterCapture = AfterCaptureAction.QuickAccess;
        settings.General.LaunchAtLogin = false;
        settings.General.PlayCaptureSound = false;
    }

    private async Task RunResidentProbeAsync(ResidentProbeOptions probe)
    {
        var recorder = new ResidentProbeRecorder(probe, Dispatcher);
        int exitCode = 0;
        try
        {
            await Task.WhenAll(_initialLibraryRecoveryTask, _initialEditRecoveryTask);
            await recorder.SettleAsync("start", probe.StartSeconds);
            await recorder.AuditAsync("start");
            bool measuredIdleRelease = false;
            bool capturedForIdle = false;
            foreach (string scenario in probe.Scenarios)
            {
                try
                {
                    if (probe.MeasureIdleRelease && scenario == "editor-4k" && !capturedForIdle)
                        await ProbeCaptureToCardAsync(recorder, "idle preparation", dwellMs: 1200);
                    await RunResidentProbeScenarioAsync(probe, recorder, scenario);
                    if (scenario is "capture" or "capture-30") capturedForIdle = true;
                    if (probe.MeasureIdleRelease && scenario == "editor-4k")
                    {
                        await ProbeIdleReleaseAsync(probe, recorder);
                        measuredIdleRelease = true;
                    }
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
            if (probe.MeasureIdleRelease && !measuredIdleRelease)
            {
                // A custom scenario list still gets the same capture/card/4K-editor preparation.
                await ProbeCaptureToCardAsync(recorder, "idle preparation", dwellMs: 1200);
                await ProbeEditorAsync(recorder);
                await ProbeIdleReleaseAsync(probe, recorder);
            }
            CheckResidentIdleFailures();
        }
        catch (Exception ex)
        {
            recorder.Fail(ex);
            exitCode = 1;
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
        double selectorMs = await ProbeShortcutToSelectorAsync();
        recorder.Time(name + ": shortcut → selector first frame", selectorMs);
        recorder.Time(name + ": synthetic desktop grab (inside the selector timing)", s_lastProbeAcquisitionMs);
        CaptureOverlayWindow overlay = ProbeOverlay ?? throw new InvalidOperationException("The selector did not open.");
        recorder.Track(name, "selector window", overlay);
        recorder.Track(name, "selector frame", (overlay.Content as CaptureOverlayView)?.Frame);

        long selected = Stopwatch.GetTimestamp();
        double cardMs = -1;
        EventHandler rendering = (_, _) =>
        {
            if (cardMs < 0 && _quickAccess is { IsVisible: true }) cardMs = Stopwatch.GetElapsedTime(selected).TotalMilliseconds;
        };
        CompositionTarget.Rendering += rendering;
        try
        {
            overlay.CompleteSelection(new RectD(160, 120, 1280, 720));
            await ProbeWaitAsync(() => cardMs >= 0, 10_000, "card shown");
        }
        finally { CompositionTarget.Rendering -= rendering; }
        recorder.Time(name + ": selection → card shown", cardMs);
        recorder.Track(name, "card", _quickAccess);
        recorder.Track(name, "card image", _quickAccess?.PreviewForTest.Source);
        recorder.Track(name, "flight", _captureFlight);
        recorder.Track(name, "flight window", _captureFlight?.WindowForTest);

        await Task.Delay(dwellMs);
        _quickAccess?.RequestClose();
        await ProbeWaitAsync(() => _quickAccess is null && _overlay?.IsActive != true && _capturePersistenceTask.IsCompleted,
            10_000, "card closed");
    }

    /// <summary>The capture shortcut's handler until the selector has drawn its frozen frame.</summary>
    private async Task<double> ProbeShortcutToSelectorAsync()
    {
        long started = Stopwatch.GetTimestamp();
        // Include synchronous device restoration in the shortcut timing, before a Rendering
        // subscription itself can wake WPF's render path.
        BeforeResidentUse();
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

    /// <summary>A 3840 x 2160 capture opened in the editor, closed, then the next shortcut at once.</summary>
    private async Task ProbeEditorAsync(ResidentProbeRecorder recorder)
    {
        BeforeResidentUse();
        if (_overlay is null || _settings is null) throw new InvalidOperationException("The capture shell is unavailable.");
        AfterCaptureAction previous = _settings.General.AfterCapture;
        _settings.General.AfterCapture = AfterCaptureAction.Editor;
        try
        {
            DismissQuickAccessForCapture();
            RectD desktop = MonitorEnumerator.GetVirtualDesktopBounds();
            FrozenFrame frame = ResidentProbeImages.DesktopFrame(new RectD(desktop.Left, desktop.Top, 3840, 2160), 7);
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
        double selectorMs = await ProbeShortcutToSelectorAsync();
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
        BeforeResidentUse();
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
        BeforeResidentUse();
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

    private async Task ProbeIdleReleaseAsync(ResidentProbeOptions probe, ResidentProbeRecorder recorder)
    {
        // Restart the idle interval after the final editor/selector closure, without a forced GC.
        BeforeResidentUse();
        NativeMessageWindow shell = _services!.GetRequiredService<NativeMessageWindow>();
        IntPtr shellHandle = shell.Handle;
        var wait = Stopwatch.StartNew();
        TimeSpan lastVisible = TimeSpan.Zero;
        TimeSpan threshold = TimeSpan.FromSeconds(probe.IdleSeconds);
        recorder.Sample("idle release before", 0);
        while (true)
        {
            CheckResidentIdleFailures();
            bool visible = ResidentWindowActivity.HasVisibleProcessWindow();
            if (visible) lastVisible = wait.Elapsed;
            TimeSpan inputIdle = ResidentWindowActivity.InputIdleTime();
            _residentRenderIdle?.Poll(visible, inputIdle);
            if (!visible && wait.Elapsed - lastVisible >= threshold && inputIdle >= threshold
                && (_residentRenderIdle is null || _residentRenderIdle.IsReleased)) break;
            if (wait.Elapsed > threshold + TimeSpan.FromSeconds(Math.Max(60, probe.IdleSeconds)))
                throw new TimeoutException("Resident idle release did not reach a quiet interval; check visible windows and desktop input.");
            await Task.Delay(100);
        }
        recorder.Time("idle release: actual quiet wait", wait.Elapsed.TotalMilliseconds);
        string? Detail() => $"variant={probe.IdleRelease}; released={_residentRenderIdle?.IsReleased == true}; "
            + $"wait={wait.Elapsed.TotalSeconds:0.000}s; shell unchanged={shell.Handle == shellHandle}; "
            + $"keepalive={shell.IsRenderKeepAlivePresent}";
        recorder.Sample("idle release threshold", probe.IdleSeconds, Detail());
        // WPF sends device/channel destruction to its render thread. Give it the same natural
        // drain interval in every variant, with no Rendering subscription and no forced GC.
        await recorder.SettleAsync("idle release", [0, 1, 3], Detail);
        CheckResidentIdleFailures();
        if (ResidentWindowActivity.HasVisibleProcessWindow()
            || (_residentRenderIdle is not null && !_residentRenderIdle.IsReleased))
            throw new InvalidOperationException("A window interrupted the idle-release measurement.");
        if (shell.Handle != shellHandle) throw new InvalidOperationException("Idle release replaced the shell HWND.");
        int restores = _residentRenderIdle?.RestoreCount ?? 0;
        foreach (string name in new[] { "idle release: first capture", "idle release: second capture" })
        {
            double selectorMs = await ProbeShortcutToSelectorAsync();
            recorder.Time(name + ": shortcut → selector first frame", selectorMs);
            recorder.Time(name + ": synthetic desktop grab (inside the selector timing)", s_lastProbeAcquisitionMs);
            if (_residentRenderIdle?.IsReleased == true)
                throw new InvalidOperationException("The selector was shown before the render path was restored.");
            _overlay!.Cancel();
            await ProbeWaitAsync(() => _overlay.IsActive != true, 10_000, "post-release selector cancelled");
        }
        if (_residentRenderIdle is not null && _residentRenderIdle.RestoreCount != restores + 1)
            throw new InvalidOperationException("The first post-release capture did not restore exactly once.");
        if (shell.Handle != shellHandle) throw new InvalidOperationException("Restoration replaced the shell HWND.");
        CheckResidentIdleFailures();
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
