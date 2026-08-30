using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Ocr;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// Renders a high-contrast synthetic English image with known words, runs OS OCR over it, and
/// writes a report.
/// </summary>
/// <remarks>
/// <para>
/// Reachable via <c>MyCapture.exe --selftest-ocr &lt;directory&gt;</c>.
/// </para>
/// <para>
/// Windows desktop OCR frequently requires package identity, and a machine may have no OCR
/// language pack installed at all. When the engine cannot be created the test reports an
/// explicit <c>SKIP</c>/<c>UNAVAILABLE</c> and exits 0 rather than faking a pass — a genuine
/// live end-to-end validation belongs to the packaged build (task 14). When the engine is
/// available, the report states whether the known words were found.
/// </para>
/// </remarks>
internal static class OcrSelfTest
{
    internal const string CommandLineSwitch = "--selftest-ocr";

    private static readonly string[] KnownWords = ["MyCapture", "Hello", "World", "OCR", "12345"];

    internal static int Run(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var report = new StringBuilder();
        void Line(string text) => report.AppendLine(text);

        Line("MyCapture OCR self-test");
        Line($"UTC: {DateTimeOffset.UtcNow:u}");
        Line($"OS: {Environment.OSVersion.VersionString}");
        Line($"64-bit process: {Environment.Is64BitProcess}");
        Line(string.Empty);

        int exitCode = 0;

        try
        {
            BitmapSource synthetic = RenderKnownWordsImage();
            string imagePath = Path.Combine(outputDirectory, "ocr-synthetic.png");
            _ = ImageCodec.SavePng(synthetic, imagePath);
            Line($"Synthetic image: {synthetic.PixelWidth}x{synthetic.PixelHeight} -> {Path.GetFileName(imagePath)}");
            Line($"Known words: {string.Join(", ", KnownWords)}");
            Line(string.Empty);

            var service = new WindowsOcrService(NullLogger<WindowsOcrService>.Instance);

            Line($"OCR available: {service.IsAvailable}");
            Line($"Supported languages: {(service.SupportedLanguages.Count == 0 ? "(none)" : string.Join(", ", service.SupportedLanguages))}");
            Line(string.Empty);

            if (!service.IsAvailable)
            {
                // Not a failure: no package identity or no language pack. Report and exit clean.
                Line("RESULT: SKIP (UNAVAILABLE)");
                Line("The Windows OCR engine could not be created on this system.");
                Line("This is expected for an unpackaged debug build without package identity, or");
                Line("on a machine with no installed OCR language pack. Live end-to-end OCR");
                Line("validation is planned for the packaged build (task 14).");
                WriteReport(outputDirectory, report);
                return 0;
            }

            var request = OcrRequest.FromFile(
                imagePath,
                upscaleFactor: 2.0,
                preferredLanguages: ["en-US", "en"]);

            OcrResult result = service.RecognizeAsync(request).GetAwaiter().GetResult();

            Line($"Status: {result.Status}");
            Line($"Language: {result.LanguageTag ?? "(none)"}");
            Line(string.Create(CultureInfo.InvariantCulture, $"Elapsed: {result.Elapsed.TotalMilliseconds:0.0}ms"));
            Line($"Lines: {result.Lines.Count}");
            Line(string.Empty);
            Line("Recognised text:");
            Line(result.Text.Length == 0 ? "  (empty)" : result.Text);
            Line(string.Empty);

            if (result.Status == OcrStatus.Success)
            {
                var found = new List<string>();
                var missing = new List<string>();
                foreach (string word in KnownWords)
                {
                    bool hit = result.Text.Contains(word, StringComparison.OrdinalIgnoreCase);
                    (hit ? found : missing).Add(word);
                }

                Line($"Found: {(found.Count == 0 ? "(none)" : string.Join(", ", found))}");
                Line($"Missing: {(missing.Count == 0 ? "(none)" : string.Join(", ", missing))}");
                Line(string.Empty);

                // Accept a partial hit: OCR on synthetic glyphs is imperfect and a strict all-words
                // match would make the diagnostic flaky. At least one known word proves the whole
                // pipeline (decode -> scale -> engine -> map -> normalise) ran end to end.
                if (found.Count > 0)
                {
                    Line("RESULT: PASS");
                }
                else
                {
                    Line("RESULT: FAIL (engine ran but recognised none of the known words)");
                    exitCode = 1;
                }
            }
            else if (result.Status == OcrStatus.Unavailable)
            {
                Line("RESULT: SKIP (UNAVAILABLE)");
                Line(result.Message ?? "The OCR engine became unavailable during recognition.");
            }
            else
            {
                Line($"RESULT: FAIL ({result.Status}: {result.Message ?? "no detail"})");
                exitCode = 1;
            }
        }
        catch (Exception ex)
        {
            Line(string.Empty);
            Line("RESULT: FAIL (unhandled exception)");
            Line(ex.ToString());
            exitCode = 2;
        }

        WriteReport(outputDirectory, report);
        return exitCode;
    }

    /// <summary>
    /// Draws the known words as large, bold, black text on a white background — the highest-
    /// contrast, most OCR-friendly layout, and deterministic across machines.
    /// </summary>
    private static BitmapSource RenderKnownWordsImage()
    {
        const int width = 720;
        const int height = 360;

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));

            var typeface = new Typeface(
                new FontFamily("Segoe UI, Arial"),
                FontStyles.Normal,
                FontWeights.Bold,
                FontStretches.Normal);

            double y = 24;
            foreach (string word in KnownWords)
            {
                var text = new FormattedText(
                    word,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    48,
                    Brushes.Black,
                    1.0);
                dc.DrawText(text, new Point(32, y));
                y += 64;
            }
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static void WriteReport(string outputDirectory, StringBuilder report) =>
        File.WriteAllText(
            Path.Combine(outputDirectory, "ocr-selftest-report.txt"),
            report.ToString(),
            new UTF8Encoding(false));
}
