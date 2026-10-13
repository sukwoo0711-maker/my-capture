using System.Globalization;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class VideoTimecodeTests
{
    [Theory]
    [InlineData(0, "0:00.000")]
    [InlineData(300, "0:00.300")]
    [InlineData(66.6667, "0:00.067")]
    [InlineData(1_600, "0:01.600")]
    [InlineData(59_999.6, "1:00.000")]
    [InlineData(61_234, "1:01.234")]
    [InlineData(599_999, "9:59.999")]
    [InlineData(3_599_999.4, "59:59.999")]
    [InlineData(3_600_000, "1:00:00.000")]
    [InlineData(3_723_004, "1:02:03.004")]
    [InlineData(-5, "0:00.000")]
    [InlineData(double.NaN, "0:00.000")]
    [InlineData(double.PositiveInfinity, "0:00.000")]
    public void FormatIsMinutesSecondsAndMilliseconds_WithHoursOnlyFromAnHour(double milliseconds, string expected) =>
        Assert.Equal(expected, VideoTimecode.Format(milliseconds));

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(500, "0:00.5")]
    [InlineData(1_000, "0:01")]
    [InlineData(2_500, "0:02.5")]
    [InlineData(60_000, "1:00")]
    [InlineData(3_600_000, "1:00:00")]
    public void TickLabelsKeepTheFormatWithoutTrailingZeros(double milliseconds, string expected) =>
        Assert.Equal(expected, VideoTimecode.FormatTick(milliseconds));

    [Theory]
    // The current format.
    [InlineData("0:00.300", 300)]
    [InlineData("1:02.345", 62_345)]
    [InlineData("1:02:03.004", 3_723_004)]
    // Every format the editor showed before.
    [InlineData("00:00.300", 300)]
    [InlineData("00:03.0", 3_000)]
    [InlineData("00:00:02.500", 2_500)]
    [InlineData("01:02:03.004", 3_723_004)]
    // Shorter or longer fractions, a comma, no fraction, plain seconds and spaces around.
    [InlineData("0:01.5", 1_500)]
    [InlineData("0:01.25", 1_250)]
    [InlineData("0:01.2345", 1_234.5)]
    [InlineData("0:01,5", 1_500)]
    [InlineData("1:05", 65_000)]
    [InlineData("75:00", 4_500_000)]
    [InlineData("2.5", 2_500)]
    [InlineData("12", 12_000)]
    [InlineData("  0:02.000 ", 2_000)]
    public void ParseReadsTheCurrentAndEveryEarlierFormat(string text, double expected)
    {
        Assert.True(VideoTimecode.TryParse(text, out double milliseconds));
        Assert.Equal(expected, milliseconds, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1:60.000")]
    [InlineData("1:80:00")]
    [InlineData("00:80:00")]
    [InlineData("1:2:3:4")]
    [InlineData("-0:01.000")]
    [InlineData("0:-1")]
    [InlineData("0:01.")]
    [InlineData("0:01.2.3")]
    [InlineData(":01")]
    [InlineData("0:")]
    [InlineData("a:01")]
    [InlineData("0: 01")]
    public void ParseRejectsWhatIsNotATime(string? text) => Assert.False(VideoTimecode.TryParse(text, out _));

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    [InlineData(1_234)]
    [InlineData(59_999)]
    [InlineData(3_599_999)]
    [InlineData(3_600_001)]
    [InlineData(90_000_000)]
    public void WholeMillisecondsRoundTrip(double milliseconds)
    {
        Assert.True(VideoTimecode.TryParse(VideoTimecode.Format(milliseconds), out double parsed));
        Assert.Equal(milliseconds, parsed);
    }

    [Fact]
    public void FormatIgnoresTheCurrentCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1:02.345", VideoTimecode.Format(62_345));
            Assert.True(VideoTimecode.TryParse("1:02.345", out double parsed));
            Assert.Equal(62_345, parsed, 6);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
