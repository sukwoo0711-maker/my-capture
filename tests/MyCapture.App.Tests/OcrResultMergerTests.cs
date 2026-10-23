using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrResultMergerTests
{
    private static readonly TimeSpan WallTime = TimeSpan.FromMilliseconds(125);

    [Fact]
    public void CompactAgreementKeepsWindowsSpacingAndWordGeometry()
    {
        OcrLine windows = Row(10, Word("다운로드", 10, 10, 80), Word("폴더", 100, 10, 40));
        OcrLine neural = Line("다운로드폴더", 8, 8, 136, 24);

        OcrResult merged = Merge(Result(windows), Result(neural));

        Assert.Equal("다운로드 폴더", merged.Text);
        Assert.Equal(windows.Bounds, Assert.Single(merged.Lines).Bounds);
        Assert.Same(windows.Words, merged.Lines[0].Words);
        Assert.Equal("다운로드 폴더", OcrExtractionAnalyzer.Analyze(merged).Text(false));
        Assert.Equal(WallTime, merged.Elapsed);
    }

    [Fact]
    public void AlignsBySourceOverlapInsteadOfInputOrder()
    {
        OcrResult windows = Result(Line("second row", 10, 50), Line("first row", 10, 10));
        OcrResult neural = Result(Line("firstrow", 12, 12), Line("secondrow", 12, 52));

        OcrResult merged = Merge(windows, neural);

        Assert.Equal("first row\nsecond row", merged.Text);
        Assert.Equal(2, merged.Lines.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PaddedNeuralBoxesAlignWithTightWindowsGlyphBoxesOnAgreement(bool reverseWindows, bool reverseNeural)
    {
        // Original-coordinate boxes observed in the real dual benchmark: neural padding
        // makes both heights exceed twice the Windows height despite matching centers.
        OcrLine[] windows =
        [
            Line("const rows = [1, 2, 3];", 10.75, 12.75, 158.5, 10.5),
            Line("return rows.map(row => row + 1);", 10, 32.75, 187.25, 11.5),
        ];
        OcrLine[] neural =
        [
            Line("const rows = [1, 2, 3];", 5, 4, 169, 24),
            Line("return rows.map(row=>row+1);", 5, 24, 197, 26),
        ];

        OcrResult merged = Merge(Result(reverseWindows ? windows.Reverse().ToArray() : windows),
            Result(reverseNeural ? neural.Reverse().ToArray() : neural));

        Assert.Equal("const rows = [1, 2, 3];\nreturn rows.map(row => row + 1);", merged.Text);
        Assert.Equal(2, merged.Lines.Count);
        Assert.Equal(windows.Select(line => line.Bounds), merged.Lines.Select(line => line.Bounds));
        Assert.Same(windows[0].Words, merged.Lines[0].Words);
        Assert.Same(windows[1].Words, merged.Lines[1].Words);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PaddedNeuralBoxesAlignOnDisagreementWithoutChangingTextSelection(bool reverseWindows, bool reverseNeural)
    {
        OcrLine[] windows =
        [
            Line("const rows = [1, 2, 3];", 10.75, 12.75, 158.5, 10.5),
            Line("return rows.map(row => row + 1);", 10, 32.75, 187.25, 11.5),
        ];
        OcrLine[] neural =
        [
            Line("const rows = [1, 2, 8];", 5, 4, 169, 24),
            Line("return rows.map(row => row + 2);", 5, 24, 197, 26),
        ];

        OcrResult merged = Merge(Result(reverseWindows ? windows.Reverse().ToArray() : windows),
            Result(reverseNeural ? neural.Reverse().ToArray() : neural));

        // These equally plausible alternatives still take the existing neural fallback;
        // alignment must remove duplicates without treating either string as ground truth.
        Assert.Equal("const rows = [1, 2, 8];\nreturn rows.map(row => row + 2);", merged.Text);
        Assert.Equal(2, merged.Lines.Count);
        Assert.Equal(neural.Select(line => line.Bounds), merged.Lines.Select(line => line.Bounds));
        Assert.Same(neural[0].Words, merged.Lines[0].Words);
        Assert.Same(neural[1].Words, merged.Lines[1].Words);
    }

    [Fact]
    public void RetainsShortTitleDroppedByNeuralAndBodySpacing()
    {
        OcrResult windows = Result(Line("설정", 10, 10, 40), Line("저장 위치 선택", 10, 55, 160));
        OcrResult neural = Result(Line("저장위치선택", 8, 53, 164, 24));

        OcrResult merged = Merge(windows, neural);

        Assert.Equal("설정\n저장 위치 선택", merged.Text);
        Assert.Equal(2, merged.Lines.Count);
    }

    [Fact]
    public void RetainsLowContrastBlockWhenWindowsReturnsNoText()
    {
        OcrResult neural = Result(Line("희미한 본문 첫 줄", 10, 10), Line("희미한 본문 둘째 줄", 10, 40));

        OcrResult merged = Merge(OcrResult.NoText("ko-KR", TimeSpan.Zero), neural);

        Assert.Equal(neural.Text, merged.Text);
        Assert.Equal(neural.Lines, merged.Lines);
        Assert.Equal(WallTime, merged.Elapsed);
    }

    [Fact]
    public void NearbyNonOverlappingColumnsRemainSeparateAndSortLeftToRightDespiteJitter()
    {
        OcrResult windows = Result(Line("왼쪽", 10, 12, 40));
        OcrResult neural = Result(Line("오른쪽", 100, 10, 60));

        OcrResult merged = Merge(windows, neural);

        Assert.Equal(new[] { "왼쪽", "오른쪽" }, merged.Lines.Select(line => line.Text));
        Assert.Equal(2, merged.Lines.Count);
    }

    [Fact]
    public void DistinctRowsWithIdenticalValuesAreNotDeduplicated()
    {
        OcrResult windows = Result(Line("100", 10, 10), Line("100", 10, 40));
        OcrResult neural = Result(Line("100", 11, 11), Line("100", 11, 41));

        OcrResult merged = Merge(windows, neural);

        Assert.Equal("100\n100", merged.Text);
        Assert.Equal(2, merged.Lines.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AdjacentRowsDoNotMergeThroughInterveningDetection(bool reverseWindows, bool matchesSecondRow)
    {
        OcrLine[] rows = [Line("alpha", 10, 0, 100, 20), Line("beta", 10, 16, 100, 20)];
        OcrResult windows = Result(reverseWindows ? rows.Reverse().ToArray() : rows);
        OcrResult neural = Result(Line(matchesSecondRow ? "beta" : "alpha", 10, 8, 100, 20));

        OcrResult merged = Merge(windows, neural);

        Assert.Equal("alpha\nbeta", merged.Text);
        Assert.Equal(2, merged.Lines.Count);
        Assert.Equal(rows.Select(line => line.Bounds), merged.Lines.Select(line => line.Bounds));
    }

    [Fact]
    public void OneWindowsRowAlignsWithSeveralNeuralCellsWithoutDuplication()
    {
        OcrLine windows = Row(10, Word("USB", 10, 10, 30), Word("cable", 44, 10, 42),
            Word("2", 200, 10, 16), Word("99.50", 300, 10, 50));
        OcrResult neural = Result(Line("99.50", 300, 9, 50), Line("USBcable", 10, 12, 76),
            Line("2", 200, 11, 16));

        OcrResult merged = Merge(Result(windows), neural);

        Assert.Equal("USB cable 2 99.50", merged.Text);
        Assert.Equal(windows.Words, Assert.Single(merged.Lines).Words);
    }

    [Fact]
    public void SeveralWindowsFragmentsAlignWithOneNeuralRow()
    {
        OcrResult windows = Result(Line("left cell", 10, 12, 80), Line("right cell", 150, 10, 80));
        OcrResult neural = Result(Line("leftcellrightcell", 8, 8, 226, 24));

        OcrResult merged = Merge(windows, neural);

        Assert.Equal(new[] { "left cell", "right cell" }, merged.Lines.Select(line => line.Text));
        Assert.Equal(windows.Lines.Sum(line => line.Words.Count), merged.Lines.Sum(line => line.Words.Count));
    }

    [Fact]
    public void TallSpuriousBoxDoesNotConsumeSeparateSourceRows()
    {
        OcrResult windows = Result(Line("first row", 10, 10), Line("second row", 10, 50));
        OcrResult neural = Result(Line("uncertain", 8, 8, 105, 65));

        OcrResult merged = Merge(windows, neural);

        Assert.Contains(merged.Lines, line => line.Text == "first row");
        Assert.Contains(merged.Lines, line => line.Text == "second row");
    }

    [Fact]
    public void SpreadsheetGridKeepsCellValuesAndOriginalBoxes()
    {
        OcrResult windows = Result(
            Row(10, Word("Part", 20, 10, 40), Word("Qty", 232, 10, 30), Word("Amount", 400, 10, 60)),
            Row(34, Word("Keyboard", 20, 34, 80), Word("1280", 416, 34, 40)),
            Row(58, Word("USB", 20, 58, 24), Word("cable", 52, 58, 40),
                Word("2", 248, 58, 8), Word("99.50", 408, 58, 40)));
        OcrResult neural = Result(windows.Lines.SelectMany(line => line.Words)
            .Select(word => new OcrLine(word.Text, word.Bounds, new[] { word })).Reverse().ToArray());

        OcrResult merged = Merge(windows, neural);
        OcrTable table = OcrExtractionAnalyzer.Analyze(merged).Table;

        Assert.Equal("Part,Qty,Amount\r\nKeyboard,,1280\r\nUSB cable,2,99.50", OcrTableExport.Csv(table));
        Assert.Equal(windows.Lines.SelectMany(line => line.Words), merged.Lines.SelectMany(line => line.Words));
        Assert.Equal(windows.Lines[1].Words[0].Bounds, table.Rows[1].Cells[0].Bounds);
        Assert.Equal(default, table.Rows[1].Cells[1].Bounds);
        Assert.True(table.HasHeader);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingInteriorNeuralCellKeepsCorroboratedWindowsRowAndQuantity(bool splitFirstCellIntoWords)
    {
        OcrWord[] firstCell = splitFirstCellIntoWords
            ? [Word("USB", 10, 40, 30), Word("cable", 44, 40, 42)]
            : [Word("USB cable", 10, 40, 76)];
        OcrWord quantity = Word("2", 200, 40, 16);
        OcrLine header = Row(10, Word("Part", 10, 10, 40), Word("Qty", 200, 10, 30),
            Word("Amount", 300, 10, 60));
        OcrLine body = Row(40, [.. firstCell, quantity, Word("99.50", 300, 40, 50)]);
        OcrResult windows = Result(header, body);
        OcrResult neural = Result(header.Words.Select(word => new OcrLine(word.Text, word.Bounds, [word]))
            .Concat(new[] { Line("USBcable", 10, 40, 76), Line("99.50", 300, 40, 50) }).ToArray());

        OcrResult merged = Merge(windows, neural);
        OcrTable table = OcrExtractionAnalyzer.Analyze(merged).Table;

        Assert.Equal("Part Qty Amount\nUSB cable 2 99.50", merged.Text);
        Assert.Equal("Part,Qty,Amount\r\nUSB cable,2,99.50", OcrTableExport.Csv(table));
        Assert.Equal(3, table.Columns.Count);
        Assert.Equal("2", table.Rows[1].Cells[1].Text);
        Assert.Equal(quantity.Bounds, table.Rows[1].Cells[1].Bounds);
        Assert.Same(body.Words, merged.Lines[1].Words);
    }

    [Fact]
    public void InteriorWordDoesNotFavorWindowsWhenObservedCellsDisagree()
    {
        OcrLine windows = Row(10, Word("USB cable", 10, 10, 76), Word("2", 200, 10, 16),
            Word("99.50", 300, 10, 50));
        OcrResult neural = Result(Line("USBcase", 10, 10, 76), Line("99.50", 300, 10, 50));

        OcrResult merged = Merge(Result(windows), neural);

        Assert.Equal("USBcase\n99.50", merged.Text);
        Assert.Equal(neural.Lines, merged.Lines);
    }

    [Theory]
    [InlineData("ru-RU", "Привет мир", "privetmir")]
    [InlineData("ja-JP", "設定を保存", "설정저장")]
    [InlineData("zh-Hans", "保存设置", "보존설정")]
    public void RetainsWindowsScriptOutsideKoreanModelCoverage(string language, string expected, string substitute)
    {
        OcrResult windows = Result(language, Line(expected, 10, 10));
        OcrResult neural = Result("ko-KR", Line(substitute, 9, 9, 102, 22));

        OcrResult merged = Merge(windows, neural);

        Assert.Equal(expected, merged.Text);
        Assert.Equal(language, merged.LanguageTag);
    }

    [Fact]
    public void LanguageTagDifferenceDoesNotPreventSharedLatinSpacingAgreement()
    {
        OcrResult merged = Merge(Result("en-US", Line("Save folder", 10, 10)),
            Result("ko-KR", Line("Savefolder", 10, 10)));

        Assert.Equal("Save folder", merged.Text);
        Assert.Equal("en-US", merged.LanguageTag);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectsReplacementCharactersAndPunctuationNoiseFromEitherEngine(bool windowsHasNoise)
    {
        OcrResult noise = Result(Line("���", 10, 10), Line("???", 10, 40), Line("\uE000", 10, 70));
        OcrResult useful = Result(Line("정상 본문", 10, 10));

        OcrResult merged = windowsHasNoise ? Merge(noise, useful) : Merge(useful, noise);

        Assert.Equal("정상 본문", merged.Text);
        Assert.Single(merged.Lines);
    }

    [Fact]
    public void CodeSymbolsAndStandaloneClosingBraceSurvive()
    {
        OcrResult windows = Result("en-US", Line("if(x>=2){return x+1;}", 10, 10, 200), Line("}", 10, 40, 8));
        OcrResult neural = Result("ko-KR", Line("ifx2returnx1", 9, 9, 202, 22));

        OcrResult merged = Merge(windows, neural);

        Assert.Equal("if(x>=2){return x+1;}\n}", merged.Text);
        Assert.Equal(windows.Lines[0].Words, merged.Lines[0].Words);
    }

    [Fact]
    public void CodeSymbolsCanAlsoComeFromNeural()
    {
        OcrResult merged = Merge(Result(Line("x2", 10, 10)), Result(Line("x+=2;", 10, 10)));

        Assert.Equal("x+=2;", merged.Text);
    }

    [Fact]
    public void PlausibleDisagreementKeepsAccurateEngineInsteadOfRewardingLength()
    {
        OcrResult merged = Merge(Result(Line("much longer guessed text", 10, 10)),
            Result(Line("실제 본문", 10, 10)));

        Assert.Equal("실제 본문", merged.Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ContainmentWithWiderGeometryRecoversTruncatedText(bool windowsHasFullText)
    {
        OcrResult full = Result(Line("prefix body suffix", 10, 10, 170));
        OcrResult partial = Result(Line("body", 80, 10, 40));

        OcrResult merged = windowsHasFullText ? Merge(full, partial) : Merge(partial, full);

        Assert.Equal(full.Text, merged.Text);
        Assert.Equal(full.Lines[0].Bounds, Assert.Single(merged.Lines).Bounds);
    }

    [Fact]
    public void EmptyInputsReturnNoTextWithWallTime()
    {
        OcrResult merged = Merge(OcrResult.NoText("ko-KR", TimeSpan.Zero),
            OcrResult.NoText("ko-KR", TimeSpan.Zero));

        Assert.Equal(OcrStatus.NoText, merged.Status);
        Assert.Empty(merged.Lines);
        Assert.Empty(merged.Text);
        Assert.Equal(WallTime, merged.Elapsed);
    }

    [Fact]
    public void SuccessfulEngineSurvivesOtherEngineFailure()
    {
        OcrResult useful = Result(Line("본문", 10, 10));

        Assert.Equal("본문", Merge(useful, OcrResult.Failed("failed")).Text);
        Assert.Equal("본문", Merge(OcrResult.Unavailable("missing"), useful).Text);
    }

    [Fact]
    public void NoSuccessfulEnginePreservesFailureAndCancellation()
    {
        OcrResult failed = Merge(OcrResult.Unavailable("missing"), OcrResult.Failed("failed"));
        Assert.Equal(OcrStatus.Failed, failed.Status);
        Assert.Equal("failed", failed.Message);
        Assert.Equal(WallTime, failed.Elapsed);
        Assert.Equal(OcrStatus.Cancelled, Merge(Result(Line("text", 10, 10)), OcrResult.Cancelled()).Status);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RetainsReadingAxesAndOriginalCoordinatesForRotatedSource(int rotation)
    {
        var orientation = new OcrReadingOrientation(rotation, 400, 800);
        OcrLine[] upright = [Line("first row", 10, 10), Line("second row", 10, 50)];
        OcrLine[] original = upright.Select(line => line with
        {
            Bounds = orientation.ToOriginal(line.Bounds),
            Words = line.Words.Select(word => word with { Bounds = orientation.ToOriginal(word.Bounds) }).ToArray(),
        }).ToArray();
        OcrResult windows = OcrResult.Success("first row\nsecond row", "en-US", original.Reverse().ToArray(),
            TimeSpan.Zero, orientation);
        OcrResult neural = Result("ko-KR", original.Select(line => line with
        {
            Text = line.Text.Replace(" ", "", StringComparison.Ordinal),
        }).ToArray());

        OcrResult merged = Merge(windows, neural);

        Assert.Equal("first row\nsecond row", merged.Text);
        Assert.Equal(orientation, merged.ReadingOrientation);
        Assert.Equal(original.Select(line => line.Bounds), merged.Lines.Select(line => line.Bounds));
        Assert.Equal(original.SelectMany(line => line.Words), merged.Lines.SelectMany(line => line.Words));
        Assert.Equal("first row\n\nsecond row", OcrExtractionAnalyzer.Analyze(merged).Text(false));
    }

    [Fact]
    public void LeavesInputRecordsAndCollectionsUnchanged()
    {
        OcrLine[] lines = [Line(" Save folder ", 10, 10)];
        OcrResult windows = Result(lines);
        OcrResult neural = Result(Line("Savefolder", 10, 10));
        OcrWord[] words = lines[0].Words.ToArray();

        OcrResult merged = Merge(windows, neural);

        Assert.Equal(" Save folder ", windows.Lines[0].Text);
        Assert.Same(lines, windows.Lines);
        Assert.Equal(words, windows.Lines[0].Words);
        Assert.Equal("Savefolder", neural.Text);
        Assert.Equal("Save folder", merged.Text);
    }

    private static OcrResult Merge(OcrResult windows, OcrResult neural) =>
        OcrResultMerger.Merge(windows, neural, WallTime);

    private static OcrResult Result(params OcrLine[] lines) => Result("ko-KR", lines);

    private static OcrResult Result(string language, params OcrLine[] lines) =>
        OcrResult.Success(string.Join('\n', lines.Select(line => line.Text)), language, lines, TimeSpan.Zero);

    private static OcrLine Line(string text, double x, double y, double width = 100, double height = 20)
    {
        var bounds = new OcrRect(x, y, width, height);
        return new(text, bounds, [new(text, bounds)]);
    }

    private static OcrWord Word(string text, double x, double y, double width) =>
        new(text, new OcrRect(x, y, width, 20));

    private static OcrLine Row(double y, params OcrWord[] words) =>
        new(string.Join(' ', words.Select(word => word.Text)),
            new OcrRect(words.Min(word => word.Bounds.X), y,
                words.Max(word => word.Bounds.Right) - words.Min(word => word.Bounds.X), 20), words);
}
