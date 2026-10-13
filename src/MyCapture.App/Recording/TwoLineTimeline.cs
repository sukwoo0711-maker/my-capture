using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// The recording editor's trim timeline, with an optional "overview + detail" pair. The top
/// strip always shows the complete clip: the part that stays is bracketed between two large
/// handles that carry their start/end times, and the parts that will be cut are dark and
/// hatched. When the detail strip is shown, a thin zoom lane under the top strip selects the
/// range that the bottom strip expands to its full width, exposing individual frames.
/// </summary>
/// <remarks>
/// All time/pixel maths is delegated to <see cref="TimelineViewport"/>,
/// <see cref="TrimSelection"/>, <see cref="FrameStepCalculator"/> and
/// <see cref="FilmstripLayout"/>. Rendering uses three fixed DrawingVisual layers per strip;
/// pointer input only mutates state and marks layers dirty, and loaded controls draw at most
/// once on the next composition frame. The top strip's track shows a filmstrip of frame
/// thumbnails (decoded off the UI thread by <see cref="TimelineFilmstrip"/>) under its ticks,
/// so the user sees what the cut ranges remove.
/// </remarks>
internal sealed class TwoLineTimeline : ContentControl, IDisposable
{
    /// <summary>Top strip height while the detail strip is also shown.</summary>
    private const double OverviewHeight = 60.0;

    /// <summary>Top strip height when it is the only (trim) timeline.</summary>
    private const double OverviewTrimHeight = 74.0;

    /// <summary>
    /// The strip heights of a short window. The editor's minimum window gives the timeline
    /// about 195 DIP; the taller strips would push the first layer track out of that
    /// viewport whenever a layer is selected, so these keep the pre-filmstrip geometry there.
    /// </summary>
    private const double OverviewCompactHeight = 44.0;
    private const double OverviewTrimCompactHeight = 58.0;

    /// <summary>Host windows at least this tall show the full-height strips.</summary>
    internal const double FullStripWindowHeight = 600.0;
    private const double DetailHeight = 42.0;
    // The connector also draws a 10.5pt explanatory line; reserve its complete glyph height.
    private const double ConnectorHeight = 18.0;
    /// <summary>Top lane of the trim strip: time ruler labels, overlaid by the handle chips.</summary>
    private const double LabelLaneHeight = 18.0;
    /// <summary>Bottom lane of the trim strip that holds the detail zoom range.</summary>
    private const double ZoomLaneHeight = 9.0;
    private const double EdgeGrab = 14.0;
    private const double BrushGripWidth = 10.0;
    private const double TrimHandleWidth = 14.0;

    /// <summary>
    /// Half the pointer target around a trim handle. The 44 DIP target follows the Windows
    /// touch-optimized size (the visible handle can be narrower than its target).
    /// </summary>
    internal const double TrimHandleGrab = 22.0;

    /// <summary>How long the strip width must hold before a changed tile count decodes new thumbnails.</summary>
    private static readonly TimeSpan FilmstripResampleDelay = TimeSpan.FromMilliseconds(250);

    private readonly TimelineRenderSurface _overview;
    private readonly TimelineRenderSurface _connector;
    private readonly TimelineRenderSurface _detail;
    private readonly TextBlock _overviewCaption;
    private readonly TextBlock _detailCaption;
    private readonly FrameworkElement _detailPanel;
    private readonly CompositionFrameScheduler _renderScheduler;

    private readonly Brush _surfaceSunken;
    private readonly Brush _surfaceBase;
    private readonly Brush _surfaceScrim;
    private readonly Brush _textPrimary;
    private readonly Brush _textSecondary;
    private readonly Brush _textMuted;
    private readonly Brush _borderSubtle;
    private readonly Brush _accent;
    private readonly Brush _accentSubtle;
    private readonly Brush _playhead;
    private readonly Brush _handleFill;
    private readonly Brush _handleText;
    private readonly Brush _cutFill;
    private readonly Pen _majorTickPen;
    private readonly Pen _minorTickPen;
    private readonly Pen _overviewMajorTickPen;
    private readonly Pen _overviewMinorTickPen;
    private readonly Pen _detailGroupPen;
    private readonly Pen _accentOutlinePen;
    private readonly Pen _connectorPen;
    private readonly Pen _playheadPen;
    private readonly Pen _gripMarkPen;
    private readonly Pen _cutHatchPen;
    private readonly Pen _cutEdgePen;
    private readonly Pen _focusPen;
    private readonly Pen _laneSeparatorPen;
    private readonly Typeface _regularTypeface;
    private readonly Typeface _semiBoldTypeface;
    private readonly StreamGeometry _playheadMarker;

    private int _fps = 15;
    private double _durationMs = 1;
    private double _playheadMs;
    private TimelineViewport _viewport = new(1, 15);
    private TrimSelection _trim = new(1);
    private bool _trimModeEnabled;
    private bool _detailVisible = true;
    private TrimHandle _activeTrimHandle = TrimHandle.In;

    private DragMode _drag = DragMode.None;
    private double _dragAnchorMs;
    private double _dragStartX;
    private bool _dragMoved;
    private TimelineRenderSurface? _dragStrip;
    private TrimHandle _pendingHandle = TrimHandle.In;

    /// <summary>
    /// True only after the user took hold of a handle (pressed it, dragged it, or chose it
    /// with the Trim command). A plain click or drag on the strip seeks, and leaves the arrow
    /// keys to the playhead; without this, every click turned ←/→ into handle nudges.
    /// </summary>
    private bool _handleArmed;

    /// <summary>Half the width of the handle itself; a press here drags at once.</summary>
    private const double HandleBodyGrab = 8.0;

    private readonly DispatcherTimer _filmstripResample;
    private string? _filmstripPath;
    private int _filmstripSourceWidth;
    private int _filmstripSourceHeight;
    private ILogger? _filmstripLog;
    private TimelineFilmstrip? _filmstrip;
    private TimelineFilmstrip? _filmstripFallback;
    private FilmstripSlotCache _filmstripSlots = FilmstripSlotCache.Empty;
    private Window? _hostWindow;
    private bool _fullStrip;
    private bool _disposed;

    private enum DragMode { None, Playhead, BrushBody, BrushLeft, BrushRight, TrimIn, TrimOut, TrimPending }

    private enum TrimHandle { In, Out }

    internal TwoLineTimeline()
    {
        Focusable = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        _surfaceSunken = ResolveBrush("Surface.Sunken", Color.FromRgb(0x08, 0x0C, 0x12));
        _surfaceBase = ResolveBrush("Surface.Base", Color.FromRgb(0x0B, 0x0F, 0x17));
        _surfaceScrim = ResolveBrush("Surface.Scrim", Color.FromArgb(0xC2, 0x08, 0x0C, 0x12));
        _textPrimary = ResolveBrush("Text.Primary", Colors.White);
        _textSecondary = ResolveBrush("Text.Secondary", Colors.LightGray);
        _textMuted = ResolveBrush("Text.Secondary", Colors.Gray);
        _borderSubtle = ResolveBrush("Border.Subtle", Colors.DimGray);
        _accent = ResolveBrush("Accent.Default", Color.FromRgb(0x7C, 0x6C, 0xFF));
        _accentSubtle = ResolveBrush("Accent.Subtle", Color.FromRgb(0x15, 0x2F, 0x3E));
        _playhead = ResolveBrush("Timeline.Playhead", Color.FromRgb(0x97, 0x89, 0xFF));
        // The kept range and its handles use the theme's accent pair (accent fill with its
        // on-accent text), so every palette, high contrast included, keeps a tested contrast.
        _handleFill = _accent;
        _handleText = ResolveBrush("Text.OnAccent", Color.FromRgb(0x0A, 0x08, 0x18));
        _cutFill = ResolveBrush("Surface.Scrim", Color.FromArgb(0xE6, 0x08, 0x0C, 0x12));

        _majorTickPen = CreatePen(_textSecondary, 2.0);
        _minorTickPen = CreatePen(_borderSubtle, 1.0);
        // The trim strip's ticks sit over frame thumbnails: thin and translucent, so they
        // still mark the ruler without hiding the frames.
        _overviewMajorTickPen = CreatePen(WithOpacity(_textSecondary, 0.55), 1.0);
        _overviewMinorTickPen = CreatePen(WithOpacity(_textSecondary, 0.35), 1.0);
        _detailGroupPen = CreatePen(_textMuted, 1.6);
        _accentOutlinePen = CreatePen(_accent, 2.0);
        _connectorPen = CreatePen(_accent, 2.0);
        _playheadPen = CreatePen(_playhead, 2.6);
        _gripMarkPen = CreatePen(_handleText, 1.6);
        _cutHatchPen = CreatePen(WithOpacity(_textSecondary, 0.38), 1.2);
        _cutEdgePen = CreatePen(WithOpacity(_textSecondary, 0.55), 1.0);
        _focusPen = CreatePen(ResolveBrush("Border.Focus", Color.FromRgb(0x97, 0x89, 0xFF)), 2.0);
        _laneSeparatorPen = CreatePen(_borderSubtle, 1.0);

        FontFamily uiFont = Application.Current?.TryFindResource("Font.Ui") as FontFamily
            ?? new FontFamily("Segoe UI");
        _regularTypeface = new Typeface(uiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _semiBoldTypeface = new Typeface(uiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        _playheadMarker = BuildPlayheadMarker();

        _renderScheduler = new CompositionFrameScheduler(Dispatcher, RenderDirtyLayers);
        _overview = new TimelineRenderSurface(StripHeight(), DrawOverviewLayer);
        _connector = new TimelineRenderSurface(ConnectorHeight, DrawConnectorLayer) { IsHitTestVisible = false };
        _detail = new TimelineRenderSurface(DetailHeight, DrawDetailLayer);
        _overview.LayersInvalidated += OnLayersInvalidated;
        _connector.LayersInvalidated += OnLayersInvalidated;
        _detail.LayersInvalidated += OnLayersInvalidated;
        _overview.SizeChanged += OnOverviewSizeChanged;
        _filmstripResample = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = FilmstripResampleDelay,
        };
        _filmstripResample.Tick += OnFilmstripResampleTick;

        _overviewCaption = BuildCaption();
        _detailCaption = BuildCaption();

        var panel = new StackPanel();
        panel.Children.Add(Labelled(_overviewCaption, _overview, UiText.Get("VideoEditorUx_TrimTimelineName")));
        panel.Children.Add(_connector);
        _detailPanel = Labelled(_detailCaption, _detail, UiText.Get("Text_AB71749112CF"));
        panel.Children.Add(_detailPanel);
        Content = panel;

        UpdateOverviewHelp();
        AutomationProperties.SetHelpText(
            _detail,
            UiText.Get("Text_6B4EE4E6C8FF"));

        _overview.Cursor = Cursors.Hand;
        _detail.Cursor = Cursors.Cross;
        _overview.MouseLeftButtonDown += (_, e) => OnStripDown(_overview, e, isOverview: true);
        _detail.MouseLeftButtonDown += (_, e) => OnStripDown(_detail, e, isOverview: false);
        _overview.MouseMove += (_, e) => OnStripMove(_overview, e);
        _detail.MouseMove += (_, e) => OnStripMove(_detail, e);
        _overview.MouseLeftButtonUp += (_, e) => EndDrag(_overview, e);
        _detail.MouseLeftButtonUp += (_, e) => EndDrag(_detail, e);
        _overview.MouseLeave += (_, _) => { if (_drag == DragMode.None) { _overview.Cursor = Cursors.Hand; } };
        _detail.MouseLeave += (_, _) => { if (_drag == DragMode.None) { _detail.Cursor = Cursors.Cross; } };
        _overview.LostMouseCapture += OnLostMouseCapture;
        _detail.LostMouseCapture += OnLostMouseCapture;
        _detail.MouseWheel += OnDetailWheel;
        PreviewKeyDown += OnPreviewKeyDown;
        // The keyboard-selected handle shows a focus ring only while the timeline has focus.
        IsKeyboardFocusWithinChanged += (_, _) => InvalidateRange();
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1.0 : 0.5;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Raised for immediate visual intent while the user moves the playhead.</summary>
    internal event EventHandler<double>? PlayheadChanged;
    internal event EventHandler? TrimInteractionStarted;
    internal event EventHandler? TrimInteractionCompleted;

    /// <summary>Raised once after a pointer playhead interaction finishes; use for exact seek.</summary>
    internal event EventHandler<double>? PlayheadInteractionCompleted;

    /// <summary>Raised when the trim In/Out changes.</summary>
    internal event EventHandler? TrimChanged;

    internal double DurationMs => _durationMs;

    internal double PlayheadMs => _playheadMs;

    internal double InMs => _trim.InMs;

    internal double OutMs => _trim.OutMs;

    internal bool IsFullClip => _trim.IsFullClip;

    internal bool TrimModeEnabled => _trimModeEnabled;

    internal double SelectedDurationMs => _trim.SelectedDurationMs;

    internal double ViewStartMs => _viewport.ViewStartMs;

    internal double ViewEndMs => _viewport.ViewEndMs;

    internal double VisibleSpanMs => _viewport.VisibleSpanMs;

    internal bool IsFitAll => _viewport.IsFitAll;

    internal double CoarseIntervalMs => TickStepSeconds() * 1000.0;

    internal string OverviewRangeText => _overviewCaption.Text;

    internal string DetailRangeText => _detailCaption.Text;

    /// <summary>True while the frame-level detail strip (and its zoom lane) is shown.</summary>
    internal bool DetailVisible => _detailVisible;

    /// <summary>True while a trim handle is being dragged.</summary>
    internal bool IsTrimDragging => _drag is DragMode.TrimIn or DragMode.TrimOut;

    /// <summary>True when the end handle is the one the keyboard (and X) adjusts.</summary>
    internal bool IsEndHandleActive => _activeTrimHandle == TrimHandle.Out;

    internal double TrimStripHeight => _overview.Height;

    internal FrameworkElement TrimStripForTest => _overview;

    /// <summary>The thumbnail sampler of the trim strip, or null before a source is set.</summary>
    internal TimelineFilmstrip? FilmstripForTest => _filmstrip;

    /// <summary>True while the host window is tall enough for the full-height strips.</summary>
    internal bool IsFullStripForTest => _fullStrip;

    internal int FixedVisualCountForTest =>
        _overview.FixedVisualCount + _connector.FixedVisualCount + _detail.FixedVisualCount;

    internal long RenderRequestCountForTest => _renderScheduler.RequestCount;

    internal long CoalescedRenderRequestCountForTest => _renderScheduler.CoalescedRequestCount;

    internal long RenderFrameCountForTest => _renderScheduler.RenderFrameCount;

    internal long TransientDrawCountForTest =>
        _overview.TransientDrawCount + _connector.TransientDrawCount + _detail.TransientDrawCount;

    internal void FlushRenderForTest() => _renderScheduler.FlushForTest();

    internal void Initialize(double durationMs, int fps)
    {
        _durationMs = Math.Max(1, durationMs);
        _fps = Math.Max(1, fps);
        _viewport = new TimelineViewport(_durationMs, _fps);
        _trim = new TrimSelection(_durationMs);
        _playheadMs = 0;
        _trimModeEnabled = true;
        _activeTrimHandle = TrimHandle.In;
        _handleArmed = false;

        // The two strips must look different immediately. Select one coarse overview interval
        // by default so the bottom line visibly expands the frames between two heavy marks.
        double initialDetailSpan = Math.Min(_durationMs, CoarseIntervalMs);
        if (initialDetailSpan < _durationMs - 0.001)
        {
            _viewport.SetView(0, initialDetailSpan);
        }

        UpdateRangeCaptions();
        UpdateTrimStatus();
        UpdateOverviewHelp();
        InvalidateAll();
        ScheduleFilmstrip(immediate: true);
    }

    /// <summary>
    /// Shows frame thumbnails of <paramref name="sourcePath"/> in the trim strip. Decoding runs
    /// off the UI thread and starts as soon as the strip has a width; a clip that cannot be
    /// decoded leaves the strip as it was. Call after <see cref="Initialize"/>.
    /// </summary>
    internal void SetFilmstripSource(string sourcePath, int sourceWidth, int sourceHeight, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _filmstripLog = loggerFactory.CreateLogger<TimelineFilmstrip>();
        ClearFilmstrip();
        if (string.IsNullOrWhiteSpace(sourcePath) || sourceWidth < 1 || sourceHeight < 1 || !File.Exists(sourcePath))
        {
            _filmstripLog.LogDebug(
                "No filmstrip for {Path}: the clip is unavailable or reports no size", sourcePath);
            return;
        }

        _filmstripPath = sourcePath;
        _filmstripSourceWidth = sourceWidth;
        _filmstripSourceHeight = sourceHeight;
        ScheduleFilmstrip(immediate: true);
    }

    /// <summary>Externally sets the playhead and keeps it inside the detail viewport.</summary>
    internal void SetPlayhead(double ms, bool ensureVisible = true)
    {
        _playheadMs = Math.Clamp(ms, 0, _durationMs);
        double beforeStart = _viewport.ViewStartMs;
        double beforeEnd = _viewport.ViewEndMs;
        if (ensureVisible)
        {
            _viewport.EnsureVisible(_playheadMs);
        }

        if (ViewportChanged(beforeStart, beforeEnd))
        {
            InvalidateViewport();
        }
        else
        {
            InvalidatePlayhead();
        }
    }

    internal void SetIn(double ms)
    {
        _activeTrimHandle = TrimHandle.In;
        _trim.SetIn(ms);
        OnTrimRangeChanged();
    }

    internal void SetOut(double ms)
    {
        _activeTrimHandle = TrimHandle.Out;
        _trim.SetOut(ms);
        OnTrimRangeChanged();
    }

    internal bool SetTrimRange(double inMs, double outMs)
    {
        if (!double.IsFinite(inMs) || !double.IsFinite(outMs) || inMs < 0
            || outMs > _durationMs || outMs - inMs < Math.Min(TrimSelection.MinimumSpanMs, _durationMs))
            return false;
        if (inMs == InMs && outMs == OutMs) return true;
        _trim.Reset();
        _trim.SetOut(outMs);
        _trim.SetIn(inMs);
        OnTrimRangeChanged();
        return true;
    }

    internal void SetTrimMode(bool enabled)
    {
        if (_trimModeEnabled == enabled)
        {
            return;
        }

        _trimModeEnabled = enabled;
        if (enabled)
        {
            _activeTrimHandle = TrimHandle.In;
            _ = Focus();
        }

        UpdateOverviewHelp();
        InvalidateRange();
    }

    /// <summary>
    /// Shows or hides the frame-level detail strip. Without it the top strip is the single,
    /// taller trim timeline and its zoom lane is not drawn.
    /// </summary>
    internal void SetDetailVisible(bool visible)
    {
        if (_detailVisible == visible)
        {
            return;
        }

        _detailVisible = visible;
        Visibility detail = visible ? Visibility.Visible : Visibility.Collapsed;
        _overviewCaption.Visibility = detail;
        _connector.Visibility = detail;
        _detailPanel.Visibility = detail;
        _overview.Height = StripHeight();
        UpdateOverviewHelp();
        InvalidateAll();
    }

    private double StripHeight() => _detailVisible
        ? (_fullStrip ? OverviewHeight : OverviewCompactHeight)
        : (_fullStrip ? OverviewTrimHeight : OverviewTrimCompactHeight);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachHostWindow(Window.GetWindow(this));
        RequestRenderIfDirty();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AttachHostWindow(null);
        _renderScheduler.CancelPending();
    }

    private void AttachHostWindow(Window? window)
    {
        if (!ReferenceEquals(window, _hostWindow))
        {
            if (_hostWindow is not null)
            {
                _hostWindow.SizeChanged -= OnHostWindowSizeChanged;
            }

            _hostWindow = window;
            if (window is not null)
            {
                window.SizeChanged += OnHostWindowSizeChanged;
            }
        }

        UpdateStripMode();
    }

    private void OnHostWindowSizeChanged(object sender, SizeChangedEventArgs e) => UpdateStripMode();

    /// <summary>Switches between the full and the compact strip heights as the host window's height allows.</summary>
    private void UpdateStripMode()
    {
        bool full = _hostWindow is { ActualHeight: >= FullStripWindowHeight };
        if (full == _fullStrip)
        {
            return;
        }

        _fullStrip = full;
        _overview.Height = StripHeight();
        InvalidateAll();
    }

    /// <summary>
    /// Selects the start or end handle for keyboard adjustment and moves focus to the timeline.
    /// </summary>
    internal void ActivateTrimHandle(bool end)
    {
        if (!_trimModeEnabled)
        {
            SetTrimMode(true);
        }

        _activeTrimHandle = end ? TrimHandle.Out : TrimHandle.In;
        _handleArmed = true;
        _ = Focus();
        InvalidateRange();
    }

    /// <summary>Whether a handle is selected for keyboard adjustment (see <see cref="ActivateTrimHandle"/>).</summary>
    internal bool IsHandleArmed => _handleArmed;

    private void SetHandleArmed(bool armed)
    {
        if (_handleArmed == armed)
        {
            return;
        }

        _handleArmed = armed;
        InvalidateRange();
    }

    internal void FitAll()
    {
        _viewport.FitAll();
        InvalidateViewport();
    }

    internal void ZoomAroundPlayhead(double factor)
    {
        _viewport.Zoom(_playheadMs, factor);
        InvalidateViewport();
    }

    internal void SeekFromOverview(double ms) =>
        SeekTo(ms, snap: false, followDetail: true);

    // ---- test hooks: the same paths a pointer drag uses, without native mouse input ----

    internal void BeginTrimDragForTest(bool end) => BeginTrimDrag(end ? TrimHandle.Out : TrimHandle.In);

    internal void MoveTrimDragForTest(double ms) => MoveTrimDrag(ms);

    internal void EndTrimDragForTest() => EndTrimDrag();

    /// <summary>A pointer press on the trim strip (or the detail strip) at strip coordinates.</summary>
    internal void PressForTest(double x, double y, bool overview = true) =>
        PressAt(overview ? _overview : _detail, new Point(x, y), overview);

    /// <summary>Pointer movement after <see cref="PressForTest"/>, without mouse capture.</summary>
    internal void MoveForTest(double x) => DragTo(x);

    /// <summary>Pointer release after <see cref="PressForTest"/>.</summary>
    internal void ReleaseForTest(double x, double y) => ReleaseAt(new Point(x, y));

    /// <summary>Pixel position of a source time on the trim strip, for tests that press there.</summary>
    internal double OverviewPxForTest(double ms) => OverviewMsToPx(ms, _overview.ActualWidth);

    internal double OverviewTrackMiddleForTest => (LabelLaneHeight + OverviewTrackBottom(_overview.ActualHeight)) / 2.0;

    /// <summary>Names the action a press at (<paramref name="x"/>, <paramref name="y"/>) on the trim strip starts.</summary>
    internal string OverviewHitForTest(double x, double y, double width) => HitTestOverview(x, y, width).ToString();

    // ---- interaction ----

    private void OnDetailWheel(object sender, MouseWheelEventArgs e)
    {
        double centre = _viewport.PxToMs(e.GetPosition(_detail).X, _detail.ActualWidth);
        _viewport.Zoom(centre, e.Delta > 0 ? 0.8 : 1.25);
        InvalidateViewport();
        e.Handled = true;
    }

    private DragMode HitTestOverview(double x, double y, double width)
    {
        if (_trimModeEnabled)
        {
            double inPx = OverviewMsToPx(_trim.InMs, width);
            double outPx = OverviewMsToPx(_trim.OutMs, width);
            ChipLayout chips = LayoutChips(width);
            bool inChip = chips.In.Contains(x, y);
            bool outChip = !inChip && chips.Out.Contains(x, y);
            bool onIn = inChip || (!outChip && Math.Abs(x - inPx) <= TrimHandleGrab);
            bool onOut = outChip || (!inChip && Math.Abs(x - outPx) <= TrimHandleGrab);
            if (onIn && onOut)
            {
                // Close handles share their targets: the nearer handle wins, and a press
                // between two coincident handles goes to the side it is on.
                double toIn = Math.Abs(x - inPx);
                double toOut = Math.Abs(x - outPx);
                onIn = toIn < toOut || (toIn == toOut && x <= inPx);
                onOut = !onIn;
            }

            if (onIn)
            {
                return DragMode.TrimIn;
            }

            if (onOut)
            {
                return DragMode.TrimOut;
            }
        }

        if (_detailVisible && y >= OverviewTrackBottom(_overview.Height) - 3)
        {
            double leftPx = OverviewMsToPx(_viewport.ViewStartMs, width);
            double rightPx = OverviewMsToPx(_viewport.ViewEndMs, width);
            if (Math.Abs(x - leftPx) <= EdgeGrab)
            {
                return DragMode.BrushLeft;
            }

            if (Math.Abs(x - rightPx) <= EdgeGrab)
            {
                return DragMode.BrushRight;
            }

            if (x > leftPx && x < rightPx)
            {
                return DragMode.BrushBody;
            }
        }

        return DragMode.Playhead;
    }

    private void OnStripDown(TimelineRenderSurface strip, MouseButtonEventArgs e, bool isOverview)
    {
        Point position = e.GetPosition(strip);
        if (strip.ActualWidth <= 0)
        {
            return;
        }

        PressAt(strip, position, isOverview);
        strip.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>
    /// A pointer press on a strip. On a handle's own body or its chip the drag starts at once;
    /// in the wider grab zone around a handle the press stays undecided until the pointer
    /// moves, so a click there seeks like anywhere else and only a drag moves the handle.
    /// </summary>
    private void PressAt(TimelineRenderSurface strip, Point position, bool isOverview)
    {
        double x = position.X;
        double width = strip.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        _dragStartX = x;
        _dragMoved = false;
        _dragStrip = strip;
        if (_trimModeEnabled)
        {
            _ = Focus();
        }

        DragMode mode;
        bool onHandleBody = false;
        if (isOverview)
        {
            mode = HitTestOverview(x, position.Y, width);
            if (mode is DragMode.TrimIn or DragMode.TrimOut)
            {
                ChipLayout chips = LayoutChips(width);
                double handlePx = OverviewMsToPx(mode == DragMode.TrimIn ? _trim.InMs : _trim.OutMs, width);
                Rect chip = mode == DragMode.TrimIn ? chips.In : chips.Out;
                onHandleBody = chip.Contains(x, position.Y) || Math.Abs(x - handlePx) <= HandleBodyGrab;
            }
            else if (mode == DragMode.BrushBody)
            {
                _dragAnchorMs = OverviewPxToMs(x, width) - _viewport.ViewStartMs;
            }
            else if (mode == DragMode.Playhead)
            {
                // Seeking outside the zoom range also moves the lower detail range to that
                // location. Otherwise the shared playhead disappears from the detail.
                SeekFromOverview(OverviewPxToMs(x, width));
            }
        }
        else
        {
            double inPx = _viewport.MsToPx(_trim.InMs, width);
            double outPx = _viewport.MsToPx(_trim.OutMs, width);
            bool onIn = _trimModeEnabled && IsVisibleInDetail(_trim.InMs) && Math.Abs(x - inPx) <= TrimHandleGrab;
            bool onOut = _trimModeEnabled && IsVisibleInDetail(_trim.OutMs) && Math.Abs(x - outPx) <= TrimHandleGrab;
            if (onIn && onOut)
            {
                onIn = Math.Abs(x - inPx) <= Math.Abs(x - outPx);
                onOut = !onIn;
            }

            mode = onIn ? DragMode.TrimIn : onOut ? DragMode.TrimOut : DragMode.Playhead;
            if (mode is DragMode.TrimIn or DragMode.TrimOut)
            {
                onHandleBody = Math.Abs(x - (mode == DragMode.TrimIn ? inPx : outPx)) <= HandleBodyGrab;
            }
            else
            {
                SeekTo(_viewport.PxToMs(x, width), snap: true, followDetail: false);
            }
        }

        if (mode is DragMode.TrimIn or DragMode.TrimOut)
        {
            TrimHandle handle = mode == DragMode.TrimIn ? TrimHandle.In : TrimHandle.Out;
            if (onHandleBody)
            {
                SetHandleArmed(true);
                BeginTrimDrag(handle);
            }
            else
            {
                _pendingHandle = handle;
                _drag = DragMode.TrimPending;
            }
        }
        else
        {
            SetHandleArmed(false);
            _drag = mode;
        }

        UpdateCursor(strip, position);
    }

    private void OnStripMove(TimelineRenderSurface eventStrip, MouseEventArgs e)
    {
        if (_drag == DragMode.None)
        {
            UpdateCursor(eventStrip, e.GetPosition(eventStrip));
            return;
        }

        TimelineRenderSurface strip = _dragStrip ?? eventStrip;
        DragTo(e.GetPosition(strip).X);
    }

    /// <summary>Pointer movement while a press is held, in the pressed strip's coordinates.</summary>
    private void DragTo(double x)
    {
        TimelineRenderSurface strip = _dragStrip ?? _overview;
        double width = strip.ActualWidth;
        if (_drag == DragMode.None || width <= 0)
        {
            return;
        }

        if (Math.Abs(x - _dragStartX) >= SystemParameters.MinimumHorizontalDragDistance)
        {
            _dragMoved = true;
        }

        switch (_drag)
        {
            case DragMode.TrimPending:
                if (_dragMoved)
                {
                    SetHandleArmed(true);
                    BeginTrimDrag(_pendingHandle);
                    MoveTrimDrag(ReferenceEquals(strip, _overview)
                        ? OverviewPxToMs(x, width)
                        : _viewport.PxToMs(x, width));
                }
                break;
            case DragMode.Playhead:
                bool onDetail = ReferenceEquals(strip, _detail);
                if (onDetail)
                {
                    SeekTo(_viewport.PxToMs(x, width), snap: true, followDetail: false);
                }
                else
                {
                    SeekFromOverview(OverviewPxToMs(x, width));
                }
                break;
            case DragMode.BrushBody:
                if (_dragMoved)
                {
                    double targetStart = OverviewPxToMs(x, width) - _dragAnchorMs;
                    _viewport.Pan(targetStart - _viewport.ViewStartMs);
                    InvalidateViewport();
                }
                break;
            case DragMode.BrushLeft:
                _viewport.SetView(OverviewPxToMs(x, width), _viewport.ViewEndMs);
                InvalidateViewport();
                break;
            case DragMode.BrushRight:
                _viewport.SetView(_viewport.ViewStartMs, OverviewPxToMs(x, width));
                InvalidateViewport();
                break;
            case DragMode.TrimIn:
            case DragMode.TrimOut:
                MoveTrimDrag(ReferenceEquals(strip, _overview)
                    ? OverviewPxToMs(x, width)
                    : _viewport.PxToMs(x, width));
                break;
        }
    }

    private void EndDrag(TimelineRenderSurface strip, MouseButtonEventArgs e)
    {
        TimelineRenderSurface pressed = _dragStrip ?? strip;
        ReleaseAt(e.GetPosition(pressed));
        if (pressed.IsMouseCaptured)
        {
            pressed.ReleaseMouseCapture();
        }

        e.Handled = true;
    }

    /// <summary>Pointer release in the pressed strip's coordinates; a press that never moved seeks.</summary>
    private void ReleaseAt(Point position)
    {
        TimelineRenderSurface strip = _dragStrip ?? _overview;
        DragMode completed = _drag;
        bool overview = ReferenceEquals(strip, _overview);
        bool coarseClick = completed == DragMode.BrushBody && !_dragMoved && overview;
        bool pendingClick = completed == DragMode.TrimPending;
        double width = strip.ActualWidth;

        // Finish the drag before capture is released, so LostMouseCapture does not report it twice.
        bool trim = completed is DragMode.TrimIn or DragMode.TrimOut;
        if (trim)
        {
            EndTrimDrag();
        }
        else
        {
            _drag = DragMode.None;
        }

        _dragStrip = null;
        if (!trim)
        {
            if ((coarseClick || pendingClick) && width > 0)
            {
                if (overview)
                {
                    SeekFromOverview(OverviewPxToMs(position.X, width));
                }
                else
                {
                    SeekTo(_viewport.PxToMs(position.X, width), snap: true, followDetail: false);
                }
            }

            if (completed == DragMode.Playhead || coarseClick || pendingClick)
            {
                PlayheadInteractionCompleted?.Invoke(this, _playheadMs);
            }
        }

        UpdateCursor(strip, position);
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_drag is DragMode.TrimIn or DragMode.TrimOut)
        {
            EndTrimDrag();
            return;
        }

        bool reconcile = _drag == DragMode.Playhead;
        _drag = DragMode.None;
        _dragStrip = null;
        if (reconcile)
        {
            PlayheadInteractionCompleted?.Invoke(this, _playheadMs);
        }
    }

    private void BeginTrimDrag(TrimHandle handle)
    {
        _activeTrimHandle = handle;
        _drag = handle == TrimHandle.In ? DragMode.TrimIn : DragMode.TrimOut;
        InvalidateRange();
        TrimInteractionStarted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the dragged handle and shows that frame: the playhead follows the handle.</summary>
    private void MoveTrimDrag(double ms)
    {
        double snapped = FrameStepCalculator.SnapToFrame(ms, _fps, _durationMs);
        if (_drag == DragMode.TrimOut)
        {
            SetOut(snapped);
            SeekTo(OutMs, snap: false, followDetail: false);
        }
        else
        {
            SetIn(snapped);
            SeekTo(InMs, snap: false, followDetail: false);
        }
    }

    private void EndTrimDrag()
    {
        if (_drag is not (DragMode.TrimIn or DragMode.TrimOut))
        {
            return;
        }

        _drag = DragMode.None;
        InvalidateRange();
        PlayheadInteractionCompleted?.Invoke(this, _playheadMs);
        TrimInteractionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCursor(TimelineRenderSurface strip, Point position)
    {
        double width = strip.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        double x = position.X;
        if (ReferenceEquals(strip, _overview))
        {
            strip.Cursor = HitTestOverview(x, position.Y, width) switch
            {
                DragMode.TrimIn or DragMode.TrimOut or DragMode.BrushLeft or DragMode.BrushRight => Cursors.SizeWE,
                DragMode.BrushBody => Cursors.SizeAll,
                _ => Cursors.Hand,
            };
            return;
        }

        bool onTrim = _trimModeEnabled
            && ((IsVisibleInDetail(_trim.InMs)
                    && Math.Abs(x - _viewport.MsToPx(_trim.InMs, width)) <= TrimHandleGrab)
                || (IsVisibleInDetail(_trim.OutMs)
                    && Math.Abs(x - _viewport.MsToPx(_trim.OutMs, width)) <= TrimHandleGrab));
        strip.Cursor = onTrim ? Cursors.SizeWE : Cursors.Cross;
    }

    private double OverviewPxToMs(double x, double width) =>
        Math.Clamp(x / width, 0, 1) * _durationMs;

    private double OverviewMsToPx(double ms, double width) =>
        Math.Clamp(ms / _durationMs, 0, 1) * width;

    private bool IsVisibleInDetail(double ms) =>
        ms >= _viewport.ViewStartMs - 0.001 && ms <= _viewport.ViewEndMs + 0.001;

    private double OverviewTrackBottom(double height) =>
        _detailVisible ? Math.Max(LabelLaneHeight + 8, height - ZoomLaneHeight) : height;

    private void SeekTo(double ms, bool snap, bool followDetail)
    {
        double clamped = Math.Clamp(ms, 0, _durationMs);
        if (snap)
        {
            clamped = FrameStepCalculator.SnapToFrame(clamped, _fps, _durationMs);
        }

        double beforeStart = _viewport.ViewStartMs;
        double beforeEnd = _viewport.ViewEndMs;
        _playheadMs = clamped;
        if (followDetail && !IsVisibleInDetail(_playheadMs))
        {
            CenterDetailOn(_playheadMs);
        }

        if (ViewportChanged(beforeStart, beforeEnd))
        {
            InvalidateViewport();
        }
        else
        {
            InvalidatePlayhead();
        }

        PlayheadChanged?.Invoke(this, _playheadMs);
    }

    /// <summary>
    /// Keyboard nudges for a handle the user took hold of. Everything else, Tab included, is
    /// left to the window: Tab keeps moving focus, ←/→ and Home/End move the playhead, and
    /// X (the Trim command) switches handles. Esc lets go of the handle.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Tunnelling key events only reach this control while focus is within it.
        if (!_trimModeEnabled || !_handleArmed)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            SetHandleArmed(false);
            e.Handled = true;
            return;
        }

        double frameMs = FrameStepCalculator.FrameDurationMs(_fps);
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? frameMs * 10 : frameMs;
        double current = _activeTrimHandle == TrimHandle.In ? _trim.InMs : _trim.OutMs;
        double target = e.Key switch
        {
            Key.Left => current - step,
            Key.Right => current + step,
            _ => double.NaN,
        };
        if (!double.IsFinite(target))
        {
            return;
        }

        target = FrameStepCalculator.SnapToFrame(target, _fps, _durationMs);
        if (_activeTrimHandle == TrimHandle.In)
        {
            SetIn(target);
            SetPlayhead(_trim.InMs);
        }
        else
        {
            SetOut(target);
            SetPlayhead(_trim.OutMs);
        }

        PlayheadChanged?.Invoke(this, _playheadMs);
        PlayheadInteractionCompleted?.Invoke(this, _playheadMs);
        e.Handled = true;
    }

    private void CenterDetailOn(double ms)
    {
        double targetStart = ms - (_viewport.VisibleSpanMs / 2.0);
        _viewport.Pan(targetStart - _viewport.ViewStartMs);
    }

    private void OnTrimRangeChanged()
    {
        UpdateTrimStatus();
        InvalidateRange();
        TrimChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- filmstrip thumbnails ----

    private void OnOverviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_filmstripPath is null || _disposed)
        {
            return;
        }

        if (_filmstrip is null)
        {
            // The first width the strip receives starts the first decode at once.
            ScheduleFilmstrip(immediate: true);
            return;
        }

        // A width that keeps the tile count only re-spaces the cached tiles (the surface
        // already redraws its static layer); a changed count waits for the resize to settle.
        FilmstripLayout? layout = PlanFilmstrip(e.NewSize.Width);
        if (layout is not null && !_filmstrip.Layout.SamplesSameFramesAs(layout))
        {
            ScheduleFilmstrip(immediate: false);
        }
    }

    private void OnFilmstripResampleTick(object? sender, EventArgs e)
    {
        _filmstripResample.Stop();
        EnsureFilmstrip();
    }

    private void ScheduleFilmstrip(bool immediate)
    {
        _filmstripResample.Stop();
        if (_filmstripPath is null || _disposed)
        {
            return;
        }

        if (immediate)
        {
            EnsureFilmstrip();
        }
        else
        {
            _filmstripResample.Start();
        }
    }

    /// <summary>Plans tiles as tall as the strip's track, so a thumbnail is decoded at the size it is drawn.</summary>
    private FilmstripLayout? PlanFilmstrip(double width)
    {
        double tileHeight = OverviewTrackBottom(_overview.Height) - LabelLaneHeight;
        return _filmstripPath is null || !double.IsFinite(width) || width <= 0 || tileHeight < 1
            ? null
            : FilmstripLayout.Create(
                _durationMs,
                _filmstripSourceWidth,
                _filmstripSourceHeight,
                width,
                tileHeight,
                _fps);
    }

    /// <summary>Starts a sampler for the current strip width unless the running one already decodes the same frames.</summary>
    private void EnsureFilmstrip()
    {
        if (_disposed || _filmstripPath is null || _filmstripLog is null)
        {
            return;
        }

        FilmstripLayout? layout = PlanFilmstrip(_overview.ActualWidth);
        if (layout is null || _filmstrip?.Layout.SamplesSameFramesAs(layout) == true)
        {
            return;
        }

        // The previous tiles stay on screen, re-spaced to the new width, until the new
        // decode publishes: a resize never blanks the strip.
        TimelineFilmstrip? previous = _filmstrip;
        previous?.Dispose();
        if (previous is { PublishedCount: > 0 })
        {
            _filmstripFallback?.Dispose();
            _filmstripFallback = previous;
        }

        _filmstrip = TimelineFilmstrip.Start(
            _filmstripPath,
            layout,
            VisualTreeHelper.GetDpi(this),
            Dispatcher,
            OnFilmstripTilesPublished,
            _filmstripLog);
        _overview.InvalidateLayers(TimelineRenderLayer.Static);
    }

    private void OnFilmstripTilesPublished(TimelineFilmstrip sender)
    {
        // A sampler cancelled by a resize may still report; only the current one redraws.
        if (!_disposed && ReferenceEquals(sender, _filmstrip))
        {
            _filmstripFallback?.Dispose();
            _filmstripFallback = null;
            _overview.InvalidateLayers(TimelineRenderLayer.Static);
        }
    }

    private void ClearFilmstrip()
    {
        _filmstripResample.Stop();
        _filmstripPath = null;
        _filmstrip?.Dispose();
        _filmstrip = null;
        _filmstripFallback?.Dispose();
        _filmstripFallback = null;
        _filmstripSlots = FilmstripSlotCache.Empty;
    }

    /// <summary>
    /// Draws the published thumbnails edge to edge under the ticks. Each tile covers its share
    /// of the strip at the source aspect and is clipped to it, so a strip whose width changed
    /// since the decode crops a little instead of stretching the frames.
    /// </summary>
    private void DrawFilmstrip(DrawingContext dc, double width, double top, double bottom)
    {
        TimelineFilmstrip? filmstrip = _filmstrip;
        if (filmstrip is null || width <= 0 || bottom - top <= 0)
        {
            return;
        }

        int published = filmstrip.PublishedCount;
        if (published == 0 && _filmstripFallback is { PublishedCount: > 0 } fallback)
        {
            filmstrip = fallback;
            published = fallback.PublishedCount;
        }

        if (published == 0)
        {
            return;
        }

        if (!_filmstripSlots.Matches(filmstrip.Layout, width, top, bottom))
        {
            _filmstripSlots = FilmstripSlotCache.Build(filmstrip.Layout, width, top, bottom);
        }

        FilmstripSlotCache slots = _filmstripSlots;
        dc.PushClip(slots.Track);
        for (int index = 0; index < published; index++)
        {
            BitmapSource? tile = filmstrip.Tile(index);
            if (tile is null)
            {
                continue;
            }

            RectangleGeometry? clip = slots.Clips[index];
            if (clip is not null)
            {
                dc.PushClip(clip);
            }

            dc.DrawImage(tile, slots.Images[index]);
            if (clip is not null)
            {
                dc.Pop();
            }
        }

        dc.Pop();
    }

    /// <summary>
    /// The clip and image rectangles of every thumbnail for one strip width and track. Built
    /// when the static layer first draws a new size, never per pointer event.
    /// </summary>
    private sealed class FilmstripSlotCache
    {
        internal static readonly FilmstripSlotCache Empty = new(null, 0, 0, 0, Frozen(new RectangleGeometry()), [], []);

        private readonly FilmstripLayout? _layout;
        private readonly double _width;
        private readonly double _top;
        private readonly double _bottom;

        private FilmstripSlotCache(
            FilmstripLayout? layout,
            double width,
            double top,
            double bottom,
            RectangleGeometry track,
            Rect[] images,
            RectangleGeometry?[] clips)
        {
            _layout = layout;
            _width = width;
            _top = top;
            _bottom = bottom;
            Track = track;
            Images = images;
            Clips = clips;
        }

        internal RectangleGeometry Track { get; }

        internal Rect[] Images { get; }

        /// <summary>Per tile: a frozen clip when its image overflows its slot, otherwise null.</summary>
        internal RectangleGeometry?[] Clips { get; }

        internal bool Matches(FilmstripLayout layout, double width, double top, double bottom) =>
            ReferenceEquals(_layout, layout) && _width == width && _top == top && _bottom == bottom;

        internal static FilmstripSlotCache Build(FilmstripLayout layout, double width, double top, double bottom)
        {
            int count = layout.TileCount;
            double height = bottom - top;
            var images = new Rect[count];
            var clips = new RectangleGeometry?[count];
            for (int index = 0; index < count; index++)
            {
                double left = index * width / count;
                double right = index == count - 1 ? width : (index + 1) * width / count;
                var slot = new Rect(left, top, right - left, height);
                // Cover the slot at the tile aspect: the overflow on one axis is clipped away.
                double scale = Math.Max(slot.Width / layout.TileWidth, height / layout.TileHeight);
                double imageWidth = layout.TileWidth * scale;
                double imageHeight = layout.TileHeight * scale;
                images[index] = new Rect(
                    slot.X + ((slot.Width - imageWidth) / 2),
                    slot.Y + ((height - imageHeight) / 2),
                    imageWidth,
                    imageHeight);
                if (imageWidth > slot.Width + 0.01 || imageHeight > height + 0.01)
                {
                    clips[index] = Frozen(new RectangleGeometry(slot));
                }
            }

            return new FilmstripSlotCache(
                layout,
                width,
                top,
                bottom,
                Frozen(new RectangleGeometry(new Rect(0, top, width, height))),
                images,
                clips);
        }

        private static RectangleGeometry Frozen(RectangleGeometry geometry)
        {
            geometry.Freeze();
            return geometry;
        }
    }

    // ---- dirty-layer scheduling ----

    private void InvalidateAll()
    {
        _overview.InvalidateLayers(TimelineRenderLayer.All);
        _connector.InvalidateLayers(TimelineRenderLayer.All);
        _detail.InvalidateLayers(TimelineRenderLayer.All);
    }

    private void InvalidateViewport()
    {
        UpdateRangeCaptions();
        _overview.InvalidateLayers(TimelineRenderLayer.Range);
        _connector.InvalidateLayers(TimelineRenderLayer.Range);
        _detail.InvalidateLayers(TimelineRenderLayer.All);
    }

    private void InvalidateRange()
    {
        _overview.InvalidateLayers(TimelineRenderLayer.Range);
        _detail.InvalidateLayers(TimelineRenderLayer.Range);
    }

    private void InvalidatePlayhead()
    {
        _overview.InvalidateLayers(TimelineRenderLayer.Transient);
        _detail.InvalidateLayers(TimelineRenderLayer.Transient);
    }

    private bool ViewportChanged(double beforeStart, double beforeEnd) =>
        Math.Abs(beforeStart - _viewport.ViewStartMs) > 0.001
        || Math.Abs(beforeEnd - _viewport.ViewEndMs) > 0.001;

    private void OnLayersInvalidated(object? sender, EventArgs e)
    {
        if (IsLoaded)
        {
            _renderScheduler.Request();
        }
        else
        {
            // Headless STA tests and pre-load layout still need deterministic visuals.
            RenderDirtyLayers();
        }
    }

    private void RequestRenderIfDirty()
    {
        if (_overview.DirtyLayers != TimelineRenderLayer.None
            || _connector.DirtyLayers != TimelineRenderLayer.None
            || _detail.DirtyLayers != TimelineRenderLayer.None)
        {
            _renderScheduler.Request();
        }
    }

    private void RenderDirtyLayers()
    {
        _overview.RenderDirtyLayers();
        _connector.RenderDirtyLayers();
        _detail.RenderDirtyLayers();
    }

    private void UpdateRangeCaptions()
    {
        double frameMs = FrameStepCalculator.FrameDurationMs(_fps);
        int visibleFrames = frameMs > 0
            ? Math.Max(1, (int)Math.Ceiling(_viewport.VisibleSpanMs / frameMs))
            : 1;

        _overviewCaption.Text = UiText.Format("Text_4667ECA4EE5B", FormatMs(0), FormatMs(_durationMs), FormatMs(CoarseIntervalMs));
        _detailCaption.Text = UiText.Format("Text_162F0703007C", FormatMs(_viewport.ViewStartMs), FormatMs(_viewport.ViewEndMs), FormatMs(_viewport.VisibleSpanMs), visibleFrames);

        AutomationProperties.SetName(_detail, _detailCaption.Text);
    }

    private void UpdateTrimStatus() =>
        AutomationProperties.SetItemStatus(_overview, TrimPresentation.TimelineStatus(_trim.InMs, _trim.OutMs));

    private void UpdateOverviewHelp()
    {
        string help = _trimModeEnabled
            ? UiText.Get("VideoEditorUx_TrimTimelineHelp")
            : UiText.Get("VideoEditorUx_TrimOffHelp");
        if (_detailVisible)
        {
            help += " " + UiText.Get("VideoEditorUx_ZoomLaneHelp");
        }

        AutomationProperties.SetHelpText(_overview, help);
        AutomationProperties.SetHelpText(this, help);
    }

    // ---- DrawingVisual layers ----

    private void DrawOverviewLayer(DrawingContext dc, TimelineRenderLayer layer, Size size)
    {
        double width = size.Width;
        double height = size.Height;
        double trackTop = LabelLaneHeight;
        double trackBottom = OverviewTrackBottom(height);
        switch (layer)
        {
            case TimelineRenderLayer.Static:
                dc.DrawRectangle(_surfaceSunken, null, new Rect(size));
                DrawLine(dc, _laneSeparatorPen, 0, trackTop - 0.5, width, trackTop - 0.5);
                DrawFilmstrip(dc, width, trackTop, trackBottom);
                double majorMs = CoarseIntervalMs;
                double minorMs = majorMs / 4.0;
                int minorCount = Math.Max(1, (int)Math.Ceiling(_durationMs / minorMs));
                double labelRight = double.NegativeInfinity;
                for (int index = 0; index <= minorCount; index++)
                {
                    double time = Math.Min(_durationMs, index * minorMs);
                    double x = OverviewMsToPx(time, width);
                    bool major = index % 4 == 0;
                    // Over the thumbnails a major tick spans the track and a minor one only
                    // marks the bottom edge; both are translucent hairlines.
                    DrawLine(dc, major ? _overviewMajorTickPen : _overviewMinorTickPen, x,
                        major ? trackTop : trackBottom - 6, x, trackBottom);
                    if (major)
                    {
                        // A ruler along the top lane; the handle chips draw over it. A label
                        // that would run into the previous one (the clip end) is left out.
                        FormattedText label = Format(FormatTickLabel(time), 10, semiBold: false, _textSecondary);
                        double left = Math.Clamp(x + 3, 3, Math.Max(3, width - label.Width - 3));
                        if (left >= labelRight + 6)
                        {
                            dc.DrawText(label, new Point(left, 2));
                            labelRight = left + label.Width;
                        }
                    }
                }
                break;

            case TimelineRenderLayer.Range:
                if (_detailVisible)
                {
                    DrawZoomLane(dc, width, trackBottom, height);
                }

                double trimInX = OverviewMsToPx(_trim.InMs, width);
                double trimOutX = OverviewMsToPx(_trim.OutMs, width);
                DrawCutRange(dc, 0, trimInX, trackTop, trackBottom);
                DrawCutRange(dc, trimOutX, width, trackTop, trackBottom);

                // The kept range reads as one bracket: accent bars along its top and bottom.
                DrawRect(dc, trimInX, trackTop, trimOutX - trimInX, 3, _handleFill, null);
                DrawRect(dc, trimInX, trackBottom - 3, trimOutX - trimInX, 3, _handleFill, null);
                if (_trimModeEnabled)
                {
                    ChipLayout chips = LayoutChips(width);
                    DrawTrimHandle(dc, trimInX, chips.In.Top, trackBottom, TrimHandle.In);
                    DrawTrimHandle(dc, trimOutX, chips.Out.Top, trackBottom, TrimHandle.Out);
                    DrawChip(dc, chips.In, chips.InText, TrimHandle.In);
                    DrawChip(dc, chips.Out, chips.OutText, TrimHandle.Out);
                }
                break;

            case TimelineRenderLayer.Transient:
                DrawPlayhead(dc, OverviewMsToPx(_playheadMs, width), trackTop, height);
                break;
        }
    }

    private void DrawZoomLane(DrawingContext dc, double width, double top, double bottom)
    {
        double left = OverviewMsToPx(_viewport.ViewStartMs, width);
        double right = OverviewMsToPx(_viewport.ViewEndMs, width);
        double brushWidth = Math.Max(3, right - left);
        double laneHeight = bottom - top;
        DrawRect(dc, left, top + 1, brushWidth, laneHeight - 1, _accentSubtle, _accentOutlinePen);
        double gripWidth = Math.Min(BrushGripWidth, Math.Max(3, brushWidth / 2.0));
        DrawRect(dc, left, top + 1, gripWidth, laneHeight - 1, _accent, null);
        DrawRect(dc, left + brushWidth - gripWidth, top + 1, gripWidth, laneHeight - 1, _accent, null);
    }

    private void DrawConnectorLayer(DrawingContext dc, TimelineRenderLayer layer, Size size)
    {
        if (layer != TimelineRenderLayer.Range)
        {
            return;
        }

        double width = size.Width;
        double left = OverviewMsToPx(_viewport.ViewStartMs, width);
        double right = OverviewMsToPx(_viewport.ViewEndMs, width);
        DrawLine(dc, _connectorPen, left, 0, 1, ConnectorHeight);
        DrawLine(dc, _connectorPen, right, 0, width - 1, ConnectorHeight);

        if (width >= 360)
        {
            DrawText(dc, UiText.Get("Text_41F81C272691"), width / 2.0, 0,
                _textSecondary, 10.5, semiBold: true, _surfaceBase, centered: true);
        }
    }

    private void DrawDetailLayer(DrawingContext dc, TimelineRenderLayer layer, Size size)
    {
        double width = size.Width;
        double height = size.Height;
        switch (layer)
        {
            case TimelineRenderLayer.Static:
                dc.DrawRectangle(_surfaceSunken, null, new Rect(size));
                double frameMs = FrameStepCalculator.FrameDurationMs(_fps);
                double framePx = _viewport.VisibleSpanMs > 0 ? frameMs / _viewport.VisibleSpanMs * width : 0;
                if (framePx >= 2.0)
                {
                    long firstFrame = (long)Math.Ceiling(_viewport.ViewStartMs / frameMs);
                    long lastFrame = (long)Math.Floor(_viewport.ViewEndMs / frameMs);
                    for (long frame = firstFrame; frame <= lastFrame; frame++)
                    {
                        double time = frame * frameMs;
                        double x = _viewport.MsToPx(time, width);
                        bool group = frame % 5 == 0;
                        DrawLine(dc, group ? _detailGroupPen : _minorTickPen, x,
                            group ? height * 0.48 : height * 0.67, x, height);
                    }
                }
                else
                {
                    double step = Math.Max(frameMs, _viewport.VisibleSpanMs / 36.0);
                    for (double time = Math.Ceiling(_viewport.ViewStartMs / step) * step;
                         time <= _viewport.ViewEndMs;
                         time += step)
                    {
                        double x = _viewport.MsToPx(time, width);
                        DrawLine(dc, _minorTickPen, x, height * 0.68, x, height);
                    }

                    DrawText(dc, UiText.Get("Text_D9483E35CD83"), width / 2.0,
                        height * 0.36, _textSecondary, 11, semiBold: true, _surfaceScrim, centered: true);
                }

                double majorMs = CoarseIntervalMs;
                double firstMajor = Math.Ceiling(_viewport.ViewStartMs / majorMs) * majorMs;
                for (double time = firstMajor; time <= _viewport.ViewEndMs + 0.001; time += majorMs)
                {
                    double x = _viewport.MsToPx(time, width);
                    DrawLine(dc, _majorTickPen, x, 0, x, height);
                    DrawText(dc, FormatTickLabel(time),
                        Math.Clamp(x + 4, 3, Math.Max(3, width - 54)), 2,
                        _textSecondary, 11, semiBold: true, _surfaceScrim, centered: false);
                }
                break;

            case TimelineRenderLayer.Range:
                double visibleIn = Math.Max(_trim.InMs, _viewport.ViewStartMs);
                double visibleOut = Math.Min(_trim.OutMs, _viewport.ViewEndMs);
                if (visibleOut > visibleIn)
                {
                    double keptX = _viewport.MsToPx(visibleIn, width);
                    double keptRight = _viewport.MsToPx(visibleOut, width);
                    DrawRect(dc, keptX, 0, keptRight - keptX, 3, _handleFill, null);
                    DrawRect(dc, keptX, height - 3, keptRight - keptX, 3, _handleFill, null);
                }

                if (_trim.InMs > _viewport.ViewStartMs)
                {
                    double cutRight = _viewport.MsToPx(
                        Math.Min(_trim.InMs, _viewport.ViewEndMs),
                        width);
                    DrawCutRange(dc, 0, cutRight, 0, height);
                }

                if (_trim.OutMs < _viewport.ViewEndMs)
                {
                    double cutLeft = _viewport.MsToPx(
                        Math.Max(_trim.OutMs, _viewport.ViewStartMs),
                        width);
                    DrawCutRange(dc, cutLeft, width, 0, height);
                }

                if (_trimModeEnabled && IsVisibleInDetail(_trim.InMs))
                {
                    DrawTrimHandle(dc, _viewport.MsToPx(_trim.InMs, width), 0, height, TrimHandle.In);
                }

                if (_trimModeEnabled && IsVisibleInDetail(_trim.OutMs))
                {
                    DrawTrimHandle(dc, _viewport.MsToPx(_trim.OutMs, width), 0, height, TrimHandle.Out);
                }
                break;

            case TimelineRenderLayer.Transient:
                if (IsVisibleInDetail(_playheadMs))
                {
                    DrawPlayhead(dc, _viewport.MsToPx(_playheadMs, width), 0, height);
                }
                break;
        }
    }

    private void DrawPlayhead(DrawingContext dc, double x, double top, double height)
    {
        DrawLine(dc, _playheadPen, x, top, x, height);
        dc.PushTransform(new TranslateTransform(x, top));
        dc.DrawGeometry(_playhead, null, _playheadMarker);
        dc.Pop();
    }

    /// <summary>A part that saving or exporting removes: dark, hatched and labelled.</summary>
    private void DrawCutRange(DrawingContext dc, double left, double right, double top, double bottom)
    {
        double width = right - left;
        double height = bottom - top;
        if (width <= 0.5 || height <= 0)
        {
            return;
        }

        var bounds = new Rect(left, top, width, height);
        dc.DrawRectangle(_cutFill, null, bounds);
        dc.PushClip(new RectangleGeometry(bounds));
        const double hatchStep = 9;
        for (double x = left - height; x < right; x += hatchStep)
        {
            dc.DrawLine(_cutHatchPen, new Point(x, bottom), new Point(x + height, top));
        }

        dc.Pop();
        DrawLine(dc, _cutEdgePen, left, top + 0.5, right, top + 0.5);
        DrawLine(dc, _cutEdgePen, left, bottom - 0.5, right, bottom - 0.5);

        string label = UiText.Get("VideoEditorUx_TrimCut");
        FormattedText text = Format(label, 11, semiBold: true, _textPrimary);
        if (width >= text.Width + 16 && height >= text.Height)
        {
            DrawText(dc, label, left + (width / 2), top + Math.Max(0, (height - text.Height - 2) / 2),
                _textPrimary, 11, semiBold: true, _surfaceBase, centered: true);
        }
    }

    private void DrawTrimHandle(DrawingContext dc, double x, double top, double bottom, TrimHandle handle)
    {
        var bounds = new Rect(x - (TrimHandleWidth / 2), top, TrimHandleWidth, Math.Max(1, bottom - top));
        dc.DrawRoundedRectangle(_handleFill, null, bounds, 3, 3);
        double middle = top + ((bottom - top) / 2);
        DrawLine(dc, _gripMarkPen, x - 2.5, middle - 6, x - 2.5, middle + 6);
        DrawLine(dc, _gripMarkPen, x + 2.5, middle - 6, x + 2.5, middle + 6);
        if (ShowsFocusOn(handle))
        {
            Rect ring = bounds;
            ring.Inflate(3, 0);
            dc.DrawRoundedRectangle(null, _focusPen, ring, 4, 4);
        }
    }

    private void DrawChip(DrawingContext dc, Rect bounds, FormattedText text, TrimHandle handle)
    {
        dc.DrawRoundedRectangle(_handleFill, null, bounds, 3, 3);
        dc.DrawText(text, new Point(bounds.Left + ((bounds.Width - text.Width) / 2), bounds.Top + ((bounds.Height - text.Height) / 2)));
        if (ShowsFocusOn(handle))
        {
            Rect ring = bounds;
            ring.Inflate(2, 1);
            dc.DrawRoundedRectangle(null, _focusPen, ring, 4, 4);
        }
    }

    private bool ShowsFocusOn(TrimHandle handle) =>
        _activeTrimHandle == handle && _handleArmed && (IsKeyboardFocusWithin || IsTrimDragging);

    private readonly record struct ChipLayout(Rect In, Rect Out, FormattedText InText, FormattedText OutText);

    /// <summary>
    /// Places the "start"/"end" time chips on the outer edge of their handles, pushing them
    /// apart when a short kept range would make them overlap.
    /// </summary>
    private ChipLayout LayoutChips(double width)
    {
        FormattedText inText = Format(TrimPresentation.StartChip(_trim.InMs), 11.5, semiBold: true, _handleText);
        FormattedText outText = Format(TrimPresentation.EndChip(_trim.OutMs), 11.5, semiBold: true, _handleText);
        const double gap = 4;
        double height = LabelLaneHeight - 1;
        double inWidth = inText.Width + 12;
        double outWidth = outText.Width + 12;
        double inLeft = OverviewMsToPx(_trim.InMs, width) - (TrimHandleWidth / 2);
        double outLeft = OverviewMsToPx(_trim.OutMs, width) + (TrimHandleWidth / 2) - outWidth;
        double overlap = inLeft + inWidth + gap - outLeft;
        if (overlap > 0)
        {
            inLeft -= overlap / 2;
            outLeft += overlap / 2;
        }

        inLeft = Math.Clamp(inLeft, 0, Math.Max(0, width - inWidth));
        outLeft = Math.Clamp(outLeft, 0, Math.Max(0, width - outWidth));
        if (inLeft + inWidth + gap > outLeft)
        {
            if (inLeft <= 0)
            {
                outLeft = Math.Min(Math.Max(0, width - outWidth), inWidth + gap);
            }
            else
            {
                inLeft = Math.Max(0, outLeft - gap - inWidth);
            }
        }

        return new ChipLayout(
            new Rect(inLeft, 0.5, inWidth, height),
            new Rect(outLeft, 0.5, outWidth, height),
            inText,
            outText);
    }

    private static void DrawLine(
        DrawingContext dc,
        Pen pen,
        double x1,
        double y1,
        double x2,
        double y2) =>
        dc.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));

    private static void DrawRect(
        DrawingContext dc,
        double x,
        double y,
        double width,
        double height,
        Brush? fill,
        Pen? pen)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        dc.DrawRectangle(fill, pen, new Rect(x, y, width, height));
    }

    private FormattedText Format(string text, double fontSize, bool semiBold, Brush foreground) =>
        new(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            semiBold ? _semiBoldTypeface : _regularTypeface,
            fontSize,
            foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawText(
        DrawingContext dc,
        string text,
        double x,
        double y,
        Brush foreground,
        double fontSize,
        bool semiBold,
        Brush? background,
        bool centered)
    {
        FormattedText formatted = Format(text, fontSize, semiBold, foreground);
        double paddingX = background is null ? 0 : 5;
        double paddingY = background is null ? 0 : 1;
        double left = centered
            ? Math.Max(2, x - ((formatted.Width + (paddingX * 2)) / 2.0))
            : x;
        if (background is not null)
        {
            dc.DrawRectangle(
                background,
                null,
                new Rect(left, y, formatted.Width + (paddingX * 2), formatted.Height + (paddingY * 2)));
        }

        dc.DrawText(formatted, new Point(left + paddingX, y + paddingY));
    }

    private double TickStepSeconds()
    {
        // Roughly twenty heavy overview intervals. One interval becomes the initial detail view.
        double target = Math.Max(0.001, _durationMs / 1000.0 / 20.0);
        double[] steps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600];
        foreach (double step in steps)
        {
            if (step >= target)
            {
                return step;
            }
        }

        return 1200;
    }

    private StackPanel Labelled(TextBlock caption, TimelineRenderSurface strip, string automationName)
    {
        var panel = new StackPanel();
        panel.Children.Add(caption);
        var host = new Border
        {
            BorderBrush = _borderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = strip,
        };
        AutomationProperties.SetName(strip, automationName);
        panel.Children.Add(host);
        return panel;
    }

    private TextBlock BuildCaption()
    {
        var caption = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 5),
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        return caption;
    }

    private static string FormatMs(double ms) => VideoTimecode.Format(ms);

    private static string FormatTickLabel(double ms) => VideoTimecode.FormatTick(ms);

    private static Brush ResolveBrush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? FrozenBrush(fallback);

    private static Brush WithOpacity(Brush brush, double opacity)
    {
        Brush copy = brush.CloneCurrentValue();
        copy.Opacity = opacity;
        if (copy.CanFreeze)
        {
            copy.Freeze();
        }

        return copy;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        return pen;
    }

    private static StreamGeometry BuildPlayheadMarker()
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(-7, 0), isFilled: true, isClosed: true);
            context.LineTo(new Point(7, 0), isStroked: true, isSmoothJoin: false);
            context.LineTo(new Point(0, 10), isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearFilmstrip();
        AttachHostWindow(null);
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        _filmstripResample.Tick -= OnFilmstripResampleTick;
        _overview.SizeChanged -= OnOverviewSizeChanged;
        _overview.LayersInvalidated -= OnLayersInvalidated;
        _connector.LayersInvalidated -= OnLayersInvalidated;
        _detail.LayersInvalidated -= OnLayersInvalidated;
        PreviewKeyDown -= OnPreviewKeyDown;
        _renderScheduler.Dispose();
    }
}
