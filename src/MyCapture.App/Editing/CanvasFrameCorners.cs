using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace MyCapture.App.Editing;

/// <summary>
/// The rounded corners of the editor's canvas frame, drawn over the canvas. A Border does not clip
/// its child to its rounded corners, and the canvas paints its whole rectangle (the letterbox
/// dimmer, or the image itself when zoomed in), so on its own the canvas squared the frame's
/// corners and hid the outline's arcs.
/// </summary>
/// <remarks>
/// The canvas is not clipped to the outline's rounded inside edge. WPF draws a clip that is not an
/// axis-aligned rectangle through an intermediate surface as large as what it clips: with that clip
/// a 4K editor committed about 30 MB more while open and kept about 14 MB after it closed (the 5.2.0
/// release gate, four alternating pairs against 5.1.0). This element is the canvas's size and paints
/// only where the canvas lies outside that inside edge: what is behind the frame, then the part of
/// the frame's outline that falls there, so the corners look as the clip drew them.
/// </remarks>
internal sealed class CanvasFrameCorners : FrameworkElement
{
    internal static readonly DependencyProperty BehindProperty = DependencyProperty.Register(
        nameof(Behind), typeof(Brush), typeof(CanvasFrameCorners),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    internal static readonly DependencyProperty OutlineProperty = DependencyProperty.Register(
        nameof(Outline), typeof(Brush), typeof(CanvasFrameCorners),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // Arcs flattened this finely stay within a hundredth of a DIP of the outline WPF strokes.
    private const double Tolerance = 0.01;

    private readonly Border _frame;
    private (Size Size, double Inside, double Thickness) _shapesFor;
    private Geometry? _outside;
    private Geometry? _arcs;

    /// <param name="frame">The rounded frame whose child holds this element over the canvas.</param>
    /// <param name="behindKey">The brush resource of the surface the frame sits on.</param>
    internal CanvasFrameCorners(Border frame, string behindKey)
    {
        _frame = frame;
        IsHitTestVisible = false;
        Focusable = false;
        SetResourceReference(BehindProperty, behindKey);
        SetBinding(OutlineProperty, new Binding(nameof(Border.BorderBrush)) { Source = frame });
    }

    /// <summary>What shows outside the frame's outline: the surface the frame sits on.</summary>
    internal Brush? Behind
    {
        get => (Brush?)GetValue(BehindProperty);
        set => SetValue(BehindProperty, value);
    }

    /// <summary>The frame's outline brush.</summary>
    internal Brush? Outline
    {
        get => (Brush?)GetValue(OutlineProperty);
        set => SetValue(OutlineProperty, value);
    }

    /// <summary>
    /// The radius of the outline's inside edge. WPF strokes a Border's corner radius on the outline's
    /// centre line, so the inside edge turns half the outline's thickness tighter around the same
    /// corner centre; that edge runs along this element's sides.
    /// </summary>
    internal double InsideRadius => Math.Max(0, _frame.CornerRadius.TopLeft - _frame.BorderThickness.Left / 2);

    /// <summary>Where the canvas lies outside the outline's inside edge, in the canvas's coordinates.</summary>
    internal Geometry? OutsideCorners
    {
        get
        {
            UpdateShapes();
            return _outside;
        }
    }

    /// <summary>The parts of the frame's outline that lie over the canvas: its four corner arcs.</summary>
    internal Geometry? OutlineArcs
    {
        get
        {
            UpdateShapes();
            return _arcs;
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        UpdateShapes();
        if (_outside is null) return;
        drawingContext.DrawGeometry(Behind, null, _outside);
        if (_arcs is not null) drawingContext.DrawGeometry(Outline, null, _arcs);
    }

    private void UpdateShapes()
    {
        Size size = RenderSize;
        double inside = InsideRadius;
        double thickness = Math.Max(0, _frame.BorderThickness.Left);
        if (_shapesFor == (size, inside, thickness)) return;
        _shapesFor = (size, inside, thickness);
        _outside = null;
        _arcs = null;
        if (inside <= 0 || size.Width <= 0 || size.Height <= 0) return;

        var canvas = new Rect(size);
        var whole = new RectangleGeometry(canvas);
        var insideEdge = new RectangleGeometry(canvas, inside, inside);
        _outside = Frozen(Geometry.Combine(whole, insideEdge, GeometryCombineMode.Exclude, null, Tolerance, ToleranceType.Absolute));
        if (thickness <= 0) return;

        // The outline's outside edge is the frame's own box, around the same corner centres.
        var outsideEdge = new RectangleGeometry(Rect.Inflate(canvas, thickness, thickness), inside + thickness, inside + thickness);
        Geometry outline = Geometry.Combine(outsideEdge, insideEdge, GeometryCombineMode.Exclude, null, Tolerance, ToleranceType.Absolute);
        _arcs = Frozen(Geometry.Combine(outline, whole, GeometryCombineMode.Intersect, null, Tolerance, ToleranceType.Absolute));
    }

    private static Geometry Frozen(Geometry geometry)
    {
        if (geometry.CanFreeze) geometry.Freeze();
        return geometry;
    }
}
