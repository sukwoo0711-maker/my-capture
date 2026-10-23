using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrResultMergerTests
{
    private static readonly TimeSpan WallTime = TimeSpan.FromMilliseconds(125);

    [Theory]
    [InlineData("...")]
    [InlineData("=>")]
    [InlineData("₩")]
    public void SpatialAgreementPreservesSymbolOnlyLines(string text)
    {
        OcrLine windows = Line(text, 10, 10, 35, 20);
        OcrResult merged = Merge(Result(windows), Result(Line(text, 12, 11, 35, 20)));
        Assert.Equal(OcrStatus.Success, merged.Status);
        Assert.Equal(text, merged.Text);
        Assert.Single(merged.Lines);
    }

    [Theory]
    [InlineData("...")]
    [InlineData("=>")]
    [InlineData("₩")]
    public void AgreementInDifferentCellsDoesNotRescueSymbols(string text)
    {
        OcrResult merged = Merge(Result(Line(text, 10, 10, 35, 20)), Result(Line(text, 100, 10, 35, 20)));
        Assert.Equal(OcrStatus.NoText, merged.Status);
        Assert.Empty(merged.Lines);
    }

    [Fact]
    public void SpatialAgreementAcceptsPaddedNeuralBoxOffsetFromTightGlyphs()
    {
        // A PP-OCR box padded by the unclip ratio, its center well below the tight Windows box.
        OcrResult merged = Merge(Result(Line("...", 10, 10, 30, 10)), Result(Line("...", 8, 12, 34, 20)));
        Assert.Equal(OcrStatus.Success, merged.Status);
        Assert.Equal("...", merged.Text);
        Assert.Single(merged.Lines);
    }

    [Fact]
    public void SpatialAgreementPreservesLowDensityDotLeaderLine()
    {
        // Two digits among eleven glyphs fall below the single-engine density filter.
        OcrResult merged = Merge(Result(Line(".........12", 10, 10, 120)), Result(Line(".........12", 11, 11, 120)));
        Assert.Equal(".........12", merged.Text);
        Assert.Single(merged.Lines);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UncorroboratedSymbolOnlyLineIsStillDropped(bool windowsHasSymbol)
    {
        OcrResult symbol = Result(Line("본문", 10, 10), Line("=>", 10, 40, 30));
        OcrResult other = Result(Line("본문", 10, 10));
        OcrResult merged = windowsHasSymbol ? Merge(symbol, other) : Merge(other, symbol);
        Assert.Equal("본문", merged.Text);
        Assert.Single(merged.Lines);
    }

    [Theory]
    [InlineData("\uFFFD")]
    [InlineData("\uE000")]
    [InlineData("\u200B")]
    [InlineData("\u0001")]
    [InlineData("=>\uFFFD")]
    public void AgreedInvalidGlyphsAreStillRejected(string text)
    {
        OcrResult merged = Merge(Result(Line(text, 10, 10)), Result(Line(text, 10, 10)));
        Assert.Equal(OcrStatus.NoText, merged.Status);
        Assert.Empty(merged.Lines);
    }

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

        // Corroborated row/cell matches now prefer the cell-level text and preserve its
        // real word geometry, assembling one visual row rather than retaining the wide row.
        Assert.Equal("USBcable 2 99.50", merged.Text);
        Assert.Equal(neural.Lines.OrderBy(line => line.Bounds.X).SelectMany(line => line.Words),
            Assert.Single(merged.Lines).Words);
        Assert.Equal("USBcable,2,99.50", OcrTableExport.Csv(OcrExtractionAnalyzer.Analyze(merged).Table));
    }

    [Fact]
    public void SeveralWindowsFragmentsAlignWithOneNeuralRow()
    {
        OcrResult windows = Result(Line("left cell", 10, 12, 80), Line("right cell", 150, 10, 80));
        OcrResult neural = Result(Line("leftcellrightcell", 8, 8, 226, 24));

        OcrResult merged = Merge(windows, neural);

        // The same assembly applies with the engines reversed: the Windows cell text
        // supplies a single row while its separate cell boxes continue to define columns.
        Assert.Equal("left cell right cell", Assert.Single(merged.Lines).Text);
        Assert.Equal(windows.Lines.Sum(line => line.Words.Count), merged.Lines.Sum(line => line.Words.Count));
        Assert.Equal(windows.Lines.OrderBy(line => line.Bounds.X).SelectMany(line => line.Words), merged.Lines[0].Words);
        Assert.Equal("left cell,right cell", OcrTableExport.Csv(OcrExtractionAnalyzer.Analyze(merged).Table));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Observed031SpreadsheetBoxesEmitCellRepresentationOnce(bool rowFromWindows, bool reverseCells)
    {
        // Retained original-coordinate line boxes from 031-spreadsheet. The word fallback
        // is synthetic: the old report retained these line boxes, not individual word boxes.
        OcrLine row = Line("용지 1,250.50 2026-10-23", 10.25, 32.75, 261, 12.5);
        OcrLine[] cells =
        [
            Line("용지", 0, 22, 39, 31),
            Line("1,250.50", 86, 25, 62, 27),
            Line("2026-10-23", 198, 25, 78, 27),
        ];
        OcrResult cellResult = Result(reverseCells ? cells.Reverse().ToArray() : cells);

        OcrResult merged = rowFromWindows ? Merge(Result(row), cellResult) : Merge(cellResult, Result(row));
        OcrExtraction extraction = OcrExtractionAnalyzer.Analyze(merged);

        Assert.Equal("용지 1,250.50 2026-10-23", merged.Text);
        Assert.Single(merged.Lines);
        Assert.Equal(cells.SelectMany(line => line.Words), merged.Lines[0].Words);
        Assert.Equal("용지 1,250.50 2026-10-23", extraction.Text(false));
        Assert.Equal("용지,\"1,250.50\",2026-10-23", OcrTableExport.Csv(extraction.Table));
        Assert.Equal(3, extraction.Table.Columns.Count);
        Assert.Equal(cells.Select(line => line.Bounds), extraction.Table.Rows[0].Cells.Select(cell => cell.Bounds));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void JpegStyle057SpreadsheetUsesCorroboratedCellsDespiteSmallRecognitionDifference(bool reverseCells, bool slightDifference)
    {
        // This earlier JPEG-style fixture uses illustrative padding/recognition variation.
        // A separate regression below uses the subsequently recovered actual 057 boxes.
        OcrLine row = Line(slightDifference ? "용지 1,250.50 2026-10-28" : "용지 1,250.50 2026-10-23",
            11, 33, 260, 13);
        OcrLine[] cells =
        [
            Line("용지", 2, 23, 40, 32),
            Line("1,250.50", 85, 24, 66, 30),
            Line("2026-10-23", 196, 23, 84, 31),
        ];

        OcrResult merged = Merge(Result(row), Result(reverseCells ? cells.Reverse().ToArray() : cells));
        OcrExtraction extraction = OcrExtractionAnalyzer.Analyze(merged);

        Assert.Equal("용지 1,250.50 2026-10-23", merged.Text);
        Assert.Single(merged.Lines);
        Assert.Equal(cells.SelectMany(line => line.Words), merged.Lines[0].Words);
        Assert.Equal(3, extraction.Table.Columns.Count);
        Assert.Equal("용지,\"1,250.50\",2026-10-23", OcrTableExport.Csv(extraction.Table));
        Assert.Equal("용지 1,250.50 2026-10-23", extraction.Text(false));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Observed057JpegNumericGroupingDifferenceEmitsCellRepresentationOnce(bool rowFromWindows, bool reverseCells)
    {
        // Original 057-jpeg70-spreadsheet A-dual evidence, including actual word boxes.
        OcrLine row = new("잉크 23 400", new OcrRect(11.25, 53.75, 120.75, 11.5),
        [
            new("잉크", new OcrRect(11.25, 53.75, 22.75, 11.5)),
            new("23", new OcrRect(94, 54.5, 13.25, 9.75)),
            new("400", new OcrRect(110.75, 54.5, 21.25, 9.75)),
        ]);
        OcrLine[] cells =
        [
            new("잉크", new OcrRect(4, 44, 34, 25), [new("잉크", new OcrRect(8, 44, 26, 25))]),
            new("23,400", new OcrRect(87, 46, 50, 23), [new("23,400", new OcrRect(93, 46, 42, 23))]),
        ];
        OcrResult cellResult = Result(reverseCells ? cells.Reverse().ToArray() : cells);

        OcrResult merged = rowFromWindows ? Merge(Result(row), cellResult) : Merge(cellResult, Result(row));
        OcrExtraction extraction = OcrExtractionAnalyzer.Analyze(merged);

        Assert.Equal("잉크 23,400", merged.Text);
        Assert.Equal(cells.SelectMany(line => line.Words), Assert.Single(merged.Lines).Words);
        Assert.Equal("잉크 23,400", extraction.Text(false));
        Assert.Equal("잉크,\"23,400\"", OcrTableExport.Csv(extraction.Table));
        Assert.Equal(2, extraction.Table.Columns.Count);
        Assert.Equal(cells.SelectMany(line => line.Words).Select(word => word.Bounds),
            extraction.Table.Rows[0].Cells.Select(cell => cell.Bounds));
    }

    [Theory]
    [InlineData("23400", "23,400")]
    [InlineData("1234567", "1,234,567")]
    [InlineData("-23400", "-23,400")]
    [InlineData("23400.50", "23,400.50")]
    public void NumericGroupingAgreementPreservesCellFormattingAndValue(string ungrouped, string grouped)
    {
        OcrLine row = Line("Ink " + ungrouped, 11.25, 53.75, 120.75, 11.5);
        OcrLine[] cells = [Line("Ink", 4, 44, 34, 25), Line(grouped, 87, 46, 50, 23)];

        OcrResult merged = Merge(Result(row), Result(cells));

        Assert.Equal("Ink " + grouped, merged.Text);
        Assert.Equal(cells.SelectMany(line => line.Words), Assert.Single(merged.Lines).Words);
    }

    [Theory]
    [InlineData("23400", "23,401")]
    [InlineData("23400", "-23,400")]
    [InlineData("23400", "+23,400")]
    [InlineData("23400", "23.400")]
    [InlineData("2340", "23,40")]
    [InlineData("23400", "2,34,00")]
    [InlineData("1.2345", "1.2,345")]
    [InlineData("12345", "12345.")]
    public void ShortNumericValueSignDecimalOrMalformedGroupingConflictDoesNotCreateRowMatch(string rowValue, string cellValue)
    {
        OcrLine row = Line("잉크 " + rowValue, 11.25, 53.75, 120.75, 11.5);
        OcrLine[] cells = [Line("잉크", 4, 44, 34, 25), Line(cellValue, 87, 46, 50, 23)];

        OcrResult merged = Merge(Result(row), Result(cells));

        // The ordinary area-overlap evidence is insufficient for these padded cell boxes.
        // Numeric formatting must not manufacture equivalence or merge conflicting values.
        Assert.Equal(3, merged.Lines.Count);
        Assert.Contains(merged.Lines, line => line.Text == row.Text);
        foreach (OcrLine cell in cells) Assert.Contains(merged.Lines, line => line.Text == cell.Text);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PaddedCellsFromAdjacentTableRowsRemainSeparate(bool reverseRows, bool reverseCells)
    {
        OcrLine[] rows =
        [
            Line("용지 1,250.50 2026-10-23", 10.25, 32.75, 261, 12.5),
            Line("토너 9,500.00 2026-10-24", 10.25, 52.75, 261, 12.5),
        ];
        OcrLine[] cells =
        [
            Line("용지", 0, 22, 39, 31), Line("1,250.50", 86, 25, 62, 27), Line("2026-10-23", 198, 25, 78, 27),
            Line("토너", 0, 42, 39, 31), Line("9,500.00", 86, 45, 62, 27), Line("2026-10-24", 198, 45, 78, 27),
        ];

        OcrResult merged = Merge(Result(reverseRows ? rows.Reverse().ToArray() : rows),
            Result(reverseCells ? cells.Reverse().ToArray() : cells));
        OcrTable table = OcrExtractionAnalyzer.Analyze(merged).Table;

        Assert.Equal("용지 1,250.50 2026-10-23\n토너 9,500.00 2026-10-24", merged.Text);
        Assert.Equal(2, merged.Lines.Count);
        Assert.Equal(cells.SelectMany(line => line.Words), merged.Lines.SelectMany(line => line.Words));
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(3, table.Columns.Count);
        Assert.Equal("용지,\"1,250.50\",2026-10-23\r\n토너,\"9,500.00\",2026-10-24", OcrTableExport.Csv(table));
    }

    [Fact]
    public void MateriallyDisagreeingCellsDoNotCreateAnApproximateWholeRowMatch()
    {
        OcrLine row = Line("용지 1,250.50 2026-10-23", 10.25, 32.75, 261, 12.5);
        OcrLine[] cells =
        [
            Line("잉크", 0, 22, 39, 31), Line("9,999.99", 86, 25, 62, 27), Line("2030-01-01", 198, 25, 78, 27),
        ];

        OcrResult merged = Merge(Result(row), Result(cells));

        // With weak ordinary overlap and incompatible text there is no evidence to discard
        // either interpretation. In particular, no synthetic hybrid row should be emitted.
        Assert.Equal(4, merged.Lines.Count);
        Assert.Contains(merged.Lines, line => line.Text == row.Text);
        foreach (OcrLine cell in cells) Assert.Contains(merged.Lines, line => line.Text == cell.Text);
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

    [Fact]
    public void ApproximatePaddedCellAgreementDoesNotEraseCorroboratedInteriorQuantity()
    {
        OcrWord[] words =
        [
            new("ProductName", new OcrRect(10, 30, 100, 10)),
            new("2", new OcrRect(200, 30, 8, 10)),
            new("12345678", new OcrRect(300, 30, 80, 10)),
        ];
        OcrLine row = new("ProductName 2 12345678", new OcrRect(10, 30, 370, 10), words);
        // Padded/narrower detector boxes fail area overlap for these tight word boxes.
        // Their text and horizontal containment still corroborate both outside cells.
        OcrResult neural = Result(Line("ProductName", 30, 20, 40, 30), Line("12345678", 295, 20, 30, 30));

        OcrResult merged = Merge(Result(row), neural);
        OcrTable table = OcrExtractionAnalyzer.Analyze(merged).Table;

        Assert.Equal("ProductName 2 12345678", merged.Text);
        Assert.Same(words, Assert.Single(merged.Lines).Words);
        Assert.Equal("ProductName,2,12345678", OcrTableExport.Csv(table));
        Assert.Equal(3, table.Columns.Count);
        Assert.Equal(words[1].Bounds, table.Rows[0].Cells[1].Bounds);
    }

    [Fact]
    public void OneWideWordCannotCorroborateTwoDifferentCellRegions()
    {
        OcrWord[] words =
        [
            new("A", new OcrRect(10, 30, 100, 10)),
            new("2", new OcrRect(200, 30, 8, 10)),
            new("12345678901234567890", new OcrRect(300, 30, 100, 10)),
        ];
        OcrLine row = new("A 2 12345678901234567890", new OcrRect(10, 30, 390, 10), words);
        OcrLine[] cells =
        [
            Line("A", 20, 20, 10, 30), Line("A", 80, 20, 10, 30),
            Line("12345678901234567890", 300, 20, 100, 30),
        ];

        OcrResult merged = Merge(Result(row), Result(cells));

        // Matching the same wide A box twice is ambiguous and cannot establish the
        // interior-quantity exception. The normal corroborated cell representation wins.
        Assert.Equal("A A 12345678901234567890", merged.Text);
        Assert.Equal(cells.SelectMany(line => line.Words), Assert.Single(merged.Lines).Words);
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Observed038BroadFragmentBoxesPreferPlausibleLongLineWithoutSubstringEvidence(bool longFromWindows, bool reverseInputs)
    {
        // The geometry and neural fragment are retained from 038-mixed-paragraph. The full
        // raw sentence was not retained; representative text deliberately excludes that glyph.
        OcrLine full = Line("선택한 문장의 내용을 확인합니다", 10.75, 32, 216.75, 12.25);
        OcrLine fragment = Line("이", 5, 24, 225, 24);
        OcrLine heading = Line("본문", 10, 0, 40, 12);
        OcrLine[] longLines = [heading, full];
        OcrLine[] shortLines = [heading, fragment];
        OcrResult longer = Result(reverseInputs ? longLines.Reverse().ToArray() : longLines);
        OcrResult shorter = Result(reverseInputs ? shortLines.Reverse().ToArray() : shortLines);

        OcrResult merged = longFromWindows ? Merge(longer, shorter) : Merge(shorter, longer);

        Assert.DoesNotContain("이", full.Text);
        Assert.Equal("본문\n선택한 문장의 내용을 확인합니다", merged.Text);
        Assert.Equal(2, merged.Lines.Count);
        Assert.Equal(full.Bounds, merged.Lines[1].Bounds);
        Assert.Same(full.Words, merged.Lines[1].Words);
    }

    [Theory]
    [InlineData("X")]
    [InlineData("ZZ")]
    public void BroadFragmentRuleUsesCoverageAndLengthRatherThanSpecificCharacters(string fragment)
    {
        OcrResult longer = Result("en-US", Line("Read the selected paragraph carefully", 12, 30, 220, 14));
        OcrResult shorter = Result("en-US", Line(fragment, 5, 22, 230, 30));

        OcrResult merged = Merge(longer, shorter);

        Assert.Equal(longer.Text, merged.Text);
        Assert.Equal(longer.Lines, merged.Lines);
    }

    [Fact]
    public void NarrowSingleCharacterDoesNotTriggerBroadFragmentPreference()
    {
        OcrResult longer = Result(Line("Read the selected paragraph carefully", 10, 10, 220, 20));
        OcrResult narrow = Result(Line("Z", 40, 10, 9, 20));

        OcrResult merged = Merge(longer, narrow);

        Assert.Equal("Z", merged.Text);
        Assert.Equal(narrow.Lines, merged.Lines);
    }

    [Fact]
    public void SeparateShortLabelOutsideLongLineIsRetained()
    {
        OcrResult longer = Result(Line("Read the selected paragraph carefully", 10, 10, 220, 20));
        OcrResult label = Result(Line("Z", 260, 10, 9, 20));

        OcrResult merged = Merge(longer, label);

        Assert.Equal(2, merged.Lines.Count);
        Assert.Equal("Read the selected paragraph carefully\nZ", merged.Text);
        Assert.Equal(label.Lines[0].Bounds, merged.Lines[1].Bounds);
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Observed026OffsetPaddedBoxesAlignWithTightGlyphRowsWithoutDuplicates(bool reverseWindows, bool reverseNeural)
    {
        // Line boxes retained from validation2 026 (offset heading, 100%, dark). Each padded
        // PP-OCR box sits 9-10 px above its Windows glyph box, beyond the center tolerance.
        OcrLine[] windows =
        [
            Line("내보내기", 28, 8.5, 50, 15.25), Line("이미지 형식", 2.25, 43.75, 57, 13.75),
            Line("석상 유지", 4, 73.5, 46.25, 12.75), Line("품즫 조정", 6, 102.5, 46.75, 13.25),
        ];
        OcrLine[] neural =
        [
            Line("Ichah", 24, 0, 62, 22), Line("이미지 형식", 0, 30, 66, 23),
            Line("색상 유지", 0, 59, 54, 23), Line("품질 조정", 0, 85, 57, 28),
        ];

        OcrResult merged = Merge(Result(reverseWindows ? windows.Reverse().ToArray() : windows),
            Result(reverseNeural ? neural.Reverse().ToArray() : neural));

        // One line per glyph row. Exact agreement keeps the Windows box; one-syllable
        // disagreements keep the existing neural choice; the Latin misreading is rejected.
        Assert.Equal("내보내기\n이미지 형식\n색상 유지\n품질 조정", merged.Text);
        Assert.Equal(new[] { windows[0].Bounds, windows[1].Bounds, neural[2].Bounds, neural[3].Bounds },
            merged.Lines.Select(line => line.Bounds));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void OffsetPaddedRowsWithIdenticalValuesStayOnePerRow(bool reverseWindows, bool reverseNeural)
    {
        OcrLine[] windows = [Line("100", 10, 40, 30, 12), Line("100", 10, 68, 30, 12)];
        OcrLine[] neural = [Line("100", 5, 28, 40, 24), Line("100", 5, 56, 40, 24)];

        OcrResult merged = Merge(Result(reverseWindows ? windows.Reverse().ToArray() : windows),
            Result(reverseNeural ? neural.Reverse().ToArray() : neural));

        Assert.Equal("100\n100", merged.Text);
        Assert.Equal(windows.Select(line => line.Bounds), merged.Lines.Select(line => line.Bounds));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Observed029ContainedGlyphBoxAlignsWithItsPaddedRowWithoutTextAgreement(bool reverseInputs)
    {
        // Original 029-code boxes. The garbled Windows first line lies inside the neural
        // box, which holds no other Windows row, so it is the same row and not an extra line.
        OcrLine[] windows =
        [
            Line("const I'OWS", 9.75, 16.5, 69, 9.25),
            Line("fnC{ 교•. 7, value: 0.25 }),•", 9.75, 31.5, 187.25, 16.5),
            Line("// 결과 확인: ok-done", 10.75, 54.75, 138.25, 15.25),
        ];
        OcrLine[] neural =
        [
            Line("const rows = [1, 2, 3];", 5, 4, 167, 23),
            Line("fn({ id: 7, value: 0.25 });", 4, 25, 196, 23),
            Line("//결과 확인: ok_done", 5, 44, 147, 26),
        ];

        OcrResult merged = Merge(Result(reverseInputs ? windows.Reverse().ToArray() : windows),
            Result(reverseInputs ? neural.Reverse().ToArray() : neural));

        // The substituted mark ("-" for "_") does not order the last pair; neural stays.
        Assert.Equal("const rows = [1, 2, 3];\nfn({ id: 7, value: 0.25 });\n//결과 확인: ok_done", merged.Text);
        Assert.Equal(neural.Select(line => line.Bounds), merged.Lines.Select(line => line.Bounds));
    }

    [Fact]
    public void SeparateRowInsideTallBoxIsNotJoinedToTheAgreeingRow()
    {
        OcrResult windows = Result(Line("first row", 10, 10), Line("second row", 10, 50));
        OcrResult neural = Result(Line("first row", 8, 8, 105, 65));

        OcrResult merged = Merge(windows, neural);

        Assert.Equal("first row\nsecond row", merged.Text);
        Assert.Equal(windows.Lines.Select(line => line.Bounds), merged.Lines.Select(line => line.Bounds));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Observed017StackedHalvesOfOneRowDoNotLeaveAnExtraLine(bool reverseInputs)
    {
        // validation2 017 (storage card, 150%): Windows split one bold row into an upper and a
        // lower partial line; both lie inside the single neural box for that row.
        OcrLine[] windows =
        [
            Line("보관함", 9.75, 8.75, 62.25, 19.5), Line("나으 요 근들", 10.75, 63.75, 71.5, 9.5),
            Line("口 0", 12.75, 72.75, 67.5, 6.5), Line("폴더 이동", 93.75, 117.75, 76.5, 17.5),
        ];
        OcrLine[] neural =
        [
            Line("보관함", 3, 0, 74, 35), Line("남은 용량", 3, 55, 84, 31), Line("폴더 이동", 86, 109, 89, 34),
        ];

        OcrResult merged = Merge(Result(reverseInputs ? windows.Reverse().ToArray() : windows),
            Result(reverseInputs ? neural.Reverse().ToArray() : neural));

        Assert.Equal("보관함\n남은 용량\n폴더 이동", merged.Text);
        Assert.Equal(3, merged.Lines.Count);
    }

    [Theory]
    [InlineData("내보내기", "Ichah", "내보내기")]
    [InlineData("Ichah", "내보내기", "내보내기")]
    [InlineData("최근 캡처", "Recent", "최근 캡처")]
    [InlineData("설", "Sl", "Sl")]
    [InlineData("Wi-Fi 연결", "WiFi 연걸", "WiFi 연걸")]
    public void HangulReadingWinsOnlyOverUnrelatedLatinReadingOfSameBox(string windows, string neural, string expected)
    {
        // One syllable is too little evidence, and mixed-script readings are not a script
        // conflict; both keep the ordinary neural default for an ambiguous disagreement.
        OcrResult merged = Merge(Result(Line(windows, 10, 10, 60, 16)), Result(Line(neural, 8, 6, 66, 24)));

        Assert.Equal(expected, merged.Text);
    }

    [Theory]
    [InlineData("2,750", "2750", "2,750")]
    [InlineData("2750", "2,750", "2,750")]
    [InlineData("cfg.enabled = active !== null;", "cfg.enabled = active != null;", "cfg.enabled = active !== null;")]
    [InlineData("저장하시겠습니까", "저장하시겠습니까?", "저장하시겠습니까?")]
    [InlineData("검토 대상: draft-v2", "검토 대상:draft_v2", "검토 대상:draft_v2")]
    [InlineData("연결 상태. Ready", "연결 상태: Ready", "연결 상태: Ready")]
    public void SameLettersKeepTheReadingWithEveryMarkAndSubstitutedMarksKeepNeural(
        string windows, string neural, string expected)
    {
        OcrResult merged = Merge(Result(Line(windows, 10, 10, 200)), Result(Line(neural, 9, 9, 202, 22)));

        Assert.Equal(expected, merged.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Observed053GarbledHeaderCellJoinsCorroboratedCells(bool reverseCells)
    {
        // validation2 053 header (150%): Windows read the first two cells as one line with the
        // second cell garbled; the neural cells agree with the first word and contain no digit.
        OcrLine row = new("구분 거 人", new OcrRect(12.67, 13.67, 161, 16.67),
        [
            new("구분", new OcrRect(12.67, 13.67, 32.67, 16.67)),
            new("거", new OcrRect(142, 13.67, 14, 11.67)),
            new("人", new OcrRect(159.67, 13.67, 14, 6.67)),
        ]);
        OcrLine[] cells = [Line("구분", 2, 1, 51, 40), Line("건수", 132, 2, 50, 36)];
        OcrLine[] neural = [.. cells, Line("단가", 286, 2, 50, 36), Line("잔액", 446, 1, 50, 37)];

        OcrResult merged = Merge(Result(row, Line("단가", 295.67, 13.67, 32.33, 16.67), Line("잔액", 455.67, 13.67, 31.67, 16.67)),
            Result(reverseCells ? neural.Reverse().ToArray() : neural));

        Assert.Equal("구분 건수\n단가\n잔액", merged.Text);
        Assert.Equal(cells.SelectMany(line => line.Words), merged.Lines[0].Words);
    }

    [Theory]
    [InlineData("23400", "23,401")]
    [InlineData("18", "16")]
    public void GarbledCellWithConflictingQuantityKeepsBothRowReadings(string rowValue, string cellValue)
    {
        OcrLine row = new("잉크 " + rowValue, new OcrRect(11.25, 53.75, 120.75, 11.5),
        [
            new("잉크", new OcrRect(11.25, 53.75, 22.75, 11.5)),
            new(rowValue, new OcrRect(94, 54.5, 38, 9.75)),
        ]);
        OcrLine[] cells = [Line("잉크", 4, 44, 34, 25), Line(cellValue, 87, 46, 50, 23)];

        OcrResult merged = Merge(Result(row), Result(cells));

        Assert.Equal(3, merged.Lines.Count);
        Assert.Contains(merged.Lines, line => line.Text == row.Text);
        Assert.Contains(merged.Lines, line => line.Text == cellValue);
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
