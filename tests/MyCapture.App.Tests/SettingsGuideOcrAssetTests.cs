using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Ocr;
using MyCapture.App.Settings;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class SettingsGuideOcrAssetTests
{
    [Fact]
    public Task GenerateOcrAssets() => SettingsThemePreviewTests.RunIsolated(nameof(GenerateOcrAssets), () =>
    {
        string? root = Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_GUIDES_SOURCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        if (Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_GUIDES_OCR_REUSE_RESULTS") == "1")
        {
            RenderRecordedResults(root);
            return;
        }
        string evidence = Path.Combine(root, "work", "settings-guides-tray", "ocr");
        string output = Path.Combine(root, "src", "MyCapture.App", "Assets", "SettingsGuides");
        string? modelRoot = Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_GUIDES_OCR_MODELS");
        bool accurateOnly = Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_GUIDES_OCR_ACCURATE_ONLY") == "1";
        Directory.CreateDirectory(evidence); Directory.CreateDirectory(output);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = SettingsGuideAssetTests.Resources() };
        var receipts = new List<object>();
        var failures = new List<string>();
        try
        {
            var windows = new WindowsOcrService(NullLogger<WindowsOcrService>.Instance);
            AppPaths paths = AppPaths.CreateForRoot(evidence);
            if (!string.IsNullOrWhiteSpace(modelRoot))
            {
                Assert.True(Path.IsPathFullyQualified(modelRoot));
                // Override only this test instance's model path; leave all output under evidence.
                paths = (AppPaths)Activator.CreateInstance(typeof(AppPaths), BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null, args: [paths.DataRoot, paths.CapturesRoot, paths.QuickSaveRoot, modelRoot, paths.TranslationModelsRoot], culture: null)!;
                // The engine's background verifier also checks retired files. Do not let it clean an external fixture.
                Assert.DoesNotContain(OcrModelCatalog.LegacySuperResolutionFiles, name => File.Exists(Path.Combine(modelRoot, name)));
            }
            // Never download inside an evidence test or inspect the real user's model store.
            var required = OcrModelCatalog.Required.Concat(OcrModelCatalog.Optional).ToArray();
            bool modelsReady = required.All(file => File.Exists(Path.Combine(paths.OcrModelsRoot, file.FileName)) &&
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(paths.OcrModelsRoot, file.FileName)))) == file.ExpectedSha256);
            foreach (string language in new[] { "ko", "en" })
            {
                using var culture = UiText.UseLanguage(language);
                var sample = new TextBlock { Text = SourceText(language), FontSize = 14, Margin = new Thickness(20) };
                sample.FontFamily = new FontFamily("Malgun Gothic"); sample.Foreground = Brushes.Black;
                BitmapSource source = SettingsGuideAssetTests.Snapshot(new Border { Background = Brushes.White, Child = sample }, 400, 140);
                SettingsGuideAssetTests.Write(source, Path.Combine(evidence, $"source-{language}.png"));
                foreach (OcrQuality quality in accurateOnly ? [OcrQuality.Accurate] : new[] { OcrQuality.Fast, OcrQuality.Balanced, OcrQuality.Accurate })
                {
                    if (quality == OcrQuality.Accurate && !modelsReady)
                    {
                        receipts.Add(new { language, quality = quality.ToString(), status = "NOT_RUN", reason = "Verified Korean recognition, dictionary and detection models are absent from the configured model folder." });
                        failures.Add($"{language} Accurate: verified models are missing.");
                        continue;
                    }
                    var settings = new OcrSettings { Quality = quality, PreferredLanguages = [language == "ko" ? "ko-KR" : "en-US"] };
                    var request = OcrRequestFactory.FromBitmap(source, settings);
                    OcrResult result;
                    OcrResult? neuralResult = null;
                    if (quality == OcrQuality.Accurate)
                    {
                        using var http = new HttpClient(new NoDownloadHandler());
                        var store = new OcrModelStore(paths, http, NullLogger<OcrModelStore>.Instance);
                        using var neural = new NeuralOcrEngine(store, NullLogger<NeuralOcrEngine>.Instance);
                        neural.ModelVerificationTask.GetAwaiter().GetResult();
                        var service = new CaptureOcrService(windows,
                            async (input, cancellation) => neuralResult = await neural.RecognizeAsync(input, cancellation),
                            () => neural.CanRun, () => neural.IsReady, NullLogger<CaptureOcrService>.Instance);
                        result = service.RecognizeAsync(request).GetAwaiter().GetResult();
                    }
                    else result = windows.RecognizeAsync(request).GetAwaiter().GetResult();
                    receipts.Add(new { language, quality = quality.ToString(), status = result.Status.ToString(), result.Text, result.LanguageTag, result.Message,
                        elapsedMs = result.Elapsed.TotalMilliseconds, neuralStatus = neuralResult?.Status.ToString(), neuralText = neuralResult?.Text });
                    if (quality == OcrQuality.Accurate && neuralResult?.Status != OcrStatus.Success)
                    {
                        failures.Add($"{language} Accurate: PP-OCR did not succeed ({neuralResult?.Status}); no fallback image is accepted.");
                        continue;
                    }
                    if (result.Status != OcrStatus.Success) { failures.Add($"{language} {quality}: {result.Status}: {result.Message}"); continue; }
                    WriteComparison(output, language, quality, RenderComparison(source, SourceText(language), result.Text));
                }
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(evidence, accurateOnly ? "results-accurate.json" : "results.json"), JsonSerializer.Serialize(receipts, new JsonSerializerOptions { WriteIndented = true }));
            application.Shutdown();
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }, typeof(SettingsGuideOcrAssetTests));

    private static string SourceText(string language) => language == "ko"
        ? "검토 일정 10:30\n준비 자료 24개\n합계 128,000원"
        : "Review at 10:30\n24 documents ready\nTotal 128,000 KRW";

    private static void RenderRecordedResults(string root)
    {
        string evidence = Path.Combine(root, "work", "settings-guides-tray", "ocr");
        string output = Path.Combine(root, "src", "MyCapture.App", "Assets", "SettingsGuides");
        using var windowsResults = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "results.json")));
        using var accurateResults = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "results-accurate.json")));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = SettingsGuideAssetTests.Resources() };
        try
        {
            foreach (string language in new[] { "ko", "en" })
            {
                using var culture = UiText.UseLanguage(language);
                using var sourceFile = File.OpenRead(Path.Combine(evidence, $"source-{language}.png"));
                BitmapSource source = new PngBitmapDecoder(sourceFile, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                foreach (OcrQuality quality in new[] { OcrQuality.Fast, OcrQuality.Balanced, OcrQuality.Accurate })
                {
                    var records = quality == OcrQuality.Accurate ? accurateResults : windowsResults;
                    var receipt = records.RootElement.EnumerateArray().Single(entry =>
                        entry.GetProperty("language").GetString() == language && entry.GetProperty("quality").GetString() == quality.ToString());
                    Assert.Equal("Success", receipt.GetProperty("status").GetString());
                    if (quality == OcrQuality.Accurate) Assert.Equal("Success", receipt.GetProperty("neuralStatus").GetString());
                    WriteComparison(output, language, quality, RenderComparison(source, SourceText(language), receipt.GetProperty("Text").GetString()!));
                }
            }
        }
        finally { application.Shutdown(); }
    }

    private static void WriteComparison(string output, string language, OcrQuality quality, BitmapSource render)
    {
        if (quality == OcrQuality.Fast)
        {
            foreach (var comparison in SettingsGuideCatalog.Get("G09").Comparisons)
                SettingsGuideAssetTests.Write(render, Path.Combine(output, comparison.AssetFile(language, "before")));
        }
        else
        {
            var comparison = SettingsGuideCatalog.Get("G09").Comparisons[quality == OcrQuality.Balanced ? 0 : 1];
            SettingsGuideAssetTests.Write(render, Path.Combine(output, comparison.AssetFile(language, "after")));
        }
    }

    private static BitmapSource RenderComparison(BitmapSource source, string expected, string recognized)
    {
        // Keep the original source pixels. At guide size they appear at 17.5 px text size;
        // the recorded result is 18 px. No OCR text is corrected or replaced here.
        var crop = new CroppedBitmap(source, new Int32Rect(18, 18, 330, 64));
        var visual = new DrawingVisual();
        Brush primary = (Brush)Application.Current.Resources["Text.Primary"];
        Brush secondary = (Brush)Application.Current.Resources["Text.Secondary"];
        var underline = new Pen((Brush)Application.Current.Resources["Accent.Default"], 1.5);
        FormattedText Text(string text, double size, Brush brush) => new(text, UiText.Culture, FlowDirection.LeftToRight,
            new Typeface("Malgun Gothic"), size, brush, 1);
        var differences = CompareCharacters(expected, recognized);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 440, 280));
            dc.DrawText(Text(UiText.Get("Crop.Original"), 14, secondary), new Point(16, 12));
            dc.DrawImage(crop, new Rect(16, 42, 412.5, 80));
            dc.DrawText(Text(UiText.Get("Text_B75AA3DC8AFB"), 14, secondary), new Point(16, 148));
            int offset = 0, row = 0;
            foreach (string line in recognized.Split('\n'))
            {
                double y = 176 + row++ * 26;
                double At(int index) => 16 + Text(line[..index], 18, primary).WidthIncludingTrailingWhitespace;
                dc.DrawText(Text(line, 18, primary), new Point(16, y));
                for (int i = 0; i < line.Length; i++)
                    if (differences.Changed[offset + i])
                        dc.DrawLine(underline, new Point(At(i), y + 23), new Point(At(i + 1), y + 23));
                // A missing character has no glyph to underline. Mark its exact gap instead,
                // without adding a character or changing the recorded text's spacing.
                foreach (int gap in differences.MissingAt.Where(index => index >= offset && index <= offset + line.Length))
                    dc.DrawLine(underline, new Point(At(gap - offset) - 2.5, y + 23), new Point(At(gap - offset) + 2.5, y + 23));
                offset += line.Length + 1;
            }
        }
        var bitmap = new RenderTargetBitmap(440, 280, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }

    private static (bool[] Changed, int[] MissingAt) CompareCharacters(string expected, string actual)
    {
        var costs = new int[expected.Length + 1, actual.Length + 1];
        for (int i = 0; i <= expected.Length; i++) costs[i, 0] = i;
        for (int j = 0; j <= actual.Length; j++) costs[0, j] = j;
        for (int i = 1; i <= expected.Length; i++)
        for (int j = 1; j <= actual.Length; j++)
            costs[i, j] = Math.Min(costs[i - 1, j - 1] + (expected[i - 1] == actual[j - 1] ? 0 : 1),
                Math.Min(costs[i - 1, j] + 1, costs[i, j - 1] + 1));
        var changed = new bool[actual.Length]; var missing = new List<int>();
        int x = expected.Length, y = actual.Length;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && expected[x - 1] == actual[y - 1]) { x--; y--; }
            else if (x > 0 && y > 0 && costs[x, y] == costs[x - 1, y - 1] + 1) { changed[--y] = true; x--; }
            else if (x > 0 && costs[x, y] == costs[x - 1, y] + 1) { missing.Add(y); x--; }
            else changed[--y] = true;
        }
        return (changed, missing.Distinct().Order().ToArray());
    }

    [Theory]
    [InlineData("Review", "Revtew", "000100", "")]
    [InlineData("10:30", "1030", "0000", "2")]
    [InlineData("일정 10:30", "일정10:30", "0000000", "2")]
    [InlineData("Review at 10:30", "Review at 10:30", "000000000000000", "")]
    [InlineData("24개", "24X개", "0010", "")]
    public void DifferenceMarksPreserveSubstitutionsInsertionsAndMissingPositions(string expected, string actual, string changed, string missing)
    {
        var result = CompareCharacters(expected, actual);
        Assert.Equal(changed, string.Concat(result.Changed.Select(value => value ? '1' : '0')));
        Assert.Equal(missing, string.Join(",", result.MissingAt));
    }

    private sealed class NoDownloadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Guide asset generation uses verified local models only.");
    }
}
