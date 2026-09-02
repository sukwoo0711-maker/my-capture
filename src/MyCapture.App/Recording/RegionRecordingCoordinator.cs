using System.IO;
using System.Windows;
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
    // session has fully ended). Without it, a second Ctrl+X arriving during the
    // brief stop→finalise→editor transition — when _controls may already be null but the
    // editor not yet shown — would fall through to StartRegionSelection() and begin a NEW
    // recording from 0. This flag closes that race deterministically.
    private bool _finishing;
    private bool _completionInProgress;

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

    internal bool IsActive =>
        _selectionOverlay is not null
        || _controls is not null
        || _editor is not null
        || _writeSession is not null
        || _finishing;

    /// <summary>
    /// Entry point for the Ctrl+X command. If a recording is already running,
    /// the control window stops it (toggle behaviour); otherwise a new region is chosen.
    /// </summary>
    internal void Toggle()
    {
        Application.Current.Dispatcher.VerifyAccess();

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
        FrozenFrame frame = _captureEngine.CaptureVirtualDesktop(includeCursor: false);

        // Reuse the exact capture region selector. Recording selects an area the same
        // way capture does, so muscle memory transfers.
        var overlay = new CaptureOverlayWindow(frame, abortOnFocusLoss: false, showMagnifier: true);
        _selectionOverlay = overlay;
        overlay.SelectionCompleted += OnRegionChosen;
        overlay.SelectionCancelled += OnSelectionCancelled;
        overlay.Closed += OnSelectionClosed;

        _log.LogInformation(
            "Recording region selector opened across virtual desktop ({Width}x{Height})",
            frame.PixelWidth,
            frame.PixelHeight);
        overlay.Show();
        _ = overlay.Activate();
    }

    private void OnSelectionCancelled(object? sender, EventArgs e) =>
        _log.LogInformation("Recording region selection cancelled");

    private void OnSelectionClosed(object? sender, EventArgs e)
    {
        if (sender is CaptureOverlayWindow overlay)
        {
            overlay.SelectionCompleted -= OnRegionChosen;
            overlay.SelectionCancelled -= OnSelectionCancelled;
            overlay.Closed -= OnSelectionClosed;
            if (ReferenceEquals(_selectionOverlay, overlay))
            {
                _selectionOverlay = null;
            }
        }

        EndSessionIfIdle();
    }

    private void OnRegionChosen(object? sender, CaptureSelectionCompletedEventArgs e)
    {
        // The overlay reports the region in bitmap space of the virtual-desktop frame; convert
        // back to virtual-desktop screen pixels the recorder captures from.
        RectD screenRegion = new(
            e.Frame.ScreenBounds.Left + e.BitmapRegion.Left,
            e.Frame.ScreenBounds.Top + e.BitmapRegion.Top,
            e.BitmapRegion.Width,
            e.BitmapRegion.Height);

        OpenControls(screenRegion);
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
                "녹화를 시작할 준비를 마치지 못했습니다. 저장 공간과 화면 녹화 설정을 확인해 주세요.\n\n" + ex.Message,
                "MyCapture — 녹화 시작 실패",
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
            "녹화를 정상적으로 마무리하지 못했습니다. 완성되지 않은 임시 파일은 갤러리에 " +
            "잘못 등록되지 않도록 정리했습니다.\n\n" + e.Exception.Message,
            "MyCapture — 녹화 실패",
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
            EndSessionIfIdle();
            return;
        }

        _finishing = true;
        _completionInProgress = true;
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
            MessageBox.Show(
                "녹화 파일은 복구 표식과 함께 보존했지만 갤러리 등록을 완료하지 못했습니다. " +
                "MyCapture를 다시 시작하면 복구를 시도합니다.\n\n" + ex.Message,
                "MyCapture — 녹화 저장 실패",
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
        }
        catch (Exception ex)
        {
            _videoEditSession?.Dispose();
            _videoEditSession = null;
            _editor = null;
            _finishing = false;
            _log.LogError(ex, "The recording was saved, but its editor could not be opened");
            MessageBox.Show(
                "녹화는 갤러리에 안전하게 저장했지만 편집 창을 열지 못했습니다. " +
                "갤러리에서 영상을 다시 열어 주세요.\n\n" + ex.Message,
                "MyCapture — 편집기 열기 실패",
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
