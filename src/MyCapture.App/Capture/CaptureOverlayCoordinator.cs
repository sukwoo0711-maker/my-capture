using System.Windows;
using Microsoft.Extensions.Logging;
using MyCapture.App.Editing;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

internal sealed class CaptureOverlayCoordinator
{
    private readonly ScreenCaptureEngine _captureEngine;
    private readonly WindowCandidateService _windowCandidates;
    private readonly ILogger<CaptureOverlayCoordinator> _log;
    private CaptureOverlayWindow? _activeOverlay;
    private AnnotationEditorWindow? _activeEditor;

    internal CaptureOverlayCoordinator(
        ScreenCaptureEngine captureEngine,
        WindowCandidateService windowCandidates,
        ILogger<CaptureOverlayCoordinator> log)
    {
        _captureEngine = captureEngine ?? throw new ArgumentNullException(nameof(captureEngine));
        _windowCandidates = windowCandidates ?? throw new ArgumentNullException(nameof(windowCandidates));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal event EventHandler<CaptureSelectionCompletedEventArgs>? SelectionCompleted;

    internal event EventHandler<AnnotationEditingResult>? EditingCompleted;

    /// <summary>Raised when the complete selector/editor session has ended.</summary>
    internal event EventHandler? OverlayClosed;

    internal Func<AnnotationEditingResult, bool>? CommitRequested { get; set; }

    internal bool IsActive => _activeOverlay is not null || _activeEditor is not null;

    internal ScreenCaptureEngine Engine => _captureEngine;

    /// <summary>Used only by the explicit advanced “capture window” command.</summary>
    internal WindowCandidateService WindowCandidates => _windowCandidates;

    internal void Start(bool includeCursor, bool abortOnFocusLoss, bool showMagnifier)
    {
        Application.Current.Dispatcher.VerifyAccess();

        if (ActivateCurrent())
        {
            return;
        }

        // Freeze before showing any UI. Manual region capture intentionally does not enumerate
        // or expose window candidates; Ctrl+Shift+C is a pure free-drag workflow.
        MonitorInfo monitor = MonitorEnumerator.GetFromCursor();
        FrozenFrame frame = _captureEngine.CaptureMonitor(monitor, includeCursor);

        var overlay = new CaptureOverlayWindow(frame, abortOnFocusLoss, showMagnifier);
        _activeOverlay = overlay;
        overlay.SelectionCompleted += OnOverlaySelectionCompleted;
        overlay.Closed += OnOverlayClosed;

        _log.LogInformation(
            "Opening free-region selector on {Device} ({Width}x{Height}, {Dpi}dpi)",
            monitor.DeviceName,
            monitor.PixelWidth,
            monitor.PixelHeight,
            monitor.Dpi);

        overlay.Show();
        _ = overlay.Activate();
    }

    internal bool StartWithSelection(FrozenFrame frame, RectD region) =>
        StartWithSelection(frame, region, sourceTitle: string.Empty, recordForRepeat: false);

    internal bool StartWithSelection(FrozenFrame frame, RectD region, string sourceTitle) =>
        StartWithSelection(frame, region, sourceTitle, recordForRepeat: false);

    /// <summary>
    /// Opens the normal editor directly for an advanced capture whose physical region is already
    /// known. No full-screen overlay is created.
    /// </summary>
    internal bool StartWithSelection(
        FrozenFrame frame,
        RectD region,
        string sourceTitle,
        bool recordForRepeat)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Application.Current.Dispatcher.VerifyAccess();

        if (ActivateCurrent())
        {
            return false;
        }

        RectD pixels = region.ToPixelBounds().ClampTo(new RectD(0, 0, frame.PixelWidth, frame.PixelHeight));
        System.Windows.Media.Imaging.BitmapSource crop = ScreenCaptureEngine.Crop(frame, pixels);
        var selection = new CaptureSelectionCompletedEventArgs(
            frame,
            pixels,
            crop,
            sourceTitle,
            recordForRepeat);

        _log.LogInformation(
            "Opening standalone editor over region {Region} on a {Width}x{Height} frame",
            pixels,
            frame.PixelWidth,
            frame.PixelHeight);

        AnnounceSelectionAndOpenEditor(selection);
        return true;
    }

    internal void Cancel()
    {
        Application.Current.Dispatcher.VerifyAccess();
        if (_activeOverlay is not null)
        {
            _activeOverlay.Close();
        }
        else
        {
            _activeEditor?.Close();
        }
    }

    private bool ActivateCurrent()
    {
        Window? active = _activeEditor ?? (Window?)_activeOverlay;
        if (active is null)
        {
            return false;
        }

        if (active.WindowState == WindowState.Minimized)
        {
            active.WindowState = WindowState.Normal;
        }

        _ = active.Activate();
        return true;
    }

    private void OnOverlaySelectionCompleted(object? sender, CaptureSelectionCompletedEventArgs e) =>
        AnnounceSelectionAndOpenEditor(e);

    private void AnnounceSelectionAndOpenEditor(CaptureSelectionCompletedEventArgs selection)
    {
        _log.LogInformation(
            "Selected free region {Region} ({Width}x{Height}); opening standalone editor",
            selection.BitmapRegion,
            selection.SelectedBitmap.PixelWidth,
            selection.SelectedBitmap.PixelHeight);

        // Persist the untouched original before the editor appears, preserving the existing
        // crash-safety invariant.
        SelectionCompleted?.Invoke(this, selection);

        var editor = new AnnotationEditorWindow(
            selection.Frame,
            selection.BitmapRegion,
            selection.SelectedBitmap);
        _activeEditor = editor;
        editor.CommitRequested = CommitRequested;
        editor.Committed += OnEditorCommitted;
        editor.Cancelled += OnEditorCancelled;
        editor.Closed += OnEditorClosed;
        editor.Show();
        _ = editor.Activate();
    }

    private void OnEditorCommitted(object? sender, AnnotationEditingResult e)
    {
        _log.LogInformation(
            "Editing committed: {ItemCount} annotation(s), {ImageCount} inserted image(s)",
            e.Document.Items.Count,
            e.ImageAssetSources.Count);
        EditingCompleted?.Invoke(this, e);
    }

    private void OnEditorCancelled(object? sender, EventArgs e) =>
        _log.LogInformation("Standalone capture editor cancelled");

    private void OnOverlayClosed(object? sender, EventArgs e)
    {
        if (sender is CaptureOverlayWindow overlay)
        {
            overlay.SelectionCompleted -= OnOverlaySelectionCompleted;
            overlay.Closed -= OnOverlayClosed;
            if (ReferenceEquals(_activeOverlay, overlay))
            {
                _activeOverlay = null;
            }
        }

        EndSessionIfIdle();
    }

    private void OnEditorClosed(object? sender, EventArgs e)
    {
        if (sender is AnnotationEditorWindow editor)
        {
            editor.Committed -= OnEditorCommitted;
            editor.Cancelled -= OnEditorCancelled;
            editor.Closed -= OnEditorClosed;
            if (ReferenceEquals(_activeEditor, editor))
            {
                _activeEditor = null;
            }
        }

        EndSessionIfIdle();
    }

    private void EndSessionIfIdle()
    {
        if (_activeOverlay is null && _activeEditor is null)
        {
            OverlayClosed?.Invoke(this, EventArgs.Empty);
        }
    }
}
