using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MyCapture.App.Editing;

internal sealed partial class AnnotationEditorControl
{
    private enum TextStyleGlyphKind { Bold, Background, Outline }
    private static readonly Lazy<Geometry> TextOutlineGlyph = new(CreateTextOutlineGlyph);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Geometry> FallbackIcons = new();
    private static readonly Brush StepPreviewOutlineBrush = CreateStepPreviewOutlineBrush();

    private static Brush CreateStepPreviewOutlineBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x17));
        brush.Freeze();
        return brush;
    }

    private static Geometry SharedFallbackIcon(string key, Func<Geometry> create) =>
        FallbackIcons.GetOrAdd(key, _ =>
        {
            Geometry geometry = create();
            geometry.Freeze();
            return geometry;
        });

    private static Geometry CreateTextOutlineGlyph()
    {
        var letter = new FormattedText("A", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            18, Brushes.Black, 1);
        Geometry geometry = letter.BuildGeometry(new Point((20 - letter.WidthIncludingTrailingWhitespace) / 2,
            (20 - letter.Height) / 2));
        geometry.Freeze();
        return geometry;
    }

    private static FrameworkElement BuildTextStyleGlyph(TextStyleGlyphKind kind)
    {
        var glyph = new Grid { Width = 20, Height = 20, IsHitTestVisible = false };
        if (kind == TextStyleGlyphKind.Outline)
        {
            var outline = new Path
            {
                Data = TextOutlineGlyph.Value,
                Fill = Brushes.Transparent, StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round,
            };
            outline.SetBinding(Shape.StrokeProperty, ToggleForeground());
            glyph.Children.Add(outline);
        }
        else
        {
            var letter = new TextBlock
            {
                Text = kind == TextStyleGlyphKind.Bold ? "B" : "A",
                FontFamily = new FontFamily("Segoe UI"), FontSize = kind == TextStyleGlyphKind.Bold ? 18 : 14,
                FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (kind == TextStyleGlyphKind.Background)
            {
                var highlight = new Border
                {
                    Width = 18, Height = 18, CornerRadius = new CornerRadius(2), Child = letter,
                };
                highlight.SetBinding(Border.BackgroundProperty, ToggleForeground());
                letter.SetResourceReference(TextBlock.ForegroundProperty, "Surface.Raised");
                glyph.Children.Add(highlight);
            }
            else
            {
                letter.SetBinding(TextBlock.ForegroundProperty, ToggleForeground());
                glyph.Children.Add(letter);
            }
        }
        return glyph;
    }

    private static Binding ToggleForeground() => new(nameof(Control.Foreground))
    {
        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ToggleButton), 1),
    };
}
