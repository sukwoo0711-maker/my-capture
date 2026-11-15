using MyCapture.Core.Capture;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class ColorNamesTests
{
    [Fact]
    public void TableHasEveryCssKeywordAndOneEntryPerValue()
    {
        Assert.Equal(148, ColorNames.Keywords.Count);
        Assert.Equal(148, ColorNames.Keywords.Select(item => item.Keyword).Distinct().Count());
        Assert.Equal(139, ColorNames.All.Count);
        Assert.Equal(139, ColorNames.All.Select(color => (color.Red, color.Green, color.Blue)).Distinct().Count());
        Assert.All(ColorNames.All, color => Assert.False(string.IsNullOrWhiteSpace(color.KoreanName)));
        // Every keyword is shown under a name in the table, and the name spells the keyword.
        var names = ColorNames.All.Select(color => color.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(ColorNames.Keywords, item => Assert.Contains(item.Name, names));
        Assert.All(ColorNames.All, color => Assert.Contains(ColorNames.Keywords,
            item => item.Keyword == color.Name.ToLowerInvariant() && item.Name == color.Name));
    }

    [Theory]
    [InlineData("aliceblue", 0xF0, 0xF8, 0xFF)]
    [InlineData("blanchedalmond", 0xFF, 0xEB, 0xCD)]
    [InlineData("cadetblue", 0x5F, 0x9E, 0xA0)]
    [InlineData("chartreuse", 0x7F, 0xFF, 0x00)]
    [InlineData("crimson", 0xDC, 0x14, 0x3C)]
    [InlineData("darkgoldenrod", 0xB8, 0x86, 0x0B)]
    [InlineData("darkslategray", 0x2F, 0x4F, 0x4F)]
    [InlineData("dodgerblue", 0x1E, 0x90, 0xFF)]
    [InlineData("gainsboro", 0xDC, 0xDC, 0xDC)]
    [InlineData("gray", 0x80, 0x80, 0x80)]
    [InlineData("green", 0x00, 0x80, 0x00)]
    [InlineData("indianred", 0xCD, 0x5C, 0x5C)]
    [InlineData("lightgoldenrodyellow", 0xFA, 0xFA, 0xD2)]
    [InlineData("lime", 0x00, 0xFF, 0x00)]
    [InlineData("magenta", 0xFF, 0x00, 0xFF)]
    [InlineData("mediumvioletred", 0xC7, 0x15, 0x85)]
    [InlineData("navajowhite", 0xFF, 0xDE, 0xAD)]
    [InlineData("navy", 0x00, 0x00, 0x80)]
    [InlineData("olivedrab", 0x6B, 0x8E, 0x23)]
    [InlineData("rebeccapurple", 0x66, 0x33, 0x99)]
    [InlineData("saddlebrown", 0x8B, 0x45, 0x13)]
    [InlineData("teal", 0x00, 0x80, 0x80)]
    [InlineData("tomato", 0xFF, 0x63, 0x47)]
    [InlineData("yellowgreen", 0x9A, 0xCD, 0x32)]
    public void ValuesMatchTheCssColorSpecification(string keyword, int red, int green, int blue)
    {
        string name = ColorNames.Keywords.Single(item => item.Keyword == keyword).Name;
        NamedColor color = ColorNames.All.Single(item => item.Name == name);
        Assert.Equal((red, green, blue), (color.Red, color.Green, color.Blue));
    }

    [Theory]
    [InlineData("aqua", "Cyan")]
    [InlineData("cyan", "Cyan")]
    [InlineData("fuchsia", "Magenta")]
    [InlineData("magenta", "Magenta")]
    [InlineData("grey", "Gray")]
    [InlineData("darkgrey", "DarkGray")]
    [InlineData("darkslategrey", "DarkSlateGray")]
    [InlineData("dimgrey", "DimGray")]
    [InlineData("lightgrey", "LightGray")]
    [InlineData("lightslategrey", "LightSlateGray")]
    [InlineData("slategrey", "SlateGray")]
    public void AliasesShareOneName(string keyword, string name) =>
        Assert.Equal(name, ColorNames.Keywords.Single(item => item.Keyword == keyword).Name);

    [Fact]
    public void ExactValuesAreExactAndOthersApproximate()
    {
        ColorNameMatch magenta = ColorNames.Nearest(0xFF, 0x00, 0xFF);
        Assert.True(magenta.Exact);
        Assert.Equal("Magenta", magenta.Color.Name);
        Assert.Equal("Magenta", ColorNames.Display(magenta, korean: false));
        Assert.Equal("마젠타 (Magenta)", ColorNames.Display(magenta, korean: true));
        Assert.Equal("Cyan", ColorNames.Nearest(0x00, 0xFF, 0xFF).Color.Name);

        ColorNameMatch near = ColorNames.Nearest(0xFE, 0x01, 0xFE);
        Assert.False(near.Exact);
        Assert.Equal("Magenta", near.Color.Name);
        Assert.Equal("≈ Magenta", ColorNames.Display(near, korean: false));
        Assert.Equal("≈ 마젠타 (Magenta)", ColorNames.Display(near, korean: true));
        Assert.Equal("≈ 마젠타", ColorNames.PrimaryName(near, korean: true));
        Assert.True(near.Distance > 0);

        // The application's own dark surface is nearest a dark grey, not black by RGB distance alone.
        Assert.Equal("Black", ColorNames.Nearest(0x05, 0x05, 0x05).Color.Name);
        Assert.Equal("Crimson", ColorNames.Nearest(0xDB, 0x14, 0x3D).Color.Name);
        Assert.Equal("DodgerBlue", ColorNames.Nearest(0x1F, 0x8F, 0xFE).Color.Name);
        Assert.Equal("Black", ColorNames.Nearest(0, 0, 0).Color.Name);
        Assert.True(ColorNames.Nearest(0, 0, 0).Exact);
        Assert.Equal("White", ColorNames.Nearest(0xFF, 0xFF, 0xFF).Color.Name);
    }

    [Fact]
    public void EveryNamedValueFindsItself()
    {
        foreach (NamedColor color in ColorNames.All)
        {
            ColorNameMatch match = ColorNames.Nearest(color.Red, color.Green, color.Blue);
            Assert.True(match.Exact);
            Assert.Same(color, match.Color);
        }
    }

    // Reference pairs from Sharma, Wu and Dalal, "The CIEDE2000 Color-Difference Formula:
    // Implementation Notes, Supplementary Test Data, and Mathematical Observations" (2005).
    [Theory]
    [InlineData(50.0000, 2.6772, -79.7751, 50.0000, 0.0000, -82.7485, 2.0425)]
    [InlineData(50.0000, 3.1571, -77.2803, 50.0000, 0.0000, -82.7485, 2.8615)]
    [InlineData(50.0000, 2.8361, -74.0200, 50.0000, 0.0000, -82.7485, 3.4412)]
    [InlineData(50.0000, 0.0000, 0.0000, 50.0000, -1.0000, 2.0000, 2.3669)]
    [InlineData(50.0000, 2.5000, 0.0000, 73.0000, 25.0000, -18.0000, 27.1492)]
    [InlineData(60.2574, -34.0099, 36.2677, 60.4626, -34.1751, 39.4387, 1.2644)]
    [InlineData(2.0776, 0.0795, -1.1350, 0.9033, -0.0636, -0.5514, 0.9082)]
    public void DeltaE2000MatchesPublishedPairs(double l1, double a1, double b1, double l2, double a2, double b2, double expected)
    {
        var first = new ColorNames.Lab(l1, a1, b1);
        var second = new ColorNames.Lab(l2, a2, b2);
        Assert.Equal(expected, ColorNames.DeltaE2000(first, second), 4);
        Assert.Equal(expected, ColorNames.DeltaE2000(second, first), 4);
    }

    [Fact]
    public void SrgbToLabMatchesKnownWhiteAndPrimaries()
    {
        ColorNames.Lab white = ColorNames.ToLab(255, 255, 255);
        Assert.Equal(100, white.L, 2);
        Assert.Equal(0, white.A, 2);
        Assert.Equal(0, white.B, 2);
        ColorNames.Lab red = ColorNames.ToLab(255, 0, 0);
        Assert.Equal(53.24, red.L, 1);
        Assert.Equal(80.09, red.A, 1);
        Assert.Equal(67.20, red.B, 1);
    }
}
