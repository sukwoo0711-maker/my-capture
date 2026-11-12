using System.IO;
using System.Windows;
using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
using MyCapture.App.Threading;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// Owns the region-recording session: pick a region (reusing the capture overlay),
/// let the user reposition and start it, record on a background thread, then open the
/// video editor. Deliberately parallels <c>CaptureOverlayCoordinator</c> so recording
/// feels like a sibling of capture rather than a bolt-on.
/// </summary>
internal sealed class RegionRecordingCoordinator
{
    private readonly ScreenCaptureEngine _captureEngine;
    private readonly AppPaths _paths;
    private readonly Func<RecordingSettings> _settings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RegionRecordingCoordinator> _log;
    private readonly VideoLibraryService _videoLibrary;

    private CaptureOverlayWindow? _selectionOverlay;
    private NativeCaptureOverlay? _nativeSelectionOverlay;
    private bool _nativeSelectionCompleted;
    private SelectionPreparation? _nativeSelectionContext;
    private RecordingControlWindow? _controls;
    private VideoEditorWindow? _editor;
    private VideoCaptureWriteSession? _writeSession;
    private VideoEditSession? _videoEditSession;

    // Set the moment a stop is requested and held until the editor has opened (or the
    // session has fully ended). Without it, a second Ctrl+Shift+X arriving during the
    // brief stop→finalise→editor transition — when _controls may already be null but the
    // editor not yet shown — would fall through to StartRegionSelection() and begin a NEW
    // recording from 0. This flag closes that race deterministically.
    private bool _finishing;
    private bool _completionInProgress;
    private RecordingOpenLatency? _openLatency;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private SelectionPreparation? _selectionPreparation;
    internal Func<FrozenFrame>? AcquireSelectionFrame { get; set; }
    internal Func<RectD> SelectionDesktopBounds { get; set; } = MonitorEnumerator.GetVirtualDesktopBounds;
    internal Func<bool> ShowSelectionGrid { get; set; } = () => true;
    internal Func<Window, bool> ExcludeSelectionWindow { get; set; } = CaptureWindowExclusion.TryApply;
    internal Func<IntPtr, bool> ExcludeNativeSelectionWindow { get; set; } = CaptureWindowExclusion.TryApply;
    internal bool UseNativeDibSelector { get; set; } = true;
    internal NativeSelectorChromeStrategy NativeChromeStrategy { get; set; } = NativeSelectorChromeStrategy.Retained;
    internal NativeCaptureOverlay? NativeOverlayForProbe => _nativeSelectionOverlay;
    internal Func<RectD, NativeCaptureOverlay> CreateNativeSelector { get; set; } =
        bounds => new NativeCaptureOverlay(bounds, abortOnFocusLoss: false, showMagnifier: true);
    internal Action<NativeCaptureOverlay> ShowNativeSelector { get; set; } = native => native.Show();
    internal Action<RectD>? OpenControlsForTest { get; set; }
    internal Task LastSelectionPreparation { get; private set; } = Task.CompletedTask;
    internal event Action<Exception>? SelectionPreparationFailed;
    internal Action? CaptureSelectionRequested { get; set; }
    internal Task LastSelectorActionForTest { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Raised when the Ctrl+Shift+X hotkey arrives while the post-stop tail (validation,
    /// thumbnail, library publication) is still running and the hotkey is intentionally
    /// ignored, so the host can surface visible feedback instead of silence.
    /// </summary>
    internal event Action? FinishingBusyFeedback;

    private sealed class SelectionPreparation
    {
        internal volatile bool Cancelled;
        internal Stopwatch Elapsed { get; } = Stopwatch.StartNew();
        internal RectD DesktopBounds { get; set; }
        internal bool ShowSelectionGrid { get; set; }
        internal System.Drawing.Size? PresetSize { get; set; }
        internal bool OffersOtherActions { get; set; }
        internal FrozenFrame? Frame { get; set; }
        internal bool NativeFallbackAttempted { get; set; }
    }

    internal RegionRecordingCoordinator(
        ScreenCaptureEngine captureEngine,
        AppPaths paths,
        Func<RecordingSettings> settings,
        ILoggerFactory loggerFactory,
        VideoLibraryService videoLibrary)
    {
        _captureEngine = captureEngine ?? throw new ArgumentNullException(nameof(captureEngine));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _videoLibrary = videoLibrary ?? throw new ArgumentNullException(nameof(videoLibrary));
        _log = loggerFactory.CreateLogger<RegionRecordingCoordinator>();
    }

    /// <summary>Raised whenever the whole recording session (selection→record→edit) ends.</summary>
    internal event EventHandler? SessionEnded;
    /// <summary>No selector or frame preparation remains, even if recording controls are open.</summary>
    internal event Action? SelectorClosed;

    /// <summary>
    /// Committed edit that yielded a still image the caller should push into the
    /// capture queue, exactly like a normal capture. Carries the annotation result.
    /// </summary>
    internal event EventHandler<AnnotationFrameCapturedEventArgs>? FrameImageCaptured;

    /// <summary>
    /// Creates one commit closure per extracted-frame editor. The closure can cache its queue
    /// record across failed clipboard/export retries without creating duplicates.
    /// </summary>
    internal Func<FrameImageCommitSession>? FrameImageCommitHandlerFactory { get; set; }
    internal VideoFrameCaptureService? FrameCaptureService { get; set; }
    internal Action<Guid>? ShowFrameInLibrary { get; set; }
    internal Action<RecordingWatermarkSettings>? PersistWatermarkSettings { get; set; }
    internal Action<bool, bool>? PersistAudioPreferences { get; set; }

    internal void RefreshAudioPreferences(bool systemEnabled, bool microphoneEnabled) =>
        _controls?.RefreshAudioPreferences(systemEnabled, microphoneEnabled);

    /// <summary>Called with true once a finished recording is in the library, false when the recording or its save failed.</summary>
    internal Action<bool>? RecordingSaved { get; set; }

    // Installed only by the explicit isolated resident long-run diagnostic before Toggle.
    // The grabber snapshots the delegate for the lifetime of that recording.
    internal Action<byte[], int, int, int>? SyntheticRecordingFrame { get; set; }

    // Internal diagnostic/test seam; the ordinary UI exposes neither preferences nor devices.
    internal RecordingAudioSourceFactory? RecordingAudioSourceFactory { get; set; }

    internal bool IsWatermarkLocked => _controls is { CanEditWatermark: false };

    internal void RefreshWatermarkSettings(RecordingWatermarkSettings settings) =>
        _controls?.RefreshWatermarkSettings(settings);

    internal IPrivacyRedactionService? PrivacyRedactionService { get; set; }

    internal bool CanCaptureStill => !_finishing && !_completionInProgress
        && (_controls?.CanCaptureStill == true || (_editor is not null && _controls is null && _selectionOverlay is null && _nativeSelectionOverlay is null));

    // Starting a new still and protecting a window during a stop transition are different decisions.
    internal bool RequiresCaptureExclusion => _controls?.IsRecording == true;

    /// <summary>Finish a recording before app exit; do not dispose an unfinished encoder.</summary>
    internal async Task<bool> PrepareForAppExitAsync()
    {
        _dispatcher.VerifyAccess();
        CancelRegionSelection();
        if (_controls is { IsRecording: true } running)
        {
            _finishing = true;
            running.RequestStop();
        }
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while ((_finishing || _completionInProgress || _controls is not null) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        return !_finishing && !_completionInProgress && _controls is null;
    }

    internal bool IsActive =>
        _selectionPreparation is not null
        || _selectionOverlay is not null
        || _nativeSelectionOverlay is not null
        || _controls is not null
        || _editor is not null
        || _writeSession is not null
        || _finishing;

    internal bool IsSelecting => _selectionPreparation is not null || _selectionOverlay is not null || _nativeSelectionOverlay is not null;

    /// <summary>A completed editor does not occupy the recorder's start/stop slot.</summary>
    internal bool HasRecordingSession => _selectionPreparation is not null || _selectionOverlay is not null
        || _nativeSelectionOverlay is not null
        || _controls is not null || _writeSession is not null || _finishing || _completionInProgress;

    /// <summary>
    /// Entry point for the Ctrl+Shift+X command. If a recording is already running,
    /// the control window stops it (toggle behaviour); otherwise a new region is chosen.
    /// </summary>
    internal void Toggle()
    {
        _dispatcher.VerifyAccess();

        // A stop→finalise→editor transition is in flight: ignore re-triggers so a second
        // hotkey press can never start a brand-new recording from 0 mid-transition. The
        // host raises a notice and the saving bar, while it is still there, comes forward.
        if (_finishing)
        {
            FinishingBusyFeedback?.Invoke();
            ActivateExisting();
            return;
        }

        if (_controls is { IsRecording: true } running)
        {
            _finishing = true;
            running.RequestStop();
            return;
        }

        if (HasRecordingSession)
        {
            ActivateExisting();
            return;
        }

        if (_editor is { IsCaptureBusy: true })
        {
            ActivateExisting();
            return;
        }

        PreserveCompletedEditor();

        StartRegionSelection();
    }

    /// <summary>The old editor keeps its document and edit lease while a new recording starts.</summary>
    private void PreserveCompletedEditor()
    {
        if (_editor is not { } editor) return;
        VideoEditSession? session = _videoEditSession;
        _editor = null;
        _videoEditSession = null;
        editor.Closed -= OnEditorClosed;
        void Release(object? sender, EventArgs e)
        {
            editor.Closed -= Release;
            editor.FrameImageCaptured -= OnFrameImageCaptured;
            session?.Dispose();
        }
        editor.Closed += Release;
    }

    /// <summary>
    /// Entry point for the optional pause hotkey. A running recording is paused or resumed
    /// through the control window's own pause path; otherwise the chord does nothing and
    /// only leaves a log line, because there is nothing to pause.
    /// </summary>
    /// <returns>True when a pause or resume was started.</returns>
    internal bool TogglePause()
    {
        _dispatcher.VerifyAccess();

        if (_finishing || _controls is not { IsRecording: true } running)
        {
            _log.LogInformation("Pause hotkey ignored: no recording is running");
            return false;
        }

        if (!running.TogglePause())
        {
            _log.LogInformation("Pause hotkey ignored: the recording is stopping or a pause change is still applying");
            return false;
        }

        return true;
    }

    /// <summary>Brings the recording's current window (its editor, controls or selector) forward.</summary>
    internal void ActivateExisting()
    {
        if (_editor is null && _controls is null && _nativeSelectionOverlay is { } native)
        {
            native.Activate();
            return;
        }
        Window? active = _editor ?? (Window?)_controls ?? _selectionOverlay;
        if (active is null)
        {
            return;
        }

        if (active.WindowState == WindowState.Minimized)
        {
            active.WindowState = WindowState.Normal;
        }

        _ = active.Activate();
    }

    private void StartRegionSelection()
    {
        var preparation = new SelectionPreparation();
        _selectionPreparation = preparation;
        try
        {
            preparation.DesktopBounds = SelectionDesktopBounds();
            preparation.ShowSelectionGrid = ShowSelectionGrid();
            preparation.OffersOtherActions = CaptureSelectionRequested is not null;
            RecordingSettings settings = _settings();
            if (settings.PresetWidth is { } w && w > 0 && settings.PresetHeight is { } h && h > 0)
                preparation.PresetSize = new System.Drawing.Size(w, h);
            if (UseNativeDibSelector)
            {
                try { CreateNativeSelection(preparation); }
                catch (Exception error) when (!preparation.Cancelled) { FallBackToWpfSelection(preparation, error); }
            }
            else CreateWpfSelection(preparation);
            LastSelectionPreparation = PrepareSelectionAsync(preparation);
        }
        catch (Exception error)
        {
            bool cancelled = preparation.Cancelled;
            preparation.Cancelled = true;
            _selectionOverlay?.Close();
            _nativeSelectionOverlay?.Close();
            _selectionPreparation = null;
            EndSessionIfIdle();
            if (cancelled) return;
            if (preparation.NativeFallbackAttempted) { ReportSelectionFailure(error); return; }
            throw;
        }
    }

    private void CreateWpfSelection(SelectionPreparation preparation)
    {
        var overlay = new CaptureOverlayWindow(preparation.DesktopBounds, abortOnFocusLoss: false, showMagnifier: true,
            showSelectionGrid: preparation.ShowSelectionGrid)
        {
            // Says "record" and takes the recording colour, so it never reads as a screenshot.
            RecordingSelection = true,
            ShowModeBar = true,
            OffersOtherActions = preparation.OffersOtherActions,
            PresetSize = preparation.PresetSize,
        };
        _selectionOverlay = overlay;
        overlay.GeometrySelectionCompleted = OpenControls;
        overlay.SelectionCancelled += OnSelectionCancelled;
        overlay.ActionRequested += OnSelectionActionRequested;
        overlay.Closed += OnSelectionClosed;
        overlay.ContentRendered += (_, _) => _log.LogInformation(
            "Recording selector first rendered after {Elapsed:0.0}ms", preparation.Elapsed.Elapsed.TotalMilliseconds);
        // Both renderers apply exclusion before the hidden window's frame is acquired.
        _ = ExcludeSelectionWindow(overlay);
    }

    private void CreateNativeSelection(SelectionPreparation preparation)
    {
        NativeCaptureOverlay native = CreateNativeSelector(preparation.DesktopBounds);
        _nativeSelectionOverlay = native;
        _nativeSelectionCompleted = false;
        _nativeSelectionContext = preparation;
        native.ChromeStrategy = NativeChromeStrategy;
        native.RecordingSelection = true;
        native.OffersOtherActions = preparation.OffersOtherActions;
        native.PresetSize = preparation.PresetSize;
        native.ShowSelectionGrid = preparation.ShowSelectionGrid;
        native.GeometrySelectionCompleted = OnNativeGeometrySelected;
        native.ActionRequested += OnSelectionActionRequested;
        native.InputReady += OnNativeInputReady;
        native.Failed += OnNativeSelectionFailed;
        // The same hidden-window exclusion boundary as the WPF recording selector.
        native.Prepare(ExcludeNativeSelectionWindow);
        if (!native.IsClosed) native.Closed += OnNativeSelectionClosed;
        else OnNativeSelectionClosed(native, EventArgs.Empty);
    }

    private void ShowNativeSelection(NativeCaptureOverlay native, FrozenFrame frame)
    {
        // Show closes its HWND before rethrowing. Keep that failed startup from ending the
        // capture before the coordinator can give the same frozen frame to WPF.
        native.Closed -= OnNativeSelectionClosed;
        bool completed = false;
        try
        {
            native.AttachFrame(frame);
            if (!native.IsVisible) ShowNativeSelector(native);
            completed = true;
        }
        finally
        {
            if (!native.IsClosed) native.Closed += OnNativeSelectionClosed;
            else if (completed) OnNativeSelectionClosed(native, EventArgs.Empty);
        }
    }

    private void ShowWpfSelection(FrozenFrame frame)
    {
        CaptureOverlayWindow overlay = _selectionOverlay ?? throw new InvalidOperationException("The recording selector is unavailable.");
        overlay.AttachFrame(frame);
        if (!overlay.IsVisible) { overlay.Show(); _ = overlay.Activate(); }
    }

    private void FallBackToWpfSelection(SelectionPreparation preparation, Exception error)
    {
        if (!preparation.NativeFallbackAttempted)
        {
            preparation.NativeFallbackAttempted = true;
            _log.LogWarning(error, "Native recording selector failed; using the WPF selector for this capture");
        }
        if (_nativeSelectionOverlay is { } native)
        {
            preparation.Frame ??= native.Frame;
            DetachNativeSelection(native);
            _nativeSelectionOverlay = null;
            _nativeSelectionContext = null;
            native.Close();
        }
        CreateWpfSelection(preparation);
        if (preparation.Cancelled) return;
        if (preparation.Frame is { } frame) ShowWpfSelection(frame);
    }

    private async Task PrepareSelectionAsync(SelectionPreparation preparation)
    {
        FrozenFrame? frame = null;
        Exception? failure = null;
        try
        {
            // Reserve the session while tray/help windows finish disappearing, as still
            // capture does. Do not wait for dispatcher idle: native acquisition is off-thread.
            await Task.Delay(100).ConfigureAwait(false);
            // The frame is a WPF bitmap; a short-lived STA worker shuts down the Dispatcher it
            // creates, where a thread-pool thread would keep it until the pool retires the thread.
            frame = await StaThreadTask.RunAsync(() =>
            {
                if (preparation.Cancelled) throw new OperationCanceledException();
                FrozenFrame acquired = AcquireSelectionFrame?.Invoke() ?? _captureEngine.CaptureVirtualDesktop(false);
                if (!acquired.Bitmap.IsFrozen) throw new InvalidOperationException("Selection frame must be frozen.");
                return acquired;
            }, "MyCapture recording selection frame").ConfigureAwait(false);
        }
        catch (Exception ex) { failure = ex; }
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_selectionPreparation, preparation)) return;
                try
                {
                    if (preparation.Cancelled || (_nativeSelectionOverlay is null && _selectionOverlay is null)) return;
                    if (failure is not null) throw failure;
                    preparation.Frame = frame!;
                    if (_nativeSelectionOverlay is { } native)
                    {
                        try { ShowNativeSelection(native, frame!); }
                        catch (Exception error) when (!preparation.Cancelled) { FallBackToWpfSelection(preparation, error); }
                    }
                    else ShowWpfSelection(frame!);
                    _log.LogInformation("Recording selection frame attached after {Elapsed:0.0}ms",
                        preparation.Elapsed.Elapsed.TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    bool cancelled = preparation.Cancelled;
                    _nativeSelectionOverlay?.Close();
                    _selectionOverlay?.Close();
                    if (!cancelled) ReportSelectionFailure(ex);
                }
                finally
                {
                    _selectionPreparation = null;
                    EndSessionIfIdle();
                }
            }).Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) { }
        catch (Exception ex) { _log.LogError(ex, "Recording selection dispatcher transition failed"); }
    }

    internal void CancelRegionSelection()
    {
        _dispatcher.VerifyAccess();
        if (_selectionPreparation is { } preparation) preparation.Cancelled = true;
        _selectionOverlay?.Close();
        _nativeSelectionOverlay?.Close();
    }

    private void OnSelectionCancelled(object? sender, EventArgs e) =>
        _log.LogInformation("Recording region selection cancelled");

    private void OnSelectionActionRequested(object? sender, CaptureSelectorRequest request)
    {
        if (request.Action == CaptureSelectorAction.Capture && CaptureSelectionRequested is not null)
        {
            if (sender is NativeCaptureOverlay) _nativeSelectionCompleted = true;
            LastSelectorActionForTest = OpenCaptureAfterSelectionAsync();
        }
    }

    private async Task OpenCaptureAfterSelectionAsync()
    {
        // The overlay raises its action before its finally block closes it. Let that close and
        // any in-flight frozen-frame preparation finish before starting the sibling selector.
        await Dispatcher.Yield(DispatcherPriority.Background);
        await LastSelectionPreparation;
        try { CaptureSelectionRequested?.Invoke(); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not switch from recording selection to capture");
            SelectionPreparationFailed?.Invoke(ex);
        }
    }

    private void OnSelectionClosed(object? sender, EventArgs e)
    {
        if (sender is CaptureOverlayWindow overlay)
        {
            if (_selectionPreparation is { } preparation) preparation.Cancelled = true;
            overlay.GeometrySelectionCompleted = null;
            overlay.SelectionCancelled -= OnSelectionCancelled;
            overlay.ActionRequested -= OnSelectionActionRequested;
            overlay.Closed -= OnSelectionClosed;
            if (ReferenceEquals(_selectionOverlay, overlay))
            {
                _selectionOverlay = null;
            }
        }

        EndSessionIfIdle();
    }

    private void OnNativeGeometrySelected(RectD screenRegion)
    {
        _nativeSelectionCompleted = true;
        OpenControls(screenRegion);
    }

    private void OnNativeInputReady(object? sender, EventArgs e)
    {
        if (sender is NativeCaptureOverlay native) native.InputReady -= OnNativeInputReady;
        if (_nativeSelectionContext is { } preparation)
            _log.LogInformation("Recording selector input ready after {Elapsed:0.0}ms", preparation.Elapsed.Elapsed.TotalMilliseconds);
    }

    private void OnNativeSelectionFailed(Exception error)
    {
        if (_nativeSelectionContext is not { } preparation || preparation.Cancelled || _nativeSelectionCompleted) return;
        bool preparing = ReferenceEquals(_selectionPreparation, preparation);
        _selectionPreparation = preparation;
        try { FallBackToWpfSelection(preparation, error); }
        catch (Exception fallbackError)
        {
            bool cancelled = preparation.Cancelled;
            _selectionOverlay?.Close();
            if (!cancelled) ReportSelectionFailure(fallbackError);
        }
        finally
        {
            if (!preparing)
            {
                _selectionPreparation = null;
                EndSessionIfIdle();
            }
        }
    }

    private void ReportSelectionFailure(Exception error)
    {
        _log.LogError(error, "Recording selection preparation failed");
        if (SelectionPreparationFailed is { } reportFailure) reportFailure(error);
        else MyCapture.App.Themes.ThemedDialog.Error(null, UiText.Get("Text_25E15E06C2EA"), error);
    }

    private void OnNativeSelectionClosed(object? sender, EventArgs e)
    {
        if (sender is NativeCaptureOverlay native)
        {
            if (_selectionPreparation is { } preparation) preparation.Cancelled = true;
            DetachNativeSelection(native);
            if (ReferenceEquals(_nativeSelectionOverlay, native))
            {
                _nativeSelectionOverlay = null;
                _nativeSelectionContext = null;
                if (!_nativeSelectionCompleted) OnSelectionCancelled(sender, e);
            }
        }
        EndSessionIfIdle();
    }

    private void DetachNativeSelection(NativeCaptureOverlay native)
    {
        native.GeometrySelectionCompleted = null;
        native.ActionRequested -= OnSelectionActionRequested;
        native.InputReady -= OnNativeInputReady;
        native.Failed -= OnNativeSelectionFailed;
        native.Closed -= OnNativeSelectionClosed;
    }

    private void OpenControls(RectD screenRegion)
    {
        if (OpenControlsForTest is { } open) { open(screenRegion); return; }
        RecordingControlWindow? controls = null;
        try
        {
            RecordingSettings settings = _settings();
            _writeSession?.Dispose();
            _writeSession = _videoLibrary.BeginCapture(settings.TargetFps);

            RegionRecorder BuildRecorder()
            {
                // The diagnostic environment switch outranks the user's capture-method setting.
                string? backendOverride = RecordingCaptureBackendSelection.EnvironmentOverrideValue();
                RecordingCaptureBackend backend = RecordingCaptureBackendSelection.Resolve(settings.CaptureMode, backendOverride);
                _log.LogInformation(
                    "Recording capture backend request: {Backend} (setting {Mode}, {Variable}={Override})",
                    backend,
                    RecordingCaptureModeNames.ToSetting(settings.CaptureMode),
                    RecordingCaptureBackendSelection.EnvironmentVariable,
                    RecordingCaptureBackendSelection.ParseOverride(backendOverride)?.ToString() ?? "unset");
                Action<byte[], int, int, int>? synthetic = SyntheticRecordingFrame;
                var grabber = new RegionFrameGrabber(_captureEngine, settings.IncludeCursor, settings.CursorHighlight,
                    backend, _loggerFactory.CreateLogger<RegionFrameGrabber>(), synthetic);
                if (synthetic is not null)
                    _log.LogInformation("Recording input source: synthetic BGRA; native capture backend: none");
                return new RegionRecorder(
                    grabber,
                    options => new MediaFoundationVideoEncoder(
                        options,
                        _loggerFactory.CreateLogger<MediaFoundationVideoEncoder>()),
                    _loggerFactory.CreateLogger<RegionRecorder>(), audioSourceFactory: RecordingAudioSourceFactory);
            }

            controls = new RecordingControlWindow(
                screenRegion,
                settings,
                BuildRecorder,
                () => _writeSession?.StagingOutputPath
                      ?? throw new InvalidOperationException("The pending recording path is unavailable."),
                _loggerFactory.CreateLogger<RecordingControlWindow>());
            controls.WatermarkImageFolder = Path.Combine(_paths.DataRoot, StampStore.FolderName);
            controls.WatermarkSettingsChanged += OnWatermarkSettingsChanged;
            controls.AudioPreferencesChanged += OnAudioPreferencesChanged;
            _controls = controls;
            controls.RecordingFinished += OnRecordingFinished;
            controls.Cancelled += OnControlsCancelled;
            controls.Failed += OnControlsFailed;
            controls.Stopping += OnControlsStopping;
            controls.Closed += OnControlsClosed;
            controls.Show();
            _ = controls.Activate();
        }
        catch (Exception ex)
        {
            if (controls is not null)
            {
                controls.RecordingFinished -= OnRecordingFinished;
                controls.WatermarkSettingsChanged -= OnWatermarkSettingsChanged;
                controls.AudioPreferencesChanged -= OnAudioPreferencesChanged;
                controls.Cancelled -= OnControlsCancelled;
                controls.Failed -= OnControlsFailed;
                controls.Stopping -= OnControlsStopping;
                controls.Closed -= OnControlsClosed;
                if (ReferenceEquals(_controls, controls))
                {
                    _controls = null;
                }
            }

            _writeSession?.Dispose();
            _writeSession = null;
            _finishing = false;
            _log.LogError(ex, "Could not open recording controls or allocate the pending video");
            MyCapture.App.Themes.ThemedDialog.Error(null, UiText.Get("Text_25E15E06C2EA"), ex);
            EndSessionIfIdle();
        }
    }

    private void OnControlsStopping(object? sender, EventArgs e)
    {
        _finishing = true;
        _openLatency = new RecordingOpenLatency(Stopwatch.GetTimestamp());
    }

    private void OnWatermarkSettingsChanged(RecordingWatermarkSettings settings) =>
        PersistWatermarkSettings?.Invoke(settings);

    private void OnAudioPreferencesChanged(bool systemEnabled, bool microphoneEnabled) =>
        PersistAudioPreferences?.Invoke(systemEnabled, microphoneEnabled);

    private void OnControlsCancelled(object? sender, EventArgs e)
    {
        _log.LogInformation("Recording cancelled before or during capture");
        _openLatency = null;
        _writeSession?.Dispose();
        _writeSession = null;
        _completionInProgress = false;
    }

    private void OnControlsFailed(object? sender, RecordingFailedEventArgs e)
    {
        _log.LogError(e.Exception, "Recording stopped without a completed video");
        _openLatency = null;
        _writeSession?.Dispose();
        _writeSession = null;
        _completionInProgress = false;
        _finishing = false;
        ReportRecordingSaved(false);
        MyCapture.App.Themes.ThemedDialog.Error(null, UiText.Get("Text_89CF3D468EC1"), e.Exception);
    }

    private void OnControlsClosed(object? sender, EventArgs e) => ReleaseControls(sender as RecordingControlWindow);

    // The bar stops belonging to the session when it closes, or when Saved has handed over to
    // the editor and only its exit is left.
    private void ReleaseControls(RecordingControlWindow? controls)
    {
        bool finalizedOnClose = false;
        if (controls is not null)
        {
            controls.RecordingFinished -= OnRecordingFinished;
            controls.WatermarkSettingsChanged -= OnWatermarkSettingsChanged;
            controls.AudioPreferencesChanged -= OnAudioPreferencesChanged;
            controls.Cancelled -= OnControlsCancelled;
            controls.Failed -= OnControlsFailed;
            controls.Stopping -= OnControlsStopping;
            controls.Closed -= OnControlsClosed;
            finalizedOnClose = controls.FinalizedOnClose;
            if (ReferenceEquals(_controls, controls))
            {
                _controls = null;
            }
        }

        if (_writeSession is not null && !_completionInProgress)
        {
            if (finalizedOnClose)
            {
                // The controls closed (application exit, session end or a forced close) while
                // recording or paused, and the recorder still finalised the private MP4.
                // Disposing the session would abort and delete that clip. Leave the pending
                // sidecar and the finalised file in place: startup recovery validates and
                // publishes them to the gallery. The session owns no native resources.
                _log.LogWarning(
                    "Recording controls closed before completion; keeping the finalized clip for startup recovery");
            }
            else
            {
                _writeSession.Dispose();
            }

            _writeSession = null;
        }

        // By now either async completion owns the pending session, the editor has opened, or the
        // control ended without a clip and the session was aborted above.
        if (_writeSession is null)
        {
            _finishing = false;
        }

        EndSessionIfIdle();
    }

    /// <summary>Tells the observer whether the finished recording reached the library; it never interrupts the flow.</summary>
    private void ReportRecordingSaved(bool saved)
    {
        try { RecordingSaved?.Invoke(saved); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _log.LogWarning(error, "Could not report the saved recording");
        }
    }

    private async void OnRecordingFinished(object? sender, RecordingResult result)
    {
        RecordingOpenLatency openLatency = _openLatency ?? new RecordingOpenLatency(Stopwatch.GetTimestamp());
        _openLatency = null;
        openLatency.Mark(RecordingOpenStage.Finalize);
        _log.LogInformation(
            "Recording produced {Path} ({Frames}/{ExpectedFrames} frames, {Duration:0}ms, " +
            "dropped {DroppedFrames}, effective {EffectiveFps:0.0}fps)",
            result.OutputPath,
            result.EmittedFrames,
            result.ExpectedFrames,
            result.DurationMs,
            result.DroppedFrames,
            result.EffectiveFps);

        VideoCaptureWriteSession? writeSession = _writeSession;
        if (writeSession is null)
        {
            _log.LogError("Recording completed without a pending video-library session");
            _completionInProgress = false;
            _finishing = false;
            (sender as RecordingControlWindow)?.CompleteAndClose();
            EndSessionIfIdle();
            return;
        }

        _finishing = true;
        _completionInProgress = true;
        // Finalisation already finished (RecordingFinished fires after the encoder join). The
        // bar stays with its saving status while validation, thumbnail and library publication
        // run, so the wait before the editor is never silent; its interior is transparent and
        // passes clicks, so the screen stays usable. A crash or quit mid-tail is recovered from
        // the publish-ready marker at startup. The tail keeps running with its continuations on
        // the dispatcher, exactly like the pre-background flow, because the capture queue's
        // index state is dispatcher-serialised.
        var controls = sender as RecordingControlWindow;
        VideoLibraryItem item;
        try
        {
            item = await _videoLibrary.CompleteCaptureAsync(writeSession, result, openLatency: openLatency);
            writeSession.Dispose();
            _writeSession = null;
            _completionInProgress = false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not persist completed recording in the gallery");
            // Disposing a retained session keeps the finished clip and its recovery sidecar for
            // the next startup; only a clip proven unusable has already been removed.
            Exception shown = DescribeCompletionFailure(writeSession, ex);
            writeSession.Dispose();
            _writeSession = null;
            _completionInProgress = false;
            _finishing = false;
            controls?.CompleteAndClose();
            ReportRecordingSaved(false);
            MyCapture.App.Themes.ThemedDialog.Error(null, UiText.Get("Text_87B145D16453"), shown);
            EndSessionIfIdle();
            return;
        }

        ReportRecordingSaved(true);

        if (controls is not null && ReferenceEquals(_controls, controls))
        {
            controls.ShowSaved();
            try
            {
                await DrawSavedAsync();
            }
            catch (TaskCanceledException) when (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                return;
            }
        }

        try
        {
            VideoEditSession editSession = _videoLibrary.BeginEdit(item.Record);
            _videoEditSession = editSession;
            var editor = new VideoEditorWindow(item.Recording, _paths, _loggerFactory, item.EditDocument)
            {
                FrameCaptureService = FrameCaptureService,
                FrameSourceVideoId = item.Record.Id,
                FrameSourceRevision = item.Record.ContentRevision,
                ShowFrameInLibrary = ShowFrameInLibrary,
                RenderStagingPathFactory = () => _videoLibrary.CreateRenderStagingPath(item.Record),
                VideoCommitHandler = (document, stage, cancellationToken) =>
                    _videoLibrary.CommitEditAsync(
                        item.Record,
                        editSession,
                        document,
                        stage,
                        cancellationToken),
            };
            editor.FrameImageCommitHandlerFactory = FrameImageCommitHandlerFactory;
            editor.PrivacyRedactionService = PrivacyRedactionService;
            _editor = editor;
            // The stop→finalise→editor transition is complete: the editor now anchors the
            // session, so clear the finishing guard.
            _finishing = false;
            editor.FrameImageCaptured += OnFrameImageCaptured;
            editor.Closed += OnEditorClosed;
            ObserveFirstRender(editor, openLatency);
            editor.Show();
            _ = editor.Activate();
            openLatency.Mark(RecordingOpenStage.EditorOpen);
            LogOpenLatency(openLatency, result);
            if (controls is not null)
            {
                // The editor is on screen: the session lets go of the bar now and only its exit
                // is left to play.
                ReleaseControls(controls);
                controls.LeaveAfterSaved();
            }
        }
        catch (Exception ex)
        {
            _videoEditSession?.Dispose();
            _videoEditSession = null;
            _editor = null;
            _finishing = false;
            _log.LogError(ex, "The recording was saved, but its editor could not be opened");
            controls?.CompleteAndClose();
            MyCapture.App.Themes.ThemedDialog.Error(null, UiText.Get("Text_49020661A850"), ex);
            EndSessionIfIdle();
        }
    }

    /// <summary>
    /// What the save-failure dialog explains. When the library kept the finished clip for startup
    /// recovery, the notice says so and when recovery runs; the cause stays in the details.
    /// </summary>
    internal static Exception DescribeCompletionFailure(VideoCaptureWriteSession session, Exception error) =>
        session.IsRetainedForRecovery
            ? MyCapture.App.Themes.UserFacingError.Mark(
                new IOException(UiText.Get("Text_C5AC44062ACA").TrimEnd(), error))
            : error;

    /// <summary>
    /// Lets Saved reach the screen before the editor's construction holds the dispatcher: one
    /// frame is drawn, or 100 ms pass when nothing can draw (a locked session).
    /// </summary>
    private async Task DrawSavedAsync()
    {
        var rendering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRendering(object? sender, EventArgs e) => rendering.TrySetResult();
        System.Windows.Media.CompositionTarget.Rendering += OnRendering;
        try
        {
            await Task.WhenAny(rendering.Task, Task.Delay(100));
        }
        finally
        {
            System.Windows.Media.CompositionTarget.Rendering -= OnRendering;
        }

        // Rendering is raised just before the frame is drawn; this turn comes after it.
        await _dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Logs where the stop-to-editor wait went. Durations only: the clip path and window titles
    /// are deliberately absent so diagnostics can be shared without exposing user content.
    /// </summary>
    private void LogOpenLatency(RecordingOpenLatency latency, RecordingResult result) =>
        _log.LogInformation(
            "Recording open latency: finalize {FinalizeMs:0}ms (MP4 complete {EncoderCompleteMs:0}ms), " +
            "validate {ValidateMs:0}ms, thumbnail {ThumbnailMs:0}ms, publish {PublishMs:0}ms, " +
            "editor {EditorMs:0}ms; stop to editor {TotalMs:0}ms",
            latency[RecordingOpenStage.Finalize],
            result.Performance?.FinalizationMs ?? 0,
            latency[RecordingOpenStage.Validate],
            latency[RecordingOpenStage.Thumbnail],
            latency[RecordingOpenStage.Publish],
            latency[RecordingOpenStage.EditorOpen],
            latency.TotalMs);

    private void ObserveFirstRender(VideoEditorWindow editor, RecordingOpenLatency latency)
    {
        void OnContentRendered(object? sender, EventArgs e)
        {
            editor.ContentRendered -= OnContentRendered;
            latency.Mark(RecordingOpenStage.EditorFirstRender);
            _log.LogInformation(
                "Recording editor first render {FirstRenderMs:0}ms after show; stop to first render {TotalMs:0}ms",
                latency[RecordingOpenStage.EditorFirstRender],
                latency.TotalMs);
        }

        editor.ContentRendered += OnContentRendered;
    }

    private void OnFrameImageCaptured(object? sender, AnnotationFrameCapturedEventArgs e) =>
        FrameImageCaptured?.Invoke(this, e);

    private void OnEditorClosed(object? sender, EventArgs e)
    {
        if (sender is VideoEditorWindow editor)
        {
            editor.FrameImageCaptured -= OnFrameImageCaptured;
            editor.Closed -= OnEditorClosed;
            if (ReferenceEquals(_editor, editor))
            {
                _editor = null;
            }
        }

        _videoEditSession?.Dispose();
        _videoEditSession = null;

        EndSessionIfIdle();
    }

    private void EndSessionIfIdle()
    {
        if (!IsSelecting) SelectorClosed?.Invoke();
        if (!IsActive)
        {
            SessionEnded?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// Carries an annotated still extracted from a recorded frame back to the app so it can
/// be persisted through the normal capture queue.
/// </summary>
internal sealed class AnnotationFrameCapturedEventArgs : EventArgs
{
    internal AnnotationFrameCapturedEventArgs(AnnotationEditingResult result)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    internal AnnotationEditingResult Result { get; }
}

/// <summary>
/// Couples one extracted-frame editor's retryable commit closure with its retention lease.
/// Closing or cancelling the editor disposes the session even when no commit ever succeeds.
/// </summary>
internal sealed class FrameImageCommitSession : IDisposable
{
    private Action? _release;

    internal FrameImageCommitSession(
        Func<AnnotationEditingResult, Task<bool>> commitAsync,
        Action release)
    {
        CommitAsync = commitAsync ?? throw new ArgumentNullException(nameof(commitAsync));
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    internal Func<AnnotationEditingResult, Task<bool>> CommitAsync { get; }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
