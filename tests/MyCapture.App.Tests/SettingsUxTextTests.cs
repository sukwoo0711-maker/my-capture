using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
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
    public void OcrSizesAndPrivacyDescribeOnlyTheAccurateCatalog(string language)
    {
        using var scope = UiText.UseLanguage(language);
        Assert.Equal(101_654_967, OcrModelCatalog.DownloadBytesFor(OcrQuality.Accurate));
        Assert.Equal(101_654_967, OcrModelCatalog.DownloadBytesFor(OcrQuality.Enhanced));
        Assert.Equal(0, OcrModelCatalog.DownloadBytesFor(OcrQuality.Fast));
        Assert.Equal(0, OcrModelCatalog.DownloadBytesFor(OcrQuality.Balanced));
        Assert.Equal(102, SettingsWindow.OcrModelMegabytes);
        string privacy = UiText.Format("About_PrivacyModel", SettingsWindow.OcrModelMegabytes);
        Assert.Contains("102 MB", privacy, StringComparison.Ordinal);
        Assert.Contains("102 MB", SettingsWindow.OcrQualityDescription, StringComparison.Ordinal);
        Assert.Equal(UiText.Format("Settings.OcrQualityHint", SettingsWindow.OcrModelMegabytes),
            SettingsWindow.OcrQualityDescription);
        foreach (string copy in new[] { privacy, SettingsWindow.OcrQualityDescription })
        {
            foreach (string stale in new[] { "{0}", "{1}", "164 MB", "Qualcomm", "super-resolution",
                "upscaling", "Most accurate", "가장 정확", "receipt", "영수증" })
                Assert.DoesNotContain(stale, copy, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("ModelScope", privacy, StringComparison.Ordinal);
        if (language == "ko-KR")
        {
            Assert.StartsWith("정확 품질은 텍스트 인식 모델", privacy, StringComparison.Ordinal);
            Assert.StartsWith("빠름과 보통은 Windows 기본 인식 기능을 사용합니다.", SettingsWindow.OcrQualityDescription,
                StringComparison.Ordinal);
            Assert.DoesNotContain("?", privacy, StringComparison.Ordinal);
            Assert.DoesNotContain("?", SettingsWindow.OcrQualityDescription, StringComparison.Ordinal);
            Assert.Contains("작고 흐린 글자와 코드", SettingsWindow.OcrQualityDescription, StringComparison.Ordinal);
            Assert.Contains("Windows 인식 결과와 줄별로 비교합니다.", SettingsWindow.OcrQualityDescription, StringComparison.Ordinal);
            Assert.Contains("이미지는 전송하지 않습니다.", privacy, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("small, low-contrast text and code", SettingsWindow.OcrQualityDescription, StringComparison.Ordinal);
            Assert.Contains("Windows recognition line by line", SettingsWindow.OcrQualityDescription, StringComparison.Ordinal);
            Assert.Contains("Images are never sent.", privacy, StringComparison.Ordinal);
        }
    }

    [Fact]
    public Task OcrSelectorOffersOnlyThreeKoreanQualities() => SettingsThemePreviewTests.RunIsolated(
        nameof(OcrSelectorOffersOnlyThreeKoreanQualities),
        () => VerifyOcrSelector("ko-KR", "빠름", "보통", "정확"), typeof(SettingsUxTextTests));

    [Fact]
    public Task OcrSelectorOffersOnlyThreeEnglishQualities() => SettingsThemePreviewTests.RunIsolated(
        nameof(OcrSelectorOffersOnlyThreeEnglishQualities),
        () => VerifyOcrSelector("en-US", "Fast", "Normal", "Accurate"), typeof(SettingsUxTextTests));

    private static void VerifyOcrSelector(string language, string fast, string balanced, string accurate)
    {
        using var scope = UiText.UseLanguage(language);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            // Settings BAML resolves these application resources during construction.
            // Each language runs in its own process to isolate WPF's Application singleton.
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
                });
            var settings = new AppSettings { Ocr = { Quality = OcrQuality.Enhanced, UpscaleFactor = 2.0 } };
            var window = new SettingsWindow(() => settings,
                _ => new SettingsApplyResult(true, true, true, false, []), NullLogger.Instance)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, Width = 940, Height = 700,
                ShowActivated = false, ShowInTaskbar = false,
            };
            try
            {
                Assert.Equal("accurate", Assert.IsType<SettingsDraft>(window.DataContext).OcrQuality);
                FluidMotion.SetWindowEntrance(window, false);
                window.Show();
                var categories = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
                Assert.True(window.NavigateToSection(SettingsSection.Text));
                // Match the product-theme fixture's drain after showing the category:
                // bindings and layout must settle before reading the actual selection.
                window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
                var selector = Assert.IsType<ComboBox>(window.FindName("OcrQualitySelector"));
                ComboBoxItem[] items = selector.Items.Cast<ComboBoxItem>().ToArray();
                Assert.Equal(new[] { "fast", "balanced", "accurate" }, items.Select(item => (string)item.Tag));
                Assert.Equal(new[] { fast, balanced, accurate }, items.Select(item => (string)item.Content));
                Assert.Equal("accurate", selector.SelectedValue);
                Assert.Equal(SettingsWindow.OcrQualityDescription,
                    KoreanWordWrap.Strip(Assert.IsType<TextBlock>(window.FindName("OcrQualityHintText")).Text));
                Assert.Equal(UiText.Format("About_PrivacyModel", SettingsWindow.OcrModelMegabytes),
                    KoreanWordWrap.Strip(Assert.IsType<TextBlock>(window.FindName("PrivacyModelText")).Text));
            }
            finally { window.CloseForExit(); }
        }
        finally { application.Shutdown(); }
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
