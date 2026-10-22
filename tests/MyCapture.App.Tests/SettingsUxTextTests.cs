using MyCapture.App.Settings;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SettingsUxTextTests
{
    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void OcrSizes_IncludeOptionalDetectionAndOneSuperResolutionArchive(string language)
    {
        using var scope = UiText.UseLanguage(language);
        Assert.Equal(101_654_967, OcrModelCatalog.DownloadBytesFor(OcrQuality.Accurate));
        Assert.Equal(163_828_993, OcrModelCatalog.DownloadBytesFor(OcrQuality.Enhanced));
        Assert.Equal(0, OcrModelCatalog.DownloadBytesFor(OcrQuality.Fast));
        Assert.Equal(0, OcrModelCatalog.DownloadBytesFor(OcrQuality.Balanced));
        Assert.Equal(102, SettingsWindow.OcrModelMegabytes);
        Assert.Equal(164, SettingsWindow.EnhancedModelMegabytes);
        string privacy = UiText.Format("About_PrivacyModel", SettingsWindow.OcrModelMegabytes,
            SettingsWindow.EnhancedModelMegabytes);
        Assert.Contains("102 MB", privacy, StringComparison.Ordinal);
        Assert.Contains("164 MB", privacy, StringComparison.Ordinal);
        Assert.Contains("102 MB", SettingsWindow.OcrQualityDescription, StringComparison.Ordinal);
        Assert.Contains("164 MB", SettingsWindow.OcrQualityDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", SettingsWindow.OcrQualityDescription, StringComparison.Ordinal);
        if (language == "ko-KR")
        {
            Assert.StartsWith("정확 품질은 텍스트 인식 모델", privacy, StringComparison.Ordinal);
            Assert.StartsWith("빠름과 보통은 Windows 기본 인식 기능을 사용합니다.", SettingsWindow.OcrQualityDescription,
                StringComparison.Ordinal);
            Assert.DoesNotContain("?", privacy, StringComparison.Ordinal);
            Assert.DoesNotContain("?", SettingsWindow.OcrQualityDescription, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("ko-KR", 168, "7일")]
    [InlineData("ko-KR", 24, "1일")]
    [InlineData("ko-KR", 36, "1일 12시간")]
    [InlineData("ko-KR", 5, "5시간")]
    [InlineData("ko-KR", 8760, "365일")]
    [InlineData("en-US", 168, "7 days")]
    [InlineData("en-US", 24, "1 day")]
    [InlineData("en-US", 25, "1 day 1 hour")]
    [InlineData("en-US", 1, "1 hour")]
    public void Duration_UsesDaysAndHours(string language, int hours, string expected)
    {
        using var scope = UiText.UseLanguage(language);
        Assert.Equal(expected, SettingsUxText.Duration(hours));
    }

    [Theory]
    [InlineData("168", "만든 지 7일이 지난")]
    [InlineData(" 36 ", "만든 지 1일 12시간이 지난")]
    public void RetentionSummary_StatesTheTypedPeriod(string typed, string expected)
    {
        using var scope = UiText.UseLanguage("ko-KR");
        string summary = SettingsUxText.RetentionSummary(typed);
        Assert.StartsWith(expected, summary, StringComparison.Ordinal);
        Assert.DoesNotContain("7일 자동", summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("8761")]
    [InlineData("abc")]
    [InlineData(null)]
    public void RetentionSummary_ShowsTheRangeForAnEntryTheDraftRejects(string? typed)
    {
        using var scope = UiText.UseLanguage("ko-KR");
        Assert.Equal(UiText.Get("SettingsUx_RetentionRange"), SettingsUxText.RetentionSummary(typed));
    }
}
