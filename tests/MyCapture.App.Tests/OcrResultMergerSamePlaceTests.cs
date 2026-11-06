using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrResultMergerSamePlaceTests
{
    [Fact]
    public void PaddedQuantityIsRemovedWhileConflictingAmountRemains()
    {
        OcrLine row = Row(Word("19", 93.8, 36.8, 10.2, 8.5), Word("3.634", 140, 36.8, 29.3, 8.5));
        OcrLine quantity = Cell("19", 85, 29, 23, 19);
        OcrLine amount = Cell("3,634", 132, 29, 44, 19);
        OcrResult neural = Result(quantity, amount);

        OcrResult merged = OcrResultMerger.Merge(Result(row), neural, TimeSpan.Zero);

        Assert.Equal("19 3.634\n3,634", merged.Text);
        Assert.Equal(new[] { row, amount }, merged.Lines);
        Assert.Same(row.Words, merged.Lines[0].Words);
        Assert.Same(neural.Lines, merged.NeuralLines);
    }

    [Fact]
    public void BothPaddedCellsAreRemovedWhenTightWindowsRowRepeatsThem()
    {
        OcrLine row = Row(Word("8", 226.7, 137.7, 9.6, 15.7), Word("29,102", 301, 137.7, 52.4, 15.7));
        OcrResult neural = Result(Cell("8", 216, 126, 27, 32), Cell("29,102", 291, 125, 71, 36));

        OcrResult merged = OcrResultMerger.Merge(Result(row), neural, TimeSpan.Zero);

        Assert.Equal("8 29,102", merged.Text);
        Assert.Equal(row, Assert.Single(merged.Lines));
        Assert.Same(row.Words, merged.Lines[0].Words);
        Assert.Same(neural.Lines, merged.NeuralLines);
    }

    [Fact]
    public void EarlierRowWithSameQuantitySurvivesRemovalAtLaterRow()
    {
        OcrLine first = Row(Word("06k", 10, 46.8, 20, 8.5), Word("lamp", 40, 46.8, 30, 8.5),
            Word("9", 116, 46.8, 7, 8.5), Word("21,103", 150, 46.8, 35, 8.5));
        OcrLine second = Row(Word("9", 116, 142.8, 7, 8.5), Word("4,245", 150, 142.8, 30, 8.5));

        OcrResult merged = OcrResultMerger.Merge(Result(first, second), Result(Cell("9", 109, 136, 18, 19)), TimeSpan.Zero);

        Assert.Equal("06k lamp 9 21,103\n9 4,245", merged.Text);
        Assert.Equal(new[] { first, second }, merged.Lines);
        Assert.Equal(2, merged.Lines.SelectMany(line => line.Words).Count(word => word.Text == "9"));
    }

    [Fact]
    public void RepeatedTableQuantityKeepsOneWordOnEachRow()
    {
        OcrLine first = Row(Word("모니터", 10, 36.8, 40, 8.5), Word("1", 93.8, 36.8, 7, 8.5),
            Word("1,204,000", 140, 36.8, 50, 8.5));
        OcrLine second = Row(Word("마우스", 10, 76.8, 40, 8.5), Word("1", 93.8, 76.8, 7, 8.5),
            Word("9,900", 140, 76.8, 30, 8.5));

        OcrResult merged = OcrResultMerger.Merge(Result(first, second),
            Result(Cell("1", 85, 29, 23, 19), Cell("1", 85, 69, 23, 19)), TimeSpan.Zero);

        Assert.Equal("모니터 1 1,204,000\n마우스 1 9,900", merged.Text);
        Assert.Equal(new[] { first, second }, merged.Lines);
        Assert.All(merged.Lines, line => Assert.Single(line.Words, word => word.Text == "1"));
    }

    [Fact]
    public void SameTextInDifferentColumnRemains()
    {
        OcrLine row = Row(Word("1", 93.8, 36.8, 7, 8.5), Word("9,900", 140, 36.8, 30, 8.5));
        OcrLine amount = Cell("1", 145, 29, 23, 19);

        OcrResult merged = OcrResultMerger.Merge(Result(row), Result(amount), TimeSpan.Zero);

        Assert.Equal("1 9,900\n1", merged.Text);
        Assert.Equal(new[] { row, amount }, merged.Lines);
    }

    [Fact]
    public void HalfCoveredWordRemains()
    {
        OcrLine row = Row(Word("19", 100, 36.8, 10, 8.5), Word("3.634", 140, 36.8, 29.3, 8.5));
        OcrLine quantity = Cell("19", 82, 29, 23, 19);

        OcrResult merged = OcrResultMerger.Merge(Result(row), Result(quantity), TimeSpan.Zero);

        Assert.Equal("19\n19 3.634", merged.Text);
        Assert.Equal(new[] { quantity, row }, merged.Lines);
    }

    [Fact]
    public void DistinctColumnsKeepExistingTextAndGeometry()
    {
        // The layout from NearbyNonOverlappingColumnsRemainSeparateAndSortLeftToRightDespiteJitter.
        OcrLine left = Cell("왼쪽", 10, 12, 40, 20);
        OcrLine right = Cell("오른쪽", 100, 10, 60, 20);

        OcrResult merged = OcrResultMerger.Merge(Result(left), Result(right), TimeSpan.Zero);

        Assert.Equal("왼쪽\n오른쪽", merged.Text);
        Assert.Equal(new[] { left, right }, merged.Lines);
        Assert.Same(left.Words, merged.Lines[0].Words);
        Assert.Same(right.Words, merged.Lines[1].Words);
    }

    [Fact]
    public void SameEngineRepetitionsRemain()
    {
        OcrLine row = Row(Word("19", 93.8, 36.8, 10.2, 8.5), Word("3.634", 140, 36.8, 29.3, 8.5));
        OcrLine quantity = Cell("19", 85, 29, 23, 19);
        OcrResult reading = Result(quantity, row);
        OcrResult empty = OcrResult.NoText("en-US", TimeSpan.Zero);

        OcrResult windowsOnly = OcrResultMerger.Merge(reading, empty, TimeSpan.Zero);
        OcrResult neuralOnly = OcrResultMerger.Merge(empty, reading, TimeSpan.Zero);

        Assert.Equal("19\n19 3.634", windowsOnly.Text);
        Assert.Equal(new[] { quantity, row }, windowsOnly.Lines);
        Assert.Equal(windowsOnly.Text, neuralOnly.Text);
        Assert.Equal(windowsOnly.Lines, neuralOnly.Lines);
    }

    [Fact]
    public void ContiguousWordRunMatchesCompactCellText()
    {
        OcrLine row = Row(Word("1", 93.8, 36.8, 4, 8.5), Word("9", 100, 36.8, 4, 8.5),
            Word("3.634", 140, 36.8, 29.3, 8.5));

        OcrResult merged = OcrResultMerger.Merge(Result(row), Result(Cell("1 9", 85, 29, 23, 19)), TimeSpan.Zero);

        Assert.Equal("1 9 3.634", merged.Text);
        Assert.Equal(row, Assert.Single(merged.Lines));
    }

    [Fact]
    public void NoncontiguousWordsDoNotMatchCellText()
    {
        OcrLine row = Row(Word("1", 93.8, 36.8, 2, 8.5), Word("2", 98, 36.8, 2, 8.5),
            Word("9", 102, 36.8, 2, 8.5), Word("3.634", 140, 36.8, 29.3, 8.5));
        OcrLine quantity = Cell("19", 85, 29, 23, 19);

        OcrResult merged = OcrResultMerger.Merge(Result(row), Result(quantity), TimeSpan.Zero);

        Assert.Equal("19\n1 2 9 3.634", merged.Text);
        Assert.Equal(new[] { quantity, row }, merged.Lines);
    }

    [Fact]
    public void SubstringInsideWordDoesNotMatchCellText()
    {
        OcrLine row = Row(Word("19", 93.8, 36.8, 10.2, 8.5), Word("3.634", 140, 36.8, 29.3, 8.5));
        OcrLine quantity = Cell("9", 85, 29, 23, 19);

        OcrResult merged = OcrResultMerger.Merge(Result(row), Result(quantity), TimeSpan.Zero);

        Assert.Equal("9\n19 3.634", merged.Text);
        Assert.Equal(new[] { quantity, row }, merged.Lines);
    }

    [Fact]
    public void TightWindowsRepetitionIsRemovedFromAssembledNeuralRow()
    {
        OcrLine row = Row(Word("alpha", 12, 14, 20, 10), Word("beta", 80, 14, 20, 10));
        OcrLine repetition = Cell("alpha", 12, 14, 20, 10);
        OcrLine[] cells = [Cell("alpha", 8, 10, 28, 18), Cell("beta", 76, 10, 28, 18)];

        OcrResult merged = OcrResultMerger.Merge(Result(row, repetition), Result(cells), TimeSpan.Zero);

        Assert.Equal("alpha beta", merged.Text);
        OcrLine kept = Assert.Single(merged.Lines);
        Assert.Equal(OcrRect.Union(cells[0].Bounds, cells[1].Bounds), kept.Bounds);
        Assert.Equal(cells.SelectMany(line => line.Words), kept.Words);
    }

    [Fact]
    public void EqualCompactTextKeepsWindowsOverAssembledNeuralRow()
    {
        OcrLine first = Row(Word("alpha", 12, 14, 20, 10), Word("beta", 80, 14, 20, 10));
        OcrLine second = Row(Word("alpha", 12, 14, 20, 10), Word("beta", 80, 14, 20, 10));
        OcrLine[] cells = [Cell("alpha", 8, 10, 28, 18), Cell("beta", 76, 10, 28, 18)];

        OcrResult merged = OcrResultMerger.Merge(Result(first, second), Result(cells), TimeSpan.Zero);

        Assert.Equal("alpha beta", merged.Text);
        Assert.Equal(second, Assert.Single(merged.Lines));
        Assert.Same(second.Words, merged.Lines[0].Words);
    }

    [Fact]
    public void RotatedSourceUsesReadingAxesAndKeepsOriginalWordBoxes()
    {
        var orientation = new OcrReadingOrientation(90, 400, 800);
        OcrLine row = Row(Word("19", 93.8, 36.8, 10.2, 8.5), Word("3.634", 140, 36.8, 29.3, 8.5));
        row = row with
        {
            Bounds = orientation.ToOriginal(row.Bounds),
            Words = row.Words.Select(word => word with { Bounds = orientation.ToOriginal(word.Bounds) }).ToArray(),
        };
        OcrLine quantity = Cell("19", 85, 29, 23, 19);
        quantity = quantity with
        {
            Bounds = orientation.ToOriginal(quantity.Bounds),
            Words = quantity.Words.Select(word => word with { Bounds = orientation.ToOriginal(word.Bounds) }).ToArray(),
        };
        OcrResult windows = OcrResult.Success(row.Text, "en-US", new[] { row }, TimeSpan.Zero, orientation);

        OcrResult merged = OcrResultMerger.Merge(windows, Result(quantity), TimeSpan.Zero);

        Assert.Equal("19 3.634", merged.Text);
        Assert.Equal(row, Assert.Single(merged.Lines));
        Assert.Same(row.Words, merged.Lines[0].Words);
        Assert.Equal(orientation, merged.ReadingOrientation);
    }

    private static OcrResult Result(params OcrLine[] lines) =>
        OcrResult.Success(string.Join('\n', lines.Select(line => line.Text)), "en-US", lines, TimeSpan.Zero);

    private static OcrWord Word(string text, double x, double y, double width, double height) =>
        new(text, new OcrRect(x, y, width, height));

    private static OcrLine Cell(string text, double x, double y, double width, double height)
    {
        var bounds = new OcrRect(x, y, width, height);
        return new(text, bounds, new[] { new OcrWord(text, bounds) });
    }

    private static OcrLine Row(params OcrWord[] words) =>
        new(string.Join(' ', words.Select(word => word.Text)),
            words.Select(word => word.Bounds).Aggregate(default(OcrRect), OcrRect.Union), words);
}
