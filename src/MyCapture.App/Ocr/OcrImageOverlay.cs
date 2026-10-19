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
    internal Brush RegionBrush { get => (Brush)GetValue(RegionBrushProperty); set => SetValue(RegionBrushProperty, value); }
    private BitmapSource? _image;
    private IReadOnlyList<OcrRect> _regions = [];
    private Point? _dragStart;
    private Point _dragEnd;
    private int _activeRegion;
    internal OcrRect? Selection { get; private set; }
    internal BitmapSource? SourceImage => _image;
    internal IReadOnlyList<OcrRect> Regions => _regions;
    internal event EventHandler? SelectionChanged;

    internal OcrImageOverlay()
    {
        Focusable = true;
        ClipToBounds = true;
        MinHeight = 180;
        Cursor = Cursors.Cross;
        SetResourceReference(RegionBrushProperty, "Text.Primary");
        SetResourceReference(RegionHaloBrushProperty, "Surface.Raised");
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

    internal void SetRegions(IEnumerable<OcrRect> regions)
    {
        _regions = regions.Where(OcrGeometry.IsValid).ToArray();
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
