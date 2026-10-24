using System.Globalization;
using System.Text;

namespace MyCapture.Ocr;

public enum OcrExtractionMode { Table, Code, Text, Translation }

public sealed record OcrTableCell(string Text, OcrRect Bounds, OcrRect Region);
public sealed record OcrTableRow(IReadOnlyList<OcrTableCell> Cells);
public sealed record OcrTableColumn(bool IsNumeric);

public sealed record OcrTable(
    IReadOnlyList<OcrTableRow> Rows,
    IReadOnlyList<OcrTableColumn> Columns,
    bool HasHeader)
{
    public static OcrTable Empty { get; } = new([], [], false);
    public bool HasStructure => Columns.Count > 1 && Rows.Count > 1;
    public OcrRect Bounds => Rows.SelectMany(row => row.Cells)
        .Where(cell => cell.Text.Length > 0).Select(cell => cell.Bounds)
        .Aggregate(default(OcrRect), OcrRect.Union);
}

public sealed record OcrCode(string Text, string? Language, bool? IsMonospaced,
    double CharacterWidth, IReadOnlyList<OcrRect> Regions);
public sealed record OcrParagraph(IReadOnlyList<string> Lines, OcrRect Bounds);

public sealed record OcrExtraction(OcrTable Table, OcrCode Code,
    IReadOnlyList<OcrParagraph> Paragraphs, bool HasGeometry)
{
    public string Text(bool joinLines) => string.Join("\n\n", Paragraphs.Select(paragraph =>
        string.Join(joinLines ? " " : "\n", paragraph.Lines)));
}

/// <summary>Reconstructs in reading axes; returned geometry always uses original image pixels.</summary>
public static class OcrExtractionAnalyzer
{
    public static OcrExtraction Analyze(OcrResult result, OcrRect? selection = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.HasText)
            return new(OcrTable.Empty, new("", null, null, 1, []), [], false);

        var allWords = result.Lines.SelectMany(line => line.Words.Count > 0
                ? line.Words
                : string.IsNullOrWhiteSpace(line.Text) ? [] : new[] { new OcrWord(line.Text, line.Bounds) })
            .Where(word => !string.IsNullOrWhiteSpace(word.Text) && OcrGeometry.IsValid(word.Bounds))
            .Select(word => word with { Text = word.Text.Trim() }).ToArray();
        if (allWords.Length == 0)
        {
            string fallback = selection is null ? result.Text.Replace("\r\n", "\n").Replace('\r', '\n') : "";
            var paragraphs = fallback.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(block => new OcrParagraph(block.Split('\n'), default)).ToArray();
            return new(OcrTable.Empty, new(fallback, OcrCodeReconstructor.EstimateLanguage(fallback),
                null, 1, []), paragraphs, false);
        }

        OcrReadingOrientation orientation = result.ReadingOrientation;
        OcrWord[] words = selection is null ? allWords : allWords
            .Where(word => OcrGeometry.IsValid(selection.Value)
                && OcrGeometry.Contains(selection.Value, word.Bounds.X + word.Bounds.Width / 2,
                    word.Bounds.Y + word.Bounds.Height / 2)).ToArray();
        var rows = OcrLayoutRow.Build(ReadingWords(words, orientation));
        var table = OcrTableReconstructor.Build(rows);
        var codeRows = SelectCodeRows(rows, table);
        IReadOnlyList<OcrLayoutRow> referenceRows = codeRows;
        if (selection is not null && codeRows.Count > 0)
        {
            var allRows = OcrLayoutRow.Build(ReadingWords(allWords, orientation));
            var fullCodeRows = SelectCodeRows(allRows, OcrTableReconstructor.Build(allRows));
            // A selection inside the original code block retains its indentation and language
            // context. A selection in a different region does not inherit that code's language.
            if (codeRows.All(row => fullCodeRows.Any(full => OcrGeometry.Contains(full.Bounds,
                    row.Bounds.X + row.Bounds.Width / 2, row.Bounds.Y + row.Bounds.Height / 2))))
                referenceRows = fullCodeRows;
        }
        double origin = referenceRows.Count == 0 ? 0 : referenceRows.Min(row => row.Bounds.X);
        return ToOriginal(new(table, OcrCodeReconstructor.Build(codeRows, origin, referenceRows),
            OcrTextReconstructor.Build(rows), true), orientation);
    }

    private static IEnumerable<OcrWord> ReadingWords(IEnumerable<OcrWord> words,
        OcrReadingOrientation orientation) => orientation.IsRotated
            ? words.Select(word => word with { Bounds = orientation.ToReading(word.Bounds) }) : words;

    private static OcrExtraction ToOriginal(OcrExtraction extraction, OcrReadingOrientation orientation)
    {
        if (!orientation.IsRotated) return extraction;
        return extraction with
        {
            Table = extraction.Table with
            {
                Rows = extraction.Table.Rows.Select(row => new OcrTableRow(row.Cells.Select(cell => cell with
                {
                    Bounds = orientation.ToOriginal(cell.Bounds),
                    Region = orientation.ToOriginal(cell.Region),
                }).ToArray())).ToArray(),
            },
            Code = extraction.Code with
            {
                Regions = extraction.Code.Regions.Select(orientation.ToOriginal).ToArray(),
            },
            Paragraphs = extraction.Paragraphs.Select(paragraph => paragraph with
                { Bounds = orientation.ToOriginal(paragraph.Bounds) }).ToArray(),
        };
    }

    private static List<OcrLayoutRow> SelectCodeRows(List<OcrLayoutRow> rows, OcrTable table)
    {
        // On a mixed capture the table and code have separate extraction surfaces.
        if (!table.HasStructure) return rows;
        var outsideTable = rows.Where(row => !OcrGeometry.Contains(table.Bounds,
            row.Bounds.X + row.Bounds.Width / 2, row.Bounds.Y + row.Bounds.Height / 2)).ToList();
        return OcrCodeReconstructor.EstimateLanguage(string.Join("\n",
            outsideTable.Select(row => row.Text))) is not null ? outsideTable : rows;
    }
}

public static class OcrGeometry
{
    public static bool IsValid(OcrRect rect) => double.IsFinite(rect.X) && double.IsFinite(rect.Y)
        && double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom)
        && rect.Width > 0 && rect.Height > 0;
    public static bool Contains(OcrRect rect, double x, double y) =>
        x >= rect.X && x <= rect.Right && y >= rect.Y && y <= rect.Bottom;

    public static OcrRect Fit(double viewportWidth, double viewportHeight, double imageWidth, double imageHeight)
    {
        if (!double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight)
            || !double.IsFinite(imageWidth) || !double.IsFinite(imageHeight)
            || viewportWidth <= 0 || viewportHeight <= 0 || imageWidth <= 0 || imageHeight <= 0)
            return default;
        double scale = Math.Min(viewportWidth / imageWidth, viewportHeight / imageHeight);
        return new((viewportWidth - imageWidth * scale) / 2,
            (viewportHeight - imageHeight * scale) / 2, imageWidth * scale, imageHeight * scale);
    }

    public static OcrRect Selection(double startX, double startY, double endX, double endY,
        OcrRect fittedImage, double imageWidth, double imageHeight)
    {
        if (!IsValid(fittedImage) || !double.IsFinite(startX) || !double.IsFinite(startY)
            || !double.IsFinite(endX) || !double.IsFinite(endY)
            || !double.IsFinite(imageWidth) || !double.IsFinite(imageHeight)
            || imageWidth <= 0 || imageHeight <= 0)
            return default;
        double x1 = Math.Clamp((startX - fittedImage.X) / fittedImage.Width * imageWidth, 0, imageWidth);
        double x2 = Math.Clamp((endX - fittedImage.X) / fittedImage.Width * imageWidth, 0, imageWidth);
        double y1 = Math.Clamp((startY - fittedImage.Y) / fittedImage.Height * imageHeight, 0, imageHeight);
        double y2 = Math.Clamp((endY - fittedImage.Y) / fittedImage.Height * imageHeight, 0, imageHeight);
        return new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
    }

    internal static double Median(IEnumerable<double> values, double fallback = 1)
    {
        double[] sorted = values.Where(value => double.IsFinite(value) && value > 0).Order().ToArray();
        return sorted.Length == 0 ? fallback : sorted.Length % 2 == 0
            ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
    }

    internal static int CharacterColumns(string text)
    {
        int columns = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
                continue;
            int value = rune.Value;
            columns += value >= 0x1100 && (value <= 0x115f || value is >= 0x2e80 and <= 0xa4cf
                || value is >= 0xac00 and <= 0xd7af || value is >= 0xf900 and <= 0xfaff
                || value is >= 0xff01 and <= 0xff60 || value >= 0x1f300) ? 2 : 1;
        }
        return Math.Max(1, columns);
    }

    internal static double CharacterWidth(IEnumerable<OcrWord> words) =>
        Median(words.Where(word => CharacterColumns(word.Text) >= 2)
            .Select(word => word.Bounds.Width / CharacterColumns(word.Text)),
            Median(words.Select(word => word.Bounds.Height)) * .5);
}

internal sealed class OcrLayoutRow
{
    internal List<OcrWord> Words { get; } = [];
    internal OcrRect Bounds { get; private set; }
    internal string Text => string.Join(" ", Words.OrderBy(word => word.Bounds.X).Select(word => word.Text));
    internal void Add(OcrWord word)
    {
        Words.Add(word);
        Bounds = OcrRect.Union(Bounds, word.Bounds);
    }

    internal static List<OcrLayoutRow> Build(IEnumerable<OcrWord> words)
    {
        var rows = new List<OcrLayoutRow>();
        foreach (OcrWord word in words.OrderBy(word => word.Bounds.Y + word.Bounds.Height / 2)
                     .ThenBy(word => word.Bounds.X))
        {
            OcrLayoutRow? row = rows.LastOrDefault();
            double overlap = row is null ? 0 : Math.Min(row.Bounds.Bottom, word.Bounds.Bottom)
                - Math.Max(row.Bounds.Y, word.Bounds.Y);
            if (row is null || overlap < Math.Min(row.Bounds.Height, word.Bounds.Height) * .45)
            {
                row = new OcrLayoutRow();
                rows.Add(row);
            }
            row.Add(word);
        }
        foreach (OcrLayoutRow row in rows) row.Words.Sort((left, right) => left.Bounds.X.CompareTo(right.Bounds.X));
        return rows;
    }

    internal List<OcrLayoutRun> Runs(double gap)
    {
        var runs = new List<OcrLayoutRun>();
        foreach (OcrWord word in Words)
        {
            if (runs.Count == 0 || word.Bounds.X - runs[^1].Bounds.Right > gap)
                runs.Add(new OcrLayoutRun());
            runs[^1].Add(word);
        }
        return runs;
    }
}

internal sealed class OcrLayoutRun
{
    internal List<OcrWord> Words { get; } = [];
    internal OcrRect Bounds { get; private set; }
    internal string Text => string.Join(" ", Words.Select(word => word.Text));
    internal void Add(OcrWord word)
    {
        Words.Add(word);
        Bounds = OcrRect.Union(Bounds, word.Bounds);
    }
}
