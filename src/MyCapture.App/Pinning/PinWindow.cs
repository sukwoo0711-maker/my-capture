using System.Windows;
using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.App.Sources;
using MyCapture.Core.Capture;
using MyCapture.Core.Pin;
using MyCapture.Core.Platform;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;

namespace MyCapture.App.Pinning;

/// <summary>
/// A borderless, top-most, independent window that pins one frozen image to the screen,
/// the way Snipaste's pinned screenshots behave.
/// </summary>
/// <remarks>
/// <para>
/// The window is a thin shell over <see cref="PinViewState"/> and <see cref="PinGeometry"/>:
/// pointer and keyboard input are turned into calls on the pure state, and the results
/// (size, position, opacity, click-through) are mirrored onto the live window. All the
/// clampable arithmetic lives in the core layer and is unit-tested there; this class only
/// owns the WPF plumbing that cannot run without a message pump.
/// </para>
/// <para>
/// Local layout and zoom use WPF DIP; dragging uses one physical virtual-desktop plane
/// so a different monitor DPI cannot shorten the available movement range. The image is drawn with
/// <see cref="BitmapScalingMode.HighQuality"/> so scaled pins stay crisp.
/// </para>
/// </remarks>
internal sealed class PinWindow : Window
{
    private const double GrabMarginDip = 24.0;
    private const double OpacityStep = 0.1;
    private const double ArrowNudgeDip = 1.0;
    private const double ArrowNudgeShiftDip = 10.0;

    /// <summary>Outer corner radius matching the host's top-level window frame.</summary>
    internal static readonly double FrameCornerRadiusDip =
        WindowsFeatureSupport.WindowCornerRadiusDip(Environment.OSVersion.Version.Build);

    private const double FrameThicknessDip = 1.0;

    /// <summary>
    /// The image clip edge sits half a DIP inside the outline's outer edge, under the stroke at
    /// every display scale, so neither the desktop nor an anti-aliased image edge shows between
    /// the outline and the image.
    /// </summary>
    private const double FrameClipInsetDip = 0.5;

    private PinContent _content;
    private BitmapSource _image => _content.Image;
    private readonly PinContentKind _contentKind;
    private readonly string? _originalText;
    private readonly PinViewState _state;
    private readonly Func<PinSettings> _settings;
    private readonly IPinDragInput _dragInput;
    private readonly Image _imageElement;
    private readonly RectangleGeometry _imageClip;
    private readonly Grid _chrome;
    private readonly SolidColorBrush _chromeBrush;
    private bool _chromeHover;
    private bool _chromeActive;
    private readonly ScaleTransform _pinScale;
    private readonly Border _feedback;
    private readonly TextBlock _feedbackText;
    private readonly Button _feedbackDismiss;
    private readonly FeedbackCountdown _feedbackCountdown;
    private FeedbackKind _feedbackKind;
    private (string Text, FeedbackKind Kind)? _heldFeedback;
    private int _feedbackGeneration;
    private bool _feedbackHiding;
    private readonly DispatcherTimer _ctrlClickTimer;
    private readonly DispatcherTimer _zoomSettleTimer;
    private readonly DispatcherTimer _hoverHideTimer;
    private PinHoverBar? _hoverBar;
    private double _labeledHoverBarWidth;

    private bool _liveZooming;
    private bool _dragging;
    private Point _dragAnchor;
    private RectD? _dragDesktop;
    private double _dragGrabMarginPx;
    private int _lastDragCursorX = int.MinValue;
    private int _lastDragCursorY = int.MinValue;
    private IntPtr _handle;
    // Closed as far as anyone can tell: set when a close starts, even while its exit still plays.
    private bool _isClosed;
    private bool _closeStarted;
    private bool _windowClosed;
    private bool _saveInProgress;
    private MenuItem? _restoreClosedItem;
    private MenuItem? _clickThroughItem;
    private MenuItem? _selectTextItem;
    private PinTextSelectionView? _textSelection;
    private RectD? _restorePhysicalBounds;
    private Stopwatch? _landingClock;
    private PinLandingDecoration? _landingDecoration;
    private DispatcherTimer? _landingResetTimer;
    private RectD _landingBounds;
    private double _landingAccentAmount;

    internal PinWindow(
        PinContent content,
        PinViewState state,
        double initialLeft,
        double initialTop,
        Func<PinSettings>? settings = null,
        IPinDragInput? dragInput = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
        _contentKind = content.Kind;
        _originalText = content.OriginalText;
        SourcePageUrl = content.SourcePageUrl;
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _settings = settings ?? (static () => new PinSettings());
        _dragInput = dragInput ?? NativePinDragInput.Instance;

        Title = UiText.Get("Text_03151E566E1D");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Focusable = true;
        FluidMotion.SetWindowEntrance(this, false);

        Left = initialLeft;
        Top = initialTop;
        Width = _state.WidthDip;
        Height = _state.HeightDip;
        Opacity = _state.Opacity;

        _imageElement = new Image
        {
            Source = _image,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(_imageElement, BitmapScalingMode.HighQuality);
        UpdateImageRasterLayout();

        // The bottom message wraps inside narrow pins. It takes the pointer only to pause its
        // countdown and to offer a close button for a failure; dragging through it still moves the pin.
        _feedbackText = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _feedbackText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        var dismissGlyph = new SymbolIcon
        {
            Size = 16,
            Data = Application.Current?.TryFindResource("Icon.Close") as Geometry ?? Geometry.Parse("M5,5 L15,15 M15,5 L5,15"),
        };
        dismissGlyph.SetResourceReference(SymbolIcon.StrokeProperty, "Text.Primary");
        _feedbackDismiss = new Button
        {
            Content = dismissGlyph,
            Width = 22,
            Height = 22,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(8, -2, -4, -2),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            ToolTip = UiText.Get("Feedback.Dismiss"),
        };
        _feedbackDismiss.SetResourceReference(StyleProperty, "Button.Ghost");
        AutomationProperties.SetName(_feedbackDismiss, UiText.Get("Feedback.Dismiss"));
        _feedbackDismiss.Click += (_, _) => DismissFeedback();
        DockPanel.SetDock(_feedbackDismiss, Dock.Right);
        _feedback = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 8, 8),
            Padding = new Thickness(10, 5, 10, 5),
            CornerRadius = new CornerRadius(6),
            Background = ThemeBrushes.Follow("Surface.Floating", Color.FromArgb(0xE8, 0x15, 0x1E, 0x2B)),
            Child = new DockPanel { Children = { _feedbackDismiss, _feedbackText } },
            Visibility = Visibility.Collapsed,
        };

        // Image-first chrome: one continuous one-DIP outline matching the host's window corners
        // rests in a quiet accent tone and turns opaque on hover or while the pin is active
        // (PinFrameColor). The image fills the whole pin and is clipped to the rounded
        // rectangle the outline follows, and the outline is drawn
        // over the image's outermost edge. A Border's CornerRadius does not clip its child and
        // a child draws over its parent's stroke, so an image inset inside a Border covered the
        // corner arcs and left resampled edge rows beside the straight outline segments.
        _chromeBrush = new SolidColorBrush(PinFrameColor.Resolve(null, null, contrast: false, emphasized: false));
        // The token references follow every theme, accent and contrast change while the pin is open.
        SetResourceReference(FrameAccentProperty, PinFrameColor.AccentKey);
        SetResourceReference(FrameNeutralProperty, PinFrameColor.NeutralKey);
        _pinScale = new ScaleTransform(1, 1);
        _imageClip = new RectangleGeometry(
            ImageClipBounds(new Size(_state.WidthDip, _state.HeightDip)),
            Math.Max(0, FrameCornerRadiusDip - FrameClipInsetDip),
            Math.Max(0, FrameCornerRadiusDip - FrameClipInsetDip));
        _imageElement.Clip = _imageClip;
        _imageElement.SizeChanged += (_, args) => UpdateImageClip(args.NewSize);
        var outline = new Border
        {
            BorderThickness = new Thickness(FrameThicknessDip),
            BorderBrush = _chromeBrush,
            CornerRadius = new CornerRadius(FrameCornerRadiusDip),
            IsHitTestVisible = false,
        };
        _chrome = new Grid
        {
            ClipToBounds = true,
            Children = { _imageElement, outline, _feedback },
            RenderTransform = _pinScale,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        Content = _chrome;

        // The hover bar shows copy, save, opacity and close; the tooltip only names the gestures.
        ToolTip = UiText.Get("Onboard_PinTip");
        AutomationProperties.SetName(this, UiText.Get("Text_85E926C7F694"));
        AutomationProperties.SetHelpText(
            this,
            _originalText is null
                ? UiText.Format("Text_E22E2C953A8B", ClickThroughRecoveryHint())
                : UiText.Get("Text_55694B0252DB"));

        BuildContextMenu();

        // Hides the bottom message after its kind's display time (FeedbackTiming); pointing at
        // it or focusing its close button pauses the countdown.
        _feedbackCountdown = new FeedbackCountdown(
            Dispatcher,
            () => _feedback.IsMouseOver || _feedback.IsKeyboardFocusWithin,
            OnFeedbackExpired);

        // Tracks the double-click interval without modifying the clipboard on a single click.
        // Ctrl+C copies the image and retained source; Ctrl+double-click copies only text.
        _ctrlClickTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _ctrlClickTimer.Tick += OnCtrlClickTimerTick;
        _zoomSettleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(160),
        };
        _zoomSettleTimer.Tick += OnZoomSettleTick;

        // The hover bar leaves shortly after the pointer leaves both the pin and the bar, so the
        // pointer can travel from the image onto the bar.
        _hoverHideTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = HoverBarHideDelay,
        };
        _hoverHideTimer.Tick += OnHoverHideTick;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnPinLoaded;
        MouseEnter += (_, _) =>
        {
            SetChromeHover(true);
            ShowHoverBar();
        };
        MouseLeave += (_, _) =>
        {
            SetChromeHover(false);
            ScheduleHoverBarHide();
        };
        // The active pin takes the keys (Esc closes it), so its outline stays emphasized.
        Activated += (_, _) =>
        {
            SetChromeActive(true);
            if (!_isClosed) ViewStateChanged?.Invoke();
        };
        Deactivated += (_, _) => SetChromeActive(false);
        LocationChanged += (_, _) => PlaceHoverBar();
        SizeChanged += (_, _) =>
        {
            UpdateImageRasterLayout();
            PlaceHoverBar();
        };
        PendingProvenanceUpdateForTest = content.PendingProvenance is { } pending
            ? ApplyPendingProvenanceAsync(new WeakReference<PinWindow>(this), pending)
            : Task.CompletedTask;
    }

    internal Task PendingProvenanceUpdateForTest { get; }

    private static async Task ApplyPendingProvenanceAsync(WeakReference<PinWindow> target, Task<CaptureProvenance> pending)
    {
        CaptureProvenance source;
        try
        {
            source = await pending.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            return;
        }
        if (!source.HasSource || !target.TryGetTarget(out PinWindow? pin)
            || pin.Dispatcher.HasShutdownStarted || pin.Dispatcher.HasShutdownFinished) return;

        try
        {
            await pin.Dispatcher.InvokeAsync(() =>
            {
                if (pin._isClosed) return;
                bool changed = pin.Provenance != source;
                pin._content = pin._content.WithProvenance(source);
                if (!changed) return;
                if (pin._hoverBar is { } bar)
                {
                    bar.SetProvenance(source);
                    pin.RefreshSourceMenu(bar);
                    pin._labeledHoverBarWidth = bar.MeasureLabeledWidth();
                    pin.PlaceHoverBar();
                }
                pin.ContentChanged?.Invoke();
            });
        }
        catch (OperationCanceledException)
        {
            // The pin's dispatcher shut down before the source lookup could be applied.
        }
    }

    /// <summary>How long the hover bar stays after the pointer leaves the pin and the bar.</summary>
    internal static readonly TimeSpan HoverBarHideDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>The work area the hover bar is kept on; tests keep it off the desktop.</summary>
    internal Func<IntPtr, RectD> ReadWorkArea { get; set; } = handle => MonitorEnumerator.GetFromWindow(handle).WorkArea;

    /// <summary>The hover bar, once the pointer has been over the pin.</summary>
    internal PinHoverBar? HoverBarForTest => _hoverBar;

    internal PinHoverBar CreateHoverBarForTest() => _hoverBar ??= CreateHoverBar(attachOwner: false);

    internal bool IsHoverBarShown => _hoverBar?.IsVisible == true;

    /// <summary>Test hook: the hide timer's tick, without waiting for it.</summary>
    internal void ForceHoverBarHideForTest() => OnHoverHideTick(this, EventArgs.Empty);

    /// <summary>
    /// Shows copy, save, opacity and close in the pin's top-right corner (or just above a small pin).
    /// Not while the pin is hidden, passes clicks through, is being dragged or is closing.
    /// </summary>
    internal void ShowHoverBar()
    {
        if (_isClosed || _state.IsHidden || _state.IsClickThrough || _dragging || IsTextSelectionMode || !IsVisible) return;
        _hoverHideTimer.Stop();
        PinHoverBar bar = _hoverBar ??= CreateHoverBar();
        if (!bar.IsVisible)
        {
            // Start next to the pin so the first frame never lands on another monitor.
            bar.Left = Left + Math.Max(0, Width - 180);
            bar.Top = Top + PinHoverBar.EdgeGap;
            bar.Show();
            bar.Reveal();
        }

        PlaceHoverBar();
    }

    internal void HideHoverBar()
    {
        _hoverHideTimer.Stop();
        if (_hoverBar is { IsVisible: true } bar) bar.Hide();
    }

    private PinHoverBar CreateHoverBar(bool attachOwner = true)
    {
        var bar = new PinHoverBar(Provenance) { ReadOpacity = () => _state.Opacity };
        if (attachOwner) bar.Owner = this;
        bar.CopyRequested += CopyImageToClipboard;
        bar.SaveRequested += () => RequestSave(PinSaveMode.QuickSave);
        bar.SourceRequested += () => LastSourceOperationForTest = OpenSourceAsync();
        RefreshSourceMenu(bar);
        bar.OpacityRequested += opacity =>
        {
            double target = AdjustOpacity(opacity - _state.Opacity);
            ShowFeedback(UiText.Format("Text_C740003A2150", target * 100));
        };
        bar.CloseRequested += CloseWithExit;
        bar.MouseEnter += (_, _) => _hoverHideTimer.Stop();
        bar.MouseLeave += (_, _) => ScheduleHoverBarHide();
        bar.SizeChanged += (_, _) => PlaceHoverBar();
        // Measured before the bar is ever shown; measuring the hidden window itself gave zero,
        // so even the narrowest pin kept the names.
        _labeledHoverBarWidth = bar.MeasureLabeledWidth();
        return bar;
    }

    private void RefreshSourceMenu(PinHoverBar bar)
    {
        if (bar.SourceButton.ContextMenu is { } previous) previous.IsOpen = false;
        bar.SourceButton.ContextMenu = null;
        if (!Provenance.HasSource) return;
        ContextMenu menu = SourceChip.CreateMenu(Provenance,
            status => ShowFeedback(status, FeedbackKind.Result),
            menu: MyCapture.App.Capture.CaptureWindowExclusion.CreateContextMenu());
        menu.Opened += (_, _) => MyCapture.App.Capture.CaptureWindowExclusion.ExcludeMenuPopup(menu);
        bar.SourceButton.ContextMenu = menu;
    }

    private void ScheduleHoverBarHide()
    {
        if (_hoverBar is not { IsVisible: true }) return;
        _hoverHideTimer.Stop();
        _hoverHideTimer.Start();
    }

    private void OnHoverHideTick(object? sender, EventArgs e)
    {
        _hoverHideTimer.Stop();
        if (IsMouseOver || _hoverBar?.IsMouseOver == true || _hoverBar?.IsMenuOpen == true) return;
        HideHoverBar();
    }

    private void PlaceHoverBar()
    {
        if (_hoverBar is not { IsVisible: true } bar || _handle == IntPtr.Zero || _isClosed) return;
        IntPtr barHandle = new WindowInteropHelper(bar).Handle;
        if (barHandle == IntPtr.Zero) return;
        // A narrow pin keeps the icons and drops the names, which stay in the tooltips.
        if (_labeledHoverBarWidth <= 0 && !bar.Compact)
        {
            // Resources were not there before the first show; the shown, labelled bar is the measure.
            bar.UpdateLayout();
            _labeledHoverBarWidth = bar.ActualWidth;
        }
        bar.Compact = Width < _labeledHoverBarWidth + (PinHoverBar.EdgeGap * 4);
        bar.UpdateLayout();
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        (int left, int top, int right, int bottom) = WindowStyleFacade.GetWindowBounds(_handle);
        RectD target = PinHoverBar.Place(new RectD(left, top, right - left, bottom - top),
            bar.ActualWidth * dpi.DpiScaleX, bar.ActualHeight * dpi.DpiScaleY, ReadWorkArea(_handle), dpi.DpiScaleX);
        try
        {
            PhysicalWindowPositioner.Move(barHandle, target);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The bar is a convenience; the pin keeps working where Windows leaves it.
        }
    }

    /// <summary>Raised when the user asks (via this pin) to close every pin.</summary>
    internal event EventHandler? CloseAllRequested;

    /// <summary>Raised when the user copies this pin's image, with the page it came from.</summary>
    internal event EventHandler<PinImageCopyRequest>? CopyRequested;

    /// <summary>Raised when a rendered text/table pin requests its retained source text.</summary>
    internal event EventHandler<string>? OriginalTextCopyRequested;

    /// <summary>Raised when the user asks to run OCR on this pin's image.</summary>
    internal event EventHandler<BitmapSource>? OcrRequested;

    /// <summary>Raised when the user asks to save the frozen source image.</summary>
    internal event EventHandler<PinSaveRequestedEventArgs>? SaveRequested;

    /// <summary>Raised when the user asks (via this pin) to reopen the last closed pin.</summary>
    internal event EventHandler? RestoreClosedRequested;

    /// <summary>Raised after movement or zoom settles, or opacity or stacking order changes.</summary>
    internal event Action? ViewStateChanged;

    /// <summary>Raised when a source lookup enriches the retained content and its saved session.</summary>
    internal event Action? ContentChanged;

    /// <summary>
    /// Reports whether a closed pin can be reopened; drives the context-menu item state.
    /// Set by the manager that owns the closed-pin history.
    /// </summary>
    internal Func<bool>? CanRestoreClosedPin { get; set; }

    /// <summary>
    /// The immutable content this pin shows. A restored pin reuses the same instance, so its
    /// frozen source pixels and retained text are exactly what the closed pin had.
    /// </summary>
    internal PinContent PinnedContent => _content;

    /// <summary>
    /// Where this pin was on the virtual desktop when it began closing, or
    /// <see langword="null"/> when it closed before it had a native window.
    /// </summary>
    internal PinClosedPlacement? ClosedPlacement { get; private set; }

    /// <summary>Reads the live placement without closing the window or recording history.</summary>
    internal PinClosedPlacement? GetCurrentPlacement() => CaptureClosedPlacement();

    /// <summary>The pin's live presentation state.</summary>
    internal PinViewState State => _state;

    /// <summary>Whether this pin represents an image, plain text, or a table.</summary>
    internal PinContentKind ContentKind => _contentKind;

    /// <summary>The exact retained source string, exposed internally for verification.</summary>
    internal string? OriginalText => _originalText;

    internal bool IsTextSelectionMode => _textSelection is not null;

    internal PinTextSelectionView? TextSelectionForTest => _textSelection;

    internal IPinSelectionFocus SelectionFocus { get; set; } = NativePinSelectionFocus.Instance;

    /// <summary>The source page this pin came from, if the clipboard carried one.</summary>
    internal string SourcePageUrl { get; } = string.Empty;

    internal CaptureProvenance Provenance => _content.Provenance;

    internal Func<CaptureProvenance, Task<string>> OpenSource { get; set; } =
        CaptureSourceActions.Default.OpenAsync;

    internal Task? LastSourceOperationForTest { get; private set; }

    internal async Task OpenSourceAsync()
    {
        if (!Provenance.HasSource || _isClosed) return;
        string status = await OpenSource(Provenance);
        if (!string.IsNullOrEmpty(status)) ShowFeedback(status, FeedbackKind.Result);
    }

    /// <summary>The native handle, valid after the window is shown.</summary>
    internal IntPtr Handle => _handle;

    /// <summary>Whether this pin has been closed and should be pruned by the manager.</summary>
    internal bool IsClosed => _isClosed;

    /// <summary>Test hook: whether the bottom feedback overlay is currently visible.</summary>
    internal bool IsFeedbackVisible => _feedback.Visibility == Visibility.Visible;

    /// <summary>Test hook: whether the auto-hide feedback timer is currently running.</summary>
    internal bool IsFeedbackTimerRunning => _feedbackCountdown.IsRunning;

    /// <summary>Test hook: the bottom message, its kind and whether it offers a close button.</summary>
    internal (string Text, FeedbackKind Kind, bool Dismissible) FeedbackForTest =>
        (_feedbackText.Text, _feedbackKind, _feedbackDismiss.Visibility == Visibility.Visible);

    /// <summary>Test hook: spends display time as the countdown's clock would.</summary>
    internal void AdvanceFeedbackForTest(TimeSpan elapsed) => _feedbackCountdown.Advance(elapsed);

    /// <summary>
    /// Test hook: fires the feedback auto-hide logic immediately, without waiting for the
    /// real timer interval, so tests can prove the overlay hides without brittle sleeps.
    /// </summary>
    internal void ForceFeedbackTimeoutForTest()
    {
        _heldFeedback = null;
        _feedbackCountdown.Stop();
        HideFeedback(immediate: true);
    }

    /// <summary>Test hook: requests either quick save or Save As without keyboard input.</summary>
    internal void SimulateSaveRequestForTest(PinSaveMode mode) => RequestSave(mode);

    /// <summary>Completes the pin-local save progress state after the manager finishes I/O.</summary>
    internal void ReportSaveResult(PinSaveResult result)
    {
        _saveInProgress = false;
        switch (result.Status)
        {
            case PinSaveStatus.Saved:
                string fileName = string.IsNullOrWhiteSpace(result.Path)
                    ? "PNG"
                    : System.IO.Path.GetFileName(result.Path);
                // The file name is worth reading, so this is a result rather than a quick confirmation.
                ShowFeedback(UiText.Format("Text_E3F51EDC0F16", fileName), FeedbackKind.Result);
                break;
            case PinSaveStatus.Cancelled:
                ShowFeedback(UiText.Get("Text_F122632462DF"), FeedbackKind.Confirmation);
                break;
            default:
                ShowFeedback(UiText.Get("Text_AE94EC51B2C2"), FeedbackKind.ActionRequired);
                break;
        }
    }

    /// <summary>Completes pin-local clipboard feedback after the asynchronous copy.</summary>
    internal void ReportCopyResult(bool copied) => ShowFeedback(
        copied ? UiText.Get("Text_9693E1EB3EDB") : UiText.Get("Text_0642E2D15469"),
        copied ? FeedbackKind.Confirmation : FeedbackKind.ActionRequired);

    /// <summary>Completes feedback for copying a rendered pin's retained source text.</summary>
    /// <remarks>
    /// For an image pin the text comes from OCR, and a failure there usually means no text was
    /// found, which retrying cannot fix; it is a warning rather than a message that stays.
    /// </remarks>
    internal void ReportOriginalTextCopyResult(bool copied) => ShowFeedback(
        copied ? UiText.Get("Text_5C9B3C62EC41") : UiText.Get("Text_BCC2F939B733"),
        copied ? FeedbackKind.Confirmation : _originalText is null ? FeedbackKind.Warning : FeedbackKind.ActionRequired);

    /// <summary>Test hook: whether the Ctrl+click copy debounce timer is currently armed.</summary>
    internal bool IsCtrlClickTimerRunning => _ctrlClickTimer.IsEnabled;

    /// <summary>
    /// Test hook: simulates a Ctrl+single-click, arming the copy debounce exactly as the mouse
    /// handler does, so the single-vs-double-click race is testable without a live pointer.
    /// </summary>
    internal void SimulateCtrlSingleClickForTest() => StartCtrlClickCopyDebounce();

    /// <summary>
    /// Test hook: simulates a Ctrl+double-click. Image pins request OCR; rendered text/table
    /// pins copy their retained source string. Both cancel the pending single-click copy.
    /// </summary>
    internal void SimulateCtrlDoubleClickForTest() => HandleCtrlDoubleClick();

    /// <summary>Test hook: fires the copy debounce immediately as the real timer tick would.</summary>
    internal void ForceCtrlClickTimeoutForTest() => OnCtrlClickTimerTick(this, EventArgs.Empty);

    /// <summary>Applies click-through by toggling extended window styles via the facade.</summary>
    internal void ApplyClickThrough(bool enabled)
    {
        _state.SetClickThrough(enabled);
        if (_handle != IntPtr.Zero)
        {
            WindowStyleFacade.SetClickThrough(_handle, enabled);
        }

        if (enabled) HideHoverBar();
        // Turning it on carries the key that turns it off again, which needs time to read.
        ShowFeedback(
            enabled ? UiText.Format("Onboard_PinClickThroughOn", ClickThroughRecoveryHint()) : UiText.Get("Onboard_PinClickThroughOff"),
            enabled ? FeedbackKind.Result : FeedbackKind.Confirmation);
    }

    /// <summary>Toggles click-through and returns the new state.</summary>
    internal bool ToggleClickThrough()
    {
        bool next = !_state.IsClickThrough;
        ApplyClickThrough(next);
        return next;
    }

    /// <summary>The pin's window bounds in physical pixels; empty if not yet shown.</summary>
    internal (int Left, int Top, int Right, int Bottom) PhysicalBounds =>
        _handle != IntPtr.Zero ? WindowStyleFacade.GetWindowBounds(_handle) : (0, 0, 0, 0);

    /// <summary>Hides the pin while retaining its position and zoom.</summary>
    internal void HidePin()
    {
        _state.IsHidden = true;
        StopLandingEffect();
        LeaveTextSelection(restoreFocus: false);
        HideHoverBar();
        if (!FluidMotion.AnimationsEnabled || !IsVisible)
        {
            Hide();
            return;
        }

        var fade = new DoubleAnimation(_chrome.Opacity, 0, FluidMotion.FastDuration)
        {
            EasingFunction = FluidMotion.StandardEasing,
            FillBehavior = FillBehavior.Stop,
        };
        fade.Completed += (_, _) =>
        {
            if (_state.IsHidden && !_isClosed)
            {
                Hide();
            }
        };
        _chrome.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Reveals a hidden pin at its retained position and zoom.</summary>
    internal void ShowPin()
    {
        _state.IsHidden = false;
        _chrome.BeginAnimation(UIElement.OpacityProperty, null);
        Show();
        Topmost = true;
        AnimatePinReveal();
    }

    /// <summary>
    /// Requests that the native window land on <paramref name="physicalBounds"/>' top-left
    /// once it exists. Used by restore, whose position is resolved in physical pixels so a
    /// pin returns to the exact spot on a mixed-DPI desktop.
    /// </summary>
    internal void PlaceAtPhysicalOnShow(RectD physicalBounds) => _restorePhysicalBounds = physicalBounds;

    /// <summary>
    /// Moves the shown window onto the requested restore position. The first attempt runs
    /// from <see cref="OnSourceInitialized"/> before the window is visible; calling it again
    /// after <c>Show</c> only verifies (the positioner skips an unchanged position) and
    /// corrects a shift caused by a DPI change on the way. The pin stays usable where WPF put
    /// it if Windows refuses the move.
    /// </summary>
    /// <returns><see langword="false"/> only when a requested move failed.</returns>
    internal bool ApplyRestorePlacement()
    {
        if (_restorePhysicalBounds is not { } bounds || _handle == IntPtr.Zero || _isClosed)
        {
            return true;
        }

        try
        {
            PhysicalWindowPositioner.Move(_handle, bounds);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Clears a completed restore placement request.</summary>
    internal void CompleteRestorePlacement() => _restorePhysicalBounds = null;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        RerenderForDisplayScale(newDpi.DpiScaleX);
        base.OnDpiChanged(oldDpi, newDpi);
    }

    /// <summary>Changes only the raster; the pin's DIP geometry and interaction state stay intact.</summary>
    internal void RerenderForDisplayScale(double scale)
    {
        if (_isClosed || !_content.CanRerender) return;
        PinContent rendered = _content.RenderedAt(scale);
        if (ReferenceEquals(rendered, _content)) return;
        _content = rendered;
        _imageElement.Source = rendered.Image;
        UpdateImageRasterLayout();
        ViewStateChanged?.Invoke();
    }

    internal void SimulateDpiChangeForTest(double scale) => RerenderForDisplayScale(scale);

    private void UpdateImageRasterLayout()
    {
        if (!_content.CanRerender) return;
        // The outward-rounded raster may contain a partial DIP of padding. Let the
        // viewport clip it instead of stretching every glyph to fit that rounding.
        _imageElement.Stretch = Stretch.None;
        _imageElement.HorizontalAlignment = HorizontalAlignment.Left;
        _imageElement.VerticalAlignment = VerticalAlignment.Top;
        _imageElement.LayoutTransform = new ScaleTransform(_state.Zoom, _state.Zoom);
        if (_imageClip is not null) UpdateImageClip(_imageElement.RenderSize);
    }

    private void UpdateImageClip(Size imageSize)
    {
        double zoom = _content.CanRerender ? _state.Zoom : 1;
        Rect bounds = ImageClipBounds(_content.CanRerender ? new Size(_state.WidthDip, _state.HeightDip) : imageSize);
        _imageClip.Rect = new Rect(bounds.X / zoom, bounds.Y / zoom, bounds.Width / zoom, bounds.Height / zoom);
        _imageClip.RadiusX = _imageClip.RadiusY = Math.Max(0, FrameCornerRadiusDip - FrameClipInsetDip) / zoom;
    }

    /// <summary>How long a pin takes to leave after the user closes it: its landing played backwards.</summary>
    internal static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Keeps a leaving pin out of screen captures, so a capture that starts during its exit never
    /// shows a pin the user already closed. Where Windows refuses, the pin closes at once instead.
    /// Tests substitute a recorder.
    /// </summary>
    internal static Func<IntPtr, bool> ExcludeLeavingPinFromCapture { get; set; } =
        MyCapture.App.Capture.CaptureWindowExclusion.TryApply;

    /// <summary>
    /// Raised once when the pin begins to close, before any exit plays and while its placement is
    /// known; <see cref="Window.Closed"/> follows when the window itself is gone.
    /// </summary>
    internal event EventHandler? CloseStarted;

    /// <summary>
    /// Closes the pin the way a user close does (its close button, Esc or Delete, a double-click
    /// when that setting is on, or its menu): the close happens now, so the placement, the
    /// "reopen closed pin" history, the open-pin session and every list are already right, and
    /// only the pixels linger. The pin shrinks to 0.97 and fades over <see cref="ExitDuration"/>
    /// without taking input, and its hover bar fades with it over the fast token. Without
    /// Windows animations, or when the leaving pin cannot be kept out of a capture, it closes at once.
    /// </summary>
    internal void CloseWithExit()
    {
        if (_isClosed) return;
        StartClosing();
        if (!FluidMotion.AnimationsEnabled || !IsVisible || _handle == IntPtr.Zero
            || !ExcludeLeavingPinFromCapture(_handle))
        {
            Close();
            return;
        }

        FluidMotion.BeginExit(this, ExitMotion.Shrink, CloseLeftWindow, ExitDuration);
        if (!FluidMotion.IsExiting(this)) return;

        // The frame owns its scale transform (the exit keeps a transform a screen owns), so the
        // pin shrinks itself alongside the shared fade.
        var shrink = new DoubleAnimation(1, 0.97, ExitDuration) { EasingFunction = FluidMotion.StandardEasing };
        _pinScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        _pinScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        _hoverBar?.Leave();
    }

    /// <summary>Ends a playing exit now (close all, app exit): the window closes at once.</summary>
    internal void CompleteExit() => FluidMotion.CompleteExit(this);

    /// <summary>Whether the pin has closed but its exit is still on the screen.</summary>
    internal bool IsLeaving => FluidMotion.IsExiting(this);

    // The exit's deferred close; the window may already have been closed directly meanwhile.
    private void CloseLeftWindow()
    {
        if (!_windowClosed) Close();
    }

    // Everything a close changes happens here, once, at its start: an exit only delays pixels.
    private void StartClosing()
    {
        if (_closeStarted) return;
        _closeStarted = true;
        // Snapshot while the native window still exists and nothing has moved it.
        ClosedPlacement = CaptureClosedPlacement();
        _isClosed = true;
        StopLandingEffect();
        EndDrag();
        _feedbackCountdown.Stop();
        _ctrlClickTimer.Stop();
        _zoomSettleTimer.Stop();
        _hoverHideTimer.Stop();
        if (ContextMenu is { IsOpen: true } menu) menu.IsOpen = false;
        CloseStarted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
        {
            // A direct close (close all, app exit) starts and ends in this one call.
            StartClosing();
        }
    }

    private PinClosedPlacement? CaptureClosedPlacement()
    {
        if (_handle == IntPtr.Zero)
        {
            return null;
        }

        (int left, int top, int right, int bottom) = WindowStyleFacade.GetWindowBounds(_handle);
        if (right <= left || bottom <= top)
        {
            return null;
        }

        MonitorInfo monitor = MonitorEnumerator.GetFromWindow(_handle);
        return new PinClosedPlacement(
            new RectD(left, top, right - left, bottom - top),
            monitor.DeviceName,
            monitor.Bounds);
    }

    private void OnPinLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        AnimatePinReveal();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;

        // WPF's ShowInTaskbar=false does not remove the window from Alt+Tab; without the
        // tool-window bit every pin on the desk shows up as an identical switcher entry.
        WindowStyleFacade.ExcludeFromAltTab(_handle);

        // Re-apply any click-through state that was set before the handle existed.
        if (_state.IsClickThrough)
        {
            WindowStyleFacade.SetClickThrough(_handle, enabled: true);
        }

        // A restored pin moves to its physical position before it first becomes visible. A
        // failure is retried and reported by the manager once the window is shown.
        _ = ApplyRestorePlacement();
    }

    /// <summary>The accent token brush the outline follows (<see cref="PinFrameColor.AccentKey"/>).</summary>
    private static readonly DependencyProperty FrameAccentProperty = DependencyProperty.Register(
        "FrameAccent", typeof(Brush), typeof(PinWindow), new PropertyMetadata(null, OnFrameTokenChanged));

    /// <summary>The neutral token brush a contrast theme rests on (<see cref="PinFrameColor.NeutralKey"/>).</summary>
    private static readonly DependencyProperty FrameNeutralProperty = DependencyProperty.Register(
        "FrameNeutral", typeof(Brush), typeof(PinWindow), new PropertyMetadata(null, OnFrameTokenChanged));

    // A theme, accent or contrast change swaps the token brushes; the outline changes at once.
    private static void OnFrameTokenChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((PinWindow)sender).UpdateChromeColor(animate: false);

    /// <summary>Test hook: the outline colour the pin shows once any transition ends.</summary>
    internal Color FrameColorForTest => _chromeBrush.Color;

    /// <summary>Test hook: what the Activated and Deactivated events do, without taking the foreground.</summary>
    internal void SetChromeActiveForTest(bool active) => SetChromeActive(active);

    private void SetChromeHover(bool hovering)
    {
        _chromeHover = hovering;
        UpdateChromeColor(animate: true);
    }

    private void SetChromeActive(bool active)
    {
        _chromeActive = active;
        UpdateChromeColor(animate: true);
    }

    /// <summary>
    /// The emphasized outline colour of the current theme. The landing flash on the frame and
    /// the landing ornament's ring and glow share it, so they follow the chosen accent.
    /// </summary>
    internal Color LandingAccentColor => PinFrameColor.Resolve(
        (Brush?)GetValue(FrameAccentProperty), (Brush?)GetValue(FrameNeutralProperty),
        ThemeService.IsSystemHighContrast || ThemeService.Current == Core.Themes.AppTheme.HighContrast,
        emphasized: true);

    private void UpdateChromeColor(bool animate)
    {
        // The dedicated brand outline is quiet at rest and stronger on hover or activation.
        bool contrast = ThemeService.IsSystemHighContrast || ThemeService.Current == Core.Themes.AppTheme.HighContrast;
        Color target = PinFrameColor.Resolve(
            (Brush?)GetValue(FrameAccentProperty),
            (Brush?)GetValue(FrameNeutralProperty),
            contrast,
            emphasized: _chromeHover || _chromeActive);
        if (_landingAccentAmount > 0)
        {
            target = BlendFrameColor(target, LandingAccentColor, _landingAccentAmount);
            animate = false;
        }
        // Read the presentation value before replacing an in-flight animation; otherwise a
        // fast enter/leave reversal visibly jumps back to the previous base colour.
        Color current = _chromeBrush.Color;
        _chromeBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _chromeBrush.Color = target;
        if (!animate || !FluidMotion.AnimationsEnabled || current == target)
        {
            return;
        }

        var animation = new ColorAnimation(current, target, FluidMotion.FastDuration)
        {
            EasingFunction = FluidMotion.StandardEasing,
            FillBehavior = FillBehavior.Stop,
        };
        _chromeBrush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>The rounded image clip for an image laid out at <paramref name="size"/>.</summary>
    internal static Rect ImageClipBounds(Size size) => new(
        FrameClipInsetDip,
        FrameClipInsetDip,
        Math.Max(0, size.Width - (2 * FrameClipInsetDip)),
        Math.Max(0, size.Height - (2 * FrameClipInsetDip)));

    // ----- Mouse: drag to move, wheel to zoom, Ctrl+wheel opacity -----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (IsTextSelectionMode) return;

        bool ctrl = _dragInput.Modifiers.HasFlag(ModifierKeys.Control);

        if (ctrl)
        {
            // Ctrl gestures never drag. A single click only focuses; double-click copies
            // recognized text for an image or exact source text for a rendered text/table pin.
            if (e.ClickCount >= 2)
            {
                Focus();
                HandleCtrlDoubleClick();
            }
            else
            {
                StartCtrlClickCopyDebounce();
                Focus();
            }

            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2)
        {
            // Plain double-click closes the pin only when the setting says so, matching Snipaste.
            if (_settings().CloseOnDoubleClick)
            {
                CloseWithExit();
                e.Handled = true;
                return;
            }
        }

        EndDrag();
        HideHoverBar();
        try
        {
            // Capture can fail (for example while another native control owns input).
            // Never leave a gesture active without ownership of subsequent input.
            if (_dragInput.TryCapture(this))
            {
                Point anchor = _dragInput.LocalAnchor(e, this);
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                _dragAnchor = new Point(anchor.X * dpi.DpiScaleX, anchor.Y * dpi.DpiScaleY);
                _dragDesktop = MonitorEnumerator.GetVirtualDesktopBounds();
                _dragGrabMarginPx = GrabMarginDip * dpi.DpiScaleX;
                _dragging = true;
                Focus();
            }
        }
        catch
        {
            EndDrag();
            throw;
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            return;
        }

        if (!_dragInput.HasCapture(this) || !_dragInput.LeftButtonPressed(e))
        {
            EndDrag();
            return;
        }

        (int cursorX, int cursorY) = _dragInput.CursorPosition;
        if (cursorX == _lastDragCursorX && cursorY == _lastDragCursorY)
        {
            return;
        }

        _lastDragCursorX = cursorX;
        _lastDragCursorY = cursorY;
        MovePhysical(cursorX - _dragAnchor.X, cursorY - _dragAnchor.Y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging)
        {
            EndDrag();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        EndDrag();
        base.OnLostMouseCapture(e);
    }

    private void EndDrag()
    {
        bool wasDragging = _dragging;
        _dragging = false;
        _dragDesktop = null;
        _lastDragCursorX = int.MinValue;
        _lastDragCursorY = int.MinValue;
        if (_dragInput.HasCapture(this)) _dragInput.Release(this);
        if (wasDragging && !_isClosed && IsMouseOver) ShowHoverBar();
        if (wasDragging && !_isClosed) ViewStateChanged?.Invoke();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled) return;
        HandleMouseWheel(e, Keyboard.Modifiers);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (IsTextSelectionMode) HandleMouseWheel(e, Keyboard.Modifiers);
    }

    internal void HandleMouseWheel(MouseWheelEventArgs e, ModifierKeys modifiers)
    {
        if (!modifiers.HasFlag(ModifierKeys.Control) && _textSelection?.TryScroll(e.Delta) == true)
        {
            e.Handled = true;
            return;
        }

        int notches = e.Delta / 120;
        if (notches == 0)
        {
            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            double opacity = AdjustOpacity(OpacityStep * notches);
            ShowFeedback(UiText.Format("Text_C740003A2150", opacity * 100));
        }
        else
        {
            Point pointer = e.GetPosition(this);
            ZoomAt(notches, pointer.X + Left, pointer.Y + Top);
        }

        e.Handled = true;
    }

    // ----- Keyboard -----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (!e.Handled) HandleKeyDown(e, Keyboard.Modifiers);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // A closed pin that is still leaving takes no keys: its close has already happened.
        if (_isClosed)
        {
            e.Handled = true;
            return;
        }

        // Text controls consume Copy and navigation before the bubbling window handler.
        if (IsTextSelectionMode) HandleKeyDown(e, Keyboard.Modifiers);
    }

    internal void HandleKeyDown(KeyEventArgs e, ModifierKeys modifiers)
    {
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        double nudge = shift ? ArrowNudgeShiftDip : ArrowNudgeDip;

        switch (e.Key)
        {
            case Key.F2:
                ToggleTextSelection();
                e.Handled = true;
                break;

            case Key.Escape when IsTextSelectionMode:
                LeaveTextSelection();
                e.Handled = true;
                break;

            case Key.Escape:
            case Key.Delete:
                // Esc first dismisses a failure message that stays; the next Esc closes the pin.
                if (e.Key == Key.Escape && DismissFeedback())
                {
                    e.Handled = true;
                    break;
                }

                CloseWithExit();
                e.Handled = true;
                break;

            case Key.Add:
            case Key.OemPlus:
                ZoomCentered(1);
                e.Handled = true;
                break;

            case Key.Subtract:
            case Key.OemMinus:
                ZoomCentered(-1);
                e.Handled = true;
                break;

            case Key.D0:
            case Key.NumPad0:
                ResetZoomCentered();
                e.Handled = true;
                break;

            case Key.Left or Key.Right or Key.Up or Key.Down when IsTextSelectionMode:
                if (_textSelection!.TableGrid is not null)
                {
                    _textSelection.MoveCell(e.Key, shift);
                    e.Handled = true;
                }
                break;

            case Key.Left:
                MoveBy(-nudge, 0);
                e.Handled = true;
                break;
            case Key.Right:
                MoveBy(nudge, 0);
                e.Handled = true;
                break;
            case Key.Up:
                MoveBy(0, -nudge);
                e.Handled = true;
                break;
            case Key.Down:
                MoveBy(0, nudge);
                e.Handled = true;
                break;

            case Key.C when ctrl:
                CopyImageToClipboard();
                e.Handled = true;
                break;

            case Key.A when ctrl && IsTextSelectionMode:
                _textSelection!.SelectAll();
                e.Handled = true;
                break;

            case Key.S when ctrl && shift:
                RequestSave(PinSaveMode.SaveAs);
                e.Handled = true;
                break;

            case Key.S when ctrl:
                RequestSave(PinSaveMode.QuickSave);
                e.Handled = true;
                break;
        }
    }

    private void ToggleTextSelection()
    {
        if (IsTextSelectionMode)
        {
            LeaveTextSelection();
            return;
        }
        if (_originalText is null)
        {
            ShowFeedback(UiText.Get("Pin_SelectTextImageHint"), FeedbackKind.Result);
            return;
        }

        _ctrlClickTimer.Stop();
        EndDrag();
        HideHoverBar();
        var selection = new PinTextSelectionView(_content);
        var clip = new RectangleGeometry(ImageClipBounds(new Size(Width, Height)),
            Math.Max(0, FrameCornerRadiusDip - FrameClipInsetDip),
            Math.Max(0, FrameCornerRadiusDip - FrameClipInsetDip));
        selection.Clip = clip;
        selection.SizeChanged += (_, args) => clip.Rect = ImageClipBounds(args.NewSize);
        selection.ContextMenu = ContextMenu;
        selection.SetZoom(_state.Zoom);
        _textSelection = selection;
        _chrome.Children.Insert(1, selection);
        if (_selectTextItem is not null) _selectTextItem.IsChecked = true;
        SelectionFocus.Enter(this, selection);
        ShowFeedback(UiText.Get("Pin_SelectTextStarted"), FeedbackKind.Result);
    }

    private void LeaveTextSelection(bool restoreFocus = true)
    {
        if (_textSelection is null) return;
        _chrome.Children.Remove(_textSelection);
        _textSelection = null;
        if (_selectTextItem is not null) _selectTextItem.IsChecked = false;
        if (restoreFocus && IsVisible && !_state.IsHidden && SelectionFocus.IsActive(this))
        {
            SelectionFocus.Restore(this);
        }
        ShowFeedback(UiText.Get("Pin_SelectTextEnded"), FeedbackKind.Confirmation);
        if (IsMouseOver) ShowHoverBar();
    }

    // ----- State mirroring -----

    private void MoveBy(double dx, double dy)
    {
        if (_handle == IntPtr.Zero)
        {
            Left += dx;
            Top += dy;
        }
        else
        {
            (int left, int top, _, _) = WindowStyleFacade.GetWindowBounds(_handle);
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            MovePhysical(left + (dx * dpi.DpiScaleX), top + (dy * dpi.DpiScaleY));
        }
        if (!_isClosed) ViewStateChanged?.Invoke();
    }

    private void MovePhysical(double left, double top)
    {
        (int currentLeft, int currentTop, int right, int bottom) = WindowStyleFacade.GetWindowBounds(_handle);
        double width = right - currentLeft;
        double height = bottom - currentTop;
        if (width <= 0 || height <= 0) return;
        RectD desktop = _dragDesktop ?? MonitorEnumerator.GetVirtualDesktopBounds();
        double grabMarginPx = _dragging
            ? _dragGrabMarginPx
            : GrabMarginDip * VisualTreeHelper.GetDpi(this).DpiScaleX;
        (left, top) = PinGeometry.KeepGrabbable(left, top, width, height,
            desktop.Left, desktop.Top, desktop.Width, desktop.Height,
            grabMarginPx);
        var target = new RectD(left, top, width, height);
        if (_dragging)
        {
            PhysicalWindowPositioner.Move(_handle, target);
        }
        else
        {
            PhysicalWindowPositioner.PlaceTopmost(_handle, target);
        }
    }

    internal void MovePhysicalForTest(double left, double top) => MovePhysical(left, top);

    private void ZoomAt(int notches, double pointerXDip, double pointerYDip)
    {
        double oldZoom = _state.Zoom;
        double newZoom = _state.ApplyZoomStep(notches);
        if (Math.Abs(newZoom - oldZoom) < 1e-9)
        {
            ShowFeedback($"{newZoom * 100:0}%");
            return;
        }

        (double newLeft, double newTop) = PinGeometry.AnchorTopLeftForZoom(
            Left, Top, oldZoom, newZoom, pointerXDip, pointerYDip);

        Width = _state.WidthDip;
        Height = _state.HeightDip;
        ApplyPositionKeepingGrabbable(newLeft, newTop);
        BeginLiveZoom();
        ShowFeedback($"{newZoom * 100:0}%");
    }

    private void ZoomCentered(int notches) =>
        ZoomAt(notches, Left + (Width / 2.0), Top + (Height / 2.0));

    private void ResetZoomCentered()
    {
        double centerX = Left + (Width / 2.0);
        double centerY = Top + (Height / 2.0);
        double oldZoom = _state.Zoom;
        double newZoom = _state.ResetZoom();

        (double newLeft, double newTop) = PinGeometry.AnchorTopLeftForZoom(
            Left, Top, oldZoom, newZoom, centerX, centerY);

        Width = _state.WidthDip;
        Height = _state.HeightDip;
        ApplyPositionKeepingGrabbable(newLeft, newTop);
        BeginLiveZoom();
        ShowFeedback("100%");
    }

    private void BeginLiveZoom()
    {
        _textSelection?.SetZoom(_state.Zoom);
        _liveZooming = true;
        RenderOptions.SetBitmapScalingMode(_imageElement, BitmapScalingMode.LowQuality);
        _imageElement.CacheMode ??= new BitmapCache { RenderAtScale = 1, EnableClearType = false, SnapsToDevicePixels = true };
        _pinScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _pinScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _pinScale.ScaleX = 1;
        _pinScale.ScaleY = 1;
        _zoomSettleTimer.Stop();
        _zoomSettleTimer.Start();
    }

    private void OnZoomSettleTick(object? sender, EventArgs e)
    {
        _zoomSettleTimer.Stop();
        CommitCrispZoom();
    }

    /// <summary>Test hook: commit the live-zoom cache the way the settle timer would.</summary>
    internal void ForceZoomSettleForTest() => CommitCrispZoom();

    internal bool IsLiveZoomingForTest => _liveZooming;

    private void CommitCrispZoom()
    {
        bool wasZooming = _liveZooming;
        _liveZooming = false;
        _imageElement.CacheMode = null;
        RenderOptions.SetBitmapScalingMode(_imageElement, BitmapScalingMode.HighQuality);
        _imageElement.InvalidateVisual();
        _chrome.InvalidateVisual();
        if (wasZooming && !_isClosed) ViewStateChanged?.Invoke();
    }

    private void ApplyPositionKeepingGrabbable(double left, double top)
    {
        (double dLeft, double dTop, double dWidth, double dHeight) = VirtualDesktopDip();
        (left, top) = PinGeometry.KeepGrabbable(
            left, top, Width, Height, dLeft, dTop, dWidth, dHeight, GrabMarginDip);
        Left = left;
        Top = top;
    }

    private (double Left, double Top, double Width, double Height) VirtualDesktopDip()
    {
        // Project the physical desktop into this window's current local DIP plane.
        // A primary-monitor DIP rectangle clips movement on mixed-DPI desktops.
        RectD desktop = MonitorEnumerator.GetVirtualDesktopBounds();
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        (int physicalLeft, int physicalTop, _, _) = WindowStyleFacade.GetWindowBounds(_handle);
        return (
            Left + ((desktop.Left - physicalLeft) / dpi.DpiScaleX),
            Top + ((desktop.Top - physicalTop) / dpi.DpiScaleY),
            desktop.Width / dpi.DpiScaleX,
            desktop.Height / dpi.DpiScaleY);
    }

    private void CopyImageToClipboard()
    {
        if (_textSelection?.SelectedOriginalText is { } selection)
        {
            ShowFeedback(UiText.Get("Text_BDB7C68F7452"), FeedbackKind.Progress);
            OriginalTextCopyRequested?.Invoke(this, selection);
            return;
        }
        ShowFeedback(UiText.Get("Text_4C160257F02C"), FeedbackKind.Progress);
        CopyRequested?.Invoke(this, new PinImageCopyRequest(_image, SourcePageUrl, _originalText));
    }

    private void RequestOcr()
    {
        ShowFeedback(UiText.Get("Text_15A871A266BE"), FeedbackKind.Progress);
        OcrRequested?.Invoke(this, _image);
    }

    private void HandleCtrlDoubleClick()
    {
        _ctrlClickTimer.Stop();
        if (IsTextSelectionMode) return;
        if (_originalText is not null)
        {
            RequestOriginalTextCopy();
            return;
        }

        RequestOcr();
    }

    private void RequestOriginalTextCopy()
    {
        if (_originalText is null)
        {
            return;
        }

        ShowFeedback(UiText.Get("Text_BDB7C68F7452"), FeedbackKind.Progress);
        OriginalTextCopyRequested?.Invoke(this, _originalText);
    }

    private void RequestSave(PinSaveMode mode)
    {
        if (_saveInProgress)
        {
            ShowFeedback(UiText.Get("Text_88DAAEEDFE4C"), FeedbackKind.Progress);
            return;
        }

        EventHandler<PinSaveRequestedEventArgs>? handler = SaveRequested;
        if (handler is null)
        {
            ShowFeedback(UiText.Get("Text_9A07C688760E"), FeedbackKind.Warning);
            return;
        }

        _saveInProgress = true;
        ShowFeedback(
            mode == PinSaveMode.SaveAs ? UiText.Get("Text_B53B72EF2371") : UiText.Get("Text_88DAAEEDFE4C"),
            FeedbackKind.Progress);
        handler.Invoke(this, new PinSaveRequestedEventArgs(mode, _image, SourcePageUrl));
    }

    private double AdjustOpacity(double delta)
    {
        double oldOpacity = Opacity;
        double target = _state.AdjustOpacity(delta);
        BeginAnimation(Window.OpacityProperty, null);
        Opacity = target;
        if (FluidMotion.AnimationsEnabled)
        {
            var fade = new DoubleAnimation(oldOpacity, target, FluidMotion.NormalDuration)
            {
                EasingFunction = FluidMotion.SoftLandingEasing,
                FillBehavior = FillBehavior.Stop,
            };
            BeginAnimation(Window.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        }

        if (!_isClosed) ViewStateChanged?.Invoke();
        return target;
    }

    private void AnimatePinReveal()
    {
        if (_isClosed)
        {
            return;
        }

        _chrome.BeginAnimation(UIElement.OpacityProperty, null);
        _pinScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _pinScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _chrome.Opacity = 1;
        _pinScale.ScaleX = 1;
        _pinScale.ScaleY = 1;

        if (!FluidMotion.AnimationsEnabled)
        {
            return;
        }

        DoubleAnimation fade = CreateRevealAnimation();
        _chrome.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    internal static DoubleAnimation CreateRevealAnimation() => new(0, 1, FluidMotion.FastDuration)
    {
        FillBehavior = FillBehavior.Stop,
    };

    internal PinLandingDecoration? LandingDecoration => _landingDecoration;
    internal bool LandingStarted { get; private set; }

    /// <summary>Called in the same dispatcher turn immediately after a new/reopened pin's Show.</summary>
    internal void StartLandingEffect()
    {
        if (_isClosed || !IsVisible || _state.IsHidden) return;
        StopLandingEffect();
        // A restored HWND may have changed DPI during Show. Settle that physical move
        // before the ornament takes its bounds; the manager still verifies/reports it.
        bool placementSettled = ApplyRestorePlacement();
        LandingStarted = true;
        _landingClock = Stopwatch.StartNew();
        (int left, int top, int right, int bottom) = PhysicalBounds;
        _landingBounds = new RectD(left, top, right - left, bottom - top);
        LocationChanged += OnLandingBoundsChanged;
        SizeChanged += OnLandingSizeChanged;
        IsVisibleChanged += OnLandingVisibilityChanged;

        if (!FluidMotion.AnimationsEnabled)
        {
            // Reduced motion is an immediate colour change followed by an immediate reset;
            // neither the content nor the outline has an animation clock in this path.
            _landingAccentAmount = 1;
            UpdateChromeColor(animate: false);
            _landingResetTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(PinLandingDecoration.OutlineEndMs),
            };
            _landingResetTimer.Tick += OnLandingReset;
            _landingResetTimer.Start();
            return;
        }

        CompositionTarget.Rendering += OnLandingRendering;
        bool withinBurst = PinLandingDecoration.Throttle.TryAcquire(PinLandingDecoration.ReadOpeningTime());
        try
        {
            bool remote = PinLandingDecoration.ReadRemoteSession();
            if (placementSettled && withinBurst && PinLandingDecoration.IsEligible(
                animations: true, remote, layered: !remote && PinLandingDecoration.ReadLayeredSupport()))
            {
                _landingDecoration = PinLandingDecoration.TryShow(this, _landingClock);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // A failed optional capability probe must not interrupt activation or the
            // manager's open-pin notification. The already-open pin keeps its outline flash.
        }
    }

    private void OnLandingRendering(object? sender, EventArgs e)
    {
        if (_landingClock is not { } clock) return;
        double elapsed = clock.Elapsed.TotalMilliseconds;
        if (elapsed >= PinLandingDecoration.OutlineEndMs)
        {
            StopLandingEffect();
            return;
        }
        double amount = PinLandingDecoration.Render(elapsed).BorderAccentAmount;
        if (amount == _landingAccentAmount) return;
        _landingAccentAmount = amount;
        UpdateChromeColor(animate: false);
    }

    private void OnLandingReset(object? sender, EventArgs e) => StopLandingEffect();

    private void OnLandingBoundsChanged(object? sender, EventArgs e)
    {
        (int left, int top, int right, int bottom) = PhysicalBounds;
        if (new RectD(left, top, right - left, bottom - top) != _landingBounds) StopLandingEffect();
    }

    private void OnLandingSizeChanged(object sender, SizeChangedEventArgs e) => OnLandingBoundsChanged(sender, e);

    private void OnLandingVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) StopLandingEffect();
    }

    private void StopLandingEffect()
    {
        CompositionTarget.Rendering -= OnLandingRendering;
        LocationChanged -= OnLandingBoundsChanged;
        SizeChanged -= OnLandingSizeChanged;
        IsVisibleChanged -= OnLandingVisibilityChanged;
        if (_landingResetTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnLandingReset;
            _landingResetTimer = null;
        }
        _landingClock = null;
        if (_landingDecoration is { IsClosed: false } decoration) decoration.Close();
        _landingDecoration = null;
        _landingAccentAmount = 0;
        UpdateChromeColor(animate: false);
    }

    private static Color BlendFrameColor(Color rest, Color accent, double amount) => Color.FromArgb(
        (byte)Math.Round(rest.A + ((accent.A - rest.A) * amount)),
        (byte)Math.Round(rest.R + ((accent.R - rest.R) * amount)),
        (byte)Math.Round(rest.G + ((accent.G - rest.G) * amount)),
        (byte)Math.Round(rest.B + ((accent.B - rest.B) * amount)));

    /// <summary>
    /// Tracks the configured double-click interval. Expiry never changes the clipboard;
    /// the second click invokes text copy and Ctrl+C independently invokes image copy.
    /// </summary>
    private void StartCtrlClickCopyDebounce()
    {
        if (IsTextSelectionMode) return;
        int debounceMs = Math.Max(0, _settings().CtrlClickDebounceMs);
        _ctrlClickTimer.Stop();

        if (debounceMs == 0)
        {
            // A Ctrl single-click only focuses the pin. Ctrl+C copies its image.
            return;
        }

        _ctrlClickTimer.Interval = TimeSpan.FromMilliseconds(debounceMs);
        _ctrlClickTimer.Start();
    }

    private void OnCtrlClickTimerTick(object? sender, EventArgs e)
    {
        _ctrlClickTimer.Stop();
    }

    // ----- Feedback toast -----

    /// <summary>
    /// Shows <paramref name="text"/> at the bottom of the pin for as long as
    /// <see cref="FeedbackTiming"/> gives <paramref name="kind"/> over the user's content.
    /// </summary>
    /// <remarks>
    /// Progress and failures to act on have no display time. A zoom or opacity readout passes
    /// over them and they come back when it expires; any other message is the newer outcome
    /// and replaces them.
    /// </remarks>
    private void ShowFeedback(string text, FeedbackKind kind = FeedbackKind.Echo)
    {
        // OCR/clipboard completions can arrive after Close detached the timer's stop handler.
        // Never restart that timer or animate a window whose lifetime has ended.
        if (_isClosed) return;
        if (kind != FeedbackKind.Echo)
        {
            _heldFeedback = FeedbackTiming.DisplayTime(kind) is null ? (text, kind) : null;
        }

        PresentFeedback(text, kind, announce: true);
    }

    private void PresentFeedback(string text, FeedbackKind kind, bool announce)
    {
        bool dismissible = FeedbackTiming.StaysUntilDismissed(kind);
        bool showing = _feedback.Visibility == Visibility.Visible && !_feedbackHiding;
        _feedbackGeneration++;
        _feedbackHiding = false;
        _feedbackKind = kind;
        _feedbackText.Text = text;
        _feedbackText.SetResourceReference(TextBlock.ForegroundProperty, dismissible ? "State.Danger" : "Text.Primary");
        _feedbackDismiss.Visibility = dismissible ? Visibility.Visible : Visibility.Collapsed;
        _feedback.Visibility = Visibility.Visible;
        _feedback.BeginAnimation(UIElement.OpacityProperty, null);
        _feedback.Opacity = 1;
        // A message replacing a visible one changes in place; only a new appearance fades in.
        if (!showing && FluidMotion.AnimationsEnabled)
        {
            var fade = new DoubleAnimation(0, 1, FluidMotion.FastDuration)
            {
                EasingFunction = FluidMotion.SoftLandingEasing,
                FillBehavior = FillBehavior.Stop,
            };
            _feedback.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        }

        // Every message restarts the countdown, so the latest text gets its full display time.
        _feedbackCountdown.Start(FeedbackTiming.DisplayTime(kind, overContent: true));
        if (announce) FeedbackTiming.Announce(_feedbackText, text, kind, "MyCapture.Pin.Feedback");
    }

    private void OnFeedbackExpired()
    {
        if (_isClosed) return;
        if (_heldFeedback is { } held)
        {
            PresentFeedback(held.Text, held.Kind, announce: false);
            return;
        }

        HideFeedback(immediate: false);
    }

    /// <summary>Closes a failure message that stays until dismissed; false when none is showing.</summary>
    private bool DismissFeedback()
    {
        if (!IsFeedbackVisible || !FeedbackTiming.StaysUntilDismissed(_feedbackKind)) return false;
        _heldFeedback = null;
        _feedbackCountdown.Stop();
        HideFeedback(immediate: false);
        return true;
    }

    private void HideFeedback(bool immediate)
    {
        int generation = ++_feedbackGeneration;
        _feedbackKind = FeedbackKind.Echo;
        _feedbackDismiss.Visibility = Visibility.Collapsed;
        _feedback.BeginAnimation(UIElement.OpacityProperty, null);
        if (immediate || !FluidMotion.AnimationsEnabled || _isClosed)
        {
            _feedbackHiding = false;
            _feedback.Visibility = Visibility.Collapsed;
            _feedback.Opacity = 1;
            return;
        }

        _feedbackHiding = true;
        var fade = new DoubleAnimation(_feedback.Opacity, 0, FluidMotion.FastDuration)
        {
            EasingFunction = FluidMotion.StandardEasing,
            FillBehavior = FillBehavior.Stop,
        };
        fade.Completed += (_, _) =>
        {
            // A message shown during the fade owns the overlay now.
            if (generation != _feedbackGeneration) return;
            _feedbackHiding = false;
            _feedback.Visibility = Visibility.Collapsed;
            _feedback.Opacity = 1;
        };
        _feedback.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    // ----- Context menu -----

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(MenuItemFor(
            UiText.Get("Text_A81F975C401F"),
            (_, _) => RequestSave(PinSaveMode.SaveAs),
            UiText.Get("Text_78C743CC6F59"),
            "Icon.SaveAs",
            "Ctrl+Shift+S"));
        menu.Items.Add(MenuItemFor(
            UiText.Get("Text_DFC084A111D0"),
            (_, _) => RequestSave(PinSaveMode.QuickSave),
            UiText.Get("Text_8300472A5BE3"),
            "Icon.Save",
            "Ctrl+S"));
        menu.Items.Add(MenuItemFor(
            UiText.Get("Text_37B3D3B11B26"),
            (_, _) => CopyImageToClipboard(),
            iconResourceKey: "Icon.Copy",
            inputGestureText: "Ctrl+C"));
        if (_originalText is not null)
        {
            menu.Items.Add(MenuItemFor(
                UiText.Get("Text_976A6C92B870"),
                (_, _) => RequestOriginalTextCopy(),
                UiText.Get("Text_BB7798D87E07"),
                "Icon.Copy",
                UiText.Get("Text_C7011B8D345C")));
            _selectTextItem = MenuItemFor(UiText.Get("Pin_SelectText"),
                (_, _) => ToggleTextSelection(), inputGestureText: "F2");
            _selectTextItem.IsCheckable = true;
            menu.Items.Add(_selectTextItem);
        }
        else
        {
            menu.Items.Add(MenuItemFor(UiText.Get("Text_03A1596F1681"), (_, _) => RequestOcr(), iconResourceKey: "Icon.Copy", inputGestureText: UiText.Get("Text_C7011B8D345C")));
        }
        // Every key sits in the right-hand column; the names carry no key text.
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("100%", (_, _) => ResetZoomCentered(), inputGestureText: "0"));
        menu.Items.Add(MenuItemFor(UiText.Get("Onboard_PinZoomIn"), (_, _) => ZoomCentered(1), iconResourceKey: "Icon.ZoomIn", inputGestureText: "+"));
        menu.Items.Add(MenuItemFor(UiText.Get("Onboard_PinZoomOut"), (_, _) => ZoomCentered(-1), iconResourceKey: "Icon.ZoomOut", inputGestureText: "-"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor(UiText.Get("Onboard_PinMoreTransparent"), (_, _) =>
        {
            double opacity = AdjustOpacity(-OpacityStep);
            ShowFeedback(UiText.Format("Text_C740003A2150", opacity * 100));
        }, inputGestureText: UiText.Get("Onboard_PinWheelDown")));
        menu.Items.Add(MenuItemFor(UiText.Get("Onboard_PinMoreOpaque"), (_, _) =>
        {
            double opacity = AdjustOpacity(OpacityStep);
            ShowFeedback(UiText.Format("Text_C740003A2150", opacity * 100));
        }, inputGestureText: UiText.Get("Onboard_PinWheelUp")));
        menu.Items.Add(new Separator());
        _clickThroughItem = MenuItemFor(
            UiText.Get("Onboard_PinAllowClickThrough"),
            (_, _) => ToggleClickThrough(),
            UiText.Format("Onboard_PinAllowClickThroughHelp", ClickThroughRecoveryHint()),
            "Icon.Select",
            ClickThroughGesture());
        menu.Items.Add(_clickThroughItem);
        menu.Items.Add(new Separator());
        _restoreClosedItem = MenuItemFor(
            UiText.Get("Onboard_TrayReopenPin"),
            (_, _) => RestoreClosedRequested?.Invoke(this, EventArgs.Empty),
            UiText.Get("PinRestore_CommandHelp"),
            "Icon.Pin",
            RestoreClosedGesture());
        _restoreClosedItem.IsEnabled = false;
        menu.Items.Add(_restoreClosedItem);
        menu.Items.Add(MenuItemFor(UiText.Get("Dialog.Close"), (_, _) => CloseWithExit(), iconResourceKey: "Icon.Close", inputGestureText: "Esc"));
        menu.Items.Add(MenuItemFor(UiText.Get("Text_E657405F6B2D"), (_, _) => CloseAllRequested?.Invoke(this, EventArgs.Empty), iconResourceKey: "Icon.Close"));
        menu.Opened += (_, _) => RefreshRestoreClosedMenuItem();

        ContextMenu = menu;
    }

    /// <summary>
    /// Enables the restore command only while the history has a closed pin, and refreshes its
    /// gesture so a hotkey changed in settings is shown on pins that were already open.
    /// </summary>
    internal void RefreshRestoreClosedMenuItem()
    {
        if (_restoreClosedItem is null)
        {
            return;
        }

        _restoreClosedItem.IsEnabled = CanRestoreClosedPin?.Invoke() == true;
        _restoreClosedItem.InputGestureText = RestoreClosedGesture();
        if (_clickThroughItem is not null) _clickThroughItem.InputGestureText = ClickThroughGesture();
    }

    private static string ClickThroughGesture()
    {
        Hotkey key = ShortcutHelpWindow.ReadHotkeys().ToggleClickThrough;
        return key.IsAssigned ? key.ToString() : string.Empty;
    }

    /// <summary>Test hook: the context-menu restore command.</summary>
    internal MenuItem? RestoreClosedMenuItemForTest => _restoreClosedItem;

    private static string RestoreClosedGesture()
    {
        Hotkey key = ShortcutHelpWindow.ReadHotkeys().RestoreClosedPin;
        return key.IsAssigned ? key.ToString() : string.Empty;
    }

    /// <summary>
    /// How to turn click-through off again with the current shortcuts: the toggle key, the hide-all
    /// key twice, or else the tray menu's command for every pin. The pin's feedback says it; the
    /// tutorial clip generator shows the same words in its caption.
    /// </summary>
    internal static string ClickThroughRecoveryHint()
    {
        HotkeySettings keys = ShortcutHelpWindow.ReadHotkeys();
        return keys.ToggleClickThrough.IsAssigned ? keys.ToggleClickThrough.ToString()
            : keys.HideAllPins.IsAssigned ? UiText.Format("Hotkey.Twice", keys.HideAllPins)
            : UiText.Get("Hotkey.TrayRestore");
    }

    private static MenuItem MenuItemFor(
        string header,
        RoutedEventHandler onClick,
        string? helpText = null,
        string? iconResourceKey = null,
        string? inputGestureText = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = inputGestureText };
        AutomationProperties.SetName(item, header);
        if (!string.IsNullOrEmpty(inputGestureText)) AutomationProperties.SetAcceleratorKey(item, inputGestureText);
        if (helpText is not null)
        {
            AutomationProperties.SetHelpText(item, helpText);
        }

        if (iconResourceKey is not null && Application.Current?.TryFindResource(iconResourceKey) is Geometry geometry)
        {
            var glyph = SymbolIcon.Create(geometry, SymbolIcon.InlineSize);
            glyph.SetBinding(SymbolIcon.ForegroundProperty, new Binding(nameof(Control.Foreground))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(MenuItem), 1),
                FallbackValue = Application.Current.TryFindResource("Text.Secondary") as Brush ?? Brushes.LightGray,
            });
            item.Icon = glyph;
        }

        item.Click += onClick;
        return item;
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowClosed = true;
        StartClosing();
        // A direct close during an exit (close all, app exit) ends it; its deferred close is moot.
        FluidMotion.CompleteExit(this);
        StopLandingEffect();
        EndDrag();
        _feedbackCountdown.Stop();
        _ctrlClickTimer.Stop();
        _ctrlClickTimer.Tick -= OnCtrlClickTimerTick;
        _zoomSettleTimer.Stop();
        _zoomSettleTimer.Tick -= OnZoomSettleTick;
        _hoverHideTimer.Stop();
        _hoverHideTimer.Tick -= OnHoverHideTick;
        _hoverBar?.Close();
        _hoverBar = null;
        SourceInitialized -= OnSourceInitialized;
        Loaded -= OnPinLoaded;
        base.OnClosed(e);
    }
}
