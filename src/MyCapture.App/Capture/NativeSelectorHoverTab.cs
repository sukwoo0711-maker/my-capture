using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Capture;

/// <summary>Coalesced, bounded window label. Its text layouts survive every frame of a glide.</summary>
/// <remarks>
/// A calm chip, not an accent tab: what a click does in the guide text colour, a hairline, then the
/// size it captures in tabular figures and the muted colour. It floats just outside the frame, its
/// left edge on the frame's outer edge, and flips inside the target near the top of the screen or
/// when it would cover the mode bar or the hint below it. The chip's window draws its surface and
/// edge (DWM rounds it on Windows 11); this drawing holds only the text and the hairline.
/// </remarks>
internal sealed class NativeSelectorHoverTab
{
    /// <summary>Chip height, DIPs.</summary>
    internal const double Height = 26;
    /// <summary>Space from the chip's edges to its text, DIPs.</summary>
    internal const double PaddingX = 10;
    /// <summary>Space on each side of the hairline between the words and the size, DIPs.</summary>
    internal const double Gap = 8;
    /// <summary>The hairline's height, DIPs.</summary>
    internal const double HairlineHeight = 12;
    /// <summary>Space between the frame's outer edge and the chip placed above it, DIPs.</summary>
    internal const double FrameGap = 4;
    /// <summary>The chip's offset from the target's top-left corner when it flips inside, DIPs.</summary>
    internal const double InsideInset = 8;

    private readonly Brush _text, _muted, _hairline;
    private readonly Typeface _ui, _digits;
    private readonly string[] _labels = [UiText.Get("CaptureGuide2_ClickWindow"), UiText.Get("CaptureGuide2_ClickMonitor"),
        UiText.Get("CaptureGuide2_ClickWindowText"), UiText.Get("CaptureGuide2_ClickMonitorText"),
        UiText.Get("CaptureGuide2_ClickElement"), UiText.Get("CaptureGuide2_ClickElementText"),
        UiText.Get("CaptureGuide2_ClickWindow") + UiText.Get("CaptureGuide2_ElementWheelHint"),
        UiText.Get("CaptureGuide2_ClickWindowText") + UiText.Get("CaptureGuide2_ElementWheelHint"),
        UiText.Get("CaptureGuide2_ClickScroll")];
    private const int ScrollLabel = 8;
    private string? _delayedLabel;
    private int _delayedIndex = -1, _delayedSeconds;
    private readonly DrawingGroup _drawing = new();
    private FormattedText? _labelText;
    private NativeSelectorTabularText? _sizeText;
    private int _labelIndex = -1, _width, _height;
    private double _scale;
    internal Image Image { get; }
    internal Rect? Bounds { get; private set; }
    internal CaptureCueTabPlacement Placement { get; private set; }
    internal string? TargetLabel { get; private set; }
    internal object? LabelLayout => _labelText;
    internal string? SizeTextForTest => _sizeText?.Text;
    internal Rect HairlineForTest { get; private set; }
    internal DrawingGroup Drawing => _drawing;
    internal int RenderCount { get; private set; }
    internal int LayoutCount { get; private set; }

    internal NativeSelectorHoverTab(IReadOnlyDictionary<string, Brush> palette)
    {
        _text = palette["CaptureGuide.Text"];
        _muted = palette["CaptureGuide.MutedText"];
        _hairline = palette["CaptureGuide.ChipHairline"];
        FontFamily ui = Application.Current?.TryFindResource("Font.Ui") as FontFamily ?? new FontFamily("Segoe UI");
        _ui = new Typeface(ui, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
        _digits = new Typeface(ui, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        Image = new Image { Source = new DrawingImage(_drawing), Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    }

    /// <summary>"W × H" with thin spaces around the sign, as the size read-out shows it.</summary>
    internal static string FormatSize(int width, int height) => $"{width} × {height}";

    /// <summary>
    /// Draws the tab for <paramref name="target"/>. The scroll mode names its own click; an armed
    /// delay (seconds above zero) follows the label of a capture click. <paramref name="frameOutset"/>
    /// is how far, in physical pixels, the frame reaches outside the target.
    /// </summary>
    internal Rect Render(RectD shown, RectD target, bool wholeMonitor, bool textMode, double scale,
        Size viewport, IReadOnlyList<Rect> avoid, bool element = false, bool elementHint = false,
        bool scrollMode = false, int delaySeconds = 0, int frameOutset = CaptureFrameStyle.Outset)
    {
        RenderCount++;
        int index = scrollMode ? ScrollLabel : element ? (textMode ? 5 : 4) : elementHint && !wholeMonitor ? (textMode ? 7 : 6)
            : (textMode ? 2 : 0) + (wholeMonitor ? 1 : 0);
        int delay = scrollMode || textMode ? 0 : Math.Max(0, delaySeconds);
        int width = (int)target.Width, height = (int)target.Height;
        if (_labelText is null || _labelIndex != index || _width != width || _height != height || _scale != scale
            || _delayedSeconds != delay)
        {
            string text = Label(index, delay);
            _labelText = new FormattedText(text, UiText.Culture, FlowDirection.LeftToRight,
                _ui, TypeScale.Caption, _text, scale);
            _sizeText?.Dispose();
            _sizeText = new NativeSelectorTabularText(FormatSize(width, height), _digits, TypeScale.Caption, _muted, scale);
            // The accessible name keeps plain spaces; only the drawn size uses thin ones.
            TargetLabel = text + "  " + $"{width} × {height}";
            _labelIndex = index; _width = width; _height = height; _scale = scale; _delayedSeconds = delay;
            LayoutCount++;
        }
        FormattedText label = _labelText;
        NativeSelectorTabularText dimensions = _sizeText!;
        double pixel = 1 / scale;
        var sizeDip = new Size(PaddingX + label.Width + Gap + pixel + Gap + dimensions.Width + PaddingX, Height);
        var shownDip = new Rect(shown.X / scale, shown.Y / scale, shown.Width / scale, shown.Height / scale);
        (Rect tab, CaptureCueTabPlacement placement) = CaptureCueLayout.PlaceTab(shownDip, sizeDip, viewport,
            avoid, wholeMonitor, pixel, pixel, new CaptureCueLayout.TabSpacing(frameOutset * pixel, FrameGap, InsideInset));
        Bounds = tab;
        Placement = placement;
        Image.Width = tab.Width; Image.Height = tab.Height;
        var local = new Rect(0, 0, tab.Width, tab.Height);
        using DrawingContext dc = _drawing.Open();
        // An invisible full-size rectangle keeps the drawing's bounds at the chip's, so the image
        // never shrinks to its text and shifts.
        dc.DrawRectangle(Brushes.Transparent, null, local);
        dc.PushClip(new RectangleGeometry(local));
        double labelTop = Snap((tab.Height - label.Height) / 2, pixel);
        dc.DrawText(label, new Point(PaddingX, labelTop));
        if (tab.Width + 0.5 >= sizeDip.Width)
        {
            double hairlineX = Snap(PaddingX + label.Width + Gap, pixel);
            HairlineForTest = new Rect(hairlineX, Snap((tab.Height - HairlineHeight) / 2, pixel), pixel, HairlineHeight);
            dc.DrawRectangle(_hairline, null, HairlineForTest);
            dimensions.Draw(dc, new Point(tab.Width - PaddingX - dimensions.Width, Snap((tab.Height - dimensions.Height) / 2, pixel)));
        }
        else HairlineForTest = Rect.Empty;
        dc.Pop();
        return tab;
    }

    private static double Snap(double value, double pixel) => Math.Round(value / pixel) * pixel;

    // The delayed label is built once per label and delay, never per frame.
    private string Label(int index, int delay)
    {
        if (delay <= 0) return _labels[index];
        if (_delayedLabel is null || _delayedIndex != index || _delayedSeconds != delay)
        {
            _delayedLabel = _labels[index] + "  ·  " + UiText.Format("CaptureGuide2_DelaySuffix", delay);
            _delayedIndex = index;
        }
        return _delayedLabel;
    }

    internal void Clear()
    {
        if (_drawing.Children.Count > 0) _drawing.Children.Clear();
        Bounds = null;
        TargetLabel = null;
        _labelIndex = -1;
    }
}
