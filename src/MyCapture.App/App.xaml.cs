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
using MyCapture.Core.Diagnostics;
using MyCapture.Core.Queue;
using MyCapture.Core.Capture;
using MyCapture.Core.Privacy;
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
    private RegisteredWaitHandle? _activationWait;
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
    private DispatcherTimer? _historyMaintenance;
    private OcrIndexingService? _automaticIndexer;
    private readonly CancellationTokenSource _indexingCancellation = new();
    private bool _indexingRunning;
    private bool _indexingRequested;
    private CapturePersistenceService? _persistence;
    private CaptureCommitService? _commit;
    private MyCapture.App.Recording.VideoLibraryService? _videoLibrary;
    private AppPaths? _capturePaths;
    private GalleryController? _galleryController;
    private GalleryReeditLoader? _reeditLoader;
    private GalleryWindow? _galleryWindow;
    private PinManager? _pins;
    private IOcrService? _ocrService;
    private OcrResultPresenter? _ocrPresenter;
    private IPrivacyRedactionService? _privacyRedactionService;
    private SettingsWindow? _settingsWindow;
    private StartupRegistrationService? _startupService;
    private SettingsApplyService? _settingsApply;
    private bool _pasteToScreenInFlight;

    /// <summary>The record persisted for the capture currently being edited, if any.</summary>
    private CaptureRecord? _currentRecord;
    private CaptureEditSession? _currentEditSession;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        UiText.Configure(null);

        // Windows 11 is the supported baseline. The installer refuses older hosts, but the
        // portable ZIP does not run the installer, so the process enforces the floor itself
        // before any capture or diagnostics code touches Windows 11-era APIs.
        if (HostRequirementGate.BlockUnsupportedHost(
            Environment.OSVersion.Version,
            message => MessageBox.Show(message, "MyCapture", MessageBoxButton.OK, MessageBoxImage.Error),
            Shutdown))
        {
            return;
        }

        // Diagnostics intentionally run before the ownership gate. Capture hardware
        // can be tested beside a normal resident instance; the shell test will report
        // hotkey conflicts if the normal instance already owns them.
        if (TryRunSelfTest(e.Args))
        {
            return;
        }

        // Publish the signal before claiming ownership: a second launch can queue
        // activation even while the first process is loading a large library.
        _activationSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            SingleInstanceMutexName,
            out bool isFirstInstance);

        if (!isFirstInstance)
        {
            if (FindSwitch(e.Args, StartupRegistrationService.BackgroundSwitch) < 0)
            {
                NativeMessageWindow.AllowResidentForegroundActivation();
                _activationSignal.Set();
            }
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            _services = BuildServiceProvider();
            _log = _services.GetRequiredService<ILogger<App>>();
            _log.LogInformation("MyCapture starting up");
            InitializeShell();
            StartActivationListener();
        }
        catch (Exception ex)
        {
            _log?.LogCritical(ex, "Could not initialize the resident shell");
            MessageBox.Show(
                UiText.Format("Text_CA6DE730BB6E", ex.Message),
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
        else if (FindSwitch(e.Args, StartupRegistrationService.BackgroundSwitch) < 0)
        {
            _ = Dispatcher.BeginInvoke(new Action(HandleGalleryRequested));
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
        UiText.Configure(_settings.General.Language);

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

        var pinSaveService = new PinImageSaveService(
            () => _settings!,
            () => _capturePaths ?? _services.GetRequiredService<AppPaths>(),
            _services.GetRequiredService<ILogger<PinImageSaveService>>());
        _pins = new PinManager(
            () => _settings!.Pin,
            pinSaveService,
            _services.GetRequiredService<ILogger<PinManager>>());

        _ocrService = _services.GetRequiredService<IOcrService>();
        _automaticIndexer = new OcrIndexingService(
            _galleryController!, _ocrService, record => _queue!.GetDirectory(record),
            () => _settings!.Ocr, _services.GetRequiredService<ILogger<OcrIndexingService>>(), Dispatcher);
        _persistence!.ImagePersisted += (_, _) =>
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                MaintainHistory();
                RunAutomaticIndexing();
            }));
        _historyMaintenance = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher)
        {
            Interval = TimeSpan.FromHours(1),
        };
        _historyMaintenance.Tick += (_, _) => MaintainHistory();
        _historyMaintenance.Start();
        MaintainHistory();
        RequestAutomaticIndexing();
        _ocrPresenter = new OcrResultPresenter(
            _ocrService,
            Dispatcher,
            SetOcrBusy,
            _services.GetRequiredService<ILogger<OcrResultPresenter>>());
        _privacyRedactionService = new PrivacyRedactionService(
            _ocrService,
            new PrivacyDetector(),
            () => _settings!.Ocr);
        _overlay.PrivacyRedactionService = _privacyRedactionService;

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
        _overlay.SelectionPersistRequested = OnCaptureSelectionCompletedAsync;
        _overlay.EditingCompleted += OnAnnotationEditingCompleted;
        _overlay.OverlayClosed += (_, _) =>
        {
            _currentEditSession?.Dispose();
            _currentRecord = null;
            _currentEditSession = null;
            RestoreTrayAfterCapture();
        };
        _overlay.CommitRequested = HandleCommitAsync;
        _overlay.RequiresCaptureExclusion = () => _recorder?.RequiresCaptureExclusion == true;
        _overlay.TransitionFailed += exception =>
            _tray?.ShowBalloon(UiText.Get("Text_B4B8EE7BE3D6"), exception.Message, TrayBalloonKind.Error);

        // Region video recording (Ctrl+Shift+X). Shares the capture engine and, on a
        // frame-image edit, the same persistence/commit path as still capture so recordings
        // inherit the gallery, layer-preserving re-edit and offline story unchanged.
        _recorder = new MyCapture.App.Recording.RegionRecordingCoordinator(
            _services.GetRequiredService<ScreenCaptureEngine>(),
            _capturePaths ?? _services.GetRequiredService<AppPaths>(),
            () => _settings!.Recording,
            _services.GetRequiredService<ILoggerFactory>(),
            _videoLibrary ?? throw new InvalidOperationException("Video library is unavailable."));
        _recorder.FrameImageCaptured += OnRecordedFrameImageCaptured;
        _recorder.FrameImageCommitHandlerFactory = CreateRecordedFrameCommitHandler;
        _recorder.PrivacyRedactionService = _privacyRedactionService;
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
                : UiText.Format("Text_646F2DBF1852", _hotkeys.Failures.Count - 1);

            _tray.ShowBalloon(
                UiText.Get("Text_5564316AA7EB"),
                UiText.Format("Text_114E1105C3EE", first.Hotkey, suffix),
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
            case GlobalHotkeyCommand.OpenLibrary:
                _log?.LogInformation("Library hotkey requested");
                HandleGalleryRequested();
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
    /// Pins a clipboard image, text selection, or spreadsheet range to the screen, or shows a
    /// concise tray balloon when there is no supported content or the clipboard is momentarily
    /// busy. Never throws into the message pump: a pin failure must not take down the resident
    /// process.
    /// </summary>
    private async void HandlePasteToScreen()
    {
        if (_pins is null || _pasteToScreenInFlight)
        {
            return;
        }

        _pasteToScreenInFlight = true;
        try
        {
            PasteResult result = await _pins.PasteFromClipboardAsync();
            switch (result)
            {
                case PasteResult.NoSupportedContent:
                    _tray?.ShowBalloon(
                        UiText.Get("Text_5B8BBBA7AA27"),
                        UiText.Get("Text_5260EE77B969"),
                        TrayBalloonKind.Information,
                        playSound: false);
                    break;
                case PasteResult.ClipboardBusy:
                    _tray?.ShowBalloon(
                        UiText.Get("Text_C5B654A9AE39"),
                        UiText.Get("Text_E68076415933"),
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
                UiText.Get("Text_CE57D5CA2394"),
                ex.Message,
                TrayBalloonKind.Error,
                playSound: false);
        }
        finally
        {
            _pasteToScreenInFlight = false;
        }
    }

    private void HandleCaptureRequested()
    {
        _log?.LogInformation("Region capture requested");
        if (_overlay is null || _settings is null || !GuardStillCapture(UiText.Get("Text_096A13A757DB")))
        {
            return;
        }

        try
        {
            _tray?.SetState(TrayIconState.Capturing);
            // Start reserves the session immediately; full-desktop acquisition runs off the UI
            // thread and reports asynchronous failures through TransitionFailed on this dispatcher.
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
                UiText.Get("Text_70EECBC7C211"),
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
        if (_advancedCapture is null || !GuardStillCapture(UiText.Get("Text_254C836B9ED2")))
        {
            return;
        }

        _tray?.SetState(TrayIconState.Capturing);
        ReportOutcome(_advancedCapture.CaptureFullScreen(), UiText.Get("Text_254C836B9ED2"));
    }

    /// <summary>
    /// Window capture: hit-tests the window under the cursor, crops it out of the monitor
    /// frame, and opens the editor. A missing window is reported, not thrown.
    /// </summary>
    private void HandleCaptureWindow()
    {
        if (_advancedCapture is null || !GuardStillCapture(UiText.Get("Text_36458FABBDBB")))
        {
            return;
        }

        _tray?.SetState(TrayIconState.Capturing);
        ReportOutcome(_advancedCapture.CaptureWindow(), UiText.Get("Text_36458FABBDBB"));
    }

    /// <summary>
    /// Repeat-last-region: replays the most recent confirmed region with no overlay. Shows a
    /// balloon when there is no region to repeat yet.
    /// </summary>
    private void HandleRepeatLastRegion()
    {
        if (_advancedCapture is null || !GuardStillCapture(UiText.Get("Text_92E815063265")))
        {
            return;
        }

        _tray?.SetState(TrayIconState.Capturing);
        ReportOutcome(_advancedCapture.RepeatLastRegion(), UiText.Get("Text_92E815063265"));
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
        if (_settings is null || !GuardStillCapture(UiText.Get("Text_38476EDDA50B")))
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
                UiText.Get("Text_07E499DBBB2A"),
                UiText.Get("Text_70534261ED6C"),
                TrayBalloonKind.Information,
                playSound: false);
        }

        countdown.Elapsed += OnElapsed;
        countdown.Cancelled += OnCancelled;
        countdown.Closed += (_, _) => { if (_activeCountdown == countdown) { Cleanup(); RestoreTrayAfterCapture(); } };
        if (_recorder?.RequiresCaptureExclusion == true && !CaptureWindowExclusion.TryApply(countdown))
        {
            countdown.Close();
            _tray?.ShowBalloon(UiText.Get("Text_40F24016E78F"), UiText.Get("Text_773C24F0A693"), TrayBalloonKind.Error);
            return;
        }
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
            try
            {
                _scrollCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            _tray?.ShowBalloon(
                UiText.Get("Text_6EB131FB1810"),
                UiText.Get("Text_F018AA1A2813"),
                TrayBalloonKind.Information,
                playSound: false);
            return;
        }

        if (!GuardStillCapture(UiText.Get("Text_4B4D91FFC215")))
        {
            return;
        }

        Core.Primitives.PointD cursor = CursorLocator.GetPosition();
        WindowUnderCursor? window =
            _services?.GetRequiredService<WindowTitleService>().ResolveAt(cursor);

        if (window is null || window.ScrollBounds.IsEmpty)
        {
            ReportOutcome(
                CaptureOutcome.NothingToCapture(UiText.Get("Text_A57954F341E2")),
                UiText.Get("Text_4B4D91FFC215"));
            return;
        }

        var cancellation = new CancellationTokenSource();
        _scrollCancellation = cancellation;
        _tray?.SetScrollingCaptureActive(true);
        _tray?.SetState(TrayIconState.Busy);
        _tray?.ShowBalloon(
            UiText.Get("Text_6182AA2DFF8D"),
            UiText.Get("Text_82B423E5AFB8"),
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
            CancellationTokenSource? current = Interlocked.CompareExchange(ref _scrollCancellation, null, cancellation);
            if (ReferenceEquals(current, cancellation))
            {
                cancellation.Dispose();
            }

            _tray?.SetScrollingCaptureActive(false);
        }

        ReportOutcome(outcome, UiText.Get("Text_4B4D91FFC215"));
    }

    private bool GuardStillCapture(string mode)
    {
        if (_overlay?.IsActive == true || _activeCountdown is not null || _scrollCancellation is not null)
            return false;
        if (_recorder?.IsActive != true || _recorder.CanCaptureStill)
            return true;

        _tray?.ShowBalloon(
            mode,
            UiText.Get("Text_90290F46A7D5"),
            TrayBalloonKind.Information,
            playSound: false);
        return false;
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
                    string.IsNullOrEmpty(outcome.Message) ? UiText.Get("Text_AD101D75FFAE") : outcome.Message,
                    TrayBalloonKind.Information,
                    playSound: false);
                break;

            case CaptureOutcomeKind.Failed:
                _tray?.SetState(TrayIconState.Error);
                _tray?.ShowBalloon(
                    UiText.Format("Text_60E0184FCBE5", mode),
                    outcome.Message,
                    TrayBalloonKind.Error);
                break;
        }
    }

    private async Task OnCaptureSelectionCompletedAsync(CaptureSelectionCompletedEventArgs e)
    {
        _log?.LogInformation(
            "Capture selection ready for editor: {Width}x{Height}",
            e.SelectedBitmap.PixelWidth,
            e.SelectedBitmap.PixelHeight);

        // Persist the untouched selection before editing continues so the capture survives a
        // crash or abandon, while encoding and disk flushes run off the UI dispatcher.
        if (_persistence is null)
        {
            return;
        }

        // Start the exact-PNG clipboard work as soon as the explicit region is frozen. It is
        // safe to run beside persistence because SelectedBitmap is frozen, and awaiting both
        // below keeps transition latency near the slower operation instead of their sum.
        Task<bool>? automaticClipboardCopy = e.CopyToClipboardImmediately && _commit is not null
            ? _commit.CopyCapturedRegionAsync(e.SelectedBitmap)
            : null;

        try
        {
            _currentRecord = await _persistence.PersistOriginalAsync(
                e.SelectedBitmap,
                e.Frame.DpiScale,
                sourceWindowTitle: e.SourceTitle,
                sourceMonitor: e.Frame.Monitor?.DeviceName ?? string.Empty);
            _currentEditSession = _commit?.BeginEditSession(_currentRecord);

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
                MonitorInfo? monitor = e.Frame.Monitor
                    ?? MonitorEnumerator.GetAll().FirstOrDefault(candidate =>
                        screenRegion.Left >= candidate.Bounds.Left
                        && screenRegion.Top >= candidate.Bounds.Top
                        && screenRegion.Right <= candidate.Bounds.Right
                        && screenRegion.Bottom <= candidate.Bounds.Bottom);
                _lastRegions.Record(monitor is null
                    ? RegionHistoryEntry.Legacy(screenRegion)
                    : new RegionHistoryEntry(
                        screenRegion,
                        monitor.DeviceName,
                        monitor.Bounds,
                        monitor.Dpi));
            }

            _tray?.SetCaptureCount(_queue?.Count ?? 0);
        }
        catch (Exception ex)
        {
            _currentEditSession?.Dispose();
            _currentRecord = null;
            _currentEditSession = null;
            _log?.LogError(ex, "Could not persist the captured original");
            _tray?.ShowBalloon(
                UiText.Get("Text_4033027EAEDB"),
                ex.Message,
                TrayBalloonKind.Error);
        }

        // Copy the untouched explicit-region selection now, before the editor can be cancelled
        // or committed with any action, and do not consult the quick-save clipboard preference.
        // Advanced capture modes opt out when they synthesize their editor selection.
        if (e.CopyToClipboardImmediately)
        {
            bool copied = false;
            try
            {
                copied = automaticClipboardCopy is not null
                         && await automaticClipboardCopy;
            }
            catch (Exception ex)
            {
                // The shared clipboard service normally converts OLE/encoding failures to a
                // false result. Keep this final boundary so an unexpected integration failure
                // still cannot unwind the already durable capture or suppress the editor.
                _log?.LogWarning(ex, "Automatic region-capture clipboard copy failed unexpectedly");
            }

            if (!copied)
            {
                string message = _currentRecord is null
                    ? UiText.Get("Text_4048C8E18C61")
                    : UiText.Get("Text_9E0860CEAEC2");
                try
                {
                    _tray?.ShowBalloon(
                        UiText.Get("Text_422F61DDCB7C"),
                        message,
                        TrayBalloonKind.Warning,
                        playSound: false);
                }
                catch (Exception ex)
                {
                    // A shell-notification failure must not suppress the editor after the
                    // durable capture and clipboard attempt have already completed.
                    _log?.LogWarning(ex, "Could not show automatic clipboard failure notification");
                }
            }

            // Capturing opens the editor and copies the untouched selection, but does not also
            // create a persistent floating window. F3 remains the explicit paste-to-screen
            // action when the user actually wants a pin.
        }
    }

    /// <summary>
    /// Performs an editor commit: flatten, persist, and any clipboard/export the action
    /// requires. Returns whether the editor should close.
    /// </summary>
    private async Task<bool> HandleCommitAsync(AnnotationEditingResult result)
    {
        if (_commit is null)
        {
            return false;
        }

        try
        {
            bool shouldClose = await _commit.CommitAsync(_currentRecord, result, _currentEditSession);
            if (shouldClose)
            {
                _tray?.SetCaptureCount(_queue?.Count ?? 0);
            }

            return shouldClose;
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Could not finalise the annotated capture");
            _tray?.ShowBalloon(
                UiText.Get("Text_C1A40373A077"),
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

        // The commit itself (flatten/persist/clipboard/export) completed before the editor
        // closed. Nothing remains except to release the in-flight record.
        _currentEditSession?.Dispose();
        _currentRecord = null;
        _currentEditSession = null;
    }

    /// <summary>
    /// Region video recording entry point. Toggles: a running recording is stopped, otherwise
    /// a new region is chosen. Never throws into the message pump.
    /// </summary>
    private void HandleRecordRegion()
    {
        if (_recorder is null || (!_recorder.IsActive && (_overlay?.IsActive == true || _activeCountdown is not null || _scrollCancellation is not null)))
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
                UiText.Get("Text_48C3B6EBA637"),
                ex.Message,
                TrayBalloonKind.Error);
        }
    }

    /// <summary>
    /// Persists a still image the user extracted and annotated from a recorded frame, reusing
    /// the exact capture persistence/commit path so it lands in the gallery as a first-class
    /// capture with a preserved layer document.
    /// </summary>
    private MyCapture.App.Recording.FrameImageCommitSession CreateRecordedFrameCommitHandler()
    {
        CaptureRecord? record = null;
        CaptureEditSession? editSession = null;

        async Task<bool> CommitAsync(AnnotationEditingResult result)
        {
            if (_persistence is null || _commit is null)
            {
                return false;
            }

            if (record is null)
            {
                try
                {
                    record = await _persistence.PersistOriginalAsync(
                        result.SelectedBitmap,
                        result.Frame.DpiScale,
                        sourceWindowTitle: UiText.Get("Text_8D723235CBF3"),
                        sourceMonitor: result.Frame.Monitor?.DeviceName ?? string.Empty);
                    editSession = _commit.BeginEditSession(record);
                }
                catch (Exception ex)
                {
                    _log?.LogError(ex, "Could not persist the original image from a recorded frame");
                    _tray?.ShowBalloon(
                        UiText.Get("Text_EB29C2038682"),
                        UiText.Get("Text_7C7E6C9D8CD7"),
                        TrayBalloonKind.Error);
                }
            }

            try
            {
                bool shouldClose = await _commit.CommitAsync(record, result, editSession);
                if (shouldClose)
                {
                    _tray?.SetCaptureCount(_queue?.Count ?? 0);
                }

                return shouldClose;
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "Could not commit an image edited from a recorded frame");
                _tray?.ShowBalloon(
                    UiText.Get("Text_E87551A1A39F"),
                    ex.Message,
                    TrayBalloonKind.Error);
                return false;
            }
        }

        return new MyCapture.App.Recording.FrameImageCommitSession(
            CommitAsync,
            () =>
            {
                editSession?.Dispose();
                editSession = null;
            });
    }

    private void OnRecordedFrameImageCaptured(
        object? sender,
        MyCapture.App.Recording.AnnotationFrameCapturedEventArgs e)
    {
        _log?.LogInformation(
            "Recorded-frame image commit completed ({Action}, {Width}x{Height})",
            e.Result.Action,
            e.Result.SelectedBitmap.PixelWidth,
            e.Result.SelectedBitmap.PixelHeight);
        _tray?.SetCaptureCount(_queue?.Count ?? 0);
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
        using IDisposable startupEviction = queue.SuspendEviction();

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
        _videoLibrary = new MyCapture.App.Recording.VideoLibraryService(
            queue,
            paths,
            _services.GetRequiredService<ILogger<MyCapture.App.Recording.VideoLibraryService>>());
        _videoLibrary.VideoAdded += (_, _) =>
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _tray?.SetCaptureCount(_queue?.Count ?? 0);
                _galleryWindow?.RefreshFromQueue();
            }));
        _videoLibrary.VideoUpdated += (_, record) =>
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                _galleryWindow?.RefreshFromQueue(record.Id)));

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
                    "Deleted evicted capture directory {Directory} ({Reason})",
                    LogText.SingleLine(directory),
                    e.Reason);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the index entry is what enforces the cap; reclaiming the bytes can wait.
            // The orphaned directory is harmless — it is not re-indexed on a normal load — and
            // can be cleaned up by a later maintenance pass.
            _log?.LogWarning(
                ex,
                "Could not delete evicted capture directory {Directory}",
                LogText.SingleLine(directory));
        }
    }

    private void MaintainHistory()
    {
        try
        {
            if (_queue?.ExpireHistory(DateTimeOffset.UtcNow) > 0)
            {
                _galleryWindow?.RefreshFromQueue();
                _tray?.SetCaptureCount(_queue.Count);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Could not finish managed history retention");
        }
    }

    private void RequestAutomaticIndexing()
    {
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(RunAutomaticIndexing));
    }

    private async void RunAutomaticIndexing()
    {
        _indexingRequested = true;
        if (_indexingRunning || _automaticIndexer is null || _indexingCancellation.IsCancellationRequested)
        {
            return;
        }
        _indexingRunning = true;
        try
        {
            while (_indexingRequested && !_indexingCancellation.IsCancellationRequested)
            {
                _indexingRequested = false;
                await _automaticIndexer.IndexMissingAsync(cancellationToken: _indexingCancellation.Token);
                _galleryWindow?.RefreshFromQueue();
            }
        }
        catch (Exception ex)
        {
            // Automatic indexing must never interrupt capture or open an OCR dialog.
            _log?.LogWarning(ex, "Automatic capture indexing did not complete");
        }
        finally
        {
            _indexingRunning = false;
        }
    }

    private void RestoreTrayAfterCapture()
    {
        if (_hotkeys?.Failures.Count > 0)
        {
            _tray?.SetState(TrayIconState.Error);
        }
        else if (_recorder?.IsActive == true)
        {
            _tray?.SetState(TrayIconState.Capturing);
        }
        else
        {
            _tray?.SetState(TrayIconState.Idle);
        }
    }

    /// <summary>Copies pin text directly without opening the OCR result window.</summary>
    private async void OnPinOcrRequested(object? sender, System.Windows.Media.Imaging.BitmapSource image)
    {
        bool copied = false;
        try
        {
            if (_ocrService is not null)
            {
                copied = await PinTextCopyService.CopyAsync(image, _ocrService,
                    ClipboardImageService.CopyTextAsync, _settings?.Ocr.UpscaleFactor ?? 2.0,
                    _settings?.Ocr.PreferredLanguages ?? []);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Could not copy text from pinned image");
        }
        finally
        {
            if (sender is PinWindow pin) pin.ReportOriginalTextCopyResult(copied);
        }
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
            || _videoLibrary is null
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

        OcrIndexingService ocrIndexing = _automaticIndexer
            ?? throw new InvalidOperationException("Capture indexing is unavailable.");

        var window = new GalleryWindow(
            viewModel,
            _galleryController,
            _reeditLoader,
            _commit,
            _queue,
            _videoLibrary,
            paths,
            _services.GetRequiredService<ILoggerFactory>(),
            _ocrPresenter!,
            () => _settings!.Ocr,
            ocrIndexing,
            _privacyRedactionService
                ?? throw new InvalidOperationException("Privacy redaction is unavailable."),
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

        window.CanExitForUpdate = () =>
            _recorder?.IsActive != true && _overlay?.IsActive != true &&
            _activeCountdown is null && _scrollCancellation is null &&
            !_pasteToScreenInFlight && _currentEditSession is null &&
            !Windows.OfType<Window>().Any(w => w is MyCapture.App.Recording.VideoEditorWindow or AnnotationEditorWindow);
        window.ExitForUpdate = () => Shutdown(0);

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
        _activationWait = ThreadPool.RegisterWaitForSingleObject(_activationSignal!, (_, _) =>
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                _ = Dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new Action(HandleGalleryRequested));
            }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private bool TryRunSelfTest(string[] args)
    {
        int uxIndex = FindSwitch(args, UxReviewSelfTest.CommandLineSwitch);
        if (uxIndex >= 0)
        {
            int exitCode;
            try
            {
                exitCode = UxReviewSelfTest.Run();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("UX review failed: {0}", ex);
                exitCode = 2;
            }
            Shutdown(exitCode);
            return true;
        }

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

        int pinStorageIndex = FindSwitch(args, PinStoragePerformanceSelfTest.CommandLineSwitch);
        if (pinStorageIndex >= 0)
        {
            string outputDirectory = OutputDirectoryAfter(args, pinStorageIndex, "mycapture-pin-storage-performance");
            return RunSelfTest(outputDirectory, "pin-storage-performance-report.txt", PinStoragePerformanceSelfTest.Run);
        }

        int recordingPerformanceIndex = FindSwitch(args, RecordingPerformanceSelfTest.CommandLineSwitch);
        if (recordingPerformanceIndex >= 0)
        {
            string outputDirectory = OutputDirectoryAfter(args, recordingPerformanceIndex, "mycapture-recording-performance");
            return RunSelfTest(outputDirectory, "recording-performance-report.txt", RecordingPerformanceSelfTest.Run);
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

        int localizationIndex = FindSwitch(args, LocalizationSelfTest.CommandLineSwitch);
        if (localizationIndex >= 0)
        {
            return RunSelfTest(
                OutputDirectoryAfter(args, localizationIndex, "mycapture-localization-selftest"),
                "localization-selftest-report.txt",
                LocalizationSelfTest.Run);
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
            Path.Combine(AppContext.BaseDirectory, "Assets", "tray-busy.ico"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "tray-error.ico")));
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
            UiText.Format("Text_6A4B9316D8C2", e.Exception.Message),
            "MyCapture",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Invalidate pending frame acquisition before tearing down tray/persistence services.
        // Native capture cannot be interrupted, but its late result must never open a window.
        _overlay?.Dispose();
        _historyMaintenance?.Stop();
        _indexingCancellation.Cancel();
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

        _activationWait?.Unregister(null);
        _activationSignal?.Dispose();

        // Cancel any in-flight scrolling capture. The handler disposes the token source.
        CancellationTokenSource? scrolling = Interlocked.Exchange(ref _scrollCancellation, null);
        try
        {
            scrolling?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The capture handler already finished and disposed the source.
        }

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
