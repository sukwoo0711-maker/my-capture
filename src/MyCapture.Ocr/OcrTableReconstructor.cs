using System.Globalization;

namespace MyCapture.Ocr;

internal static class OcrTableReconstructor
{
    internal static OcrTable Build(IReadOnlyList<OcrLayoutRow> source)
    {
        if (source.Count == 0) return OcrTable.Empty;
        double height = OcrGeometry.Median(source.Select(row => row.Bounds.Height));
        double characterWidth = OcrGeometry.CharacterWidth(source.SelectMany(row => row.Words));
        double gap = Math.Max(height * .7, characterWidth * 2.5);
        List<List<OcrLayoutRow>> groups = [[]];
        foreach (OcrLayoutRow row in source)
        {
            if (groups[^1].Count > 0 && row.Bounds.Y - groups[^1][^1].Bounds.Bottom > height * 1.8)
                groups.Add([]);
            groups[^1].Add(row);
        }
        var group = groups.OrderByDescending(rows => rows.Count(row => row.Runs(gap).Count >= 2))
            .ThenByDescending(rows => rows.Count).First();
        int first = group.FindIndex(row => row.Runs(gap).Count >= 2);
        int last = group.FindLastIndex(row => row.Runs(gap).Count >= 2);
        // Infer columns from the dense core before considering sparse boundary rows.
        var rows = first >= 0 && last > first ? group.Skip(first).Take(last - first + 1).ToList()
            : source.ToList();
        var runs = rows.Select(row => row.Runs(gap)).ToList();
        int count = runs.Where(row => row.Count >= 2).GroupBy(row => row.Count)
            .OrderByDescending(grouping => grouping.Count()).ThenByDescending(grouping => grouping.Key)
            .Select(grouping => grouping.Key).FirstOrDefault(1);
        var reference = runs.FirstOrDefault(row => row.Count == count) ?? runs[0];
        count = reference.Count;
        if (first >= 0 && last > first)
        {
            double pitch = OcrGeometry.Median(rows.Skip(1).Select((row, index) =>
                CenterY(row.Bounds) - CenterY(rows[index].Bounds)), height * 1.5);
            double tolerance = Math.Max(height * .5, characterWidth);
            while (first > 0 && IsSparseBoundaryRow(group[first - 1], group[first],
                       reference, gap, pitch, tolerance)) first--;
            while (last + 1 < group.Count && IsSparseBoundaryRow(group[last + 1], group[last],
                       reference, gap, pitch, tolerance)) last++;
            rows = group.Skip(first).Take(last - first + 1).ToList();
            runs = rows.Select(row => row.Runs(gap)).ToList();
        }
        var cells = new List<OcrTableCell[]>();
        foreach (var row in runs)
        {
            var buckets = Enumerable.Range(0, count).Select(_ => new List<OcrWord>()).ToArray();
            for (int index = 0; index < row.Count; index++)
            {
                OcrLayoutRun run = row[index];
                int column = row.Count == count ? index : Enumerable.Range(0, count)
                    .MinBy(candidate => Distance(run.Bounds, reference[candidate].Bounds));
                buckets[column].AddRange(run.Words);
            }
            cells.Add(buckets.Select(bucket => new OcrTableCell(
                string.Join(" ", bucket.OrderBy(word => word.Bounds.X).Select(word => word.Text)),
                bucket.Select(word => word.Bounds).Aggregate(default(OcrRect), OcrRect.Union), default)).ToArray());
        }

        double[] edges = new double[count + 1];
        edges[0] = rows.Min(row => row.Bounds.X);
        edges[count] = rows.Max(row => row.Bounds.Right);
        for (int column = 1; column < count; column++)
        {
            double right = cells.Where(row => row[column - 1].Text.Length > 0)
                .Select(row => row[column - 1].Bounds.Right).DefaultIfEmpty(reference[column - 1].Bounds.Right).Max();
            double left = cells.Where(row => row[column].Text.Length > 0)
                .Select(row => row[column].Bounds.X).DefaultIfEmpty(reference[column].Bounds.X).Min();
            edges[column] = Math.Clamp((right + left) / 2, edges[column - 1], edges[count]);
        }
        for (int row = 0; row < cells.Count; row++)
        {
            double top = row == 0 ? rows[row].Bounds.Y : (rows[row - 1].Bounds.Bottom + rows[row].Bounds.Y) / 2;
            double bottom = row == cells.Count - 1 ? rows[row].Bounds.Bottom
                : (rows[row].Bounds.Bottom + rows[row + 1].Bounds.Y) / 2;
            for (int column = 0; column < count; column++)
                cells[row][column] = cells[row][column] with
                { Region = new OcrRect(edges[column], top, edges[column + 1] - edges[column], bottom - top) };
        }

        var columns = Enumerable.Range(0, count).Select(column =>
        {
            var body = cells.Skip(cells.Count > 1 ? 1 : 0).Select(row => row[column].Text)
                .Where(text => text.Length > 0).ToArray();
            return new OcrTableColumn(body.Length > 0 && body.Count(IsNumeric) >= body.Length * .75);
        }).ToArray();
        bool header = cells.Count > 1 && (Enumerable.Range(0, count).Any(column =>
            columns[column].IsNumeric && cells[0][column].Text.Length > 0 && !IsNumeric(cells[0][column].Text))
            || (cells[0].All(cell => cell.Text.Length > 0 && !IsNumeric(cell.Text))
                && rows[0].Bounds.Height > OcrGeometry.Median(rows.Skip(1).Select(row => row.Bounds.Height)) * 1.15));
        return new(cells.Select(row => new OcrTableRow(row)).ToArray(), columns, header);
    }

    private static bool IsSparseBoundaryRow(OcrLayoutRow row, OcrLayoutRow adjacent,
        IReadOnlyList<OcrLayoutRun> reference, double gap, double pitch, double tolerance)
    {
        double spacing = Math.Abs(CenterY(row.Bounds) - CenterY(adjacent.Bounds));
        if (Math.Abs(spacing - pitch) > Math.Max(pitch * .25, tolerance)) return false;
        var runs = row.Runs(gap);
        if (runs.Count != 1) return false;
        OcrRect bounds = runs[0].Bounds;
        for (int column = 0; column < reference.Count; column++)
        {
            OcrRect anchor = reference[column].Bounds;
            // Alignment alone would admit a spanning title that starts at the first column.
            // The candidate must also fit one column's lane, allowing normal glyph variation.
            double left = column == 0 ? double.NegativeInfinity
                : (reference[column - 1].Bounds.Right + anchor.X) / 2;
            double right = column == reference.Count - 1 ? double.PositiveInfinity
                : (anchor.Right + reference[column + 1].Bounds.X) / 2;
            if (Distance(bounds, anchor) <= tolerance
                && bounds.X >= left - tolerance && bounds.Right <= right + tolerance) return true;
        }
        return false;
    }

    private static double CenterY(OcrRect bounds) => bounds.Y + bounds.Height / 2;

    private static double Distance(OcrRect value, OcrRect anchor) =>
        Math.Min(Math.Abs(value.X - anchor.X), Math.Min(Math.Abs(value.Right - anchor.Right),
            Math.Abs(value.X + value.Width / 2 - anchor.X - anchor.Width / 2)));

    internal static bool IsNumeric(string text)
    {
        string value = text.Trim().TrimStart('$', '₩', '€', '£', '¥').TrimEnd('%').Trim();
        // OCR can represent a thousands separator as a space. This is only an alignment
        // classification; exported text stays exactly as recognized.
        string[] parts = value.TrimStart('+', '-').Split('.');
        string[] groups = parts[0].Split([' ', '\u00a0', '\u202f']);
        if (parts.Length <= 2 && groups.Length > 1 && groups[0].Length is >= 1 and <= 3
            && groups[0].All(char.IsAsciiDigit)
            && groups.Skip(1).All(group => group.Length == 3 && group.All(char.IsAsciiDigit))
            && (parts.Length == 1 || parts[1].Length > 0 && parts[1].All(char.IsAsciiDigit)))
            value = value.Replace(" ", "").Replace("\u00a0", "").Replace("\u202f", "");
        return value.Length > 0 && double.TryParse(value, NumberStyles.Number | NumberStyles.AllowExponent
            | NumberStyles.AllowParentheses, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number);
    }
}
