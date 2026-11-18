using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MyCapture.App.Themes;

/// <summary>The template switch follows the live palette, including Windows contrast themes.</summary>
public static class ControlLanguage
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ControlLanguage), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));
    public static readonly DependencyProperty PreserveProgressPaletteProperty = DependencyProperty.RegisterAttached(
        "PreserveProgressPalette", typeof(bool), typeof(ControlLanguage), new PropertyMetadata(false));
    public static bool GetPreserveProgressPalette(DependencyObject element) => (bool)element.GetValue(PreserveProgressPaletteProperty);
    public static void SetPreserveProgressPalette(DependencyObject element, bool value) => element.SetValue(PreserveProgressPaletteProperty, value);
    public static readonly DependencyProperty PreserveExpanderMetricsProperty = DependencyProperty.RegisterAttached(
        "PreserveExpanderMetrics", typeof(bool), typeof(ControlLanguage), new PropertyMetadata(false));
    public static bool GetPreserveExpanderMetrics(DependencyObject element) => (bool)element.GetValue(PreserveExpanderMetricsProperty);
    public static void SetPreserveExpanderMetrics(DependencyObject element, bool value) => element.SetValue(PreserveExpanderMetricsProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
}

/// <summary>A render-only bottom contour: its stroke is independent of the input's height.</summary>
public sealed class InputEdge : FrameworkElement
{
    private readonly InputEdgeDrawing _drawing = new();
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(InputEdge), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(InputEdge), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(InputEdge), new FrameworkPropertyMetadata(FoundationMetrics.ControlRadius, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush? Brush { get => (Brush?)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        _drawing.Draw(drawingContext, RenderSize, Thickness, Brush, Radius);
    }
}

/// <summary>Reuses the contour and pen while input geometry and its semantic brush are unchanged.</summary>
internal sealed class InputEdgeDrawing
{
    private Size _size;
    private double _thickness;
    private double _radius;
    private StreamGeometry? _contour;
    private Pen? _pen;

    internal void Draw(DrawingContext drawingContext, Size size, double thickness, Brush? brush, double radius)
    {
        thickness = Math.Min(Math.Max(0, thickness), Math.Min(size.Width, size.Height));
        if (brush is null || thickness <= 0) return;
        if (_contour is null || _size != size || _thickness != thickness || _radius != radius)
        {
            _size = size;
            _thickness = thickness;
            _radius = radius;
            double inset = thickness / 2;
            double arc = Math.Max(0, Math.Min(radius, Math.Min(size.Width, size.Height) / 2) - inset);
            double bottom = size.Height - inset;
            _contour = new StreamGeometry();
            using (StreamGeometryContext path = _contour.Open())
            {
                path.BeginFigure(new Point(inset, bottom - arc), false, false);
                path.ArcTo(new Point(inset + arc, bottom), new Size(arc, arc), 0, false, SweepDirection.Counterclockwise, true, false);
                path.LineTo(new Point(size.Width - inset - arc, bottom), true, false);
                path.ArcTo(new Point(size.Width - inset, bottom - arc), new Size(arc, arc), 0, false, SweepDirection.Counterclockwise, true, false);
            }
            _contour.Freeze();
        }
        if (_pen is null || !ReferenceEquals(_pen.Brush, brush) || _pen.Thickness != thickness)
            _pen = new Pen(brush, thickness);
        drawingContext.DrawGeometry(null, _pen, _contour);
    }
}

/// <summary>The grip changes only its painted width; Track and the 16-DIP input lane remain native.</summary>
public sealed class ScrollThumbChrome : FrameworkElement
{
    private readonly SolidColorBrush _gripBrush = new();
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(ScrollThumbChrome), new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ExpandedProperty = DependencyProperty.Register(
        nameof(Expanded), typeof(bool), typeof(ScrollThumbChrome), new PropertyMetadata(false, OnStateChanged));
    public static readonly DependencyProperty PressedProperty = DependencyProperty.Register(
        nameof(Pressed), typeof(bool), typeof(ScrollThumbChrome), new PropertyMetadata(false, OnStateChanged));
    public static readonly DependencyProperty RestBrushProperty = DependencyProperty.Register(
        nameof(RestBrush), typeof(Brush), typeof(ScrollThumbChrome), new PropertyMetadata(Brushes.Transparent, OnStateChanged));
    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.Register(
        nameof(HoverBrush), typeof(Brush), typeof(ScrollThumbChrome), new PropertyMetadata(Brushes.Transparent, OnStateChanged));
    public static readonly DependencyProperty PressedBrushProperty = DependencyProperty.Register(
        nameof(PressedBrush), typeof(Brush), typeof(ScrollThumbChrome), new PropertyMetadata(Brushes.Transparent, OnStateChanged));
    public static readonly DependencyProperty GripWidthProperty = DependencyProperty.Register(
        nameof(GripWidth), typeof(double), typeof(ScrollThumbChrome), new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GripColorProperty = DependencyProperty.Register(
        nameof(GripColor), typeof(Color), typeof(ScrollThumbChrome), new FrameworkPropertyMetadata(Colors.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public bool Expanded { get => (bool)GetValue(ExpandedProperty); set => SetValue(ExpandedProperty, value); }
    public bool Pressed { get => (bool)GetValue(PressedProperty); set => SetValue(PressedProperty, value); }
    public Brush RestBrush { get => (Brush)GetValue(RestBrushProperty); set => SetValue(RestBrushProperty, value); }
    public Brush HoverBrush { get => (Brush)GetValue(HoverBrushProperty); set => SetValue(HoverBrushProperty, value); }
    public Brush PressedBrush { get => (Brush)GetValue(PressedBrushProperty); set => SetValue(PressedBrushProperty, value); }
    public double GripWidth { get => (double)GetValue(GripWidthProperty); set => SetValue(GripWidthProperty, value); }
    public Color GripColor { get => (Color)GetValue(GripColorProperty); set => SetValue(GripColorProperty, value); }

    public ScrollThumbChrome()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => SetState(animate: false);
        Unloaded += (_, _) => SetState(animate: false);
    }

    private static void OnStateChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var chrome = (ScrollThumbChrome)owner;
        // A newly resolved palette is already the current appearance, not a transition from
        // the transparent dependency-property default. Only pointer interaction animates.
        bool interaction = args.Property == ExpandedProperty || args.Property == PressedProperty;
        chrome.SetState(animate: interaction && chrome.IsLoaded && chrome.IsVisible);
    }

    private void SetState(bool animate)
    {
        double fromWidth = GripWidth;
        Color fromColor = GripColor;
        double width = Expanded || Pressed ? 6 : 3;
        Brush brush = Pressed ? PressedBrush : Expanded ? HoverBrush : RestBrush;
        Color color = brush is SolidColorBrush solid ? solid.Color : Colors.Transparent;
        BeginAnimation(GripWidthProperty, null);
        BeginAnimation(GripColorProperty, null);
        // These are owned animation outputs. Commit a local base value so FillBehavior.Stop
        // returns to this state instead of the transparent/three-DIP metadata defaults.
        SetValue(GripWidthProperty, width);
        SetValue(GripColorProperty, color);
        if (!animate || !FluidMotion.AnimationsEnabled || (fromWidth == width && fromColor == color)) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(GripWidthProperty, new DoubleAnimation(fromWidth, width, FluidMotion.NormalDuration)
        { EasingFunction = ease, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(GripColorProperty, new ColorAnimation(fromColor, color, FluidMotion.NormalDuration)
        { EasingFunction = ease, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        bool vertical = Orientation == Orientation.Vertical;
        double width = Math.Min(GripWidth, Math.Max(0, (vertical ? ActualWidth : ActualHeight) - 4));
        double length = vertical ? ActualHeight : ActualWidth;
        if (width <= 0 || length <= 0) return;
        Rect rect = vertical ? new Rect(ActualWidth - 4 - width, 0, width, length)
            : new Rect(0, ActualHeight - 4 - width, length, width);
        _gripBrush.Color = GripColor;
        drawingContext.DrawRoundedRectangle(_gripBrush, null, rect, width / 2, width / 2);
    }
}
