using System.Globalization;
using MyCapture.Core.Export;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class NaturalTitleComparerTests
{
    private static readonly NaturalTitleComparer Korean = new(CultureInfo.GetCultureInfo("ko-KR"));

    [Theory]
    [InlineData("Capture 9", "Capture 10")]
    [InlineData("Capture 2", "Capture 10")]
    [InlineData("capture 9", "Capture 10")]
    [InlineData("Capture 10", "capture 11")]
    [InlineData("v1.9", "v1.10")]
    [InlineData("Capture 9b", "Capture 10a")]
    [InlineData("Capture", "Capture 1")]
    [InlineData("1 apple", "apple")]
    [InlineData("화면 2", "화면 12")]
    [InlineData("가나 3", "다라 1")]
    [InlineData("Capture 7", "Capture 007")]
    [InlineData("Capture 007a", "Capture 7b")]
    [InlineData("99999999999999999999999999999998", "99999999999999999999999999999999")]
    [InlineData("99999999999999999999999999999999", "100000000000000000000000000000000")]
    [InlineData("0000000000000000000000000000000000001", "2")]
    public void LeftComesBeforeRight(string left, string right)
    {
        Assert.True(Korean.Compare(left, right) < 0, $"{left} < {right}");
        Assert.True(Korean.Compare(right, left) > 0, $"{right} > {left}");
    }

    [Theory]
    [InlineData("Capture 10", "capture 10")]
    [InlineData("", "")]
    [InlineData("000", "000")]
    public void EqualTitlesCompareEqual(string left, string right) => Assert.Equal(0, Korean.Compare(left, right));

    [Fact]
    public void SortingFollowsExplorerOrder()
    {
        string[] titles = ["Capture 10", "Capture 9", "capture 1", "Capture 100", "Capture 02", "Capture 2"];
        Assert.Equal(["capture 1", "Capture 2", "Capture 02", "Capture 9", "Capture 10", "Capture 100"],
            titles.Order(Korean).ToArray());
        Assert.True(Korean.Compare(null, "a") < 0);
        Assert.True(Korean.Compare("a", null) > 0);
    }
}
