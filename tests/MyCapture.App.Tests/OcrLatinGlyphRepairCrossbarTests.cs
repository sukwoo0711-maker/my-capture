using System.Windows.Media.Imaging;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrLatinGlyphRepairCrossbarTests
{
    [Theory]
    [InlineData("Segoe UI", 16)]
    [InlineData("Segoe UI", 24)]
    [InlineData("Arial", 24)]
    [InlineData("Consolas", 16)]
    [InlineData("Consolas", 24)]
    public void InteriorCrossbarAndXHeightRoundGlyphRepairTheNumericReading(string font, int size) => StaTestHost.Run(() =>
    {
        const string drawn = "We have to read the page";
        Assert.Equal(drawn, RepairFont(drawn, "We have 10 read the page", font, size));
    });

    public static TheoryData<string, int> Fonts() => OcrLatinGlyphRepairTests.Fonts();

    [Theory]
    [MemberData(nameof(Fonts))]
    [InlineData("Segoe UI", 24)]
    [InlineData("Arial", 24)]
    [InlineData("Consolas", 24)]
    public void DrawnDigitsAndNumericIdentifiersKeepTheirReading(string font, int size) => StaTestHost.Run(() =>
    {
        const string text = "We read 10 0x10 v10 10% 100 101 more";
        Assert.Equal(text, RepairFont(text, text, font, size));
        Assert.Equal("We have 10 more", RepairFont("We have t0 more", "We have 10 more", font, size));
        Assert.Equal("We have 10 more", RepairFont("We have 1o more", "We have 10 more", font, size));
    });

    [Theory]
    [InlineData("Segoe UI", 16)]
    [InlineData("Arial", 24)]
    public void CrossbarRepairRequiresLineReferencesAndAnUnadornedNumericAtom(string font, int size) => StaTestHost.Run(() =>
    {
        Assert.Equal("10", RepairFont("to", "10", font, size));
        Assert.Equal("MMMM 10", RepairFont("MMMM to", "MMMM 10", font, size));
        Assert.Equal("We have 10% more", RepairFont("We have to% more", "We have 10% more", font, size));
        Assert.Equal("We have v10 more", RepairFont("We have vto more", "We have v10 more", font, size));
        Assert.Equal("We have 0x10 more", RepairFont("We have 0xto more", "We have 0x10 more", font, size));
        Assert.Equal("We have 10 more", RepairFont("We have to more", "We have 10 more", font, 6));
    });

    [Theory]
    [InlineData("10", "to")]
    [InlineData("100", "too")]
    public void IndependentPixelCrossbarRepairsTheWholeWordAndPreservesGeometry(string read, string expected)
    {
        PixelScene scene = Pixels(read);
        OcrLatinGlyphRepair.Glyph[] glyphs = TargetGlyphs(scene);
        Assert.Equal(read.Length, glyphs.Length);
        Assert.True(glyphs[0].HasCrossbar);
        Assert.False(glyphs[0].HasGap);
        Assert.Equal(9d, glyphs[0].Height);
        Assert.All(glyphs.Skip(1), glyph => Assert.Equal(7d, glyph.Height));

        OcrResult repaired = OcrLatinGlyphRepair.Repair(scene.Reading, scene.Image);
        Assert.Equal("aa bb " + expected, repaired.Text);
        Assert.Equal(scene.Reading.Lines[0].Bounds, repaired.Lines[0].Bounds);
        Assert.Equal(scene.Reading.Lines[0].Words.Select(word => word.Bounds), repaired.Lines[0].Words.Select(word => word.Bounds));
    }

    [Fact]
    public void ConnectedPixelGlyphsKeepTheirReadingEvenWithValidLineReferences()
    {
        PixelScene separated = Pixels("10");
        Assert.Equal("aa bb to", OcrLatinGlyphRepair.Repair(separated.Reading, separated.Image).Text);

        // The sole changed pixels bridge the target's column runs. No font or host DPI chooses this premise.
        PixelScene connected = Pixels("10", connected: true);
        Assert.Single(TargetGlyphs(connected));
        Assert.Same(connected.Reading, OcrLatinGlyphRepair.Repair(connected.Reading, connected.Image));
    }

    [Theory]
    [InlineData("x-height")]
    [InlineData("tall")]
    public void IndependentPixelCrossbarStillRequiresBothLineReferences(string onlyReference)
    {
        PixelScene scene = Pixels("10", onlyReference: onlyReference);
        OcrLatinGlyphRepair.Glyph[] glyphs = TargetGlyphs(scene);
        Assert.Equal(2, glyphs.Length);
        Assert.True(glyphs[0].HasCrossbar);
        Assert.Equal("to", OcrLatinGlyphRepair.RepairWord("10", glyphs, 7, 10));
        Assert.Same(scene.Reading, OcrLatinGlyphRepair.Repair(scene.Reading, scene.Image));
    }

    [Theory]
    [InlineData("flag")]
    [InlineData("baseline-serif")]
    [InlineData("one-sided")]
    public void AFlagSerifOrOneSidedBarDoesNotSupplyInteriorCrossbarEvidence(string shape)
    {
        string[] stem = Enumerable.Repeat("..#..", 9).ToArray();
        if (shape == "flag") { stem[0] = "###.."; stem[1] = ".##.."; }
        else if (shape == "baseline-serif") stem[8] = "#####";
        else stem[2] = "###..";
        PixelScene scene = Pixels("10", stem: stem);
        OcrLatinGlyphRepair.Glyph[] glyphs = TargetGlyphs(scene);
        Assert.Equal(2, glyphs.Length);
        Assert.False(glyphs[0].HasCrossbar);
        Assert.False(glyphs[0].HasGap);
        Assert.Same(scene.Reading, OcrLatinGlyphRepair.Repair(scene.Reading, scene.Image));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(129)]
    public void PixelBodiesOutsideTheShapeEvidenceBoundsDoNotSupplyACrossbar(int height)
    {
        string[] stem = Enumerable.Repeat("..#..", height).ToArray();
        stem[2] = "#####";
        PixelScene scene = Pixels("10", stem: stem);
        OcrLatinGlyphRepair.Glyph[] glyphs = TargetGlyphs(scene);
        Assert.Equal(2, glyphs.Length);
        Assert.Equal((double)height, glyphs[0].Height);
        Assert.False(glyphs[0].HasCrossbar);
        Assert.Same(scene.Reading, OcrLatinGlyphRepair.Repair(scene.Reading, scene.Image));
    }

    [Fact]
    public void CrossbarRequiresAnUndottedAscenderAndAnAdjacentRoundGlyphAtXHeight()
    {
        OcrLatinGlyphRepair.Glyph crossbar = new(9, false, true), round = new(7, false), tall = new(10, false);
        Assert.Equal("to", OcrLatinGlyphRepair.RepairWord("10", [crossbar, round], 7, 10));
        Assert.Equal("10", OcrLatinGlyphRepair.RepairWord("10", [new(7.99, false, true), round], 7, 10));
        Assert.Equal("10", OcrLatinGlyphRepair.RepairWord("10", [new(8, false, true), round], 7, 10));
        Assert.Equal("to", OcrLatinGlyphRepair.RepairWord("10", [new(8.01, false, true), round], 7, 10));
        Assert.Equal("10", OcrLatinGlyphRepair.RepairWord("10", [new(9, false), round], 7, 10));
        Assert.Equal("10", OcrLatinGlyphRepair.RepairWord("10", [crossbar, tall], 7, 10));
        Assert.Equal("10", OcrLatinGlyphRepair.RepairWord("10", [crossbar, new(3, false)], 7, 10));
        Assert.Equal("10", OcrLatinGlyphRepair.RepairWord("10", [new(9, true, true), round], 7, 10));
        Assert.Equal("10", OcrLatinGlyphRepair.RepairWord("10", [new(7, false, true), round], 7, 10));
        Assert.Equal("10", OcrLatinGlyphRepair.RepairWord("10", [new(15, false, true), round], 7, 10));
        Assert.Equal("10%", OcrLatinGlyphRepair.RepairWord("10%", [crossbar, round, tall], 7, 10));
        Assert.Equal("v10", OcrLatinGlyphRepair.RepairWord("v10", [round, crossbar, round], 7, 10));
        Assert.Equal("0x10", OcrLatinGlyphRepair.RepairWord("0x10", [tall, round, crossbar, round], 7, 10));
        Assert.Equal("10", OcrLatinGlyphRepair.RepairWord("10", [crossbar], 7, 10));
    }

    private static string RepairFont(string drawn, string read, string font, int size)
    {
        (BitmapSource bitmap, OcrResult reading) = OcrLatinGlyphRepairTests.Scene(drawn, read, font, size);
        OcrResult repaired = OcrLatinGlyphRepair.Repair(reading, bitmap);
        Assert.Equal(reading.Lines[0].Words.Select(word => word.Bounds), repaired.Lines[0].Words.Select(word => word.Bounds));
        return repaired.Text;
    }

    private sealed record PixelScene(OcrGrayImage Image, OcrResult Reading, OcrWord Target);

    private static OcrLatinGlyphRepair.Glyph[] TargetGlyphs(PixelScene scene) =>
        Assert.IsType<OcrLatinGlyphRepair.Glyph[]>(OcrLatinGlyphRepair.Segment(scene.Image, scene.Target, scene.Reading.Lines[0].Bounds));

    private static PixelScene Pixels(string read, bool connected = false, string? onlyReference = null, string[]? stem = null)
    {
        string[] stemPixels = stem ?? ["..#..", "..#..", "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."];
        string[] round = [".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."];
        string[] low = Enumerable.Repeat("###", 7).ToArray(), high = Enumerable.Repeat("###", 10).ToArray();
        const int width = 100, gap = 3;
        int height = Math.Max(10, stemPixels.Length) + 6, baseline = height - 3, x = 3;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        var words = new List<OcrWord>();
        if (onlyReference == "x-height") AddWord("aaaa", [low, low, low, low]);
        else if (onlyReference == "tall") AddWord("bbbb", [high, high, high, high]);
        else { AddWord("aa", [low, low]); AddWord("bb", [high, high]); }
        int targetX = x;
        string[][] targetMasks = read.Select(value => value == '1' ? stemPixels : round).ToArray();
        OcrWord target = AddWord(read, targetMasks);
        if (connected)
            for (int bridge = targetX + 2; bridge <= targetX + stemPixels[0].Length + gap + 1; bridge++)
                pixels[(baseline - 1) * width + bridge] = 0;
        string text = string.Join(' ', words.Select(word => word.Text));
        OcrRect bounds = words.Select(word => word.Bounds).Aggregate(OcrRect.Union);
        var line = new OcrLine(text, bounds, words);
        return new PixelScene(new OcrGrayImage(width, height, pixels), OcrResult.Success(text, "en", [line], TimeSpan.Zero), target);

        OcrWord AddWord(string text, string[][] masks)
        {
            int start = x, tallest = masks.Max(mask => mask.Length);
            foreach (string[] mask in masks)
            {
                for (int row = 0; row < mask.Length; row++)
                    for (int column = 0; column < mask[row].Length; column++)
                        if (mask[row][column] == '#') pixels[(baseline - mask.Length + row) * width + x + column] = 0;
                x += mask[0].Length + gap;
            }
            var word = new OcrWord(text, new OcrRect(start, baseline - tallest, x - start - gap, tallest));
            words.Add(word);
            x += 3;
            return word;
        }
    }
}
