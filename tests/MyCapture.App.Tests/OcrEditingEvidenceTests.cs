using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Ocr;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrEditingEvidenceTests
{
    [Theory]
    [InlineData("ko", AppTheme.Midnight)]
    [InlineData("en", AppTheme.Midnight)]
    [InlineData("ko", AppTheme.Daylight)]
    [InlineData("en", AppTheme.Daylight)]
    public void RenderEditedTabsAndTablePin(string language, AppTheme theme) => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var window = new OcrResultWindow { Width = 1040, Height = 720, SuppressPresentation = true };
        Ocr410Fixture.LoadTheme(window);
        ThemeService.ApplyResources(theme, window.Resources, systemHighContrast: false);
        try
        {
            var (image, original) = Ocr410Fixture.Table();
            OcrLine[] lines = original.Lines.Select((line, index) => index == 2 ? line with
            {
                Text = line.Text.Replace("12", "l2", StringComparison.Ordinal),
                Words = line.Words.Select((word, column) => column == 1 ? word with { Text = "l2" } : word).ToArray(),
            } : line).ToArray();
            OcrResult result = OcrResult.Success(string.Join('\n', lines.Select(line => line.Text)), "ko", lines, TimeSpan.Zero);
            window.ShowResult(result, "OCR", image, quality: OcrQuality.Accurate);
            window.CompleteAccuracyPresentation();
            window.SelectExtractionMode(OcrExtractionMode.Table);
            window.EditTableCell(2, 1, "12");
            window.TableGrid.CurrentCell = new DataGridCellInfo(window.TableGrid.Items[1], window.TableGrid.Columns[1]);
            window.TableGrid.SelectedCells.Add(window.TableGrid.CurrentCell);
            Assert.Equal(3, window.ImageOverlay.HighlightedRegions.Count);
            Save(window, language, theme, "table");

            var manager = new PinManager(() => new PinSettings { ClosedWindowRestoreLimit = 0 }, NullLogger.Instance)
            { ShowPinForTest = (_, _) => { }, ActivatePinForTest = _ => { } };
            PinWindow? pin = null;
            window.PinRequested += (_, request) =>
            {
                pin = manager.PinOcrAsync(request.Text, request.Kind).GetAwaiter().GetResult();
                request.Completion = Task.FromResult(true);
            };
            try
            {
                window.RequestPinAsync().GetAwaiter().GetResult();
                Assert.NotNull(pin);
                Assert.Equal(OcrTableExport.Tsv(window.CurrentTable()), pin.OriginalText);
                SaveBitmap(pin.PinnedContent.Image, language, theme, "table-pin");
            }
            finally { manager.CloseAllForExit(); }

            window.SelectExtractionMode(OcrExtractionMode.Text);
            window.ResultTextBox.CaretIndex = string.Join('\n', window.Extraction!.Paragraphs[0].Lines).Length + 2;
            Assert.NotEmpty(window.ImageOverlay.HighlightedRegions);
            Save(window, language, theme, "text");

            window.ShowResult(CodeResult(), "OCR", CodeImage(), quality: OcrQuality.Accurate);
            window.SelectExtractionMode(OcrExtractionMode.Code);
            window.ResultTextBox.Text = window.ResultTextBox.Text.Replace("l2", "12", StringComparison.Ordinal);
            window.ResultTextBox.CaretIndex = window.ResultTextBox.Text.IndexOf("12", StringComparison.Ordinal);
            Assert.True(window.HasEditedText);
            Save(window, language, theme, "code");
        }
        finally { window.CloseForExit(); }
    });

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void MinimumWindowFitsEveryFooterButton(string language) => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var window = new OcrResultWindow { Width = 760, Height = 520, ShowInTaskbar = false };
        using var size = new WindowTrackSizeScope(window, new Size(760, 520));
        Ocr410Fixture.LoadTheme(window);
        try
        {
            var (image, result) = Ocr410Fixture.Table();
            window.ShowResult(result, "table", image);
            window.CompleteAccuracyPresentation();
            window.EditTableCell(2, 1, "123");
            foreach (OcrExtractionMode mode in new[] { OcrExtractionMode.Table, OcrExtractionMode.Text, OcrExtractionMode.Code })
            {
                window.SelectExtractionMode(mode);
                window.UpdateLayout();
                Ocr410Fixture.Pump();
                foreach (Button button in OcrEditingWindowTests.Descendants(window).OfType<Button>().Where(b => b.IsVisible))
                {
                    Rect bounds = button.TransformToAncestor(window).TransformBounds(new Rect(button.RenderSize));
                    Assert.True(bounds.Left >= 0 && bounds.Right <= window.ActualWidth && bounds.Top >= 0
                        && bounds.Bottom <= window.ActualHeight,
                        $"{mode}/{AutomationProperties.GetName(button)}: {bounds}; {window.ActualWidth}x{window.ActualHeight}");
                }
            }
        }
        finally { window.CloseForExit(); }
    });

    private static OcrResult CodeResult() => Ocr410ExtractionTests.CodeResult(
        "public class Sample\n{\n    public int Count = l2;\n}", top: 35);

    private static BitmapSource CodeImage()
    {
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 560, 300));
            foreach (OcrWord word in CodeResult().Lines.SelectMany(line => line.Words))
                context.DrawText(new FormattedText(word.Text.Replace("l2", "12", StringComparison.Ordinal),
                    System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Consolas"),
                    13.3, Brushes.Black, 1), new Point(word.Bounds.X, word.Bounds.Y));
        }
        var image = new RenderTargetBitmap(560, 300, 96, 96, PixelFormats.Pbgra32);
        image.Render(drawing);
        image.Freeze();
        return image;
    }

    private static void Save(OcrResultWindow window, string language, AppTheme theme, string state)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1040, 720));
        content.Arrange(new Rect(0, 0, 1040, 720));
        content.UpdateLayout();
        Ocr410Fixture.Pump();
        content.Measure(new Size(1040, 720));
        content.Arrange(new Rect(0, 0, 1040, 720));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1040, 720, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        SaveBitmap(bitmap, language, theme, state);
    }

    private static void SaveBitmap(BitmapSource bitmap, string language, AppTheme theme, string state)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_OCR_EDIT_EVIDENCE");
        if (string.IsNullOrEmpty(requested)) return;
        string root = DiagnosticOutputPaths.Create(requested);
        Ocr410Fixture.Save(bitmap, DiagnosticOutputPaths.Child(root,
            $"{state}-{language}-{(theme == AppTheme.Daylight ? "light" : "dark")}.png"));
    }
}
