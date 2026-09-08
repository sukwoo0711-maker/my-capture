using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// A multi-track, distinct-row timeline view of non-destructive video layers.
/// Provides readable rows per layer, compact empty states, body move, and edge trimming.
/// </summary>
internal sealed class VideoLayerTimeline : FrameworkElement
{
    private const double LabelWidth = 82;
    private const double LayerTrackHeight = 28;
    private const double EmptyTrackHeight = 22;
    private const double BarHeight = 20;
    private const double Gap = 4;
    private const double PaddingTop = 4;
    private const double PaddingBottom = 4;
    private const double HandleZoneWidth = 10;
    private const double MinBarWidth = 4;

    private readonly Brush _background;
    private readonly Brush _track;
    private readonly Brush _textLayer;
    private readonly Brush _frameLayer;
    private readonly Brush _foreground;
    private readonly Brush _textSecondary;
    private readonly Brush _muted;
    private readonly Pen _gridPen;
    private readonly Pen _playheadPen;
    private readonly Typeface _typeface;

    private IReadOnlyList<TimedTextOverlay> _textLayers = [];
    private IReadOnlyList<FrameEditLayer> _frameLayers = [];
    private int _textLayerCount;
    private int _frameLayerCount;
    private double _durationMs = 1;
    private double _playheadMs;
    private Guid? _selectedId;

    private bool _dragging;
    private DragAction _dragAction = DragAction.None;
    private double _dragInitialStartMs;
    private double _dragInitialEndMs;
    private double _dragStartMouseX;
    private Guid _dragTargetId;
    private bool _dragTargetIsText;

    internal event EventHandler? LayerSelected;
    internal event EventHandler? LayerTimingChanged;
    internal event EventHandler? LayerTimingInteractionCompleted;
    internal event EventHandler? TextTimingChanged;
    internal event EventHandler? TextLayerSelected;

    internal Guid? SelectedId => _selectedId;
    internal Guid? SelectedLayerId => _selectedId;
    internal Guid? SelectedTextId => _selectedId;
    internal bool IsDragging => _dragging;

    internal void SelectLayer(Guid? id)
    {
        _selectedId = id;
        InvalidateVisual();
    }

    internal void SelectText(Guid? id) => SelectLayer(id);

    internal VideoLayerTimeline()
    {
        _background = ResolveBrush("Timeline.Background", Color.FromRgb(0x0B, 0x0F, 0x17));
        _track = ResolveBrush("Timeline.Track", Color.FromRgb(0x15, 0x1E, 0x2B));
        _textLayer = ResolveBrush("Timeline.TextLayer", Color.FromRgb(0x3B, 0x82, 0xF6));
        _frameLayer = ResolveBrush("Timeline.FrameLayer", Color.FromRgb(0x9B, 0x7E, 0xDE));
        _foreground = ResolveBrush("Text.Badge", Colors.White);
        _textSecondary = ResolveBrush("Text.Secondary", Color.FromRgb(0xC4, 0xCF, 0xDC));
        _muted = ResolveBrush("Text.Muted", Color.FromRgb(0x8E, 0x9C, 0xAF));
        _gridPen = FrozenPen(ResolveBrush("Timeline.Grid", Color.FromRgb(0x2B, 0x3A, 0x50)), 1);
        _playheadPen = FrozenPen(ResolveBrush("Timeline.Playhead", Color.FromRgb(0x7D, 0xD7, 0xF8)), 2);

        FontFamily uiFont = Application.Current?.TryFindResource("Font.Ui") as FontFamily
            ?? new FontFamily("Segoe UI");
        _typeface = new Typeface(uiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        Height = ComputeDesiredHeight();
        MinWidth = 240;
        SnapsToDevicePixels = true;
        Focusable = true;
        Cursor = Cursors.Hand;
        ToolTip = "레이어 막대의 가운데를 드래그해 이동하거나 양 끝을 드래그해 길이를 조절합니다. 위/아래: 레이어 선택 · 좌/우: 시작 시간 · Shift+좌/우: 끝 시간 · Ctrl: 0.01초 단위 · Esc: 취소";
        AutomationProperties.SetName(this, "영상 레이어 타임라인");
        AutomationProperties.SetHelpText(this, (string)ToolTip);
    }

    internal void Initialize(double durationMs)
    {
        _durationMs = Math.Max(1, durationMs);
        _playheadMs = 0;
        InvalidateVisual();
    }

    internal void SetLayers(
        IReadOnlyList<TimedTextOverlay> textLayers,
        IReadOnlyList<FrameEditLayer> frameLayers)
    {
        IReadOnlyList<TimedTextOverlay> nextTextLayers = textLayers ?? [];
        IReadOnlyList<FrameEditLayer> nextFrameLayers = frameLayers ?? [];

        double nextHeight = ComputeDesiredHeight(nextTextLayers.Count, nextFrameLayers.Count);
        bool geometryChanged = _textLayerCount != nextTextLayers.Count
            || _frameLayerCount != nextFrameLayers.Count
            || Math.Abs(Height - nextHeight) > 0.001;

        _textLayers = [.. nextTextLayers];
        _frameLayers = [.. nextFrameLayers];
        _textLayerCount = nextTextLayers.Count;
        _frameLayerCount = nextFrameLayers.Count;

        if (geometryChanged)
        {
            Height = nextHeight;
            AutomationProperties.SetHelpText(
                this,
                $"텍스트 레이어 {_textLayers.Count}개, 프레임 레이어 {_frameLayers.Count}개. {(string)ToolTip}");
            InvalidateMeasure();
        }

        bool hasSelected = false;
        if (_selectedId.HasValue)
        {
            for (int i = 0; i < _textLayers.Count; i++)
            {
                if (_textLayers[i].Id == _selectedId.Value)
                {
                    hasSelected = true;
                    break;
                }
            }

            if (!hasSelected)
            {
                for (int i = 0; i < _frameLayers.Count; i++)
                {
                    if (_frameLayers[i].Id == _selectedId.Value)
                    {
                        hasSelected = true;
                        break;
                    }
                }
            }
        }

        if (!hasSelected)
        {
            if (_textLayers.Count > 0)
            {
                _selectedId = _textLayers[0].Id;
            }
            else if (_frameLayers.Count > 0)
            {
                _selectedId = _frameLayers[0].Id;
            }
            else
            {
                _selectedId = null;
            }
        }

        InvalidateVisual();
    }

    internal void SetPlayhead(double sourceTimeMs)
    {
        double next = double.IsFinite(sourceTimeMs)
            ? Math.Clamp(sourceTimeMs, 0, _durationMs)
            : 0;
        if (Math.Abs(next - _playheadMs) < 0.1)
        {
            return;
        }

        _playheadMs = next;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = ComputeDesiredHeight();
        double width = double.IsInfinity(availableSize.Width)
            ? MinWidth
            : Math.Max(MinWidth, availableSize.Width);
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= LabelWidth + 2 || height <= 0)
        {
            return;
        }

        dc.DrawRoundedRectangle(_background, _gridPen, new Rect(0, 0, width, height), 6, 6);
        if (IsKeyboardFocused)
        {
            dc.DrawRoundedRectangle(null, _playheadPen, new Rect(1, 1, width - 2, height - 2), 6, 6);
        }

        double timelineWidth = Math.Max(1, width - LabelWidth);
        double currentTop = PaddingTop;

        // Draw Text Rows
        if (_textLayers.Count == 0)
        {
            DrawEmptyTrack(dc, currentTop, EmptyTrackHeight, "T  텍스트", "텍스트 레이어 없음", timelineWidth);
            currentTop += EmptyTrackHeight + Gap;
        }
        else
        {
            for (int index = 0; index < _textLayers.Count; index++)
            {
                TimedTextOverlay layer = _textLayers[index];
                string rowLabel = _textLayers.Count == 1 ? "T  텍스트" : $"T  텍스트 {index + 1}";
                DrawLayerRow(
                    dc,
                    currentTop,
                    LayerTrackHeight,
                    rowLabel,
                    layer.StartMs,
                    layer.EndMs,
                    OneLine(layer.Text),
                    layer.Id,
                    _textLayer,
                    hatch: false,
                    timelineWidth);
                currentTop += LayerTrackHeight + Gap;
            }
        }

        // Draw Frame Rows
        if (_frameLayers.Count == 0)
        {
            DrawEmptyTrack(dc, currentTop, EmptyTrackHeight, "F  프레임", "프레임 레이어 없음", timelineWidth);
            currentTop += EmptyTrackHeight + Gap;
        }
        else
        {
            for (int index = 0; index < _frameLayers.Count; index++)
            {
                FrameEditLayer layer = _frameLayers[index];
                string rowLabel = _frameLayers.Count == 1 ? "F  프레임" : $"F  프레임 {index + 1}";
                DrawLayerRow(
                    dc,
                    currentTop,
                    LayerTrackHeight,
                    rowLabel,
                    layer.StartMs,
                    layer.EndMs,
                    layer.Name,
                    layer.Id,
                    _frameLayer,
                    hatch: true,
                    timelineWidth);
                currentTop += LayerTrackHeight + Gap;
            }
        }

        // Playhead
        double playheadX = LabelWidth + ((_playheadMs / _durationMs) * timelineWidth);
        dc.DrawLine(_playheadPen, new Point(playheadX, 0), new Point(playheadX, height));
    }

    private void DrawEmptyTrack(
        DrawingContext dc,
        double top,
        double height,
        string label,
        string placeholder,
        double timelineWidth)
    {
        dc.DrawRectangle(_track, null, new Rect(LabelWidth, top, timelineWidth, height));
        dc.DrawLine(_gridPen, new Point(LabelWidth, top), new Point(LabelWidth, top + height));
        DrawText(dc, label, 8, top + ((height - 14) / 2.0), _textSecondary, 10.5, LabelWidth - 12);
        DrawText(dc, placeholder, LabelWidth + 12, top + ((height - 14) / 2.0), _muted, 10, timelineWidth - 24);
    }

    private void DrawLayerRow(
        DrawingContext dc,
        double top,
        double height,
        string rowLabel,
        double startMs,
        double endMs,
        string itemLabel,
        Guid layerId,
        Brush layerBrush,
        bool hatch,
        double timelineWidth)
    {
        dc.DrawRectangle(_track, null, new Rect(LabelWidth, top, timelineWidth, height));
        dc.DrawLine(_gridPen, new Point(LabelWidth, top), new Point(LabelWidth, top + height));
        DrawText(dc, rowLabel, 8, top + ((height - 14) / 2.0), _textSecondary, 10.5, LabelWidth - 12);

        double left = LabelWidth + (Math.Clamp(startMs / _durationMs, 0, 1) * timelineWidth);
        double right = LabelWidth + (Math.Clamp(endMs / _durationMs, 0, 1) * timelineWidth);
        double barTop = top + ((height - BarHeight) / 2.0);
        double barWidth = Math.Max(MinBarWidth, right - left);
        var bar = new Rect(left, barTop, barWidth, BarHeight);

        dc.DrawRoundedRectangle(layerBrush, null, bar, 3, 3);
        if (hatch && bar.Width >= 8)
        {
            for (double x = bar.Left - bar.Height; x < bar.Right; x += 10)
            {
                dc.DrawLine(
                    _gridPen,
                    new Point(Math.Max(bar.Left, x), bar.Bottom - Math.Max(0, bar.Left - x)),
                    new Point(Math.Min(bar.Right, x + bar.Height), bar.Top + Math.Max(0, x + bar.Height - bar.Right)));
            }
        }

        if (bar.Width >= 36)
        {
            DrawText(dc, itemLabel, bar.Left + 6, bar.Top + ((bar.Height - 14) / 2.0), _foreground, 10, bar.Width - 12);
        }

        bool selected = layerId == _selectedId;
        Pen pen = selected ? _playheadPen : _gridPen;
        dc.DrawRoundedRectangle(null, pen, bar, 3, 3);

        double handleWidth = Math.Min(4, bar.Width / 3.0);
        if (handleWidth >= 2)
        {
            dc.DrawRectangle(_foreground, null, new Rect(bar.Left, bar.Top + 3, handleWidth, bar.Height - 6));
            dc.DrawRectangle(_foreground, null, new Rect(bar.Right - handleWidth, bar.Top + 3, handleWidth, bar.Height - 6));
        }
    }

    private static double ComputeDesiredHeight(int textCount, int frameCount)
    {
        return PaddingTop + GetTextSectionHeight(textCount) + Gap + GetFrameSectionHeight(frameCount) + PaddingBottom;
    }

    private static double GetTextSectionHeight(int count) =>
        count == 0
            ? EmptyTrackHeight
            : (count * LayerTrackHeight) + ((count - 1) * Gap);

    private static double GetFrameSectionHeight(int count) =>
        count == 0
            ? EmptyTrackHeight
            : (count * LayerTrackHeight) + ((count - 1) * Gap);

    private double ComputeDesiredHeight() =>
        ComputeDesiredHeight(_textLayers.Count, _frameLayers.Count);

    private double GetTextSectionHeight() => GetTextSectionHeight(_textLayers.Count);

    private double GetFrameSectionHeight() => GetFrameSectionHeight(_frameLayers.Count);

    private Rect GetTextBarRect(int index, double actualWidth)
    {
        if (index < 0 || index >= _textLayers.Count)
        {
            return Rect.Empty;
        }

        TimedTextOverlay layer = _textLayers[index];
        double top = PaddingTop + (index * (LayerTrackHeight + Gap));
        double barTop = top + ((LayerTrackHeight - BarHeight) / 2.0);
        double timelineWidth = Math.Max(1, actualWidth - LabelWidth);
        double left = LabelWidth + (Math.Clamp(layer.StartMs / _durationMs, 0, 1) * timelineWidth);
        double right = LabelWidth + (Math.Clamp(layer.EndMs / _durationMs, 0, 1) * timelineWidth);
        return new Rect(left, barTop, Math.Max(MinBarWidth, right - left), BarHeight);
    }

    private Rect GetFrameBarRect(int index, double actualWidth)
    {
        if (index < 0 || index >= _frameLayers.Count)
        {
            return Rect.Empty;
        }

        FrameEditLayer layer = _frameLayers[index];
        double frameSectionTop = PaddingTop + GetTextSectionHeight() + Gap;
        double top = frameSectionTop + (index * (LayerTrackHeight + Gap));
        double barTop = top + ((LayerTrackHeight - BarHeight) / 2.0);
        double timelineWidth = Math.Max(1, actualWidth - LabelWidth);
        double left = LabelWidth + (Math.Clamp(layer.StartMs / _durationMs, 0, 1) * timelineWidth);
        double right = LabelWidth + (Math.Clamp(layer.EndMs / _durationMs, 0, 1) * timelineWidth);
        return new Rect(left, barTop, Math.Max(MinBarWidth, right - left), BarHeight);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TimelineAutomationPeer(this);

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    private sealed class TimelineAutomationPeer(VideoLayerTimeline owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => "VideoLayerTimeline";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override bool IsControlElementCore() => true;
    }

    private bool HitTestBar(Point point, out bool isText, out int index, out Rect barRect)
    {
        double width = ActualWidth;
        for (int i = _frameLayers.Count - 1; i >= 0; i--)
        {
            Rect bar = GetFrameBarRect(i, width);
            Rect hit = bar;
            hit.Inflate(4, 3);
            if (hit.Contains(point))
            {
                isText = false;
                index = i;
                barRect = bar;
                return true;
            }
        }

        for (int i = _textLayers.Count - 1; i >= 0; i--)
        {
            Rect bar = GetTextBarRect(i, width);
            Rect hit = bar;
            hit.Inflate(4, 3);
            if (hit.Contains(point))
            {
                isText = true;
                index = i;
                barRect = bar;
                return true;
            }
        }

        isText = false;
        index = -1;
        barRect = Rect.Empty;
        return false;
    }

    private bool HitTestRow(Point point, out bool isText, out int index)
    {
        double width = ActualWidth;
        if (point.X < 0 || point.X > width)
        {
            isText = false;
            index = -1;
            return false;
        }

        if (_textLayers.Count > 0)
        {
            for (int i = 0; i < _textLayers.Count; i++)
            {
                double top = PaddingTop + (i * (LayerTrackHeight + Gap));
                if (point.Y >= top && point.Y <= top + LayerTrackHeight)
                {
                    isText = true;
                    index = i;
                    return true;
                }
            }
        }

        if (_frameLayers.Count > 0)
        {
            double frameSectionTop = PaddingTop + GetTextSectionHeight() + Gap;
            for (int i = 0; i < _frameLayers.Count; i++)
            {
                double top = frameSectionTop + (i * (LayerTrackHeight + Gap));
                if (point.Y >= top && point.Y <= top + LayerTrackHeight)
                {
                    isText = false;
                    index = i;
                    return true;
                }
            }
        }

        isText = false;
        index = -1;
        return false;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Point point = e.GetPosition(this);

        if (HitTestBar(point, out bool isText, out int index, out Rect bar))
        {
            _ = Focus();
            Guid layerId = isText ? _textLayers[index].Id : _frameLayers[index].Id;
            double startMs = isText ? _textLayers[index].StartMs : _frameLayers[index].StartMs;
            double endMs = isText ? _textLayers[index].EndMs : _frameLayers[index].EndMs;

            _selectedId = layerId;
            NotifyLayerSelected();

            double handleZone = Math.Min(HandleZoneWidth, bar.Width / 3.0);
            if (point.X <= bar.Left + handleZone)
            {
                _dragAction = DragAction.TrimStart;
                Cursor = Cursors.SizeWE;
            }
            else if (point.X >= bar.Right - handleZone)
            {
                _dragAction = DragAction.TrimEnd;
                Cursor = Cursors.SizeWE;
            }
            else
            {
                _dragAction = DragAction.MoveBody;
                Cursor = Cursors.SizeAll;
            }

            _dragInitialStartMs = startMs;
            _dragInitialEndMs = endMs;
            _dragStartMouseX = point.X;
            _dragTargetId = layerId;
            _dragTargetIsText = isText;
            _dragging = CaptureMouse();
            if (!_dragging)
            {
                _dragAction = DragAction.None;
                Cursor = Cursors.Hand;
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (HitTestRow(point, out bool rowIsText, out int rowIndex))
        {
            _ = Focus();
            Guid layerId = rowIsText ? _textLayers[rowIndex].Id : _frameLayers[rowIndex].Id;
            _selectedId = layerId;
            NotifyLayerSelected();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point point = e.GetPosition(this);

        if (_dragging)
        {
            double timelineWidth = Math.Max(1, ActualWidth - LabelWidth);
            double deltaX = point.X - _dragStartMouseX;
            double deltaMs = (deltaX / timelineWidth) * _durationMs;
            double currentMouseTimeMs = Math.Clamp(((point.X - LabelWidth) / timelineWidth) * _durationMs, 0, _durationMs);

            if (_dragTargetIsText)
            {
                TimedTextOverlay? target = null;
                for (int i = 0; i < _textLayers.Count; i++)
                {
                    if (_textLayers[i].Id == _dragTargetId)
                    {
                        target = _textLayers[i];
                        break;
                    }
                }

                if (target is not null)
                {
                    ApplyDrag(target.StartMs, target.EndMs, out double newStart, out double newEnd, deltaMs, currentMouseTimeMs);
                    if (Math.Abs(target.StartMs - newStart) > 0.01 || Math.Abs(target.EndMs - newEnd) > 0.01)
                    {
                        target.StartMs = newStart;
                        target.EndMs = newEnd;
                        NotifyTimingChanged();
                        AutomationProperties.SetName(this, $"텍스트 표시 시간: {target.StartMs / 1000:0.00}초부터 {target.EndMs / 1000:0.00}초까지");
                        InvalidateVisual();
                    }
                }
            }
            else
            {
                FrameEditLayer? target = null;
                for (int i = 0; i < _frameLayers.Count; i++)
                {
                    if (_frameLayers[i].Id == _dragTargetId)
                    {
                        target = _frameLayers[i];
                        break;
                    }
                }

                if (target is not null)
                {
                    ApplyDrag(target.StartMs, target.EndMs, out double newStart, out double newEnd, deltaMs, currentMouseTimeMs);
                    if (Math.Abs(target.StartMs - newStart) > 0.01 || Math.Abs(target.EndMs - newEnd) > 0.01)
                    {
                        target.StartMs = newStart;
                        target.EndMs = newEnd;
                        NotifyTimingChanged();
                        AutomationProperties.SetName(this, $"프레임 표시 시간: {target.StartMs / 1000:0.00}초부터 {target.EndMs / 1000:0.00}초까지");
                        InvalidateVisual();
                    }
                }
            }

            e.Handled = true;
            return;
        }

        if (HitTestBar(point, out _, out _, out Rect hoverBar))
        {
            double handleZone = Math.Min(HandleZoneWidth, hoverBar.Width / 3.0);
            if (point.X <= hoverBar.Left + handleZone || point.X >= hoverBar.Right - handleZone)
            {
                Cursor = Cursors.SizeWE;
            }
            else
            {
                Cursor = Cursors.SizeAll;
            }
        }
        else
        {
            Cursor = Cursors.Hand;
        }
    }

    private void ApplyDrag(
        double currentStart,
        double currentEnd,
        out double newStart,
        out double newEnd,
        double deltaMs,
        double mouseTimeMs)
    {
        switch (_dragAction)
        {
            case DragAction.TrimStart:
                (newStart, newEnd) = TextLayerTiming.Resize(
                    _dragInitialStartMs, _dragInitialEndMs, _durationMs, startHandle: true, targetMs: mouseTimeMs);
                break;

            case DragAction.TrimEnd:
                (newStart, newEnd) = TextLayerTiming.Resize(
                    _dragInitialStartMs, _dragInitialEndMs, _durationMs, startHandle: false, targetMs: mouseTimeMs);
                break;

            case DragAction.MoveBody:
                double duration = _dragInitialEndMs - _dragInitialStartMs;
                double maxStart = Math.Max(0, _durationMs - duration);
                newStart = Math.Clamp(_dragInitialStartMs + deltaMs, 0, maxStart);
                newEnd = Math.Min(_durationMs, newStart + duration);
                break;

            default:
                newStart = currentStart;
                newEnd = currentEnd;
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging)
        {
            _dragging = false;
            _dragAction = DragAction.None;
            ReleaseMouseCapture();
            Cursor = Cursors.Hand;
            NotifyTimingGestureCompleted();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            _dragAction = DragAction.None;
            Cursor = Cursors.Hand;
            NotifyTimingGestureCompleted();
        }

        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_dragging)
        {
            Cursor = Cursors.Hand;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape && _dragging)
        {
            if (_dragTargetIsText)
            {
                for (int i = 0; i < _textLayers.Count; i++)
                {
                    if (_textLayers[i].Id == _dragTargetId)
                    {
                        _textLayers[i].StartMs = _dragInitialStartMs;
                        _textLayers[i].EndMs = _dragInitialEndMs;
                        break;
                    }
                }
            }
            else
            {
                for (int i = 0; i < _frameLayers.Count; i++)
                {
                    if (_frameLayers[i].Id == _dragTargetId)
                    {
                        _frameLayers[i].StartMs = _dragInitialStartMs;
                        _frameLayers[i].EndMs = _dragInitialEndMs;
                        break;
                    }
                }
            }

            _dragging = false;
            _dragAction = DragAction.None;
            ReleaseMouseCapture();
            Cursor = Cursors.Hand;
            NotifyTimingChanged();
            NotifyTimingGestureCompleted();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        int totalCount = _textLayers.Count + _frameLayers.Count;
        if (e.Key is Key.Up or Key.Down && totalCount > 0)
        {
            int currentIndex = -1;
            for (int i = 0; i < _textLayers.Count; i++)
            {
                if (_textLayers[i].Id == _selectedId)
                {
                    currentIndex = i;
                    break;
                }
            }

            if (currentIndex < 0)
            {
                for (int i = 0; i < _frameLayers.Count; i++)
                {
                    if (_frameLayers[i].Id == _selectedId)
                    {
                        currentIndex = _textLayers.Count + i;
                        break;
                    }
                }
            }

            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            int nextIndex = (currentIndex + (e.Key == Key.Up ? totalCount - 1 : 1)) % totalCount;
            if (nextIndex < _textLayers.Count)
            {
                _selectedId = _textLayers[nextIndex].Id;
            }
            else
            {
                _selectedId = _frameLayers[nextIndex - _textLayers.Count].Id;
            }

            NotifyLayerSelected();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Left or Key.Right && _selectedId.HasValue)
        {
            bool isStartHandle = !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 10 : 100;
            double delta = e.Key == Key.Left ? -step : step;

            for (int i = 0; i < _textLayers.Count; i++)
            {
                if (_textLayers[i].Id == _selectedId.Value)
                {
                    TimedTextOverlay text = _textLayers[i];
                    double target = (isStartHandle ? text.StartMs : text.EndMs) + delta;
                    (text.StartMs, text.EndMs) = TextLayerTiming.Resize(
                        text.StartMs, text.EndMs, _durationMs, isStartHandle, target);
                    NotifyTimingChanged();
                    NotifyTimingGestureCompleted();
                    AutomationProperties.SetName(this, $"텍스트 표시 시간: {text.StartMs / 1000:0.00}초부터 {text.EndMs / 1000:0.00}초까지");
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
            }

            for (int i = 0; i < _frameLayers.Count; i++)
            {
                if (_frameLayers[i].Id == _selectedId.Value)
                {
                    FrameEditLayer frame = _frameLayers[i];
                    double target = (isStartHandle ? frame.StartMs : frame.EndMs) + delta;
                    (frame.StartMs, frame.EndMs) = TextLayerTiming.Resize(
                        frame.StartMs, frame.EndMs, _durationMs, isStartHandle, target);
                    NotifyTimingChanged();
                    NotifyTimingGestureCompleted();
                    AutomationProperties.SetName(this, $"프레임 표시 시간: {frame.StartMs / 1000:0.00}초부터 {frame.EndMs / 1000:0.00}초까지");
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
            }
        }
    }

    private void NotifyLayerSelected()
    {
        LayerSelected?.Invoke(this, EventArgs.Empty);
        TextLayerSelected?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyTimingChanged()
    {
        LayerTimingChanged?.Invoke(this, EventArgs.Empty);
        TextTimingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyTimingGestureCompleted()
    {
        LayerTimingInteractionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void DrawText(
        DrawingContext dc,
        string text,
        double x,
        double y,
        Brush brush,
        double size,
        double maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
        {
            return;
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            _typeface,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(formatted, new Point(x, y));
    }

    private static string OneLine(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.IndexOf('\r') < 0 && text.IndexOf('\n') < 0
            ? text
            : text.Replace('\r', ' ').Replace('\n', ' ');
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush ResolveBrush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? Frozen(fallback);

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        return pen;
    }

    private enum DragAction
    {
        None,
        TrimStart,
        TrimEnd,
        MoveBody,
    }
}
