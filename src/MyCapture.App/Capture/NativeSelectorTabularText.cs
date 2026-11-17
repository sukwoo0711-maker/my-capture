using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace MyCapture.App.Capture;

/// <summary>
/// One line of selector chip text laid out by WPF's own text formatter, with OpenType tabular
/// figures when asked: every digit takes the same advance, so a size such as "1920 × 1080" keeps its
/// width while its digits change. <see cref="FormattedText"/> has no typography options; this is the
/// path a <c>TextBlock</c> with <c>Typography.NumeralAlignment="Tabular"</c> takes.
/// </summary>
internal sealed class NativeSelectorTabularText : IDisposable
{
    private static readonly TextFormatter Formatter = TextFormatter.Create(TextFormattingMode.Ideal);
    private const double ParagraphWidth = 100_000;
    private TextLine? _line;

    internal NativeSelectorTabularText(string text, Typeface typeface, double size, Brush brush, double pixelsPerDip,
        bool tabular = true)
    {
        Text = text;
        var properties = new RunProperties(typeface, size, brush, pixelsPerDip, tabular);
        // The formatter takes only a finite paragraph width; the line never wraps, so any wide one does.
        _line = Formatter.FormatLine(new Source(text, properties), 0, ParagraphWidth,
            new ParagraphProperties(properties), null);
        Width = _line.WidthIncludingTrailingWhitespace;
        Height = _line.Height;
        Baseline = _line.Baseline;
    }

    internal string Text { get; }

    internal double Width { get; }

    internal double Height { get; }

    internal double Baseline { get; }

    /// <summary>Draws the line with its top-left corner at <paramref name="origin"/>.</summary>
    internal void Draw(DrawingContext dc, Point origin)
    {
        ObjectDisposedException.ThrowIf(_line is null, this);
        _line.Draw(dc, origin, InvertAxes.None);
    }

    public void Dispose()
    {
        _line?.Dispose();
        _line = null;
    }

    private sealed class Source(string text, TextRunProperties properties) : TextSource
    {
        public override TextRun GetTextRun(int textSourceCharacterIndex) =>
            textSourceCharacterIndex < text.Length
                ? new TextCharacters(text, textSourceCharacterIndex, text.Length - textSourceCharacterIndex, properties)
                : new TextEndOfParagraph(1);

        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int textSourceCharacterIndexLimit) =>
            new(0, new CultureSpecificCharacterBufferRange(CultureInfo.CurrentUICulture,
                new CharacterBufferRange(string.Empty, 0, 0)));

        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int textSourceCharacterIndex) =>
            textSourceCharacterIndex;
    }

    private sealed class RunProperties : TextRunProperties
    {
        private readonly Typeface _typeface;
        private readonly double _size;
        private readonly Brush _brush;
        private readonly TextRunTypographyProperties _typography;

        internal RunProperties(Typeface typeface, double size, Brush brush, double pixelsPerDip, bool tabular)
        {
            _typeface = typeface;
            _size = size;
            _brush = brush;
            _typography = new Typography(tabular ? FontNumeralAlignment.Tabular : FontNumeralAlignment.Normal);
            PixelsPerDip = pixelsPerDip;
        }

        public override Typeface Typeface => _typeface;
        public override double FontRenderingEmSize => _size;
        public override double FontHintingEmSize => _size;
        public override TextDecorationCollection? TextDecorations => null;
        public override Brush ForegroundBrush => _brush;
        public override Brush? BackgroundBrush => null;
        public override CultureInfo CultureInfo => CultureInfo.CurrentUICulture;
        public override TextEffectCollection? TextEffects => null;
        public override TextRunTypographyProperties TypographyProperties => _typography;
    }

    private sealed class ParagraphProperties(TextRunProperties properties) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => 0;
        public override bool FirstLineInParagraph => true;
        public override TextRunProperties DefaultTextRunProperties => properties;
        public override TextWrapping TextWrapping => TextWrapping.NoWrap;
        public override TextMarkerProperties? TextMarkerProperties => null;
        public override double Indent => 0;
    }

    /// <summary>WPF's default typography (the values a TextBlock starts with) apart from the figures.</summary>
    private sealed class Typography(FontNumeralAlignment numerals) : TextRunTypographyProperties
    {
        public override bool StandardLigatures => true;
        public override bool ContextualLigatures => true;
        public override bool DiscretionaryLigatures => false;
        public override bool HistoricalLigatures => false;
        public override bool ContextualAlternates => true;
        public override bool HistoricalForms => false;
        public override bool Kerning => true;
        public override bool CapitalSpacing => false;
        public override bool CaseSensitiveForms => false;
        public override bool StylisticSet1 => false;
        public override bool StylisticSet2 => false;
        public override bool StylisticSet3 => false;
        public override bool StylisticSet4 => false;
        public override bool StylisticSet5 => false;
        public override bool StylisticSet6 => false;
        public override bool StylisticSet7 => false;
        public override bool StylisticSet8 => false;
        public override bool StylisticSet9 => false;
        public override bool StylisticSet10 => false;
        public override bool StylisticSet11 => false;
        public override bool StylisticSet12 => false;
        public override bool StylisticSet13 => false;
        public override bool StylisticSet14 => false;
        public override bool StylisticSet15 => false;
        public override bool StylisticSet16 => false;
        public override bool StylisticSet17 => false;
        public override bool StylisticSet18 => false;
        public override bool StylisticSet19 => false;
        public override bool StylisticSet20 => false;
        public override bool SlashedZero => false;
        public override bool MathematicalGreek => false;
        public override bool EastAsianExpertForms => false;
        public override FontVariants Variants => FontVariants.Normal;
        public override FontCapitals Capitals => FontCapitals.Normal;
        public override FontFraction Fraction => FontFraction.Normal;
        public override FontNumeralStyle NumeralStyle => FontNumeralStyle.Normal;
        public override FontNumeralAlignment NumeralAlignment => numerals;
        public override FontEastAsianWidths EastAsianWidths => FontEastAsianWidths.Normal;
        public override FontEastAsianLanguage EastAsianLanguage => FontEastAsianLanguage.Normal;
        public override int StandardSwashes => 0;
        public override int ContextualSwashes => 0;
        public override int StylisticAlternates => 0;
        public override int AnnotationAlternates => 0;
    }
}
