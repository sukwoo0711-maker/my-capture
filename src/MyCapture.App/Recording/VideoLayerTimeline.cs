using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

/// <summary>A compact two-track, music-editor-style view of non-destructive video layers.</summary>
internal sealed class VideoLayerTimeline : FrameworkElement
{
    private const double LabelWidth = 78;
    private const double TrackHeight = 34;
    private const double Gap = 4;
    private readonly Brush _background;
    private readonly Brush _track;
    private readonly Brush _textLayer;
    private readonly Brush _frameLayer;
    private readonly Brush _foreground;
    private readonly Brush _muted;
    private readonly Pen _gridPen;
    private readonly Pen _playheadPen;
    private readonly Typeface _typeface;
    private IReadOnlyList<TimedTextOverlay> _textLayers = [];
    private IReadOnlyList<FrameEditLayer> _frameLayers = [];
    private double _durationMs = 1;
    private double _playheadMs;
    private Guid? _selectedId;
    private bool _dragging;
    private bool _startHandle;
    private double _dragStartMs;
    private double _dragEndMs;

    internal event EventHandler? TextTimingChanged;
    internal event EventHandler? TextLayerSelected;
    internal Guid? SelectedTextId => _selectedId;

    internal void SelectText(Guid? id)
    {
        _selectedId = id;
        InvalidateVisual();
    }

    internal VideoLayerTimeline()
    {
        _background = ResolveBrush("Timeline.Background", Color.FromRgb(0x0B, 0x0F, 0x17));
        _track = ResolveBrush("Timeline.Track", Color.FromRgb(0x15, 0x1E, 0x2B));
        _textLayer = ResolveBrush("Timeline.TextLayer", Color.FromRgb(0x3B, 0x82, 0xF6));
        _frameLayer = ResolveBrush("Timeline.FrameLayer", Color.FromRgb(0x9B, 0x7E, 0xDE));
        _foreground = ResolveBrush("Text.Badge", Colors.White);
        _muted = ResolveBrush("Text.Muted", Color.FromRgb(0x8E, 0x9C, 0xAF));
        _gridPen = FrozenPen(ResolveBrush("Timeline.Grid", Color.FromRgb(0x2B, 0x3A, 0x50)), 1);
        _playheadPen = FrozenPen(ResolveBrush("Timeline.Playhead", Color.FromRgb(0x7D, 0xD7, 0xF8)), 2);
        FontFamily uiFont = Application.Current?.TryFindResource("Font.Ui") as FontFamily
            ?? new FontFamily("Segoe UI");
        _typeface = new Typeface(uiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        Height = (TrackHeight * 2) + Gap;
        MinWidth = 240;
        SnapsToDevicePixels = true;
        Focusable = true;
        Cursor = Cursors.Hand;
        ToolTip = "텍스트 막대의 양 끝을 드래그해 표시 시간을 조절합니다. 위/아래: 레이어 선택 · 좌/우: 시작 시간 · Shift+좌/우: 끝 시간 · Ctrl: 0.01초 단위";
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
        _textLayers = textLayers ?? [];
        _frameLayers = frameLayers ?? [];
        AutomationProperties.SetHelpText(
            this,
            $"텍스트 레이어 {_textLayers.Count}개, 프레임 레이어 {_frameLayers.Count}개. {(string)ToolTip}");
        if (!_textLayers.Any(layer => layer.Id == _selectedId))
        {
            _selectedId = _textLayers.FirstOrDefault()?.Id;
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
        DrawTrack(dc, 0, "T  텍스트", _textLayers.Select(layer =>
            new LayerSpan(layer.StartMs, layer.EndMs, OneLine(layer.Text))).ToList(), _textLayer, hatch: false);
        DrawTrack(dc, TrackHeight + Gap, "F  프레임", _frameLayers.Select(layer =>
            new LayerSpan(layer.StartMs, layer.EndMs, layer.Name)).ToList(), _frameLayer, hatch: true);

        double timelineWidth = width - LabelWidth;
        double playheadX = LabelWidth + ((_playheadMs / _durationMs) * timelineWidth);
        dc.DrawLine(_playheadPen, new Point(playheadX, 0), new Point(playheadX, height));
        for (int index = 0; index < _textLayers.Count; index++)
        {
            Rect bar = TextBar(index);
            bool selected = _textLayers[index].Id == _selectedId;
            var pen = selected ? _playheadPen : _gridPen;
            dc.DrawRoundedRectangle(null, pen, bar, 3, 3);
            dc.DrawRectangle(_foreground, null, new Rect(bar.Left, bar.Top + 3, Math.Min(4, bar.Width / 2), bar.Height - 6));
            dc.DrawRectangle(_foreground, null, new Rect(bar.Right - Math.Min(4, bar.Width / 2), bar.Top + 3, Math.Min(4, bar.Width / 2), bar.Height - 6));
        }
    }

    private Rect TextBar(int index)
    {
        TimedTextOverlay layer = _textLayers[index];
        double width = Math.Max(1, ActualWidth - LabelWidth);
        double left = LabelWidth + Math.Clamp(layer.StartMs / _durationMs, 0, 1) * width;
        double right = LabelWidth + Math.Clamp(layer.EndMs / _durationMs, 0, 1) * width;
        return new Rect(left, 5 + ((index % 2) * 4), Math.Max(3, right - left), 20);
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

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Point point = e.GetPosition(this);
        for (int index = _textLayers.Count - 1; index >= 0; index--)
        {
            Rect bar = TextBar(index);
            Rect hit = bar;
            hit.Inflate(8, 3);
            if (!hit.Contains(point))
            {
                continue;
            }

            _ = Focus();
            TimedTextOverlay layer = _textLayers[index];
            _selectedId = layer.Id;
            TextLayerSelected?.Invoke(this, EventArgs.Empty);
            _startHandle = Math.Abs(point.X - bar.Left) <= Math.Abs(point.X - bar.Right);
            if (Math.Min(Math.Abs(point.X - bar.Left), Math.Abs(point.X - bar.Right)) <= 12)
            {
                _dragStartMs = layer.StartMs;
                _dragEndMs = layer.EndMs;
                _dragging = CaptureMouse();
                Cursor = Cursors.SizeWE;
            }

            InvalidateVisual();
            e.Handled = true;
            break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            double target = (e.GetPosition(this).X - LabelWidth) / Math.Max(1, ActualWidth - LabelWidth) * _durationMs;
            ResizeSelected(target);
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Hand;
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _dragging = false;
        Cursor = Cursors.Hand;
        base.OnLostMouseCapture(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        TimedTextOverlay? selected = _textLayers.FirstOrDefault(layer => layer.Id == _selectedId);
        if (e.Key == Key.Escape && _dragging && selected is not null)
        {
            selected.StartMs = _dragStartMs;
            selected.EndMs = _dragEndMs;
            ReleaseMouseCapture();
            TextTimingChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down && _textLayers.Count > 0)
        {
            int index = Math.Max(0, _textLayers.ToList().FindIndex(layer => layer.Id == _selectedId));
            index = (index + (e.Key == Key.Up ? _textLayers.Count - 1 : 1)) % _textLayers.Count;
            _selectedId = _textLayers[index].Id;
            TextLayerSelected?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Right && selected is not null)
        {
            _startHandle = !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 10 : 100;
            ResizeSelected((_startHandle ? selected.StartMs : selected.EndMs) + (e.Key == Key.Left ? -step : step));
            e.Handled = true;
        }
    }

    private void ResizeSelected(double target)
    {
        TimedTextOverlay? selected = _textLayers.FirstOrDefault(layer => layer.Id == _selectedId);
        if (selected is null)
        {
            return;
        }

        (selected.StartMs, selected.EndMs) = TextLayerTiming.Resize(
            selected.StartMs, selected.EndMs, _durationMs, _startHandle, target);
        TextTimingChanged?.Invoke(this, EventArgs.Empty);
        AutomationProperties.SetName(this, $"텍스트 표시 시간: {selected.StartMs / 1000:0.00}초부터 {selected.EndMs / 1000:0.00}초까지");
        InvalidateVisual();
    }

    private void DrawTrack(
        DrawingContext dc,
        double top,
        string label,
        IReadOnlyList<LayerSpan> layers,
        Brush layerBrush,
        bool hatch)
    {
        double timelineWidth = Math.Max(1, ActualWidth - LabelWidth);
        dc.DrawRectangle(_track, null, new Rect(LabelWidth, top, timelineWidth, TrackHeight));
        dc.DrawLine(_gridPen, new Point(LabelWidth, top), new Point(LabelWidth, top + TrackHeight));
        DrawText(dc, label, 8, top + 8, _muted, 11, maxWidth: LabelWidth - 12);

        for (int index = 0; index < layers.Count; index++)
        {
            LayerSpan layer = layers[index];
            double left = LabelWidth + (Math.Clamp(layer.StartMs / _durationMs, 0, 1) * timelineWidth);
            double right = LabelWidth + (Math.Clamp(layer.EndMs / _durationMs, 0, 1) * timelineWidth);
            double barTop = top + 5 + ((index % 2) * 4);
            var bar = new Rect(left, barTop, Math.Max(3, right - left), 20);
            dc.DrawRoundedRectangle(layerBrush, null, bar, 3, 3);
            if (hatch && bar.Width >= 8)
            {
                for (double x = bar.Left - bar.Height; x < bar.Right; x += 10)
                {
                    dc.DrawLine(_gridPen,
                        new Point(Math.Max(bar.Left, x), bar.Bottom - Math.Max(0, bar.Left - x)),
                        new Point(Math.Min(bar.Right, x + bar.Height), bar.Top + Math.Max(0, x + bar.Height - bar.Right)));
                }
            }

            if (bar.Width >= 42)
            {
                DrawText(dc, layer.Label, bar.Left + 5, bar.Top + 3, _foreground, 10, bar.Width - 10);
            }
        }
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

    private static string OneLine(string text) =>
        (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');

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

    private sealed record LayerSpan(double StartMs, double EndMs, string Label);
}
