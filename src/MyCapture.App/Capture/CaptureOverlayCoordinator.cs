using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
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
    private CapturePreparation? _preparation;
    private bool _disposed;
    private CaptureOverlayWindow? _activeOverlay;
    private NativeCaptureOverlay? _nativeOverlay;
    internal bool UseNativeDibSelector { get; set; } = true;
    internal NativeSelectorChromeStrategy NativeChromeStrategy { get; set; } = NativeSelectorChromeStrategy.Retained;
    internal NativeCaptureOverlay? NativeOverlayForProbe => _nativeOverlay;
    internal Func<RectD, bool, bool, NativeCaptureOverlay> CreateNativeSelector { get; set; } =
        (bounds, abortOnFocusLoss, showMagnifier) => new(bounds, abortOnFocusLoss, showMagnifier);
    internal Action<NativeCaptureOverlay> ShowNativeSelector { get; set; } = native => native.Show();
    private SelectorPresentation? _nativePresentation;
    private bool _startingNative;
    private bool _handlingNativeFailure;
    private bool _practiceCancellationIssued;
    private AnnotationEditorWindow? _activeEditor;
    private CaptureOverlayCoordinator? _editorScreenCapture;
    private bool _isOpeningEditor;
    private CancellationTokenSource? _openingEditorCts;

    /// <summary>
    /// Unexcluded dismissals need time to leave composition. Excluded dismissals do not
    /// delay the grab, and never mask an earlier unexcluded dismissal in the same window.
    /// </summary>
    private PresentationDismissals _presentationDismissals = new();

    /// <summary>How long after a dismissal a capture still waits out the presentation.</summary>
    internal const int PresentationSettleMs = 750;

    internal CaptureOverlayCoordinator(
        ScreenCaptureEngine captureEngine,
        WindowCandidateService windowCandidates,
        ILogger<CaptureOverlayCoordinator> log,
        Func<bool, FrozenFrame>? acquireFrame = null,
        Func<RectD>? desktopBounds = null,
        Func<RectD, IReadOnlyList<WindowCandidate>>? readWindowCandidates = null)
        : this(captureEngine, windowCandidates, log, acquireFrame, desktopBounds, readWindowCandidates,
            new BrowserPageUrlService(waitForBusyProbe: true).ReadAsync,
            new WindowSourceService(waitForBusyProbe: true).ReadAsync)
    {
    }

    private CaptureOverlayCoordinator(
        ScreenCaptureEngine captureEngine,
        WindowCandidateService windowCandidates,
        ILogger<CaptureOverlayCoordinator> log,
        Func<bool, FrozenFrame>? acquireFrame,
        Func<RectD>? desktopBounds,
        Func<RectD, IReadOnlyList<WindowCandidate>>? readWindowCandidates,
        Func<IReadOnlyList<WindowCandidate>, Task<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>> readPageUrls,
        Func<IReadOnlyList<WindowCandidate>, Task<IReadOnlyDictionary<IntPtr, WindowSourceSnapshot>>>? readWindowSources)
    {
        _captureEngine = captureEngine ?? throw new ArgumentNullException(nameof(captureEngine));
        _windowCandidates = windowCandidates ?? throw new ArgumentNullException(nameof(windowCandidates));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _acquireFrame = acquireFrame ?? captureEngine.CaptureVirtualDesktop;
        _desktopBounds = desktopBounds ?? MonitorEnumerator.GetVirtualDesktopBounds;
        _readWindowCandidates = readWindowCandidates ?? windowCandidates.GetCandidates;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _dispatcher.ShutdownStarted += OnDispatcherShutdownStarted;
        // Nested captures retain the parent's readers directly. Only a root coordinator creates
        // the default services; their probes never block the frozen-frame presentation.
        ReadWindowSources = readWindowSources;
        ReadPageUrls = readPageUrls;
    }

    /// <summary>
    /// Reads each exposed browser window's page document around the frozen frame. Tests replace it.
    /// </summary>
    internal Func<IReadOnlyList<WindowCandidate>, Task<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>> ReadPageUrls { get; set; }

    internal Func<CaptureSelectionCompletedEventArgs, Task>? SelectionPersistRequested { get; set; }
    internal Func<CaptureSelectionCompletedEventArgs, Task>? EditorScreenCaptureRequested { get; set; }
    internal Func<string?>? EditorSourcePath { get; set; }

    /// <summary>
    /// Reads each exposed window's application and Explorer folder around the frozen frame. Tests
    /// replace it; null skips source metadata entirely.
    /// </summary>
    internal Func<IReadOnlyList<WindowCandidate>, Task<IReadOnlyDictionary<IntPtr, WindowSourceSnapshot>>>? ReadWindowSources { get; set; }

    internal Func<bool> OpenEditorAfterCapture { get; set; } = () => true;
    internal Func<bool> OriginalNeedsRecovery { get; set; } = () => false;

    /// <summary>
    /// Existing visible editors/library keep their work on screen. A capture started over them uses
    /// separate persistence and a receipt, regardless of the normal after-capture preference.
    /// This reads only tracked managed window state; no desktop enumeration or rendering occurs.
    /// </summary>
    internal Func<bool> HasVisiblePreservedWindow { get; set; } = () => CaptureEditorRegistry.HasVisibleEditor;

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
    internal Func<bool, bool> ClaimKeyPanel { get; set; } = _ => false;
    internal Func<Hotkey> ReadPasteToScreenHotkey { get; set; } = () => Hotkey.None;

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
    internal Func<IntPtr, bool> ApplyNativeCaptureExclusion { get; set; } = CaptureWindowExclusion.TryApply;
    internal event Action<Exception>? TransitionFailed;

    private void PrepareWindow(Window window)
    {
        if (RequiresCaptureExclusion?.Invoke() == true && !ApplyCaptureExclusion(window))
            throw new InvalidOperationException(UiText.Get("Text_D1F0DAEAA780"));
    }

    internal event EventHandler<AnnotationEditingResult>? EditingCompleted;

    /// <summary>Raised when the complete selector/editor session has ended.</summary>
    internal event EventHandler? OverlayClosed;
    internal event Action? SessionStarted;
    private readonly Dictionary<object, PracticeCaptureTarget> _practiceSelectors = [];
    private readonly HashSet<object> _practiceSelections = [];
    private PracticeCaptureTarget? _persistingPracticeCapture;

    internal bool OwnsPracticeCapture(PracticeCaptureTarget? target) => target is { } capture &&
        (_preparation?.PracticeTarget == capture || _practiceSelectors.Values.Contains(capture)
            || _persistingPracticeCapture == capture || _editorScreenCapture?.OwnsPracticeCapture(capture) == true);

    internal Func<AnnotationEditingResult, Task<bool>>? CommitRequested { get; set; }

    internal bool IsActive => !_disposed &&
        (_preparation is not null || _activeOverlay is not null || _nativeOverlay is not null || _activeEditor is not null || _isOpeningEditor || _editorScreenCapture is not null);

    internal Task LastTransitionForTest { get; private set; } = Task.CompletedTask;
    internal Task LastPreparationForTest { get; private set; } = Task.CompletedTask;

    internal ScreenCaptureEngine Engine => _captureEngine;
    internal bool IsSelecting => _preparation is not null || _activeOverlay is not null || _nativeOverlay is not null || _editorScreenCapture?.IsSelecting == true;
    internal AnnotationEditorControl? CurrentEditor => _activeEditor?.Editor;
    internal void ConfirmSelectionAsPin()
    {
        VerifyDispatcherAccess();
        if (_disposed || _editorScreenCapture is not null || _activeEditor is not null) return;
        _nativeOverlay?.ConfirmSelectionAsPin();
        _activeOverlay?.ConfirmSelectionAsPin();
    }
    internal Task WaitForPresentationDismissalAsync() =>
        _presentationDismissals.NeedsSettle(Environment.TickCount64)
            ? Task.Delay(100) : Task.CompletedTask;

    internal bool CanRetake => !_disposed && _activeEditor is { } editor && !IsEditorSaving(editor) && !IsEditorBusy(editor) &&
        !editor.IsAskingToClose && _preparation is null && !_isOpeningEditor && _editorScreenCapture is null;

    internal bool CanAcceptSelection => !IsActive || (CanRetake && EditorScreenCaptureRequested is not null);

    private bool CanCaptureOverPreservedWindow => CanRetake || (!IsActive && HasVisiblePreservedWindow());

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
        if (!_disposed && _nativeOverlay is { IsVisible: true } native) { native.Activate(); return true; }
        if (_disposed || _activeOverlay is not { IsVisible: true } overlay) return false;
        _ = overlay.Activate();
        return true;
    }

    /// <summary>Used only by the explicit advanced “capture window” command.</summary>
    internal WindowCandidateService WindowCandidates => _windowCandidates;

    internal void Start(bool includeCursor, bool abortOnFocusLoss, bool showMagnifier, bool showSelectionGrid = true,
        bool textCapture = false, bool preserveEditor = false, PracticeCaptureTarget? practiceTarget = null)
    {
        VerifyDispatcherAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        SelectorTimeline.Mark?.Invoke("start");
        if (_editorScreenCapture is { } current)
        {
            current.ActivateCurrent();
            return;
        }
        if (preserveEditor && CanCaptureOverPreservedWindow && EditorScreenCaptureRequested is { } persist)
        {
            StartOverEditor(persist, includeCursor, abortOnFocusLoss, showMagnifier, showSelectionGrid, textCapture, practiceTarget);
            return;
        }

        // Until the selector shows its frozen frame, the preparation ignores new requests.
        if (_isOpeningEditor || (_preparation is not null && _activeOverlay is not { IsVisible: true } && _nativeOverlay is not { IsVisible: true }))
        {
            _log.LogInformation("Capture ignored while a frame or selection is being prepared");
            return;
        }

        // The text capture command over an open selector switches it to text mode.
        if (textCapture && _activeOverlay is { TextCaptureAvailable: true } open) open.TextCaptureMode = true;
        if (textCapture && _nativeOverlay is { TextCaptureAvailable: true } native) native.TextCaptureMode = true;

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
            PracticeTarget = practiceTarget,
        };
        _practiceCancellationIssued = false;
        _preparation = preparation;
        SessionStarted?.Invoke();
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
            bool settle = _presentationDismissals.NeedsSettle(Environment.TickCount64);
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
            CancelPractice(preparation.PracticeTarget);
            _preparation = null;
            EndSessionIfIdle();
            throw;
        }
    }

    internal static bool NeedsPresentationSettle(long currentTick, long lastDismissedTick)
        => PresentationDismissals.IsRecent(currentTick, lastDismissedTick);

    private void StartOverEditor(Func<CaptureSelectionCompletedEventArgs, Task> persist,
        bool includeCursor, bool abortOnFocusLoss, bool showMagnifier, bool showSelectionGrid, bool textCapture, PracticeCaptureTarget? practiceTarget)
    {
        CaptureOverlayCoordinator nested = CreateCaptureOverEditor(persist);
        try
        {
            nested.Start(includeCursor, abortOnFocusLoss, showMagnifier, showSelectionGrid, textCapture, practiceTarget: practiceTarget);
            LastPreparationForTest = nested.LastPreparationForTest;
        }
        catch
        {
            _editorScreenCapture = null;
            nested.Dispose();
            throw;
        }
    }

    private CaptureOverlayCoordinator CreateCaptureOverEditor(Func<CaptureSelectionCompletedEventArgs, Task> persist)
    {
        var nested = new CaptureOverlayCoordinator(_captureEngine, _windowCandidates, _log,
            _acquireFrame, _desktopBounds, _readWindowCandidates, ReadPageUrls, ReadWindowSources)
        {
            SelectionPersistRequested = persist,
            OpenEditorAfterCapture = () => false,
            AutoDetectWindows = AutoDetectWindows,
            ShowShortcutHints = ShowShortcutHints,
            RequiresCaptureExclusion = RequiresCaptureExclusion,
            ApplyCaptureExclusion = ApplyCaptureExclusion,
            ApplyNativeCaptureExclusion = ApplyNativeCaptureExclusion,
            UseNativeDibSelector = UseNativeDibSelector,
            NativeChromeStrategy = NativeChromeStrategy,
            CreateNativeSelector = CreateNativeSelector,
            ShowNativeSelector = ShowNativeSelector,
            TextCaptureRequested = TextCaptureRequested,
            SelectorActionRequested = SelectorActionRequested,
            CanRepeatLastRegion = CanRepeatLastRegion,
            CopyColorText = CopyColorText,
            ColorCopied = ColorCopied,
            ReadColorFormat = ReadColorFormat,
        };
        nested._presentationDismissals = _presentationDismissals;
        nested.SessionStarted += () => SessionStarted?.Invoke();
        _editorScreenCapture = nested;
        nested.TransitionFailed += error => TransitionFailed?.Invoke(error);
        nested.OverlayClosed += (_, _) =>
        {
            if (ReferenceEquals(_editorScreenCapture, nested)) _editorScreenCapture = null;
            nested.Dispose();
            if (_activeEditor is { IsVisible: true } editor) editor.Activate();
            EndSessionIfIdle();
        };
        return nested;
    }

    /// <summary>Unknown or unexcluded surfaces retain the conservative presentation wait.</summary>
    internal long LastPresentationDismissalTick => _presentationDismissals.LastDismissedTick;

    internal void NoteForegroundDismissal() => NoteForegroundDismissal(captureExcluded: false);

    internal void NoteForegroundDismissal(bool captureExcluded) =>
        _presentationDismissals.Record(Environment.TickCount64, captureExcluded);

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
                SelectorTimeline.Mark?.Invoke("source-frame-ready");
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
            if (_disposed || preparation.Cancelled) { CancelPractice(preparation.PracticeTarget); return false; }
            if (failure is not null) throw failure;
            FrozenFrame acquired = frame ?? throw new InvalidOperationException("Capture acquisition returned no frame.");
            if (_nativeOverlay is { } native)
            {
                SelectorPresentation presentation = _nativePresentation!;
                presentation.Frame = acquired;
                presentation.SourceWindows = preparation.SourceWindows;
                presentation.SourceDetails = confirmedWindows;
                try
                {
                    _startingNative = true;
                    // Recording may have started while the frame was being acquired.
                    native.RequireCaptureExclusion = RequiresCaptureExclusion?.Invoke() == true;
                    native.Prepare(ApplyNativeCaptureExclusion);
                    native.SourceWindows = preparation.SourceWindows;
                    if (confirmedWindows is not null) native.AwaitSourceDetails(confirmedWindows);
                    native.AttachFrame(acquired);
                    ShowNativeSelector(native);
                }
                catch (Exception error)
                {
                    if (_disposed || preparation.Cancelled) native.Close();
                    else FallBackToWpf(presentation, error);
                }
                finally { _startingNative = false; }
                if (ReferenceEquals(_nativeOverlay, native) && native.IsClosed)
                    OnNativeOverlayClosed(native, EventArgs.Empty);
                return _nativeOverlay?.IsVisible == true || _activeOverlay?.IsVisible == true;
            }
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
            if (_nativeOverlay is { } shownNative)
            {
                if (confirmedWindows is not null) shownNative.AwaitSourceDetails(confirmedWindows);
                if (_nativePresentation is { } presentation) presentation.SourceDetails = confirmedWindows;
                return shownNative.IsVisible;
            }
            if (_activeOverlay is not { } shown) return false;
            if (confirmedWindows is not null) shown.AwaitSourceDetails(confirmedWindows);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not prepare the capture overlay");
            CancelPractice(preparation.PracticeTarget);
            _nativeOverlay?.Close();
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
        var presentation = new SelectorPresentation(screenBounds, abortOnFocusLoss, showMagnifier, elapsed, showSelectionGrid)
        {
            Frame = frame,
            PracticeTarget = _preparation?.PracticeTarget,
            SourceWindows = _preparation?.SourceWindows ?? [],
            TextCapture = _preparation?.TextCapture == true,
        };
        if (UseNativeDibSelector)
        {
            try
            {
                _startingNative = true;
                ShowNativeOverlay(presentation);
            }
            catch (Exception error)
            {
                if (_disposed || _preparation?.Cancelled == true) _nativeOverlay?.Close();
                else FallBackToWpf(presentation, error);
            }
            finally { _startingNative = false; }
            if (_nativeOverlay is { IsClosed: true } closed) OnNativeOverlayClosed(closed, EventArgs.Empty);
            return;
        }
        ShowWpfOverlay(presentation);
    }

    private void ShowWpfOverlay(SelectorPresentation presentation)
    {
        RectD screenBounds = presentation.Bounds;
        FrozenFrame? frame = presentation.Frame;
        var overlay = new CaptureOverlayWindow(screenBounds, presentation.AbortOnFocusLoss,
            presentation.ShowMagnifier, showSelectionGrid: presentation.ShowSelectionGrid);
        _activeOverlay = overlay;
        if (presentation.PracticeTarget is { } practice) _practiceSelectors[overlay] = practice;
        overlay.Closed += OnOverlayClosed;
        void OnFirstRendered(object? sender, EventArgs args)
        {
            overlay.ContentRendered -= OnFirstRendered;
            if (!presentation.PracticeReady && _practiceSelectors.TryGetValue(overlay, out var practice))
            {
                presentation.PracticeReady = true;
                PracticeSignals.Publish(new(PracticeEventKind.SelectorReady, practice.SessionId, practice.RequestId));
            }
            _log.LogInformation("Capture overlay first rendered after {Elapsed:0.0}ms", presentation.Elapsed.Elapsed.TotalMilliseconds);
        }

        try
        {
            SelectorTimeline.Mark?.Invoke("window-constructed");
            overlay.SourceWindows = presentation.SourceWindows;
            if (presentation.SourceDetails is { } sourceDetails) overlay.AwaitSourceDetails(sourceDetails);
            overlay.DetectWindows = AutoDetectWindows();
            overlay.ShowShortcutHints = ShowShortcutHints();
            overlay.CopyColorText = CopyColorText;
            overlay.ColorCopied = feedback =>
            {
                if (!_disposed) ColorCopied?.Invoke(feedback);
            };
            overlay.ReadColorFormat = ReadColorFormat;
            overlay.TextCaptureAvailable = TextCaptureRequested is not null;
            overlay.TextCaptureMode = presentation.TextCapture;
            // The screenshot selector always offers its modes as a bar; recording keeps the plain selector.
            overlay.ShowModeBar = true;
            overlay.CanRepeatLastRegion = CanRepeatLastRegion;
            overlay.OffersOtherActions = SelectorActionRequested is not null;
            overlay.ShowStartGuide = ClaimStartGuide();
            overlay.ClaimKeyPanel = ClaimKeyPanel;
            overlay.ReadPasteToScreenHotkey = ReadPasteToScreenHotkey;
            // F3 pins the chosen region (ConfirmSelectionAsPin), except while an editor is open for a retake.
            overlay.PinSelectionAvailable = _editorScreenCapture is null && _activeEditor is null;
            overlay.SelectionCompleted += OnOverlaySelectionCompleted;
            overlay.ActionRequested += OnOverlayActionRequested;
            overlay.ContentRendered += OnFirstRendered;

            _log.LogInformation(
                "Opening free-region selector across virtual desktop ({Width}x{Height})",
                (int)screenBounds.Width,
                (int)screenBounds.Height);

            bool excluded = ApplyCaptureExclusion(overlay);
            if (_disposed || _preparation?.Cancelled == true || !ReferenceEquals(_activeOverlay, overlay)) return;
            CaptureWindowExclusion.RecordResult(overlay, excluded);
            SelectorTimeline.Mark?.Invoke("hwnd-created-exclusion-applied");
            if (RequiresCaptureExclusion?.Invoke() == true && !excluded)
            {
                throw new InvalidOperationException(UiText.Get("Text_D1F0DAEAA780"));
            }

            if (frame is not null)
            {
                overlay.AttachFrame(frame);
                overlay.Show();
            }
            if (overlay.IsVisible) _ = overlay.Activate();
        }
        catch
        {
            bool cancelled = _disposed || _preparation?.Cancelled == true || !ReferenceEquals(_activeOverlay, overlay);
            overlay.Close();
            if (cancelled) return;
            throw;
        }
    }

    private void ShowNativeOverlay(SelectorPresentation presentation)
    {
        _nativePresentation = presentation;
        var native = CreateNativeSelector(presentation.Bounds, presentation.AbortOnFocusLoss, presentation.ShowMagnifier);
        _nativeOverlay = native;
        native.ChromeStrategy = NativeChromeStrategy;
        native.RequireCaptureExclusion = RequiresCaptureExclusion?.Invoke() == true;
        native.SourceWindows = presentation.SourceWindows;
        if (presentation.SourceDetails is { } sourceDetails) native.AwaitSourceDetails(sourceDetails);
        native.CopyColorText = CopyColorText;
        native.ColorCopied = feedback => { if (!_disposed) ColorCopied?.Invoke(feedback); };
        native.ReadColorFormat = ReadColorFormat;
        native.TextCaptureAvailable = TextCaptureRequested is not null;
        native.TextCaptureMode = presentation.TextCapture;
        native.DetectWindows = AutoDetectWindows();
        native.OffersOtherActions = SelectorActionRequested is not null;
        native.CanRepeatLastRegion = CanRepeatLastRegion;
        if (presentation.PracticeTarget is { } practice) _practiceSelectors[native] = practice;
        native.SelectionCompleted += OnOverlaySelectionCompleted;
        native.InputReady += OnNativeInputReady;
        native.ActionRequested += OnOverlayActionRequested;
        native.Failed += OnNativeFailed;
        native.Closed += OnNativeOverlayClosed;
        native.Prepare(ApplyNativeCaptureExclusion);
        if (presentation.Frame is { } frame) { native.AttachFrame(frame); ShowNativeSelector(native); }
    }

    private void OnNativeOverlayClosed(object? sender, EventArgs args)
    {
        if (sender is NativeCaptureOverlay native)
        {
            // Show closes its HWND before throwing. Its caller still owns the same capture
            // until it either finishes startup or installs the WPF replacement.
            if (_startingNative && ReferenceEquals(_nativeOverlay, native)) return;
            if (_practiceSelectors.Remove(native, out var practice) && !_practiceSelections.Remove(native))
                CancelPractice(practice);
            NoteForegroundDismissal(native.CaptureExcluded);
            native.SelectionCompleted -= OnOverlaySelectionCompleted;
            native.InputReady -= OnNativeInputReady;
            native.ActionRequested -= OnOverlayActionRequested;
            native.Failed -= OnNativeFailed;
            native.Closed -= OnNativeOverlayClosed;
            if (ReferenceEquals(_nativeOverlay, native)) { _nativeOverlay = null; _nativePresentation = null; }
        }
        EndSessionIfIdle();
    }

    private void OnNativeInputReady(object? sender, EventArgs args)
    {
        if (sender is NativeCaptureOverlay native)
        {
            native.InputReady -= OnNativeInputReady;
            if (_practiceSelectors.TryGetValue(native, out var practice))
            {
                if (_nativePresentation is { } presentation) presentation.PracticeReady = true;
                PracticeSignals.Publish(new(PracticeEventKind.SelectorReady, practice.SessionId, practice.RequestId));
            }
        }
    }

    private void OnNativeFailed(Exception error)
    {
        if (_nativePresentation is not { } presentation) return;
        if (_disposed || _preparation?.Cancelled == true) return;
        _handlingNativeFailure = true;
        try { FallBackToWpf(presentation, error); }
        catch (Exception failure)
        {
            _log.LogError(failure, "Could not show the fallback capture overlay");
            CancelPractice(presentation.PracticeTarget);
            _activeOverlay?.Close();
            TransitionFailed?.Invoke(failure);
        }
        finally
        {
            _handlingNativeFailure = false;
            EndSessionIfIdle();
        }
    }

    private void FallBackToWpf(SelectorPresentation presentation, Exception error)
    {
        _log.LogWarning(error, "Native capture selector failed; using the WPF selector for this capture");
        if (_nativeOverlay is { } native)
        {
            if (native.Frame is not null) presentation.TextCapture = native.TextCaptureMode;
            native.SelectionCompleted -= OnOverlaySelectionCompleted;
            native.InputReady -= OnNativeInputReady;
            native.ActionRequested -= OnOverlayActionRequested;
            native.Failed -= OnNativeFailed;
            native.Closed -= OnNativeOverlayClosed;
            _practiceSelectors.Remove(native);
            _practiceSelections.Remove(native);
            _nativeOverlay = null;
            native.Close();
        }
        _nativePresentation = null;
        ShowWpfOverlay(presentation);
    }

    private sealed class SelectorPresentation(RectD bounds, bool abortOnFocusLoss, bool showMagnifier,
        Stopwatch elapsed, bool showSelectionGrid)
    {
        internal RectD Bounds { get; } = bounds;
        internal bool AbortOnFocusLoss { get; } = abortOnFocusLoss;
        internal bool ShowMagnifier { get; } = showMagnifier;
        internal bool ShowSelectionGrid { get; } = showSelectionGrid;
        internal Stopwatch Elapsed { get; } = elapsed;
        internal FrozenFrame? Frame { get; set; }
        internal PracticeCaptureTarget? PracticeTarget { get; init; }
        internal IReadOnlyList<WindowCandidate> SourceWindows { get; set; } = [];
        internal Task<IReadOnlyList<WindowCandidate>>? SourceDetails { get; set; }
        internal bool TextCapture { get; set; }
        internal bool PracticeReady { get; set; }
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
        Task<CaptureSelectionSource>? pendingSource = null,
        bool preserveEditor = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        VerifyDispatcherAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A nested capture can be persisting after its selector has closed, with no window to
        // activate. It still owns the capture session and must reject a second advanced capture.
        if (_editorScreenCapture is { } current)
        {
            current.ActivateCurrent();
            return false;
        }

        if (preserveEditor && CanCaptureOverPreservedWindow && EditorScreenCaptureRequested is { } persist)
        {
            CaptureOverlayCoordinator nested = CreateCaptureOverEditor(persist);
            try
            {
                bool started = nested.StartWithSelection(frame, region, sourceTitle, recordForRepeat,
                    sourcePageUrl, sourceDetails, pendingSource);
                LastTransitionForTest = nested.LastTransitionForTest;
                return started;
            }
            catch
            {
                _editorScreenCapture = null;
                nested.Dispose();
                throw;
            }
        }

        if (_isOpeningEditor || (_preparation is not null && _activeOverlay is not { IsVisible: true } && _nativeOverlay is not { IsVisible: true }))
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
        if (_nativeOverlay is not null)
        {
            _nativeOverlay.Close();
        }
        else if (_activeOverlay is not null)
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
        internal PracticeCaptureTarget? PracticeTarget { get; init; }
        internal volatile bool Cancelled;
        internal IReadOnlyList<WindowCandidate> SourceWindows { get; set; } = [];
        internal Stopwatch Elapsed { get; } = Stopwatch.StartNew();
    }

    internal bool ActivateCurrent()
    {
        if (_editorScreenCapture is { } nested) return nested.ActivateCurrent();
        if (_nativeOverlay is { } native) { native.Activate(); return true; }
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
        if (sender is not null && _practiceSelectors.TryGetValue(sender, out var practice))
        { e.PracticeTarget = practice; _practiceSelections.Add(sender); }
        if (e.TextCapture && TextCaptureRequested is { } textCapture)
        {
            CancelPractice(e.PracticeTarget);
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
        _persistingPracticeCapture = selection.PracticeTarget;
        SessionStarted?.Invoke();
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

            if (selection.PinRequested || !OpenEditorAfterCapture()) return;

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
            editor.Editor.SourceFilePath = EditorSourcePath?.Invoke();
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
            CancelPractice(selection.PracticeTarget);
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
                _persistingPracticeCapture = null;
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

    private void CancelPractice(PracticeCaptureTarget? target)
    {
        if (target is { } capture && !_practiceCancellationIssued)
        {
            _practiceCancellationIssued = true;
            PracticeSignals.Publish(new(PracticeEventKind.CaptureCancelled, capture.SessionId, capture.RequestId));
        }
    }

    private void OnEditorCancelled(object? sender, EventArgs e) =>
        _log.LogInformation("Standalone capture editor cancelled");

    private void OnOverlayActionRequested(object? sender, CaptureSelectorRequest request) => _pendingSelectorAction = request;

    private void OnOverlayClosed(object? sender, EventArgs e)
    {
        if (sender is CaptureOverlayWindow overlay)
        {
            if (_practiceSelectors.Remove(overlay, out var practice) && !_practiceSelections.Remove(overlay))
                CancelPractice(practice);
            NoteForegroundDismissal(CaptureWindowExclusion.WasApplied(overlay));
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
            // Editors retain the wait even when recording temporarily applies exclusion.
            NoteForegroundDismissal(captureExcluded: false);
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
        if (!_disposed && !_handlingNativeFailure && _preparation is null && _activeOverlay is null && _nativeOverlay is null && _activeEditor is null && !_isOpeningEditor && _editorScreenCapture is null)
        {
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
