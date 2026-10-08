using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Ocr;
using MyCapture.Core.Localization;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class Ocr410WindowsEngineTests
{
    [Ocr410WindowsEngineTheory]
    [InlineData("table")]
    [InlineData("code")]
    [InlineData("columns")]
    [Trait("Category", "WindowsOcr410")]
    public void InstalledWindowsEngineRecognizesIsolatedExtractionExamples(string example) => Ocr410Fixture.RunSta(() =>
    {
        using var language = UiText.UseLanguage("ko");
        BitmapSource image = example == "table" ? Ocr410Fixture.Table().Image : Draw(example);
        string languageTag = example == "table" ? "ko-KR" : "en-US";
        var service = new WindowsOcrService(NullLogger<WindowsOcrService>.Instance);
        Assert.True(service.IsAvailable, "Windows OCR is unavailable.");
        Assert.NotEmpty(service.SupportedLanguages);
        OcrResult result = service.RecognizeAsync(OcrRequest.FromBitmap(image,
            preferredLanguages: [languageTag], searchRotatedOrientations: false)).GetAwaiter().GetResult();
        OcrExtraction extraction = OcrExtractionAnalyzer.Analyze(result);
        SaveEvidence(example, image, result, extraction);
        Assert.Equal(OcrStatus.Success, result.Status);
        Assert.True(extraction.HasGeometry);
        if (example == "table")
        {
            Assert.Equal(3, extraction.Table.Columns.Count);
            Assert.Equal(4, extraction.Table.Rows.Count);
            Assert.True(extraction.Table.HasHeader);
            Assert.Contains("128,400", OcrTableExport.Csv(extraction.Table));
            Assert.Contains("1,204,000", OcrTableExport.Csv(extraction.Table));
        }
        else if (example == "code")
        {
            Assert.Equal("Python", extraction.Code.Language);
            Assert.Contains("greet", extraction.Code.Text);
            Assert.Contains("\n    ", extraction.Code.Text);
            Assert.Contains("return", extraction.Code.Text);
        }
        else
        {
            string text = extraction.Text(false);
            Assert.Contains("Left second", text);
            Assert.Contains("Right first", text);
            Assert.True(text.IndexOf("Left second", StringComparison.Ordinal)
                < text.IndexOf("Right first", StringComparison.Ordinal), text);
            Assert.Contains("Left first line Left second line", extraction.Text(true));
        }
    });

    [Ocr410WindowsEngineTheory]
    [InlineData("table")]
    [InlineData("code")]
    public void OriginalResolutionProbe(string example) => Ocr410Fixture.RunSta(() =>
    {
        BitmapSource image = example == "table" ? Ocr410Fixture.Table().Image : Draw(example);
        var recognizer = new WindowsOcrRecognizer(NullLogger.Instance);
        Assert.True(recognizer.IsAvailable);
        RecognizedText? raw = recognizer.RecognizeAsync(image, example == "table" ? "ko" : "en",
            CancellationToken.None).GetAwaiter().GetResult();
        Assert.NotNull(raw);
        OcrLine[] lines = raw.Lines.Select(line => new OcrLine(line.Text,
            line.Words.Select(word => word.Bounds).Aggregate(default(OcrRect), OcrRect.Union),
            line.Words.Select(word => new OcrWord(word.Text, word.Bounds)).ToArray())).ToArray();
        var result = OcrResult.Success(string.Join("\n", raw.Lines.Select(line => line.Text)),
            raw.LanguageTag, lines, TimeSpan.Zero);
        OcrExtraction extraction = OcrExtractionAnalyzer.Analyze(result);
        SaveEvidence(example + "-original", image, result, extraction);
        if (example == "table")
        {
            Assert.Contains("1,204,000", OcrTableExport.Csv(extraction.Table));
            Assert.Contains("항목", OcrTableExport.Csv(extraction.Table));
        }
        else Assert.Contains("if name is None:", extraction.Code.Text);
    });

    private static BitmapSource Draw(string example)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 940, 300));
            if (example == "code")
            {
                string[] lines = ["def greet(name):", "    if name is None:", "        return \"Hello\"", "    return name"];
                for (int index = 0; index < lines.Length; index++)
                    DrawText(drawing, lines[index], "Consolas", 28, 25, 30 + index * 44);
            }
            else
            {
                DrawText(drawing, "Left first line", "Segoe UI", 28, 25, 30);
                DrawText(drawing, "Left second line", "Segoe UI", 28, 25, 74);
                DrawText(drawing, "Right first line", "Segoe UI", 28, 500, 30);
                DrawText(drawing, "Right second line", "Segoe UI", 28, 500, 74);
            }
        }
        var image = new RenderTargetBitmap(940, 300, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        return image;
    }

    private static void DrawText(DrawingContext drawing, string text, string font, double size, double x, double y) =>
        drawing.DrawText(new FormattedText(text, CultureInfo.GetCultureInfo("en-US"),
            FlowDirection.LeftToRight, new Typeface(font), size, Brushes.Black, 1), new Point(x, y));

    private static void SaveEvidence(string name, BitmapSource image, OcrResult result, OcrExtraction extraction)
    {
        string? root = Environment.GetEnvironmentVariable("MYCAPTURE_OCR410_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        Ocr410Fixture.Save(image, Path.Combine(root, name + "-input.png"));
        File.WriteAllText(Path.Combine(root, name + "-result.json"), JsonSerializer.Serialize(new
        {
            result.Status, result.LanguageTag, RawText = result.Text, result.Lines,
            Csv = OcrTableExport.Csv(extraction.Table), Markdown = OcrTableExport.Markdown(extraction.Table),
            Code = extraction.Code, PlainText = extraction.Text(false), Joined = extraction.Text(true),
        }, new JsonSerializerOptions { WriteIndented = true }));
        var window = new OcrResultWindow { ShowInTaskbar = false, Width = 1040, Height = 720 };
        try
        {
            Ocr410Fixture.LoadTheme(window);
            window.ShowResult(result, "Windows OCR · " + name, image);
            window.SelectExtractionMode(name.StartsWith("table", StringComparison.Ordinal) ? OcrExtractionMode.Table
                : name.StartsWith("code", StringComparison.Ordinal) ? OcrExtractionMode.Code : OcrExtractionMode.Text);
            window.UpdateLayout();
            Ocr410Fixture.Settle();
            var screenshot = new RenderTargetBitmap(1040, 720, 96, 96, PixelFormats.Pbgra32);
            screenshot.Render(window);
            Ocr410Fixture.Save(screenshot, Path.Combine(root, name + "-window.png"));
        }
        finally { window.CloseForExit(); }
    }
}

internal sealed class Ocr410WindowsEngineTheoryAttribute : TheoryAttribute
{
    public Ocr410WindowsEngineTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_OCR410_RUN_LIVE") != "1")
            Skip = "Opt-in Windows OCR diagnostics: set MYCAPTURE_OCR410_RUN_LIVE=1 on the validation host.";
    }
}
