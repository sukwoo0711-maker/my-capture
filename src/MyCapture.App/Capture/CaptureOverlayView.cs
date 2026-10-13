using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.App.Recording;
using MyCapture.App.Themes;

namespace MyCapture.App.Capture;

/// <summary>
/// Full-monitor frozen-frame selector for one free-form rectangular drag.
/// </summary>
/// <remarks>
/// A valid drag is committed as soon as the mouse button is released. Keyboard users retain
/// Ctrl+A + Enter as a full-monitor alternative and Esc always cancels. When the owner enables
/// <see cref="DetectWindows"/>, hovering highlights the frontmost top-level window and a click
/// (no drag) selects it; over empty desktop that is the whole monitor under the pointer. The
/// highlight carries an accent tab naming what a click captures, and the top status lists drag
/// and click side by side with the one that applies right now filled in. There is still no
/// snapping or Tab target selection. The owner can also enable C to copy the frozen pixel's
/// colour and T to switch to text capture.
/// <para>
/// With <see cref="ShowModeBar"/> the screenshot selector gets a Snipping Tool style mode bar at
/// the top of the pointer's monitor (region or window, full screen, text, colour, help, cancel) and
/// <see cref="ShowStartGuide"/> a short guide in the middle of that monitor. Presses on the bar
/// never start a capture. Every cue lives only in this overlay: the capture is cropped from the
/// frozen frame, never from what the overlay draws.
/// </para>
/// </remarks>
internal sealed class CaptureOverlayView : FrameworkElement
{
    private const double MinimumSelectionPixels = 2;
    private const int MagnifierSourcePixels = 15;
    private const double MagnifierDestinationPixels = 150;

    internal static string InstructionText => UiText.Get("Text_798E080A9262");

    private FrozenFrame? _frame;
    private readonly RectD _screenBounds;
    private readonly bool _showMagnifier;
    private readonly bool _showSelectionGrid;

    /// <summary>
    /// Optional fixed selection size (physical pixels). When set, dragging only positions
    /// the rectangle: the drag start seeds a preset-sized box clamped to the frame, so
    /// repeated captures keep identical dimensions.
    /// </summary>
    internal System.Drawing.Size? PresetSize { get; set; }
    private Brush _dimmerBrush = null!;
    private Brush _chromeBrush = null!;
    private Brush _primaryTextBrush = null!;
    private Brush _mutedTextBrush = null!;
    private Brush _accentBrush = null!;
    private Brush _accentTextBrush = null!;
    private IReadOnlyDictionary<string, Brush> _guidePalette;
    internal IReadOnlyDictionary<string, Brush> GuidePalette => _guidePalette;
    internal int GuidePenBuildCount { get; private set; }
    internal int GuideGeometryBuildCount { get; private set; }
    private DpiScale _guideDpi;
    internal double GuidePixelSizeX => 1 / _guideDpi.DpiScaleX;
    internal double GuidePixelSizeY => 1 / _guideDpi.DpiScaleY;
    private readonly Typeface _uiTypeface;
    private readonly Typeface _monoTypeface;

    private RectD? _selection;
    private PointD _cursorPixel;
    private PointD _dragAnchor;
    private InteractionMode _interaction;
    private WriteableBitmap? _magnifierCrop;
    private int _magnifierCropX = -1;
    private int _magnifierCropY = -1;
    private string _sampleLabel = "#000000";
    private bool _ended;
    private readonly CapturePrecisionPointer _precisionPointer = new();
    private readonly byte[] _samplePixels = new byte[MagnifierSourcePixels * MagnifierSourcePixels * 4];
    private PointD? _lastSample;
    private bool _pointerInitialized;
    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _desktopVisual = new();
    private readonly DrawingVisual _dimmerVisual = new();
    private readonly DrawingVisual _revealVisual = new();
    private readonly RectangleGeometry _revealClip = new(new Rect(0, 0, 0, 0));
    private readonly DrawingVisual _horizontalGuideVisual = new();
    private readonly DrawingVisual _verticalGuideVisual = new();
    private readonly RectangleGeometry _horizontalGuideClip = new();
    private readonly RectangleGeometry _verticalGuideClip = new();
    private readonly DrawingVisual _selectionVisual = new();
    private readonly DrawingVisual _instructionVisual = new();
    private readonly DrawingVisual _anchorVisual = new();
    private readonly DrawingVisual _pointerVisual = new();
    private readonly DrawingVisual _magnifierVisual = new();
    private readonly DrawingVisual _toastVisual = new();
    private readonly CompositionFrameScheduler _feedbackScheduler;
    private IReadOnlyList<WindowCandidate> _windowCandidates = [];
    private WindowCandidate? _hoverWindow;
    private RectD? _hoverRegion;
    private bool _hoverWholeMonitor;
    private RectD? _drawnHover;
    private bool _drawnHoverWholeMonitor;
    private IReadOnlyList<MonitorInfo>? _monitors;
    private WindowCandidate? _selectedWindow;
    private bool _dragBeyondClick;
    private bool _textCaptureMode;
    private bool _colorPickMode;
    private bool _detectWindows;
    private bool _textCaptureAvailable;
    private bool _showShortcutHints = true;
    private bool _offersOtherActions;
    private bool _recordingSelection;
    private Brush _recordingBrush = RecordingSelectionCue.Resolve();
    private Pen _recordingPen = null!;
    private Pen _recordingEdgePen = null!;
    private Func<string, Task<bool>>? _copyColorText;
    private CaptureSelectorMode _drawnMode;
    private CaptureSelectorMode _drawnSelectionMode;
    private bool _drawnCueMonitor;
    private CaptureModeBar? _modeBar;
    private CaptureStartGuideCard? _startGuide;
    private Rect? _modeBarBounds;
    private Rect? _drawnModeBarBounds;
    private CaptureGuideArea? _guideArea;
    private double _guideScale = 1;
    private bool _pointerOverBar;
    private bool _startGuideShown;
    private PointD? _startGuideOrigin;
    private DispatcherTimer? _startGuideTimer;
    private bool _drawnMagnifierOn;
    private string? _toastMessage;
    private string? _drawnToast;
    private PointD? _drawnToastPointer;
    private DispatcherTimer? _toastTimer;
    private Pen _accentChipPen = null!;
    private Pen _detectedBorderPen = null!;
    private Pen _keylinePen = null!;
    private List<Rect> _chromeBounds = [];
    private CaptureCueState _drawnCueState;
    private Rect? _targetTab;
    private CaptureCueTabPlacement _targetTabPlacement;
    private string? _targetLabel;
    private Rect? _activeCueSegment;
    private bool _staticDirty = true;
    private Size _staticSize = Size.Empty;
    private bool _released;
    private RectD? _drawnSelection;
    private PointD? _drawnAnchor;
    private bool _drawnPrecision;
    private PointD? _drawnPointer;
    private PointD? _drawnMagnifierPointer;
    private bool _drawnMagnifierMouseOver;
    private bool _guideGeometryReady;
    private bool _guideStripsArePixels;
    private bool _guidesVisible;
    private FormattedText? _instructionText;
    private FormattedText? _anchorText;
    private FormattedText? _precisionText;
    private Pen _pointerOuterPen = null!;
    private Pen _pointerInnerPen = null!;
    private Pen _anchorPen = null!;
    private Pen _guideShadowPen = null!;
    private Pen _guideLinePen = null!;
    private Pen _selectionBorderPen = null!;
    private Pen _innerGlowPen = null!;
    private Pen _chipBorderPen = null!;
    private Pen _magnifierBorderPen = null!;
    private Pen _magnifierGridPen = null!;
    internal int StaticRenderCount { get; private set; }
    internal int DimmerRenderCount { get; private set; }
    internal int FeedbackRenderCount { get; private set; }
    internal int MagnifierUpdateCount { get; private set; }
    internal bool HasPendingFeedback => _feedbackScheduler.IsPending;
    internal BitmapSource? MagnifierBitmapForTest => _magnifierCrop;
    internal string SampleLabelForTest => _sampleLabel;
    internal void FlushFeedbackForTest() => _feedbackScheduler.FlushForTest();
    internal RectD? HoverRegionForTest => _hoverRegion;
    internal bool HoverWholeMonitorForTest => _hoverWholeMonitor;
    internal string? ToastMessageForTest => _toastMessage;
    internal IReadOnlyList<Rect> ChromeBoundsForTest => _chromeBounds;
    internal Rect? TargetTabForTest => _targetTab;
    internal CaptureCueTabPlacement TargetTabPlacementForTest => _targetTabPlacement;
    internal string? TargetLabelForTest => _targetLabel;
    internal Rect? ActiveCueSegmentForTest => _activeCueSegment;
    internal IReadOnlyList<string> StatusTextsForTest { get; private set; } = [];
    internal CaptureModeBar? ModeBarForTest => _modeBar;
    internal CaptureStartGuideCard? StartGuideCardForTest => _startGuide;
    internal Rect? ModeBarBoundsForTest => _modeBarBounds;
    internal bool PointerOverModeBarForTest => _pointerOverBar;
    internal double GuideScaleForTest => _guideScale;

    /// <summary>
    /// How long the in-overlay copy message stays. The chip follows the pointer the user is aiming
    /// with, so it uses the over-content confirmation time. A failed copy is a warning rather than a
    /// message that stays: a click there starts a selection and Esc cancels the capture, so the
    /// chip could not offer its own close, and pressing the copy key again is the retry.
    /// </summary>
    internal static TimeSpan ToastDuration(bool copied) =>
        FeedbackTiming.DisplayTime(copied ? FeedbackKind.Confirmation : FeedbackKind.Warning, overContent: true)!.Value;

    internal TimeSpan? ToastIntervalForTest => _toastTimer?.IsEnabled == true ? _toastTimer.Interval : null;

    /// <summary>How long the start guide stays when the pointer does not move away.</summary>
    internal static readonly TimeSpan StartGuideDuration = TimeSpan.FromSeconds(8);

    /// <summary>Pointer travel, in DIPs, after which the start guide gets out of the way.</summary>
    internal const double StartGuideMoveDismissDip = 96;

    /// <summary>
    /// Highlights the frontmost top-level window under the pointer and lets a click select it.
    /// Off by default so recording selection and diagnostics keep the plain region selector.
    /// </summary>
    internal bool DetectWindows
    {
        get => _detectWindows;
        set
        {
            _detectWindows = value;
            ConfigureGuides();
        }
    }

    /// <summary>
    /// Shows the mode bar and the status under it at the top of the pointer's monitor. The screenshot
    /// selector turns it on; recording selection keeps the plain instruction.
    /// </summary>
    internal bool ShowModeBar
    {
        get => _modeBar is not null;
        set
        {
            if (value == ShowModeBar || _released) return;
            if (value)
            {
                var bar = new CaptureModeBar();
                TextElement.SetFontFamily(bar, _uiTypeface.FontFamily);
                bar.ApplyPalette(_guidePalette);
                bar.ModeRequested += OnModeRequested;
                bar.FullScreenRequested += OnFullScreenRequested;
                bar.HelpRequested += OnHelpRequested;
                bar.CancelRequested += RequestCancel;
                bar.ActionRequested += OnBarActionRequested;
                _modeBar = bar;
                AddGuideChild(bar);
            }
            else if (_modeBar is { } bar)
            {
                bar.ModeRequested -= OnModeRequested;
                bar.FullScreenRequested -= OnFullScreenRequested;
                bar.HelpRequested -= OnHelpRequested;
                bar.CancelRequested -= RequestCancel;
                bar.ActionRequested -= OnBarActionRequested;
                RemoveGuideChild(bar);
                _modeBar = null;
                _modeBarBounds = null;
                _pointerOverBar = false;
            }

            ConfigureGuides();
            _staticDirty = true;
            InvalidateMeasure();
            QueueFeedback();
        }
    }

    /// <summary>
    /// The short start guide in the middle of the bar's monitor. It fades when the pointer moves
    /// <see cref="StartGuideMoveDismissDip"/> away, on any press or key, or after
    /// <see cref="StartGuideDuration"/>; the bar's help button shows it again.
    /// </summary>
    internal bool ShowStartGuide
    {
        get => _startGuideShown;
        set
        {
            if (value) OpenStartGuide();
            else DismissStartGuide();
        }
    }

    /// <summary>What the next click or drag produces; the bar fills in the matching button.</summary>
    internal CaptureSelectorMode Mode =>
        _colorPickMode ? CaptureSelectorMode.Color : _textCaptureMode ? CaptureSelectorMode.Text : CaptureSelectorMode.Capture;

    /// <summary>Top-level windows in z-order (frontmost first), in virtual-desktop pixels.</summary>
    internal IReadOnlyList<WindowCandidate> WindowCandidates
    {
        get => _windowCandidates;
        set
        {
            _windowCandidates = value ?? [];
            if (_pointerInitialized && RefreshHover(_cursorPixel.Offset(_screenBounds.Left, _screenBounds.Top)))
                QueueFeedback();
        }
    }

    /// <summary>The overlay's own HWND, never offered as a detected window.</summary>
    internal IntPtr OwnWindowHandle { get; set; }

    /// <summary>
    /// Display layout in virtual-desktop physical pixels. Read once, the first time the pointer is
    /// over the desktop shell window, whose hover and click select that monitor's full bounds.
    /// </summary>
    internal Func<IReadOnlyList<MonitorInfo>> ReadMonitors { get; set; } = MonitorEnumerator.GetAll;

    /// <summary>Copies the sampled colour (C key and the colour mode). Null disables colour copy.</summary>
    internal Func<string, Task<bool>>? CopyColorText
    {
        get => _copyColorText;
        set
        {
            _copyColorText = value;
            if (value is null && _colorPickMode) SelectMode(CaptureSelectorMode.Capture);
            ConfigureGuides();
        }
    }

    internal Func<ColorFormat> ReadColorFormat { get; set; } = () => ColorFormat.Hex;

    /// <summary>Lets T and the text button switch between image capture and text capture.</summary>
    internal bool TextCaptureAvailable
    {
        get => _textCaptureAvailable;
        set
        {
            _textCaptureAvailable = value;
            ConfigureGuides();
        }
    }

    /// <summary>
    /// General.ShowShortcutHints, read per selector. The mode bar no longer draws key badges: its keys
    /// are in the tooltips and accelerator keys whatever this says, like the target tab and the names.
    /// </summary>
    internal bool ShowShortcutHints
    {
        get => _showShortcutHints;
        set
        {
            _showShortcutHints = value;
            ConfigureGuides();
        }
    }

    /// <summary>
    /// Offers the bar's capture/record switch and its ⋯ menu (delay, scrolling, previous region). The
    /// owner must run <see cref="ActionRequested"/>; off by default.
    /// </summary>
    internal bool OffersOtherActions
    {
        get => _offersOtherActions;
        set
        {
            _offersOtherActions = value;
            ConfigureGuides();
        }
    }

    /// <summary>Whether a previous region exists; the ⋯ menu's entry is dimmed without one.</summary>
    internal Func<bool> CanRepeatLastRegion { get; set; } = () => true;

    /// <summary>
    /// The selector closes and the owner runs one of its existing commands: the recording selector,
    /// a delayed or scrolling capture, or the previous region.
    /// </summary>
    internal event EventHandler<CaptureSelectorRequest>? ActionRequested;

    /// <summary>
    /// The recording region selector: its instruction says "record", starts with a record dot, and the
    /// monitor edge and the selection take the recording colour, so it never reads as a screenshot.
    /// </summary>
    internal bool RecordingSelection
    {
        get => _recordingSelection;
        set
        {
            if (_recordingSelection == value) return;
            _recordingSelection = value;
            _staticDirty = true;
            QueueFeedback();
        }
    }

    /// <summary>The recording colour: the theme's danger token, or the system highlight in high contrast.</summary>
    internal Brush RecordingBrush
    {
        get => _recordingBrush;
        set
        {
            _recordingBrush = value ?? throw new ArgumentNullException(nameof(value));
            _staticDirty = true;
            QueueFeedback();
        }
    }

    /// <summary>
    /// The status part that applies now: a click while a window or monitor is highlighted
    /// (including a press still inside the click threshold), a drag once a free region is being drawn.
    /// The colour mode has no click or drag part; its single status part is always lit.
    /// </summary>
    internal CaptureCueState CueState
    {
        get
        {
            if (_ended || _colorPickMode || (!DetectWindows && !ShowModeBar)) return CaptureCueState.None;
            if (_interaction == InteractionMode.Create && _selection.HasValue) return CaptureCueState.Drag;
            return _hoverRegion.HasValue && !_selection.HasValue ? CaptureCueState.Click : CaptureCueState.None;
        }
    }

    /// <summary>When true, the confirmed region is recognised as text instead of opening the editor.</summary>
    internal bool TextCaptureMode
    {
        get => _textCaptureMode;
        set
        {
            if (_textCaptureMode == value) return;
            _textCaptureMode = value;
            if (value) _colorPickMode = false;
            OnModeChanged();
        }
    }

    /// <summary>
    /// Switches what the next click or drag produces, from the mode bar or the keyboard. Text and
    /// colour are ignored unless their owners enabled them.
    /// </summary>
    internal void SelectMode(CaptureSelectorMode mode)
    {
        if (_ended) return;
        switch (mode)
        {
            case CaptureSelectorMode.Text when TextCaptureAvailable:
                TextCaptureMode = true;
                break;
            case CaptureSelectorMode.Color when CopyColorText is not null:
                if (_colorPickMode) break;
                _colorPickMode = true;
                _textCaptureMode = false;
                OnModeChanged();
                break;
            case CaptureSelectorMode.Capture:
                bool changed = _colorPickMode || _textCaptureMode;
                _colorPickMode = false;
                _textCaptureMode = false;
                if (changed) OnModeChanged();
                break;
        }

        _modeBar?.ShowMode(Mode);
    }

    private void OnModeChanged()
    {
        // The colour mode never highlights a window; leaving it shows the one under the pointer again.
        if (_pointerInitialized) RefreshHover(_cursorPixel.Offset(_screenBounds.Left, _screenBounds.Top));
        _modeBar?.ShowMode(Mode);
        QueueFeedback();
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override IEnumerator LogicalChildren =>
        new object?[] { _modeBar, _startGuide }.Where(child => child is not null).GetEnumerator();

    private void QueueFeedback()
    {
        if (!_released && IsLoaded) _feedbackScheduler.Request();
    }

    internal void ReleaseResources()
    {
        if (_released) return;
        _released = true;
        ThemeService.CaptureGuideChanged -= OnGuidePaletteChanged;
        _ended = true;
        EndPointerInteraction();
        _feedbackScheduler.Dispose();
        _toastTimer?.Stop();
        _toastTimer = null;
        _toastMessage = null;
        _windowCandidates = [];
        _hoverWindow = _selectedWindow = null;
        _hoverRegion = null;
        _hoverWholeMonitor = false;
        _monitors = null;
        _copyColorText = null;
        _startGuideTimer?.Stop();
        _startGuideTimer = null;
        _startGuideShown = false;
        _startGuide?.BeginAnimation(OpacityProperty, null);
        if (_modeBar is { } bar)
        {
            bar.ModeRequested -= OnModeRequested;
            bar.FullScreenRequested -= OnFullScreenRequested;
            bar.HelpRequested -= OnHelpRequested;
            bar.CancelRequested -= RequestCancel;
            bar.ActionRequested -= OnBarActionRequested;
        }
        foreach (FrameworkElement child in new FrameworkElement?[] { _modeBar, _startGuide }.OfType<FrameworkElement>())
            RemoveLogicalChild(child);
        _modeBar = null;
        _startGuide = null;
        _modeBarBounds = null;
        _frame = null;
        _magnifierCrop = null;
        _lastSample = null;
        foreach (DrawingVisual visual in _visuals.OfType<DrawingVisual>())
        {
            using DrawingContext dc = visual.RenderOpen();
            visual.Clip = null;
        }
        _visuals.Clear();
    }
    internal Func<PointD> ReadCursor { get; set; } = CursorLocator.GetPosition;
    internal Func<PointD, bool> PlaceCursor { get; set; } = CursorLocator.TrySetPosition;

    internal void InitializePointer()
    {
        if (_ended) return;
        _precisionPointer.Reset();
        UpdatePointer();
        // The guide's clock and travel start once the selector is really on screen.
        if (_startGuideShown && _startGuideOrigin is null)
        {
            _startGuideOrigin = _cursorPixel;
            RestartStartGuideTimer();
        }
    }

    internal void EndPointerInteraction()
    {
        _precisionPointer.Reset();
        if (ReferenceEquals(Mouse.Captured, this)) Mouse.Capture(null);
    }

    private PointD UpdatePointer()
    {
        bool active = IsVisible && Window.GetWindow(this)?.IsActive == true;
        return UpdatePointer(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), active);
    }

    internal PointD UpdatePointer(bool precision, bool active)
    {
        if (_ended) return ClampEdgePoint(_cursorPixel);
        bool wasPrecision = _precisionPointer.IsPrecision;
        PointD actual = ReadCursor();
        PointD target = _precisionPointer.Update(actual,
            active && precision, _screenBounds);
        if (target != actual && (!active || !PlaceCursor(target)))
        {
            _precisionPointer.Reset();
            target = actual;
        }
        PointD local = target.Offset(-_screenBounds.Left, -_screenBounds.Top);
        PointD cursor = ClampSamplePoint(local);
        bool changed = _staticDirty || !_pointerInitialized || _cursorPixel != cursor || _drawnPointer != cursor
            || wasPrecision != _precisionPointer.IsPrecision || _drawnPrecision != _precisionPointer.IsPrecision
            || _drawnMagnifierMouseOver != PointerInside;
        _cursorPixel = cursor;
        _pointerInitialized = true;
        bool overBar = IsOverModeBar(cursor);
        if (overBar != _pointerOverBar)
        {
            _pointerOverBar = overBar;
            changed = true;
        }
        // Heading somewhere means the guide has been read; it must not hide what the user aims at.
        if (_startGuideShown && _startGuideOrigin is { } origin
            && origin.DistanceTo(cursor) > StartGuideMoveDismissDip * Math.Max(_guideDpi.DpiScaleX, 0.01))
        {
            DismissStartGuide();
        }
        if (RefreshHover(target)) changed = true;
        if (changed) QueueFeedback();
        return ClampEdgePoint(local);
    }

    /// <summary>True when the pointer is over the mode bar, where no window is detected and no capture starts.</summary>
    private bool IsOverModeBar(PointD pixel) =>
        _modeBar is { Visibility: Visibility.Visible } && _modeBarBounds is { } bar && bar.Contains(ToDipPoint(pixel));

    /// <summary>
    /// Re-evaluates the highlighted window before a drag starts. A press keeps the window it
    /// started on until the pointer leaves the click threshold, so the hover is frozen while
    /// a selection or drag exists. Returns whether the highlight changed.
    /// </summary>
    private bool RefreshHover(PointD screenPoint)
    {
        WindowCandidate? window = null;
        RectD? region = null;
        bool wholeMonitor = false;
        if (DetectWindows && !_ended && !_colorPickMode && !_pointerOverBar
            && _interaction == InteractionMode.None && !_selection.HasValue)
        {
            window = WindowDetectHitTest.FindTopmost(_windowCandidates, screenPoint, OwnWindowHandle);
            region = window is null ? null : WindowDetectHitTest.ResolveRegion(window, _windowCandidates, screenPoint,
                _screenBounds, () => _monitors ??= ReadMonitors(), out wholeMonitor);
            if (region is null) window = null;
        }
        else if (_interaction == InteractionMode.Create && !_dragBeyondClick)
        {
            return false;
        }

        bool changed = _hoverRegion != region || !ReferenceEquals(_hoverWindow, window)
            || _hoverWholeMonitor != wholeMonitor;
        _hoverWindow = window;
        _hoverRegion = region;
        _hoverWholeMonitor = wholeMonitor;
        return changed;
    }

    private void ClearHover()
    {
        if (_hoverRegion is null && _hoverWindow is null) return;
        _hoverWindow = null;
        _hoverRegion = null;
        _hoverWholeMonitor = false;
        QueueFeedback();
    }

    private double ClickThresholdX => Math.Max(1, SystemParameters.MinimumHorizontalDragDistance * _guideDpi.DpiScaleX);

    private double ClickThresholdY => Math.Max(1, SystemParameters.MinimumVerticalDragDistance * _guideDpi.DpiScaleY);

    internal CaptureOverlayView(FrozenFrame frame, bool showMagnifier = true, bool showSelectionGrid = true)
        : this(frame.ScreenBounds, frame, showMagnifier, showSelectionGrid)
    {
    }

    internal CaptureOverlayView(RectD screenBounds, FrozenFrame? frame = null, bool showMagnifier = true,
        bool showSelectionGrid = true)
    {
        _screenBounds = screenBounds.ToPixelBounds();
        if (_screenBounds.IsEmpty)
        {
            throw new ArgumentException("A non-empty virtual-desktop rectangle is required.", nameof(screenBounds));
        }

        _frame = frame;
        _showMagnifier = showMagnifier;
        _showSelectionGrid = showSelectionGrid;
        _visuals = new VisualCollection(this)
        {
            _desktopVisual, _dimmerVisual, _revealVisual, _horizontalGuideVisual, _verticalGuideVisual, _selectionVisual,
            _instructionVisual, _anchorVisual, _pointerVisual, _magnifierVisual, _toastVisual,
        };
        _feedbackScheduler = new CompositionFrameScheduler(Dispatcher, RenderLayers);
        _revealVisual.Clip = _revealClip;
        _horizontalGuideVisual.Clip = _horizontalGuideClip;
        _verticalGuideVisual.Clip = _verticalGuideClip;
        _horizontalGuideVisual.Opacity = _verticalGuideVisual.Opacity = 0;
        Loaded += (_, _) =>
        {
            if (_released) return;
            ThemeService.CaptureGuideChanged -= OnGuidePaletteChanged;
            ThemeService.CaptureGuideChanged += OnGuidePaletteChanged;
            // The constructor already resolved the palette. Rebuilding the static layers anyway redrew
            // the whole virtual desktop once more just after the selector appeared, as the pointer
            // started to move; only a palette that changed since then needs it.
            if (_guidePalette is not { } constructed || !SamePalette(CaptureGuidePalette.Resolve(), constructed)
                || !SameColor(RecordingSelectionCue.Resolve(), _recordingBrush))
                OnGuidePaletteChanged();
            // Feedback asked for before Loaded (the pointer read on activation) is drawn on the next frame.
            else QueueFeedback();
        };
        Unloaded += (_, _) =>
        {
            ThemeService.CaptureGuideChanged -= OnGuidePaletteChanged;
            _feedbackScheduler.CancelPending();
        };

        Focusable = true;
        // The system focus rectangle would dash-outline the whole virtual desktop. It shows after the
        // keyboard shortcut that opens the selector, and its bounds meet every dirty region, so WPF
        // re-rasterised thousands of dashes on each pointer frame. The selection, the highlighted
        // window and the mode bar's own focus cues already show where the keys act.
        FocusVisualStyle = null;
        // The selector never accepts text; keep single-letter commands out of the IME.
        InputMethod.SetIsInputMethodEnabled(this, false);
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);

        _guideDpi = VisualTreeHelper.GetDpi(this);
        _guidePalette = CaptureGuidePalette.Resolve();
        ApplyGuideBrushes();

        FontFamily uiFont = Application.Current?.TryFindResource("Font.Ui") as FontFamily
            ?? new FontFamily("Segoe UI");
        FontFamily monoFont = Application.Current?.TryFindResource("Font.Mono") as FontFamily
            ?? new FontFamily("Consolas");
        _uiTypeface = new Typeface(uiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        _monoTypeface = new Typeface(monoFont, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    }

    internal FrozenFrame? Frame => _frame;

    internal event EventHandler<RegionSelectionEventArgs>? SelectionConfirmed;

    internal event EventHandler? CancelRequested;

    internal RectD? Selection => _selection;

    /// <summary>Normalizes, clips and validates one completed physical-pixel drag.</summary>
    internal static RectD? ResolveCompletedDrag(PointD start, PointD end, RectD frameBounds)
    {
        RectD pixels = RectD.FromCorners(start, end)
            .ClampTo(frameBounds)
            .ToPixelBounds();

        return pixels.Width >= MinimumSelectionPixels && pixels.Height >= MinimumSelectionPixels
            ? pixels
            : null;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        // Hit testing stays on this element. Pixel content lives in retained child visuals;
        // moving the pointer never reopens the full-desktop drawing command list.
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        _feedbackScheduler.CancelPending();
        RenderLayers();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        // The first arrange renders the layers at the new size before this notification arrives;
        // marking them dirty again then redrew the whole desktop on the first pointer frame.
        if (sizeInfo.NewSize == _staticSize && !_staticDirty) return;
        _staticDirty = true;
        QueueFeedback();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _guideDpi = newDpi;
        _staticDirty = true;
        InvalidateMeasure();
        QueueFeedback();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Size desired = base.MeasureOverride(availableSize);
        if (_modeBar is null && _startGuide is null) return desired;
        double dipPerPixel = availableSize.Width > 0 && !double.IsInfinity(availableSize.Width)
            ? availableSize.Width / PixelWidth : GuidePixelSizeX;
        ApplyGuideScale(CaptureModeBarLayout.ContentScale(GuideArea.Scale, dipPerPixel));
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _modeBar?.Measure(unbounded);
        _startGuide?.Measure(unbounded);
        return desired;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size arranged = base.ArrangeOverride(finalSize);
        if (_modeBar is null && _startGuide is null) return arranged;
        Rect work = CaptureModeBarLayout.ToDip(GuideArea.WorkArea, finalSize.Width / PixelWidth, finalSize.Height / PixelHeight);
        Rect? bar = null;
        if (_modeBar is not null)
        {
            Rect placed = CaptureModeBarLayout.PlaceBar(work, _modeBar.DesiredSize, finalSize, _guideScale,
                GuidePixelSizeX, GuidePixelSizeY);
            _modeBar.Arrange(placed);
            bar = placed;
        }

        _startGuide?.Arrange(CaptureModeBarLayout.PlaceCard(work, _startGuide.DesiredSize, finalSize,
            GuidePixelSizeX, GuidePixelSizeY, bar, _guideScale));
        if (_modeBarBounds != bar)
        {
            _modeBarBounds = bar;
            QueueFeedback();
        }

        return arranged;
    }

    /// <summary>The monitor the bar and the guide sit on, chosen once from the pointer at first layout.</summary>
    private CaptureGuideArea GuideArea =>
        _guideArea ??= CaptureModeBarLayout.ChooseArea(_monitors ??= ReadMonitors(), ReadCursor(), _screenBounds);

    private void ApplyGuideScale(double scale)
    {
        if (Math.Abs(scale - _guideScale) < 0.0001) return;
        _guideScale = scale;
        Transform transform = Math.Abs(scale - 1) < 0.0001 ? Transform.Identity : new ScaleTransform(scale, scale);
        if (_modeBar is not null) _modeBar.LayoutTransform = transform;
        if (_startGuide is not null) _startGuide.LayoutTransform = transform;
    }

    /// <summary>Above the pointer feedback, below the magnifier and the copy confirmation.</summary>
    private void AddGuideChild(FrameworkElement child)
    {
        _visuals.Insert(_visuals.IndexOf(_magnifierVisual), child);
        AddLogicalChild(child);
    }

    private void RemoveGuideChild(FrameworkElement child)
    {
        RemoveLogicalChild(child);
        _visuals.Remove(child);
    }

    private void ConfigureGuides()
    {
        if (_released) return;
        _modeBar?.Configure(DetectWindows, TextCaptureAvailable, CopyColorText is not null, OffersOtherActions,
            CanRepeatLastRegion());
        _modeBar?.ShowMode(Mode);
        _startGuide?.Configure(DetectWindows);
        _staticDirty = true;
        QueueFeedback();
    }

    private void OnModeRequested(CaptureSelectorMode mode)
    {
        DismissStartGuide();
        SelectMode(mode);
        ReturnFocusAfterPointer();
    }

    /// <summary>The record switch or a ⋯ command: the selector ends without a capture and the owner runs it.</summary>
    private void OnBarActionRequested(CaptureSelectorRequest request)
    {
        if (_ended || !OffersOtherActions) return;
        DismissStartGuide();
        _ended = true;
        EndPointerInteraction();
        ActionRequested?.Invoke(this, request);
    }

    private void OnFullScreenRequested()
    {
        DismissStartGuide();
        _ = CaptureFullScreen();
    }

    private void OnHelpRequested()
    {
        if (_startGuideShown) DismissStartGuide();
        else OpenStartGuide();
        ReturnFocusAfterPointer();
    }

    /// <summary>A mouse click on the bar hands the keys back to the selector; Tab focus stays on the bar.</summary>
    private void ReturnFocusAfterPointer()
    {
        if (!_ended && InputManager.Current.MostRecentInputDevice is MouseDevice) Focus();
    }

    /// <summary>
    /// The full-screen button: confirms the whole monitor the bar is on, the same region a click on
    /// that monitor's desktop selects. It is cropped from the frozen frame like every other selection,
    /// in the current image or text mode.
    /// </summary>
    internal bool CaptureFullScreen()
    {
        if (_ended) return false;
        RectD monitor = GuideArea.Bounds.ClampTo(FrameBounds).ToPixelBounds();
        if (monitor.Width < MinimumSelectionPixels || monitor.Height < MinimumSelectionPixels) return false;
        _selection = monitor;
        _selectedWindow = null;
        ClearHover();
        ConfirmSelection(wholeMonitor: true);
        return true;
    }

    private void OpenStartGuide()
    {
        if (_released || _ended) return;
        if (_startGuide is null)
        {
            var card = new CaptureStartGuideCard();
            TextElement.SetFontFamily(card, _uiTypeface.FontFamily);
            card.ApplyPalette(_guidePalette);
            if (Math.Abs(_guideScale - 1) >= 0.0001) card.LayoutTransform = new ScaleTransform(_guideScale, _guideScale);
            _startGuide = card;
            AddGuideChild(card);
        }

        _startGuide.Configure(DetectWindows);
        _startGuide.BeginAnimation(OpacityProperty, null);
        _startGuide.Opacity = 1;
        _startGuide.Visibility = Visibility.Visible;
        _startGuideShown = true;
        _startGuideOrigin = _pointerInitialized ? _cursorPixel : null;
        if (_startGuideOrigin.HasValue) RestartStartGuideTimer();
        InvalidateMeasure();
    }

    /// <summary>Fades the start guide out, or hides it at once when Windows animations are off.</summary>
    internal void DismissStartGuide()
    {
        if (!_startGuideShown) return;
        _startGuideShown = false;
        _startGuideOrigin = null;
        _startGuideTimer?.Stop();
        if (_startGuide is not { } card) return;
        if (!_released && IsLoaded && FluidMotion.AnimationsEnabled)
        {
            var fade = new DoubleAnimation(0, FluidMotion.NormalDuration) { EasingFunction = FluidMotion.StandardEasing };
            fade.Completed += (_, _) =>
            {
                if (!_startGuideShown && ReferenceEquals(_startGuide, card)) card.Visibility = Visibility.Collapsed;
            };
            card.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            card.BeginAnimation(OpacityProperty, null);
            card.Visibility = Visibility.Collapsed;
        }
    }

    private void RestartStartGuideTimer()
    {
        if (_released) return;
        _startGuideTimer ??= CreateStartGuideTimer();
        _startGuideTimer.Stop();
        _startGuideTimer.Start();
    }

    private DispatcherTimer CreateStartGuideTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = StartGuideDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            DismissStartGuide();
        };
        return timer;
    }

    internal bool StartGuideTimerRunningForTest => _startGuideTimer?.IsEnabled == true;

    private void RenderLayers()
    {
        if (_released || ActualWidth <= 0 || ActualHeight <= 0) return;
        FeedbackRenderCount++;
        bool rebuild = _staticDirty;
        if (rebuild)
        {
            _staticDirty = false;
            _staticSize = RenderSize;
            StaticRenderCount++;
            BuildGuidePens();
            _instructionText = _anchorText = _precisionText = null;
            using (DrawingContext dc = _desktopVisual.RenderOpen())
                if (_frame?.Bitmap is { } bitmap) dc.DrawImage(bitmap, new Rect(RenderSize));
            using (DrawingContext dc = _dimmerVisual.RenderOpen())
                dc.DrawRectangle(_dimmerBrush, null, new Rect(RenderSize));
            DimmerRenderCount++;
            // Both image visuals share exactly one source; there is no rasterized cache.
            using (DrawingContext dc = _revealVisual.RenderOpen())
                if (_frame?.Bitmap is { } revealBitmap) dc.DrawImage(revealBitmap, new Rect(RenderSize));
        }
        // Instructions first: the detected-target tab is placed clear of the chips they occupy.
        CaptureCueState cue = CueState;
        CaptureSelectorMode mode = Mode;
        RectD? hover = _selection.HasValue ? null : _hoverRegion;
        bool hoverWholeMonitor = hover.HasValue && _hoverWholeMonitor;
        bool chromeMoved = false;
        if (rebuild || _drawnPrecision != _precisionPointer.IsPrecision || _drawnMode != mode
            || _drawnCueState != cue || _drawnCueMonitor != hoverWholeMonitor || _drawnModeBarBounds != _modeBarBounds)
        {
            _drawnPrecision = _precisionPointer.IsPrecision;
            _drawnMode = mode;
            _drawnCueState = cue;
            _drawnCueMonitor = hoverWholeMonitor;
            _drawnModeBarBounds = _modeBarBounds;
            var chrome = new List<Rect>(4);
            using (DrawingContext dc = _instructionVisual.RenderOpen())
                DrawInstructions(dc, cue, chrome);
            chromeMoved = !chrome.SequenceEqual(_chromeBounds);
            _chromeBounds = chrome;
        }
        if (rebuild || chromeMoved || _drawnSelection != _selection || _drawnHover != hover
            || _drawnHoverWholeMonitor != hoverWholeMonitor || _drawnSelectionMode != mode)
        {
            _drawnSelection = _selection;
            _drawnHover = hover;
            _drawnHoverWholeMonitor = hoverWholeMonitor;
            _drawnSelectionMode = mode;
            _targetTab = null;
            _targetLabel = null;
            RectD? revealed = _selection ?? hover;
            UpdateRevealedRegion(revealed);
            using DrawingContext dc = _selectionVisual.RenderOpen();
            if (_selection is { } selection) DrawSelection(dc, selection);
            else if (hover is { } window) DrawDetectedWindow(dc, window, hoverWholeMonitor);
        }
        PointD? anchor = _interaction == InteractionMode.Create && _pointerInitialized && !_ended ? _dragAnchor : null;
        if (rebuild || _drawnAnchor != anchor)
        {
            _drawnAnchor = anchor;
            using DrawingContext dc = _anchorVisual.RenderOpen();
            if (anchor.HasValue) DrawPointerFeedback(dc, anchor: true);
        }
        UpdateStartingGuides(rebuild);
        // Over the mode bar the pointer is an ordinary arrow: no crosshair, guides or magnifier.
        PointD? pointer = _pointerInitialized && !_ended && !_pointerOverBar ? _cursorPixel : null;
        if (rebuild || _drawnPointer != pointer)
        {
            _drawnPointer = pointer;
            using DrawingContext dc = _pointerVisual.RenderOpen();
            if (pointer.HasValue) DrawPointerFeedback(dc, anchor: false);
        }
        bool mouseOver = PointerInside;
        bool magnifierOn = ShowsMagnifier;
        if (_drawnMagnifierOn && !magnifierOn)
        {
            // Leaving the colour mode removes the magnifier it showed with the setting off.
            using DrawingContext dc = _magnifierVisual.RenderOpen();
        }
        if (rebuild || _drawnMagnifierPointer != pointer || _drawnMagnifierMouseOver != mouseOver
            || _drawnMagnifierOn != magnifierOn)
        {
            _drawnMagnifierPointer = pointer;
            _drawnMagnifierMouseOver = mouseOver;
            _drawnMagnifierOn = magnifierOn;
            if (magnifierOn)
            {
                if (pointer.HasValue) UpdateMagnifier();
                using DrawingContext dc = _magnifierVisual.RenderOpen();
                if (pointer.HasValue) DrawMagnifier(dc);
            }
        }
        if (rebuild || _drawnToast != _toastMessage || (_toastMessage is not null && _drawnToastPointer != pointer))
        {
            _drawnToast = _toastMessage;
            _drawnToastPointer = pointer;
            using DrawingContext dc = _toastVisual.RenderOpen();
            if (_toastMessage is { } message && !_ended) DrawToast(dc, message);
        }
    }

    /// <summary>
    /// Uncovers the selected or highlighted region: the full-screen dimmer never changes after the
    /// static build, and only the clip of the desktop drawn over it moves.
    /// </summary>
    /// <remarks>
    /// WPF redraws every visual that meets a changed visual's old or new bounds. A dimmer redrawn
    /// around each new selection changed a viewport-sized visual, so every drag frame and every new
    /// highlighted window re-rendered the whole desktop, including the mode bar and the instruction
    /// chips. Moving only this clip limits the redraw to the old and new region. Inside the clip
    /// the opaque desktop covers the dimmer, so the pixels match the dimmer drawn outside the
    /// region; fractional physical edges keep the clip's own antialiasing as they always had.
    /// </remarks>
    private void UpdateRevealedRegion(RectD? revealed)
    {
        Rect visible = revealed is { } selected
            ? ToDipRect(selected.ClampTo(FrameBounds)) : new Rect(0, 0, 0, 0);
        if (_revealClip.Rect != visible) _revealClip.Rect = visible;
    }

    /// <summary>Short confirmation chip above and to the right of the pointer, clear of the magnifier.</summary>
    private void DrawToast(DrawingContext dc, string message)
    {
        FormattedText text = CreateText(message, _uiTypeface, 12, _primaryTextBrush);
        const double paddingX = 10;
        const double paddingY = 6;
        double width = text.Width + (paddingX * 2);
        double height = text.Height + (paddingY * 2);
        Point cursor = ToDipPoint(_cursorPixel);
        double x = Math.Clamp(cursor.X + 16, 8, Math.Max(8, ActualWidth - width - 8));
        double y = cursor.Y - height - 14;
        if (y < 8) y = Math.Min(Math.Max(8, ActualHeight - height - 8), cursor.Y + 18);
        var background = new Rect(x, y, width, height);
        dc.DrawRoundedRectangle(_chromeBrush, _accentChipPen, background, 7, 7);
        dc.DrawText(text, new Point(x + paddingX, y + paddingY));
    }

    private void DrawPointerFeedback(DrawingContext dc, bool anchor)
    {
        if (!_pointerInitialized || _ended) return;
        void Target(PointD pixel)
        {
            Point p = ToDipPoint(pixel);
            void Cross(Pen pen)
            {
                dc.DrawLine(pen, new Point(p.X - 11, p.Y), new Point(p.X + 11, p.Y));
                dc.DrawLine(pen, new Point(p.X, p.Y - 11), new Point(p.X, p.Y + 11));
                if (anchor) dc.DrawEllipse(null, pen, p, 5, 5);
            }
            Cross(_pointerOuterPen);
            Cross(anchor ? _anchorPen : _pointerInnerPen);
            if (anchor)
            {
                FormattedText text = _anchorText ??= CreateText(UiText.Get("CaptureSelectionStart"), _uiTypeface, 11, _primaryTextBrush);
                double x = Math.Clamp(p.X + 15, 4, Math.Max(4, ActualWidth - text.Width - 12));
                double y = Math.Clamp(p.Y + 12, 4, Math.Max(4, ActualHeight - text.Height - 8));
                dc.DrawRoundedRectangle(_chromeBrush, _chipBorderPen, new Rect(x - 4, y - 2, text.Width + 8, text.Height + 4), 4, 4);
                dc.DrawText(text, new Point(x, y));
            }
        }
        Target(anchor ? _dragAnchor : _cursorPixel);
    }

    private void DrawSelection(DrawingContext dc, RectD pixelRect)
    {
        Rect rect = SnapToPhysicalPixels(pixelRect);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        double px = GuidePixelSizeX, py = GuidePixelSizeY;
        // Clip the low-opacity accent to the inside; the crisp border occupies one
        // physical pixel inside the selected pixel bounds at every display scale.
        dc.PushClip(new RectangleGeometry(rect));
        dc.DrawRectangle(null, _innerGlowPen, rect);
        dc.Pop();
        if (RecordingSelection)
        {
            // Two physical pixels of the recording colour, inside the selected bounds.
            double inset = _recordingPen.Thickness / 2;
            dc.DrawRectangle(null, _recordingPen, Deflate(rect, inset));
        }
        else
        {
            var border = new Rect(rect.X + px / 2, rect.Y + py / 2,
                Math.Max(0, rect.Width - px), Math.Max(0, rect.Height - py));
            dc.DrawRectangle(null, _selectionBorderPen, border);
        }
        DrawDimensionLabel(dc, pixelRect.ToPixelBounds(), rect);
    }

    /// <summary>
    /// The window (or whole monitor) a click captures: a three-physical-pixel accent band with a
    /// one-pixel contrast keyline inside it, both within the captured bounds, and an accent tab on
    /// the border saying what a click does and the size it captures. A monitor keeps its tab inside
    /// that monitor.
    /// </summary>
    private void DrawDetectedWindow(DrawingContext dc, RectD pixelRect, bool wholeMonitor)
    {
        Rect rect = SnapToPhysicalPixels(pixelRect);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        double band = _detectedBorderPen.Thickness;
        double keyline = _keylinePen.Thickness;
        dc.DrawRectangle(null, _detectedBorderPen, Deflate(rect, band / 2));
        // The keyline separates the band from window pixels that happen to match the accent.
        dc.DrawRectangle(null, _keylinePen, Deflate(rect, band + keyline / 2));

        RectD pixels = pixelRect.ToPixelBounds();
        // The same words as the status above and the mode bar: what a click does in this mode.
        string label = UiText.Get(ClickKey(wholeMonitor));
        string size = $"{(int)pixels.Width} × {(int)pixels.Height}";
        FormattedText labelText = CreateText(label, _uiTypeface, 12, _accentTextBrush);
        FormattedText sizeText = CreateText(size, _monoTypeface, 12, _accentTextBrush);
        const double paddingX = 9;
        const double paddingY = 4;
        const double gap = 10;
        var tabSize = new Size(labelText.Width + gap + sizeText.Width + (paddingX * 2),
            Math.Max(labelText.Height, sizeText.Height) + (paddingY * 2));
        (Rect tab, CaptureCueTabPlacement placement) = CaptureCueLayout.PlaceTab(rect, tabSize,
            new Size(ActualWidth, ActualHeight), _chromeBounds, insideOnly: wholeMonitor, GuidePixelSizeX, GuidePixelSizeY);
        // Square on the edge it is attached to, rounded on the free edge.
        bool roundTop = placement is CaptureCueTabPlacement.Above or CaptureCueTabPlacement.Floating;
        bool roundBottom = placement is not CaptureCueTabPlacement.Above;
        dc.DrawGeometry(_accentBrush, null, TabGeometry(tab, 6, roundTop, roundBottom));
        // Only a display narrower than the tab shortens it; the text is then clipped to the tab.
        dc.PushClip(new RectangleGeometry(tab));
        dc.DrawText(labelText, new Point(tab.Left + paddingX, tab.Top + ((tab.Height - labelText.Height) / 2)));
        if (tab.Width + 0.5 >= tabSize.Width)
            dc.DrawText(sizeText, new Point(tab.Right - paddingX - sizeText.Width, tab.Top + ((tab.Height - sizeText.Height) / 2)));
        dc.Pop();
        _targetTab = tab;
        _targetTabPlacement = placement;
        _targetLabel = label + "  " + size;
    }

    private Rect SnapToPhysicalPixels(RectD pixelRect)
    {
        Rect mapped = ToDipRect(pixelRect.ToPixelBounds());
        double px = GuidePixelSizeX, py = GuidePixelSizeY;
        double left = Math.Round(mapped.Left / px) * px, top = Math.Round(mapped.Top / py) * py;
        double right = Math.Round(mapped.Right / px) * px, bottom = Math.Round(mapped.Bottom / py) * py;
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static Rect Deflate(Rect rect, double inset) => new(rect.X + inset, rect.Y + inset,
        Math.Max(0, rect.Width - (inset * 2)), Math.Max(0, rect.Height - (inset * 2)));

    private static StreamGeometry TabGeometry(Rect r, double radius, bool roundTop, bool roundBottom)
    {
        double limit = Math.Min(r.Width, r.Height) / 2;
        double top = roundTop ? Math.Min(radius, limit) : 0;
        double bottom = roundBottom ? Math.Min(radius, limit) : 0;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(new Point(r.Left + top, r.Top), isFilled: true, isClosed: true);
            g.LineTo(new Point(r.Right - top, r.Top), false, false);
            if (top > 0) g.ArcTo(new Point(r.Right, r.Top + top), new Size(top, top), 0, false, SweepDirection.Clockwise, false, false);
            g.LineTo(new Point(r.Right, r.Bottom - bottom), false, false);
            if (bottom > 0) g.ArcTo(new Point(r.Right - bottom, r.Bottom), new Size(bottom, bottom), 0, false, SweepDirection.Clockwise, false, false);
            g.LineTo(new Point(r.Left + bottom, r.Bottom), false, false);
            if (bottom > 0) g.ArcTo(new Point(r.Left, r.Bottom - bottom), new Size(bottom, bottom), 0, false, SweepDirection.Clockwise, false, false);
            g.LineTo(new Point(r.Left, r.Top + top), false, false);
            if (top > 0) g.ArcTo(new Point(r.Left + top, r.Top), new Size(top, top), 0, false, SweepDirection.Clockwise, false, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private void UpdateStartingGuides(bool rebuild)
    {
        if (rebuild) _guideGeometryReady = false;
        bool visible = _showSelectionGrid && _pointerInitialized && !_ended && !_pointerOverBar
            && _interaction == InteractionMode.None && !_selection.HasValue;
        if (!visible)
        {
            if (_guidesVisible)
                _horizontalGuideVisual.Opacity = _verticalGuideVisual.Opacity = 0;
            _guidesVisible = false;
            return;
        }
        Point p = ToDipPoint(_cursorPixel);
        bool pixelStrips = GuideStripsOnDevicePixels;
        if (rebuild || !_guideGeometryReady || pixelStrips != _guideStripsArePixels)
        {
            GuideGeometryBuildCount++;
            // Retain two narrow strips rather than redrawing one viewport-sized visual.
            // Their order preserves the original horizontal-then-vertical intersection.
            using (DrawingContext dc = _horizontalGuideVisual.RenderOpen())
            {
                if (pixelStrips) DrawStrip(dc, horizontal: true);
                else DrawGuide(dc, new Point(0, 0), new Point(ActualWidth, 0));
            }
            using (DrawingContext dc = _verticalGuideVisual.RenderOpen())
            {
                if (pixelStrips) DrawStrip(dc, horizontal: false);
                else DrawGuide(dc, new Point(0, 0), new Point(0, ActualHeight));
            }
            _guideGeometryReady = true;
            _guideStripsArePixels = pixelStrips;
        }
        _horizontalGuideVisual.Offset = new Vector(0, p.Y);
        _verticalGuideVisual.Offset = new Vector(p.X, 0);
        double halfStroke = _guideShadowPen.Thickness / 2;
        // Leave one physical pixel around the stroke's antialias fringe. Clipping at
        // the stroke edge itself would multiply its partial coverage a second time.
        double horizontalExtent = halfStroke + DipPerPixelY;
        double verticalExtent = halfStroke + DipPerPixelX;
        double top = Math.Max(-horizontalExtent, -p.Y);
        double bottom = Math.Min(horizontalExtent, ActualHeight - p.Y);
        double left = Math.Max(-verticalExtent, -p.X);
        double right = Math.Min(verticalExtent, ActualWidth - p.X);
        Rect horizontal = new(0, top, ActualWidth, Math.Max(0, bottom - top));
        Rect vertical = new(left, 0, Math.Max(0, right - left), ActualHeight);
        if (_horizontalGuideClip.Rect != horizontal) _horizontalGuideClip.Rect = horizontal;
        if (_verticalGuideClip.Rect != vertical) _verticalGuideClip.Rect = vertical;
        if (!_guidesVisible)
            _horizontalGuideVisual.Opacity = _verticalGuideVisual.Opacity = 1;
        _guidesVisible = true;

        void DrawGuide(DrawingContext dc, Point from, Point to)
        {
            dc.DrawLine(_guideShadowPen, from, to);
            dc.DrawLine(_guideLinePen, from, to);
        }

        // The bitmap covers the pixel on each side of the cursor's pixel edge, so it lands on the
        // device pixels the strokes would paint. The visual's offset moves it with the pointer.
        void DrawStrip(DrawingContext dc, bool horizontal)
        {
            Brush shadow = _guidePalette["CaptureGuide.LineShadow"], line = _guidePalette["CaptureGuide.Line"];
            if (horizontal)
            {
                dc.DrawImage(CaptureGuideStrip.Get(shadow, line, PixelWidth, horizontal: true),
                    new Rect(0, -DipPerPixelY, ActualWidth, CaptureGuideStrip.Thickness * DipPerPixelY));
            }
            else
            {
                dc.DrawImage(CaptureGuideStrip.Get(shadow, line, PixelHeight, horizontal: false),
                    new Rect(-DipPerPixelX, 0, CaptureGuideStrip.Thickness * DipPerPixelX, ActualHeight));
            }
        }
    }

    /// <summary>
    /// Whether the guides fall on device-pixel edges: the pointer is on a whole pixel (always true for
    /// the real cursor) and one frame pixel is one device pixel. Only then do the cached strips match
    /// the strokes pixel for pixel; anything else draws the strokes themselves.
    /// </summary>
    private bool GuideStripsOnDevicePixels =>
        IsWhole(_cursorPixel.X) && IsWhole(_cursorPixel.Y)
        && Math.Abs((DipPerPixelX * _guideDpi.DpiScaleX) - 1) <= 0.0001
        && Math.Abs((DipPerPixelY * _guideDpi.DpiScaleY) - 1) <= 0.0001;

    private static bool IsWhole(double value) => Math.Abs(value - Math.Round(value)) <= 0.000001;

    internal bool GuideStripsArePixelsForTest => _guideStripsArePixels;

    private void DrawDimensionLabel(DrawingContext dc, RectD pixels, Rect selectionDip)
    {
        string text = $"{(int)pixels.Width} × {(int)pixels.Height}";
        FormattedText formatted = CreateText(text, _monoTypeface, 12, _primaryTextBrush);
        const double paddingX = 9;
        const double paddingY = 5;
        double width = formatted.Width + (paddingX * 2);
        double height = formatted.Height + (paddingY * 2);

        double x = Math.Clamp(selectionDip.Left, 8, Math.Max(8, ActualWidth - width - 8));
        double y = selectionDip.Top - height - 8;
        if (y < 8)
        {
            y = Math.Min(ActualHeight - height - 8, selectionDip.Bottom + 8);
        }

        var background = new Rect(x, y, width, height);
        dc.DrawRoundedRectangle(_chromeBrush, RecordingSelection ? _recordingPen : _chipBorderPen, background, 7, 7);
        dc.DrawText(formatted, new Point(x + paddingX, y + paddingY));
    }

    /// <summary>
    /// Draws the top instruction (none under the mode bar: the bar's buttons and the label beside the
    /// highlighted window already say what a click and a drag do) and the precision chip, and records
    /// them after the bar in <paramref name="chrome"/>.
    /// </summary>
    private void DrawInstructions(DrawingContext dc, CaptureCueState cue, List<Rect> chrome)
    {
        Rect? bar = _modeBar is not null ? _modeBarBounds : null;
        if (bar is { } barBounds) chrome.Add(barBounds);
        double scale = bar.HasValue ? _guideScale : 1;
        double centerX = bar is { } b ? b.Left + (b.Width / 2) : ActualWidth / 2;
        Rect? primary = null;
        if (ShowModeBar)
        {
            _activeCueSegment = null;
            StatusTextsForTest = [];
        }
        else
        {
            primary = RecordingSelection ? DrawRecordingInstruction(dc)
                : DetectWindows ? DrawCueInstruction(dc, cue, centerX, 18, scale)
                : DrawPlainInstruction(dc);
            chrome.Add(primary.Value);
        }

        if (RecordingSelection)
        {
            // The monitor edge in the recording colour: this selector records, it does not capture.
            double edge = _recordingEdgePen.Thickness;
            dc.DrawRectangle(null, _recordingEdgePen, new Rect(edge / 2, edge / 2,
                Math.Max(0, ActualWidth - edge), Math.Max(0, ActualHeight - edge)));
        }

        if (_precisionPointer.IsPrecision)
        {
            FormattedText precision = _precisionText ??= CreateText(UiText.Get("CapturePrecisionActive"), _uiTypeface, 12, _primaryTextBrush);
            var size = new Size(precision.Width + 20, precision.Height + 12);
            // Under the bar when it is shown; at the old top-left spot otherwise.
            Rect chip = bar is { } under
                ? new Rect(centerX - (size.Width * scale / 2), under.Bottom + (8 * scale), size.Width * scale, size.Height * scale)
                : new Rect(new Point(16, 64), size);
            dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, chip.Left, chip.Top));
            dc.DrawRoundedRectangle(_chromeBrush, _chipBorderPen, new Rect(size), 6, 6);
            dc.DrawText(precision, new Point(10, 6));
            dc.Pop();
            chrome.Add(chip);
        }
    }

    /// <summary>The drag-only sentence used when window detection and the mode bar are off.</summary>
    private Rect DrawPlainInstruction(DrawingContext dc)
    {
        _activeCueSegment = null;
        StatusTextsForTest = [InstructionText];
        FormattedText text = _instructionText ??= CreateText(InstructionText, _uiTypeface, 13, _primaryTextBrush);
        const double paddingX = 14;
        const double paddingY = 9;
        double width = text.Width + (paddingX * 2);
        double left = Math.Max(16, (ActualWidth - width) / 2);
        var background = new Rect(left, 18, width, text.Height + (paddingY * 2));
        dc.DrawRoundedRectangle(_chromeBrush, _chipBorderPen, background, 10, 10);
        dc.DrawText(text, new Point(background.Left + paddingX, background.Top + paddingY));
        return background;
    }

    /// <summary>
    /// The recording selector's instruction: a record dot, "drag the area to record" and a quieter
    /// "Esc: cancel", on a chip outlined in the recording colour.
    /// </summary>
    private Rect DrawRecordingInstruction(DrawingContext dc)
    {
        _activeCueSegment = null;
        string sentence = RecordingInstructionText;
        string escape = UiText.Get("CaptureGuide2_Esc");
        StatusTextsForTest = [sentence, escape];
        FormattedText text = _instructionText ??= CreateText(sentence, _uiTypeface, 14, _primaryTextBrush);
        FormattedText hint = CreateText(escape, _uiTypeface, 12, _mutedTextBrush);
        const double paddingX = 14;
        const double paddingY = 9;
        const double dot = 10;
        const double gap = 9;
        const double hintGap = 18;
        double width = paddingX + dot + gap + text.Width + hintGap + hint.Width + paddingX;
        double height = Math.Max(text.Height, dot) + (paddingY * 2);
        double left = Math.Max(16, (ActualWidth - width) / 2);
        var background = new Rect(left, 18, width, height);
        dc.DrawRoundedRectangle(_chromeBrush, _recordingPen, background, 10, 10);
        dc.DrawEllipse(_recordingBrush, null, new Point(left + paddingX + (dot / 2), background.Top + (height / 2)), dot / 2, dot / 2);
        double x = left + paddingX + dot + gap;
        dc.DrawText(text, new Point(x, background.Top + ((height - text.Height) / 2)));
        dc.DrawText(hint, new Point(x + text.Width + hintGap, background.Top + ((height - hint.Height) / 2)));
        return background;
    }

    /// <summary>The recording selector's sentence.</summary>
    internal static string RecordingInstructionText => UiText.Get("Onboard_RecordSelectInstruction");

    /// <summary>
    /// What a drag and a click do in the current mode, side by side, with the part that applies now
    /// filled with the accent. Each part is as wide as its longest wording (window or monitor), so the
    /// chip never shifts while the pointer moves. Under the mode bar it is centred on the bar and
    /// scaled with it; without the bar it sits at the top and also lists Esc.
    /// </summary>
    private Rect DrawCueInstruction(DrawingContext dc, CaptureCueState cue, double centerX, double top, double scale)
    {
        const double inset = 5;
        const double paddingX = 10;
        const double paddingY = 5;
        const double separator = 16;
        List<(string Text, string[] Variants)> parts = StatusParts(cue, out int active);
        StatusTextsForTest = parts.Select(part => part.Text).ToArray();
        var texts = new FormattedText[parts.Count];
        var widths = new double[parts.Count];
        double width = inset * 2;
        double textHeight = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            texts[i] = CreateText(parts[i].Text, _uiTypeface, 13, i == active ? _accentTextBrush : _primaryTextBrush);
            widths[i] = parts[i].Variants.Max(variant => CreateText(variant, _uiTypeface, 13, _primaryTextBrush).Width);
            width += widths[i] + (paddingX * 2) + (i > 0 ? separator : 0);
            textHeight = Math.Max(textHeight, texts[i].Height);
        }

        double segmentHeight = textHeight + (paddingY * 2);
        var background = new Rect(0, 0, width, segmentHeight + (inset * 2));
        double left = ShowModeBar
            ? Math.Clamp(centerX - (width * scale / 2), 0, Math.Max(0, ActualWidth - (width * scale)))
            : Math.Max(16, (ActualWidth - width) / 2);
        Rect Place(Rect local) => new(left + (local.X * scale), top + (local.Y * scale), local.Width * scale, local.Height * scale);

        // Laid out at the origin, then placed (and scaled with the bar) by one transform.
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, left, top));
        dc.DrawRoundedRectangle(_chromeBrush, _chipBorderPen, background, 10, 10);
        FormattedText dot = CreateText("·", _uiTypeface, 13, _mutedTextBrush);
        double x = inset;
        _activeCueSegment = null;
        for (int i = 0; i < texts.Length; i++)
        {
            if (i > 0)
            {
                dc.DrawText(dot, new Point(x + ((separator - dot.Width) / 2), inset + paddingY));
                x += separator;
            }
            var segment = new Rect(x, inset, widths[i] + (paddingX * 2), segmentHeight);
            if (i == active)
            {
                dc.DrawRoundedRectangle(_accentBrush, null, segment, 7, 7);
                _activeCueSegment = Place(segment);
            }
            dc.DrawText(texts[i], new Point(segment.Left + ((segment.Width - texts[i].Width) / 2), segment.Top + paddingY));
            x = segment.Right;
        }
        dc.Pop();
        return Place(background);
    }

    /// <summary>
    /// The status parts for the current mode, each with every wording it can take. The colour mode has
    /// one part, always lit: every click copies a colour.
    /// </summary>
    private List<(string Text, string[] Variants)> StatusParts(CaptureCueState cue, out int active)
    {
        active = -1;
        var parts = new List<(string Text, string[] Variants)>(3);
        if (_colorPickMode)
        {
            string color = UiText.Get("CaptureGuide2_ClickColor");
            parts.Add((color, [color]));
            active = 0;
            return parts;
        }

        string drag = UiText.Get(_textCaptureMode ? "CaptureGuide2_DragText" : "CaptureGuide2_DragCapture");
        parts.Add((drag, [drag]));
        if (cue == CaptureCueState.Drag) active = 0;
        if (DetectWindows)
        {
            string window = UiText.Get(ClickKey(wholeMonitor: false));
            string monitor = UiText.Get(ClickKey(wholeMonitor: true));
            bool onMonitor = _hoverRegion.HasValue && _hoverWholeMonitor && !_selection.HasValue;
            parts.Add((onMonitor ? monitor : window, [window, monitor]));
            if (cue == CaptureCueState.Click) active = 1;
        }

        if (!ShowModeBar)
        {
            string esc = UiText.Get("CaptureGuide2_Esc");
            parts.Add((esc, [esc]));
        }

        return parts;
    }

    /// <summary>What a click on a highlighted window or monitor does in the current mode.</summary>
    private string ClickKey(bool wholeMonitor) => (_textCaptureMode, wholeMonitor) switch
    {
        (true, true) => "CaptureGuide2_ClickMonitorText",
        (true, false) => "CaptureGuide2_ClickWindowText",
        (false, true) => "CaptureGuide2_ClickMonitor",
        _ => "CaptureGuide2_ClickWindow",
    };

    /// <summary>The colour mode shows the magnifier even with the setting off: it is how a pixel is aimed at.</summary>
    private bool ShowsMagnifier => _showMagnifier || _colorPickMode;

    private void DrawMagnifier(DrawingContext dc)
    {
        if (!ShowsMagnifier || _magnifierCrop is null || !PointerInside)
        {
            return;
        }

        double width = MagnifierDestinationPixels * DipPerPixelX;
        double imageHeight = MagnifierDestinationPixels * DipPerPixelY;
        double footerHeight = 38 * DipPerPixelY;
        double gapX = 20 * DipPerPixelX;
        double gapY = 22 * DipPerPixelY;
        Point cursorDip = ToDipPoint(_cursorPixel);

        double x = cursorDip.X + gapX;
        double y = cursorDip.Y + gapY;
        if (x + width > ActualWidth - 8)
        {
            x = cursorDip.X - gapX - width;
        }

        if (y + imageHeight + footerHeight > ActualHeight - 8)
        {
            y = cursorDip.Y - gapY - imageHeight - footerHeight;
        }

        x = Math.Clamp(x, 8, Math.Max(8, ActualWidth - width - 8));
        y = Math.Clamp(y, 8, Math.Max(8, ActualHeight - imageHeight - footerHeight - 8));

        var panel = new Rect(x - 3, y - 3, width + 6, imageHeight + footerHeight + 6);
        dc.DrawRoundedRectangle(_chromeBrush, _magnifierBorderPen, panel, 9, 9);
        dc.DrawImage(_magnifierCrop, new Rect(x, y, width, imageHeight));

        double cellWidth = width / _magnifierCrop.PixelWidth;
        double cellHeight = imageHeight / _magnifierCrop.PixelHeight;
        Pen gridPen = _magnifierGridPen;
        for (int index = 1; index < _magnifierCrop.PixelWidth; index++)
        {
            dc.DrawLine(gridPen, new Point(x + (index * cellWidth), y), new Point(x + (index * cellWidth), y + imageHeight));
        }
        for (int index = 1; index < _magnifierCrop.PixelHeight; index++)
        {
            dc.DrawLine(gridPen, new Point(x, y + (index * cellHeight)), new Point(x + width, y + (index * cellHeight)));
        }

        int cursorX = Math.Clamp((int)Math.Floor(_cursorPixel.X) - _magnifierCropX, 0, _magnifierCrop.PixelWidth - 1);
        int cursorY = Math.Clamp((int)Math.Floor(_cursorPixel.Y) - _magnifierCropY, 0, _magnifierCrop.PixelHeight - 1);
        var cell = new Rect(x + (cursorX * cellWidth), y + (cursorY * cellHeight), cellWidth, cellHeight);
        dc.DrawRectangle(null, _magnifierBorderPen, cell);

        string coordinate = $"{(int)_cursorPixel.X}, {(int)_cursorPixel.Y}";
        FormattedText color = CreateText(_sampleLabel, _monoTypeface, 12, _primaryTextBrush);
        FormattedText location = CreateText(coordinate, _monoTypeface, 10, _mutedTextBrush);
        double footerTop = y + imageHeight;
        dc.DrawText(color, new Point(x + 8, footerTop + 5));
        dc.DrawText(location, new Point(x + width - location.Width - 8, footerTop + 6));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        PointerMoved();
    }

    /// <summary>
    /// Plays a pointer gesture from code at the position <see cref="ReadCursor"/> reports,
    /// through the same handling as the mouse, without capturing the real mouse. The tutorial
    /// clip generator uses it.
    /// </summary>
    internal void SimulatePointerDown() => PointerPressed(source: null, capture: false);

    /// <inheritdoc cref="SimulatePointerDown"/>
    internal void SimulatePointerMove() => PointerMoved();

    /// <inheritdoc cref="SimulatePointerDown"/>
    internal void SimulatePointerUp() => PointerReleased(releaseCapture: false);

    /// <summary>
    /// Whether the pointer is over the selector, for a simulated gesture that has no real mouse
    /// over the window; null (the default) uses <see cref="UIElement.IsMouseOver"/>.
    /// </summary>
    internal bool? SimulatedPointerInside { get; set; }

    private bool PointerInside => SimulatedPointerInside ?? IsMouseOver;

    private void PointerMoved()
    {
        if (_ended) return;
        PointD rawPixel = UpdatePointer();
        RectD? previous = _selection;

        if (_interaction == InteractionMode.Create)
        {
            PointD edge = ClampEdgePoint(rawPixel);
            if (_hoverRegion.HasValue && !_dragBeyondClick)
            {
                // Inside the click threshold the press still targets the highlighted window.
                if (WindowDetectHitTest.IsClick(_dragAnchor, edge, ClickThresholdX, ClickThresholdY)) return;
                _dragBeyondClick = true;
                ClearHover();
            }

            if (PresetSize is { } preset && preset.Width > 0 && preset.Height > 0)
            {
                // Fixed size: the drag only moves the box's top-left corner. The stored
                // size is honoured exactly (clamped to the frame) so every capture from
                // the same preset has identical dimensions.
                double width = Math.Min(preset.Width, FrameBounds.Width);
                double height = Math.Min(preset.Height, FrameBounds.Height);
                _selection = new RectD(_dragAnchor.X, _dragAnchor.Y, width, height).ClampTo(FrameBounds);
            }
            else
            {
                _selection = RectD.FromCorners(_dragAnchor, edge).ClampTo(FrameBounds);
            }
        }

        if (previous != _selection) QueueFeedback();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (PointerPressed(e.OriginalSource as DependencyObject, capture: true)) e.Handled = true;
    }

    /// <summary>A left press at the current pointer; true when handled.</summary>
    private bool PointerPressed(DependencyObject? source, bool capture)
    {
        if (_ended)
        {
            return false;
        }

        DismissStartGuide();
        PointD rawPoint = UpdatePointer();
        // The mode bar's own buttons handle their presses; its padding and gaps must not start a capture.
        if (_pointerOverBar || _modeBar?.Holds(source) == true)
        {
            return true;
        }

        Focus();
        if (_colorPickMode)
        {
            _ = CopyColorAtPointerAsync();
            return true;
        }

        PointD point = ClampEdgePoint(rawPoint);
        _cursorPixel = ClampSamplePoint(rawPoint);
        _dragAnchor = point;
        _selectedWindow = null;
        _dragBeyondClick = false;
        // A press on a highlighted window keeps that highlight until the pointer leaves the
        // click threshold; otherwise the free-region drag starts exactly as before.
        _selection = _hoverRegion.HasValue ? null : new RectD(point.X, point.Y, 0, 0);
        _interaction = InteractionMode.Create;
        if (capture) _ = Mouse.Capture(this);
        QueueFeedback();
        return true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (PointerReleased(releaseCapture: true)) e.Handled = true;
    }

    /// <summary>A left release at the current pointer; true when handled.</summary>
    private bool PointerReleased(bool releaseCapture)
    {
        if (_interaction != InteractionMode.Create || _ended)
        {
            return false;
        }

        PointD rawPoint = UpdatePointer();
        PointD point = ClampEdgePoint(rawPoint);
        _cursorPixel = ClampSamplePoint(rawPoint);
        if (_hoverRegion is { } windowRegion && !_dragBeyondClick &&
            WindowDetectHitTest.IsClick(_dragAnchor, point, ClickThresholdX, ClickThresholdY))
        {
            _selection = windowRegion.ClampTo(FrameBounds).ToPixelBounds();
            _selectedWindow = _hoverWindow;
            _hoverWindow = null;
            _hoverRegion = null;
            _hoverWholeMonitor = false;
        }
        else
        {
            _selection = ResolveCompletedDrag(_dragAnchor, point, FrameBounds);
        }
        _interaction = InteractionMode.None;
        if (releaseCapture) Mouse.Capture(null);
        QueueFeedback();

        if (_selection.HasValue)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(ConfirmSelection));
        }

        return true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        RequestCancel();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // A Korean IME in Hangul mode reports letters as ImeProcessed; C and T must still work.
        Key key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt
            or Key.RightAlt or Key.System or Key.LWin or Key.RWin))
        {
            DismissStartGuide();
        }

        // On a focused bar button Enter presses it and the arrows move along the bar.
        if (_modeBar?.Holds(e.OriginalSource as DependencyObject) == true)
        {
            if (key == Key.Enter && e.OriginalSource is ButtonBase button)
            {
                if (button is System.Windows.Controls.RadioButton radio) radio.IsChecked = true;
                else button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                e.Handled = true;
                return;
            }

            if (key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Enter or Key.Space) return;
        }

        switch (key)
        {
            case Key.Escape:
                RequestCancel();
                e.Handled = true;
                return;
            case Key.LeftShift:
            case Key.RightShift:
                UpdatePointer();
                return;
            case Key.Enter:
                ConfirmSelection();
                e.Handled = true;
                return;
            case Key.A when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                _selection = FrameBounds;
                _selectedWindow = null;
                ClearHover();
                QueueFeedback();
                e.Handled = true;
                return;
            // Plain C/T only: Shift may be held for precision, Ctrl/Alt chords stay untouched.
            case Key.C when CopyColorText is not null && IsPlainOrShift(Keyboard.Modifiers):
                UpdatePointer();
                _ = CopyColorAtPointerAsync();
                e.Handled = true;
                return;
            case Key.T when TextCaptureAvailable && IsPlainOrShift(Keyboard.Modifiers):
                TextCaptureMode = !TextCaptureMode;
                e.Handled = true;
                return;
            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
                NudgeSelection(e.Key, Keyboard.Modifiers);
                e.Handled = true;
                return;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (!_ended && e.Key is Key.LeftShift or Key.RightShift) UpdatePointer();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _precisionPointer.Reset();
        if (_interaction == InteractionMode.Create && !_ended)
        {
            _interaction = InteractionMode.None;
            _selection = null;
            QueueFeedback();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_interaction == InteractionMode.None)
        {
            _lastSample = null;
            QueueFeedback();
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (!_ended) QueueFeedback();
    }

    private void NudgeSelection(Key key, ModifierKeys modifiers)
    {
        if (_selection is not RectD selection)
        {
            return;
        }

        double step = modifiers.HasFlag(ModifierKeys.Control) ? 10 : 1;
        bool resize = modifiers.HasFlag(ModifierKeys.Shift);
        _selectedWindow = null;

        if (resize)
        {
            double left = selection.Left;
            double top = selection.Top;
            double right = selection.Right;
            double bottom = selection.Bottom;
            switch (key)
            {
                case Key.Left: right = Math.Max(left + 1, right - step); break;
                case Key.Right: right = Math.Min(FrameBounds.Right, right + step); break;
                case Key.Up: bottom = Math.Max(top + 1, bottom - step); break;
                case Key.Down: bottom = Math.Min(FrameBounds.Bottom, bottom + step); break;
            }

            _selection = new RectD(left, top, right - left, bottom - top).ToPixelBounds();
        }
        else
        {
            double dx = key switch { Key.Left => -step, Key.Right => step, _ => 0 };
            double dy = key switch { Key.Up => -step, Key.Down => step, _ => 0 };
            _selection = new RectD(selection.Left + dx, selection.Top + dy, selection.Width, selection.Height)
                .ClampTo(FrameBounds)
                .ToPixelBounds();
        }

        QueueFeedback();
    }

    private void ConfirmSelection() => ConfirmSelection(wholeMonitor: false);

    private void ConfirmSelection(bool wholeMonitor)
    {
        if (_ended || _selection is not RectD selection)
        {
            return;
        }

        RectD pixels = selection.ToPixelBounds().ClampTo(FrameBounds);
        if (pixels.Width < MinimumSelectionPixels || pixels.Height < MinimumSelectionPixels)
        {
            return;
        }

        _ended = true;
        EndPointerInteraction();
        SelectionConfirmed?.Invoke(this, new RegionSelectionEventArgs(pixels)
        {
            DetectedWindow = _selectedWindow,
            TextCapture = _textCaptureMode,
            WholeMonitor = wholeMonitor,
        });
    }

    private static bool IsPlainOrShift(ModifierKeys modifiers) =>
        modifiers is ModifierKeys.None or ModifierKeys.Shift;

    /// <summary>
    /// Copies the frozen pixel under the pointer (the one the magnifier centres on) in the
    /// configured format and shows a short confirmation inside the overlay. The overlay stays
    /// open. Returns the copied value, or null when nothing was copied.
    /// </summary>
    internal async Task<string?> CopyColorAtPointerAsync()
    {
        if (_ended || _released || CopyColorText is not { } copy || !TrySampleColor(_cursorPixel, out byte red, out byte green, out byte blue))
        {
            return null;
        }

        string value = ColorSampleFormatter.Format(red, green, blue, ReadColorFormat());
        bool copied;
        try
        {
            copied = await copy(value).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            copied = false;
        }

        string message = copied ? UiText.Format("Tools_ColorCopied", value) : UiText.Get("ColorPick_CopyFailed");
        if (Dispatcher.CheckAccess()) ShowToast(message, copied);
        else _ = Dispatcher.BeginInvoke(new Action(() => ShowToast(message, copied)));
        return copied ? value : null;
    }

    private bool TrySampleColor(PointD pixel, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        if (_frame is not { } frame || frame.PixelWidth < 1 || frame.PixelHeight < 1) return false;
        int x = Math.Clamp((int)Math.Floor(pixel.X), 0, frame.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Floor(pixel.Y), 0, frame.PixelHeight - 1);
        var rect = new Int32Rect(x, y, 1, 1);
        byte[] sample = new byte[4];
        BitmapSource source = frame.Bitmap;
        if (source.Format == PixelFormats.Bgr32 || source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32)
        {
            source.CopyPixels(rect, sample, 4, 0);
        }
        else
        {
            // Same narrow conversion as the magnifier: never convert the whole desktop.
            new FormatConvertedBitmap(new CroppedBitmap(source, rect), PixelFormats.Bgra32, null, 0)
                .CopyPixels(sample, 4, 0);
        }

        blue = sample[0];
        green = sample[1];
        red = sample[2];
        return true;
    }

    private void ShowToast(string message, bool copied)
    {
        if (_released || _ended) return;
        _toastMessage = message;
        _toastTimer ??= CreateToastTimer();
        _toastTimer.Stop();
        _toastTimer.Interval = ToastDuration(copied);
        _toastTimer.Start();
        QueueFeedback();
        FeedbackTiming.Announce(this, message, copied ? FeedbackKind.Confirmation : FeedbackKind.Warning, "MyCapture.Capture.ColorCopy");
    }

    private DispatcherTimer CreateToastTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _toastMessage = null;
            QueueFeedback();
        };
        return timer;
    }

    private void RequestCancel()
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        EndPointerInteraction();
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void AttachFrame(FrozenFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_released) return;
        _frame = frame;
        _staticDirty = true;
        _magnifierCrop = null;
        _magnifierCropX = -1;
        _magnifierCropY = -1;
        _lastSample = null;
        QueueFeedback();
    }

    private void UpdateMagnifier()
    {
        if (_frame is null)
        {
            _magnifierCrop = null;
            return;
        }

        int centerX = Math.Clamp((int)Math.Floor(_cursorPixel.X), 0, _frame.PixelWidth - 1);
        int centerY = Math.Clamp((int)Math.Floor(_cursorPixel.Y), 0, _frame.PixelHeight - 1);
        PointD sample = new(centerX, centerY);
        if (_magnifierCrop is not null && _lastSample == sample) return;
        _lastSample = sample;
        int half = MagnifierSourcePixels / 2;
        int x = Math.Clamp(centerX - half, 0, Math.Max(0, _frame.PixelWidth - MagnifierSourcePixels));
        int y = Math.Clamp(centerY - half, 0, Math.Max(0, _frame.PixelHeight - MagnifierSourcePixels));
        int width = Math.Min(MagnifierSourcePixels, _frame.PixelWidth);
        int height = Math.Min(MagnifierSourcePixels, _frame.PixelHeight);

        int stride = width * 4;
        BitmapSource source = _frame.Bitmap;
        bool direct = source.Format == PixelFormats.Bgr32 || source.Format == PixelFormats.Bgra32
            || source.Format == PixelFormats.Pbgra32;
        PixelFormat format = direct ? source.Format : PixelFormats.Bgra32;
        if (_magnifierCrop is null)
        {
            _magnifierCrop = new WriteableBitmap(width, height, 96, 96, format, null);
        }
        var sourceRect = new Int32Rect(x, y, width, height);
        if (direct) source.CopyPixels(sourceRect, _samplePixels, stride, 0);
        else
        {
            // Unusual imported/test formats convert only the tiny sampled region. Never
            // materialize a converted desktop merely to display a magnifier pixel.
            var region = new CroppedBitmap(source, sourceRect);
            var converted = new FormatConvertedBitmap(region, PixelFormats.Bgra32, null, 0);
            converted.CopyPixels(_samplePixels, stride, 0);
        }
        _magnifierCrop.WritePixels(new Int32Rect(0, 0, width, height), _samplePixels, stride, 0);
        _magnifierCropX = x;
        _magnifierCropY = y;
        int sampleOffset = ((centerY - y) * width + centerX - x) * 4;
        _sampleLabel = $"#{_samplePixels[sampleOffset + 2]:X2}{_samplePixels[sampleOffset + 1]:X2}{_samplePixels[sampleOffset]:X2}";
        MagnifierUpdateCount++;
    }

    private void ApplyGuideBrushes()
    {
        _dimmerBrush = _guidePalette["CaptureGuide.Dimmer"];
        _chromeBrush = _guidePalette["CaptureGuide.ChipSurface"];
        _primaryTextBrush = _guidePalette["CaptureGuide.Text"];
        _mutedTextBrush = _guidePalette["CaptureGuide.MutedText"];
        _accentBrush = _guidePalette["CaptureGuide.SelectionBorder"];
        _accentTextBrush = _guidePalette["CaptureGuide.AccentText"];
        _modeBar?.ApplyPalette(_guidePalette);
        _startGuide?.ApplyPalette(_guidePalette);
    }

    /// <summary>Whether two palettes paint identically: the same keys, each a solid brush of the same colour.</summary>
    internal static bool SamePalette(IReadOnlyDictionary<string, Brush> left, IReadOnlyDictionary<string, Brush> right) =>
        left.Count == right.Count
        && left.All(entry => right.TryGetValue(entry.Key, out Brush? other) && SameColor(entry.Value, other));

    private static bool SameColor(Brush left, Brush right) =>
        ReferenceEquals(left, right)
        || (left is SolidColorBrush a && right is SolidColorBrush b && a.Color == b.Color && a.Opacity == b.Opacity);

    private void OnGuidePaletteChanged()
    {
        if (_released) return;
        _guidePalette = CaptureGuidePalette.Resolve();
        _recordingBrush = RecordingSelectionCue.Resolve();
        ApplyGuideBrushes();
        _staticDirty = true;
        QueueFeedback();
    }

    private void BuildGuidePens()
    {
        GuidePenBuildCount++;
        double pixel = GuidePixelSizeX;
        Pen Create(string key, double width)
        {
            var pen = new Pen(_guidePalette[key], width * pixel);
            pen.Freeze();
            return pen;
        }
        _pointerOuterPen = Create("CaptureGuide.CrosshairShadow", 3);
        _pointerInnerPen = Create("CaptureGuide.Crosshair", 1);
        _anchorPen = Create("CaptureGuide.StartPoint", 1);
        _guideShadowPen = CaptureGuideStrip.ShadowPen(_guidePalette["CaptureGuide.LineShadow"], pixel);
        _guideLinePen = CaptureGuideStrip.LinePen(_guidePalette["CaptureGuide.Line"], pixel);
        _selectionBorderPen = Create("CaptureGuide.SelectionBorder", 1);
        _accentChipPen = Create("CaptureGuide.SelectionBorder", 2);
        _detectedBorderPen = Create("CaptureGuide.SelectionBorder", 3);
        _keylinePen = Create("CaptureGuide.CrosshairShadow", 1);
        _innerGlowPen = Create("CaptureGuide.InnerGlow", 3);
        _chipBorderPen = Create("CaptureGuide.ChipBorder", 1);
        _magnifierBorderPen = Create("CaptureGuide.MagnifierBorder", 1);
        _magnifierGridPen = Create("CaptureGuide.MagnifierGrid", 0.5);
        _recordingPen = new Pen(_recordingBrush, 2 * pixel);
        _recordingPen.Freeze();
        _recordingEdgePen = new Pen(_recordingBrush, 3 * pixel);
        _recordingEdgePen.Freeze();
    }

    private FormattedText CreateText(string text, Typeface typeface, double size, Brush brush) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private int PixelWidth => _frame?.PixelWidth ?? Math.Max(1, (int)_screenBounds.Width);

    private int PixelHeight => _frame?.PixelHeight ?? Math.Max(1, (int)_screenBounds.Height);

    private RectD FrameBounds => new(0, 0, PixelWidth, PixelHeight);

    private double DipPerPixelX => ActualWidth / PixelWidth;

    private double DipPerPixelY => ActualHeight / PixelHeight;

    private PointD ToPixelPoint(Point dipPoint) => new(
        dipPoint.X / Math.Max(double.Epsilon, DipPerPixelX),
        dipPoint.Y / Math.Max(double.Epsilon, DipPerPixelY));

    private Point ToDipPoint(PointD pixelPoint) => new(
        pixelPoint.X * DipPerPixelX,
        pixelPoint.Y * DipPerPixelY);

    private Rect ToDipRect(RectD pixelRect) => new(
        pixelRect.Normalized().Left * DipPerPixelX,
        pixelRect.Normalized().Top * DipPerPixelY,
        pixelRect.Normalized().Width * DipPerPixelX,
        pixelRect.Normalized().Height * DipPerPixelY);

    private PointD ClampSamplePoint(PointD point) => new(
        Math.Clamp(point.X, 0, Math.Max(0, PixelWidth - 1)),
        Math.Clamp(point.Y, 0, Math.Max(0, PixelHeight - 1)));

    private PointD ClampEdgePoint(PointD point) => new(
        Math.Clamp(point.X, 0, PixelWidth),
        Math.Clamp(point.Y, 0, PixelHeight));

    private enum InteractionMode
    {
        None,
        Create,
    }
}

internal sealed class RegionSelectionEventArgs : EventArgs
{
    internal RegionSelectionEventArgs(RectD bitmapRegion)
    {
        BitmapRegion = bitmapRegion;
    }

    internal RectD BitmapRegion { get; }

    /// <summary>The auto-detected window a click selected, or null for a drag or keyboard selection.</summary>
    internal WindowCandidate? DetectedWindow { get; init; }

    /// <summary>True when the overlay was in text capture mode at confirmation.</summary>
    internal bool TextCapture { get; init; }

    /// <summary>
    /// The full-screen button chose one whole monitor. Like a desktop click, no single window is
    /// that capture's source.
    /// </summary>
    internal bool WholeMonitor { get; init; }
}
