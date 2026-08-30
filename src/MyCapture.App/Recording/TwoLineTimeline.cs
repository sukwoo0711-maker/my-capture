using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// A two-line "overview + detail" timeline, modelled on the pro-editor convention
/// (Premiere's zoom scroll bar, Camtasia's zoom slider + fit): the TOP strip shows the whole
/// clip and carries a draggable/resizable viewport brush; the BOTTOM strip expands that
/// viewport to full width with frame-level ticks for precise work. Both strips share one
/// playhead and one trim (In/Out) selection.
/// </summary>
/// <remarks>
/// Drawn on two <see cref="Canvas"/> strips rather than composed from sliders, because the
/// interaction (brush body-drag to pan, brush edge-drag to zoom, click-to-seek, trim-handle
/// drag) does not map cleanly onto stock <see cref="Slider"/> parts. All time/pixel maths is
/// delegated to the unit-tested <see cref="TimelineViewport"/>, <see cref="TrimSelection"/> and
/// <see cref="FrameStepCalculator"/> so this control stays a thin view.
/// </remarks>
internal sealed class TwoLineTimeline : ContentControl
{
    private const double StripHeight = 34.0;
    private const double EdgeGrab = 6.0;
    private const double HandleW = 10.0;

    private readonly Canvas _overview = new() { Height = StripHeight, ClipToBounds = true };
    private readonly Canvas _detail = new() { Height = StripHeight, ClipToBounds = true };

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

        var panel = new StackPanel();
        panel.Children.Add(Labelled("전체 타임라인 (개요 · 뷰포트 몸통=이동, 양 끝=확대)", _overview, "전체 타임라인"));
        panel.Children.Add(new Border { Height = 8 });
        panel.Children.Add(Labelled("세부 타임라인 (선택 구간 · 프레임 정밀)", _detail, "세부 프레임 타임라인"));
        Content = panel;

        AutomationProperties.SetHelpText(
            _overview,
            "클립 전체입니다. 클릭하면 크게 이동하고, 선택 구간 몸통을 끌면 이동하며 양 끝을 끌면 확대합니다.");
        AutomationProperties.SetHelpText(
            _detail,
            "상단 선택 구간을 확대한 프레임 타임라인입니다. 클릭하면 프레임에 맞춰 이동하고 마우스 휠로 확대합니다.");

        _overview.MouseLeftButtonDown += (_, e) => OnStripDown(_overview, e, isOverview: true);
        _detail.MouseLeftButtonDown += (_, e) => OnStripDown(_detail, e, isOverview: false);
        _overview.MouseMove += (_, e) => OnStripMove(e);
        _detail.MouseMove += (_, e) => OnStripMove(e);
        _overview.MouseLeftButtonUp += (_, e) => EndDrag(_overview, e);
        _detail.MouseLeftButtonUp += (_, e) => EndDrag(_detail, e);
        _overview.LostMouseCapture += (_, _) => _drag = DragMode.None;
        _detail.LostMouseCapture += (_, _) => _drag = DragMode.None;
        _detail.MouseWheel += OnDetailWheel;
        _overview.SizeChanged += (_, _) => Redraw();
        _detail.SizeChanged += (_, _) => Redraw();
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1.0 : 0.5;
    }

    /// <summary>Raised when the user moves the playhead (ms). Frame-snapped on the detail strip.</summary>
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

    internal void Initialize(double durationMs, int fps)
    {
        _durationMs = Math.Max(1, durationMs);
        _fps = Math.Max(1, fps);
        _viewport = new TimelineViewport(_durationMs, _fps);
        _trim = new TrimSelection(_durationMs);
        _playheadMs = 0;
        Redraw();
    }

    /// <summary>Externally set the playhead (e.g. from transport buttons); keeps it visible.</summary>
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
        double w = strip.ActualWidth;
        if (w <= 0)
        {
            return;
        }

        _dragStartX = x;
        _dragMoved = false;

        if (isOverview)
        {
            // Overview: edge drag resizes the detail viewport; body drag pans it. A body click
            // is resolved on mouse-up as a coarse seek, so fit-all does not swallow clicks.
            double leftPx = _viewport.ViewStartMs / _durationMs * w;
            double rightPx = _viewport.ViewEndMs / _durationMs * w;
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
                _dragAnchorMs = OverviewPxToMs(x, w) - _viewport.ViewStartMs;
            }
            else
            {
                SeekTo(OverviewPxToMs(x, w), snap: false);
                _drag = DragMode.Playhead;
            }
        }
        else
        {
            // Detail: only visible trim handles are interactive. An off-screen trim point must
            // not masquerade as a handle clamped to the left/right edge.
            bool inVisible = IsVisibleInDetail(_trim.InMs);
            bool outVisible = IsVisibleInDetail(_trim.OutMs);
            double inPx = _viewport.MsToPx(_trim.InMs, w);
            double outPx = _viewport.MsToPx(_trim.OutMs, w);
            if (inVisible && Math.Abs(x - inPx) <= EdgeGrab + HandleW / 2)
            {
                _drag = DragMode.TrimIn;
            }
            else if (outVisible && Math.Abs(x - outPx) <= EdgeGrab + HandleW / 2)
            {
                _drag = DragMode.TrimOut;
            }
            else
            {
                SeekTo(_viewport.PxToMs(x, w), snap: true);
                _drag = DragMode.Playhead;
            }
        }

        strip.CaptureMouse();
        e.Handled = true;
    }

    private void OnStripMove(MouseEventArgs e)
    {
        if (_drag == DragMode.None)
        {
            return;
        }

        Canvas strip = _drag is DragMode.BrushBody or DragMode.BrushLeft or DragMode.BrushRight
            ? _overview
            : _detail;
        if (_drag == DragMode.Playhead)
        {
            strip = _detail.IsMouseCaptured ? _detail : _overview;
        }

        double w = strip.ActualWidth;
        if (w <= 0)
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
                SeekTo(onDetail ? _viewport.PxToMs(x, w) : OverviewPxToMs(x, w), snap: onDetail);
                break;
            case DragMode.BrushBody:
                if (_dragMoved)
                {
                    double targetStart = OverviewPxToMs(x, w) - _dragAnchorMs;
                    _viewport.Pan(targetStart - _viewport.ViewStartMs);
                    Redraw();
                }
                break;
            case DragMode.BrushLeft:
                _viewport.SetView(OverviewPxToMs(x, w), _viewport.ViewEndMs);
                Redraw();
                break;
            case DragMode.BrushRight:
                _viewport.SetView(_viewport.ViewStartMs, OverviewPxToMs(x, w));
                Redraw();
                break;
            case DragMode.TrimIn:
                SetIn(FrameStepCalculator.SnapToFrame(_viewport.PxToMs(x, w), _fps, _durationMs));
                break;
            case DragMode.TrimOut:
                SetOut(FrameStepCalculator.SnapToFrame(_viewport.PxToMs(x, w), _fps, _durationMs));
                break;
        }
    }

    private void EndDrag(Canvas strip, MouseButtonEventArgs e)
    {
        DragMode completed = _drag;
        bool coarseClick = completed == DragMode.BrushBody && !_dragMoved && ReferenceEquals(strip, _overview);
        double x = e.GetPosition(strip).X;
        double w = strip.ActualWidth;

        _drag = DragMode.None;
        if (strip.IsMouseCaptured)
        {
            strip.ReleaseMouseCapture();
        }

        if (coarseClick && w > 0)
        {
            SeekTo(OverviewPxToMs(x, w), snap: false);
        }

        e.Handled = true;
    }

    private double OverviewPxToMs(double x, double w) => Math.Clamp(x / w, 0, 1) * _durationMs;

    private bool IsVisibleInDetail(double ms) =>
        ms >= _viewport.ViewStartMs - 0.001 && ms <= _viewport.ViewEndMs + 0.001;

    private void SeekTo(double ms, bool snap)
    {
        double clamped = Math.Clamp(ms, 0, _durationMs);
        if (snap)
        {
            clamped = FrameStepCalculator.SnapToFrame(clamped, _fps, _durationMs);
        }

        _playheadMs = clamped;
        Redraw();
        PlayheadChanged?.Invoke(this, _playheadMs);
    }

    // ---- drawing ----

    private void Redraw()
    {
        DrawOverview();
        DrawDetail();
    }

    private void DrawOverview()
    {
        _overview.Children.Clear();
        double w = _overview.ActualWidth;
        double h = _overview.Height;
        if (w <= 0)
        {
            return;
        }

        _overview.Background = Brush("Surface.Sunken", Color.FromRgb(0x1B, 0x17, 0x12));

        for (double sec = 0; sec * 1000 <= _durationMs; sec += TickStepSeconds())
        {
            double tx = sec * 1000 / _durationMs * w;
            AddLine(_overview, tx, h * 0.55, tx, h, Brush("Border.Subtle", Colors.Gray), 1);
        }

        double inX = _trim.InMs / _durationMs * w;
        double outX = _trim.OutMs / _durationMs * w;
        AddRect(
            _overview,
            inX,
            0,
            Math.Max(0, outX - inX),
            h,
            Brush("Accent.Subtle", Color.FromArgb(0x40, 0xFF, 0xD4, 0x00)),
            null,
            0);

        double brushLeft = _viewport.ViewStartMs / _durationMs * w;
        double brushRight = _viewport.ViewEndMs / _durationMs * w;
        AddRect(
            _overview,
            brushLeft,
            0,
            Math.Max(2, brushRight - brushLeft),
            h,
            Brush("Surface.Hover", Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            Brush("Accent.Default", Color.FromRgb(0xE0, 0xA8, 0x20)),
            1.5);
        AddRect(_overview, brushLeft - 1, 0, 2, h, Brush("Accent.Default", Color.FromRgb(0xE0, 0xA8, 0x20)), null, 0);
        AddRect(_overview, brushRight - 1, 0, 2, h, Brush("Accent.Default", Color.FromRgb(0xE0, 0xA8, 0x20)), null, 0);

        double playheadX = _playheadMs / _durationMs * w;
        AddPlayhead(_overview, playheadX, h);
    }

    private void DrawDetail()
    {
        _detail.Children.Clear();
        double w = _detail.ActualWidth;
        double h = _detail.Height;
        if (w <= 0)
        {
            return;
        }

        _detail.Background = Brush("Surface.Sunken", Color.FromRgb(0x1B, 0x17, 0x12));

        double frameMs = FrameStepCalculator.FrameDurationMs(_fps);
        double framePx = _viewport.VisibleSpanMs > 0 ? frameMs / _viewport.VisibleSpanMs * w : 0;
        if (framePx >= 4)
        {
            for (double t = Math.Ceiling(_viewport.ViewStartMs / frameMs) * frameMs;
                 t <= _viewport.ViewEndMs;
                 t += frameMs)
            {
                double tx = _viewport.MsToPx(t, w);
                AddLine(_detail, tx, h * 0.6, tx, h, Brush("Border.Subtle", Colors.DimGray), 1);
            }
        }
        else
        {
            double step = Math.Max(frameMs, _viewport.VisibleSpanMs / 20.0);
            for (double t = Math.Ceiling(_viewport.ViewStartMs / step) * step;
                 t <= _viewport.ViewEndMs;
                 t += step)
            {
                double tx = _viewport.MsToPx(t, w);
                AddLine(_detail, tx, h * 0.7, tx, h, Brush("Border.Subtle", Colors.DimGray), 1);
            }
        }

        double visibleIn = Math.Max(_trim.InMs, _viewport.ViewStartMs);
        double visibleOut = Math.Min(_trim.OutMs, _viewport.ViewEndMs);
        if (visibleOut > visibleIn)
        {
            double shadeX = _viewport.MsToPx(visibleIn, w);
            double shadeRight = _viewport.MsToPx(visibleOut, w);
            AddRect(
                _detail,
                shadeX,
                0,
                Math.Max(0, shadeRight - shadeX),
                h,
                Brush("Accent.Subtle", Color.FromArgb(0x40, 0xFF, 0xD4, 0x00)),
                null,
                0);
        }

        if (IsVisibleInDetail(_trim.InMs))
        {
            double inX = _viewport.MsToPx(_trim.InMs, w);
            AddRect(_detail, inX - HandleW / 2, 0, HandleW, h, Brush("Accent.Default", Color.FromRgb(0xE0, 0xA8, 0x20)), null, 0);
        }

        if (IsVisibleInDetail(_trim.OutMs))
        {
            double outX = _viewport.MsToPx(_trim.OutMs, w);
            AddRect(_detail, outX - HandleW / 2, 0, HandleW, h, Brush("Accent.Default", Color.FromRgb(0xE0, 0xA8, 0x20)), null, 0);
        }

        if (IsVisibleInDetail(_playheadMs))
        {
            AddPlayhead(_detail, _viewport.MsToPx(_playheadMs, w), h);
        }
    }

    private double TickStepSeconds()
    {
        double target = Math.Max(0.001, _durationMs / 1000.0 / 10.0);
        double[] steps = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300];
        foreach (double step in steps)
        {
            if (step >= target)
            {
                return step;
            }
        }

        return 600;
    }

    private void AddPlayhead(Canvas canvas, double x, double h)
    {
        AddLine(canvas, x, 0, x, h, Brush("Text.Primary", Colors.White), 1.6);
        var marker = new System.Windows.Shapes.Polygon
        {
            Points = [new Point(x - 5, 0), new Point(x + 5, 0), new Point(x, 7)],
            Fill = Brush("Accent.Cool", Color.FromRgb(0x66, 0xB7, 0xFF)),
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
        };
        Canvas.SetLeft(rectangle, x);
        Canvas.SetTop(rectangle, y);
        canvas.Children.Add(rectangle);
    }

    private StackPanel Labelled(string caption, Canvas strip, string automationName)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = caption,
            Foreground = Brush("Text.Muted", Colors.Gray),
            FontSize = 11,
            Margin = new Thickness(2, 0, 0, 3),
        });
        var host = new Border
        {
            BorderBrush = Brush("Border.Subtle", Colors.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = strip,
            Cursor = Cursors.Hand,
        };
        AutomationProperties.SetName(strip, automationName);
        panel.Children.Add(host);
        return panel;
    }

    private static Brush Brush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
