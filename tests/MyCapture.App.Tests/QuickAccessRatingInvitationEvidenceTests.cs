using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class QuickAccessRatingInvitationEvidenceTests
{
    [Fact]
    public Task RenderRatingInvitationEvidenceWhenRequested()
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_RATING_CARD_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return Task.CompletedTask;
        string directory = DiagnosticOutputPaths.Create(requested);
        return SettingsThemePreviewTests.RunIsolated(nameof(RenderRatingInvitationEvidenceWhenRequested), () =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            try
            {
                var images = new List<(string Label, BitmapSource Image)>();
                foreach (AppTheme theme in new[] { AppTheme.Daylight, AppTheme.Midnight })
                foreach (string language in new[] { "ko-KR", "en" })
                foreach (double scale in new[] { 1.0, 1.5 })
                {
                    using var culture = UiText.UseLanguage(language);
                    foreach (bool invitation in new[] { false, true })
                    {
                        QuickAccessWindow card = QuickAccessRatingInvitationTests.Card(scale);
                        try
                        {
                            if (invitation) card.OfferRatingInvitation(() => Task.CompletedTask, () => { });
                            Border host = QuickAccessDetachedFixture.Layout(card, scale);
                            ThemeService.ApplyResources(theme, host.Resources, systemHighContrast: false);
                            QuickAccessRatingInvitationTests.Relayout(host, card.Width);
                            string state = invitation ? "invitation" : "baseline";
                            string label = $"{theme}-{language}-{scale.ToString("0.0", CultureInfo.InvariantCulture)}-{state}";
                            Save(label, Render(host, scale));
                            if (invitation && language == "ko-KR" && scale == 1.5)
                            {
                                double height = host.ActualHeight;
                                QuickAccessRatingInvitationTests.Click(card.RatingInvitationForTest.Close);
                                QuickAccessRatingInvitationTests.Relayout(host, card.Width);
                                Assert.Equal(height, host.ActualHeight);
                                Save(label.Replace("invitation", "closed"), Render(host, scale));
                            }
                        }
                        finally { card.Close(); }
                    }
                }
                Assert.Equal(18, images.Count);
                Ocr410Fixture.Save(ContactSheet(images), DiagnosticOutputPaths.Child(directory, "contact-sheet.png"));
                Assert.True(File.Exists(DiagnosticOutputPaths.Child(directory, "contact-sheet.png")));

                void Save(string label, BitmapSource bitmap)
                {
                    string path = DiagnosticOutputPaths.Child(directory, label + ".png");
                    Ocr410Fixture.Save(bitmap, path);
                    Assert.True(new FileInfo(path).Length > 0);
                    images.Add((label, bitmap));
                }
            }
            finally { application.Shutdown(); }
        }, typeof(QuickAccessRatingInvitationEvidenceTests));
    }

    private static BitmapSource Render(Border host, double scale)
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.SlateGray, null, new Rect(host.RenderSize));
            drawing.DrawRectangle(new VisualBrush(host) { Stretch = Stretch.None }, null, new Rect(host.RenderSize));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth * scale),
            (int)Math.Ceiling(host.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource ContactSheet(List<(string Label, BitmapSource Image)> images)
    {
        const int columns = 4;
        int cellWidth = images.Max(item => item.Image.PixelWidth) + 24;
        int cellHeight = images.Max(item => item.Image.PixelHeight) + 48;
        int width = cellWidth * columns;
        int height = cellHeight * ((images.Count + columns - 1) / columns);
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (int i = 0; i < images.Count; i++)
            {
                double x = i % columns * cellWidth + 12;
                double y = i / columns * cellHeight + 12;
                var text = new FormattedText(images[i].Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 14, Brushes.Black, 1);
                drawing.DrawText(text, new Point(x, y));
                drawing.DrawImage(images[i].Image, new Rect(x, y + 26, images[i].Image.PixelWidth, images[i].Image.PixelHeight));
            }
        }
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        return result;
    }
}
