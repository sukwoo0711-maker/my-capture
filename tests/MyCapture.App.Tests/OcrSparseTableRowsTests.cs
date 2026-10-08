using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using MyCapture.Ocr;
using Xunit;
using static MyCapture.App.Tests.Ocr410ExtractionTests;

namespace MyCapture.App.Tests;

public sealed class OcrSparseTableRowsTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void SparseBoundaryRowsSurviveInferenceSelectionAndEveryExport(
        bool leading, bool trailing, bool explicitSelection)
    {
        var lines = new List<OcrLine> { Line(0, Word("Outside", 380, 0)) };
        var expected = new List<string>();
        if (leading)
        {
            lines.Add(Line(48, Word("Washer", 20, 48)));
            expected.Add("Washer,");
        }
        lines.Add(Line(72, Word("Name", 20, 72), Word("Qty", 232, 72)));
        lines.Add(Line(96, Word("Bolt", 20, 96), Word("4", 248, 96)));
        expected.Add("Name,Qty");
        expected.Add("Bolt,4");
        if (trailing)
        {
            lines.Add(Line(120, Word("Nut", 20, 120)));
            expected.Add("Nut,");
        }
        lines.Add(Line(220, Word("After", 380, 220)));
        OcrRect? selection = explicitSelection ? new OcrRect(0, 40, 330, 100) : null;
        OcrTable table = OcrExtractionAnalyzer.Analyze(Result(lines.ToArray()), selection).Table;

        Assert.Equal(expected.Count, table.Rows.Count);
        Assert.Equal(2, table.Columns.Count);
        Assert.Equal(string.Join("\r\n", expected), OcrTableExport.Csv(table));
        Assert.Equal(string.Join("\r\n", expected.Select(row => row.Replace(',', '\t'))),
            OcrTableExport.Tsv(table));
        if (leading)
        {
            Assert.Equal("Washer", table.Rows[0].Cells[0].Text);
            Assert.Equal("", table.Rows[0].Cells[1].Text);
            Assert.Equal(default, table.Rows[0].Cells[1].Bounds);
            Assert.True(OcrGeometry.IsValid(table.Rows[0].Cells[1].Region));
            Assert.Contains("| Washer |  |", OcrTableExport.Markdown(table));
        }
        if (trailing)
        {
            Assert.Equal("Nut", table.Rows[^1].Cells[0].Text);
            Assert.Equal("", table.Rows[^1].Cells[1].Text);
            Assert.Equal(new OcrRect(20, 120, 24, 16), table.Rows[^1].Cells[0].Bounds);
            Assert.True(OcrGeometry.IsValid(table.Rows[^1].Cells[1].Region));
            Assert.Contains("| Nut |  |", OcrTableExport.Markdown(table));
        }
        AssertWorkbookRows(table, expected.Select(row => row.Split(',')).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsecutiveSparseEdgesCanAlignToMiddleOrRightColumns(bool explicitSelection)
    {
        OcrResult result = Result(
            Line(0, Word("5", 198, 0)),
            Line(24, Word("7", 320, 24)),
            Line(48, Word("Name", 20, 48), Word("Count", 182, 48), Word("Cost", 300, 48)),
            Line(72, Word("Bolt", 20, 72), Word("12", 194, 72), Word("100", 304, 72)),
            Line(96, Word("Nut", 20, 96)),
            Line(120, Word("8", 320, 120)));
        OcrRect? selection = explicitSelection ? new OcrRect(0, 0, 350, 140) : null;
        OcrTable table = OcrExtractionAnalyzer.Analyze(result, selection).Table;

        Assert.Equal(6, table.Rows.Count);
        Assert.Equal(3, table.Columns.Count);
        Assert.Equal(",5,\r\n,,7\r\nName,Count,Cost\r\nBolt,12,100\r\nNut,,\r\n,,8",
            OcrTableExport.Csv(table));
        Assert.Equal("5", table.Rows[0].Cells[1].Text);
        Assert.Equal("8", table.Rows[^1].Cells[2].Text);
        Assert.Equal("", table.Rows[^1].Cells[0].Text);
    }

    [Theory]
    [InlineData(80, 40, 24, false)]
    [InlineData(20, 240, 24, false)]
    [InlineData(20, 32, 40, false)]
    [InlineData(80, 40, 24, true)]
    [InlineData(20, 240, 24, true)]
    [InlineData(20, 32, 40, true)]
    public void NearbyCaptionsMustMatchColumnAlignmentSpanAndRowPitch(
        double x, double width, double pitch, bool explicitSelection)
    {
        OcrResult result = Result(
            Line(80 - pitch, Word("Before", x, 80 - pitch, width)),
            Line(80, Word("Name", 20, 80), Word("Qty", 232, 80)),
            Line(104, Word("Bolt", 20, 104), Word("4", 248, 104)),
            Line(104 + pitch, Word("After", x, 104 + pitch, width)));
        OcrRect? selection = explicitSelection ? new OcrRect(0, 0, 400, 190) : null;
        OcrTable table = OcrExtractionAnalyzer.Analyze(result, selection).Table;

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("Name,Qty\r\nBolt,4", OcrTableExport.Csv(table));
    }

    private static void AssertWorkbookRows(OcrTable table, string[][] expectedRows)
    {
        using var data = new MemoryStream();
        OcrTableExport.WriteWorkbook(data, table);
        data.Position = 0;
        using var archive = new ZipArchive(data, ZipArchiveMode.Read);
        using Stream sheet = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XElement[] rows = XDocument.Load(sheet).Descendants(ns + "row").ToArray();
        Assert.Equal(expectedRows.Length, rows.Length);
        for (int index = 0; index < rows.Length; index++)
            Assert.Equal(expectedRows[index], rows[index].Elements(ns + "c").Select(cell =>
                string.Concat(cell.Descendants(ns + "t").Select(text => text.Value))));
    }
}