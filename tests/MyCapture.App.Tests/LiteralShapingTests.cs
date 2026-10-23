using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A caption in the bundled Pretendard keeps technical text as typed. By default the font turns
/// "0x02D9" into "0×02D9" and "->" into an arrow; the committed caption and the live editing box
/// both draw the typed characters, at the same width.
/// </summary>
public sealed class LiteralShapingTests
{
    private const char Joiner = LiteralShaping.WordJoiner;

    // Text whose glyphs the font would replace: a multiplication sign, arrows, a not-equal shape.
    public static TheoryData<string> Replaced => ["0x02D9", "1344x680", "0X1F", "->", "<-", "=>", "!=", "a->b", "x != y"];

    // Colons, hyphens, brackets and arrow heads beside capitals or digits are only raised, at the
    // same width, and keep the font's form; whole captions still lay out at the live box width.
    public static TheoryData<string> Raised =>
        ["12:30", "A-B", "H:M", "#define UART_BRR (UART_BASE + 0x08) /* 115200 bps -> 0x02D9 */",
         "#define uart_brr (uart_base + 0x08) /* 115200 bps -> 0x02d9 */"];

    [Fact]
    public void PrepareBreaksOnlyTheRewrittenPairs()
    {
        Assert.Equal($"0{Joiner}x02D9", LiteralShaping.Prepare("0x02D9"));
        Assert.Equal($"a-{Joiner}>b", LiteralShaping.Prepare("a->b"));
        Assert.Equal($"x !{Joiner}= y", LiteralShaping.Prepare("x != y"));
        // Text the features leave alone, or only raise, is returned unchanged, without a copy.
        foreach (string plain in new[] { "box 3 x 레지스터 맵", "12:30", "2026-10-24 09:12 보드 REV_B" })
            Assert.Same(plain, LiteralShaping.Prepare(plain));
        Assert.Equal(string.Empty, LiteralShaping.Prepare(string.Empty));
        Assert.True(LiteralShaping.Applies("Pretendard"));
        Assert.False(LiteralShaping.Applies("Malgun Gothic"));
        Assert.False(LiteralShaping.Applies("JetBrains Mono"));
    }

    [Theory]
    [MemberData(nameof(Raised))]
    public void RaisedPunctuationKeepsTheLiveBoxWidth(string text) => StaTestHost.Run(() =>
    {
        (FormattedText committed, TextBlock literal) = Layouts(text);
        Assert.Equal(literal.ActualWidth, committed.WidthIncludingTrailingWhitespace, 2);
    });

    private static (FormattedText Committed, TextBlock Literal) Layouts(string text)
    {
        var caption = new TextAnnotation
        {
            Rect = new RectD(0, 0, 40, 40), Text = text, FontSize = 40, FontFamily = "Pretendard",
            AutoSize = true, Foreground = ColorRgba.Black,
        };
        FormattedText committed = TextAnnotationLayout.Format(caption, 1.0);
        var literal = new TextBlock
        {
            Text = text, FontFamily = FontCatalog.Resolve("Pretendard"), FontSize = 40,
            // The committed layout formats in the UI culture; compare in the same one.
            Language = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag),
        };
        TextOptions.SetTextFormattingMode(literal, TextFormattingMode.Ideal);
        LiteralShaping.Apply(literal);
        literal.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        literal.Arrange(new Rect(literal.DesiredSize));
        return (committed, literal);
    }

    [Theory]
    [MemberData(nameof(Replaced))]
    public void CommittedCaptionDrawsTheTypedGlyphsAtTheLiveBoxWidth(string text) => StaTestHost.Run(() =>
    {
        (FormattedText committed, TextBlock literal) = Layouts(text);
        Assert.Equal(literal.ActualWidth, committed.WidthIncludingTrailingWhitespace, 2);

        int width = (int)Math.Ceiling(Math.Max(literal.ActualWidth, committed.WidthIncludingTrailingWhitespace)) + 2;
        int height = (int)Math.Ceiling(Math.Max(literal.ActualHeight, committed.Height)) + 2;
        byte[] typed = Pixels(literal, width, height);
        // TextBlock and DrawText antialias a few edge pixels differently; a different glyph
        // (× for x, an arrow for "->") changes whole strokes.
        Assert.Equal(0, StrongDifferences(typed, Pixels(Drawn(committed), width, height)));

        // The control: without the joiners the font rewrites these, and the check sees it.
        if (text is "0x02D9" or "->" or "=>")
        {
            var rewritten = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(FontCatalog.Resolve("Pretendard"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 40, Brushes.Black, 1.0);
            Assert.True(StrongDifferences(typed, Pixels(Drawn(rewritten), width, height)) > 20, $"{text} is rewritten without the joiners");
        }
    });

    private static DrawingVisual Drawn(FormattedText text)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen()) dc.DrawText(text, new Point(0, 0));
        return visual;
    }

    private static int StrongDifferences(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        int count = 0;
        for (int index = 0; index < expected.Length; index += 4)
            if (Math.Abs(expected[index + 3] - actual[index + 3]) > 96) count++;
        return count;
    }

    private static byte[] Pixels(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }
}
