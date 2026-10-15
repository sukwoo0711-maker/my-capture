using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Capture;

/// <summary>
/// Borderless, selection-only frozen-frame overlay. It never hosts editing UI.
/// </summary>
internal sealed class CaptureOverlayWindow : Window
{
    private FrozenFrame? _frame;
    private readonly RectD _screenBounds;
    private readonly CaptureOverlayView _view;
    private readonly bool _abortOnFocusLoss;
    private bool _completed;
    private Task<IReadOnlyList<WindowCandidate>>? _pendingSourceWindows;
    private bool _placingPhysicalBounds;
    private RectD? _pendingSelection;
    private RegionSelectionEventArgs? _pendingSelectionDetails;

    internal CaptureOverlayWindow(
        FrozenFrame frame,
        bool abortOnFocusLoss,
        bool showMagnifier = true,
        bool showSelectionGrid = true)
        : this(frame.ScreenBounds, abortOnFocusLoss, showMagnifier, frame, showSelectionGrid)
    {
    }

    internal CaptureOverlayWindow(
        RectD screenBounds,
        bool abortOnFocusLoss,
        bool showMagnifier,
        FrozenFrame? frame = null,
        bool showSelectionGrid = true)
    {
        _screenBounds = screenBounds.ToPixelBounds();
        if (_screenBounds.IsEmpty)
        {
            throw new ArgumentException("A non-empty virtual-desktop rectangle is required.", nameof(screenBounds));
        }

        _frame = frame;
        _abortOnFocusLoss = abortOnFocusLoss;
        _view = frame is null
            ? new CaptureOverlayView(_screenBounds, frame: null, showMagnifier, showSelectionGrid)
            : new CaptureOverlayView(frame, showMagnifier, showSelectionGrid);

        // Frozen pixels and selection geometry must appear immediately and exactly; a reveal
        // transform here would expose the live desktop for a frame and make edge selection feel
        // imprecise. All normal application windows still use the shared entrance motion.
        FluidMotion.SetWindowEntrance(this, false);
        ConfigureWindowChrome(_screenBounds);
        Content = _view;

        _view.SelectionConfirmed += OnSelectionConfirmed;
        _view.CancelRequested += OnCancelRequested;
        _view.ActionRequested += OnActionRequested;
        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnContentRendered;
        DpiChanged += OnDpiChanged;
        Deactivated += OnDeactivated;
        Activated += OnActivated;
        IsForeground = () => GetForegroundWindow() == new WindowInteropHelper(this).Handle;
        TakeForeground = () => _ = Activate();
    }

    private void ConfigureWindowChrome(RectD screenBounds)
    {
        Title = UiText.Get("Text_3816B940D53C");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = false;
        Background = System.Windows.Media.Brushes.Black;
        // Anchor HWND creation on the virtual desktop's top-left display. SourceInitialized then
        // applies the exact physical-pixel rectangle with SetWindowPos; this initial placement
        // prevents WPF from choosing the primary monitor's DPI for a negative-origin desktop.
        MonitorInfo anchor = MonitorEnumerator.GetFromPoint(
            new PointD(screenBounds.Left, screenBounds.Top));
        double scale = anchor.ScaleFactor > 0 ? anchor.ScaleFactor : 1.0;
        Left = screenBounds.Left / scale;
        Top = screenBounds.Top / scale;
        Width = Math.Max(1, screenBounds.Width / scale);
        Height = Math.Max(1, screenBounds.Height / scale);
    }

    internal void AttachFrame(FrozenFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_completed) return;
        if (frame.ScreenBounds.ToPixelBounds() != _screenBounds.ToPixelBounds())
            throw Themes.UserFacingError.Mark(new InvalidOperationException(UiText.Get("Capture.DisplayChanged")));
        _frame = frame;
        _view.AttachFrame(frame);
        if (IsVisible)
        {
            PlacePhysicalBounds();
        }

        CompletePendingSelection();
    }

    /// <summary>
    /// The window list's geometry, z-order and titles are final, but the page and application details
    /// are still being read. A selection never waits for them: it completes at once with its title and
    /// carries the details as <see cref="CaptureSelectionCompletedEventArgs.PendingSource"/>, resolved for
    /// the same window from <paramref name="confirmed"/> exactly as an immediate selection would be.
    /// When the read finishes while the selector is still open, its list is replaced, so later
    /// selections carry the details directly.
    /// </summary>
    internal void AwaitSourceDetails(Task<IReadOnlyList<WindowCandidate>> confirmed)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        if (_completed) return;
        if (confirmed.IsCompletedSuccessfully)
        {
            ConfirmSourceDetails(confirmed.Result);
            return;
        }

        _pendingSourceWindows = confirmed;
        _ = confirmed.ContinueWith(task => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_pendingSourceWindows, task)) return;
            if (task.IsCompletedSuccessfully) ConfirmSourceDetails(task.Result);
            else _pendingSourceWindows = null;
        })), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Replaces the window list with its confirmed details.</summary>
    internal void ConfirmSourceDetails(IReadOnlyList<WindowCandidate> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (_completed) return;
        SourceWindows = windows;
        _pendingSourceWindows = null;
    }

    internal bool SourceDetailsPendingForTest => _pendingSourceWindows is not null;

    private void CompletePendingSelection()
    {
        if (_frame is null || _pendingSelection is not RectD pending) return;
        RegionSelectionEventArgs? details = _pendingSelectionDetails;
        _pendingSelection = null;
        _pendingSelectionDetails = null;
        CompleteSelection(pending, details?.DetectedWindow, details?.TextCapture == true,
            details?.WholeMonitor == true);
    }

    /// <summary>Raised after a valid drag is cropped and the overlay has been hidden.</summary>
    internal event EventHandler<CaptureSelectionCompletedEventArgs>? SelectionCompleted;

    internal event EventHandler? SelectionCancelled;

    /// <summary>
    /// Optional fixed-size preset for this selection. When set, the overlay lets the user
    /// drag only the top-left position: the drag start seeds a rectangle of exactly the
    /// preset size (clamped to the frame), so repeated recordings keep identical dimensions.
    /// </summary>
    internal System.Drawing.Size? PresetSize
    {
        get => _view.PresetSize;
        set => _view.PresetSize = value;
    }

    /// <summary>Recording needs geometry only; it must not allocate a cropped screenshot.</summary>
    internal Action<RectD>? GeometrySelectionCompleted { get; set; }

    internal IReadOnlyList<WindowCandidate> SourceWindows
    {
        get => _view.WindowCandidates;
        set => _view.WindowCandidates = value ?? [];
    }

    /// <summary>Highlights and click-selects the top-level window under the pointer.</summary>
    internal bool DetectWindows
    {
        get => _view.DetectWindows;
        set => _view.DetectWindows = value;
    }

    /// <summary>Copies the frozen pixel colour on C; null disables the key.</summary>
    internal Func<string, Task<bool>>? CopyColorText
    {
        get => _view.CopyColorText;
        set => _view.CopyColorText = value;
    }

    internal Action<ColorCopyFeedback>? ColorCopied
    {
        get => _view.ColorCopied;
        set => _view.ColorCopied = value;
    }

    internal Func<MyCapture.Core.Settings.ColorFormat> ReadColorFormat
    {
        get => _view.ReadColorFormat;
        set => _view.ReadColorFormat = value ?? (() => MyCapture.Core.Settings.ColorFormat.Hex);
    }

    /// <summary>Lets T switch this selection between image and text capture.</summary>
    internal bool TextCaptureAvailable
    {
        get => _view.TextCaptureAvailable;
        set => _view.TextCaptureAvailable = value;
    }

    internal bool TextCaptureMode
    {
        get => _view.TextCaptureMode;
        set => _view.TextCaptureMode = value;
    }

    /// <summary>Shows the key badges in the mode bar; the status and target tab always show.</summary>
    internal bool ShowShortcutHints
    {
        get => _view.ShowShortcutHints;
        set => _view.ShowShortcutHints = value;
    }

    /// <summary>The screenshot selector's mode bar at the top of the pointer's monitor.</summary>
    internal bool ShowModeBar
    {
        get => _view.ShowModeBar;
        set => _view.ShowModeBar = value;
    }

    /// <summary>The short start guide in the middle of the bar's monitor.</summary>
    internal bool ShowStartGuide
    {
        get => _view.ShowStartGuide;
        set => _view.ShowStartGuide = value;
    }

    /// <summary>The bar's capture/record switch and ⋯ menu; the owner runs <see cref="ActionRequested"/>.</summary>
    internal bool OffersOtherActions
    {
        get => _view.OffersOtherActions;
        set => _view.OffersOtherActions = value;
    }

    internal Func<bool> CanRepeatLastRegion
    {
        get => _view.CanRepeatLastRegion;
        set => _view.CanRepeatLastRegion = value ?? (() => true);
    }

    /// <summary>
    /// Raised when the record switch or a ⋯ command was chosen. The selector then closes without a
    /// capture, like a cancel, and the owner starts the command once it is gone.
    /// </summary>
    internal event EventHandler<CaptureSelectorRequest>? ActionRequested;

    /// <summary>The recording region selector's wording and colour.</summary>
    internal bool RecordingSelection
    {
        get => _view.RecordingSelection;
        set
        {
            _view.RecordingSelection = value;
            Title = UiText.Get(value ? "Onboard_RecordSelectTitle" : "Text_3816B940D53C");
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // EnsureHandle may create this HWND solely to apply recording exclusion.
        // PlaceTopmost uses SWP_SHOWWINDOW, so never call it while still hidden.
        // ContentRendered reasserts physical placement after the authorized Show.
        _view.OwnWindowHandle = new WindowInteropHelper(this).Handle;
        if (IsVisible) PlacePhysicalBounds();
    }

    private void PlacePhysicalBounds()
    {
        if (_placingPhysicalBounds)
        {
            return;
        }

        _placingPhysicalBounds = true;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            if (hwnd != IntPtr.Zero)
            {
                PhysicalWindowPositioner.PlaceTopmost(hwnd, _frame?.ScreenBounds ?? _screenBounds);
            }
        }
        finally
        {
            _placingPhysicalBounds = false;
        }
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        // WPF can perform one final DPI/layout adjustment after SourceInitialized. Reassert the
        // virtual-desktop physical rectangle before accepting input so mixed-DPI edges stay exact.
        PlacePhysicalBounds();
        _view.Focus();
        _ = Activate();
        _view.InitializePointer();
    }

    /// <summary>
    /// On the first capture after launch Windows activates the new window inside its own thread but
    /// leaves the other program in the foreground until the coordinator activates it after
    /// <see cref="Window.Show"/> returns, 30 to 60 ms later, and keys pressed meanwhile went to that
    /// program. Asking for the foreground here, as the window becomes visible, closes that gap.
    /// </summary>
    private void OnActivated(object? sender, EventArgs e)
    {
        if (!_completed && IsVisible && !_takingForeground && !IsForeground())
        {
            _takingForeground = true;
            try { TakeForeground(); }
            finally { _takingForeground = false; }
        }
        _view.InitializePointer();
    }

    private bool _takingForeground;

    /// <summary>Whether this window is the system foreground window; replaced in tests.</summary>
    internal Func<bool> IsForeground { get; set; }

    /// <summary>Brings this window to the foreground; replaced in tests.</summary>
    internal Action TakeForeground { get; set; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// The selector takes keyboard focus in <see cref="OnContentRendered"/>, after its first frame:
    /// moving focus earlier costs a text-services round trip that delayed that frame. Until then keys
    /// reach this window, which the system activates about 50 ms sooner, and an Esc or a mode key
    /// pressed as the selector appeared was lost. Keys aimed at the window itself now go to the
    /// selector exactly as they would with focus; keys from the selector or its bar reach it directly.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && !_completed && ReferenceEquals(e.OriginalSource, this)) _view.HandleKeyDown(e);
    }

    /// <inheritdoc cref="OnKeyDown"/>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (!e.Handled && !_completed && ReferenceEquals(e.OriginalSource, this)) _view.HandleKeyUp(e);
    }

    private void OnDpiChanged(object? sender, DpiChangedEventArgs e)
    {
        if (!_placingPhysicalBounds)
        {
            _ = Dispatcher.BeginInvoke(new Action(PlacePhysicalBounds));
        }
    }

    private void OnSelectionConfirmed(object? sender, RegionSelectionEventArgs e)
    {
        if (_completed)
        {
            return;
        }

        if (_frame is null && GeometrySelectionCompleted is null)
        {
            _pendingSelection = e.BitmapRegion;
            _pendingSelectionDetails = e;
            return;
        }

        CompleteSelection(e.BitmapRegion, e.DetectedWindow, e.TextCapture, e.WholeMonitor);
    }

    internal void CompleteSelection(RectD bitmapRegion) => CompleteSelection(bitmapRegion, null, false);

    internal void CompleteSelection(RectD bitmapRegion, WindowCandidate? detectedWindow, bool textCapture,
        bool wholeMonitor = false)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _view.EndPointerInteraction();
        if (GeometrySelectionCompleted is { } geometryCompleted)
        {
            RectD screenRegion = new(_screenBounds.Left + bitmapRegion.Left,
                _screenBounds.Top + bitmapRegion.Top, bitmapRegion.Width, bitmapRegion.Height);
            Hide();
            try { geometryCompleted(screenRegion); }
            finally { Close(); }
            return;
        }
        FrozenFrame frame = _frame ?? throw new InvalidOperationException("A frozen capture frame is required.");
        System.Windows.Media.Imaging.BitmapSource crop = ScreenCaptureEngine.Crop(frame, bitmapRegion);
        var selectedScreenRegion = new RectD(_screenBounds.Left + bitmapRegion.Left, _screenBounds.Top + bitmapRegion.Top,
            bitmapRegion.Width, bitmapRegion.Height);
        // The window is chosen from the hotkey-time geometry, z-order and titles, which the confirmed
        // list keeps unchanged; only its page and application details can still be on the way.
        CaptureSelectionSource source = ResolveSelectionSource(SourceWindows, selectedScreenRegion, detectedWindow,
            wholeMonitor);
        Task<CaptureSelectionSource>? pendingSource = !textCapture && _pendingSourceWindows is { IsCompleted: false } pending
            ? ResolveWhenConfirmedAsync(pending, selectedScreenRegion, detectedWindow, wholeMonitor)
            : null;
        if (!textCapture && _pendingSourceWindows is { IsCompletedSuccessfully: true } confirmed)
        {
            // Read finished, but its dispatcher turn to replace the list has not run yet.
            source = ResolveSelectionSource(confirmed.Result, selectedScreenRegion, detectedWindow, wholeMonitor);
        }

        // Remove the dimmed overlay before the normal editor window is created. The original
        // frozen frame and cropped physical pixels are retained; the desktop is never recaptured.
        Hide();
        try
        {
            SelectionCompleted?.Invoke(
                this,
                new CaptureSelectionCompletedEventArgs(frame, bitmapRegion, crop, source.Title,
                    sourcePageUrl: source.PageUrl)
                {
                    TextCapture = textCapture,
                    SourceDetails = source.Details,
                    PendingSource = pendingSource,
                });
        }
        finally
        {
            Close();
        }
    }

    /// <summary>
    /// The title, page and application of the window a selection captures. Application and folder
    /// always come from the same window as the title.
    /// </summary>
    internal static CaptureSelectionSource ResolveSelectionSource(IReadOnlyList<WindowCandidate> windows,
        RectD selectedScreenRegion, WindowCandidate? detectedWindow, bool wholeMonitor)
    {
        ArgumentNullException.ThrowIfNull(windows);
        // The full-screen button, like a desktop click, captures a monitor rather than a window.
        if (wholeMonitor) return CaptureSelectionSource.Empty;
        WindowCandidate? sourceWindow = WindowCandidateService.ResolveSourceWindow(windows, selectedScreenRegion);
        string sourceTitle = sourceWindow?.Title ?? string.Empty;
        string sourcePageUrl = WindowCandidateService.ResolveSourcePageUrl(windows, selectedScreenRegion);
        CaptureSourceDetails sourceDetails = sourceWindow?.Source ?? CaptureSourceDetails.Empty;
        if (detectedWindow is not null)
        {
            // A clicked window is the source even when another window overlaps its centre.
            // Prefer the latest candidate snapshot, which may carry a page URL read after the freeze.
            WindowCandidate window = windows.FirstOrDefault(candidate => candidate.Handle == detectedWindow.Handle)
                ?? detectedWindow;
            // A desktop click captures one whole monitor. Neither the shell's caption nor a
            // window that happens to cover the monitor centre is that capture's source.
            if (WindowCandidateService.IsDesktopShell(window, windows)) return CaptureSelectionSource.Empty;
            if (!string.IsNullOrWhiteSpace(window.Title))
            {
                sourceTitle = window.Title;
                sourcePageUrl = MyCapture.Core.Capture.SourcePageUrl.Normalize(window.SourcePageUrl);
                sourceDetails = window.Source ?? CaptureSourceDetails.Empty;
            }
        }

        return new CaptureSelectionSource(sourceTitle, sourcePageUrl, sourceDetails);
    }

    private static async Task<CaptureSelectionSource> ResolveWhenConfirmedAsync(
        Task<IReadOnlyList<WindowCandidate>> confirmed, RectD selectedScreenRegion, WindowCandidate? detectedWindow,
        bool wholeMonitor)
    {
        IReadOnlyList<WindowCandidate> windows;
        try { windows = await confirmed.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return CaptureSelectionSource.Empty; }
        return ResolveSelectionSource(windows, selectedScreenRegion, detectedWindow, wholeMonitor);
    }

    private void OnCancelRequested(object? sender, EventArgs e) => Cancel();

    private void OnActionRequested(object? sender, CaptureSelectorRequest request)
    {
        if (_completed) return;
        _completed = true;
        Hide();
        try { ActionRequested?.Invoke(this, request); }
        finally { Close(); }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        _view.EndPointerInteraction();
        if (_abortOnFocusLoss && IsVisible && !_completed)
        {
            Cancel();
        }
    }

    private void Cancel()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        SelectionCancelled?.Invoke(this, EventArgs.Empty);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _completed = true;
        _view.ReleaseResources();
        _frame = null;
        _pendingSelection = null;
        _pendingSelectionDetails = null;
        _pendingSourceWindows = null;
        Content = null;
        GeometrySelectionCompleted = null;
        SourceWindows = [];
        _view.SelectionConfirmed -= OnSelectionConfirmed;
        _view.CancelRequested -= OnCancelRequested;
        _view.ActionRequested -= OnActionRequested;
        SourceInitialized -= OnSourceInitialized;
        ContentRendered -= OnContentRendered;
        Deactivated -= OnDeactivated;
        Activated -= OnActivated;
        DpiChanged -= OnDpiChanged;
        base.OnClosed(e);
    }
}

internal sealed class CaptureSelectionCompletedEventArgs : EventArgs
{
    internal CaptureSelectionCompletedEventArgs(
        FrozenFrame frame,
        RectD bitmapRegion,
        System.Windows.Media.Imaging.BitmapSource selectedBitmap,
        string sourceTitle = "",
        bool recordForRepeat = true,
        string sourcePageUrl = "")
    {
        Frame = frame;
        BitmapRegion = bitmapRegion;
        SelectedBitmap = selectedBitmap;
        SourceTitle = sourceTitle ?? string.Empty;
        RecordForRepeat = recordForRepeat;
        SourcePageUrl = MyCapture.Core.Capture.SourcePageUrl.Normalize(sourcePageUrl);
    }

    internal FrozenFrame Frame { get; }

    internal RectD BitmapRegion { get; }

    /// <summary>True only for an explicitly completed manual-region drag.</summary>
    internal bool RecordForRepeat { get; }

    internal System.Windows.Media.Imaging.BitmapSource SelectedBitmap { get; }

    internal string SourceTitle { get; }

    internal string SourcePageUrl { get; }

    /// <summary>Application and folder of the window the title came from; empty when unknown.</summary>
    internal CaptureSourceDetails SourceDetails
    {
        get => _sourceDetails;
        init => _sourceDetails = value is null
            ? CaptureSourceDetails.Empty
            : CaptureSourceDetails.Create(value.AppName, value.AppPath, value.DocumentPath);
    }

    private readonly CaptureSourceDetails _sourceDetails = CaptureSourceDetails.Empty;

    /// <summary>
    /// Page and application of the selected window while they were still being read when the
    /// selection completed; null when <see cref="SourcePageUrl"/> and <see cref="SourceDetails"/> are
    /// already final. It resolves within the readers' deadlines and never faults. The selection is
    /// saved without waiting for it, and the details are added to the saved capture when it resolves.
    /// </summary>
    internal Task<CaptureSelectionSource>? PendingSource { get; init; }

    /// <summary>
    /// The selection was made in text capture mode: recognise and copy its text instead of
    /// persisting the image and opening the editor.
    /// </summary>
    internal bool TextCapture { get; init; }
}

/// <summary>The window a selection captured: its title, page and application, each possibly empty.</summary>
internal sealed record CaptureSelectionSource(string Title, string PageUrl, CaptureSourceDetails Details)
{
    internal static CaptureSelectionSource Empty { get; } = new(string.Empty, string.Empty, CaptureSourceDetails.Empty);

    /// <summary>True when there is neither a page nor an application/folder to record.</summary>
    internal bool HasNoDetails => PageUrl.Length == 0 && Details.IsEmpty;
}
