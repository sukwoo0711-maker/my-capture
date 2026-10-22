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

    private static FrameworkElement BuildTextStyleGlyph(TextStyleGlyphKind kind)
    {
        var glyph = new Grid { Width = 20, Height = 20, IsHitTestVisible = false };
        if (kind == TextStyleGlyphKind.Outline)
        {
            var letter = new FormattedText("A", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                18, Brushes.Black, 1);
            var outline = new Path
            {
                Data = letter.BuildGeometry(new Point((20 - letter.WidthIncludingTrailingWhitespace) / 2, (20 - letter.Height) / 2)),
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
