using System.IO;
using System.Windows;
using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
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
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private SelectionPreparation? _selectionPreparation;
    internal Func<FrozenFrame>? AcquireSelectionFrame { get; set; }
    internal Func<RectD> SelectionDesktopBounds { get; set; } = MonitorEnumerator.GetVirtualDesktopBounds;
    internal Func<Window, bool> ExcludeSelectionWindow { get; set; } = CaptureWindowExclusion.TryApply;
    internal Task LastSelectionPreparation { get; private set; } = Task.CompletedTask;
    internal event Action<Exception>? SelectionPreparationFailed;

    private sealed class SelectionPreparation
    {
        internal volatile bool Cancelled;
        internal Stopwatch Elapsed { get; } = Stopwatch.StartNew();
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

    internal IPrivacyRedactionService? PrivacyRedactionService { get; set; }

    internal bool CanCaptureStill => !_finishing && !_completionInProgress
        && (_controls?.CanCaptureStill == true || (_editor is not null && _controls is null && _selectionOverlay is null));

    // Starting a new still and protecting a window during a stop transition are different decisions.
    internal bool RequiresCaptureExclusion => _controls?.IsRecording == true;

    internal bool IsActive =>
        _selectionPreparation is not null
        || _selectionOverlay is not null
        || _controls is not null
        || _editor is not null
        || _writeSession is not null
        || _finishing;

    /// <summary>
    /// Entry point for the Ctrl+Shift+X command. If a recording is already running,
    /// the control window stops it (toggle behaviour); otherwise a new region is chosen.
    /// </summary>
    internal void Toggle()
    {
        _dispatcher.VerifyAccess();

        // A stop→finalise→editor transition is in flight: ignore re-triggers so a second
        // hotkey press can never start a brand-new recording from 0 mid-transition.
        if (_finishing)
        {
            ActivateExisting();
            return;
        }

        if (_controls is { IsRecording: true } running)
        {
            _finishing = true;
            running.RequestStop();
            return;
        }

        if (IsActive)
        {
            ActivateExisting();
            return;
        }

        StartRegionSelection();
    }

    private void ActivateExisting()
    {
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
        CaptureOverlayWindow? overlay = null;
        try
        {
            overlay = new CaptureOverlayWindow(SelectionDesktopBounds(), abortOnFocusLoss: false, showMagnifier: true);
            _selectionOverlay = overlay;
            overlay.GeometrySelectionCompleted = OpenControls;
            overlay.SelectionCancelled += OnSelectionCancelled;
            overlay.Closed += OnSelectionClosed;
            overlay.ContentRendered += (_, _) => _log.LogInformation(
                "Recording selector first rendered after {Elapsed:0.0}ms", preparation.Elapsed.Elapsed.TotalMilliseconds);
            if (ExcludeSelectionWindow(overlay))
            {
                overlay.Show();
                _ = overlay.Activate();
            }
            LastSelectionPreparation = PrepareSelectionAsync(preparation, overlay);
        }
        catch
        {
            _selectionPreparation = null;
            overlay?.Close();
            throw;
        }
    }

    private async Task PrepareSelectionAsync(SelectionPreparation preparation, CaptureOverlayWindow overlay)
    {
        FrozenFrame? frame = null;
        Exception? failure = null;
        try
        {
            frame = await Task.Run(() =>
            {
                if (preparation.Cancelled) throw new OperationCanceledException();
                FrozenFrame acquired = AcquireSelectionFrame?.Invoke() ?? _captureEngine.CaptureVirtualDesktop(false);
                if (!acquired.Bitmap.IsFrozen) throw new InvalidOperationException("Selection frame must be frozen.");
                return acquired;
            }).ConfigureAwait(false);
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
                    if (preparation.Cancelled || !ReferenceEquals(_selectionOverlay, overlay)) return;
                    if (failure is not null) throw failure;
                    overlay.AttachFrame(frame!);
                    if (!overlay.IsVisible) { overlay.Show(); _ = overlay.Activate(); }
                    _log.LogInformation("Recording selection frame attached after {Elapsed:0.0}ms",
                        preparation.Elapsed.Elapsed.TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    overlay.Close();
                    _log.LogError(ex, "Recording selection preparation failed");
                    if (SelectionPreparationFailed is { } reportFailure) reportFailure(ex);
                    else MessageBox.Show(UiText.Get("Text_188B0AF9BE23") + ex.Message,
                        UiText.Get("Text_25E15E06C2EA"), MessageBoxButton.OK, MessageBoxImage.Error);
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
    }

    private void OnSelectionCancelled(object? sender, EventArgs e) =>
        _log.LogInformation("Recording region selection cancelled");

    private void OnSelectionClosed(object? sender, EventArgs e)
    {
        if (sender is CaptureOverlayWindow overlay)
        {
            if (_selectionPreparation is { } preparation) preparation.Cancelled = true;
            overlay.GeometrySelectionCompleted = null;
            overlay.SelectionCancelled -= OnSelectionCancelled;
            overlay.Closed -= OnSelectionClosed;
            if (ReferenceEquals(_selectionOverlay, overlay))
            {
                _selectionOverlay = null;
            }
        }

        EndSessionIfIdle();
    }

    private void OpenControls(RectD screenRegion)
    {
        RecordingControlWindow? controls = null;
        try
        {
            RecordingSettings settings = _settings();
            _writeSession?.Dispose();
            _writeSession = _videoLibrary.BeginCapture(settings.TargetFps);

            RegionRecorder BuildRecorder()
            {
                var grabber = new RegionFrameGrabber(_captureEngine, settings.IncludeCursor);
                return new RegionRecorder(
                    grabber,
                    options => new MediaFoundationVideoEncoder(
                        options,
                        _loggerFactory.CreateLogger<MediaFoundationVideoEncoder>()),
                    _loggerFactory.CreateLogger<RegionRecorder>());
            }

            controls = new RecordingControlWindow(
                screenRegion,
                settings,
                BuildRecorder,
                () => _writeSession?.StagingOutputPath
                      ?? throw new InvalidOperationException("The pending recording path is unavailable."),
                _loggerFactory.CreateLogger<RecordingControlWindow>());
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
            MessageBox.Show(
                UiText.Get("Text_188B0AF9BE23") + ex.Message,
                UiText.Get("Text_25E15E06C2EA"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            EndSessionIfIdle();
        }
    }

    private void OnControlsStopping(object? sender, EventArgs e) => _finishing = true;

    private void OnControlsCancelled(object? sender, EventArgs e)
    {
        _log.LogInformation("Recording cancelled before or during capture");
        _writeSession?.Dispose();
        _writeSession = null;
        _completionInProgress = false;
    }

    private void OnControlsFailed(object? sender, RecordingFailedEventArgs e)
    {
        _log.LogError(e.Exception, "Recording stopped without a completed video");
        _writeSession?.Dispose();
        _writeSession = null;
        _completionInProgress = false;
        _finishing = false;
        MessageBox.Show(
            UiText.Get("Text_626745049C0B") + e.Exception.Message,
            UiText.Get("Text_89CF3D468EC1"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnControlsClosed(object? sender, EventArgs e)
    {
        if (sender is RecordingControlWindow controls)
        {
            controls.RecordingFinished -= OnRecordingFinished;
            controls.Cancelled -= OnControlsCancelled;
            controls.Failed -= OnControlsFailed;
            controls.Stopping -= OnControlsStopping;
            controls.Closed -= OnControlsClosed;
            if (ReferenceEquals(_controls, controls))
            {
                _controls = null;
            }
        }

        if (_writeSession is not null && !_completionInProgress)
        {
            _writeSession.Dispose();
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

    private async void OnRecordingFinished(object? sender, RecordingResult result)
    {
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
        (sender as RecordingControlWindow)?.ShowCompletionStatus(UiText.Get("Text_331C366314D0"));
        VideoLibraryItem item;
        try
        {
            item = await _videoLibrary.CompleteCaptureAsync(writeSession, result);
            writeSession.Dispose();
            _writeSession = null;
            _completionInProgress = false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not persist completed recording in the gallery");
            writeSession.Dispose();
            _writeSession = null;
            _completionInProgress = false;
            _finishing = false;
            (sender as RecordingControlWindow)?.CompleteAndClose();
            MessageBox.Show(
                UiText.Get("Text_C5AC44062ACA") + ex.Message,
                UiText.Get("Text_87B145D16453"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            EndSessionIfIdle();
            return;
        }

        try
        {
            VideoEditSession editSession = _videoLibrary.BeginEdit(item.Record);
            _videoEditSession = editSession;
            var editor = new VideoEditorWindow(item.Recording, _paths, _loggerFactory, item.EditDocument)
            {
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
            editor.Show();
            _ = editor.Activate();
            (sender as RecordingControlWindow)?.CompleteAndClose();
        }
        catch (Exception ex)
        {
            _videoEditSession?.Dispose();
            _videoEditSession = null;
            _editor = null;
            _finishing = false;
            _log.LogError(ex, "The recording was saved, but its editor could not be opened");
            (sender as RecordingControlWindow)?.CompleteAndClose();
            MessageBox.Show(
                UiText.Get("Text_BA79DCE6BD5D") + ex.Message,
                UiText.Get("Text_49020661A850"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            EndSessionIfIdle();
        }
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
