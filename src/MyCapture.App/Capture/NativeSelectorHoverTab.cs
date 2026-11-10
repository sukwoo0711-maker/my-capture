using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Capture;

/// <summary>Coalesced, bounded window label. Its text layouts survive every frame of a glide.</summary>
internal sealed class NativeSelectorHoverTab
{
    private readonly Brush _accent, _text;
    private readonly Typeface _ui, _mono;
    private readonly string[] _labels = [UiText.Get("CaptureGuide2_ClickWindow"), UiText.Get("CaptureGuide2_ClickMonitor"),
        UiText.Get("CaptureGuide2_ClickWindowText"), UiText.Get("CaptureGuide2_ClickMonitorText"),
        UiText.Get("CaptureGuide2_ClickElement"), UiText.Get("CaptureGuide2_ClickElementText"),
        UiText.Get("CaptureGuide2_ClickWindow") + UiText.Get("CaptureGuide2_ElementWheelHint"),
        UiText.Get("CaptureGuide2_ClickWindowText") + UiText.Get("CaptureGuide2_ElementWheelHint")];
    private readonly DrawingGroup _drawing = new();
    private FormattedText? _labelText, _sizeText;
    private int _labelIndex = -1, _width, _height;
    private double _scale;
    internal Image Image { get; }
    internal Rect? Bounds { get; private set; }
    internal string? TargetLabel { get; private set; }
    internal object? LabelLayout => _labelText;
    internal DrawingGroup Drawing => _drawing;
    internal int RenderCount { get; private set; }
    internal int LayoutCount { get; private set; }

    internal NativeSelectorHoverTab(IReadOnlyDictionary<string, Brush> palette)
    {
        _accent = palette["CaptureGuide.SelectionBorder"];
        _text = palette["CaptureGuide.AccentText"];
        _ui = new Typeface(Application.Current?.TryFindResource("Font.Ui") as FontFamily ?? new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        _mono = new Typeface(Application.Current?.TryFindResource("Font.Mono") as FontFamily ?? new FontFamily("Consolas"),
            FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
        Image = new Image { Source = new DrawingImage(_drawing), Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    }

    internal Rect Render(RectD shown, RectD target, bool wholeMonitor, bool textMode, double scale,
        Size viewport, IReadOnlyList<Rect> avoid, bool element = false, bool elementHint = false)
    {
        RenderCount++;
        int index = element ? (textMode ? 5 : 4) : elementHint && !wholeMonitor ? (textMode ? 7 : 6)
            : (textMode ? 2 : 0) + (wholeMonitor ? 1 : 0);
        int width = (int)target.Width, height = (int)target.Height;
        if (_labelText is null || _labelIndex != index || _width != width || _height != height || _scale != scale)
        {
            string size = $"{width} × {height}";
            _labelText = new FormattedText(_labels[index], UiText.Culture, FlowDirection.LeftToRight,
                _ui, TypeScale.Caption, _text, scale);
            _sizeText = new FormattedText(size, UiText.Culture, FlowDirection.LeftToRight,
                _mono, TypeScale.Caption, _text, scale);
            TargetLabel = _labels[index] + "  " + size;
            _labelIndex = index; _width = width; _height = height; _scale = scale;
            LayoutCount++;
        }
        FormattedText label = _labelText, dimensions = _sizeText!;
        var sizeDip = new Size(label.Width + 10 + dimensions.Width + 18, Math.Max(label.Height, dimensions.Height) + 8);
        var shownDip = new Rect(shown.X / scale, shown.Y / scale, shown.Width / scale, shown.Height / scale);
        (Rect tab, CaptureCueTabPlacement placement) = CaptureCueLayout.PlaceTab(shownDip, sizeDip, viewport,
            avoid, wholeMonitor, 1 / scale, 1 / scale);
        Bounds = tab;
        Image.Width = tab.Width; Image.Height = tab.Height;
        var local = new Rect(0, 0, tab.Width, tab.Height);
        using DrawingContext dc = _drawing.Open();
        dc.DrawGeometry(_accent, null, CaptureOverlayView.TabGeometry(local, 6,
            placement is CaptureCueTabPlacement.Above or CaptureCueTabPlacement.Floating,
            placement is not CaptureCueTabPlacement.Above));
        dc.PushClip(new RectangleGeometry(local));
        dc.DrawText(label, new Point(9, (tab.Height - label.Height) / 2));
        if (tab.Width + 0.5 >= sizeDip.Width)
            dc.DrawText(dimensions, new Point(tab.Width - 9 - dimensions.Width, (tab.Height - dimensions.Height) / 2));
        dc.Pop();
        return tab;
    }

    internal void Clear()
    {
        if (_drawing.Children.Count > 0) _drawing.Children.Clear();
        Bounds = null;
        TargetLabel = null;
        _labelIndex = -1;
    }
}
