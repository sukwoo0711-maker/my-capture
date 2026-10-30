using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Themes;

namespace MyCapture.App.Gallery;

/// <summary>A read-only image preview with physical-pixel zoom and bounded panning.</summary>
internal sealed class GalleryImagePreviewWindow : Window
{
    private readonly Canvas _viewport;
    private readonly Image _image;
    private readonly string _caption;
    private Point? _panPoint;

    internal GalleryImagePreviewWindow(BitmapSource bitmap, string caption, string accessibleName)
    {
        _caption = caption;
        ViewportModel = new(bitmap.PixelWidth, bitmap.PixelHeight);
        // Keep the source-pixel coordinate system exact before the physical zoom transform.
        // Inherited layout rounding would change odd dimensions at fractional display scales.
        _image = new Image
        {
            Source = bitmap, Width = bitmap.PixelWidth, Height = bitmap.PixelHeight,
            Stretch = Stretch.Fill, UseLayoutRounding = false,
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(_image, accessibleName);
        _viewport = new Canvas { ClipToBounds = true, Background = Brushes.Transparent, Margin = new Thickness(16) };
        _viewport.Children.Add(_image);
        Content = _viewport;
        Width = Math.Min(960, SystemParameters.WorkArea.Width - 48);
        Height = Math.Min(700, SystemParameters.WorkArea.Height - 48);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        StandardWindowTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "Surface.Base");
        _viewport.SizeChanged += (_, _) => UpdateViewport(new Size(_viewport.ActualWidth, _viewport.ActualHeight), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        Loaded += (_, _) => UpdateViewport(new Size(_viewport.ActualWidth, _viewport.ActualHeight), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _viewport.MouseWheel += (_, e) =>
        {
            if (HandleWheel(e.Delta, Keyboard.Modifiers, e.GetPosition(_viewport))) e.Handled = true;
        };
        _viewport.MouseLeftButtonDown += (_, e) =>
        {
            if (!ViewportModel.CanPan || !_viewport.CaptureMouse()) return;
            BeginPan(e.GetPosition(_viewport));
            e.Handled = true;
        };
        _viewport.MouseMove += (_, e) =>
        {
            if (_panPoint is null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { EndPan(); _viewport.ReleaseMouseCapture(); return; }
            PanTo(e.GetPosition(_viewport));
            e.Handled = true;
        };
        _viewport.MouseLeftButtonUp += (_, e) =>
        {
            if (_panPoint is null) return;
            EndPan(); _viewport.ReleaseMouseCapture(); e.Handled = true;
        };
        _viewport.LostMouseCapture += (_, _) => EndPan();
        PreviewKeyDown += (_, e) =>
        {
            if (HandleZoomKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
            else if (e.Key is Key.Escape or Key.Space) { e.Handled = true; Close(); }
        };
        RefreshPresentation();
    }

    internal GalleryImagePreviewViewport ViewportModel { get; }

    internal void UpdateViewport(Size viewport, double pixelsPerDip)
    {
        ViewportModel.Resize(viewport, pixelsPerDip);
        RefreshPresentation();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_viewport is not null) UpdateViewport(new Size(_viewport.ActualWidth, _viewport.ActualHeight), newDpi.PixelsPerDip);
    }

    internal bool HandleWheel(int delta, ModifierKeys modifiers, Point position)
    {
        if (!modifiers.HasFlag(ModifierKeys.Control)) return false;
        ViewportModel.Zoom(Math.Pow(1.2, Math.Clamp(delta / 120d, -100, 100)), position);
        RefreshPresentation();
        return true;
    }

    internal bool HandleZoomKey(Key key, ModifierKeys modifiers)
    {
        if (!modifiers.HasFlag(ModifierKeys.Control)) return false;
        Point center = new(ViewportModel.Viewport.Width / 2, ViewportModel.Viewport.Height / 2);
        switch (key)
        {
            case Key.Add: case Key.OemPlus: ViewportModel.Zoom(1.2, center); break;
            case Key.Subtract: case Key.OemMinus: ViewportModel.Zoom(1 / 1.2, center); break;
            case Key.D0: case Key.NumPad0: ViewportModel.Fit(); break;
            case Key.D1: case Key.NumPad1: ViewportModel.ActualSize(); break;
            default: return false;
        }
        RefreshPresentation();
        return true;
    }

    internal bool BeginPan(Point point)
    {
        if (!ViewportModel.CanPan) return false;
        _panPoint = point;
        RefreshPresentation();
        return true;
    }

    internal void PanTo(Point point)
    {
        if (_panPoint is not { } previous) return;
        ViewportModel.Pan(point - previous);
        _panPoint = point;
        RefreshPresentation();
    }

    internal void EndPan()
    {
        _panPoint = null;
        RefreshPresentation();
    }

    private void RefreshPresentation()
    {
        GalleryImagePreviewViewport model = ViewportModel;
        // Whole device pixels, so 100 % maps each image pixel onto one screen pixel without resampling.
        double ppd = model.PixelsPerDip;
        _image.RenderTransform = new MatrixTransform(model.Scale, 0, 0, model.Scale,
            Math.Round(model.Offset.X * ppd) / ppd, Math.Round(model.Offset.Y * ppd) / ppd);
        _viewport.Cursor = _panPoint is not null ? Cursors.SizeAll : model.CanPan ? Cursors.Hand : Cursors.Arrow;
        // A whole percentage, as the pin's zoom feedback shows it.
        Title = _caption + " · " + model.Percentage.ToString("0", CultureInfo.CurrentCulture) + "%";
    }
}
