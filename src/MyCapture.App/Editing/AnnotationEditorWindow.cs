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
        string? sourceWindowTitle = null)
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
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        MonitorInfo pointerMonitor = MonitorEnumerator.GetFromCursor();
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
            bool saved = await value(still);
            if (!saved || result.Action == EditorCommitAction.CopyToClipboard) return saved;
            return await RetireDraftBeforeCloseAsync();
        };
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
    }
}
