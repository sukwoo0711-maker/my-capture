using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using MyCapture.App.Ocr;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrAccuracyPolishTests
{
    [Theory]
    [InlineData("ko", "안내 닫기", "내려받기")]
    [InlineData("en", "Dismiss", "Download")]
    public void SuggestionHasSecondaryButtonAndAccessibleKeyboardControls(string language, string dismissName, string downloadLabel)
        => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var window = new OcrResultWindow { SuppressPresentation = true };
        try
        {
            Ocr410Fixture.LoadTheme(window);
            window.ConfigureAccuracy(new OcrAccuracyOptions(() => new OcrSettings(), () => { }, new MissingModel()),
                _ => Task.CompletedTask, () => { });
            var (image, result) = Ocr410Fixture.Table();
            window.ShowResult(result.WithQuality(OcrQuality.Fast), "fixture", image);
            window.CompleteAccuracyPresentation();
            OcrTranslationWindowTests.PumpUntil(() => window.AccuracyModelCheckTask.IsCompleted);
            window.AccuracyModelCheckTask.GetAwaiter().GetResult();
            Assert.Same(window.FindResource("Button.Secondary"), window.AccuracyAction.Style);
            Assert.True(window.AccuracyAction.Focusable);
            Assert.True(KeyboardNavigation.GetIsTabStop(window.AccuracyAction));
            Assert.True(window.AccuracyAction.IsEnabled);
            Assert.Equal(Visibility.Visible, window.AccuracyAction.Visibility);
            Assert.Equal(KeyboardNavigationMode.Continue, KeyboardNavigation.GetTabNavigation(window.AccuracySuggestion));
            Assert.Same(window.FindResource("Button.Icon"), window.AccuracyDismiss.Style);
            Assert.Equal(dismissName, AutomationProperties.GetName(window.AccuracyDismiss));
            Assert.Equal(dismissName, window.AccuracyDismiss.ToolTip);
            Assert.True(KeyboardNavigation.GetIsTabStop(window.AccuracyDismiss));
            Assert.Contains($"{downloadLabel} {OcrModelCatalog.DownloadMegabytesFor(OcrQuality.Accurate)} MB",
                window.AccuracySuggestionText.Text);
            Assert.DoesNotContain("다운로드", window.AccuracySuggestionText.Text);
            Assert.False(window.IsVisible);
        }
        finally { window.CloseForExit(); }
    });

    [Theory]
    [InlineData(0L, 0L, 0L, 1d)]
    [InlineData(0L, 20L, 0L, 1d)]
    [InlineData(20L, 30L, 100L, 0.5d)]
    [InlineData(90L, 20L, 100L, 1d)]
    [InlineData(long.MaxValue, long.MaxValue, long.MaxValue, 1d)]
    public void DownloadProgressRemainsFiniteAndBounded(long completed, long current, long total, double expected)
    {
        double progress = OcrModelStore.DownloadProgress(completed, current, total);
        Assert.True(double.IsFinite(progress));
        Assert.InRange(progress, 0, 1);
        Assert.Equal(expected, progress);
    }

    private sealed class MissingModel : IOcrAccuracyModel
    {
        public bool IsSupported => true;
        public bool IsReady => false;
        public bool IsMetered => false;
        public long DownloadBytes => OcrModelCatalog.DownloadBytesFor(OcrQuality.Accurate);
        public Task<bool> DownloadAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No model downloads in control tests.");
    }
}
