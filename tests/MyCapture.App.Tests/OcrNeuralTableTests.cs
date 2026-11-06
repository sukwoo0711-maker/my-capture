using MyCapture.Core.Settings;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrNeuralTableTests
{
    private const string ExpectedTsv = "항목\t수량\t금액\r\n키보드\t2\t128,400\r\nUSB 케이블\t12\t9,900\r\n모니터\t1\t1,204,000";

    [Fact]
    public void PreferredTableUsesNeuralHeaderAndFillsOnlyMissingQuantity()
    {
        OcrResult result = FillResult();
        OcrTable merged = OcrExtractionAnalyzer.Analyze(result).Table;
        OcrTable neural = OcrExtractionAnalyzer.Analyze(Result(result.NeuralLines)).Table;
        OcrTable table = OcrExtractionAnalyzer.Analyze(result, preferNeuralTable: true).Table;

        Assert.Equal(3, table.Columns.Count);
        Assert.Equal(4, table.Rows.Count);
        Assert.True(table.HasHeader);
        Assert.Equal(new[] { "항목", "수량", "금액" }, table.Rows[0].Cells.Select(cell => cell.Text));
        Assert.Equal("", neural.Rows[3].Cells[1].Text);
        Assert.Equal("1", table.Rows[3].Cells[1].Text);
        Assert.Equal(merged.Rows[3].Cells[1].Bounds, table.Rows[3].Cells[1].Bounds);
        Assert.Equal(neural.Rows[3].Cells[1].Region, table.Rows[3].Cells[1].Region);
        Assert.Equal(neural.Columns.ToArray(), table.Columns.ToArray());
        Assert.Equal(ExpectedTsv, OcrTableExport.Tsv(table));
    }

    [Fact]
    public void RepeatedNeuralValuesRemainInSeparateRowsAndOverrideMergedValues()
    {
        OcrLine[] neural = TableLines(padded: true, firstQuantity: "1", secondQuantity: "1");
        OcrResult result = Result(TableLines(padded: false)).WithNeuralLines(neural);
        OcrTable table = OcrExtractionAnalyzer.Analyze(result, preferNeuralTable: true).Table;

        Assert.Equal("1", table.Rows[1].Cells[1].Text);
        Assert.Equal("1", table.Rows[2].Cells[1].Text);
        Assert.Equal(neural[1].Words[1].Bounds, table.Rows[1].Cells[1].Bounds);
        Assert.Equal(neural[2].Words[1].Bounds, table.Rows[2].Cells[1].Bounds);
        AssertTableEqual(OcrExtractionAnalyzer.Analyze(Result(neural)).Table, table);
    }

    [Fact]
    public void DifferentRowCountReturnsNeuralTableWithoutFilling()
    {
        OcrLine[] neural = TableLines(padded: true, lastQuantity: "").Skip(1).ToArray();
        OcrResult result = Result(TableLines(padded: false)).WithNeuralLines(neural);
        OcrTable table = OcrExtractionAnalyzer.Analyze(result, preferNeuralTable: true).Table;

        Assert.Equal(3, table.Rows.Count);
        Assert.Equal("", table.Rows[2].Cells[1].Text);
        AssertTableEqual(OcrExtractionAnalyzer.Analyze(Result(neural)).Table, table);
    }

    [Fact]
    public void DifferentColumnCountReturnsNeuralTableWithoutFilling()
    {
        OcrLine[] neural = TableLines(padded: true).Select(line => Line(line.Words.Take(2))).ToArray();
        OcrResult result = Result(TableLines(padded: false)).WithNeuralLines(neural);
        OcrTable table = OcrExtractionAnalyzer.Analyze(result, preferNeuralTable: true).Table;

        Assert.Equal(2, table.Columns.Count);
        Assert.Equal(4, table.Rows.Count);
        AssertTableEqual(OcrExtractionAnalyzer.Analyze(Result(neural)).Table, table);
    }

    [Fact]
    public void MergedTableWithoutNumericColumnsRemainsMerged()
    {
        OcrResult result = Result(TextTableLines(padded: false)).WithNeuralLines(TableLines(padded: true));
        OcrExtraction expected = OcrExtractionAnalyzer.Analyze(result);

        Assert.True(expected.Table.HasStructure);
        Assert.All(expected.Table.Columns, column => Assert.False(column.IsNumeric));
        AssertExtractionEqual(expected, OcrExtractionAnalyzer.Analyze(result, preferNeuralTable: true));
    }

    [Fact]
    public void MergedTableWithoutStructureRemainsMerged()
    {
        OcrLine[] merged = TableLines(padded: false).Select(line => Line([line.Words[1]])).ToArray();
        OcrResult result = Result(merged).WithNeuralLines(TableLines(padded: true));
        OcrExtraction expected = OcrExtractionAnalyzer.Analyze(result);

        Assert.False(expected.Table.HasStructure);
        Assert.True(Assert.Single(expected.Table.Columns).IsNumeric);
        AssertExtractionEqual(expected, OcrExtractionAnalyzer.Analyze(result, preferNeuralTable: true));
    }

    [Fact]
    public void NeuralTableWithoutNumericColumnsFallsBackToMerged()
    {
        OcrLine[] neural = TextTableLines(padded: true);
        OcrTable neuralTable = OcrExtractionAnalyzer.Analyze(Result(neural)).Table;
        OcrResult result = Result(TableLines(padded: false)).WithNeuralLines(neural);

        Assert.True(neuralTable.HasStructure);
        Assert.All(neuralTable.Columns, column => Assert.False(column.IsNumeric));
        AssertExtractionEqual(OcrExtractionAnalyzer.Analyze(result),
            OcrExtractionAnalyzer.Analyze(result, preferNeuralTable: true));
    }

    [Fact]
    public void NeuralTableWithoutStructureFallsBackToMerged()
    {
        OcrLine[] neural = TableLines(padded: true).Take(1).ToArray();
        OcrResult result = Result(TableLines(padded: false)).WithNeuralLines(neural);

        Assert.False(OcrExtractionAnalyzer.Analyze(Result(neural)).Table.HasStructure);
        AssertExtractionEqual(OcrExtractionAnalyzer.Analyze(result),
            OcrExtractionAnalyzer.Analyze(result, preferNeuralTable: true));
    }

    [Fact]
    public void ResultsWithoutNeuralLinesRemainIdenticalBeforeAndAfterQualityCopy()
    {
        OcrResult result = Result(TableLines(padded: false));
        OcrResult copied = result.WithQuality(OcrQuality.Accurate);

        Assert.Empty(result.NeuralLines);
        Assert.Empty(copied.NeuralLines);
        AssertExtractionEqual(OcrExtractionAnalyzer.Analyze(result),
            OcrExtractionAnalyzer.Analyze(result, preferNeuralTable: true));
        AssertExtractionEqual(OcrExtractionAnalyzer.Analyze(copied),
            OcrExtractionAnalyzer.Analyze(copied, preferNeuralTable: true));
    }

    [Fact]
    public void DefaultAndExplicitFalseReturnMergedExtractionWithNeuralLinesAttached()
    {
        OcrResult merged = Result(TableLines(padded: false, garbledHeader: true));
        OcrResult result = merged.WithNeuralLines(TableLines(padded: true, lastQuantity: ""));
        OcrExtraction expected = OcrExtractionAnalyzer.Analyze(merged);

        AssertExtractionEqual(expected, OcrExtractionAnalyzer.Analyze(result));
        AssertExtractionEqual(expected, OcrExtractionAnalyzer.Analyze(result, preferNeuralTable: false));
    }

    [Fact]
    public void SelectionExcludesNeuralWordCentresOutsideEvenWhenBoxesOverlap()
    {
        OcrResult result = FillResult();
        var selection = new OcrRect(0, 0, 500, 125);
        OcrWord outside = result.NeuralLines[3].Words[0];
        Assert.True(outside.Bounds.Y < selection.Bottom);
        Assert.True(outside.Bounds.Y + outside.Bounds.Height / 2 > selection.Bottom);

        OcrExtraction selected = OcrExtractionAnalyzer.Analyze(result, selection, preferNeuralTable: true);

        Assert.Equal(3, selected.Table.Rows.Count);
        Assert.Equal("항목\t수량\t금액\r\n키보드\t2\t128,400\r\nUSB 케이블\t12\t9,900",
            OcrTableExport.Tsv(selected.Table));
        AssertNonTableEqual(OcrExtractionAnalyzer.Analyze(result, selection), selected);
    }

    [Fact]
    public void CopyMethodsPreserveResultMetadataQualityAndNeuralLines()
    {
        OcrLine[] lines = TableLines(padded: false);
        OcrLine[] neural = TableLines(padded: true);
        var orientation = new OcrReadingOrientation(90, 600, 500);
        OcrResult original = OcrResult.Success("merged", "ko", lines, TimeSpan.FromMilliseconds(42),
            orientation, textAngle: -89).WithQuality(OcrQuality.Balanced);
        OcrResult attached = original.WithNeuralLines(neural);
        OcrResult copied = attached.WithQuality(OcrQuality.Accurate);

        Assert.Equal(OcrQuality.Balanced, attached.Quality);
        Assert.Equal(OcrQuality.Accurate, copied.Quality);
        Assert.Empty(original.NeuralLines);
        foreach (OcrResult result in new[] { attached, copied })
        {
            Assert.Equal(original.Status, result.Status);
            Assert.Equal(original.Text, result.Text);
            Assert.Equal(original.LanguageTag, result.LanguageTag);
            Assert.Same(original.Lines, result.Lines);
            Assert.Equal(original.Elapsed, result.Elapsed);
            Assert.Equal(original.Message, result.Message);
            Assert.Equal(original.ReadingOrientation, result.ReadingOrientation);
            Assert.Equal(original.TextAngle, result.TextAngle);
            Assert.Same(neural, result.NeuralLines);
        }
        OcrResult failed = OcrResult.Failed("recognition failed", TimeSpan.FromSeconds(1));
        Assert.Equal(failed.Message, failed.WithNeuralLines(neural).WithQuality(OcrQuality.Accurate).Message);
    }

    [Fact]
    public void SuccessfulMergeAttachesNeuralLinesAndCancelledMergesHaveNone()
    {
        OcrResult windows = Result(TableLines(padded: false));
        OcrResult neural = Result(TableLines(padded: true));
        OcrResult merged = OcrResultMerger.Merge(windows, neural, TimeSpan.Zero);

        Assert.Equal(OcrStatus.Success, merged.Status);
        Assert.Same(neural.Lines, merged.NeuralLines);
        Assert.Empty(neural.NeuralLines);
        Assert.Empty(OcrResultMerger.Merge(windows, OcrResult.NoText("ko", TimeSpan.Zero), TimeSpan.Zero).NeuralLines);
        foreach (OcrResult cancelled in new[]
        {
            OcrResultMerger.Merge(windows, OcrResult.Cancelled(), TimeSpan.Zero),
            OcrResultMerger.Merge(OcrResult.Cancelled(), neural, TimeSpan.Zero),
        })
        {
            Assert.Equal(OcrStatus.Cancelled, cancelled.Status);
            Assert.Empty(cancelled.NeuralLines);
        }
    }

    [Fact]
    public void PreferredTableKeepsMergedCodeAndParagraphsIncludingSelectionContext()
    {
        OcrResult fill = FillResult();
        AssertNonTableEqual(OcrExtractionAnalyzer.Analyze(fill),
            OcrExtractionAnalyzer.Analyze(fill, preferNeuralTable: true));

        const string source = "public class Sample {\n    public void Run() {\n        Console.WriteLine(1);\n    }\n}";
        OcrResult code = Ocr410ExtractionTests.CodeResult(source, 240);
        OcrResult mixed = Result(fill.Lines.Concat(code.Lines).ToArray()).WithNeuralLines(fill.NeuralLines);
        OcrExtraction preferred = OcrExtractionAnalyzer.Analyze(mixed, preferNeuralTable: true);
        Assert.Equal(ExpectedTsv, OcrTableExport.Tsv(preferred.Table));
        Assert.Equal(source, preferred.Code.Text);
        AssertNonTableEqual(OcrExtractionAnalyzer.Analyze(mixed), preferred);

        var selection = new OcrRect(0, 286, 800, 20);
        OcrExtraction selected = OcrExtractionAnalyzer.Analyze(mixed, selection, preferNeuralTable: true);
        Assert.Equal("        Console.WriteLine(1);", selected.Code.Text);
        Assert.Equal("C#", selected.Code.Language);
        AssertExtractionEqual(OcrExtractionAnalyzer.Analyze(mixed, selection), selected);
    }

    [Fact]
    public void NeuralWordsUseTheSameTrimmingValidityAndLineFallbackRules()
    {
        OcrLine[] neural = TableLines(padded: true).SelectMany(line => line.Words).Select(word =>
            new OcrLine("  " + word.Text + "  ", word.Bounds, [])).ToArray();
        OcrLine[] invalid =
        [
            new("ignored words", new OcrRect(0, 0, 100, 20),
                [new("   ", new OcrRect(0, 0, 10, 10)), new("invalid", new OcrRect(double.NaN, 0, 10, 10))]),
            new("invalid fallback", new OcrRect(0, 0, 0, 10), []),
            new("   ", new OcrRect(0, 0, 10, 10), []),
        ];
        OcrResult result = Result(TableLines(padded: false, garbledHeader: true))
            .WithNeuralLines([.. neural, .. invalid]);

        Assert.Equal(ExpectedTsv, OcrTableExport.Tsv(
            OcrExtractionAnalyzer.Analyze(result, preferNeuralTable: true).Table));
    }

    [Fact]
    public void CachedGeometryAndNoTextEarlyReturnsRemainIdentical()
    {
        OcrLine[] neural = TableLines(padded: true);
        OcrResult cached = OcrResult.Success("cached\r\ntext", "ko", [], TimeSpan.Zero).WithNeuralLines(neural);
        OcrResult empty = OcrResult.NoText("ko", TimeSpan.Zero).WithNeuralLines(neural);

        foreach (OcrResult result in new[] { cached, empty })
        {
            AssertExtractionEqual(OcrExtractionAnalyzer.Analyze(result),
                OcrExtractionAnalyzer.Analyze(result, preferNeuralTable: true));
            AssertExtractionEqual(OcrExtractionAnalyzer.Analyze(result, new OcrRect(0, 0, 500, 200)),
                OcrExtractionAnalyzer.Analyze(result, new OcrRect(0, 0, 500, 200), preferNeuralTable: true));
        }
    }

    [Fact]
    public void RotatedNeuralTableAndFilledBoundsReturnToSourceCoordinates()
    {
        OcrResult upright = FillResult();
        var orientation = new OcrReadingOrientation(90, 600, 500);
        OcrLine[] merged = ToOriginal(upright.Lines, orientation);
        OcrResult rotated = OcrResult.Success(upright.Text, "ko", merged, TimeSpan.Zero, orientation)
            .WithNeuralLines(ToOriginal(upright.NeuralLines, orientation));
        OcrTable expected = OcrExtractionAnalyzer.Analyze(upright, preferNeuralTable: true).Table;
        OcrTable actual = OcrExtractionAnalyzer.Analyze(rotated, preferNeuralTable: true).Table;

        Assert.Equal(ExpectedTsv, OcrTableExport.Tsv(actual));
        for (int row = 0; row < expected.Rows.Count; row++)
        for (int column = 0; column < expected.Columns.Count; column++)
        {
            Assert.Equal(orientation.ToOriginal(expected.Rows[row].Cells[column].Bounds), actual.Rows[row].Cells[column].Bounds);
            Assert.Equal(orientation.ToOriginal(expected.Rows[row].Cells[column].Region), actual.Rows[row].Cells[column].Region);
        }
        var selection = orientation.ToOriginal(new OcrRect(0, 0, 500, 125));
        Assert.Equal(3, OcrExtractionAnalyzer.Analyze(rotated, selection, preferNeuralTable: true).Table.Rows.Count);
        AssertNonTableEqual(OcrExtractionAnalyzer.Analyze(rotated),
            OcrExtractionAnalyzer.Analyze(rotated, preferNeuralTable: true));
    }

    private static OcrResult FillResult() => Result(TableLines(padded: false, garbledHeader: true))
        .WithNeuralLines(TableLines(padded: true, lastQuantity: ""));

    private static OcrLine[] TableLines(bool padded, bool garbledHeader = false,
        string firstQuantity = "2", string secondQuantity = "12", string lastQuantity = "1")
    {
        string[][] cells =
        [
            garbledHeader ? ["öh모", "O", "그"] : ["항목", "수량", "금액"],
            ["키보드", firstQuantity, "128,400"],
            ["USB 케이블", secondQuantity, "9,900"],
            ["모니터", lastQuantity, "1,204,000"],
        ];
        return cells.Select((row, index) => Line(row.Select((text, column) =>
        {
            double width = OcrGeometry.CharacterColumns(text) * 8;
            double x = column == 0 ? 20 : (column == 1 ? 240 : 440) - width;
            double y = 20 + index * 36;
            OcrRect bounds = padded ? new(x - 4, y - 6.4, width + 8, 28.8) : new(x, y, width, 16);
            return new OcrWord(text, bounds);
        }).Where(word => word.Text.Length > 0))).ToArray();
    }

    private static OcrLine[] TextTableLines(bool padded) => TableLines(padded).Select(line => Line(
        line.Words.Select(word => OcrTableReconstructor.IsNumeric(word.Text) ? word with { Text = "값" } : word))).ToArray();

    private static OcrLine Line(IEnumerable<OcrWord> words)
    {
        OcrWord[] array = words.ToArray();
        return new(string.Join(" ", array.Select(word => word.Text)),
            array.Select(word => word.Bounds).Aggregate(default(OcrRect), OcrRect.Union), array);
    }

    private static OcrResult Result(IReadOnlyList<OcrLine> lines) =>
        OcrResult.Success(string.Join("\n", lines.Select(line => line.Text)), "ko", lines, TimeSpan.Zero);

    private static OcrLine[] ToOriginal(IReadOnlyList<OcrLine> lines, OcrReadingOrientation orientation) =>
        lines.Select(line => line with
        {
            Bounds = orientation.ToOriginal(line.Bounds),
            Words = line.Words.Select(word => word with { Bounds = orientation.ToOriginal(word.Bounds) }).ToArray(),
        }).ToArray();

    private static void AssertExtractionEqual(OcrExtraction expected, OcrExtraction actual)
    {
        AssertTableEqual(expected.Table, actual.Table);
        AssertNonTableEqual(expected, actual);
    }

    private static void AssertTableEqual(OcrTable expected, OcrTable actual)
    {
        Assert.Equal(expected.HasHeader, actual.HasHeader);
        Assert.Equal(expected.HasStructure, actual.HasStructure);
        Assert.Equal(expected.Bounds, actual.Bounds);
        Assert.Equal(expected.Columns.ToArray(), actual.Columns.ToArray());
        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        for (int row = 0; row < expected.Rows.Count; row++)
            Assert.Equal(expected.Rows[row].Cells.ToArray(), actual.Rows[row].Cells.ToArray());
    }

    private static void AssertNonTableEqual(OcrExtraction expected, OcrExtraction actual)
    {
        Assert.Equal(expected.HasGeometry, actual.HasGeometry);
        Assert.Equal(expected.Code.Text, actual.Code.Text);
        Assert.Equal(expected.Code.Language, actual.Code.Language);
        Assert.Equal(expected.Code.IsMonospaced, actual.Code.IsMonospaced);
        Assert.Equal(expected.Code.CharacterWidth, actual.Code.CharacterWidth);
        Assert.Equal(expected.Code.Regions.ToArray(), actual.Code.Regions.ToArray());
        Assert.Equal(expected.Paragraphs.Count, actual.Paragraphs.Count);
        for (int paragraph = 0; paragraph < expected.Paragraphs.Count; paragraph++)
        {
            OcrParagraph left = expected.Paragraphs[paragraph], right = actual.Paragraphs[paragraph];
            Assert.Equal(left.Bounds, right.Bounds);
            Assert.Equal(left.Lines.ToArray(), right.Lines.ToArray());
            Assert.Equal(left.SourceLines.Count, right.SourceLines.Count);
            for (int line = 0; line < left.SourceLines.Count; line++)
            {
                Assert.Equal(left.SourceLines[line].Text, right.SourceLines[line].Text);
                Assert.Equal(left.SourceLines[line].Bounds, right.SourceLines[line].Bounds);
                Assert.Equal(left.SourceLines[line].Words.ToArray(), right.SourceLines[line].Words.ToArray());
            }
        }
    }
}
