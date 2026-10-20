using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Ocr;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Editing;

/// <summary>
/// Resizable, taskbar-visible editor host used after capture selection and for gallery re-edit.
/// </summary>
internal partial class AnnotationEditorWindow : Window
{
    private readonly AnnotationEditorControl _editor;
    private bool _committed;

    internal AnnotationEditorWindow(
        FrozenFrame sourceFrame,
        RectD sourceRegion,
        BitmapSource selectedBitmap,
        string? title = null,
        AnnotationDocument? initialDocument = null,
        IReadOnlyDictionary<string, BitmapSource>? initialAssets = null,
        IPrivacyRedactionService? privacyRedactionService = null,
        string? sourceWindowTitle = null,
        MonitorInfo? pointerMonitor = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFrame);
        ArgumentNullException.ThrowIfNull(selectedBitmap);

        StandardWindowTheme.Apply(this);

        Title = title ?? UiText.Get("Text_199EB510DA51");
        Background = Application.Current?.TryFindResource("Surface.Base") as Brush
            ?? new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x17));
        Foreground = Application.Current?.TryFindResource("Text.Primary") as Brush ?? Brushes.White;
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        FontFamily = Application.Current?.TryFindResource("Font.Ui") as FontFamily
            ?? new FontFamily("Segoe UI");
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _desiredSize = new Size(Math.Max(980, selectedBitmap.PixelWidth + 360), Math.Max(620, selectedBitmap.PixelHeight + 156));
        // CenterScreen opens an ownerless window on the pointer's monitor, so it is sized for that
        // monitor, not the primary one. A capture moves it to its own monitor (PlaceOnMonitor).
        // Tests pass the pointer's monitor so the size does not depend on where the pointer is.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        pointerMonitor ??= MonitorEnumerator.GetFromCursor();
        MonitorWindowPlacement.WindowFit fit = MonitorWindowPlacement.Fit(pointerMonitor.WorkArea,
            pointerMonitor.ScaleFactor, _desiredSize, MinimumSize, WorkAreaMargin);
        MinWidth = fit.Minimum.Width;
        MinHeight = fit.Minimum.Height;
        Width = fit.Size.Width;
        Height = fit.Size.Height;

        AutomationProperties.SetName(this, UiText.Get("Text_53DCBAF22F42"));
        // The window itself never takes text; while it briefly holds focus a Hangul-mode IME
        // must not start a hidden composition either (the editor's text boxes opt back in).
        InputMethod.SetIsInputMethodEnabled(this, false);

        _editor = new AnnotationEditorControl(
            sourceFrame,
            sourceRegion,
            selectedBitmap,
            initialDocument,
            initialAssets,
            privacyRedactionService,
            sourceWindowTitle);
        _editor.EditingCompleted += OnEditingCompleted;
        _editor.CloseRequested += OnCloseRequested;
        ConfirmClose = () => EditorCloseDialog.Ask(this);
        Content = _editor;

        ContentRendered += OnContentRendered;
        InitializeDraftRecovery();
    }

    private readonly Size _desiredSize;

    /// <summary>Smallest editor that still shows its tool rows and a usable canvas (DIPs).</summary>
    internal static readonly Size MinimumSize = new(680, 460);

    /// <summary>Space kept free around the editor across each dimension of the work area (DIPs).</summary>
    internal const double WorkAreaMargin = 64;

    /// <summary>
    /// Opens the editor centred in <paramref name="monitor"/>'s work area, sized for that monitor at its
    /// DPI, landscape or portrait. Call before <see cref="Window.Show"/>.
    /// </summary>
    internal void PlaceOnMonitor(MonitorInfo monitor) =>
        MonitorWindowPlacement.CenterOn(this, monitor, _desiredSize, MinimumSize, WorkAreaMargin);

    internal Func<AnnotationEditingResult, Task<bool>>? CommitRequested
    {
        get => _editor.CommitRequested;
        set => _editor.CommitRequested = value is null ? null : async result =>
        {
            AnnotationEditingResult still = FrameStillImage is null ? result : await FrameStillImage.CreateResultAsync(result);
            if (FinishesOutOfSight && result.Action == EditorCommitAction.Done) still.CopiedToClipboard = LeaveScreenWhileFinishing;
            bool closing = false;
            try
            {
                bool saved = await value(still);
                if (!saved || result.Action == EditorCommitAction.CopyToClipboard) return saved;
                closing = await RetireDraftBeforeCloseAsync();
                return closing;
            }
            finally
            {
                // A save that failed after the window left the screen shows it again, with the
                // failure in its status line and the draft kept, as when it never left.
                if (!closing) ReturnToScreen();
            }
        };
    }

    /// <summary>
    /// Lets Done take the window off the screen as soon as the clipboard holds the result, while
    /// the library save finishes. The window stays open (hidden) until the save completes, so the
    /// commit, its events and the draft keep their order; a failed save shows it again. Only for a
    /// modeless window: hiding a modal one would end its <see cref="Window.ShowDialog"/> early.
    /// </summary>
    internal bool FinishesOutOfSight { get; set; }

    private TaskCompletionSource? _finishingOutOfSight;

    /// <summary>True while Done's save finishes after the window has left the screen.</summary>
    internal bool IsFinishingOutOfSight => _finishingOutOfSight is not null;

    /// <summary>
    /// Completes when a save finishing out of sight has closed the window, or has shown it again
    /// after a failure; completed when no save is finishing out of sight.
    /// </summary>
    internal Task FinishedOutOfSight => _finishingOutOfSight?.Task ?? Task.CompletedTask;

    private void LeaveScreenWhileFinishing()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(LeaveScreenWhileFinishing);
            return;
        }

        if (_finishingOutOfSight is not null || _windowClosed || !IsVisible) return;
        _finishingOutOfSight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Hide();
    }

    private void ReturnToScreen()
    {
        if (_finishingOutOfSight is not { } finishing) return;
        _finishingOutOfSight = null;
        if (!_windowClosed)
        {
            Show();
            WindowForeground.BringForward(this);
        }

        finishing.TrySetResult();
    }

    // Only the persistence/export consumer sees timed text; Committed retains the text-free video layer input.
    internal MyCapture.App.Recording.VideoFrameStillImage? FrameStillImage { get; set; }

    internal event EventHandler<AnnotationEditingResult>? Committed;

    /// <summary>Raised once when the window closes without a successful commit.</summary>
    internal event EventHandler? Cancelled;

    internal bool WasCommitted => _committed;

    internal AnnotationEditorControl Editor => _editor;

    /// <summary>
    /// Asks what to do with unsaved edits when the user closes the window. Tests replace it;
    /// the default shows <see cref="EditorCloseDialog"/> over this window.
    /// </summary>
    internal Func<EditorCloseChoice> ConfirmClose { get; set; }

    /// <summary>
    /// Closes the way the title bar's close button does: unsaved edits are confirmed first.
    /// </summary>
    /// <remarks>
    /// A plain <see cref="Window.Close"/> from code (a retake, the owner closing, application
    /// exit) does not ask; WPF ignores a cancelled close during exit, so a question there could
    /// not be honoured.
    /// </remarks>
    internal void RequestClose()
    {
        // While the unsaved-edits question is open the window is already closing: a second Close
        // would throw and leave the request flag set for a later close from code.
        if (IsAskingToClose) return;
        _closeAsked = true;
        Close();
    }

    /// <summary>
    /// Closes the way <see cref="RequestClose"/> does and completes once the outcome is known: true
    /// when the window has closed, false when it stays open (the user kept it, or saving failed).
    /// </summary>
    internal Task<bool> RequestCloseAsync()
    {
        if (IsCloseInProgress || IsAskingToClose || IsSavingToClose) return Task.FromResult(false);
        _discardCloseTask = null;
        _saveCloseTask = null;
        LastCloseChoice = null;
        RequestClose();
        return _discardCloseTask ?? _saveCloseTask ?? CompleteRequestedCloseAsync();
    }

    /// <summary>True while the unsaved-edits question is on screen; the window is closing meanwhile.</summary>
    internal bool IsAskingToClose { get; private set; }

    /// <summary>The answer to the last requested close's unsaved-edits question; null when none was asked.</summary>
    internal EditorCloseChoice? LastCloseChoice { get; private set; }

    /// <summary>
    /// True while a modal dialog keeps this window from taking input: one of its own, a file picker
    /// over it, or another window's (a modal dialog disables every window of the app).
    /// </summary>
    internal bool HasOpenDialog => !WindowForeground.IsEnabled(this);

    /// <summary>
    /// Asks what to do with unsaved edits. A capture shortcut can ask while the editor is minimized
    /// or behind another app, so the editor comes forward first and the question opens in front of it.
    /// </summary>
    private EditorCloseChoice AskBeforeClosing()
    {
        WindowForeground.BringForward(this);
        IsAskingToClose = true;
        try
        {
            EditorCloseChoice choice = ConfirmClose();
            LastCloseChoice = choice;
            return choice;
        }
        finally
        {
            IsAskingToClose = false;
        }
    }

    private bool _closeAsked;
    private bool _cancelRaised;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // The caption close button, Alt+F4, the system menu and the taskbar all arrive as
        // SC_CLOSE before WM_CLOSE; only those user closes are confirmed.
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int SystemCommand = 0x0112, CloseCommand = 0xF060;
        if (message == SystemCommand && ((int)(wParam.ToInt64() & 0xFFF0)) == CloseCommand)
        {
            _closeAsked = true;
        }

        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_editor.IsEditingOperationInProgress || (IsCloseInProgress && !_discardedDraft))
        {
            e.Cancel = true;
            // A picked picture still being read is added first and the close then goes ahead,
            // as it did while the picture was decoded on the UI thread; other work drops it.
            if (_editor.IsOnlyReadingPictures && !IsCloseInProgress) _ = CloseAfterPicturesAsync(_closeAsked);
            _closeAsked = false;
            return;
        }

        bool ask = _closeAsked && !_committed && NeedsUnsavedProtection;
        _closeAsked = false;
        if (ask)
        {
            switch (AskBeforeClosing())
            {
                case EditorCloseChoice.SaveAndClose:
                    // The Done commit closes the window itself once the edit is saved, and keeps
                    // it open (with the failure in the status line) when saving fails.
                    e.Cancel = true;
                    _saveCloseTask = SaveAndCloseAsync();
                    return;
                case EditorCloseChoice.DontSave:
                    if (_draftSession is null)
                    {
                        _discardedDraft = true;
                        break;
                    }
                    e.Cancel = true;
                    _discardCloseTask = DiscardAndCloseAsync();
                    return;
                default:
                    e.Cancel = true;
                    _ = _editor.Focus();
                    return;
            }
        }

        base.OnClosing(e);
    }

    private bool _closeWaitsForPictures;
    private bool _deferredCloseAsked;
    private bool _windowClosed;

    /// <summary>Repeats a close deferred by <see cref="OnClosing"/> once picked pictures are in.</summary>
    private async Task CloseAfterPicturesAsync(bool asked)
    {
        _deferredCloseAsked |= asked;
        if (_closeWaitsForPictures) return;
        _closeWaitsForPictures = true;
        try { await _editor.ImageInsertsSettledAsync(); }
        finally { _closeWaitsForPictures = false; }
        bool ask = _deferredCloseAsked;
        _deferredCloseAsked = false;
        if (_windowClosed || IsAskingToClose) return;
        _closeAsked = ask;
        Close();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_editor.HandleKeyDown(e))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        _ = Activate();
        _ = _editor.Focus();
        _ = Keyboard.Focus(_editor);
    }

    private void OnEditingCompleted(object? sender, AnnotationEditingResult e)
    {
        _committed = true;
        Committed?.Invoke(this, e);
        Close();
    }

    private void OnCloseRequested(object? sender, EventArgs e) => RequestClose();

    protected override void OnClosed(EventArgs e)
    {
        _windowClosed = true;
        FinishDraftRecovery();
        _editor.EditingCompleted -= OnEditingCompleted;
        _editor.CloseRequested -= OnCloseRequested;
        ContentRendered -= OnContentRendered;
        if (!_committed && !_cancelRaised)
        {
            _cancelRaised = true;
            Cancelled?.Invoke(this, EventArgs.Empty);
        }

        base.OnClosed(e);
        // After Closed: whoever waits for a save finishing out of sight sees the window gone.
        if (_finishingOutOfSight is { } finishing)
        {
            _finishingOutOfSight = null;
            finishing.TrySetResult();
        }
    }
}
