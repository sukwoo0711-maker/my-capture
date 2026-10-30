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
/// Fits the frozen frame inside a canvas margin, capped at one device pixel per image
/// pixel. Image coordinates remain physical pixels at every display scale.
/// </para>
/// <para>
/// Annotation coordinates are in <em>selected-image</em> pixels. The surface adds the crop
/// origin to turn them into frozen-frame pixels, then scales to DIP. The controller and the
/// document never see the window's DPI.
/// </para>
/// </remarks>
internal sealed class AnnotationEditorSurface : FrameworkElement
{
    internal const double CanvasInset = 16;
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
    private readonly Pen _edgeShadowPen;
    private readonly Pen _cropEdgePen;
    private (double Scale, Point Origin) _cachedFrameLayout;
    private Size _cachedRenderSize;
    private double _pixelsPerDip;
    private Point _deviceOffset;
    private bool _frameLayoutValid;
    private PointD? _pointerPreviewPoint;

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
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

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
        // Drawn on every paint; one follower pen each instead of a new one per paint.
        _edgeShadowPen = new Pen(_handleFillBrush, 4);
        _cropEdgePen = new Pen(_selectionBrush, 2);

        _controller.VisualInvalidated += (_, _) => InvalidateVisual();
        _controller.SelectionChanged += (_, _) => InvalidateVisual();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>The visual frame and crop used by this surface.</summary>
    internal FrozenFrame Frame => _frame;

    internal string? SourceWindowTitle { get; set; }
    internal event EventHandler? FrameLayoutChanged;
    internal CropSelection? PendingCrop { get; set; }
    internal IReadOnlyList<RectD> PendingMosaicRegions { get; set; } = [];
    internal RectD? MosaicGestureRegion { get; set; }

    /// <summary>Updates the transient pointer footprint. It never enters the annotation document.</summary>
    internal void UpdatePointerPreview(PointD? imagePoint, bool visible = true)
    {
        if (PendingCrop is not null || _controller.Tool is not (EditorTool.Pen or EditorTool.Highlighter or EditorTool.StepNumber))
        {
            // Tool/crop transitions already invalidate the surface. Plain hover for
            // tools without a footprint must not repaint the annotation document.
            _pointerPreviewPoint = null;
            return;
        }
        PointD? next = visible && imagePoint is { } point
            && point.X >= 0 && point.Y >= 0 && point.X <= _cropRegion.Width && point.Y <= _cropRegion.Height
            ? imagePoint : null;
        if (_pointerPreviewPoint == next) return;
        _pointerPreviewPoint = next;
        InvalidateVisual();
    }

    internal Rect? BrushOutlineDip
    {
        get
        {
            if (_pointerPreviewPoint is not { } point || PendingCrop is not null
                || _controller.Tool is not (EditorTool.Pen or EditorTool.Highlighter)) return null;
            double width = _controller.Tool == EditorTool.Highlighter
                ? Math.Max(12, _controller.StrokeThickness) : _controller.StrokeThickness;
            return BrushPreviewBounds(ToSurfacePoint(point), width, DipPerPixel);
        }
    }

    internal static Rect BrushPreviewBounds(Point centre, double strokeWidth, double dipPerPixel)
    {
        double diameter = Math.Max(0, strokeWidth * dipPerPixel);
        return new Rect(centre.X - diameter / 2, centre.Y - diameter / 2, diameter, diameter);
    }

    /// <summary>
    /// Replaces the base bitmap and crop (a rotation swaps width and height) and repaints.
    /// Only the editor's document-rotation action may call this.
    /// </summary>
    internal void ReplaceFrame(FrozenFrame frame, RectD cropRegion)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
        _cropRegion = cropRegion.Normalized();
        _frameLayoutValid = false;
        InvalidateVisual();
    }

    internal RectD CropRegion => _cropRegion;

    /// <summary>Device-independent units per frozen-frame physical pixel.</summary>
    internal double DipPerPixel => FrameLayout.Scale;

    /// <summary>Top-left DIP of the fitted, letterboxed frame.</summary>
    internal Point FrameOrigin => FrameLayout.Origin;

    private (double Scale, Point Origin) FrameLayout
    {
        get
        {
            Size renderSize = RenderSize;
            // Arrange can be followed by a coordinate read before SizeChanged is dispatched.
            // This cheap size check also covers that interval without consulting the tree.
            if (!_frameLayoutValid || _cachedRenderSize != renderSize)
            {
                _cachedFrameLayout = CalculateFrameLayout(renderSize,
                    new Size(_frame.PixelWidth, _frame.PixelHeight), _pixelsPerDip, _deviceOffset);
                _cachedRenderSize = renderSize;
                _frameLayoutValid = true;
            }
            return _cachedFrameLayout;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LayoutUpdated += OnLayoutUpdated;
        RefreshDeviceOffset(invalidateVisual: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => LayoutUpdated -= OnLayoutUpdated;

    private void OnLayoutUpdated(object? sender, EventArgs e) => RefreshDeviceOffset(invalidateVisual: true);

    private void RefreshDeviceOffset(bool invalidateVisual)
    {
        Point deviceOffset = default;
        // Layout rounding normally places this surface on whole device pixels. Include
        // fractional layout offsets, but not the entrance animation's RenderTransform:
        // moving the whole editor must not change its available canvas or image scale.
        // Only layout/render notifications walk the tree; pointer-coordinate reads reuse it.
        if (PresentationSource.FromVisual(this) is { RootVisual: Visual root, CompositionTarget: { } target })
        {
            Vector layoutOffset = default;
            for (Visual? visual = this; visual is not null && !ReferenceEquals(visual, root);
                visual = VisualTreeHelper.GetParent(visual) as Visual)
                layoutOffset += VisualTreeHelper.GetOffset(visual);
            deviceOffset = target.TransformToDevice.Transform(new Point(layoutOffset.X, layoutOffset.Y));
        }
        if (_deviceOffset == deviceOffset) return;
        _deviceOffset = deviceOffset;
        _frameLayoutValid = false;
        if (invalidateVisual) InvalidateVisual();
        FrameLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Fits and centres the frame on the device grid; the offset is in device pixels.</summary>
    internal static (double Scale, Point Origin) CalculateFrameLayout(
        Size viewport, Size pixels, double pixelsPerDip, Point deviceOffset = default)
    {
        double dpi = double.IsFinite(pixelsPerDip) && pixelsPerDip > 0 ? pixelsPerDip : 1;
        if (viewport.Width <= 0 || viewport.Height <= 0 || pixels.Width <= 0 || pixels.Height <= 0)
            return (1 / dpi, new Point());

        // Round the available bounds inward so snapping never consumes the crop grab margin.
        // For a normal rounded layout these are exactly the 16-DIP inset boundaries.
        double left = (Math.Ceiling(CanvasInset * dpi + deviceOffset.X) - deviceOffset.X) / dpi;
        double top = (Math.Ceiling(CanvasInset * dpi + deviceOffset.Y) - deviceOffset.Y) / dpi;
        double right = (Math.Floor((viewport.Width - CanvasInset) * dpi + deviceOffset.X) - deviceOffset.X) / dpi;
        double bottom = (Math.Floor((viewport.Height - CanvasInset) * dpi + deviceOffset.Y) - deviceOffset.Y) / dpi;
        double scale = Math.Max(double.Epsilon,
            Math.Min(1 / dpi, Math.Min((right - left) / pixels.Width, (bottom - top) / pixels.Height)));
        double x = (Math.Round((viewport.Width - pixels.Width * scale) / 2 * dpi + deviceOffset.X) - deviceOffset.X) / dpi;
        double y = (Math.Round((viewport.Height - pixels.Height * scale) / 2 * dpi + deviceOffset.Y) - deviceOffset.Y) / dpi;
        double lastX = (Math.Floor((right - pixels.Width * scale) * dpi + deviceOffset.X + 1e-9) - deviceOffset.X) / dpi;
        double lastY = (Math.Floor((bottom - pixels.Height * scale) * dpi + deviceOffset.Y + 1e-9) - deviceOffset.Y) / dpi;
        if (right >= left) x = Math.Clamp(x, left, Math.Max(left, lastX));
        if (bottom >= top) y = Math.Clamp(y, top, Math.Max(top, lastY));
        return (scale, new Point(x, y));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _pixelsPerDip = newDpi.PixelsPerDip;
        _frameLayoutValid = false;
        RefreshDeviceOffset(invalidateVisual: false);
        InvalidateVisual();
        FrameLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _frameLayoutValid = false;
        FrameLayoutChanged?.Invoke(this, EventArgs.Empty);
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
        (double scale, Point origin) = FrameLayout;
        scale = Math.Max(double.Epsilon, scale);
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
        RefreshDeviceOffset(invalidateVisual: false);

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

        var clip = new RectangleGeometry(CropRectDip);
        clip.Freeze();
        var translate = new TranslateTransform(
            origin.X + (_cropRegion.Left * scale),
            origin.Y + (_cropRegion.Top * scale));
        translate.Freeze();
        var scaleToDip = new ScaleTransform(scale, scale);
        scaleToDip.Freeze();
        dc.PushClip(clip);
        dc.PushTransform(translate);
        dc.PushTransform(scaleToDip);

        AnnotationItem? suppress = _controller.EditingText;
        // The editor's frame always starts at the selection origin, so the frame bitmap is the
        // base capture in annotation pixel space that blur layers sample.
        _renderer.Render(dc, _controller.Document, pixelsPerDip, suppress, _frame.Bitmap);
        SourceWindowTitleRenderer.Draw(dc, SourceWindowTitle, _cropRegion.Width, _cropRegion.Height);

        dc.Pop();
        dc.Pop();
        dc.Pop();

        // Opposing neutral edges remain visible on both light and dark image content.
        Rect shadowBounds = CropRectDip;
        shadowBounds.Inflate(2, 2);
        Rect edgeBounds = CropRectDip;
        edgeBounds.Inflate(1, 1);
        dc.DrawRectangle(null, _edgeShadowPen, shadowBounds);
        dc.DrawRectangle(null, _cropEdgePen, edgeBounds);

        if (PendingCrop is { } pending) DrawPendingCrop(dc, pending);
        else DrawSelectionAdorner(dc);
        foreach (RectD region in PendingMosaicRegions) DrawMosaicDraft(dc, region);
        if (MosaicGestureRegion is { } gesture) DrawMosaicDraft(dc, gesture);
        DrawPointerPreview(dc);
    }

    private void DrawPointerPreview(DrawingContext dc)
    {
        if (BrushOutlineDip is { } brush)
        {
            dc.PushClip(new RectangleGeometry(CropRectDip));
            var center = new Point(brush.X + brush.Width / 2, brush.Y + brush.Height / 2);
            // The path follows the true footprint, even for a subpixel brush. Only the
            // contrast edge has a fixed DIP width; the locator follows Windows pointer size.
            dc.DrawEllipse(null, new Pen(Brushes.Black, 3), center, brush.Width / 2, brush.Height / 2);
            dc.DrawEllipse(null, new Pen(Brushes.White, 1), center, brush.Width / 2, brush.Height / 2);
            dc.Pop();
        }
        else if (_pointerPreviewPoint is { } point && PendingCrop is null
            && _controller.Tool == EditorTool.StepNumber
            && _controller.ResolvePointerIntent(point).Action == PointerAction.AddStepNumber
            && _controller.StepNumberPreviewBounds(point) is { } badge)
        {
            Rect bounds = ToSurfaceRect(badge);
            dc.DrawRectangle(null, new Pen(Brushes.Black, 3), bounds);
            dc.DrawRectangle(null, new Pen(Brushes.White, 1), bounds);
        }
    }

    private void DrawMosaicDraft(DrawingContext dc, RectD region)
    {
        Rect bounds = ToSurfaceRect(region);
        dc.PushOpacity(0.12);
        dc.DrawRectangle(_selectionBrush, null, bounds);
        dc.Pop();
        dc.DrawRectangle(null, _edgeShadowPen, bounds);
        dc.DrawRectangle(null, _selectionOutlinePen, bounds);
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
            dc.DrawRectangle(null, _edgeShadowPen, ToSurfaceRect(marquee));
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
        bool rotated = !multiple && selected is TextAnnotation { RotationDegrees: not 0 };
        if (rotated)
            dc.PushTransform(new RotateTransform(((TextAnnotation)selected).RotationDegrees,
                bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
        dc.DrawRectangle(null, _edgeShadowPen, inflated);
        dc.DrawRectangle(null, _selectionOutlinePen, inflated);
        if (rotated) dc.Pop();

        if (multiple || !selected.SupportsResize)
        {
            return;
        }

        foreach (SelectionHandle handle in _controller.GetSelectionHandles(DipPerPixel))
        {
            Point center = ToSurfacePoint(handle.Position);
            dc.DrawRectangle(
                _handleFillBrush,
                _handlePen,
                new Rect(center.X - (HandlePixels / 2), center.Y - (HandlePixels / 2), HandlePixels, HandlePixels));
        }
    }

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
