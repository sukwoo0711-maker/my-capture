using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

/// <summary>A compact two-track, music-editor-style view of non-destructive video layers.</summary>
internal sealed class VideoLayerTimeline : FrameworkElement
{
    private const double LabelWidth = 78;
    private const double TrackHeight = 34;
    private const double Gap = 4;
    private readonly Brush _background = Frozen(Color.FromRgb(0x17, 0x15, 0x12));
    private readonly Brush _track = Frozen(Color.FromRgb(0x28, 0x24, 0x1E));
    private readonly Brush _textLayer = Frozen(Color.FromRgb(0x4B, 0x8E, 0xE8));
    private readonly Brush _frameLayer = Frozen(Color.FromRgb(0xA7, 0x55, 0xD8));
    private readonly Brush _foreground = Frozen(Colors.White);
    private readonly Brush _muted = Frozen(Color.FromRgb(0xB7, 0xB0, 0xA5));
    private readonly Pen _gridPen = FrozenPen(Color.FromRgb(0x45, 0x3F, 0x35), 1);
    private readonly Pen _playheadPen = FrozenPen(Color.FromRgb(0x66, 0xB7, 0xFF), 2);
    private IReadOnlyList<TimedTextOverlay> _textLayers = [];
    private IReadOnlyList<FrameEditLayer> _frameLayers = [];
    private double _durationMs = 1;
    private double _playheadMs;

    internal VideoLayerTimeline()
    {
        Height = (TrackHeight * 2) + Gap;
        MinWidth = 240;
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
        AutomationProperties.SetName(this, "영상 레이어 타임라인");
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
            $"텍스트 레이어 {_textLayers.Count}개, 프레임 레이어 {_frameLayers.Count}개");
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
        DrawTrack(dc, 0, "T  텍스트", _textLayers.Select(layer =>
            new LayerSpan(layer.StartMs, layer.EndMs, OneLine(layer.Text))).ToList(), _textLayer, hatch: false);
        DrawTrack(dc, TrackHeight + Gap, "F  프레임", _frameLayers.Select(layer =>
            new LayerSpan(layer.StartMs, layer.EndMs, layer.Name)).ToList(), _frameLayer, hatch: true);

        double timelineWidth = width - LabelWidth;
        double playheadX = LabelWidth + ((_playheadMs / _durationMs) * timelineWidth);
        dc.DrawLine(_playheadPen, new Point(playheadX, 0), new Point(playheadX, height));
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
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
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

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }

    private sealed record LayerSpan(double StartMs, double EndMs, string Label);
}
