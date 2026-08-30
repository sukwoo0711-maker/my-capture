using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
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

    private CaptureOverlayWindow? _selectionOverlay;
    private RecordingControlWindow? _controls;
    private VideoEditorWindow? _editor;

    // Set the moment a stop is requested and held until the editor has opened (or the
    // session has fully ended). Without it, a second Ctrl+Shift+X arriving during the
    // brief stop→finalise→editor transition — when _controls may already be null but the
    // editor not yet shown — would fall through to StartRegionSelection() and begin a NEW
    // recording from 0. This flag closes that race deterministically.
    private bool _finishing;

    internal RegionRecordingCoordinator(
        ScreenCaptureEngine captureEngine,
        AppPaths paths,
        Func<RecordingSettings> settings,
        ILoggerFactory loggerFactory)
    {
        _captureEngine = captureEngine ?? throw new ArgumentNullException(nameof(captureEngine));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _log = loggerFactory.CreateLogger<RegionRecordingCoordinator>();
    }

    /// <summary>Raised whenever the whole recording session (selection→record→edit) ends.</summary>
    internal event EventHandler? SessionEnded;

    /// <summary>
    /// Committed edit that yielded a still image the caller should push into the
    /// capture queue, exactly like a normal capture. Carries the annotation result.
    /// </summary>
    internal event EventHandler<AnnotationFrameCapturedEventArgs>? FrameImageCaptured;

    internal bool IsActive => _selectionOverlay is not null || _controls is not null || _editor is not null || _finishing;

    /// <summary>
    /// Entry point for the Ctrl+Shift+X command. If a recording is already running,
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
        MonitorInfo monitor = MonitorEnumerator.GetFromCursor();
        FrozenFrame frame = _captureEngine.CaptureMonitor(monitor, includeCursor: false);

        // Reuse the exact capture region selector. Recording selects an area the same
        // way capture does, so muscle memory transfers.
        var overlay = new CaptureOverlayWindow(frame, abortOnFocusLoss: false, showMagnifier: true);
        _selectionOverlay = overlay;
        overlay.SelectionCompleted += OnRegionChosen;
        overlay.SelectionCancelled += OnSelectionCancelled;
        overlay.Closed += OnSelectionClosed;

        _log.LogInformation("Recording region selector opened on {Device}", monitor.DeviceName);
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
        // The overlay reports the region in bitmap space of the monitor frame; convert
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
        RecordingSettings settings = _settings();

        RegionRecorder BuildRecorder()
        {
            var grabber = new RegionFrameGrabber(_captureEngine, settings.IncludeCursor);
            return new RegionRecorder(
                grabber,
                options => new MediaFoundationVideoEncoder(options, _loggerFactory.CreateLogger<MediaFoundationVideoEncoder>()),
                _loggerFactory.CreateLogger<RegionRecorder>());
        }

        var controls = new RecordingControlWindow(
            screenRegion,
            settings,
            BuildRecorder,
            () => NextOutputPath(),
            _loggerFactory.CreateLogger<RecordingControlWindow>());
        _controls = controls;
        controls.RecordingFinished += OnRecordingFinished;
        controls.Cancelled += OnControlsCancelled;
        controls.Stopping += OnControlsStopping;
        controls.Closed += OnControlsClosed;
        controls.Show();
        _ = controls.Activate();
    }

    private void OnControlsStopping(object? sender, EventArgs e) => _finishing = true;

    private void OnControlsCancelled(object? sender, EventArgs e) =>
        _log.LogInformation("Recording cancelled before or during capture");

    private void OnControlsClosed(object? sender, EventArgs e)
    {
        if (sender is RecordingControlWindow controls)
        {
            controls.RecordingFinished -= OnRecordingFinished;
            controls.Cancelled -= OnControlsCancelled;
            controls.Stopping -= OnControlsStopping;
            controls.Closed -= OnControlsClosed;
            if (ReferenceEquals(_controls, controls))
            {
                _controls = null;
            }
        }

        // By now either the editor has opened (OnRecordingFinished cleared _finishing and
        // anchored the session on _editor) or the stop yielded no clip. Either way the
        // control window is gone, so the transition is over — clear the guard so a stuck
        // flag can never block future recordings.
        _finishing = false;

        EndSessionIfIdle();
    }

    private void OnRecordingFinished(object? sender, RecordingResult result)
    {
        _log.LogInformation(
            "Recording produced {Path} ({Frames} frames, {Duration:0}ms)",
            result.OutputPath,
            result.EmittedFrames,
            result.DurationMs);

        var editor = new VideoEditorWindow(result, _paths, _loggerFactory);
        _editor = editor;
        // The stop→finalise→editor transition is complete: the editor now anchors the
        // session, so clear the finishing guard.
        _finishing = false;
        editor.FrameImageCaptured += OnFrameImageCaptured;
        editor.Closed += OnEditorClosed;
        editor.Show();
        _ = editor.Activate();
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

        EndSessionIfIdle();
    }

    private string NextOutputPath()
    {
        string dir = Path.Combine(
            _paths.CapturesRoot,
            "recordings",
            DateTimeOffset.Now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);

        string name = "recording_" +
            DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) +
            ".mp4";
        return Path.Combine(dir, name);
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
