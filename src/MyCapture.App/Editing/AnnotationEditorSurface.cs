using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;

namespace MyCapture.App.Editing;

/// <summary>
/// The drawing surface: the frozen frame, the dimmer outside the selection, the live
/// annotations, and the selection handles.
/// </summary>
/// <remarks>
/// <para>
/// Renders the entire frozen monitor frame at its physical resolution mapped 1:1 to the
/// window's device-independent units. Because the surface fills the window and the window
/// covers the monitor at physical bounds, one DIP maps to <c>1 / DpiScale</c> physical
/// pixels — the mapping is derived from <see cref="ActualWidth"/> over
/// <see cref="FrozenFrame.PixelWidth"/> so it stays correct on any scale factor.
/// </para>
/// <para>
/// Annotation coordinates are in <em>selected-image</em> pixels. The surface adds the crop
/// origin to turn them into frozen-frame pixels, then scales to DIP. The controller and the
/// document never see the window's DPI.
/// </para>
/// </remarks>
internal sealed class AnnotationEditorSurface : FrameworkElement
{
    private const double HandlePixels = 8;
    private static readonly DependencyProperty CropScrimProperty = DependencyProperty.Register(
        "CropScrim", typeof(Brush), typeof(AnnotationEditorSurface), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty CropFrameProperty = DependencyProperty.Register(
        "CropFrame", typeof(Brush), typeof(AnnotationEditorSurface), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty CropFrameShadowProperty = DependencyProperty.Register(
        "CropFrameShadow", typeof(Brush), typeof(AnnotationEditorSurface), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    private FrozenFrame _frame;
    private RectD _cropRegion;
    private readonly AnnotationEditorController _controller;
    private readonly AnnotationRenderer _renderer;
    private readonly Brush _dimmerBrush;
    private readonly Brush _selectionBrush;
    private readonly Brush _handleFillBrush;
    private readonly Pen _handlePen;
    private readonly Pen _selectionOutlinePen;

    internal AnnotationEditorSurface(
        FrozenFrame frame,
        RectD cropRegion,
        AnnotationEditorController controller,
        AnnotationRenderer renderer)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        _cropRegion = cropRegion.Normalized();
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

        Focusable = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        SetResourceReference(CropScrimProperty, "Crop.Scrim");
        SetResourceReference(CropFrameProperty, "Crop.Frame");
        SetResourceReference(CropFrameShadowProperty, "Crop.FrameShadow");

        _dimmerBrush = Resource("Overlay.Dimmer", new SolidColorBrush(Color.FromArgb(115, 0, 0, 0)));
        _selectionBrush = Resource("Text.Primary", Brushes.White);
        _handleFillBrush = Resource("Surface.Raised", Brushes.Black);

        // Keep private follower brushes mutable so an open editor follows theme changes.
        _handlePen = new Pen(_selectionBrush, 2);
        _selectionOutlinePen = new Pen(_selectionBrush, 2) { DashStyle = new DashStyle([4, 3], 0) };

        _controller.VisualInvalidated += (_, _) => InvalidateVisual();
        _controller.SelectionChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>The visual frame and crop used by this surface.</summary>
    internal FrozenFrame Frame => _frame;

    internal string? SourceWindowTitle { get; set; }
    internal CropSelection? PendingCrop { get; set; }

    /// <summary>
    /// Replaces the base bitmap and crop (a rotation swaps width and height) and repaints.
    /// Only the editor's document-rotation action may call this.
    /// </summary>
    internal void ReplaceFrame(FrozenFrame frame, RectD cropRegion)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        _cropRegion = cropRegion.Normalized();
        InvalidateVisual();
    }

    internal RectD CropRegion => _cropRegion;

    /// <summary>Device-independent units per frozen-frame physical pixel.</summary>
    internal double DipPerPixel
    {
        get
        {
            if (ActualWidth <= 0 || ActualHeight <= 0 ||
                _frame.PixelWidth <= 0 || _frame.PixelHeight <= 0)
            {
                return 1.0;
            }

            // A capture overlay has the same aspect ratio as its frame, but a resizable
            // gallery editor does not. Fit by both axes so the frame never extends below or
            // beside the surface (which previously produced negative dimmer rectangles).
            return Math.Max(
                double.Epsilon,
                Math.Min(
                    ActualWidth / _frame.PixelWidth,
                    ActualHeight / _frame.PixelHeight));
        }
    }

    /// <summary>Top-left DIP of the fitted, letterboxed frame.</summary>
    internal Point FrameOrigin
    {
        get
        {
            double scale = DipPerPixel;
            return new Point(
                Math.Max(0, (ActualWidth - (_frame.PixelWidth * scale)) / 2),
                Math.Max(0, (ActualHeight - (_frame.PixelHeight * scale)) / 2));
        }
    }

    internal Rect FrameRectDip
    {
        get
        {
            Point origin = FrameOrigin;
            double scale = DipPerPixel;
            return new Rect(
                origin.X,
                origin.Y,
                _frame.PixelWidth * scale,
                _frame.PixelHeight * scale);
        }
    }

    /// <summary>Maps a point on the surface (DIP) to selected-image pixel coordinates.</summary>
    internal PointD ToImagePoint(Point dip)
    {
        double scale = Math.Max(double.Epsilon, DipPerPixel);
        Point origin = FrameOrigin;
        double framePixelX = (dip.X - origin.X) / scale;
        double framePixelY = (dip.Y - origin.Y) / scale;
        return new PointD(framePixelX - _cropRegion.Left, framePixelY - _cropRegion.Top);
    }

    /// <summary>Maps a selected-image pixel point to a point on the surface (DIP).</summary>
    internal Point ToSurfacePoint(PointD imagePoint)
    {
        double scale = DipPerPixel;
        Point origin = FrameOrigin;
        return new Point(
            origin.X + ((imagePoint.X + _cropRegion.Left) * scale),
            origin.Y + ((imagePoint.Y + _cropRegion.Top) * scale));
    }

    /// <summary>Maps a selected-image pixel rect to a rect on the surface (DIP).</summary>
    internal Rect ToSurfaceRect(RectD imageRect)
    {
        RectD n = imageRect.Normalized();
        Point topLeft = ToSurfacePoint(new PointD(n.Left, n.Top));
        double scale = DipPerPixel;
        return new Rect(topLeft.X, topLeft.Y, n.Width * scale, n.Height * scale);
    }

    /// <summary>Crop region in surface (DIP) coordinates.</summary>
    internal Rect CropRectDip
    {
        get
        {
            Point origin = FrameOrigin;
            double scale = DipPerPixel;
            return new Rect(
                origin.X + (_cropRegion.Left * scale),
                origin.Y + (_cropRegion.Top * scale),
                _cropRegion.Width * scale,
                _cropRegion.Height * scale);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        // Frozen monitor background, fitted uniformly inside the available surface. Capture
        // overlays naturally fill this rectangle; resizable gallery editors may letterbox it.
        dc.DrawImage(_frame.Bitmap, FrameRectDip);

        // Dim everything outside the selected crop so the editable area is unmistakable.
        DrawDimmerOutside(dc, CropRectDip);

        // Annotations are drawn in image-pixel space, clipped to the crop, via one transform.
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double scale = DipPerPixel;
        Point origin = FrameOrigin;

        dc.PushClip(new RectangleGeometry(CropRectDip));
        dc.PushTransform(new TranslateTransform(
            origin.X + (_cropRegion.Left * scale),
            origin.Y + (_cropRegion.Top * scale)));
        dc.PushTransform(new ScaleTransform(scale, scale));

        AnnotationItem? suppress = _controller.EditingText;
        // The editor's frame always starts at the selection origin, so the frame bitmap is the
        // base capture in annotation pixel space that blur layers sample.
        _renderer.Render(dc, _controller.Document, pixelsPerDip, suppress, _frame.Bitmap);
        SourceWindowTitleRenderer.Draw(dc, SourceWindowTitle, _cropRegion.Width, _cropRegion.Height);

        dc.Pop();
        dc.Pop();
        dc.Pop();

        // Opposing neutral edges remain visible on both light and dark image content.
        dc.DrawRectangle(null, new Pen(_handleFillBrush, 4), CropRectDip);
        var cropPen = new Pen(_selectionBrush, 2);
        dc.DrawRectangle(null, cropPen, CropRectDip);

        if (PendingCrop is { } pending) DrawPendingCrop(dc, pending);
        else DrawSelectionAdorner(dc);
    }

    private void DrawPendingCrop(DrawingContext dc, CropSelection pending)
    {
        Rect bounds = ToSurfaceRect(pending.Region);
        DrawDimmerOutside(dc, bounds, (Brush)GetValue(CropScrimProperty));
        var frame = (Brush)GetValue(CropFrameProperty);
        var shadow = (Brush)GetValue(CropFrameShadowProperty);
        var outline = new Pen(frame, 1);
        dc.DrawRectangle(null, new Pen(shadow, 3), bounds);
        dc.DrawRectangle(null, outline, bounds);
        if (pending.IsDragging)
        {
            for (int index = 1; index <= 2; index++)
            {
                double x = bounds.Left + bounds.Width * index / 3;
                double y = bounds.Top + bounds.Height * index / 3;
                dc.DrawLine(outline, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
                dc.DrawLine(outline, new Point(bounds.Left, y), new Point(bounds.Right, y));
            }
        }
        foreach (Point center in HandleCenters(bounds))
            dc.DrawRectangle(frame, new Pen(shadow, 1), new Rect(center.X - 4, center.Y - 4, 8, 8));
        RectD pixels = pending.Region.ToPixelBounds();
        var label = new FormattedText($"{pixels.Width} × {pixels.Height} px",
            UiText.Culture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, frame, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double left = Math.Clamp(bounds.Left + (bounds.Width - label.Width) / 2, 4, Math.Max(4, ActualWidth - label.Width - 12));
        double top = bounds.Bottom + 10 + label.Height < ActualHeight ? bounds.Bottom + 10 : Math.Max(4, bounds.Bottom - label.Height - 14);
        dc.DrawRoundedRectangle(shadow, null, new Rect(left - 5, top - 3, label.Width + 10, label.Height + 6), 3, 3);
        dc.DrawText(label, new Point(left, top));
    }

    private void DrawDimmerOutside(DrawingContext dc, Rect crop, Brush? scrim = null)
    {
        Brush brush = scrim ?? _dimmerBrush;
        Rect bounds = new(0, 0, ActualWidth, ActualHeight);
        Rect visibleCrop = Rect.Intersect(bounds, crop);
        if (visibleCrop.IsEmpty)
        {
            dc.DrawRectangle(brush, null, bounds);
            return;
        }

        DrawIfPositive(dc, new Rect(0, 0, ActualWidth, visibleCrop.Top), brush);
        DrawIfPositive(dc, new Rect(
            0,
            visibleCrop.Bottom,
            ActualWidth,
            Math.Max(0, ActualHeight - visibleCrop.Bottom)), brush);
        DrawIfPositive(dc, new Rect(0, visibleCrop.Top, visibleCrop.Left, visibleCrop.Height), brush);
        DrawIfPositive(dc, new Rect(
            visibleCrop.Right,
            visibleCrop.Top,
            Math.Max(0, ActualWidth - visibleCrop.Right),
            visibleCrop.Height), brush);
    }

    private static void DrawIfPositive(DrawingContext dc, Rect rect, Brush brush)
    {
        if (rect.Width > 0 && rect.Height > 0)
        {
            dc.DrawRectangle(brush, null, rect);
        }
    }

    private void DrawSelectionAdorner(DrawingContext dc)
    {
        if (_controller.MarqueeBounds is { } marquee)
        {
            dc.PushOpacity(0.12);
            dc.DrawRectangle(_selectionBrush, null, ToSurfaceRect(marquee));
            dc.Pop();
            dc.DrawRectangle(null, new Pen(_handleFillBrush, 4), ToSurfaceRect(marquee));
            dc.DrawRectangle(null, _selectionOutlinePen, ToSurfaceRect(marquee));
        }
        AnnotationItem? selected = _controller.Selected;
        if (selected is null || _controller.EditingText is not null)
        {
            return;
        }

        bool multiple = _controller.SelectedItems.Count > 1;
        Rect bounds = ToSurfaceRect(_controller.SelectionBounds ?? selected.Bounds);
        Rect inflated = Rect.Inflate(bounds, 2, 2);
        dc.DrawRectangle(null, new Pen(_handleFillBrush, 4), inflated);
        dc.DrawRectangle(null, _selectionOutlinePen, inflated);

        if (!multiple && !selected.SupportsResize)
        {
            return;
        }

        int index = 0;
        foreach (Point center in HandleCenters(inflated))
        {
            // Same order as HandleCenters; a caption offers no top/bottom handles.
            if (!multiple && AnnotationEditorController.UsableHandle(selected, HandleOrder[index++]) == ResizeHandle.None)
            {
                continue;
            }

            dc.DrawRectangle(
                _handleFillBrush,
                _handlePen,
                new Rect(center.X - (HandlePixels / 2), center.Y - (HandlePixels / 2), HandlePixels, HandlePixels));
        }
    }

    private static readonly ResizeHandle[] HandleOrder =
    [
        ResizeHandle.TopLeft, ResizeHandle.TopCenter, ResizeHandle.TopRight, ResizeHandle.MiddleRight,
        ResizeHandle.BottomRight, ResizeHandle.BottomCenter, ResizeHandle.BottomLeft, ResizeHandle.MiddleLeft,
    ];

    private static IEnumerable<Point> HandleCenters(Rect rect)
    {
        double cx = (rect.Left + rect.Right) / 2;
        double cy = (rect.Top + rect.Bottom) / 2;
        yield return new Point(rect.Left, rect.Top);
        yield return new Point(cx, rect.Top);
        yield return new Point(rect.Right, rect.Top);
        yield return new Point(rect.Right, cy);
        yield return new Point(rect.Right, rect.Bottom);
        yield return new Point(cx, rect.Bottom);
        yield return new Point(rect.Left, rect.Bottom);
        yield return new Point(rect.Left, cy);
    }

    private static Brush Resource(string key, Brush fallback) => Themes.ThemeBrushes.Follow(key, fallback);
}
