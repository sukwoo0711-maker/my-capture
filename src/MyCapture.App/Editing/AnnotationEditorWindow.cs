using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
        _closeAsked = true;
        Close();
    }

    internal Task<bool> RequestCloseAsync()
    {
        if (IsCloseInProgress) return Task.FromResult(false);
        _discardCloseTask = null;
        RequestClose();
        return _discardCloseTask ?? CompleteRequestedCloseAsync();
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
            switch (ConfirmClose())
            {
                case EditorCloseChoice.SaveAndClose:
                    // The Done commit closes the window itself once the edit is saved, and keeps
                    // it open (with the failure in the status line) when saving fails. Started
                    // after this close is cancelled: a commit that finishes synchronously
                    // must not re-enter Close while the window is still closing.
                    e.Cancel = true;
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, _editor.SaveAndClose);
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
