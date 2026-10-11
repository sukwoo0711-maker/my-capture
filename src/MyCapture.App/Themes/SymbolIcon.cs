using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MyCapture.App.Themes;

/// <summary>
/// Draws one Symbols.xaml geometry on its authored 20x20 grid and scales the whole grid, stroke
/// included, to <see cref="Size"/>.
/// </summary>
/// <remarks>
/// <para>
/// A <c>Path</c> with <c>Stretch="Uniform"</c> fits each glyph's own bounds to its box: a narrow
/// pin inflates, a wide keyboard shrinks, and the unscaled pen makes the same 1.6 stroke look
/// heavier on one and lighter on the other. Here the grid is the unit, so every glyph keeps the
/// proportions it was drawn with and every glyph at one size has the same stroke weight. This is
/// the image editor's fixed 20x20 canvas, shared by every screen.
/// </para>
/// <para>
/// The stroke follows the inherited <c>Foreground</c> unless <see cref="Stroke"/> is set, so
/// hover, selected, disabled and high-contrast states come from the surrounding control. Theme
/// brushes are only referenced, never frozen or recoloured here.
/// </para>
/// <para>
/// Usage: <c>&lt;themes:SymbolIcon Data="{StaticResource Icon.Save}" Size="16" /&gt;</c> in XAML,
/// or <see cref="Create(Geometry, double)"/> and <see cref="FromResource(string, double)"/> in code.
/// </para>
/// </remarks>
public sealed class SymbolIcon : FrameworkElement
{
    /// <summary>The coordinate grid every Symbols.xaml glyph is authored on.</summary>
    public const double GridSize = 20;

    /// <summary>The stroke every outline glyph is authored with, in grid units.</summary>
    public const double GridStrokeThickness = 1.6;

    /// <summary>Outer size for menu items, text-field adornments and icons inline with a line of text.</summary>
    public const double InlineSize = 16;

    /// <summary>Outer size for ordinary commands: tool bars, rails, transport and navigation.</summary>
    public const double CommandSize = 20;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(SymbolIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(SymbolIcon),
        new FrameworkPropertyMetadata(CommandSize,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double size && size >= 0 && double.IsFinite(size));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(SymbolIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(SymbolIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(SymbolIcon),
        new FrameworkPropertyMetadata(GridStrokeThickness, FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double thickness && thickness >= 0 && double.IsFinite(thickness));

    /// <summary>The text colour of the surrounding control; the stroke when <see cref="Stroke"/> is not set.</summary>
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(SymbolIcon),
        new FrameworkPropertyMetadata(SystemColors.ControlTextBrush,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    static SymbolIcon()
    {
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(SymbolIcon), new FrameworkPropertyMetadata(false));
    }

    /// <summary>A geometry authored on the 20x20 symbol grid.</summary>
    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>The outer square, in DIPs; the 20x20 grid is scaled to fill it.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>An explicit stroke brush; null follows <see cref="Foreground"/>.</summary>
    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>The interior brush of filled glyphs; null leaves outline glyphs open.</summary>
    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>The stroke in grid units: 1.6 for the outline family, 0 for a filled-only glyph.</summary>
    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>The scale from the 20x20 grid to device-independent pixels.</summary>
    public double GridScale => Size / GridSize;

    /// <summary>The drawn stroke width in DIPs: the grid stroke scaled with the grid.</summary>
    public double RenderedStrokeThickness => StrokeThickness * GridScale;

    /// <summary>An icon for <paramref name="geometry"/> at <paramref name="size"/>.</summary>
    public static SymbolIcon Create(Geometry? geometry, double size = CommandSize) => new() { Data = geometry, Size = size };

    /// <summary>
    /// An icon whose geometry follows the <paramref name="resourceKey"/> resource as the element
    /// resolves it, so a window that loads the theme after construction still draws the glyph.
    /// </summary>
    public static SymbolIcon FromResource(string resourceKey, double size = CommandSize)
    {
        var icon = new SymbolIcon { Size = size };
        icon.SetResourceReference(DataProperty, resourceKey);
        return icon;
    }

    /// <summary>A solid glyph (no outline) whose interior takes the inherited foreground.</summary>
    public static SymbolIcon CreateFilled(Geometry? geometry, double size = CommandSize)
    {
        var icon = new SymbolIcon { Data = geometry, Size = size, StrokeThickness = 0 };
        icon.SetBinding(FillProperty, new System.Windows.Data.Binding(nameof(Foreground))
        {
            RelativeSource = System.Windows.Data.RelativeSource.Self,
        });
        return icon;
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize) => new(Size, Size);

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize) => finalSize;

    protected override void OnRender(DrawingContext drawingContext)
    {
        Geometry? data = Data;
        double size = Size;
        if (data is null || size <= 0) return;

        Brush? fill = Fill;
        Brush? strokeBrush = Stroke ?? Foreground;
        double thickness = StrokeThickness;
        Pen? pen = null;
        if (strokeBrush is not null && thickness > 0)
        {
            pen = new Pen(strokeBrush, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                DashCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            // Freezing a pen freezes its brush too; a theme-following brush must stay live.
            if (strokeBrush.IsFrozen) pen.Freeze();
        }

        if (pen is null && fill is null) return;

        // Centred when a caller sizes the element larger than Size (an explicit Width or Height).
        double scale = size / GridSize;
        var transform = new MatrixTransform(scale, 0, 0, scale,
            Math.Max(0, (RenderSize.Width - size) / 2), Math.Max(0, (RenderSize.Height - size) / 2));
        transform.Freeze();
        drawingContext.PushTransform(transform);
        drawingContext.DrawGeometry(fill, pen, data);
        drawingContext.Pop();
    }
}
