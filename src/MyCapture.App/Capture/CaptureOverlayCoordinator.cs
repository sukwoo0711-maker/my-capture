using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
using MyCapture.App.Themes;
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
    private readonly Func<RectD> _desktopBounds;
    private readonly Func<RectD, IReadOnlyList<WindowCandidate>> _readWindowCandidates;
    // Nothing on screen waits for these reads, so a read that finds the previous capture's probe
    // still running waits for it within its own deadline instead of giving up at once.
    private readonly BrowserPageUrlService _pageUrls = new(waitForBusyProbe: true);
    private readonly WindowSourceService _windowSources = new(waitForBusyProbe: true);
    private CapturePreparation? _preparation;
    private bool _disposed;
    private CaptureOverlayWindow? _activeOverlay;
    private AnnotationEditorWindow? _activeEditor;
    private CaptureOverlayCoordinator? _editorScreenCapture;
    private bool _isOpeningEditor;
    private CancellationTokenSource? _openingEditorCts;

    /// <summary>
    /// Tick of the last time an app-owned topmost surface (capture overlay, editor, tray
    /// menu) left the screen. The frozen frame must not photograph its dismissal, so a
    /// capture that starts within <see cref="PresentationSettleMs"/> still waits briefly;
    /// cold captures skip the wait entirely.
    /// </summary>
    private long _lastPresentationDismissedTick = long.MinValue;

    /// <summary>How long after a dismissal a capture still waits out the presentation.</summary>
    internal const int PresentationSettleMs = 750;

    internal CaptureOverlayCoordinator(
        ScreenCaptureEngine captureEngine,
        WindowCandidateService windowCandidates,
        ILogger<CaptureOverlayCoordinator> log,
        Func<bool, FrozenFrame>? acquireFrame = null,
        Func<RectD>? desktopBounds = null,
        Func<RectD, IReadOnlyList<WindowCandidate>>? readWindowCandidates = null)
    {
        _captureEngine = captureEngine ?? throw new ArgumentNullException(nameof(captureEngine));
        _windowCandidates = windowCandidates ?? throw new ArgumentNullException(nameof(windowCandidates));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _acquireFrame = acquireFrame ?? captureEngine.CaptureVirtualDesktop;
        _desktopBounds = desktopBounds ?? MonitorEnumerator.GetVirtualDesktopBounds;
        _readWindowCandidates = readWindowCandidates ?? windowCandidates.GetCandidates;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _dispatcher.ShutdownStarted += OnDispatcherShutdownStarted;
        ReadWindowSources = _windowSources.ReadAsync;
        ReadPageUrls = _pageUrls.ReadAsync;
    }

    /// <summary>
    /// Reads each exposed browser window's page document around the frozen frame. Tests replace it.
    /// </summary>
    internal Func<IReadOnlyList<WindowCandidate>, Task<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>> ReadPageUrls { get; set; }

    internal Func<CaptureSelectionCompletedEventArgs, Task>? SelectionPersistRequested { get; set; }
    internal Func<CaptureSelectionCompletedEventArgs, Task>? EditorScreenCaptureRequested { get; set; }

    /// <summary>
    /// Reads each exposed window's application and Explorer folder around the frozen frame. Tests
    /// replace it; null skips source metadata entirely.
    /// </summary>
    internal Func<IReadOnlyList<WindowCandidate>, Task<IReadOnlyDictionary<IntPtr, WindowSourceSnapshot>>>? ReadWindowSources { get; set; }

    internal Func<bool> OpenEditorAfterCapture { get; set; } = () => true;
    internal Func<bool> OriginalNeedsRecovery { get; set; } = () => false;

    /// <summary>The persisted capture the editor saves to; its drafts recover onto that capture.</summary>
    internal Func<(Guid Id, long ContentRevision)?>? DraftRecord { get; set; }

    /// <summary>Reads Capture.AutoDetectWindows when each selector opens. Off unless the app wires it.</summary>
    internal Func<bool> AutoDetectWindows { get; set; } = () => false;

    /// <summary>Reads General.AlwaysShowShortcutHints when each selector opens; defaults to Alt-only hints.</summary>
    internal Func<bool> ShowShortcutHints { get; set; } = () => false;

    /// <summary>
    /// Asked once per selector whether it opens with the short start guide. The owner counts the
    /// showings and persists them (Capture.StartGuideShownCount); off unless the app wires it.
    /// </summary>
    internal Func<bool> ClaimStartGuide { get; set; } = () => false;

    /// <summary>
    /// Runs the existing command for the bar's record switch and ⋯ menu (recording selector, delayed
    /// or scrolling capture, previous region) once the selector has closed and the session has ended.
    /// Null hides the switch and the menu.
    /// </summary>
    internal Action<CaptureSelectorRequest>? SelectorActionRequested { get; set; }

    /// <summary>Whether a previous region exists, for the ⋯ menu's previous-region entry.</summary>
    internal Func<bool> CanRepeatLastRegion { get; set; } = () => true;

    /// <summary>Copies the overlay's sampled colour (C key). Null leaves the key unbound.</summary>
    internal Func<string, Task<bool>>? CopyColorText { get; set; }
    internal Action<ColorCopyFeedback>? ColorCopied { get; set; }

    internal Func<MyCapture.Core.Settings.ColorFormat> ReadColorFormat { get; set; } =
        () => MyCapture.Core.Settings.ColorFormat.Hex;

    /// <summary>
    /// Receives a selection confirmed in text capture mode. The image is never persisted and no
    /// editor opens; the session ends as soon as the selector closes. Null disables text capture.
    /// </summary>
    internal Action<CaptureSelectionCompletedEventArgs>? TextCaptureRequested { get; set; }

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
        (_preparation is not null || _activeOverlay is not null || _activeEditor is not null || _isOpeningEditor || _editorScreenCapture is not null);

    internal Task LastTransitionForTest { get; private set; } = Task.CompletedTask;
    internal Task LastPreparationForTest { get; private set; } = Task.CompletedTask;

    internal ScreenCaptureEngine Engine => _captureEngine;
    internal bool IsSelecting => _preparation is not null || _activeOverlay is not null || _editorScreenCapture?.IsSelecting == true;
    internal AnnotationEditorControl? CurrentEditor => _activeEditor?.Editor;
    internal Task WaitForPresentationDismissalAsync() =>
        NeedsPresentationSettle(Environment.TickCount64, _lastPresentationDismissedTick)
            ? Task.Delay(100) : Task.CompletedTask;

    internal bool CanRetake => !_disposed && _activeEditor is { } editor && !IsEditorSaving(editor) && !IsEditorBusy(editor) &&
        !editor.IsAskingToClose && _preparation is null && !_isOpeningEditor && _editorScreenCapture is null;

    private static bool IsEditorSaving(AnnotationEditorWindow editor) => editor.Editor.IsCommitInProgress || editor.IsSavingToClose;

    private static bool IsEditorBusy(AnnotationEditorWindow editor) =>
        editor.IsCloseInProgress || editor.Editor.IsEditingOperationInProgress || editor.HasOpenDialog;

    /// <summary>Adds what this session holds (a selector, an opening or busy editor) to the shell's own state.</summary>
    internal CaptureGuardState WithSession(CaptureGuardState state)
    {
        if (_disposed) return state;
        if (_editorScreenCapture is { IsActive: true } nested) return nested.WithSession(state);
        AnnotationEditorWindow? editor = _activeEditor;
        return state with
        {
            Selecting = IsSelecting,
            EditorOpening = _isOpeningEditor,
            EditorOpen = editor is not null,
            EditorAsking = editor?.IsAskingToClose == true,
            EditorSaving = editor is not null && IsEditorSaving(editor),
            EditorBusy = editor is not null && IsEditorBusy(editor),
        };
    }

    /// <summary>
    /// Closes only the capture-owned draft editor before replacing the selection, the way the
    /// user's own close does: unsaved annotations are confirmed first. When the user keeps the
    /// editor (Cancel) or saves first, the editor is still open afterwards and the caller's
    /// guard abandons the retake instead of discarding the drawing.
    /// </summary>
    internal void CloseEditorForRetake()
    {
        VerifyDispatcherAccess();
        if (!CanRetake) return;
        _openingEditorCts?.Cancel();
        _activeEditor?.RequestClose();
    }

    /// <summary>
    /// Closes the capture-owned editor for a new capture as the user's own close does, over the editor
    /// brought forward. Save and close finishes saving first, then the new capture starts.
    /// </summary>
    /// <returns><see cref="CaptureBlock.None"/> once the editor has closed, otherwise why it is still open.</returns>
    internal async Task<CaptureBlock> CloseEditorForRetakeAsync()
    {
        VerifyDispatcherAccess();
        if (!CanRetake) return _activeEditor is null ? CaptureBlock.None : CaptureBlock.EditorBusy;
        _openingEditorCts?.Cancel();
        AnnotationEditorWindow editor = _activeEditor!;
        if (await editor.RequestCloseAsync() && !_disposed && _activeEditor is null) return CaptureBlock.None;
        return editor.LastCloseChoice == EditorCloseChoice.SaveAndClose ? CaptureBlock.EditorSaveFailed : CaptureBlock.EditorKept;
    }

    /// <summary>
    /// Brings the editor, and any dialog it is waiting on, forward and says in its status line why a
    /// command did not start. False when no editor is open.
    /// </summary>
    internal bool ShowEditorBlocked(string notice)
    {
        VerifyDispatcherAccess();
        // An editor whose save finishes out of sight has already left the screen for the user.
        if (_disposed || _activeEditor is not { IsFinishingOutOfSight: false } editor) return false;
        WindowForeground.BringForward(editor);
        editor.Editor.ShowCaptureBlocked(notice);
        return true;
    }

    /// <summary>
    /// Completes once the editor's Done (which may take it off the screen while the save finishes)
    /// has closed it, or has ended without closing and left it editable on screen; at once when no
    /// such Done runs. A capture command waits for it, so it starts as soon as the previous capture
    /// is saved instead of being refused by an editor the user is leaving.
    /// </summary>
    internal Task WaitForEditorFinishedAsync() =>
        !_disposed && _activeEditor is { IsFinishingDone: true } editor ? editor.DoneFinished : Task.CompletedTask;

    /// <summary>True while the editor's Done save finishes after the editor left the screen.</summary>
    internal bool IsEditorFinishingOutOfSight => !_disposed && _activeEditor?.IsFinishingOutOfSight == true;

    /// <summary>Brings the open selector forward; false while its frame is still being taken.</summary>
    internal bool BringSelectorForward()
    {
        VerifyDispatcherAccess();
        if (_editorScreenCapture is { } nested) return nested.BringSelectorForward();
        if (_disposed || _activeOverlay is not { IsVisible: true } overlay) return false;
        _ = overlay.Activate();
        return true;
    }

    /// <summary>Used only by the explicit advanced “capture window” command.</summary>
    internal WindowCandidateService WindowCandidates => _windowCandidates;

    internal void Start(bool includeCursor, bool abortOnFocusLoss, bool showMagnifier, bool showSelectionGrid = true,
        bool textCapture = false, bool preserveEditor = false)
    {
        VerifyDispatcherAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        SelectorTimeline.Mark?.Invoke("start");
        if (_editorScreenCapture is { } current)
        {
            current.ActivateCurrent();
            return;
        }
        if (preserveEditor && CanRetake && EditorScreenCaptureRequested is { } persist)
        {
            StartOverEditor(persist, includeCursor, abortOnFocusLoss, showMagnifier, showSelectionGrid);
            return;
        }

        // Until the selector shows its frozen frame, the preparation ignores new requests.
        if (_isOpeningEditor || (_preparation is not null && _activeOverlay is not { IsVisible: true }))
        {
            _log.LogInformation("Capture ignored while a frame or selection is being prepared");
            return;
        }

        // The text capture command over an open selector switches it to text mode.
        if (textCapture && _activeOverlay is { TextCaptureAvailable: true } open) open.TextCaptureMode = true;

        if (ActivateCurrent())
        {
            return;
        }

        // Reserve the session synchronously, including when WM_HOTKEY has no WPF context.
        // Create its HWND for capture exclusion, but keep the opaque selector hidden until
        // its frozen background is ready. Showing a frameless window blacks out the desktop.
        var preparation = new CapturePreparation
        {
            ShowSelectionGrid = showSelectionGrid,
            TextCapture = textCapture && TextCaptureRequested is not null,
        };
        _preparation = preparation;
        _log.LogInformation("Capture frame acquisition requested");
        try
        {
            RectD desktop = _desktopBounds();
            preparation.DesktopBounds = desktop;
            // The windows as they are at the hotkey: handle, process, title, bounds and z-order. Every
            // page and application read is tied to these identities, and the selection's source is
            // chosen from them.
            preparation.SourceWindows = _readWindowCandidates(desktop);
            SelectorTimeline.Mark?.Invoke("windows-read");
            bool settle = NeedsPresentationSettle(Environment.TickCount64, _lastPresentationDismissedTick);
            if (settle) SelectorTimeline.Mark?.Invoke("settle-required");
            // The reads and the grab start before the selector window is built, so building it
            // overlaps the grab instead of preceding it. The window stays hidden until its frame exists.
            LastPreparationForTest = AcquireAndShowAsync(preparation, includeCursor, settle);
            SelectorTimeline.Mark?.Invoke("acquire-started");
            ShowOverlay(desktop, abortOnFocusLoss, showMagnifier, preparation.Elapsed, frame: null, showSelectionGrid);
            SelectorTimeline.Mark?.Invoke("hidden-window-ready");
        }
        catch
        {
            preparation.Cancelled = true;
            _preparation = null;
            throw;
        }
    }

    internal static bool NeedsPresentationSettle(long currentTick, long lastDismissedTick)
    {
        // The never-dismissed sentinel must not participate in elapsed-time arithmetic:
        // subtracting long.MinValue wraps negative and used to delay every first capture.
        if (lastDismissedTick == long.MinValue) return false;
        long elapsed = unchecked(currentTick - lastDismissedTick);
        return elapsed >= 0 && elapsed < PresentationSettleMs;
    }

    private void StartOverEditor(Func<CaptureSelectionCompletedEventArgs, Task> persist,
        bool includeCursor, bool abortOnFocusLoss, bool showMagnifier, bool showSelectionGrid)
    {
        var nested = new CaptureOverlayCoordinator(_captureEngine, _windowCandidates, _log,
            _acquireFrame, _desktopBounds, _readWindowCandidates)
        {
            SelectionPersistRequested = persist,
            OpenEditorAfterCapture = () => false,
            ReadPageUrls = ReadPageUrls,
            ReadWindowSources = ReadWindowSources,
            AutoDetectWindows = AutoDetectWindows,
            ShowShortcutHints = ShowShortcutHints,
            RequiresCaptureExclusion = RequiresCaptureExclusion,
            ApplyCaptureExclusion = ApplyCaptureExclusion,
        };
        _editorScreenCapture = nested;
        nested.TransitionFailed += error => TransitionFailed?.Invoke(error);
        nested.OverlayClosed += (_, _) =>
        {
            if (ReferenceEquals(_editorScreenCapture, nested)) _editorScreenCapture = null;
            nested.Dispose();
            if (_activeEditor is { IsVisible: true } editor) editor.Activate();
            EndSessionIfIdle();
        };
        try
        {
            nested.Start(includeCursor, abortOnFocusLoss, showMagnifier, showSelectionGrid);
            LastPreparationForTest = nested.LastPreparationForTest;
        }
        catch
        {
            _editorScreenCapture = null;
            nested.Dispose();
            throw;
        }
    }

    /// <summary>Records that an app-owned topmost surface has just left the screen, so the
    /// next capture waits out its dismissal instead of photographing it.</summary>
    internal void NoteForegroundDismissal() => _lastPresentationDismissedTick = Environment.TickCount64;

    private Task AcquireAndShowAsync(CapturePreparation preparation, bool includeCursor, bool settlePresentation) =>
        AcquirePreparedFrameAsync(preparation, includeCursor, settlePresentation).ContinueWith(acquired =>
        {
            var (frame, failure, confirmedWindows) = acquired.GetAwaiter().GetResult();
            // Complete on the same dispatcher turn that shows the selector. Extra asynchronous
            // continuations here leave a visible selector with its preparation still pending.
            return DispatchAsync(() => ShowPreparedFrame(preparation, frame, failure, confirmedWindows));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();

    private async Task<(FrozenFrame? Frame, Exception? Failure, Task<IReadOnlyList<WindowCandidate>>? Windows)>
        AcquirePreparedFrameAsync(CapturePreparation preparation, bool includeCursor, bool settlePresentation)
    {
        FrozenFrame? frame = null;
        Task<IReadOnlyList<WindowCandidate>>? confirmedWindows = null;
        Exception? failure = null;
        try
        {
            // A capture racing a just-dismissed overlay or tray menu would freeze that
            // dismissal into the frame, so only those captures wait for the presentation to
            // settle. Cold captures — the common case — acquire immediately. The page and
            // application reads never wait: they read state, not pixels.
            (FrozenFrame capturedFrame, Task<IReadOnlyList<WindowCandidate>> sourceWindows) = await CapturePageContext.AcquireFrameAsync(preparation.SourceWindows, () =>
            {
                if (preparation.Cancelled) throw new OperationCanceledException();
                SelectorTimeline.Mark?.Invoke("grab-start");
                FrozenFrame acquired = _acquireFrame(includeCursor);
                SelectorTimeline.Mark?.Invoke("grab-done");
                if (!acquired.Bitmap.IsFrozen)
                    throw new InvalidOperationException("Capture acquisition must return a frozen bitmap.");
                return acquired;
            }, ReadPageUrls,
                () => _readWindowCandidates(preparation.DesktopBounds),
                ReadWindowSources,
                settlePresentation ? Task.Delay(100) : null).ConfigureAwait(false);
            frame = capturedFrame;
            confirmedWindows = sourceWindows;
            LastSourceDetailsForTest = sourceWindows;
            _log.LogInformation("Capture frame acquired after {Elapsed:0.0}ms (acquisition {Acquisition:0.0}ms)",
                preparation.Elapsed.Elapsed.TotalMilliseconds, frame.ElapsedMilliseconds);
            _ = LogSourceDetailsConfirmedAsync(sourceWindows, preparation.Elapsed);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // The selector appears as soon as its frozen pixels exist. A browser walk on any monitor
        // costs up to ten screen grabs, so the page and application details never hold it back:
        // the selector and every selection made on it receive them when the reads finish.
        return (frame, failure, confirmedWindows);
    }

    private async Task LogSourceDetailsConfirmedAsync(Task<IReadOnlyList<WindowCandidate>> confirmed, Stopwatch elapsed)
    {
        try
        {
            IReadOnlyList<WindowCandidate> windows = await confirmed.ConfigureAwait(false);
            // Counts only: page addresses, titles and folders are never logged.
            _log.LogInformation(
                "Capture source details confirmed after {Elapsed:0.0}ms ({Pages} page(s), {Sources} application(s))",
                elapsed.Elapsed.TotalMilliseconds,
                windows.Count(window => window.SourcePageUrl.Length > 0),
                windows.Count(window => window.Source is { IsEmpty: false }));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Capture source details could not be confirmed");
        }
    }

    /// <summary>The page and application read of the last preparation that acquired a frame.</summary>
    internal Task<IReadOnlyList<WindowCandidate>> LastSourceDetailsForTest { get; private set; } =
        Task.FromResult<IReadOnlyList<WindowCandidate>>([]);

    /// <summary>
    /// Runs one preparation step on the owning dispatcher. Explicit dispatch also covers native
    /// hotkey callbacks without SynchronizationContext. An aborted dispatcher operation is
    /// observed; shutdown never waits for native BitBlt or accessibility reads.
    /// </summary>
    private Task<bool> DispatchAsync(Func<bool> step)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return Task.FromResult(false);
        return _dispatcher.InvokeAsync(step).Task.ContinueWith(completed =>
        {
            try
            {
                return completed.GetAwaiter().GetResult();
            }
            catch (TaskCanceledException) when (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                // Shutdown invalidates the preparation before any late window can be shown.
                return false;
            }
            catch (Exception ex)
            {
                // A feedback subscriber must not turn a fire-and-observed transition into an
                // unobserved task failure. Session cleanup runs in each step's finally.
                _log.LogError(ex, "Capture preparation dispatcher transition failed");
                return false;
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Shows the selector over its frozen frame and ends the preparation; from here the selector
    /// owns the session. Returns whether the selector is showing.
    /// </summary>
    private bool ShowPreparedFrame(CapturePreparation preparation, FrozenFrame? frame, Exception? failure,
        Task<IReadOnlyList<WindowCandidate>>? confirmedWindows)
    {
        VerifyDispatcherAccess();
        SelectorTimeline.Mark?.Invoke("frame-dispatched");
        if (!ReferenceEquals(_preparation, preparation)) return false;
        try
        {
            if (_disposed || preparation.Cancelled) return false;
            if (failure is not null) throw failure;
            FrozenFrame acquired = frame ?? throw new InvalidOperationException("Capture acquisition returned no frame.");
            if (_activeOverlay is { } overlay)
            {
                // Geometry, z-order and titles are final; page and application details may still be read.
                overlay.SourceWindows = preparation.SourceWindows;
                if (confirmedWindows is not null) overlay.AwaitSourceDetails(confirmedWindows);
                overlay.AttachFrame(acquired);
                SelectorTimeline.Mark?.Invoke("frame-attached");
                if (!overlay.IsVisible)
                {
                    PrepareWindow(overlay);
                    overlay.Show();
                    SelectorTimeline.Mark?.Invoke("shown");
                    _ = overlay.Activate();
                    SelectorTimeline.Mark?.Invoke("activated");
                }

                return true;
            }

            ShowOverlay(acquired.ScreenBounds, abortOnFocusLoss: false, showMagnifier: false, preparation.Elapsed, acquired,
                preparation.ShowSelectionGrid);
            if (_activeOverlay is not { } shown) return false;
            if (confirmedWindows is not null) shown.AwaitSourceDetails(confirmedWindows);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not prepare the capture overlay");
            _activeOverlay?.Close();
            TransitionFailed?.Invoke(ex);
            return false;
        }
        finally
        {
            _preparation = null;
            EndSessionIfIdle();
        }
    }

    private void ShowOverlay(
        RectD screenBounds,
        bool abortOnFocusLoss,
        bool showMagnifier,
        Stopwatch elapsed,
        FrozenFrame? frame,
        bool showSelectionGrid)
    {
        var overlay = frame is null
            ? new CaptureOverlayWindow(screenBounds, abortOnFocusLoss, showMagnifier, showSelectionGrid: showSelectionGrid)
            : new CaptureOverlayWindow(frame, abortOnFocusLoss, showMagnifier, showSelectionGrid);
        SelectorTimeline.Mark?.Invoke("window-constructed");
        _activeOverlay = overlay;
        overlay.SourceWindows = _preparation?.SourceWindows ?? [];
        overlay.DetectWindows = AutoDetectWindows();
        overlay.ShowShortcutHints = ShowShortcutHints();
        overlay.CopyColorText = CopyColorText;
        overlay.ColorCopied = feedback =>
        {
            if (!_disposed) ColorCopied?.Invoke(feedback);
        };
        overlay.ReadColorFormat = ReadColorFormat;
        overlay.TextCaptureAvailable = TextCaptureRequested is not null;
        overlay.TextCaptureMode = _preparation?.TextCapture == true;
        // The screenshot selector always offers its modes as a bar; recording keeps the plain selector.
        overlay.ShowModeBar = true;
        overlay.CanRepeatLastRegion = CanRepeatLastRegion;
        overlay.OffersOtherActions = SelectorActionRequested is not null;
        overlay.ShowStartGuide = ClaimStartGuide();
        overlay.SelectionCompleted += OnOverlaySelectionCompleted;
        overlay.ActionRequested += OnOverlayActionRequested;
        overlay.Closed += OnOverlayClosed;
        overlay.ContentRendered += OnFirstRendered;
        void OnFirstRendered(object? sender, EventArgs args)
        {
            overlay.ContentRendered -= OnFirstRendered;
            _log.LogInformation("Capture overlay first rendered after {Elapsed:0.0}ms", elapsed.Elapsed.TotalMilliseconds);
        }

        _log.LogInformation(
            "Opening free-region selector across virtual desktop ({Width}x{Height})",
            (int)screenBounds.Width,
            (int)screenBounds.Height);

        try
        {
            bool excluded = ApplyCaptureExclusion(overlay);
            SelectorTimeline.Mark?.Invoke("hwnd-created-exclusion-applied");
            if (RequiresCaptureExclusion?.Invoke() == true && !excluded)
            {
                throw new InvalidOperationException(UiText.Get("Text_D1F0DAEAA780"));
            }

            if (frame is not null)
            {
                overlay.Show();
            }
        }
        catch
        {
            overlay.Close();
            throw;
        }
        if (overlay.IsVisible) _ = overlay.Activate();
    }

    internal bool StartWithSelection(FrozenFrame frame, RectD region) =>
        StartWithSelection(frame, region, sourceTitle: string.Empty, recordForRepeat: false);

    internal bool StartWithSelection(FrozenFrame frame, RectD region, string sourceTitle) =>
        StartWithSelection(frame, region, sourceTitle, recordForRepeat: false);

    /// <summary>
    /// Opens the normal editor directly for an advanced capture whose physical region is already
    /// known. No full-screen overlay is created.
    /// </summary>
    /// <param name="pendingSource">
    /// Page and application still being read; the capture is saved and opened without waiting for them
    /// (<see cref="CaptureSelectionCompletedEventArgs.PendingSource"/>).
    /// </param>
    internal bool StartWithSelection(
        FrozenFrame frame,
        RectD region,
        string sourceTitle,
        bool recordForRepeat,
        string sourcePageUrl = "",
        MyCapture.Core.Capture.CaptureSourceDetails? sourceDetails = null,
        Task<CaptureSelectionSource>? pendingSource = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        VerifyDispatcherAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_isOpeningEditor || (_preparation is not null && _activeOverlay is not { IsVisible: true }))
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
            sourcePageUrl: sourcePageUrl)
        {
            SourceDetails = sourceDetails ?? MyCapture.Core.Capture.CaptureSourceDetails.Empty,
            PendingSource = pendingSource,
        };

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
        if (_editorScreenCapture is { } nested)
        {
            nested.Cancel();
            return;
        }
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
        _editorScreenCapture?.Dispose();
        _editorScreenCapture = null;
        Cancel();
        _preparation = null;
    }

    private void OnDispatcherShutdownStarted(object? sender, EventArgs e) => Dispose();

    private sealed class CapturePreparation
    {
        internal RectD DesktopBounds { get; set; }
        internal bool ShowSelectionGrid { get; init; } = true;
        internal bool TextCapture { get; init; }
        internal volatile bool Cancelled;
        internal IReadOnlyList<WindowCandidate> SourceWindows { get; set; } = [];
        internal Stopwatch Elapsed { get; } = Stopwatch.StartNew();
    }

    internal bool ActivateCurrent()
    {
        if (_editorScreenCapture is { } nested) return nested.ActivateCurrent();
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
        if (e.TextCapture && TextCaptureRequested is { } textCapture)
        {
            // Text capture keeps only the recognised text: no library record, no editor. The
            // selector closes right after this returns, ending the session before OCR runs.
            _log.LogInformation("Selected region {Region} for text capture", e.BitmapRegion);
            try
            {
                textCapture(e);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Could not start text capture");
                TransitionFailed?.Invoke(ex);
            }

            return;
        }

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

            if (!OpenEditorAfterCapture()) return;

            var editor = new AnnotationEditorWindow(
                selection.Frame,
                selection.BitmapRegion,
                selection.SelectedBitmap,
                privacyRedactionService: PrivacyRedactionService,
                sourceWindowTitle: selection.SourceTitle,
                provenance: CaptureSelectionProvenance.Current(selection),
                pendingProvenance: CaptureSelectionProvenance.Pending(selection));
            _activeEditor = editor;
            if (CaptureMonitor(selection) is { } monitor) editor.PlaceOnMonitor(monitor);
            // Done leaves the screen once the clipboard holds the result; the save finishes behind
            // it and the session ends when the editor closes, as before.
            editor.FinishesOutOfSight = true;
            editor.DraftRecordSource = DraftRecord;
            if (OriginalNeedsRecovery()) editor.MarkUnpersistedCapture();
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

    /// <summary>Reads the displays when the editor opens. Tests replace it.</summary>
    internal Func<IReadOnlyList<MonitorInfo>> ReadMonitors { get; set; } = MonitorEnumerator.GetAll;

    /// <summary>The monitor the selection was taken on: the one showing most of it.</summary>
    private MonitorInfo? CaptureMonitor(CaptureSelectionCompletedEventArgs selection)
    {
        RectD frame = selection.Frame.ScreenBounds;
        var region = new RectD(frame.Left + selection.BitmapRegion.Left, frame.Top + selection.BitmapRegion.Top,
            selection.BitmapRegion.Width, selection.BitmapRegion.Height);
        try
        {
            return MonitorWindowPlacement.MonitorFor(ReadMonitors(), region) ?? selection.Frame.Monitor;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Could not read the displays for the editor; it opens on the pointer's monitor");
            return selection.Frame.Monitor;
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

    private void OnOverlayActionRequested(object? sender, CaptureSelectorRequest request) => _pendingSelectorAction = request;

    private void OnOverlayClosed(object? sender, EventArgs e)
    {
        if (sender is CaptureOverlayWindow overlay)
        {
            overlay.SelectionCompleted -= OnOverlaySelectionCompleted;
            overlay.ActionRequested -= OnOverlayActionRequested;
            overlay.Closed -= OnOverlayClosed;
            if (ReferenceEquals(_activeOverlay, overlay))
            {
                _activeOverlay = null;
            }
        }

        EndSessionIfIdle();
    }

    private CaptureSelectorRequest? _pendingSelectorAction;

    internal Task LastSelectorActionForTest { get; private set; } = Task.CompletedTask;

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
        if (!_disposed && _preparation is null && _activeOverlay is null && _activeEditor is null && !_isOpeningEditor && _editorScreenCapture is null)
        {
            _lastPresentationDismissedTick = Environment.TickCount64;
            OverlayClosed?.Invoke(this, EventArgs.Empty);
            // A command chosen on the selector starts on a later turn, after the session has ended:
            // the app's own guards then see no active selector, and its frame waits out this dismissal.
            if (_pendingSelectorAction is { } request)
            {
                _pendingSelectorAction = null;
                if (SelectorActionRequested is { } run)
                    LastSelectorActionForTest = _dispatcher.InvokeAsync(() => run(request), DispatcherPriority.Normal).Task;
            }
        }
    }
}
