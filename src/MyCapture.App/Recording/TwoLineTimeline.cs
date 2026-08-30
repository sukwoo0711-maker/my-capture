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
/// <see cref="TrimSelection"/> and <see cref="FrameStepCalculator"/>. This control owns only
/// WPF drawing, hit testing and the visual explanation of the overview-to-detail relationship.
/// </remarks>
internal sealed class TwoLineTimeline : ContentControl
{
    private const double OverviewHeight = 58.0;
    private const double DetailHeight = 64.0;
    private const double ConnectorHeight = 15.0;
    private const double EdgeGrab = 14.0;
    private const double BrushGripWidth = 12.0;
    private const double TrimHandleWidth = 12.0;

    private readonly Canvas _overview = new() { Height = OverviewHeight, ClipToBounds = true };
    private readonly Canvas _connector = new() { Height = ConnectorHeight, IsHitTestVisible = false };
    private readonly Canvas _detail = new() { Height = DetailHeight, ClipToBounds = true };
    private readonly TextBlock _overviewCaption;
    private readonly TextBlock _detailCaption;

    private int _fps = 15;
    private double _durationMs = 1;
    private double _playheadMs;
    private TimelineViewport _viewport = new(1, 15);
    private TrimSelection _trim = new(1);

    private DragMode _drag = DragMode.None;
    private double _dragAnchorMs;
    private double _dragStartX;
    private bool _dragMoved;

    private enum DragMode { None, Playhead, BrushBody, BrushLeft, BrushRight, TrimIn, TrimOut }

    internal TwoLineTimeline()
    {
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

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
        _overview.LostMouseCapture += (_, _) => _drag = DragMode.None;
        _detail.LostMouseCapture += (_, _) => _drag = DragMode.None;
        _detail.MouseWheel += OnDetailWheel;
        _overview.SizeChanged += (_, _) => Redraw();
        _connector.SizeChanged += (_, _) => Redraw();
        _detail.SizeChanged += (_, _) => Redraw();
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1.0 : 0.5;
    }

    /// <summary>Raised when the user moves the playhead (ms). Frame-snapped on detail.</summary>
    internal event EventHandler<double>? PlayheadChanged;

    /// <summary>Raised when the trim In/Out changes.</summary>
    internal event EventHandler? TrimChanged;

    internal double DurationMs => _durationMs;

    internal double PlayheadMs => _playheadMs;

    internal double InMs => _trim.InMs;

    internal double OutMs => _trim.OutMs;

    internal bool IsFullClip => _trim.IsFullClip;

    internal double SelectedDurationMs => _trim.SelectedDurationMs;

    internal double ViewStartMs => _viewport.ViewStartMs;

    internal double ViewEndMs => _viewport.ViewEndMs;

    internal double VisibleSpanMs => _viewport.VisibleSpanMs;

    internal bool IsFitAll => _viewport.IsFitAll;

    internal double CoarseIntervalMs => TickStepSeconds() * 1000.0;

    internal string OverviewRangeText => _overviewCaption.Text;

    internal string DetailRangeText => _detailCaption.Text;

    internal void Initialize(double durationMs, int fps)
    {
        _durationMs = Math.Max(1, durationMs);
        _fps = Math.Max(1, fps);
        _viewport = new TimelineViewport(_durationMs, _fps);
        _trim = new TrimSelection(_durationMs);
        _playheadMs = 0;

        // The two strips must look different immediately. Select one coarse overview interval
        // by default so the bottom line visibly expands the frames between two heavy marks.
        double initialDetailSpan = Math.Min(_durationMs, CoarseIntervalMs);
        if (initialDetailSpan < _durationMs - 0.001)
        {
            _viewport.SetView(0, initialDetailSpan);
        }

        Redraw();
    }

    /// <summary>Externally sets the playhead and keeps it inside the detail viewport.</summary>
    internal void SetPlayhead(double ms, bool ensureVisible = true)
    {
        _playheadMs = Math.Clamp(ms, 0, _durationMs);
        if (ensureVisible)
        {
            _viewport.EnsureVisible(_playheadMs);
        }

        Redraw();
    }

    internal void SetIn(double ms)
    {
        _trim.SetIn(ms);
        Redraw();
        TrimChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void SetOut(double ms)
    {
        _trim.SetOut(ms);
        Redraw();
        TrimChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void FitAll()
    {
        _viewport.FitAll();
        Redraw();
    }

    internal void ZoomAroundPlayhead(double factor)
    {
        _viewport.Zoom(_playheadMs, factor);
        Redraw();
    }

    internal void SeekFromOverview(double ms) =>
        SeekTo(ms, snap: false, followDetail: true);

    // ---- interaction ----

    private void OnDetailWheel(object sender, MouseWheelEventArgs e)
    {
        double centre = _viewport.PxToMs(e.GetPosition(_detail).X, _detail.ActualWidth);
        _viewport.Zoom(centre, e.Delta > 0 ? 0.8 : 1.25);
        Redraw();
        e.Handled = true;
    }

    private void OnStripDown(Canvas strip, MouseButtonEventArgs e, bool isOverview)
    {
        double x = e.GetPosition(strip).X;
        double width = strip.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        _dragStartX = x;
        _dragMoved = false;

        if (isOverview)
        {
            double leftPx = OverviewMsToPx(_viewport.ViewStartMs, width);
            double rightPx = OverviewMsToPx(_viewport.ViewEndMs, width);
            if (Math.Abs(x - leftPx) <= EdgeGrab)
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
            if (inVisible && Math.Abs(x - inPx) <= EdgeGrab + TrimHandleWidth / 2)
            {
                _drag = DragMode.TrimIn;
            }
            else if (outVisible && Math.Abs(x - outPx) <= EdgeGrab + TrimHandleWidth / 2)
            {
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

    private void OnStripMove(Canvas eventStrip, MouseEventArgs e)
    {
        if (_drag == DragMode.None)
        {
            UpdateCursor(eventStrip, e.GetPosition(eventStrip).X);
            return;
        }

        Canvas strip = _drag is DragMode.BrushBody or DragMode.BrushLeft or DragMode.BrushRight
            ? _overview
            : _detail;
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
                    Redraw();
                }
                break;
            case DragMode.BrushLeft:
                _viewport.SetView(OverviewPxToMs(x, width), _viewport.ViewEndMs);
                Redraw();
                break;
            case DragMode.BrushRight:
                _viewport.SetView(_viewport.ViewStartMs, OverviewPxToMs(x, width));
                Redraw();
                break;
            case DragMode.TrimIn:
                SetIn(FrameStepCalculator.SnapToFrame(_viewport.PxToMs(x, width), _fps, _durationMs));
                break;
            case DragMode.TrimOut:
                SetOut(FrameStepCalculator.SnapToFrame(_viewport.PxToMs(x, width), _fps, _durationMs));
                break;
        }
    }

    private void EndDrag(Canvas strip, MouseButtonEventArgs e)
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

        UpdateCursor(strip, x);
        e.Handled = true;
    }

    private void UpdateCursor(Canvas strip, double x)
    {
        double width = strip.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        if (ReferenceEquals(strip, _overview))
        {
            double left = OverviewMsToPx(_viewport.ViewStartMs, width);
            double right = OverviewMsToPx(_viewport.ViewEndMs, width);
            strip.Cursor = Math.Abs(x - left) <= EdgeGrab || Math.Abs(x - right) <= EdgeGrab
                ? Cursors.SizeWE
                : x > left && x < right
                    ? Cursors.SizeAll
                    : Cursors.Cross;
            return;
        }

        bool onTrim = (IsVisibleInDetail(_trim.InMs) && Math.Abs(x - _viewport.MsToPx(_trim.InMs, width)) <= EdgeGrab)
            || (IsVisibleInDetail(_trim.OutMs) && Math.Abs(x - _viewport.MsToPx(_trim.OutMs, width)) <= EdgeGrab);
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

        _playheadMs = clamped;
        if (followDetail && !IsVisibleInDetail(clamped))
        {
            CenterDetailOn(clamped);
        }

        Redraw();
        PlayheadChanged?.Invoke(this, _playheadMs);
    }

    private void CenterDetailOn(double ms)
    {
        double targetStart = ms - (_viewport.VisibleSpanMs / 2.0);
        _viewport.Pan(targetStart - _viewport.ViewStartMs);
    }

    // ---- drawing ----

    private void Redraw()
    {
        UpdateRangeCaptions();
        DrawOverview();
        DrawConnector();
        DrawDetail();
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

    private void DrawOverview()
    {
        _overview.Children.Clear();
        double width = _overview.ActualWidth;
        double height = _overview.Height;
        if (width <= 0)
        {
            return;
        }

        _overview.Background = Brush("Surface.Sunken", Color.FromRgb(0x1B, 0x17, 0x12));

        double majorMs = CoarseIntervalMs;
        double minorMs = majorMs / 4.0;
        int minorCount = Math.Max(1, (int)Math.Ceiling(_durationMs / minorMs));
        for (int index = 0; index <= minorCount; index++)
        {
            double time = Math.Min(_durationMs, index * minorMs);
            double x = OverviewMsToPx(time, width);
            bool major = index % 4 == 0;
            AddLine(
                _overview,
                x,
                major ? height * 0.34 : height * 0.70,
                x,
                height,
                major ? Brush("Text.Secondary", Colors.LightGray) : Brush("Border.Subtle", Colors.DimGray),
                major ? 3.0 : 1.0);

            if (major)
            {
                AddText(
                    _overview,
                    FormatTickLabel(time),
                    Math.Clamp(x + 4, 3, Math.Max(3, width - 54)),
                    2,
                    Brush("Text.Secondary", Colors.LightGray),
                    11,
                    FontWeights.SemiBold,
                    null);
            }
        }

        // Always show the clip end as a heavy boundary, even when it is between regular marks.
        AddLine(_overview, width - 1.5, height * 0.20, width - 1.5, height,
            Brush("Text.Secondary", Colors.LightGray), 3.0);

        // Trim is a separate bottom band; it must not wash out the overview viewport guide.
        double trimInX = OverviewMsToPx(_trim.InMs, width);
        double trimOutX = OverviewMsToPx(_trim.OutMs, width);
        AddRect(
            _overview,
            trimInX,
            height - 7,
            Math.Max(0, trimOutX - trimInX),
            7,
            Brush("Accent.Subtle", Color.FromArgb(0x50, 0xFF, 0xD4, 0x00)),
            null,
            0);

        double brushLeft = OverviewMsToPx(_viewport.ViewStartMs, width);
        double brushRight = OverviewMsToPx(_viewport.ViewEndMs, width);
        double brushWidth = Math.Max(3, brushRight - brushLeft);

        // Darken everything not represented by the detail strip. This gives the selected upper
        // range substantially more visual weight than a thin outline alone.
        Brush outside = new SolidColorBrush(Color.FromArgb(0x86, 0x00, 0x00, 0x00));
        AddRect(_overview, 0, 0, Math.Max(0, brushLeft), height, outside, null, 0);
        AddRect(_overview, brushRight, 0, Math.Max(0, width - brushRight), height, outside, null, 0);

        Brush accent = Brush("Accent.Default", Color.FromRgb(0xE0, 0xA8, 0x20));
        AddRect(
            _overview,
            brushLeft,
            0,
            brushWidth,
            height,
            Brush("Accent.Subtle", Color.FromArgb(0x38, 0xE0, 0xA8, 0x20)),
            accent,
            3.0);

        double gripWidth = Math.Min(BrushGripWidth, Math.Max(4, brushWidth / 2.0));
        AddRect(_overview, brushLeft, 0, gripWidth, height, accent, null, 0);
        AddRect(_overview, brushRight - gripWidth, 0, gripWidth, height, accent, null, 0);
        Brush gripMark = Brush("Surface.Sunken", Color.FromRgb(0x1B, 0x17, 0x12));
        AddLine(_overview, brushLeft + gripWidth / 2.0, height * 0.32, brushLeft + gripWidth / 2.0, height * 0.68, gripMark, 2);
        AddLine(_overview, brushRight - gripWidth / 2.0, height * 0.32, brushRight - gripWidth / 2.0, height * 0.68, gripMark, 2);

        if (brushWidth >= 145)
        {
            AddCenteredText(
                _overview,
                $"{FormatMs(_viewport.ViewStartMs)} – {FormatMs(_viewport.ViewEndMs)}",
                (brushLeft + brushRight) / 2.0,
                height * 0.43,
                Brush("Text.Primary", Colors.White),
                12,
                FontWeights.SemiBold,
                Brush("Surface.Scrim", Color.FromArgb(0xB8, 0x00, 0x00, 0x00)));
        }

        AddPlayhead(_overview, OverviewMsToPx(_playheadMs, width), height);
    }

    private void DrawConnector()
    {
        _connector.Children.Clear();
        double width = _connector.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        double left = OverviewMsToPx(_viewport.ViewStartMs, width);
        double right = OverviewMsToPx(_viewport.ViewEndMs, width);
        Brush accent = Brush("Accent.Default", Color.FromRgb(0xE0, 0xA8, 0x20));
        AddLine(_connector, left, 0, 1, ConnectorHeight, accent, 2.0);
        AddLine(_connector, right, 0, width - 1, ConnectorHeight, accent, 2.0);

        if (width >= 360)
        {
            AddCenteredText(
                _connector,
                "위 선택 구간을 아래 전체 폭으로 확대",
                width / 2.0,
                -1,
                Brush("Text.Secondary", Colors.LightGray),
                10.5,
                FontWeights.SemiBold,
                Brush("Surface.Base", Color.FromRgb(0x1B, 0x17, 0x12)));
        }
    }

    private void DrawDetail()
    {
        _detail.Children.Clear();
        double width = _detail.ActualWidth;
        double height = _detail.Height;
        if (width <= 0)
        {
            return;
        }

        _detail.Background = Brush("Surface.Sunken", Color.FromRgb(0x1B, 0x17, 0x12));

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
                AddLine(
                    _detail,
                    x,
                    group ? height * 0.48 : height * 0.67,
                    x,
                    height,
                    group ? Brush("Text.Muted", Colors.Gray) : Brush("Border.Subtle", Colors.DimGray),
                    group ? 1.6 : 1.0);
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
                AddLine(_detail, x, height * 0.68, x, height, Brush("Border.Subtle", Colors.DimGray), 1);
            }

            AddCenteredText(
                _detail,
                "확대 + 또는 마우스 휠로 프레임 눈금 표시",
                width / 2.0,
                height * 0.36,
                Brush("Text.Secondary", Colors.LightGray),
                11,
                FontWeights.SemiBold,
                Brush("Surface.Scrim", Color.FromArgb(0xA8, 0x00, 0x00, 0x00)));
        }

        // Repeat the exact same heavy coarse boundaries in detail. Thin frame marks between
        // these lines visually explain that detail edits the frames inside an overview index.
        double majorMs = CoarseIntervalMs;
        double firstMajor = Math.Ceiling(_viewport.ViewStartMs / majorMs) * majorMs;
        for (double time = firstMajor; time <= _viewport.ViewEndMs + 0.001; time += majorMs)
        {
            double x = _viewport.MsToPx(time, width);
            AddLine(_detail, x, 0, x, height,
                Brush("Text.Secondary", Colors.LightGray), 3.0);
            AddText(
                _detail,
                FormatTickLabel(time),
                Math.Clamp(x + 4, 3, Math.Max(3, width - 54)),
                2,
                Brush("Text.Secondary", Colors.LightGray),
                11,
                FontWeights.SemiBold,
                Brush("Surface.Scrim", Color.FromArgb(0xA8, 0x00, 0x00, 0x00)));
        }

        double visibleIn = Math.Max(_trim.InMs, _viewport.ViewStartMs);
        double visibleOut = Math.Min(_trim.OutMs, _viewport.ViewEndMs);
        if (visibleOut > visibleIn)
        {
            double shadeX = _viewport.MsToPx(visibleIn, width);
            double shadeRight = _viewport.MsToPx(visibleOut, width);
            AddRect(
                _detail,
                shadeX,
                height - 7,
                Math.Max(0, shadeRight - shadeX),
                7,
                Brush("Accent.Subtle", Color.FromArgb(0x50, 0xFF, 0xD4, 0x00)),
                null,
                0);
        }

        Brush accent = Brush("Accent.Default", Color.FromRgb(0xE0, 0xA8, 0x20));
        if (IsVisibleInDetail(_trim.InMs))
        {
            double inX = _viewport.MsToPx(_trim.InMs, width);
            AddRect(_detail, inX - TrimHandleWidth / 2.0, 0, TrimHandleWidth, height, accent, null, 0);
        }

        if (IsVisibleInDetail(_trim.OutMs))
        {
            double outX = _viewport.MsToPx(_trim.OutMs, width);
            AddRect(_detail, outX - TrimHandleWidth / 2.0, 0, TrimHandleWidth, height, accent, null, 0);
        }

        if (IsVisibleInDetail(_playheadMs))
        {
            AddPlayhead(_detail, _viewport.MsToPx(_playheadMs, width), height);
        }
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

    private void AddPlayhead(Canvas canvas, double x, double height)
    {
        Brush playhead = Brush("Accent.Cool", Color.FromRgb(0x66, 0xB7, 0xFF));
        AddLine(canvas, x, 0, x, height, playhead, 2.6);
        var marker = new System.Windows.Shapes.Polygon
        {
            Points = [new Point(x - 7, 0), new Point(x + 7, 0), new Point(x, 10)],
            Fill = playhead,
        };
        canvas.Children.Add(marker);
    }

    private static void AddLine(
        Canvas canvas,
        double x1,
        double y1,
        double x2,
        double y2,
        Brush stroke,
        double thickness)
    {
        canvas.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = stroke,
            StrokeThickness = thickness,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false,
        });
    }

    private static void AddRect(
        Canvas canvas,
        double x,
        double y,
        double width,
        double height,
        Brush? fill,
        Brush? stroke,
        double thickness)
    {
        var rectangle = new System.Windows.Shapes.Rectangle
        {
            Width = Math.Max(0, width),
            Height = height,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = thickness,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(rectangle, x);
        Canvas.SetTop(rectangle, y);
        canvas.Children.Add(rectangle);
    }

    private static void AddText(
        Canvas canvas,
        string text,
        double x,
        double y,
        Brush foreground,
        double fontSize,
        FontWeight weight,
        Brush? background)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            Background = background,
            FontSize = fontSize,
            FontWeight = weight,
            Padding = background is null ? new Thickness(0) : new Thickness(4, 1, 4, 1),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        canvas.Children.Add(label);
    }

    private static void AddCenteredText(
        Canvas canvas,
        string text,
        double centreX,
        double y,
        Brush foreground,
        double fontSize,
        FontWeight weight,
        Brush? background)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = foreground,
            Background = background,
            FontSize = fontSize,
            FontWeight = weight,
            Padding = background is null ? new Thickness(0) : new Thickness(5, 1, 5, 1),
            IsHitTestVisible = false,
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(label, Math.Max(2, centreX - label.DesiredSize.Width / 2.0));
        Canvas.SetTop(label, y);
        canvas.Children.Add(label);
    }

    private StackPanel Labelled(TextBlock caption, Canvas strip, string automationName)
    {
        var panel = new StackPanel();
        panel.Children.Add(caption);
        var host = new Border
        {
            BorderBrush = Brush("Border.Subtle", Colors.Gray),
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
        Foreground = Brush("Text.Primary", Colors.White),
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

    private static Brush Brush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
