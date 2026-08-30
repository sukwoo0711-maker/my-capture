using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Pinning;
using MyCapture.App.Settings;
using MyCapture.Core.Queue;
using MyCapture.Core.Capture;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

public partial class App : Application
{
    /// <summary>Per-Windows-session ownership gate for the resident process.</summary>
    private const string SingleInstanceMutexName =
        @"Local\MyCapture.SingleInstance.{6F2A1C34-9B7E-4D51-8A0C-3E5D7B912F48}";

    /// <summary>
    /// Auto-reset signal used by later launches to ask the resident instance to
    /// activate its gallery rather than opening a second tray process.
    /// </summary>
    private const string ActivationEventName =
        @"Local\MyCapture.Activate.{6F2A1C34-9B7E-4D51-8A0C-3E5D7B912F48}";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activationSignal;
    private CancellationTokenSource? _activationCancellation;
    private ServiceProvider? _services;
    private ILogger<App>? _log;
    private TrayIconService? _tray;
    private GlobalHotkeyService? _hotkeys;
    private AppSettings? _settings;
    private CaptureOverlayCoordinator? _overlay;
    private MyCapture.App.Recording.RegionRecordingCoordinator? _recorder;
    private LastRegionStore? _lastRegions;
    private AdvancedCaptureService? _advancedCapture;
    private CountdownWindow? _activeCountdown;
    private CancellationTokenSource? _scrollCancellation;
    private CaptureQueue? _queue;
    private CapturePersistenceService? _persistence;
    private CaptureCommitService? _commit;
    private AppPaths? _capturePaths;
    private GalleryController? _galleryController;
    private GalleryReeditLoader? _reeditLoader;
    private GalleryWindow? _galleryWindow;
    private PinManager? _pins;
    private IOcrService? _ocrService;
    private OcrResultPresenter? _ocrPresenter;
    private SettingsWindow? _settingsWindow;
    private StartupRegistrationService? _startupService;
    private SettingsApplyService? _settingsApply;

    /// <summary>The record persisted for the capture currently being edited, if any.</summary>
    private CaptureRecord? _currentRecord;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Diagnostics intentionally run before the ownership gate. Capture hardware
        // can be tested beside a normal resident instance; the shell test will report
        // hotkey conflicts if the normal instance already owns them.
        if (TryRunSelfTest(e.Args))
        {
            return;
        }

        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            SingleInstanceMutexName,
            out bool isFirstInstance);

        if (!isFirstInstance)
        {
            SignalResidentInstance();
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _services = BuildServiceProvider();
        _log = _services.GetRequiredService<ILogger<App>>();
        _log.LogInformation("MyCapture starting up");

        try
        {
            InitializeShell();
            StartActivationListener();
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Could not initialize the resident shell");
            MessageBox.Show(
                $"MyCapture를 시작할 수 없습니다.\n\n{ex.Message}",
                "MyCapture",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        StartCapturePrewarm();

        // Optional deterministic UI smoke: a normal first-instance launch with --settings
        // opens the settings window immediately. Used by the packaged UI smoke path; it does
        // not alter the process lifecycle otherwise.
        if (FindSwitch(e.Args, SettingsCommandLineSwitch) >= 0)
        {
            _ = Dispatcher.BeginInvoke(new Action(HandleSettingsRequested));
        }
    }

    /// <summary>First-instance switch that opens the settings window on launch.</summary>
    internal const string SettingsCommandLineSwitch = "--settings";

    private void InitializeShell()
    {
        if (_services is null)
        {
            throw new InvalidOperationException("Application services are unavailable.");
        }

        _settings = _services.GetRequiredService<SettingsStore>().Load();

        _tray = _services.GetRequiredService<TrayIconService>();
        _hotkeys = _services.GetRequiredService<GlobalHotkeyService>();
        _overlay = _services.GetRequiredService<CaptureOverlayCoordinator>();

        // Advanced capture: a bounded last-region history feeds repeat-last-region, and the
        // service converges full-screen / window / repeat / scrolling onto the shared editor
        // pipeline through the overlay coordinator.
        _lastRegions = new LastRegionStore(() => _settings!.Capture.RegionHistoryLimit);
        var advancedEnvironment = new OverlayAdvancedCaptureEnvironment(
            _overlay,
            _services.GetRequiredService<WindowTitleService>(),
            () => _settings!.Capture.IncludeCursor);
        _advancedCapture = new AdvancedCaptureService(
            advancedEnvironment,
            _lastRegions,
            _services.GetRequiredService<IScrollInputSink>(),
            _services.GetRequiredService<ILogger<AdvancedCaptureService>>());

        InitializeQueue();

        _pins = new PinManager(
            () => _settings!.Pin,
            _services.GetRequiredService<ILogger<PinManager>>());

        _ocrService = _services.GetRequiredService<IOcrService>();
        _ocrPresenter = new OcrResultPresenter(
            _ocrService,
            Dispatcher,
            SetOcrBusy,
            _services.GetRequiredService<ILogger<OcrResultPresenter>>());

        // A pin's OCR is transient: recognise the pinned image bytes and show the shared result
        // window, but never cache the text — a pin has no backing capture record.
        _pins.OcrRequested += OnPinOcrRequested;

        _tray.CaptureRequested += (_, _) => HandleCaptureRequested();
        _tray.CaptureWindowRequested += (_, _) => HandleCaptureWindow();
        _tray.CaptureFullScreenRequested += (_, _) => HandleCaptureFullScreen();
        _tray.RepeatLastRegionRequested += (_, _) => HandleRepeatLastRegion();
        _tray.DelayedCaptureRequested += (_, _) => HandleDelayedCapture();
        _tray.ScrollingCaptureRequested += (_, _) => HandleScrollingCapture();
        _tray.GalleryRequested += (_, _) => HandleGalleryRequested();
        _tray.SettingsRequested += (_, _) => HandleSettingsRequested();
        _tray.ExitRequested += (_, _) => Shutdown(0);
        _hotkeys.Pressed += OnGlobalHotkeyPressed;
        _overlay.SelectionCompleted += OnCaptureSelectionCompleted;
        _overlay.EditingCompleted += OnAnnotationEditingCompleted;
        _overlay.OverlayClosed += (_, _) =>
        {
            _currentRecord = null;
            RestoreTrayAfterCapture();
        };
        _overlay.CommitRequested = HandleCommit;

        // Region video recording (Ctrl+Shift+X). Shares the capture engine and, on a
        // frame-image edit, the same persistence/commit path as still capture so recordings
        // inherit the gallery, layer-preserving re-edit and offline story unchanged.
        _recorder = new MyCapture.App.Recording.RegionRecordingCoordinator(
            _services.GetRequiredService<ScreenCaptureEngine>(),
            _capturePaths ?? _services.GetRequiredService<AppPaths>(),
            () => _settings!.Recording,
            _services.GetRequiredService<ILoggerFactory>());
        _recorder.FrameImageCaptured += OnRecordedFrameImageCaptured;
        _recorder.SessionEnded += (_, _) => RestoreTrayAfterCapture();

        // Add the icon first so registration failures have a non-modal place to be
        // reported. A tray utility must not interrupt logon with a message box merely
        // because another program claimed a chord first.
        _tray.Initialize();
        _tray.SetCaptureCount(_queue?.Count ?? 0);
        _hotkeys.Initialize(_settings.Hotkeys);

        // Launch-at-login: reconcile a stale/moved Run entry to this build's path on
        // startup, best-effort. Never mutates the Run key on this machine during tests
        // because the self-test path returns before InitializeShell runs.
        _startupService = _services.GetRequiredService<StartupRegistrationService>();
        try
        {
            _ = _startupService.ReconcileOnStartup(_settings.General.LaunchAtLogin);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Launch-at-login reconciliation failed");
        }

        _settingsApply = new SettingsApplyService(
            _services.GetRequiredService<SettingsStore>(),
            () => _settings!,
            updated => _settings = updated,
            _queue,
            _hotkeys,
            _startupService,
            _services.GetRequiredService<ILogger<SettingsApplyService>>());

        if (_hotkeys.Failures.Count > 0)
        {
            _tray.SetState(TrayIconState.Error);

            HotkeyRegistrationFailure first = _hotkeys.Failures[0];
            string suffix = _hotkeys.Failures.Count == 1
                ? string.Empty
                : $" 외 {_hotkeys.Failures.Count - 1}개";

            _tray.ShowBalloon(
                "단축키 충돌",
                $"{first.Hotkey} 단축키를 등록할 수 없습니다{suffix}. 설정에서 변경해 주세요.",
                TrayBalloonKind.Warning);
        }
    }

    private void OnGlobalHotkeyPressed(object? sender, GlobalHotkeyPressedEventArgs e)
    {
        switch (e.Command)
        {
            case GlobalHotkeyCommand.CaptureRegion:
                HandleCaptureRequested();
                break;
            case GlobalHotkeyCommand.PasteToScreen:
                _log?.LogInformation("Paste-to-screen hotkey requested");
                HandlePasteToScreen();
                break;
            case GlobalHotkeyCommand.HideAllPins:
                _log?.LogInformation("Hide-all-pins hotkey requested");
                _pins?.HideOrShowAll();
                break;
            case GlobalHotkeyCommand.ToggleClickThrough:
                _log?.LogInformation("Toggle-click-through hotkey requested");
                _pins?.ToggleClickThroughUnderCursor();
                break;
            case GlobalHotkeyCommand.RepeatLastRegion:
                _log?.LogInformation("Repeat-last-region hotkey requested");
                HandleRepeatLastRegion();
                break;
            case GlobalHotkeyCommand.CaptureWindow:
                _log?.LogInformation("Capture-window hotkey requested");
                HandleCaptureWindow();
                break;
            case GlobalHotkeyCommand.CaptureFullScreen:
                _log?.LogInformation("Capture-full-screen hotkey requested");
                HandleCaptureFullScreen();
                break;
            case GlobalHotkeyCommand.RecordRegion:
                _log?.LogInformation("Region recording hotkey requested");
                HandleRecordRegion();
                break;
        }
    }

    /// <summary>
    /// Pins the clipboard image to the screen, or shows a concise tray balloon when there is
    /// no image or the clipboard is momentarily busy. Never throws into the message pump: a
    /// pin failure must not take down the resident process.
    /// </summary>
    private void HandlePasteToScreen()
    {
        if (_pins is null)
        {
            return;
        }

        try
        {
            PasteResult result = _pins.PasteFromClipboard();
            switch (result)
            {
                case PasteResult.NoImage:
                    _tray?.ShowBalloon(
                        "화면에 고정할 수 없습니다",
                        "클립보드에 이미지가 없습니다.",
                        TrayBalloonKind.Information,
                        playSound: false);
                    break;
                case PasteResult.ClipboardBusy:
                    _tray?.ShowBalloon(
                        "클립보드를 사용할 수 없습니다",
                        "다른 프로그램이 클립보드를 사용 중입니다. 잠시 후 다시 시도해 주세요.",
                        TrayBalloonKind.Warning,
                        playSound: false);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Fail soft: a corrupt clipboard payload or a WPF decode failure must not crash
            // the tray. Report it and stay resident.
            _log?.LogError(ex, "Paste-to-screen failed");
            _tray?.ShowBalloon(
                "화면 고정에 실패했습니다",
                ex.Message,
                TrayBalloonKind.Error,
                playSound: false);
        }
    }

    private void HandleCaptureRequested()
    {
        _log?.LogInformation("Region capture requested");
        if (_overlay is null || _settings is null)
        {
            return;
        }

        try
        {
            _tray?.SetState(TrayIconState.Capturing);
            _overlay.Start(
                _settings.Capture.IncludeCursor,
                _settings.Capture.AbortOnFocusLoss,
                _settings.Capture.ShowMagnifier);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Could not open the capture overlay");
            _tray?.SetState(TrayIconState.Error);
            _tray?.ShowBalloon(
                "캡처를 시작할 수 없습니다",
                ex.Message,
                TrayBalloonKind.Error);
        }
    }

    /// <summary>
    /// Full-monitor capture: acquires the monitor under the cursor and opens the editor with
    /// no drag. Reports the typed outcome through the shared feedback path.
    /// </summary>
    private void HandleCaptureFullScreen()
    {
        if (_advancedCapture is null)
        {
            return;
        }

        _tray?.SetState(TrayIconState.Capturing);
        ReportOutcome(_advancedCapture.CaptureFullScreen(), "전체 화면 캡처");
    }

    /// <summary>
    /// Window capture: hit-tests the window under the cursor, crops it out of the monitor
    /// frame, and opens the editor. A missing window is reported, not thrown.
    /// </summary>
    private void HandleCaptureWindow()
    {
        if (_advancedCapture is null)
        {
            return;
        }

        _tray?.SetState(TrayIconState.Capturing);
        ReportOutcome(_advancedCapture.CaptureWindow(), "창 캡처");
    }

    /// <summary>
    /// Repeat-last-region: replays the most recent confirmed region with no overlay. Shows a
    /// balloon when there is no region to repeat yet.
    /// </summary>
    private void HandleRepeatLastRegion()
    {
        if (_advancedCapture is null)
        {
            return;
        }

        _tray?.SetState(TrayIconState.Capturing);
        ReportOutcome(_advancedCapture.RepeatLastRegion(), "이전 영역 반복 캡처");
    }

    /// <summary>
    /// Delayed capture: shows a transient countdown window, then — after tearing that window
    /// down so it can never appear in the frozen frame — starts a normal region capture.
    /// </summary>
    /// <remarks>
    /// The countdown lives in its own top-level window with a <c>CancellationTokenSource</c>
    /// bound to Esc. Capture is scheduled on a later dispatcher turn via
    /// <see cref="Dispatcher.BeginInvoke"/> so the countdown window is fully closed and
    /// removed from the desktop before <see cref="ScreenCaptureEngine.CaptureMonitor"/> runs,
    /// preserving the capture-before-wait invariant.
    /// </remarks>
    private void HandleDelayedCapture()
    {
        if (_settings is null)
        {
            return;
        }

        int seconds = Math.Max(0, _settings.Capture.DelaySeconds);
        if (seconds == 0)
        {
            // Zero delay is an immediate region capture.
            HandleCaptureRequested();
            return;
        }

        if (_activeCountdown is not null)
        {
            _activeCountdown.Activate();
            return;
        }

        var countdown = new CountdownWindow(seconds);
        _activeCountdown = countdown;
        _tray?.SetState(TrayIconState.Capturing);

        void Cleanup()
        {
            countdown.Elapsed -= OnElapsed;
            countdown.Cancelled -= OnCancelled;
            _activeCountdown = null;
        }

        void OnElapsed(object? sender, EventArgs e)
        {
            Cleanup();
            // Close the window FIRST, then start the capture on the next turn so the countdown
            // is gone from the screen before the freeze.
            countdown.Close();
            _ = Dispatcher.BeginInvoke(new Action(HandleCaptureRequested));
        }

        void OnCancelled(object? sender, EventArgs e)
        {
            Cleanup();
            countdown.Close();
            RestoreTrayAfterCapture();
            _tray?.ShowBalloon(
                "지연 캡처 취소됨",
                "지연 캡처를 취소했습니다.",
                TrayBalloonKind.Information,
                playSound: false);
        }

        countdown.Elapsed += OnElapsed;
        countdown.Cancelled += OnCancelled;
        countdown.Closed += (_, _) => { if (_activeCountdown == countdown) { Cleanup(); RestoreTrayAfterCapture(); } };
        countdown.Show();
        countdown.Activate();
    }

    /// <summary>
    /// Scrolling capture: uses the window under the cursor as the scroll region, drives the
    /// capture → scroll → stitch loop in Core, and opens the editor over the stitched image.
    /// </summary>
    /// <remarks>
    /// The scroll region is the client rectangle of the window under the cursor, which avoids
    /// the window chrome while matching what the user is pointing at. The heavy lifting
    /// (overlap detection, fixed-header handling, termination) is pure Core code; this handler
    /// only supplies the region and reports the typed outcome.
    /// </remarks>
    private async void HandleScrollingCapture()
    {
        if (_advancedCapture is null)
        {
            return;
        }

        // The same accessible tray command toggles to cancellation while the loop is active.
        // Because the loop awaits repaint delays, this handler remains reachable on the WPF
        // dispatcher instead of waiting behind a synchronous capture loop.
        if (_scrollCancellation is not null)
        {
            _scrollCancellation.Cancel();
            _tray?.ShowBalloon(
                "스크롤 캡처 취소 중",
                "현재 프레임 처리가 끝나면 중단합니다.",
                TrayBalloonKind.Information,
                playSound: false);
            return;
        }

        Core.Primitives.PointD cursor = CursorLocator.GetPosition();
        WindowUnderCursor? window =
            _services?.GetRequiredService<WindowTitleService>().ResolveAt(cursor);

        if (window is null || window.ScrollBounds.IsEmpty)
        {
            ReportOutcome(
                CaptureOutcome.NothingToCapture("스크롤 캡처할 창이 없습니다."),
                "스크롤 캡처");
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _scrollCancellation = cancellation;
        _tray?.SetScrollingCaptureActive(true);
        _tray?.SetState(TrayIconState.Busy);
        _tray?.ShowBalloon(
            "스크롤 캡처 시작",
            "취소하려면 트레이 메뉴에서 ‘스크롤 캡처 취소’를 선택하세요.",
            TrayBalloonKind.Information,
            playSound: false);

        CaptureOutcome outcome;
        try
        {
            outcome = await _advancedCapture.CaptureScrollingAsync(
                window.Handle,
                window.ScrollBounds,
                Core.Capture.ScrollStitchOptions.Default,
                maxFrames: 40,
                cancellation: cancellation.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogError(ex, "Unhandled scrolling capture failure");
            outcome = CaptureOutcome.Failed(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_scrollCancellation, cancellation))
            {
                _scrollCancellation = null;
            }

            _tray?.SetScrollingCaptureActive(false);
        }

        ReportOutcome(outcome, "스크롤 캡처");
    }

    /// <summary>
    /// Maps an advanced-capture <see cref="CaptureOutcome"/> to non-throwing tray feedback:
    /// completed leaves the overlay to restore the tray, nothing-to-capture shows an
    /// information balloon, and a failure flips to the error state.
    /// </summary>
    private void ReportOutcome(CaptureOutcome outcome, string mode)
    {
        switch (outcome.Kind)
        {
            case CaptureOutcomeKind.Completed:
                // The overlay's OverlayClosed handler restores the tray when the editor closes.
                if (!string.IsNullOrWhiteSpace(outcome.Message))
                {
                    _tray?.ShowBalloon(
                        mode,
                        outcome.Message,
                        TrayBalloonKind.Information,
                        playSound: false);
                }
                break;

            case CaptureOutcomeKind.Cancelled:
                RestoreTrayAfterCapture();
                if (!string.IsNullOrWhiteSpace(outcome.Message))
                {
                    _tray?.ShowBalloon(
                        mode,
                        outcome.Message,
                        TrayBalloonKind.Information,
                        playSound: false);
                }
                break;

            case CaptureOutcomeKind.NothingToCapture:
                RestoreTrayAfterCapture();
                _tray?.ShowBalloon(
                    mode,
                    string.IsNullOrEmpty(outcome.Message) ? "캡처할 대상이 없습니다." : outcome.Message,
                    TrayBalloonKind.Information,
                    playSound: false);
                break;

            case CaptureOutcomeKind.Failed:
                _tray?.SetState(TrayIconState.Error);
                _tray?.ShowBalloon(
                    $"{mode} 실패",
                    outcome.Message,
                    TrayBalloonKind.Error);
                break;
        }
    }

    private void OnCaptureSelectionCompleted(
        object? sender,
        CaptureSelectionCompletedEventArgs e)
    {
        _log?.LogInformation(
            "Capture selection ready for editor: {Width}x{Height}",
            e.SelectedBitmap.PixelWidth,
            e.SelectedBitmap.PixelHeight);

        // Persist the untouched selection synchronously, before editing continues, so the
        // capture survives a crash or the user abandoning the editor. The same record is
        // finalised with the flattened annotations when editing commits.
        if (_persistence is null)
        {
            return;
        }

        try
        {
            _currentRecord = _persistence.PersistOriginal(
                e.SelectedBitmap,
                e.Frame.DpiScale,
                sourceWindowTitle: e.SourceTitle,
                sourceMonitor: e.Frame.Monitor?.DeviceName ?? string.Empty);

            // Repeat history is intentionally limited to explicit manual region selections.
            // Advanced full/window/scroll captures carry RecordForRepeat=false because their
            // synthetic frame coordinates are not a reusable screen rectangle.
            if (e.RecordForRepeat && _lastRegions is not null)
            {
                var screenRegion = new Core.Primitives.RectD(
                    e.Frame.ScreenBounds.Left + e.BitmapRegion.Left,
                    e.Frame.ScreenBounds.Top + e.BitmapRegion.Top,
                    e.BitmapRegion.Width,
                    e.BitmapRegion.Height)
                    .ToPixelBounds();
                MonitorInfo monitor = e.Frame.Monitor
                    ?? MonitorEnumerator.GetFromPoint(screenRegion.Center);
                _lastRegions.Record(new RegionHistoryEntry(
                    screenRegion,
                    monitor.DeviceName,
                    monitor.Bounds,
                    monitor.Dpi));
            }

            _tray?.SetCaptureCount(_queue?.Count ?? 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _currentRecord = null;
            _log?.LogError(ex, "Could not persist the captured original");
            _tray?.ShowBalloon(
                "캡처를 저장할 수 없습니다",
                ex.Message,
                TrayBalloonKind.Error);
        }
    }

    /// <summary>
    /// Performs an editor commit: flatten, persist, and any clipboard/export the action
    /// requires. Returns whether the editor should close.
    /// </summary>
    private bool HandleCommit(AnnotationEditingResult result)
    {
        if (_commit is null || _currentRecord is null)
        {
            // No persisted record to finalise (persistence failed earlier); close anyway so
            // the user is not trapped in the editor.
            return true;
        }

        try
        {
            bool shouldClose = _commit.Commit(_currentRecord, result);
            if (shouldClose)
            {
                _tray?.SetCaptureCount(_queue?.Count ?? 0);
            }

            return shouldClose;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogError(ex, "Could not finalise the annotated capture");
            _tray?.ShowBalloon(
                "저장에 실패했습니다",
                ex.Message,
                TrayBalloonKind.Error);

            // A finalise failure keeps the editor open so the user can retry rather than
            // silently losing their annotations.
            return false;
        }
    }

    private void OnAnnotationEditingCompleted(
        object? sender,
        AnnotationEditingResult e)
    {
        _log?.LogInformation(
            "Annotation editing committed ({Action}): {Width}x{Height} bitmap with {ItemCount} item(s)",
            e.Action,
            e.SelectedBitmap.PixelWidth,
            e.SelectedBitmap.PixelHeight,
            e.Document.Items.Count);

        // The commit itself (flatten/persist/clipboard/export) already ran synchronously in
        // HandleCommit before the editor closed. Nothing remains except to release the
        // in-flight record.
        _currentRecord = null;
    }

    /// <summary>
    /// Region video recording entry point. Toggles: a running recording is stopped, otherwise
    /// a new region is chosen. Never throws into the message pump.
    /// </summary>
    private void HandleRecordRegion()
    {
        if (_recorder is null)
        {
            return;
        }

        try
        {
            _tray?.SetState(TrayIconState.Capturing);
            _recorder.Toggle();
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Could not start region recording");
            _tray?.SetState(TrayIconState.Error);
            _tray?.ShowBalloon(
                "녹화를 시작할 수 없습니다",
                ex.Message,
                TrayBalloonKind.Error);
        }
    }

    /// <summary>
    /// Persists a still image the user extracted and annotated from a recorded frame, reusing
    /// the exact capture persistence/commit path so it lands in the gallery as a first-class
    /// capture with a preserved layer document.
    /// </summary>
    private void OnRecordedFrameImageCaptured(
        object? sender,
        MyCapture.App.Recording.AnnotationFrameCapturedEventArgs e)
    {
        if (_persistence is null || _commit is null)
        {
            return;
        }

        try
        {
            CaptureRecord record = _persistence.PersistOriginal(
                e.Result.SelectedBitmap,
                e.Result.Frame.DpiScale,
                sourceWindowTitle: "녹화 프레임",
                sourceMonitor: e.Result.Frame.Monitor?.DeviceName ?? string.Empty);

            _ = _commit.Commit(record, e.Result);
            _tray?.SetCaptureCount(_queue?.Count ?? 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogError(ex, "Could not persist an image edited from a recorded frame");
            _tray?.ShowBalloon(
                "프레임 이미지를 저장할 수 없습니다",
                ex.Message,
                TrayBalloonKind.Error);
        }
    }

    private void InitializeQueue()
    {
        if (_services is null || _settings is null)
        {
            return;
        }

        AppPaths paths = _services.GetRequiredService<AppPaths>();

        // Honour a relocated captures directory before anything reads or writes it.
        if (!string.IsNullOrWhiteSpace(_settings.Queue.CapturesDirectoryOverride))
        {
            paths = paths.WithCapturesRoot(_settings.Queue.CapturesDirectoryOverride);
        }

        var queue = new CaptureQueue(
            paths,
            _settings.Queue,
            _services.GetRequiredService<ILogger<CaptureQueue>>());

        // Deleting evicted directories is the shell's job: the queue stays a pure index so a
        // deletion failure can never desynchronise it from the filesystem.
        queue.Evicted += OnCaptureEvicted;
        queue.Load();

        _queue = queue;
        _capturePaths = paths;
        _persistence = new CapturePersistenceService(
            queue,
            paths,
            () => _settings!.Queue,
            _services.GetRequiredService<ILogger<CapturePersistenceService>>());
        _commit = new CaptureCommitService(
            _persistence,
            () => _settings!,
            () => paths,
            _services.GetRequiredService<ILogger<CaptureCommitService>>());

        _galleryController = new GalleryController(
            queue,
            _services.GetRequiredService<ILogger<GalleryController>>());
        _reeditLoader = new GalleryReeditLoader(
            queue,
            _services.GetRequiredService<ILogger<GalleryReeditLoader>>());
    }

    private void OnCaptureEvicted(object? sender, CaptureEvictedEventArgs e)
    {
        if (_queue is null)
        {
            return;
        }

        string directory = _queue.GetDirectory(e.Record);
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
                _log?.LogInformation(
                    "Deleted evicted capture directory {Directory} ({Reason})", directory, e.Reason);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the index entry is what enforces the cap; reclaiming the bytes can wait.
            // The orphaned directory is harmless — it is not re-indexed on a normal load — and
            // can be cleaned up by a later maintenance pass.
            _log?.LogWarning(ex, "Could not delete evicted capture directory {Directory}", directory);
        }
    }

    private void RestoreTrayAfterCapture()
    {
        if (_hotkeys?.Failures.Count > 0)
        {
            _tray?.SetState(TrayIconState.Error);
        }
        else
        {
            _tray?.SetState(TrayIconState.Idle);
        }
    }

    /// <summary>
    /// Runs OCR on a pinned image and shows the shared result window. Transient: the recognised
    /// text is never cached because a pin has no backing capture record. Non-fatal.
    /// </summary>
    private void OnPinOcrRequested(object? sender, System.Windows.Media.Imaging.BitmapSource image)
    {
        if (_ocrPresenter is null || image is null)
        {
            return;
        }

        double upscale = _settings?.Ocr.UpscaleFactor ?? 2.0;
        IReadOnlyList<string> languages = _settings?.Ocr.PreferredLanguages ?? [];

        _ocrPresenter.ShowRecognized(
            () => OcrRequest.FromBitmap(image, upscale, languages),
            "화면 고정 이미지");
    }

    /// <summary>Flips the tray to Busy during recognition and restores the prior state after.</summary>
    private void SetOcrBusy(bool busy)
    {
        if (_tray is null)
        {
            return;
        }

        if (busy)
        {
            _tray.SetState(TrayIconState.Busy);
        }
        else
        {
            RestoreTrayAfterCapture();
        }
    }

    private void HandleGalleryRequested()
    {
        _log?.LogInformation("Gallery activation requested");

        GalleryWindow? window = EnsureGalleryWindow();
        window?.ShowGallery();
    }

    /// <summary>
    /// Lazily builds the one reusable gallery window. Returns <see langword="null"/> when the
    /// queue is not yet ready (an activation arriving before initialisation completes).
    /// </summary>
    private GalleryWindow? EnsureGalleryWindow()
    {
        if (_galleryWindow is not null)
        {
            return _galleryWindow;
        }

        if (_services is null
            || _queue is null
            || _capturePaths is null
            || _galleryController is null
            || _reeditLoader is null
            || _commit is null
            || _ocrPresenter is null
            || _settings is null)
        {
            return null;
        }

        AppPaths paths = _capturePaths;
        var viewModel = new GalleryViewModel(
            _galleryController,
            record => Path.Combine(_queue.GetDirectory(record), CaptureFileNames.Thumbnail),
            _settings.Queue.ThumbnailLongEdge);

        var ocrIndexing = new MyCapture.App.Ocr.OcrIndexingService(
            _galleryController,
            _ocrService ?? _services.GetRequiredService<IOcrService>(),
            record => _queue.GetDirectory(record),
            () => _settings!.Ocr,
            _services.GetRequiredService<ILogger<MyCapture.App.Ocr.OcrIndexingService>>());

        var window = new GalleryWindow(
            viewModel,
            _galleryController,
            _reeditLoader,
            _commit,
            _queue,
            _ocrPresenter!,
            () => _settings!.Ocr,
            ocrIndexing,
            _services.GetRequiredService<ILogger<GalleryWindow>>());

        // A re-edit commit finalises against the same record; keep the tray count in sync.
        window.CaptureChanged += (_, _) => _tray?.SetCaptureCount(_queue?.Count ?? 0);

        _galleryWindow = window;
        return window;
    }

    private void HandleSettingsRequested()
    {
        _log?.LogInformation("Settings window requested");

        SettingsWindow? window = EnsureSettingsWindow();
        window?.ShowSettings();
    }

    /// <summary>
    /// Lazily builds the one reusable settings window. Returns <see langword="null"/> when
    /// the shell is not yet ready (settings/apply service unavailable).
    /// </summary>
    private SettingsWindow? EnsureSettingsWindow()
    {
        if (_settingsWindow is not null)
        {
            return _settingsWindow;
        }

        if (_services is null || _settings is null || _settingsApply is null)
        {
            return null;
        }

        var window = new SettingsWindow(
            () => _settings!,
            next => _settingsApply!.Apply(next),
            _services.GetRequiredService<ILogger<SettingsWindow>>());

        // Keep the tray state in sync after an apply (a hotkey collision flips it to Error).
        window.Applied += (_, _) => RestoreTrayAfterCapture();

        _settingsWindow = window;
        return window;
    }

    private void StartCapturePrewarm()
    {
        if (_services is null)
        {
            return;
        }

        ScreenCaptureEngine captureEngine = _services.GetRequiredService<ScreenCaptureEngine>();
        _ = Task.Run(() =>
        {
            try
            {
                captureEngine.Prewarm();
            }
            catch (Exception ex)
            {
                // Warm-up is an optimization, never a startup requirement.
                _log?.LogDebug(ex, "Capture pipeline prewarm failed");
            }
        });
    }

    private void StartActivationListener()
    {
        _activationSignal = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);
        _activationCancellation = new CancellationTokenSource();

        EventWaitHandle activationSignal = _activationSignal;
        CancellationToken cancellationToken = _activationCancellation.Token;

        _ = Task.Run(() =>
        {
            WaitHandle[] handles = [activationSignal, cancellationToken.WaitHandle];
            while (WaitHandle.WaitAny(handles) == 0)
            {
                _ = Dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new Action(HandleGalleryRequested));
            }
        });
    }

    private static void SignalResidentInstance()
    {
        // The mutex is acquired before the activation event is created, leaving a
        // very small startup race. Retry briefly instead of showing a false failure.
        for (int attempt = 0; attempt < 80; attempt++)
        {
            try
            {
                using EventWaitHandle signal = EventWaitHandle.OpenExisting(ActivationEventName);
                _ = signal.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(25);
            }
        }
    }

    private bool TryRunSelfTest(string[] args)
    {
        int captureIndex = FindSwitch(args, CaptureSelfTest.CommandLineSwitch);
        if (captureIndex >= 0)
        {
            string outputDirectory = OutputDirectoryAfter(
                args,
                captureIndex,
                "mycapture-selftest");
            return RunSelfTest(
                outputDirectory,
                "selftest-report.txt",
                CaptureSelfTest.Run);
        }

        int shellIndex = FindSwitch(args, ShellSelfTest.CommandLineSwitch);
        if (shellIndex >= 0)
        {
            string outputDirectory = OutputDirectoryAfter(
                args,
                shellIndex,
                "mycapture-shell-selftest");
            return RunSelfTest(
                outputDirectory,
                "shell-selftest-report.txt",
                ShellSelfTest.Run);
        }

        int ocrIndex = FindSwitch(args, OcrSelfTest.CommandLineSwitch);
        if (ocrIndex >= 0)
        {
            string outputDirectory = OutputDirectoryAfter(
                args,
                ocrIndex,
                "mycapture-ocr-selftest");
            return RunSelfTest(
                outputDirectory,
                "ocr-selftest-report.txt",
                OcrSelfTest.Run);
        }

        int recordingIndex = FindSwitch(args, RecordingSelfTest.CommandLineSwitch);
        if (recordingIndex >= 0)
        {
            string outputDirectory = OutputDirectoryAfter(
                args,
                recordingIndex,
                "mycapture-recording-selftest");
            return RunSelfTest(
                outputDirectory,
                "recording-selftest-report.txt",
                RecordingSelfTest.Run);
        }

        int videoEditorIndex = FindSwitch(args, VideoEditorResponsivenessSelfTest.CommandLineSwitch);
        if (videoEditorIndex >= 0)
        {
            string outputDirectory = OutputDirectoryAfter(
                args,
                videoEditorIndex,
                "mycapture-video-editor-selftest");
            return RunSelfTest(
                outputDirectory,
                "video-editor-selftest-report.txt",
                VideoEditorResponsivenessSelfTest.Run);
        }

        int settingsIndex = FindSwitch(args, SettingsSelfTest.CommandLineSwitch);
        if (settingsIndex >= 0)
        {
            string outputDirectory = OutputDirectoryAfter(
                args,
                settingsIndex,
                "mycapture-settings-selftest");
            return RunSelfTest(
                outputDirectory,
                "settings-selftest-report.txt",
                SettingsSelfTest.Run);
        }

        int advancedIndex = FindSwitch(args, AdvancedCaptureSelfTest.CommandLineSwitch);
        if (advancedIndex >= 0)
        {
            string outputDirectory = OutputDirectoryAfter(
                args,
                advancedIndex,
                "mycapture-advanced-selftest");
            return RunSelfTest(
                outputDirectory,
                "advanced-selftest-report.txt",
                AdvancedCaptureSelfTest.Run);
        }

        return false;
    }

    private bool RunSelfTest(
        string outputDirectory,
        string reportFileName,
        Func<string, int> test)
    {
        int exitCode;
        try
        {
            exitCode = test(outputDirectory);
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(outputDirectory);
                File.WriteAllText(
                    Path.Combine(outputDirectory, reportFileName),
                    $"RESULT: FAIL (unhandled exception)\n\n{ex}");
            }
            catch (IOException)
            {
            }

            exitCode = 2;
        }

        Shutdown(exitCode);
        return true;
    }

    private static int FindSwitch(string[] args, string commandLineSwitch) =>
        Array.FindIndex(
            args,
            argument => string.Equals(
                argument,
                commandLineSwitch,
                StringComparison.OrdinalIgnoreCase));

    private static string OutputDirectoryAfter(
        string[] args,
        int switchIndex,
        string defaultDirectoryName) =>
        switchIndex + 1 < args.Length
            ? args[switchIndex + 1]
            : Path.Combine(Path.GetTempPath(), defaultDirectoryName);

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddDebug();
        });

        services.AddSingleton(ResolveAppPaths());
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ScreenCaptureEngine>();
        services.AddSingleton<WindowCandidateService>();
        services.AddSingleton<WindowTitleService>();
        services.AddSingleton<IScrollInputSink, NativeScrollInputSink>();
        services.AddSingleton(serviceProvider => new CaptureOverlayCoordinator(
            serviceProvider.GetRequiredService<ScreenCaptureEngine>(),
            serviceProvider.GetRequiredService<WindowCandidateService>(),
            serviceProvider.GetRequiredService<ILogger<CaptureOverlayCoordinator>>()));
        services.AddSingleton<NativeMessageWindow>();
        services.AddSingleton(new TrayIconAssets(
            Path.Combine(AppContext.BaseDirectory, "Assets", "tray-idle.ico"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "tray-capturing.ico"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "tray-busy.ico")));
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton<IOcrService, WindowsOcrService>();

        // Launch-at-login through the per-user Run key. The registry adapter is the only
        // Windows-specific piece; the service logic is fully testable against a fake store.
        services.AddSingleton<IRunKeyStore, RegistryRunKeyStore>();
        services.AddSingleton(serviceProvider => new StartupRegistrationService(
            serviceProvider.GetRequiredService<IRunKeyStore>(),
            ResolveExecutablePath()));

        return services.BuildServiceProvider();
    }


    /// <summary>
    /// Resolves the normal per-user storage root, with an explicit process-level override
    /// for portable deployments and isolated integration diagnostics.
    /// </summary>
    private static AppPaths ResolveAppPaths()
    {
        const string dataRootVariable = "MYCAPTURE_DATA_ROOT";
        string? overrideRoot = Environment.GetEnvironmentVariable(dataRootVariable);
        return string.IsNullOrWhiteSpace(overrideRoot)
            ? AppPaths.CreateDefault()
            : AppPaths.CreateForRoot(Path.GetFullPath(overrideRoot));
    }

    /// <summary>
    /// Resolves the executable path the Run key should point at. Prefers the real process
    /// module path so a moved install reconciles to where it now lives.
    /// </summary>
    private static string ResolveExecutablePath()
    {
        string? module = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(module))
        {
            return module;
        }

        // Fall back to the packaged host next to the app base directory.
        return Path.Combine(AppContext.BaseDirectory, "MyCapture.exe");
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.LogError(e.Exception, "Unhandled exception on the dispatcher thread");

        // A capture tool that dies mid-annotation loses the user's work. Failing soft
        // keeps the tray alive so the queue and any remaining windows survive.
        _tray?.SetState(TrayIconState.Error);
        MessageBox.Show(
            $"예기치 않은 오류가 발생했습니다.\n\n{e.Exception.Message}",
            "MyCapture",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.LogInformation(
            "MyCapture shutting down with code {ExitCode}",
            e.ApplicationExitCode);

        // Close the gallery for real: OnClosing otherwise cancels the close and hides it.
        try
        {
            _galleryWindow?.CloseForExit();
        }
        catch (InvalidOperationException)
        {
            // Window already torn down; nothing to do.
        }

        // Close the settings window for real: like the gallery, a normal close only hides it.
        try
        {
            _settingsWindow?.CloseForExit();
        }
        catch (InvalidOperationException)
        {
            // Window already torn down; nothing to do.
        }

        // Close every pinned window so no top-most orphan survives the process.
        try
        {
            _pins?.CloseAll();
        }
        catch (InvalidOperationException)
        {
            // A pin already torn down; nothing to do.
        }

        // Cancel any in-flight OCR and close the shared result window.
        try
        {
            _ocrPresenter?.Dispose();
        }
        catch (InvalidOperationException)
        {
            // Window already torn down; nothing to do.
        }

        // Persist the index once more so any in-memory-only state (a final byte-count update)
        // is on disk. Every mutating operation already saves, so this is belt-and-braces.
        try
        {
            _queue?.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Could not save the capture index on exit");
        }

        _activationCancellation?.Cancel();
        _activationSignal?.Set();
        _activationCancellation?.Dispose();
        _activationSignal?.Dispose();

        // Cancel and release any in-flight scrolling capture so a shutdown mid-scroll does not
        // leak the token source.
        _scrollCancellation?.Cancel();
        _scrollCancellation?.Dispose();

        _services?.Dispose();

        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owner; nothing to release.
            }

            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}
