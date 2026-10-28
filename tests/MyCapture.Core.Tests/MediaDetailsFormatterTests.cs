using MyCapture.Core.Capture;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class MediaDetailsFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(2516582, "2.4 MB")]
    [InlineData(1073741824, "1 GB")]
    public void FileSizeUsesReadableUnits(long bytes, string expected) =>
        Assert.Equal(expected, MediaDetailsFormatter.FileSize(bytes));

    [Fact]
    public void ResolutionUsesMultiplicationSignAndPixels()
    {
        Assert.Equal("1920 × 1080 px", MediaDetailsFormatter.Resolution(1920, 1080));
        Assert.Equal(string.Empty, MediaDetailsFormatter.Resolution(0, 1080));
    }

    [Theory]
    [InlineData(0, "0:00.0")]
    [InlineData(61567, "1:01.5")]
    [InlineData(59999, "0:59.9")]
    [InlineData(60000, "1:00.0")]
    [InlineData(3600100, "60:00.1")]
    public void DurationKeepsTenthsAndTotalMinutes(double duration, string expected) =>
        Assert.Equal(expected, MediaDetailsFormatter.Duration(duration));

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void UnknownDurationIsNotShownAsZero(double duration) =>
        Assert.Empty(MediaDetailsFormatter.Duration(duration));

    [Fact]
    public void MultipleSelectionTotalsEveryFileIncludingEmptyFiles()
    {
        MediaSizeSummary result = MediaDetailsFormatter.Summarize([1024, 2516582, 0]);
        Assert.Equal(3, result.Count);
        Assert.Equal(2517606, result.TotalBytes);
        Assert.Equal(0, result.UnavailableCount);
    }

    [Fact]
    public void MissingFilesRemainCountedAndFlagTheIncompleteTotal()
    {
        MediaSizeSummary result = MediaDetailsFormatter.Summarize([10, null, 20]);
        Assert.Equal(3, result.Count);
        Assert.Equal(30, result.TotalBytes);
        Assert.Equal(1, result.UnavailableCount);
        Assert.Equal(2m * long.MaxValue, MediaDetailsFormatter.Summarize([long.MaxValue, long.MaxValue]).TotalBytes);
    }
}
