using System.Windows;

namespace MyCapture.App.Gallery;

/// <summary>Image pixels mapped into a preview's logical viewport; no image metadata DPI is involved.</summary>
internal sealed class GalleryImagePreviewViewport
{
    private enum ViewMode { Fit, ActualSize, Custom }
    private ViewMode _mode;

    internal GalleryImagePreviewViewport(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    internal int PixelWidth { get; }
    internal int PixelHeight { get; }
    internal Size Viewport { get; private set; }
    internal double PixelsPerDip { get; private set; } = 1;
    internal double Scale { get; private set; } = 1;
    internal Vector Offset { get; private set; }
    internal bool IsReady => Viewport.Width > 0 && Viewport.Height > 0;
    internal double ActualScale => 1 / PixelsPerDip;
    internal double FitScale => Math.Min(ActualScale, Math.Min(Viewport.Width / PixelWidth, Viewport.Height / PixelHeight));
    internal double Percentage => Scale * PixelsPerDip * 100;
    internal bool CanPan => IsReady && (PixelWidth * Scale > Viewport.Width || PixelHeight * Scale > Viewport.Height);

    internal void Resize(Size viewport, double pixelsPerDip)
    {
        if (!double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height)
            || viewport.Width <= 0 || viewport.Height <= 0 || !double.IsFinite(pixelsPerDip) || pixelsPerDip <= 0) return;
        Point oldCenter = new(Viewport.Width / 2, Viewport.Height / 2);
        Point imageCenter = IsReady ? ImagePointAt(oldCenter) : new(PixelWidth / 2d, PixelHeight / 2d);
        double physicalZoom = Scale * PixelsPerDip;
        Viewport = viewport;
        PixelsPerDip = pixelsPerDip;
        Scale = _mode switch
        {
            ViewMode.Fit => FitScale,
            ViewMode.ActualSize => ActualScale,
            _ => ClampScale(physicalZoom / pixelsPerDip),
        };
        Offset = _mode == ViewMode.Fit
            ? CenteredOffset()
            : new Vector(Viewport.Width / 2 - imageCenter.X * Scale, Viewport.Height / 2 - imageCenter.Y * Scale);
        ClampOffset();
    }

    internal Point ImagePointAt(Point viewportPoint) => new((viewportPoint.X - Offset.X) / Scale, (viewportPoint.Y - Offset.Y) / Scale);

    internal void Fit()
    {
        if (!IsReady) return;
        _mode = ViewMode.Fit;
        Scale = FitScale;
        Offset = CenteredOffset();
    }

    internal void ActualSize()
    {
        if (!IsReady) return;
        _mode = ViewMode.ActualSize;
        Scale = ActualScale;
        Offset = CenteredOffset();
    }

    internal void Zoom(double factor, Point anchor)
    {
        if (!IsReady || !double.IsFinite(factor) || factor <= 0) return;
        Point imagePoint = ImagePointAt(anchor);
        double next = ClampScale(Scale * factor);
        if (next == Scale) return;
        _mode = ViewMode.Custom;
        Scale = next;
        Offset = new Vector(anchor.X - imagePoint.X * Scale, anchor.Y - imagePoint.Y * Scale);
        ClampOffset();
    }

    internal void Pan(Vector delta)
    {
        if (!CanPan) return;
        Offset += delta;
        ClampOffset();
    }

    private double ClampScale(double scale) => Math.Clamp(scale, Math.Min(ActualScale * .05, FitScale), ActualScale * 16);

    private Vector CenteredOffset() => new((Viewport.Width - PixelWidth * Scale) / 2, (Viewport.Height - PixelHeight * Scale) / 2);

    private void ClampOffset()
    {
        double width = PixelWidth * Scale, height = PixelHeight * Scale;
        Offset = new Vector(width <= Viewport.Width ? (Viewport.Width - width) / 2 : Math.Clamp(Offset.X, Viewport.Width - width, 0),
            height <= Viewport.Height ? (Viewport.Height - height) / 2 : Math.Clamp(Offset.Y, Viewport.Height - height, 0));
    }
}
