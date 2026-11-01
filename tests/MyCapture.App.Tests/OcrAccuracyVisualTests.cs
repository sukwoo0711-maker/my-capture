using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Ocr;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrAccuracyVisualTests
{
    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void RenderOffscreenAccuracyStates(string language) => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var (image, result) = Ocr410Fixture.Table();
        var window = new OcrResultWindow { Width = 1040, Height = 720, ShowInTaskbar = false, SuppressPresentation = true };
        try
        {
            Ocr410Fixture.LoadTheme(window);
            var settings = new OcrSettings();
            var model = new VisualModel();
            window.ConfigureAccuracy(new OcrAccuracyOptions(() => settings, () => { }, model),
                _ => Task.CompletedTask, () => { });
            window.ShowResult(result.WithQuality(OcrQuality.Fast), "MyCapture OCR", image);
            window.CompleteAccuracyPresentation();
            CompleteModelCheck(window);
            SaveContent(window, language, "suggestion");
            Assert.Equal(Visibility.Visible, window.AccuracySuggestion.Visibility);
            window.BeginQualityOperation(downloading: true);
            window.ReportQualityProgress(0.45);
            SaveContent(window, language, "downloading");
            Assert.Equal(0.45, window.AccuracyProgress.Value);
            window.EndQualityOperation();
            model.IsReady = true;
            window.RefreshAccuracy();
            CompleteModelCheck(window);
            SaveContent(window, language, "ready");
            Assert.Equal(Visibility.Visible, window.AccuracySuggestion.Visibility);
            window.ShowResult(result.WithQuality(OcrQuality.Accurate), "MyCapture OCR", image);
            SaveContent(window, language, "accurate");
            Assert.Equal(Visibility.Collapsed, window.AccuracySuggestion.Visibility);
            model.IsSupported = false;
            model.IsReady = false;
            window.ShowResult(result.WithQuality(OcrQuality.Fast), "MyCapture OCR", image);
            SaveContent(window, language, "unsupported");
            Assert.Equal(Visibility.Collapsed, window.AccuracySuggestion.Visibility);
            Assert.True(window.AccuracySelector.IsEnabled);
            Assert.False(window.IsVisible);
        }
        finally { window.CloseForExit(); }
    });

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void MinimumSizeKeepsAccuracyControlsInsideContent(string language) => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var (image, result) = Ocr410Fixture.Table();
        var window = new OcrResultWindow { SuppressPresentation = true, Width = 760, Height = 520 };
        try
        {
            Ocr410Fixture.LoadTheme(window);
            window.ConfigureAccuracy(new OcrAccuracyOptions(() => new OcrSettings(), () => { }, new VisualModel()),
                _ => Task.CompletedTask, () => { });
            window.ShowResult(result.WithQuality(OcrQuality.Fast), "MyCapture OCR", image);
            window.CompleteAccuracyPresentation();
            CompleteModelCheck(window);
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(760, 520));
            content.Arrange(new Rect(0, 0, 760, 520));
            content.UpdateLayout();
            foreach (FrameworkElement control in new FrameworkElement[] { window.AccuracySelector, window.AccuracyAction, window.AccuracyDismiss })
            {
                Rect bounds = control.TransformToAncestor(content).TransformBounds(new Rect(control.RenderSize));
                Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= 760 && bounds.Bottom <= 520,
                    $"{control.GetType().Name}: {bounds}");
            }
            Assert.False(window.IsVisible);
        }
        finally { window.CloseForExit(); }
    });

    private static void CompleteModelCheck(OcrResultWindow window)
    {
        Task check = window.AccuracyModelCheckTask;
        var elapsed = Stopwatch.StartNew();
        while (!check.IsCompleted && elapsed.Elapsed < TimeSpan.FromSeconds(10)) Ocr410Fixture.Pump();
        Assert.True(check.IsCompleted, "The offscreen model check did not complete.");
        check.GetAwaiter().GetResult();
    }

    internal static void SaveContent(OcrResultWindow window, string language, string state)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1040, 720));
        content.Arrange(new Rect(0, 0, 1040, 720));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1040, 720, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        Assert.Equal(1040, bitmap.PixelWidth);
        string? root = Environment.GetEnvironmentVariable("MYCAPTURE_OCR_ACCURACY_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        Ocr410Fixture.Save(bitmap, Path.Combine(root, $"{state}-{language}.png"));
    }

    private sealed class VisualModel : IOcrAccuracyModel
    {
        public bool IsSupported { get; set; } = true;
        public bool IsReady { get; set; }
        public bool IsMetered => false;
        public long DownloadBytes => OcrModelCatalog.DownloadBytesFor(OcrQuality.Accurate);
        public Task<bool> DownloadAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Visual fixtures never download models.");
    }
}
