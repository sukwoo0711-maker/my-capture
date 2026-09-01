using System.Windows;
using System.Windows.Threading;
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
    private bool _isOpeningEditor;
    private CancellationTokenSource? _openingEditorCts;

    internal CaptureOverlayCoordinator(
        ScreenCaptureEngine captureEngine,
        WindowCandidateService windowCandidates,
        ILogger<CaptureOverlayCoordinator> log)
    {
        _captureEngine = captureEngine ?? throw new ArgumentNullException(nameof(captureEngine));
        _windowCandidates = windowCandidates ?? throw new ArgumentNullException(nameof(windowCandidates));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal Func<CaptureSelectionCompletedEventArgs, Task>? SelectionPersistRequested { get; set; }

    internal event EventHandler<AnnotationEditingResult>? EditingCompleted;

    /// <summary>Raised when the complete selector/editor session has ended.</summary>
    internal event EventHandler? OverlayClosed;

    internal Func<AnnotationEditingResult, Task<bool>>? CommitRequested { get; set; }

    internal bool IsActive => _activeOverlay is not null || _activeEditor is not null || _isOpeningEditor;

    internal Task LastTransitionForTest { get; private set; } = Task.CompletedTask;

    internal ScreenCaptureEngine Engine => _captureEngine;

    /// <summary>Used only by the explicit advanced “capture window” command.</summary>
    internal WindowCandidateService WindowCandidates => _windowCandidates;

    internal void Start(bool includeCursor, bool abortOnFocusLoss, bool showMagnifier)
    {
        VerifyDispatcherAccess();

        if (_isOpeningEditor)
        {
            _log.LogInformation("Capture ignored while the previous selection is being persisted");
            return;
        }

        if (ActivateCurrent())
        {
            return;
        }

        // Freeze the whole physical-pixel virtual desktop before showing UI. A free drag may
        // begin on one monitor and end on another, including displays with a negative origin.
        FrozenFrame frame = _captureEngine.CaptureVirtualDesktop(includeCursor);

        var overlay = new CaptureOverlayWindow(frame, abortOnFocusLoss, showMagnifier);
        _activeOverlay = overlay;
        overlay.SelectionCompleted += OnOverlaySelectionCompleted;
        overlay.Closed += OnOverlayClosed;

        _log.LogInformation(
            "Opening free-region selector across virtual desktop ({Width}x{Height})",
            frame.PixelWidth,
            frame.PixelHeight);

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
        VerifyDispatcherAccess();

        if (_isOpeningEditor)
        {
            _log.LogInformation("Advanced capture ignored while the previous selection is being persisted");
            return false;
        }

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
            recordForRepeat,
            copyToClipboardImmediately: false);

        _log.LogInformation(
            "Opening standalone editor over region {Region} on a {Width}x{Height} frame",
            pixels,
            frame.PixelWidth,
            frame.PixelHeight);

        LastTransitionForTest = AnnounceSelectionAndOpenEditorAsync(selection);
        return true;
    }

    internal void Cancel()
    {
        VerifyDispatcherAccess();
        _openingEditorCts?.Cancel();
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

    private async void OnOverlaySelectionCompleted(object? sender, CaptureSelectionCompletedEventArgs e)
    {
        LastTransitionForTest = AnnounceSelectionAndOpenEditorAsync(e);
        await LastTransitionForTest;
    }

    private static void VerifyDispatcherAccess() =>
        (Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher).VerifyAccess();

    private async Task AnnounceSelectionAndOpenEditorAsync(CaptureSelectionCompletedEventArgs selection)
    {
        _log.LogInformation(
            "Selected free region {Region} ({Width}x{Height}); opening standalone editor",
            selection.BitmapRegion,
            selection.SelectedBitmap.PixelWidth,
            selection.SelectedBitmap.PixelHeight);

        // Encode and durably persist the untouched original off-dispatcher before the editor
        // appears. The pending flag keeps overlay session lifetime stable while the selector
        // closes during this asynchronous transition.
        var transition = new CancellationTokenSource();
        _openingEditorCts = transition;
        _isOpeningEditor = true;
        try
        {
            if (SelectionPersistRequested is not null)
            {
                await SelectionPersistRequested(selection);
            }

            if (transition.IsCancellationRequested)
            {
                _log.LogInformation("Editor opening cancelled after selection persistence");
                return;
            }

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
        catch (Exception ex)
        {
            // The app-level persistence handler normally catches storage failures and enters
            // recovery-export mode. An unexpected transition/window failure must still release
            // the single-session guard instead of leaving capture permanently wedged.
            _log.LogError(ex, "Could not complete the selection-to-editor transition");
            if (_activeEditor is { } failedEditor)
            {
                failedEditor.Committed -= OnEditorCommitted;
                failedEditor.Cancelled -= OnEditorCancelled;
                failedEditor.Closed -= OnEditorClosed;
                _activeEditor = null;
                try
                {
                    failedEditor.Close();
                }
                catch (InvalidOperationException)
                {
                    // The native window may never have been created; clearing the coordinator
                    // reference is sufficient and the managed object can be collected.
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_openingEditorCts, transition))
            {
                _openingEditorCts = null;
                _isOpeningEditor = false;
            }

            transition.Dispose();
            EndSessionIfIdle();
        }
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
        if (_activeOverlay is null && _activeEditor is null && !_isOpeningEditor)
        {
            OverlayClosed?.Invoke(this, EventArgs.Empty);
        }
    }
}
