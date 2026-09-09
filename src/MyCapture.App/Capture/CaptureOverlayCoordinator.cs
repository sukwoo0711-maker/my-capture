using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

internal sealed class CaptureOverlayCoordinator : IDisposable
{
    private readonly ScreenCaptureEngine _captureEngine;
    private readonly WindowCandidateService _windowCandidates;
    private readonly ILogger<CaptureOverlayCoordinator> _log;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool, FrozenFrame> _acquireFrame;
    private CapturePreparation? _preparation;
    private bool _disposed;
    private CaptureOverlayWindow? _activeOverlay;
    private AnnotationEditorWindow? _activeEditor;
    private bool _isOpeningEditor;
    private CancellationTokenSource? _openingEditorCts;

    internal CaptureOverlayCoordinator(
        ScreenCaptureEngine captureEngine,
        WindowCandidateService windowCandidates,
        ILogger<CaptureOverlayCoordinator> log,
        Func<bool, FrozenFrame>? acquireFrame = null)
    {
        _captureEngine = captureEngine ?? throw new ArgumentNullException(nameof(captureEngine));
        _windowCandidates = windowCandidates ?? throw new ArgumentNullException(nameof(windowCandidates));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _acquireFrame = acquireFrame ?? captureEngine.CaptureVirtualDesktop;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _dispatcher.ShutdownStarted += OnDispatcherShutdownStarted;
    }

    internal Func<CaptureSelectionCompletedEventArgs, Task>? SelectionPersistRequested { get; set; }

    internal IPrivacyRedactionService? PrivacyRedactionService { get; set; }
    internal Func<bool>? RequiresCaptureExclusion { get; set; }
    internal Func<Window, bool> ApplyCaptureExclusion { get; set; } = CaptureWindowExclusion.TryApply;
    internal event Action<Exception>? TransitionFailed;

    private void PrepareWindow(Window window)
    {
        if (RequiresCaptureExclusion?.Invoke() == true && !ApplyCaptureExclusion(window))
            throw new InvalidOperationException(UiText.Get("Text_D1F0DAEAA780"));
    }

    internal event EventHandler<AnnotationEditingResult>? EditingCompleted;

    /// <summary>Raised when the complete selector/editor session has ended.</summary>
    internal event EventHandler? OverlayClosed;

    internal Func<AnnotationEditingResult, Task<bool>>? CommitRequested { get; set; }

    internal bool IsActive => !_disposed &&
        (_preparation is not null || _activeOverlay is not null || _activeEditor is not null || _isOpeningEditor);

    internal Task LastTransitionForTest { get; private set; } = Task.CompletedTask;
    internal Task LastPreparationForTest { get; private set; } = Task.CompletedTask;

    internal ScreenCaptureEngine Engine => _captureEngine;

    /// <summary>Used only by the explicit advanced “capture window” command.</summary>
    internal WindowCandidateService WindowCandidates => _windowCandidates;

    internal void Start(bool includeCursor, bool abortOnFocusLoss, bool showMagnifier)
    {
        VerifyDispatcherAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_preparation is not null || _isOpeningEditor)
        {
            _log.LogInformation("Capture ignored while a frame or selection is being prepared");
            return;
        }

        if (ActivateCurrent())
        {
            return;
        }

        // Reserve the session synchronously, including when WM_HOTKEY has no WPF context.
        // Each accepted request acquires one fresh frame before any selector UI is created.
        var preparation = new CapturePreparation();
        _preparation = preparation;
        _log.LogInformation("Capture frame acquisition requested");
        LastPreparationForTest = AcquireAndShowAsync(preparation, includeCursor, abortOnFocusLoss, showMagnifier);
    }

    private async Task AcquireAndShowAsync(
        CapturePreparation preparation, bool includeCursor, bool abortOnFocusLoss, bool showMagnifier)
    {
        FrozenFrame? frame = null;
        Exception? failure = null;
        try
        {
            frame = await Task.Run(() =>
            {
                if (preparation.Cancelled) throw new OperationCanceledException();
                FrozenFrame acquired = _acquireFrame(includeCursor);
                if (!acquired.Bitmap.IsFrozen)
                    throw new InvalidOperationException("Capture acquisition must return a frozen bitmap.");
                return acquired;
            }).ConfigureAwait(false);
            _log.LogInformation("Capture frame acquired after {Elapsed:0.0}ms (acquisition {Acquisition:0.0}ms)",
                preparation.Elapsed.Elapsed.TotalMilliseconds, frame.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Explicit dispatch also covers native hotkey callbacks without SynchronizationContext.
        // An aborted dispatcher operation is observed; shutdown never waits for native BitBlt.
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        try
        {
            await _dispatcher.InvokeAsync(() =>
                CompletePreparation(preparation, frame, failure, abortOnFocusLoss, showMagnifier)).Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            // Shutdown invalidates the preparation before any late window can be shown.
        }
        catch (Exception ex)
        {
            // A feedback subscriber must not turn a fire-and-observed transition into an
            // unobserved task failure. Session cleanup runs in CompletePreparation's finally.
            _log.LogError(ex, "Capture preparation dispatcher transition failed");
        }
    }

    private void CompletePreparation(
        CapturePreparation preparation, FrozenFrame? frame, Exception? failure, bool abortOnFocusLoss, bool showMagnifier)
    {
        VerifyDispatcherAccess();
        if (!ReferenceEquals(_preparation, preparation)) return;
        try
        {
            if (_disposed || preparation.Cancelled) return;
            if (failure is not null) throw failure;
            ShowOverlay(frame ?? throw new InvalidOperationException("Capture acquisition returned no frame."),
                abortOnFocusLoss, showMagnifier, preparation.Elapsed);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not prepare the capture overlay");
            TransitionFailed?.Invoke(ex);
        }
        finally
        {
            _preparation = null;
            EndSessionIfIdle();
        }
    }

    private void ShowOverlay(FrozenFrame frame, bool abortOnFocusLoss, bool showMagnifier, Stopwatch elapsed)
    {

        var overlay = new CaptureOverlayWindow(frame, abortOnFocusLoss, showMagnifier);
        _activeOverlay = overlay;
        overlay.SelectionCompleted += OnOverlaySelectionCompleted;
        overlay.Closed += OnOverlayClosed;
        overlay.ContentRendered += OnFirstRendered;
        void OnFirstRendered(object? sender, EventArgs args)
        {
            overlay.ContentRendered -= OnFirstRendered;
            _log.LogInformation("Capture overlay first rendered after {Elapsed:0.0}ms", elapsed.Elapsed.TotalMilliseconds);
        }

        _log.LogInformation(
            "Opening free-region selector across virtual desktop ({Width}x{Height})",
            frame.PixelWidth,
            frame.PixelHeight);

        try
        {
            PrepareWindow(overlay);
            overlay.Show();
        }
        catch
        {
            overlay.Close();
            throw;
        }
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
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_preparation is not null || _isOpeningEditor)
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
        if (_preparation is { } preparation) preparation.Cancelled = true;
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

    public void Dispose()
    {
        VerifyDispatcherAccess();
        if (_disposed) return;
        _disposed = true;
        _dispatcher.ShutdownStarted -= OnDispatcherShutdownStarted;
        Cancel();
        _preparation = null;
    }

    private void OnDispatcherShutdownStarted(object? sender, EventArgs e) => Dispose();

    private sealed class CapturePreparation
    {
        internal volatile bool Cancelled;
        internal Stopwatch Elapsed { get; } = Stopwatch.StartNew();
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

    private void VerifyDispatcherAccess() => _dispatcher.VerifyAccess();

    private Task AnnounceSelectionAndOpenEditorAsync(CaptureSelectionCompletedEventArgs selection)
    {
        // Native hotkey callbacks can run on the STA without a WPF synchronization context.
        // Keep persistence continuations and all window/exclusion state on the owning dispatcher.
        SynchronizationContext? previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(
                Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher));
            return AnnounceSelectionAndOpenEditorCoreAsync(selection);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private async Task AnnounceSelectionAndOpenEditorCoreAsync(CaptureSelectionCompletedEventArgs selection)
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
                selection.SelectedBitmap,
                privacyRedactionService: PrivacyRedactionService);
            _activeEditor = editor;
            editor.CommitRequested = CommitRequested;
            editor.Committed += OnEditorCommitted;
            editor.Cancelled += OnEditorCancelled;
            editor.Closed += OnEditorClosed;
            PrepareWindow(editor);
            editor.Show();
            _ = editor.Activate();
        }
        catch (Exception ex)
        {
            // The app-level persistence handler normally catches storage failures and enters
            // recovery-export mode. An unexpected transition/window failure must still release
            // the single-session guard instead of leaving capture permanently wedged.
            _log.LogError(ex, "Could not complete the selection-to-editor transition");
            TransitionFailed?.Invoke(ex);
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
        if (!_disposed && _preparation is null && _activeOverlay is null && _activeEditor is null && !_isOpeningEditor)
        {
            OverlayClosed?.Invoke(this, EventArgs.Empty);
        }
    }
}
