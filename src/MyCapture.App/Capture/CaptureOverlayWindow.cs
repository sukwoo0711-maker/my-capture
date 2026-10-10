using System.Windows;
using System.Windows.Interop;
using MyCapture.App.Themes;
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
        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnContentRendered;
        DpiChanged += OnDpiChanged;
        Deactivated += OnDeactivated;
        Activated += OnActivated;
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

        if (_pendingSelection is RectD pending)
        {
            RegionSelectionEventArgs? details = _pendingSelectionDetails;
            _pendingSelection = null;
            _pendingSelectionDetails = null;
            CompleteSelection(pending, details?.DetectedWindow, details?.TextCapture == true,
                details?.WholeMonitor == true);
        }
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

    private void OnActivated(object? sender, EventArgs e) => _view.InitializePointer();

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
        string sourceTitle = WindowCandidateService.ResolveSourceTitle(SourceWindows, selectedScreenRegion);
        string sourcePageUrl = WindowCandidateService.ResolveSourcePageUrl(SourceWindows, selectedScreenRegion);
        if (wholeMonitor)
        {
            // The full-screen button, like a desktop click, captures a monitor rather than a window.
            sourceTitle = string.Empty;
            sourcePageUrl = string.Empty;
        }
        else if (detectedWindow is not null)
        {
            // A clicked window is the source even when another window overlaps its centre.
            // Prefer the latest candidate snapshot, which may carry a page URL read after the freeze.
            WindowCandidate window = SourceWindows.FirstOrDefault(candidate => candidate.Handle == detectedWindow.Handle)
                ?? detectedWindow;
            if (WindowCandidateService.IsDesktopShell(window, SourceWindows))
            {
                // A desktop click captures one whole monitor. Neither the shell's caption nor a
                // window that happens to cover the monitor centre is that capture's source.
                sourceTitle = string.Empty;
                sourcePageUrl = string.Empty;
            }
            else if (!string.IsNullOrWhiteSpace(window.Title))
            {
                sourceTitle = window.Title;
                sourcePageUrl = MyCapture.Core.Capture.SourcePageUrl.Normalize(window.SourcePageUrl);
            }
        }

        // Remove the dimmed overlay before the normal editor window is created. The original
        // frozen frame and cropped physical pixels are retained; the desktop is never recaptured.
        Hide();
        try
        {
            SelectionCompleted?.Invoke(
                this,
                new CaptureSelectionCompletedEventArgs(frame, bitmapRegion, crop, sourceTitle,
                    sourcePageUrl: sourcePageUrl)
                {
                    TextCapture = textCapture,
                });
        }
        finally
        {
            Close();
        }
    }

    private void OnCancelRequested(object? sender, EventArgs e) => Cancel();

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
        Content = null;
        GeometrySelectionCompleted = null;
        SourceWindows = [];
        _view.SelectionConfirmed -= OnSelectionConfirmed;
        _view.CancelRequested -= OnCancelRequested;
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

    /// <summary>
    /// The selection was made in text capture mode: recognise and copy its text instead of
    /// persisting the image and opening the editor.
    /// </summary>
    internal bool TextCapture { get; init; }
}
