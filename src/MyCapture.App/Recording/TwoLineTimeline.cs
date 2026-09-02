using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// A two-line "overview + detail" timeline. The top strip always shows the complete clip
/// with coarse, visually heavy index marks and a draggable viewport. The bottom strip expands
/// exactly that viewport to its full width and exposes the frames between those coarse marks.
/// </summary>
/// <remarks>
/// All time/pixel maths is delegated to <see cref="TimelineViewport"/>,
/// <see cref="TrimSelection"/> and <see cref="FrameStepCalculator"/>. Rendering uses three
/// fixed DrawingVisual layers per strip; pointer input only mutates state and marks layers
/// dirty, and loaded controls draw at most once on the next composition frame.
/// </remarks>
internal sealed class TwoLineTimeline : ContentControl, IDisposable
{
    private const double OverviewHeight = 58.0;
    private const double DetailHeight = 64.0;
    private const double ConnectorHeight = 15.0;
    private const double EdgeGrab = 14.0;
    private const double BrushGripWidth = 12.0;
    private const double TrimHandleWidth = 18.0;

    private readonly TimelineRenderSurface _overview;
    private readonly TimelineRenderSurface _connector;
    private readonly TimelineRenderSurface _detail;
    private readonly TextBlock _overviewCaption;
    private readonly TextBlock _detailCaption;
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
    private readonly Brush _outside;
    private readonly Brush _deleteFill;
    private readonly Brush _deleteHandle;
    private readonly Pen _majorTickPen;
    private readonly Pen _minorTickPen;
    private readonly Pen _detailGroupPen;
    private readonly Pen _accentOutlinePen;
    private readonly Pen _connectorPen;
    private readonly Pen _playheadPen;
    private readonly Pen _gripMarkPen;
    private readonly Pen _deleteOutlinePen;
    private readonly Pen _deleteHatchPen;
    private readonly Typeface _regularTypeface;
    private readonly Typeface _semiBoldTypeface;
    private readonly StreamGeometry _playheadMarker;

    private int _fps = 15;
    private double _durationMs = 1;
    private double _playheadMs;
    private TimelineViewport _viewport = new(1, 15);
    private TrimSelection _trim = new(1);
    private bool _trimModeEnabled;
    private TrimHandle _activeTrimHandle = TrimHandle.In;

    private DragMode _drag = DragMode.None;
    private double _dragAnchorMs;
    private double _dragStartX;
    private bool _dragMoved;

    private enum DragMode { None, Playhead, BrushBody, BrushLeft, BrushRight, TrimIn, TrimOut }

    private enum TrimHandle { In, Out }

    internal TwoLineTimeline()
    {
        Focusable = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        _surfaceSunken = ResolveBrush("Surface.Sunken", Color.FromRgb(0x1B, 0x17, 0x12));
        _surfaceBase = ResolveBrush("Surface.Base", Color.FromRgb(0x1B, 0x17, 0x12));
        _surfaceScrim = ResolveBrush("Surface.Scrim", Color.FromArgb(0xB8, 0x00, 0x00, 0x00));
        _textPrimary = ResolveBrush("Text.Primary", Colors.White);
        _textSecondary = ResolveBrush("Text.Secondary", Colors.LightGray);
        _textMuted = ResolveBrush("Text.Muted", Colors.Gray);
        _borderSubtle = ResolveBrush("Border.Subtle", Colors.DimGray);
        _accent = ResolveBrush("Accent.Default", Color.FromRgb(0xE0, 0xA8, 0x20));
        _accentSubtle = ResolveBrush("Accent.Subtle", Color.FromArgb(0x50, 0xFF, 0xD4, 0x00));
        _playhead = ResolveBrush("Accent.Cool", Color.FromRgb(0x66, 0xB7, 0xFF));
        _outside = FrozenBrush(Color.FromArgb(0x86, 0x00, 0x00, 0x00));
        _deleteFill = FrozenBrush(Color.FromArgb(0x66, 0xD8, 0x3B, 0x77));
        _deleteHandle = FrozenBrush(Color.FromRgb(0xE4, 0x4C, 0x83));

        _majorTickPen = CreatePen(_textSecondary, 3.0);
        _minorTickPen = CreatePen(_borderSubtle, 1.0);
        _detailGroupPen = CreatePen(_textMuted, 1.6);
        _accentOutlinePen = CreatePen(_accent, 3.0);
        _connectorPen = CreatePen(_accent, 2.0);
        _playheadPen = CreatePen(_playhead, 2.6);
        _gripMarkPen = CreatePen(_surfaceSunken, 2.0);
        _deleteOutlinePen = CreatePen(_deleteHandle, 2.0);
        _deleteHatchPen = CreatePen(FrozenBrush(Color.FromArgb(0xB0, 0xFF, 0xCD, 0xDE)), 1.2);

        FontFamily uiFont = Application.Current?.TryFindResource("Font.Ui") as FontFamily
            ?? new FontFamily("Segoe UI");
        _regularTypeface = new Typeface(uiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _semiBoldTypeface = new Typeface(uiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        _playheadMarker = BuildPlayheadMarker();

        _renderScheduler = new CompositionFrameScheduler(Dispatcher, RenderDirtyLayers);
        _overview = new TimelineRenderSurface(OverviewHeight, DrawOverviewLayer);
        _connector = new TimelineRenderSurface(ConnectorHeight, DrawConnectorLayer) { IsHitTestVisible = false };
        _detail = new TimelineRenderSurface(DetailHeight, DrawDetailLayer);
        _overview.LayersInvalidated += OnLayersInvalidated;
        _connector.LayersInvalidated += OnLayersInvalidated;
        _detail.LayersInvalidated += OnLayersInvalidated;

        _overviewCaption = BuildCaption();
        _detailCaption = BuildCaption();

        var panel = new StackPanel();
        panel.Children.Add(Labelled(_overviewCaption, _overview, "전체 타임라인"));
        panel.Children.Add(_connector);
        panel.Children.Add(Labelled(_detailCaption, _detail, "세부 프레임 타임라인"));
        Content = panel;

        AutomationProperties.SetHelpText(
            _overview,
            "클립 전체입니다. 굵은 눈금 한 구간이 아래에 확대됩니다. 선택 몸통을 끌면 이동하고 큰 양 끝을 끌면 확대 범위를 조정합니다.");
        AutomationProperties.SetHelpText(
            _detail,
            "상단 선택 구간의 시작과 끝을 전체 폭으로 확대한 프레임 타임라인입니다. 클릭과 트림은 프레임에 맞춰지고 휠로 확대합니다.");

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
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1.0 : 0.5;
        Loaded += (_, _) => RequestRenderIfDirty();
        Unloaded += (_, _) => _renderScheduler.CancelPending();
    }

    /// <summary>Raised for immediate visual intent while the user moves the playhead.</summary>
    internal event EventHandler<double>? PlayheadChanged;

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
        _trimModeEnabled = false;
        _activeTrimHandle = TrimHandle.In;

        // The two strips must look different immediately. Select one coarse overview interval
        // by default so the bottom line visibly expands the frames between two heavy marks.
        double initialDetailSpan = Math.Min(_durationMs, CoarseIntervalMs);
        if (initialDetailSpan < _durationMs - 0.001)
        {
            _viewport.SetView(0, initialDetailSpan);
        }

        UpdateRangeCaptions();
        InvalidateAll();
    }

    /// <summary>Externally sets the playhead and keeps it inside the detail viewport.</summary>
    internal void SetPlayhead(double ms, bool ensureVisible = true)
    {
        _playheadMs = _trim.ClampToSelection(Math.Clamp(ms, 0, _durationMs));
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
        ClampPlayheadAfterTrim();
        InvalidateRange();
        TrimChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void SetOut(double ms)
    {
        _activeTrimHandle = TrimHandle.Out;
        _trim.SetOut(ms);
        ClampPlayheadAfterTrim();
        InvalidateRange();
        TrimChanged?.Invoke(this, EventArgs.Empty);
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

        AutomationProperties.SetHelpText(
            this,
            enabled
                ? "자르기 모드입니다. 자홍색 시작/끝 삭제 핸들을 드래그하거나 방향키로 조정합니다. Tab은 조정할 핸들을 바꿉니다."
                : "자르기 버튼을 누르면 영상 양끝의 삭제 범위를 조정할 수 있습니다.");
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

    // ---- interaction ----

    private void OnDetailWheel(object sender, MouseWheelEventArgs e)
    {
        double centre = _viewport.PxToMs(e.GetPosition(_detail).X, _detail.ActualWidth);
        _viewport.Zoom(centre, e.Delta > 0 ? 0.8 : 1.25);
        InvalidateViewport();
        e.Handled = true;
    }

    private void OnStripDown(TimelineRenderSurface strip, MouseButtonEventArgs e, bool isOverview)
    {
        double x = e.GetPosition(strip).X;
        double width = strip.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        _dragStartX = x;
        _dragMoved = false;
        if (_trimModeEnabled)
        {
            _ = Focus();
        }

        if (isOverview)
        {
            double trimInPx = OverviewMsToPx(_trim.InMs, width);
            double trimOutPx = OverviewMsToPx(_trim.OutMs, width);
            double leftPx = OverviewMsToPx(_viewport.ViewStartMs, width);
            double rightPx = OverviewMsToPx(_viewport.ViewEndMs, width);
            if (_trimModeEnabled && Math.Abs(x - trimInPx) <= EdgeGrab + TrimHandleWidth / 2)
            {
                _activeTrimHandle = TrimHandle.In;
                _drag = DragMode.TrimIn;
            }
            else if (_trimModeEnabled && Math.Abs(x - trimOutPx) <= EdgeGrab + TrimHandleWidth / 2)
            {
                _activeTrimHandle = TrimHandle.Out;
                _drag = DragMode.TrimOut;
            }
            else if (Math.Abs(x - leftPx) <= EdgeGrab)
            {
                _drag = DragMode.BrushLeft;
            }
            else if (Math.Abs(x - rightPx) <= EdgeGrab)
            {
                _drag = DragMode.BrushRight;
            }
            else if (x > leftPx && x < rightPx)
            {
                _drag = DragMode.BrushBody;
                _dragAnchorMs = OverviewPxToMs(x, width) - _viewport.ViewStartMs;
            }
            else
            {
                // Seeking outside the selected upper range also moves the lower detail range
                // to that location. Otherwise the shared playhead disappears from the detail.
                SeekFromOverview(OverviewPxToMs(x, width));
                _drag = DragMode.Playhead;
            }
        }
        else
        {
            bool inVisible = IsVisibleInDetail(_trim.InMs);
            bool outVisible = IsVisibleInDetail(_trim.OutMs);
            double inPx = _viewport.MsToPx(_trim.InMs, width);
            double outPx = _viewport.MsToPx(_trim.OutMs, width);
            if (_trimModeEnabled && inVisible && Math.Abs(x - inPx) <= EdgeGrab + TrimHandleWidth / 2)
            {
                _activeTrimHandle = TrimHandle.In;
                _drag = DragMode.TrimIn;
            }
            else if (_trimModeEnabled && outVisible && Math.Abs(x - outPx) <= EdgeGrab + TrimHandleWidth / 2)
            {
                _activeTrimHandle = TrimHandle.Out;
                _drag = DragMode.TrimOut;
            }
            else
            {
                SeekTo(_viewport.PxToMs(x, width), snap: true, followDetail: false);
                _drag = DragMode.Playhead;
            }
        }

        strip.CaptureMouse();
        UpdateCursor(strip, x);
        e.Handled = true;
    }

    private void OnStripMove(TimelineRenderSurface eventStrip, MouseEventArgs e)
    {
        if (_drag == DragMode.None)
        {
            UpdateCursor(eventStrip, e.GetPosition(eventStrip).X);
            return;
        }

        TimelineRenderSurface strip = _drag is DragMode.BrushBody or DragMode.BrushLeft or DragMode.BrushRight
            ? _overview
            : _detail;
        if (_drag is DragMode.TrimIn or DragMode.TrimOut)
        {
            strip = _overview.IsMouseCaptured ? _overview : _detail;
        }
        if (_drag == DragMode.Playhead)
        {
            strip = _detail.IsMouseCaptured ? _detail : _overview;
        }

        double width = strip.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        double x = e.GetPosition(strip).X;
        if (Math.Abs(x - _dragStartX) >= SystemParameters.MinimumHorizontalDragDistance)
        {
            _dragMoved = true;
        }

        switch (_drag)
        {
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
                SetIn(FrameStepCalculator.SnapToFrame(
                    ReferenceEquals(strip, _overview)
                        ? OverviewPxToMs(x, width)
                        : _viewport.PxToMs(x, width),
                    _fps,
                    _durationMs));
                break;
            case DragMode.TrimOut:
                SetOut(FrameStepCalculator.SnapToFrame(
                    ReferenceEquals(strip, _overview)
                        ? OverviewPxToMs(x, width)
                        : _viewport.PxToMs(x, width),
                    _fps,
                    _durationMs));
                break;
        }
    }

    private void EndDrag(TimelineRenderSurface strip, MouseButtonEventArgs e)
    {
        DragMode completed = _drag;
        bool coarseClick = completed == DragMode.BrushBody && !_dragMoved && ReferenceEquals(strip, _overview);
        double x = e.GetPosition(strip).X;
        double width = strip.ActualWidth;

        _drag = DragMode.None;
        if (strip.IsMouseCaptured)
        {
            strip.ReleaseMouseCapture();
        }

        if (coarseClick && width > 0)
        {
            SeekFromOverview(OverviewPxToMs(x, width));
        }

        if (completed is DragMode.Playhead or DragMode.TrimIn or DragMode.TrimOut || coarseClick)
        {
            PlayheadInteractionCompleted?.Invoke(this, _playheadMs);
        }

        UpdateCursor(strip, x);
        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        bool reconcile = _drag is DragMode.Playhead or DragMode.TrimIn or DragMode.TrimOut;
        _drag = DragMode.None;
        if (reconcile)
        {
            PlayheadInteractionCompleted?.Invoke(this, _playheadMs);
        }
    }

    private void UpdateCursor(TimelineRenderSurface strip, double x)
    {
        double width = strip.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        if (ReferenceEquals(strip, _overview))
        {
            if (_trimModeEnabled)
            {
                double trimIn = OverviewMsToPx(_trim.InMs, width);
                double trimOut = OverviewMsToPx(_trim.OutMs, width);
                if (Math.Abs(x - trimIn) <= EdgeGrab + TrimHandleWidth / 2
                    || Math.Abs(x - trimOut) <= EdgeGrab + TrimHandleWidth / 2)
                {
                    strip.Cursor = Cursors.SizeWE;
                    return;
                }
            }

            double left = OverviewMsToPx(_viewport.ViewStartMs, width);
            double right = OverviewMsToPx(_viewport.ViewEndMs, width);
            strip.Cursor = Math.Abs(x - left) <= EdgeGrab || Math.Abs(x - right) <= EdgeGrab
                ? Cursors.SizeWE
                : x > left && x < right
                    ? Cursors.SizeAll
                    : Cursors.Cross;
            return;
        }

        bool onTrim = _trimModeEnabled
            && ((IsVisibleInDetail(_trim.InMs)
                 && Math.Abs(x - _viewport.MsToPx(_trim.InMs, width)) <= EdgeGrab)
                || (IsVisibleInDetail(_trim.OutMs)
                    && Math.Abs(x - _viewport.MsToPx(_trim.OutMs, width)) <= EdgeGrab));
        strip.Cursor = onTrim ? Cursors.SizeWE : Cursors.Cross;
    }

    private double OverviewPxToMs(double x, double width) =>
        Math.Clamp(x / width, 0, 1) * _durationMs;

    private double OverviewMsToPx(double ms, double width) =>
        Math.Clamp(ms / _durationMs, 0, 1) * width;

    private bool IsVisibleInDetail(double ms) =>
        ms >= _viewport.ViewStartMs - 0.001 && ms <= _viewport.ViewEndMs + 0.001;

    private void SeekTo(double ms, bool snap, bool followDetail)
    {
        double clamped = Math.Clamp(ms, 0, _durationMs);
        if (snap)
        {
            clamped = FrameStepCalculator.SnapToFrame(clamped, _fps, _durationMs);
        }

        double beforeStart = _viewport.ViewStartMs;
        double beforeEnd = _viewport.ViewEndMs;
        _playheadMs = _trim.ClampToSelection(clamped);
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

    private void ClampPlayheadAfterTrim()
    {
        double clamped = _trim.ClampToSelection(_playheadMs);
        if (Math.Abs(clamped - _playheadMs) <= 0.001)
        {
            return;
        }

        _playheadMs = clamped;
        _viewport.EnsureVisible(_playheadMs);
        InvalidatePlayhead();
        PlayheadChanged?.Invoke(this, _playheadMs);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_trimModeEnabled || !IsKeyboardFocusWithin)
        {
            return;
        }

        if (e.Key == Key.Tab)
        {
            _activeTrimHandle = _activeTrimHandle == TrimHandle.In ? TrimHandle.Out : TrimHandle.In;
            InvalidateRange();
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
            Key.Home => 0,
            Key.End => _durationMs,
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

        _overviewCaption.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"전체 타임라인  |  시작 {FormatMs(0)}  →  끝 {FormatMs(_durationMs)}  |  굵은 눈금 {FormatMs(CoarseIntervalMs)}");
        _detailCaption.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"세부 타임라인  |  시작 {FormatMs(_viewport.ViewStartMs)}  →  끝 {FormatMs(_viewport.ViewEndMs)}  |  범위 {FormatMs(_viewport.VisibleSpanMs)} · {visibleFrames}프레임");

        AutomationProperties.SetName(_detail, _detailCaption.Text);
    }

    // ---- DrawingVisual layers ----

    private void DrawOverviewLayer(DrawingContext dc, TimelineRenderLayer layer, Size size)
    {
        double width = size.Width;
        double height = size.Height;
        switch (layer)
        {
            case TimelineRenderLayer.Static:
                dc.DrawRectangle(_surfaceSunken, null, new Rect(size));
                double majorMs = CoarseIntervalMs;
                double minorMs = majorMs / 4.0;
                int minorCount = Math.Max(1, (int)Math.Ceiling(_durationMs / minorMs));
                for (int index = 0; index <= minorCount; index++)
                {
                    double time = Math.Min(_durationMs, index * minorMs);
                    double x = OverviewMsToPx(time, width);
                    bool major = index % 4 == 0;
                    DrawLine(dc, major ? _majorTickPen : _minorTickPen, x,
                        major ? height * 0.34 : height * 0.70, x, height);
                    if (major)
                    {
                        DrawText(dc, FormatTickLabel(time),
                            Math.Clamp(x + 4, 3, Math.Max(3, width - 54)), 2,
                            _textSecondary, 11, semiBold: true, background: null, centered: false);
                    }
                }

                DrawLine(dc, _majorTickPen, width - 1.5, height * 0.20, width - 1.5, height);
                break;

            case TimelineRenderLayer.Range:
                double trimInX = OverviewMsToPx(_trim.InMs, width);
                double trimOutX = OverviewMsToPx(_trim.OutMs, width);
                DrawRect(dc, trimInX, height - 7, trimOutX - trimInX, 7, _accentSubtle, null);

                double brushLeft = OverviewMsToPx(_viewport.ViewStartMs, width);
                double brushRight = OverviewMsToPx(_viewport.ViewEndMs, width);
                double brushWidth = Math.Max(3, brushRight - brushLeft);
                DrawRect(dc, 0, 0, brushLeft, height, _outside, null);
                DrawRect(dc, brushRight, 0, width - brushRight, height, _outside, null);
                DrawRect(dc, brushLeft, 0, brushWidth, height, _accentSubtle, _accentOutlinePen);

                double gripWidth = Math.Min(BrushGripWidth, Math.Max(4, brushWidth / 2.0));
                DrawRect(dc, brushLeft, 0, gripWidth, height, _accent, null);
                DrawRect(dc, brushRight - gripWidth, 0, gripWidth, height, _accent, null);
                DrawLine(dc, _gripMarkPen, brushLeft + gripWidth / 2.0, height * 0.32,
                    brushLeft + gripWidth / 2.0, height * 0.68);
                DrawLine(dc, _gripMarkPen, brushRight - gripWidth / 2.0, height * 0.32,
                    brushRight - gripWidth / 2.0, height * 0.68);

                if (brushWidth >= 145)
                {
                    DrawText(dc,
                        $"{FormatMs(_viewport.ViewStartMs)} – {FormatMs(_viewport.ViewEndMs)}",
                        (brushLeft + brushRight) / 2.0, height * 0.43,
                        _textPrimary, 12, semiBold: true, _surfaceScrim, centered: true);
                }

                if (_trimModeEnabled)
                {
                    DrawDeletionRange(dc, 0, trimInX, height);
                    DrawDeletionRange(dc, trimOutX, width, height);
                    DrawTrimHandle(dc, trimInX, height, TrimHandle.In);
                    DrawTrimHandle(dc, trimOutX, height, TrimHandle.Out);
                }
                break;

            case TimelineRenderLayer.Transient:
                DrawPlayhead(dc, OverviewMsToPx(_playheadMs, width), height);
                break;
        }
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
            DrawText(dc, "위 선택 구간을 아래 전체 폭으로 확대", width / 2.0, 0,
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

                    DrawText(dc, "확대 + 또는 마우스 휠로 프레임 눈금 표시", width / 2.0,
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
                    double shadeX = _viewport.MsToPx(visibleIn, width);
                    double shadeRight = _viewport.MsToPx(visibleOut, width);
                    DrawRect(dc, shadeX, height - 7, shadeRight - shadeX, 7, _accentSubtle, null);
                }

                if (_trimModeEnabled)
                {
                    if (_trim.InMs > _viewport.ViewStartMs)
                    {
                        double deleteRight = _viewport.MsToPx(
                            Math.Min(_trim.InMs, _viewport.ViewEndMs),
                            width);
                        DrawDeletionRange(dc, 0, deleteRight, height);
                    }

                    if (_trim.OutMs < _viewport.ViewEndMs)
                    {
                        double deleteLeft = _viewport.MsToPx(
                            Math.Max(_trim.OutMs, _viewport.ViewStartMs),
                            width);
                        DrawDeletionRange(dc, deleteLeft, width, height);
                    }

                    if (IsVisibleInDetail(_trim.InMs))
                    {
                        DrawTrimHandle(
                            dc,
                            _viewport.MsToPx(_trim.InMs, width),
                            height,
                            TrimHandle.In);
                    }

                    if (IsVisibleInDetail(_trim.OutMs))
                    {
                        DrawTrimHandle(
                            dc,
                            _viewport.MsToPx(_trim.OutMs, width),
                            height,
                            TrimHandle.Out);
                    }
                }
                break;

            case TimelineRenderLayer.Transient:
                if (IsVisibleInDetail(_playheadMs))
                {
                    DrawPlayhead(dc, _viewport.MsToPx(_playheadMs, width), height);
                }
                break;
        }
    }

    private void DrawPlayhead(DrawingContext dc, double x, double height)
    {
        DrawLine(dc, _playheadPen, x, 0, x, height);
        dc.PushTransform(new TranslateTransform(x, 0));
        dc.DrawGeometry(_playhead, null, _playheadMarker);
        dc.Pop();
    }

    private void DrawDeletionRange(DrawingContext dc, double left, double right, double height)
    {
        double width = right - left;
        if (width <= 0.5)
        {
            return;
        }

        DrawRect(dc, left, 0, width, height, _deleteFill, _deleteOutlinePen);
        const double hatchStep = 12;
        for (double x = left - height; x < right; x += hatchStep)
        {
            double startX = Math.Max(left, x);
            double startY = Math.Max(0, left - x);
            double endX = Math.Min(right, x + height);
            double endY = Math.Min(height, right - x);
            if (endX > startX)
            {
                DrawLine(dc, _deleteHatchPen, startX, startY, endX, endY);
            }
        }

        if (width >= 48)
        {
            DrawText(
                dc,
                "삭제",
                left + (width / 2),
                Math.Max(2, (height - 20) / 2),
                _textPrimary,
                11,
                semiBold: true,
                _surfaceScrim,
                centered: true);
        }
    }

    private void DrawTrimHandle(DrawingContext dc, double x, double height, TrimHandle handle)
    {
        DrawRect(
            dc,
            x - (TrimHandleWidth / 2),
            0,
            TrimHandleWidth,
            height,
            _deleteHandle,
            _deleteOutlinePen);
        if (_activeTrimHandle == handle)
        {
            DrawRect(dc, x - 4, 3, 8, Math.Max(1, height - 6), _textPrimary, null);
        }
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
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            semiBold ? _semiBoldTypeface : _regularTypeface,
            fontSize,
            foreground,
            pixelsPerDip);

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

    private TextBlock BuildCaption() => new()
    {
        Foreground = _textPrimary,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(2, 0, 0, 5),
    };

    private static string FormatMs(double ms)
    {
        TimeSpan value = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return value.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private static string FormatTickLabel(double ms)
    {
        TimeSpan value = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return value.TotalMinutes >= 1
            ? value.ToString(@"mm\:ss", CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{value.TotalSeconds:0.0}s");
    }

    private static Brush ResolveBrush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? FrozenBrush(fallback);

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
        _overview.LayersInvalidated -= OnLayersInvalidated;
        _connector.LayersInvalidated -= OnLayersInvalidated;
        _detail.LayersInvalidated -= OnLayersInvalidated;
        PreviewKeyDown -= OnPreviewKeyDown;
        _renderScheduler.Dispose();
    }
}
