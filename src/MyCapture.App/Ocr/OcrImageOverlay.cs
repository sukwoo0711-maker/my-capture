using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Ocr;

namespace MyCapture.App.Ocr;

internal sealed class OcrImageOverlay : FrameworkElement
{
    internal static readonly DependencyProperty RegionBrushProperty = DependencyProperty.Register(
        nameof(RegionBrush), typeof(Brush), typeof(OcrImageOverlay),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty RegionHaloBrushProperty = DependencyProperty.Register(
        "RegionHaloBrush", typeof(Brush), typeof(OcrImageOverlay),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    // Numbered badges use the neutral badge colors; the accent is reserved for primary actions.
    private static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        "LabelBrush", typeof(Brush), typeof(OcrImageOverlay),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty LabelTextBrushProperty = DependencyProperty.Register(
        "LabelTextBrush", typeof(Brush), typeof(OcrImageOverlay),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    internal Brush RegionBrush { get => (Brush)GetValue(RegionBrushProperty); set => SetValue(RegionBrushProperty, value); }
    private BitmapSource? _image;
    private IReadOnlyList<OcrRect> _regions = [];
    private IReadOnlyList<string> _labels = [];
    private int? _highlightedRegion;
    private Point? _dragStart;
    private Point _dragEnd;
    private int _activeRegion;
    internal OcrRect? Selection { get; private set; }
    internal BitmapSource? SourceImage => _image;
    internal IReadOnlyList<OcrRect> Regions => _regions;
    /// <summary>Numbers drawn at the regions' corners, one per region, or none.</summary>
    internal IReadOnlyList<string> Labels => _labels;
    internal event EventHandler? SelectionChanged;

    /// <summary>A region drawn with a stronger outline, such as the translation block in focus.</summary>
    internal int? HighlightedRegion
    {
        get => _highlightedRegion;
        set
        {
            if (_highlightedRegion == value) return;
            _highlightedRegion = value;
            InvalidateVisual();
        }
    }

    internal OcrImageOverlay()
    {
        Focusable = true;
        ClipToBounds = true;
        MinHeight = 180;
        Cursor = Cursors.Cross;
        SetResourceReference(RegionBrushProperty, "Text.Primary");
        SetResourceReference(RegionHaloBrushProperty, "Surface.Raised");
        SetResourceReference(LabelBrushProperty, "Surface.BadgeStrong");
        SetResourceReference(LabelTextBrushProperty, "Text.Badge");
        AutomationProperties.SetName(this, UiText.Get("OCR410_OriginalImage"));
        AutomationProperties.SetHelpText(this, UiText.Get("OCR410_SelectionHelp"));
        ToolTip = UiText.Get("OCR410_SelectionHelp");
    }

    internal void SetImage(BitmapSource? image)
    {
        _image = image;
        _dragStart = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    internal void SetRegions(IEnumerable<OcrRect> regions, IReadOnlyList<string>? labels = null)
    {
        var kept = regions.Select((region, index) => (Region: region, Label: labels is null || index >= labels.Count ? null : labels[index]))
            .Where(entry => OcrGeometry.IsValid(entry.Region)).ToArray();
        _regions = [.. kept.Select(entry => entry.Region)];
        _labels = labels is null ? [] : [.. kept.Select(entry => entry.Label ?? "")];
        _highlightedRegion = null;
        _activeRegion = Math.Clamp(_activeRegion, 0, Math.Max(0, _regions.Count - 1));
        InvalidateVisual();
    }

    internal void SetSelection(OcrRect? selection, bool notify = true)
    {
        Selection = selection is not null && OcrGeometry.IsValid(selection.Value) ? selection : null;
        InvalidateVisual();
        if (notify) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    internal OcrRect FittedImage => _image is null ? default
        : OcrGeometry.Fit(ActualWidth, ActualHeight, _image.PixelWidth, _image.PixelHeight);

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_image is null) return;
        OcrRect fit = FittedImage;
        if (!OcrGeometry.IsValid(fit)) return;
        drawing.DrawImage(_image, Rect(fit));
        Brush brush = SystemParameters.HighContrast ? SystemColors.HighlightBrush : RegionBrush;
        Brush halo = SystemParameters.HighContrast ? SystemColors.WindowBrush : (Brush)GetValue(RegionHaloBrushProperty);
        var pen = new Pen(brush, 1);
        foreach (OcrRect region in _regions)
        {
            drawing.DrawRectangle(null, new Pen(halo, 3), ViewRect(region, fit));
            drawing.DrawRectangle(null, pen, ViewRect(region, fit));
        }
        if (_highlightedRegion is int highlighted && highlighted >= 0 && highlighted < _regions.Count)
        {
            drawing.DrawRectangle(null, new Pen(halo, 5), ViewRect(_regions[highlighted], fit));
            drawing.DrawRectangle(null, new Pen(brush, 2.5), ViewRect(_regions[highlighted], fit));
        }
        DrawLabels(drawing, fit);
        if (IsKeyboardFocused && _regions.Count > 0)
        {
            var focusPen = new Pen(brush, 2) { DashStyle = DashStyles.Dash };
            drawing.DrawRectangle(null, new Pen(halo, 4), ViewRect(_regions[_activeRegion], fit));
            drawing.DrawRectangle(null, focusPen, ViewRect(_regions[_activeRegion], fit));
        }
        OcrRect? selection = _dragStart is Point start
            ? OcrGeometry.Selection(start.X, start.Y, _dragEnd.X, _dragEnd.Y,
                fit, _image.PixelWidth, _image.PixelHeight) : Selection;
        if (selection is not null && OcrGeometry.IsValid(selection.Value))
        {
            Rect rectangle = ViewRect(selection.Value, fit);
            drawing.PushOpacity(.16);
            drawing.DrawRectangle(brush, null, rectangle);
            drawing.Pop();
            drawing.DrawRectangle(null, new Pen(halo, 4), rectangle);
            drawing.DrawRectangle(null, new Pen(brush, 2), rectangle);
        }
    }

    /// <summary>
    /// A round number badge just outside each labeled region's top-left corner, so it overlaps the
    /// outline rather than the first letters; badges stay inside the image.
    /// </summary>
    private void DrawLabels(DrawingContext drawing, OcrRect fit)
    {
        if (_labels.Count == 0) return;
        Brush fill = SystemParameters.HighContrast ? SystemColors.HighlightBrush : (Brush)GetValue(LabelBrushProperty);
        Brush text = SystemParameters.HighContrast ? SystemColors.HighlightTextBrush : (Brush)GetValue(LabelTextBrushProperty);
        Brush halo = SystemParameters.HighContrast ? SystemColors.WindowBrush : (Brush)GetValue(RegionHaloBrushProperty);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(TryFindResource("Font.Ui") as FontFamily ?? new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        for (int index = 0; index < _regions.Count && index < _labels.Count; index++)
        {
            if (string.IsNullOrEmpty(_labels[index])) continue;
            var number = new FormattedText(_labels[index], System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 11, text, pixelsPerDip);
            double radius = Math.Max(9, number.Width / 2 + 4);
            Rect region = ViewRect(_regions[index], fit);
            var center = new Point(
                Math.Clamp(region.X - radius + 3, fit.X + radius, Math.Max(fit.X + radius, fit.Right - radius)),
                Math.Clamp(region.Y - radius + 4, fit.Y + radius, Math.Max(fit.Y + radius, fit.Bottom - radius)));
            drawing.DrawEllipse(fill, new Pen(halo, 1.5), center, radius, radius);
            drawing.DrawText(number, new Point(center.X - number.Width / 2, center.Y - number.Height / 2));
        }
    }

    private Rect ViewRect(OcrRect region, OcrRect fit) => new(
        fit.X + region.X / _image!.PixelWidth * fit.Width,
        fit.Y + region.Y / _image.PixelHeight * fit.Height,
        region.Width / _image.PixelWidth * fit.Width, region.Height / _image.PixelHeight * fit.Height);
    private static Rect Rect(OcrRect value) => new(value.X, value.Y, value.Width, value.Height);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Point point = e.GetPosition(this);
        if (_image is null || !OcrGeometry.Contains(FittedImage, point.X, point.Y)) return;
        _ = Focus();
        _dragStart = point;
        _dragEnd = point;
        if (!CaptureMouse()) _dragStart = null;
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStart is null || !IsMouseCaptured) return;
        _dragEnd = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragStart is not Point start || _image is null) return;
        Point end = e.GetPosition(this);
        _dragStart = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        OcrRect fit = FittedImage;
        if ((end - start).Length < 3)
        {
            double x = (end.X - fit.X) / fit.Width * _image.PixelWidth;
            double y = (end.Y - fit.Y) / fit.Height * _image.PixelHeight;
            OcrRect region = _regions.FirstOrDefault(region => OcrGeometry.Contains(region, x, y));
            SetSelection(OcrGeometry.IsValid(region) ? region : null);
        }
        else
        {
            SetSelection(OcrGeometry.Selection(start.X, start.Y, end.X, end.Y,
                fit, _image.PixelWidth, _image.PixelHeight));
        }
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragStart = null;
        InvalidateVisual();
    }

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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && Selection is not null)
        {
            SetSelection(null);
            e.Handled = true;
        }
        else if (_regions.Count > 0)
        {
            if (e.Key is Key.Left or Key.Up)
                _activeRegion = (_activeRegion + _regions.Count - 1) % _regions.Count;
            else if (e.Key is Key.Right or Key.Down)
                _activeRegion = (_activeRegion + 1) % _regions.Count;
            else if (e.Key == Key.Home) _activeRegion = 0;
            else if (e.Key == Key.End) _activeRegion = _regions.Count - 1;
            else if (e.Key is Key.Enter or Key.Space) SetSelection(_regions[_activeRegion]);
            else return;
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new OcrImagePeer(this);
    private sealed class OcrImagePeer(OcrImageOverlay owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(OcrImageOverlay);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
    }
}
