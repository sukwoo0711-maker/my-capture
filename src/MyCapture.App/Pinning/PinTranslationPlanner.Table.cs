using System.Text;

namespace MyCapture.App.Pinning;

// Table: tab-separated rows as copied from Excel. Cells wrapped in double quotes may contain tabs and line breaks.
internal static partial class PinTranslationPlanner
{
    /// <summary>One cell. <see cref="Start"/>..<see cref="End"/> is the trimmed text (inside the quotes for a quoted cell).</summary>
    private readonly record struct TableCell(int Start, int End, string Text, bool Quoted);

    private static PinTranslationPlan CreateTable(string source, PinTranslationDirection direction)
    {
        var builder = new PlanBuilder(source);
        foreach (List<TableCell> row in ScanTable(source))
        {
            foreach (TableCell cell in row)
            {
                if (cell.End <= cell.Start || !IsTranslatableCell(cell.Text, direction)) continue;
                string raw = source[cell.Start..cell.End];
                builder.Piece(cell.Start, cell.End, PlanKind.TableCell, string.Equals(raw, cell.Text, StringComparison.Ordinal) ? null : cell.Text);
            }
        }
        return new PinTranslationPlan(PinTextFormat.Table, builder.Finish());
    }

    private static bool IsTranslatableCell(string text, PinTranslationDirection direction) =>
        !LooksLikeData(text) && IsTranslatableText(text, direction);

    /// <summary>Rows of cells in source order. A row ends at a line break outside quotes.</summary>
    private static List<List<TableCell>> ScanTable(string text)
    {
        var rows = new List<List<TableCell>>();
        int index = 0;
        while (index < text.Length)
        {
            var row = new List<TableCell>();
            while (true)
            {
                row.Add(ReadCell(text, ref index));
                if (index < text.Length && text[index] == '\t')
                {
                    index++;
                    if (index >= text.Length) row.Add(new TableCell(index, index, string.Empty, false));
                    continue;
                }
                break;
            }
            if (index < text.Length && text[index] == '\r') index++;
            if (index < text.Length && text[index] == '\n') index++;
            rows.Add(row);
        }
        return rows;
    }

    private static TableCell ReadCell(string text, ref int index)
    {
        int cellStart = index;
        if (index < text.Length && text[index] == '"')
        {
            int cursor = index + 1;
            while (cursor < text.Length)
            {
                if (text[cursor] != '"') { cursor++; continue; }
                if (cursor + 1 < text.Length && text[cursor + 1] == '"') { cursor += 2; continue; }
                break;
            }
            bool closed = cursor < text.Length && (cursor + 1 >= text.Length || text[cursor + 1] is '\t' or '\r' or '\n');
            if (closed)
            {
                (int Start, int End)? inner = TrimSpan(text, index + 1, cursor);
                index = cursor + 1;
                if (inner is null) return new TableCell(cursor, cursor, string.Empty, true);
                string decoded = text[inner.Value.Start..inner.Value.End].Replace("\"\"", "\"", StringComparison.Ordinal);
                return new TableCell(inner.Value.Start, inner.Value.End, decoded, true);
            }
        }
        while (index < text.Length && text[index] is not ('\t' or '\r' or '\n')) index++;
        (int Start, int End)? trimmed = TrimSpan(text, cellStart, index);
        return trimmed is null
            ? new TableCell(cellStart, cellStart, string.Empty, false)
            : new TableCell(trimmed.Value.Start, trimmed.Value.End, text[trimmed.Value.Start..trimmed.Value.End], false);
    }

    /// <summary>At least two rows, and every non-empty row has the same number (at least one) of tab separators.</summary>
    private static bool IsTabular(string source)
    {
        if (source.IndexOf('\t') < 0) return false;
        List<List<TableCell>> rows = ScanTable(source);
        int expected = -1, filled = 0, separated = 0;
        foreach (List<TableCell> row in rows)
        {
            if (row.Count == 1 && row[0].End <= row[0].Start && !row[0].Quoted) continue;
            filled++;
            if (expected < 0) expected = row.Count;
            else if (row.Count != expected) return false;
            // A tab after content separates cells; tabs only at the start of a line are indentation.
            if (row.Take(row.Count - 1).Any(static cell => cell.End > cell.Start)) separated++;
        }
        return filled >= 2 && expected >= 2 && separated * 2 >= filled;
    }
}
