namespace MyCapture.Ocr;

internal static class OcrTextReconstructor
{
    internal static IReadOnlyList<OcrParagraph> Build(IReadOnlyList<OcrLayoutRow> rows)
    {
        if (rows.Count == 0) return [];
        double height = OcrGeometry.Median(rows.Select(row => row.Bounds.Height));
        double wordGap = Math.Max(height * 1.3,
            OcrGeometry.CharacterWidth(rows.SelectMany(row => row.Words)) * 3);
        var runs = rows.SelectMany(row => row.Runs(wordGap)).ToList();
        var blocks = new List<List<OcrLayoutRun>>();
        OrderBlocks(runs, height, blocks, 0);
        var paragraphs = new List<OcrParagraph>();
        foreach (var block in blocks)
        {
            var ordered = OcrLayoutRow.Build(block.SelectMany(run => run.Words));
            var lines = new List<string>();
            var sourceLines = new List<OcrLine>();
            OcrRect bounds = default;
            double previousBottom = 0;
            foreach (var row in ordered)
            {
                if (lines.Count > 0 && row.Bounds.Y - previousBottom > height * .85)
                {
                    paragraphs.Add(new(lines.ToArray(), bounds) { SourceLines = sourceLines.ToArray() });
                    lines.Clear();
                    sourceLines.Clear();
                    bounds = default;
                }
                lines.Add(row.Text);
                sourceLines.Add(new(row.Text, row.Bounds, row.Words.ToArray()));
                bounds = OcrRect.Union(bounds, row.Bounds);
                previousBottom = row.Bounds.Bottom;
            }
            if (lines.Count > 0) paragraphs.Add(new(lines.ToArray(), bounds) { SourceLines = sourceLines.ToArray() });
        }
        return paragraphs;
    }

    private static void OrderBlocks(List<OcrLayoutRun> runs, double height,
        List<List<OcrLayoutRun>> output, int depth)
    {
        if (runs.Count == 0) return;
        if (depth >= 32) { output.Add(runs); return; }
        double? column = VerticalCut(runs, height);
        if (column is not null)
        {
            OrderBlocks(runs.Where(run => run.Bounds.Right <= column).ToList(), height, output, depth + 1);
            OrderBlocks(runs.Where(run => run.Bounds.X >= column).ToList(), height, output, depth + 1);
            return;
        }

        // A title spanning both columns must precede the left column, then the right column.
        double top = runs.Min(run => run.Bounds.Y);
        var heading = runs.Where(run => run.Bounds.Y < top + height * .5).ToList();
        var body = runs.Except(heading).ToList();
        column = VerticalCut(body, height);
        if (heading.Count == 1 && column is not null
            && heading[0].Bounds.X < column && heading[0].Bounds.Right > column)
        {
            output.Add(heading);
            OrderBlocks(body, height, output, depth + 1);
            return;
        }

        double bottom = runs.Max(run => run.Bounds.Bottom);
        var footer = runs.Where(run => run.Bounds.Bottom > bottom - height * .5).ToList();
        body = runs.Except(footer).ToList();
        column = VerticalCut(body, height);
        if (footer.Count == 1 && column is not null
            && footer[0].Bounds.X < column && footer[0].Bounds.Right > column)
        {
            OrderBlocks(body, height, output, depth + 1);
            output.Add(footer);
            return;
        }

        var gap = LargestGap(runs.Select(run => (run.Bounds.Y, run.Bounds.Bottom)));
        if (gap.Width > height * .85)
        {
            OrderBlocks(runs.Where(run => run.Bounds.Bottom <= gap.Cut).ToList(), height, output, depth + 1);
            OrderBlocks(runs.Where(run => run.Bounds.Y >= gap.Cut).ToList(), height, output, depth + 1);
            return;
        }
        output.Add(runs);
    }

    private static double? VerticalCut(IReadOnlyList<OcrLayoutRun> runs, double height)
    {
        if (runs.Count < 4) return null;
        var gap = LargestGap(runs.Select(run => (run.Bounds.X, run.Bounds.Right)));
        if (gap.Width < height * 1.5) return null;
        var left = runs.Where(run => run.Bounds.Right <= gap.Cut).ToList();
        var right = runs.Where(run => run.Bounds.X >= gap.Cut).ToList();
        return left.Count >= 2 && right.Count >= 2
            && left.Max(run => run.Bounds.Bottom) > right.Min(run => run.Bounds.Y)
            && right.Max(run => run.Bounds.Bottom) > left.Min(run => run.Bounds.Y)
            ? gap.Cut : null;
    }

    private static (double Cut, double Width) LargestGap(IEnumerable<(double Start, double End)> intervals)
    {
        var sorted = intervals.OrderBy(interval => interval.Start).ToArray();
        if (sorted.Length == 0) return (0, 0);
        double end = sorted[0].End, largest = 0, cut = 0;
        foreach (var interval in sorted.Skip(1))
        {
            if (interval.Start - end > largest)
            {
                largest = interval.Start - end;
                cut = (interval.Start + end) / 2;
            }
            end = Math.Max(end, interval.End);
        }
        return (cut, largest);
    }
}
