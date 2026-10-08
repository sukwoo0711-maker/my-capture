using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Ocr;
using Xunit;
using static MyCapture.App.Tests.Ocr410ExtractionTests;

namespace MyCapture.App.Tests;

public sealed class OcrReadingOrientationTests
{
    private const int ReadingWidth = 800;
    private const int ReadingHeight = 400;

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task RecognizedLinesAndWordsKeepReadingOrderAndOriginalBoxes(int rotation)
    {
        OcrResult fixture = CodeResult("alpha beta\ngamma delta");
        OcrResult result = await Recognize(fixture, rotation);
        OcrExtraction extraction = OcrExtractionAnalyzer.Analyze(result);

        Assert.Equal("alpha beta\ngamma delta", extraction.Text(false));
        Assert.Equal("alpha beta gamma delta", extraction.Text(true));
        Assert.Equal("alpha beta\ngamma delta", extraction.Code.Text);
        Assert.Equal(fixture.Text, result.Text);
        Assert.Equal(fixture.Lines.Select(line => line.Text), result.Lines.Select(line => line.Text));
        for (int line = 0; line < fixture.Lines.Count; line++)
        {
            Assert.Equal(Original(fixture.Lines[line].Bounds, rotation), result.Lines[line].Bounds);
            Assert.Equal(fixture.Lines[line].Words.Select(word => word.Text),
                result.Lines[line].Words.Select(word => word.Text));
            for (int word = 0; word < fixture.Lines[line].Words.Count; word++)
                Assert.Equal(Original(fixture.Lines[line].Words[word].Bounds, rotation),
                    result.Lines[line].Words[word].Bounds);
        }
        Assert.Equal(Original(OcrRect.Union(fixture.Lines[0].Bounds, fixture.Lines[1].Bounds), rotation),
            Assert.Single(extraction.Paragraphs).Bounds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task TableExportsKeepRowsColumnsAndOriginalOverlayGeometry(int rotation)
    {
        OcrResult fixture = Result(
            Line(40, Word("Part", 20, 40), Word("Qty", 232, 40), Word("Amount", 400, 40)),
            Line(64, Word("Keyboard", 20, 64), Word("1280", 416, 64)),
            Line(88, Word("USB", 20, 88), Word("cable", 52, 88),
                Word("2", 248, 88), Word("99.50", 408, 88)));
        OcrResult result = await Recognize(fixture, rotation);
        OcrTable table = OcrExtractionAnalyzer.Analyze(result).Table;

        Assert.Equal("Part,Qty,Amount\r\nKeyboard,,1280\r\nUSB cable,2,99.50", OcrTableExport.Csv(table));
        Assert.Equal("Part\tQty\tAmount\r\nKeyboard\t\t1280\r\nUSB cable\t2\t99.50", OcrTableExport.Tsv(table));
        Assert.Contains("| Keyboard |  | 1280 |", OcrTableExport.Markdown(table));
        Assert.Contains("| USB cable | 2 | 99.50 |", OcrTableExport.Markdown(table));
        Assert.True(table.HasHeader);
        Assert.True(table.Columns[2].IsNumeric);
        Assert.Equal(default, table.Rows[1].Cells[1].Bounds);
        Assert.Equal(Original(fixture.Lines[1].Words[0].Bounds, rotation), table.Rows[1].Cells[0].Bounds);

        OcrTable upright = OcrExtractionAnalyzer.Analyze(fixture).Table;
        for (int row = 0; row < table.Rows.Count; row++)
        for (int column = 0; column < table.Columns.Count; column++)
        {
            Assert.Equal(Original(upright.Rows[row].Cells[column].Bounds, rotation),
                table.Rows[row].Cells[column].Bounds);
            Assert.Equal(Original(upright.Rows[row].Cells[column].Region, rotation),
                table.Rows[row].Cells[column].Region);
        }
        Assert.Equal(Original(upright.Bounds, rotation), table.Bounds);
        AssertWorkbook(table, ["Part|Qty|Amount", "Keyboard||1280", "USB cable|2|99.50"]);

        OcrRect selection = Original(new OcrRect(0, 60, ReadingWidth, 50), rotation);
        OcrTable selected = OcrExtractionAnalyzer.Analyze(result, selection).Table;
        Assert.Equal("Keyboard,,1280\r\nUSB cable,2,99.50", OcrTableExport.Csv(selected));
        Assert.Equal(table.Rows[1].Cells[0].Bounds, selected.Rows[0].Cells[0].Bounds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task MixedCaptureAndSelectionKeepCodeIndentationLanguageAndSourceRegions(int rotation)
    {
        const string code = "public void Run() {\n    Console.WriteLine(1);\n}";
        OcrResult codeFixture = CodeResult(code, 120);
        OcrResult fixture = Result(new[]
        {
            Line(0, Word("Name", 20, 0), Word("Amount", 300, 0)),
            Line(24, Word("A", 20, 24), Word("20", 300, 24)),
        }.Concat(codeFixture.Lines).ToArray());
        OcrResult result = await Recognize(fixture, rotation);
        OcrRect[] originalBoxes = result.Lines.SelectMany(line => line.Words).Select(word => word.Bounds).ToArray();
        OcrExtraction extraction = OcrExtractionAnalyzer.Analyze(result);

        Assert.Equal(2, extraction.Table.Rows.Count);
        Assert.Equal(code, extraction.Code.Text);
        Assert.Equal("C#", extraction.Code.Language);
        Assert.Equal(8, extraction.Code.CharacterWidth, 5);
        Assert.Equal(Original(codeFixture.Lines.Select(line => line.Bounds)
            .Aggregate(default(OcrRect), OcrRect.Union), rotation), Assert.Single(extraction.Code.Regions));

        OcrRect selection = Original(new OcrRect(0, 143, ReadingWidth, 18), rotation);
        OcrExtraction selected = OcrExtractionAnalyzer.Analyze(result, selection);
        Assert.Equal("    Console.WriteLine(1);", selected.Code.Text);
        Assert.Equal("C#", selected.Code.Language);
        Assert.Equal("Console.WriteLine(1);", selected.Text(false));
        Assert.Equal(Original(codeFixture.Lines[1].Bounds, rotation), Assert.Single(selected.Code.Regions));
        Assert.Equal(Original(codeFixture.Lines[1].Bounds, rotation), Assert.Single(selected.Paragraphs).Bounds);
        Assert.Equal(originalBoxes, result.Lines.SelectMany(line => line.Words).Select(word => word.Bounds));
        Assert.Empty(OcrExtractionAnalyzer.Analyze(result, new OcrRect(900, 900, 10, 10)).Text(false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task TextKeepsColumnAndParagraphReadingOrder(int rotation)
    {
        OcrResult fixture = Result(
            Line(0, Word("Left one", 20, 0, 140), Word("Right one", 320, 0, 140)),
            Line(24, Word("Left two", 20, 24, 140), Word("Right two", 320, 24, 140)),
            Line(80, Word("Left next", 20, 80, 140), Word("Right next", 320, 80, 140)));
        OcrExtraction extraction = OcrExtractionAnalyzer.Analyze(await Recognize(fixture, rotation));
        Assert.Equal("Left one\nLeft two\n\nLeft next\n\nRight one\nRight two\n\nRight next", extraction.Text(false));
        Assert.Equal("Left one Left two\n\nLeft next\n\nRight one Right two\n\nRight next", extraction.Text(true));
        OcrExtraction upright = OcrExtractionAnalyzer.Analyze(fixture);
        Assert.Equal(upright.Paragraphs.Select(paragraph => Original(paragraph.Bounds, rotation)),
            extraction.Paragraphs.Select(paragraph => paragraph.Bounds));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task LineBoundsWithoutWordBoxesKeepReadingOrderAndSelection(int rotation)
    {
        OcrResult recognized = await Recognize(CodeResult("alpha beta\ngamma delta"), rotation);
        OcrLine[] lines = recognized.Lines.Select(line => line with { Words = [] }).ToArray();
        OcrResult result = OcrResult.Success(recognized.Text, "en-US", lines, TimeSpan.Zero,
            recognized.ReadingOrientation);
        OcrExtraction extraction = OcrExtractionAnalyzer.Analyze(result);
        Assert.Equal("alpha beta\ngamma delta", extraction.Text(false));
        OcrRect selection = Original(new OcrRect(0, 23, ReadingWidth, 18), rotation);
        OcrExtraction selected = OcrExtractionAnalyzer.Analyze(result, selection);
        Assert.Equal("gamma delta", selected.Text(false));
        Assert.Equal(lines[1].Bounds, Assert.Single(selected.Paragraphs).Bounds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task SparseTableEdgesRemainSelectableInTheOriginalImage(int rotation)
    {
        OcrResult fixture = Result(
            Line(0, Word("Washer", 20, 0)),
            Line(24, Word("Name", 20, 24), Word("Qty", 232, 24)),
            Line(48, Word("Bolt", 20, 48), Word("4", 248, 48)),
            Line(72, Word("Nut", 20, 72)),
            Line(160, Word("Outside", 380, 160)));
        OcrResult result = await Recognize(fixture, rotation);
        OcrRect selection = Original(new OcrRect(0, 0, 330, 90), rotation);
        OcrTable table = OcrExtractionAnalyzer.Analyze(result, selection).Table;
        Assert.Equal("Washer,\r\nName,Qty\r\nBolt,4\r\nNut,", OcrTableExport.Csv(table));
        Assert.Equal(Original(fixture.Lines[3].Words[0].Bounds, rotation), table.Rows[3].Cells[0].Bounds);
        Assert.Equal(default, table.Rows[3].Cells[1].Bounds);
        Assert.True(OcrGeometry.IsValid(table.Rows[3].Cells[1].Region));
    }

    [Theory]
    [InlineData(0, 800, 10, 0, 16)]
    [InlineData(90, 10, 0, 16, 0)]
    [InlineData(180, 0, 374, 0, 16)]
    [InlineData(270, 374, 800, 16, 0)]
    public async Task ClippedWordBoxesStillUseOriginalImageAxes(
        int rotation, double x, double y, double width, double height)
    {
        OcrResult fixture = Result(Line(10, Word("visible", 20, 10), Word("clipped", 820, 10, 80)));
        OcrResult result = await Recognize(fixture, rotation);
        OcrWord clipped = Assert.Single(result.Lines.SelectMany(line => line.Words),
            word => word.Text == "clipped");
        Assert.Equal(new OcrRect(x, y, width, height), clipped.Bounds);
        Assert.Equal("visible", OcrExtractionAnalyzer.Analyze(result).Text(false));
    }

    private static async Task<OcrResult> Recognize(OcrResult fixture, int rotation)
    {
        int width = rotation is 90 or 270 ? ReadingHeight : ReadingWidth;
        int height = rotation is 90 or 270 ? ReadingWidth : ReadingHeight;
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, new byte[width * height * 4], width * 4);
        bitmap.Freeze();
        var recognizer = new OrientationRecognizer(fixture, rotation);
        var service = new WindowsOcrService(recognizer, _ => bitmap, _ => bitmap, NullLogger.Instance);
        OcrResult result = await service.RecognizeAsync(OcrRequest.FromBitmap(bitmap, 1, ["en-US"],
            searchRotatedOrientations: true));
        Assert.Equal(OcrStatus.Success, result.Status);
        Assert.Equal(rotation == 0 ? 1 : 4, recognizer.Calls);
        return result;
    }

    // Independent expected transform from an 800 x 400 reading image into its source image.
    private static OcrRect Original(OcrRect bounds, int rotation)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return bounds;
        return rotation switch
        {
            0 => bounds,
            90 => new(bounds.Y, ReadingWidth - bounds.Right, bounds.Height, bounds.Width),
            180 => new(ReadingWidth - bounds.Right, ReadingHeight - bounds.Bottom, bounds.Width, bounds.Height),
            270 => new(ReadingHeight - bounds.Bottom, bounds.X, bounds.Height, bounds.Width),
            _ => throw new ArgumentOutOfRangeException(nameof(rotation)),
        };
    }

    private static void AssertWorkbook(OcrTable table, string[] expectedRows)
    {
        using var data = new MemoryStream();
        OcrTableExport.WriteWorkbook(data, table);
        data.Position = 0;
        using var archive = new ZipArchive(data, ZipArchiveMode.Read);
        using Stream sheet = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XDocument xml = XDocument.Load(sheet);
        Assert.Equal(expectedRows, xml.Descendants(ns + "row").Select(row =>
            string.Join("|", row.Elements(ns + "c").Select(cell =>
                string.Concat(cell.Descendants(ns + "t").Select(text => text.Value))))));
    }

    private sealed class OrientationRecognizer(OcrResult fixture, int rotation) : IOcrRecognizer
    {
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["en-US"];
        public int MaxImageDimension => 4000;
        public int Calls { get; private set; }

        public Task<RecognizedText?> RecognizeAsync(BitmapSource preparedBitmap, string? languageTag,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Calls != rotation / 90 + 1)
                return Task.FromResult<RecognizedText?>(new("en-US", []));
            double scale = preparedBitmap.PixelWidth / (double)ReadingWidth;
            var lines = fixture.Lines.Select(line => new RecognizedLine(line.Text,
                line.Words.Select(word => new RecognizedWord(word.Text, new OcrRect(
                    word.Bounds.X * scale, word.Bounds.Y * scale,
                    word.Bounds.Width * scale, word.Bounds.Height * scale))).ToArray())).ToArray();
            return Task.FromResult<RecognizedText?>(new("en-US", lines));
        }
    }
}