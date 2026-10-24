using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Ocr;

namespace MyCapture.App.Tests;

/// <summary>
/// A datasheet register table like the one in the translation probe, drawn at 1x with the line
/// and word boxes an OCR engine returns for it. Build it on an STA thread.
/// </summary>
internal static class TranslationFixture
{
    internal const string Title = "Table 31. CTRL1 register bit fields";

    internal static readonly string[][] Rows =
    [
        ["Bit", "Name", "Access", "Reset", "Description"],
        ["15:10", "Reserved", "-", "0", "Reserved, must be kept at reset value."],
        ["7", "ARPE", "rw", "0", "Auto-reload preload enable."],
        ["3", "OPM", "rw", "0", "One-pulse mode. When set, the counter stops at the next update event."],
        ["0", "CEN", "rw", "0", "Counter enable. 0: Counter disabled. 1: Counter enabled."],
    ];

    private static readonly double[] ColumnX = [24, 104, 214, 304, 384];
    private const int Width = 900;
    private const int Height = 260;
    private const double FontSize = 14;

    internal static OcrResult RegisterTable() => Build(null);

    internal static (BitmapSource Image, OcrResult Result) RegisterTableWithImage()
    {
        var visual = new DrawingVisual();
        OcrResult result;
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xDD, 0xE3, 0xEE)), null, new Rect(16, 56, Width - 32, 32));
            var line = new Pen(new SolidColorBrush(Color.FromRgb(0xB8, 0xBE, 0xC8)), 1);
            for (int row = 0; row <= Rows.Length; row++) drawing.DrawLine(line, new Point(16, 56 + row * 34), new Point(Width - 16, 56 + row * 34));
            foreach (double x in ColumnX.Skip(1)) drawing.DrawLine(line, new Point(x - 8, 56), new Point(x - 8, 56 + Rows.Length * 34));
            result = Build(drawing);
        }
        var image = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        return (image, result);
    }

    private static OcrResult Build(DrawingContext? drawing)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var lines = new List<OcrLine>();
        lines.Add(Line(Title, 24, 18, bold, drawing));
        for (int row = 0; row < Rows.Length; row++)
        {
            var words = new List<OcrWord>();
            for (int column = 0; column < Rows[row].Length; column++)
                words.AddRange(Words(Rows[row][column], ColumnX[column], 64 + row * 34, row == 0 ? bold : typeface, drawing));
            lines.Add(new OcrLine(string.Join(" ", words.Select(word => word.Text)),
                words.Select(word => word.Bounds).Aggregate(OcrRect.Union), words));
        }
        return OcrResult.Success(string.Join("\n", lines.Select(line => line.Text)), "en-US", lines,
            TimeSpan.FromMilliseconds(80));
    }

    private static OcrLine Line(string text, double x, double y, Typeface typeface, DrawingContext? drawing)
    {
        List<OcrWord> words = Words(text, x, y, typeface, drawing);
        return new OcrLine(text, words.Select(word => word.Bounds).Aggregate(OcrRect.Union), words);
    }

    private static List<OcrWord> Words(string text, double x, double y, Typeface typeface, DrawingContext? drawing)
    {
        var words = new List<OcrWord>();
        double space = Measure(" ", typeface).WidthIncludingTrailingWhitespace;
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            FormattedText formatted = Measure(word, typeface);
            drawing?.DrawText(formatted, new Point(x, y));
            words.Add(new OcrWord(word, new OcrRect(x, y + 2, formatted.Width, formatted.Height - 4)));
            x += formatted.Width + space;
        }
        return words;
    }

    private static FormattedText Measure(string text, Typeface typeface) => new(text, CultureInfo.GetCultureInfo("en-US"),
        FlowDirection.LeftToRight, typeface, FontSize, new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x2B)), 1);
}
