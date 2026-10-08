using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class Ocr410ExtractionTests
{
    [Fact]
    public void Table_ReconstructsRowsColumnsEmptyCellsAndRightAlignedNumbers()
    {
        var result = Result(
            Line(10, Word("Item", 20, 10, 40), Word("Count", 190, 10, 50), Word("Amount", 330, 10, 60)),
            Line(34, Word("Keyboard", 20, 34, 80), Word("2", 230, 34, 10), Word("1,280", 340, 34, 50)),
            Line(58, Word("USB cable", 20, 58, 90), Word("99.50", 340, 58, 50)),
            Line(82, Word("Monitor", 20, 82, 70), Word("12", 220, 82, 20), Word("-20", 360, 82, 30)));
        OcrTable table = OcrExtractionAnalyzer.Analyze(result).Table;
        Assert.Equal(3, table.Columns.Count);
        Assert.Equal(4, table.Rows.Count);
        Assert.True(table.HasHeader);
        Assert.False(table.Columns[0].IsNumeric);
        Assert.True(table.Columns[1].IsNumeric);
        Assert.True(table.Columns[2].IsNumeric);
        Assert.Equal("", table.Rows[2].Cells[1].Text);
        Assert.Equal("99.50", table.Rows[2].Cells[2].Text);
        Assert.Equal(340, table.Rows[2].Cells[2].Bounds.X);
        Assert.True(table.Rows[2].Cells[2].Region.Width > 0);
        Assert.StartsWith("Item,Count,Amount\r\n", OcrTableExport.Csv(table));
        Assert.Contains("| --- | ---: | ---: |", OcrTableExport.Markdown(table));
    }

    [Fact]
    public void Table_AlignsSpaceGroupedNumbersWithoutRewritingRecognizedText()
    {
        var table = OcrExtractionAnalyzer.Analyze(Result(
            Line(0, Word("Item", 20, 0), Word("Amount", 300, 0)),
            Line(24, Word("A", 20, 24), Word("128,400", 300, 24)),
            Line(48, Word("B", 20, 48), Word("1 204 000", 300, 48)),
            Line(72, Word("C", 20, 72), Word("9 900", 300, 72)))).Table;
        Assert.True(table.Columns[1].IsNumeric);
        Assert.Equal("1 204 000", table.Rows[2].Cells[1].Text);
        Assert.Contains("B,1 204 000", OcrTableExport.Csv(table));
        Assert.False(OcrTableReconstructor.IsNumeric("1 20"));
        Assert.False(OcrTableReconstructor.IsNumeric("12 000 items"));
        Assert.True(OcrTableReconstructor.IsNumeric("1\u202f204\u202f000.50"));
    }

    [Fact]
    public void Table_DoesNotConsumeFirstNumericRowAsHeader()
    {
        var table = OcrExtractionAnalyzer.Analyze(Result(
            Line(0, Word("10", 0, 0), Word("100", 160, 0)),
            Line(24, Word("20", 0, 24), Word("200", 160, 24)))).Table;
        Assert.False(table.HasHeader);
        Assert.Equal(2, table.Rows.Count);
        Assert.StartsWith("| A | B |\n", OcrTableExport.Markdown(table));
        Assert.Contains("| 10 | 100 |", OcrTableExport.Markdown(table));
    }

    [Fact]
    public void Table_SortsUnorderedLinesAndWordsWithoutChangingOriginalCoordinates()
    {
        OcrWord amount = Word("300", 220, 64, 30);
        var table = OcrExtractionAnalyzer.Analyze(Result(
            Line(64, amount, Word("B", 20, 64)),
            Line(16, Word("Total", 200, 16), Word("Name", 20, 16)),
            Line(40, Word("100", 220, 40), Word("A", 20, 40)))).Table;
        Assert.Equal("Name", table.Rows[0].Cells[0].Text);
        Assert.Equal("B", table.Rows[2].Cells[0].Text);
        Assert.Equal(amount.Bounds, table.Rows[2].Cells[1].Bounds);
    }

    [Fact]
    public void Selection_ExtractsOnlyWordsWithCentersInsideAndKeepsOriginalBounds()
    {
        var result = Result(Line(0, Word("left", 0, 0), Word("right", 200, 0)),
            Line(24, Word("other", 0, 24), Word("chosen", 200, 24)));
        var selected = OcrExtractionAnalyzer.Analyze(result, new OcrRect(195, 20, 100, 24));
        Assert.Equal("chosen", selected.Text(false));
        Assert.Single(selected.Table.Rows);
        Assert.Equal(200, selected.Table.Rows[0].Cells[0].Bounds.X);
        Assert.DoesNotContain("left", OcrTableExport.Csv(selected.Table));
        Assert.Empty(OcrExtractionAnalyzer.Analyze(result, new OcrRect(800, 800, 10, 10)).Text(false));
    }

    [Fact]
    public void InvalidGeometryNeverAppearsInSelectionOrExports()
    {
        var result = Result(Line(0, Word("valid", 20, 0),
            new OcrWord("invalid", new OcrRect(double.NaN, 0, 10, 10))));
        var extraction = OcrExtractionAnalyzer.Analyze(result);
        Assert.Equal("valid", extraction.Text(false));
        Assert.DoesNotContain("invalid", extraction.Code.Text);
        Assert.Empty(OcrExtractionAnalyzer.Analyze(result,
            new OcrRect(double.NaN, 0, 30, 30)).Text(false));
    }

    [Fact]
    public void CachedTextWithoutBoxesRetainsParagraphsAndDoesNotInventSelection()
    {
        var result = OcrResult.Success("첫 줄\r\n둘째 줄\r\n\r\n새 단락", "ko-KR", [], TimeSpan.Zero);
        var extraction = OcrExtractionAnalyzer.Analyze(result);
        Assert.False(extraction.HasGeometry);
        Assert.Equal("첫 줄 둘째 줄\n\n새 단락", extraction.Text(true));
        Assert.Equal("첫 줄\n둘째 줄\n\n새 단락", extraction.Text(false));
        Assert.Empty(OcrExtractionAnalyzer.Analyze(result, new OcrRect(0, 0, 100, 100)).Text(false));
    }

    [Theory]
    [InlineData("C#", "public class Sample {\n    public void Run() {\n        Console.WriteLine(1);\n    }\n}")]
    [InlineData("JavaScript", "const value = 1;\nfunction run() {\n    console.log(value);\n}")]
    [InlineData("Python", "def run(value):\n    if value is None:\n        print(True)\n    return value")]
    [InlineData("Python", "def calculate_total(prices):\n    total = 0\n    for price in prices:\n        total += price\n    return total")]
    [InlineData("SQL", "SELECT name, amount\nFROM orders\nWHERE amount > 10\nORDER BY amount")]
    public void Code_RestoresIndentationAndEstimatesLanguageAndMonospace(string language, string source)
    {
        OcrCode code = OcrExtractionAnalyzer.Analyze(CodeResult(source)).Code;
        Assert.Equal(source, code.Text);
        Assert.Equal(language, code.Language);
        Assert.True(code.IsMonospaced);
        Assert.Equal(8, code.CharacterWidth, 5);
    }

    [Fact]
    public void Code_RegionsGroupAdjacentLinesAndKeepDistantBlocksSeparate()
    {
        var code = OcrExtractionAnalyzer.Analyze(CodeResult(
            "def alpha():\n    return True\n\n\n\ndef beta():\n    return False")).Code;
        Assert.Equal(2, code.Regions.Count);
        Assert.Equal(20, code.Regions[0].X);
        Assert.Equal(0, code.Regions[0].Y);
        Assert.Equal(40, code.Regions[0].Height);
        Assert.Equal(120, code.Regions[1].Y);
    }

    [Fact]
    public void Code_SelectionKeepsIndentationRelativeToOriginalCode()
    {
        var result = CodeResult("public void Run() {\n    Console.WriteLine(1);\n}");
        var extraction = OcrExtractionAnalyzer.Analyze(result, new OcrRect(0, 23, 800, 18));
        Assert.Equal("    Console.WriteLine(1);", extraction.Code.Text);
    }

    [Fact]
    public void Code_VariableGlyphHeightsDoNotCreateBlankLinesAtRegularPitch()
    {
        const string source = "def greet(name):\n    if name is None:\n        return \"Hello\"\n    return name";
        OcrResult fixture = CodeResult(source);
        double[] heights = [25.67, 20.67, 19.67, 18.67];
        OcrLine[] lines = fixture.Lines.Select((line, index) => Line(index * 44,
            line.Words.Select(word => word with
            {
                Bounds = new OcrRect(word.Bounds.X, index * 44, word.Bounds.Width, heights[index])
            }).ToArray())).ToArray();
        Assert.Equal(source, OcrExtractionAnalyzer.Analyze(Result(lines)).Code.Text);
    }

    [Fact]
    public void MixedCaptureSelectionUsesOriginalCodeBlockOriginAndLanguage()
    {
        var table = Result(
            Line(0, Word("Name", 20, 0), Word("Amount", 300, 0)),
            Line(24, Word("A", 20, 24), Word("20", 300, 24)));
        OcrResult original = CodeResult("public class Sample {\n    public void Run() {\n        Console.WriteLine(1);\n    }\n}", 120);
        OcrLine[] shifted = original.Lines.Select(line => Line(line.Bounds.Y,
            line.Words.Select(word => word with
            {
                Bounds = word.Bounds with { X = word.Bounds.X + 100 }
            }).ToArray())).ToArray();
        OcrResult result = Result(table.Lines.Concat(shifted).ToArray());
        var selection = OcrExtractionAnalyzer.Analyze(result, new OcrRect(100, 166, 800, 20));
        Assert.Equal("        Console.WriteLine(1);", selection.Code.Text);
        Assert.Equal("C#", selection.Code.Language);
        var tableSelection = OcrExtractionAnalyzer.Analyze(result, new OcrRect(0, 0, 400, 42));
        Assert.Null(tableSelection.Code.Language);
    }

    [Fact]
    public void Code_DoesNotInventLanguageForPlainWordsOrStronglyProportionalFont()
    {
        var result = Result(Line(0, Word("ordinary", 0, 0, 30), Word("paragraph", 40, 0, 130),
            Word("letters", 185, 0, 42), Word("wide", 240, 0, 90)));
        var code = OcrExtractionAnalyzer.Analyze(result).Code;
        Assert.Null(code.Language);
        Assert.False(code.IsMonospaced);
    }

    [Fact]
    public void Code_PreservesBlankLineAndKoreanCommentWidth()
    {
        const string source = "def run():\n    # 한국어 설명\n\n    print(True)";
        Assert.Equal(source, OcrExtractionAnalyzer.Analyze(CodeResult(source)).Code.Text);
    }

    [Fact]
    public void MixedCaptureSeparatesTableFromCode()
    {
        var table = Result(
            Line(0, Word("Name", 20, 0), Word("Amount", 300, 0)),
            Line(24, Word("A", 20, 24), Word("20", 300, 24)));
        var code = CodeResult("public class Sample {\n    public void Run() { }\n}", 120);
        var extraction = OcrExtractionAnalyzer.Analyze(Result(table.Lines.Concat(code.Lines).ToArray()));
        Assert.Equal(2, extraction.Table.Rows.Count);
        Assert.StartsWith("public class", extraction.Code.Text);
        Assert.DoesNotContain("Amount", extraction.Code.Text);
        Assert.Equal("C#", extraction.Code.Language);
    }

    [Fact]
    public void Text_ReadsEachColumnBeforeMovingRightAndJoinsOnlyWithinParagraphs()
    {
        var extraction = OcrExtractionAnalyzer.Analyze(Result(
            Line(0, Word("Left one", 20, 0, 140), Word("Right one", 320, 0, 140)),
            Line(24, Word("Left two", 20, 24, 140), Word("Right two", 320, 24, 140)),
            Line(80, Word("Left next", 20, 80, 140), Word("Right next", 320, 80, 140))));
        Assert.Equal("Left one\nLeft two\n\nLeft next\n\nRight one\nRight two\n\nRight next", extraction.Text(false));
        Assert.Equal("Left one Left two\n\nLeft next\n\nRight one Right two\n\nRight next", extraction.Text(true));
    }

    [Fact]
    public void Text_KeepsSpanningHeadingBeforeColumns()
    {
        var extraction = OcrExtractionAnalyzer.Analyze(Result(
            Line(0, Word("Spanning report heading", 20, 0, 450)),
            Line(35, Word("왼쪽 첫 줄", 20, 35, 140), Word("오른쪽 첫 줄", 320, 35, 140)),
            Line(59, Word("왼쪽 둘째 줄", 20, 59, 140), Word("오른쪽 둘째 줄", 320, 59, 140))));
        Assert.Equal("Spanning report heading\n\n왼쪽 첫 줄\n왼쪽 둘째 줄\n\n오른쪽 첫 줄\n오른쪽 둘째 줄",
            extraction.Text(false));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"https://example.invalid\")", "'=HYPERLINK(\"https://example.invalid\")")]
    [InlineData(" +cmd", "' +cmd")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("-20.5", "-20.5")]
    [InlineData("plain", "plain")]
    public void SpreadsheetCopiesKeepFormulaLikeTextLiteral(string input, string expected) =>
        Assert.Equal(expected, OcrTableExport.SpreadsheetLiteral(input));

    [Fact]
    public void ExportersPreserveDelimitersUnicodeQuotesAndLineBreaks()
    {
        var table = new OcrTable(
            [new([new("값, \"인용\"\n다음", default, default), new("A|B <tag>", default, default)])],
            [new(false), new(false)], false);
        Assert.Equal("\"값, \"\"인용\"\"\n다음\",A|B <tag>", OcrTableExport.Csv(table));
        Assert.Equal("\"값, \"\"인용\"\"\n다음\"\tA|B <tag>", OcrTableExport.Tsv(table));
        Assert.Contains("다음 | A\\|B &lt;tag&gt; |", OcrTableExport.Markdown(table));
    }

    [Fact]
    public void WorkbookIsLocalInlineTextWithHeadersAlignmentAndNoFormulasOrExternalLinks()
    {
        var table = new OcrTable(
            [new([new("제목", default, default), new("값", default, default)]),
             new([new("=1+1", default, default), new("001,200", default, default)]),
             new([new("한글😀\u0001", default, default), new("-20", default, default)])],
            [new(false), new(true)], true);
        using var data = new MemoryStream();
        OcrTableExport.WriteWorkbook(data, table);
        data.Position = 0;
        using var archive = new ZipArchive(data, ZipArchiveMode.Read);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using Stream sheet = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        var xml = XDocument.Load(sheet);
        var cells = xml.Descendants(ns + "c").ToArray();
        Assert.All(cells, cell => Assert.Equal("inlineStr", (string?)cell.Attribute("t")));
        Assert.Empty(xml.Descendants(ns + "f"));
        Assert.Equal("=1+1", cells[2].Descendants(ns + "t").Single().Value);
        Assert.Equal("001,200", cells[3].Descendants(ns + "t").Single().Value);
        Assert.Equal("한글😀", cells[4].Descendants(ns + "t").Single().Value);
        Assert.Equal("1", (string?)cells[0].Attribute("s"));
        Assert.Equal("2", (string?)cells[3].Attribute("s"));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("external", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(archive.GetEntry("[Content_Types].xml"));
        Assert.NotNull(archive.GetEntry("xl/styles.xml"));
    }

    [Fact]
    public void ImageSelectionAccountsForLetterboxingScalingAndReverseDrags()
    {
        OcrRect fit = OcrGeometry.Fit(1000, 500, 500, 500);
        Assert.Equal(new OcrRect(250, 0, 500, 500), fit);
        Assert.Equal(new OcrRect(50, 100, 350, 300),
            OcrGeometry.Selection(650, 400, 300, 100, fit, 500, 500));
        Assert.Equal(new OcrRect(0, 0, 500, 500),
            OcrGeometry.Selection(0, -10, 1000, 600, fit, 500, 500));
        Assert.Equal(default, OcrGeometry.Fit(double.NaN, 500, 500, 500));
    }

    internal static OcrWord Word(string text, double x, double y, double? width = null) =>
        new(text, new OcrRect(x, y, width ?? text.Length * 8, 16));

    internal static OcrLine Line(double y, params OcrWord[] words) =>
        new(string.Join(" ", words.Select(word => word.Text)),
            words.Where(word => OcrGeometry.IsValid(word.Bounds)).Select(word => word.Bounds)
                .Aggregate(default(OcrRect), OcrRect.Union), words);

    internal static OcrResult Result(params OcrLine[] lines) => OcrResult.Success(
        string.Join("\n", lines.Select(line => line.Text)), "en-US", lines, TimeSpan.Zero);

    internal static OcrResult CodeResult(string text, double top = 0)
    {
        var lines = new List<OcrLine>();
        int row = 0;
        foreach (string line in text.Split('\n'))
        {
            var words = new List<OcrWord>();
            int columns = 0;
            foreach (string token in line.Split(' '))
            {
                if (token.Length > 0) words.Add(Word(token, 20 + columns * 8, top + row * 24,
                    OcrGeometry.CharacterColumns(token) * 8));
                columns += token.Length == 0 ? 1 : OcrGeometry.CharacterColumns(token) + 1;
            }
            if (words.Count > 0) lines.Add(Line(top + row * 24, words.ToArray()));
            row++;
        }
        return Result(lines.ToArray());
    }
}
