using System.IO;
using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class StoreScreenshotSelfTestTests
{
    [Theory]
    [InlineData("ko", "ko")]
    [InlineData("EN", "en")]
    public void LayersArgumentsKeepExplicitOutputAndLanguage(string language, string expected)
    {
        string root = Path.Combine(Path.GetTempPath(), "store-layer-argument-only");
        var result = StoreScreenshotSelfTest.ParseLayersArguments([StoreScreenshotSelfTest.LayersCommandLineSwitch, root, language], 0);
        Assert.Equal(Path.GetFullPath(root), result.Output);
        Assert.Equal(expected, result.Language);
    }

    [Fact]
    public void LayersArgumentsDefaultToKorean()
    {
        var result = StoreScreenshotSelfTest.ParseLayersArguments([StoreScreenshotSelfTest.LayersCommandLineSwitch, "layers"], 0);
        Assert.Equal("ko", result.Language);
        Assert.True(Path.IsPathFullyQualified(result.Output));
    }

    [Theory]
    [InlineData("--selftest-store-layers")]
    [InlineData("--selftest-store-layers|--selftest-store-shots")]
    [InlineData("--selftest-store-layers|layers|fr")]
    [InlineData("--selftest-store-layers|layers|en|extra")]
    public void LayersArgumentsRejectMissingOutputAndUnsupportedOptions(string command) =>
        Assert.Throws<ArgumentException>(() => StoreScreenshotSelfTest.ParseLayersArguments(command.Split('|'), 0));
}
