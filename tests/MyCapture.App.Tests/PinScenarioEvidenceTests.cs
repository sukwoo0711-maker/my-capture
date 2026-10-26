using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.App.Threading;
using MyCapture.Core.Localization;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

// Runs only when MYCAPTURE_PIN_SCENARIOS names an output folder. It pins each sample through the same
// clipboard-reading and translation code a real F3 uses (a synthetic data object, never the Windows
// clipboard), translates with the real on-disk models of the profile named by MYCAPTURE_PIN_PROFILE
// (read only), checks the format invariants and writes the results, timings and PNGs.
public sealed class PinScenarioEvidenceTests
{
    private const string DefaultSamples = @"C:\GitRepositories\my-capture\work\pin-content-20261027\test-samples";

    private sealed record Scenario(string Id, string File, bool TargetKorean, string Kind);

    private static readonly Scenario[] Scenarios =
    [
        new("T1", "T1-pdf-copied-lines.txt", true, "text"),
        new("T2", "T2-english-screen.png", true, "image"),
        new("T3", "T3-korean-document.png", false, "image"),
        new("T4", "T4-json-nested.json", true, "text"),
        new("T5-cs", "T5-comments.cs", true, "text"),
        new("T5-py", "T5-comments.py", true, "text"),
        new("T5-js", "T5-comments.js", true, "text"),
        new("T6", "T6-markdown-english.md", true, "text"),
        new("T7", "T7-mermaid-flowchart-en.mmd", true, "text"),
        new("T8", "A5-excel-table.tsv", true, "text"),
        new("T9", "T9-article-copied.txt", true, "text"),
        new("T10", "T10-mixed-ko-en.txt", true, "text"),
        new("T11", "T11-korean-paragraph.txt", false, "text"),
        new("T12", "T11-korean-paragraph.txt", true, "text"),
        new("T13", "T13-japanese.txt", true, "text"),
        new("T14", "T14-protected-tokens.txt", true, "text"),
        new("T15", "T15-long-english-2000.txt", true, "text"),
        new("A6-ko", "A6-korean-two-lines.txt", true, "text"),
        new("A6-en", "A6-plain-english-two-lines.txt", true, "text"),
    ];

    [Fact]
    public Task RunScenariosWithTheRealModels()
    {
        string? output = Environment.GetEnvironmentVariable("MYCAPTURE_PIN_SCENARIOS");
        string? profile = Environment.GetEnvironmentVariable("MYCAPTURE_PIN_PROFILE");
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(profile)) return Task.CompletedTask;
        string samples = Environment.GetEnvironmentVariable("MYCAPTURE_SAMPLES") ?? DefaultSamples;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = WpfTestThread.Create(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await RunAsync(output, profile, samples); completion.TrySetResult(true); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task RunAsync(string output, string profile, string samples)
    {
        Directory.CreateDirectory(output);
        AppPaths paths = AppPaths.CreateForRoot(profile);
        var enKoStore = new TranslationModelStore(paths, NullLogger<TranslationModelStore>.Instance);
        using var enKo = new TextTranslationService(enKoStore, NullLogger<TextTranslationService>.Instance);
        var koEnStore = new TranslationModelStore(paths, NullLogger<TranslationModelStore>.Instance, TranslationModelCatalog.KoreanToEnglish);
        using var koEn = new TextTranslationService(koEnStore, NullLogger<TextTranslationService>.Instance,
            TranslationModelCatalog.KoreanToEnglish.ModelFiles);
        Assert.True(await enKo.IsModelInstalledAsync(CancellationToken.None), "English-to-Korean model is not installed in the profile.");
        Assert.True(await koEn.IsModelInstalledAsync(CancellationToken.None), "Korean-to-English model is not installed in the profile.");
        var ocr = new WindowsOcrService(NullLogger<WindowsOcrService>.Instance);
        var results = new List<Dictionary<string, object?>>();
        foreach (Scenario scenario in Scenarios)
        {
            string path = Path.Combine(samples, scenario.File);
            if (!File.Exists(path)) continue;
            using IDisposable language = UiText.UseLanguage(scenario.TargetKorean ? "ko-KR" : "en-US");
            var row = new Dictionary<string, object?> { ["id"] = scenario.Id, ["file"] = scenario.File, ["targetKorean"] = scenario.TargetKorean };
            try { await RunScenarioAsync(scenario, path, output, enKo, koEn, ocr, row); }
            catch (Exception ex) { row["error"] = ex.GetType().Name + ": " + ex.Message; }
            results.Add(row);
        }
        File.WriteAllText(Path.Combine(output, "scenarios.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            new UTF8Encoding(false));
    }

    private static async Task RunScenarioAsync(Scenario scenario, string path, string output, TextTranslationService enKo,
        TextTranslationService koEn, IOcrService ocr, Dictionary<string, object?> row)
    {
        PinContent pin;
        string? recognized = null;
        if (scenario.Kind == "image")
        {
            BitmapSource image = LoadImage(path);
            pin = PinContent.FromImage(image);
            var clock = Stopwatch.StartNew();
            recognized = await PinTranslationEngines.RecognizeAsync(ocr, image, new MyCapture.Core.Settings.OcrSettings(), CancellationToken.None);
            row["ocrMs"] = Math.Round(clock.Elapsed.TotalMilliseconds);
            row["recognized"] = recognized;
        }
        else
        {
            string text = File.ReadAllText(path, new UTF8Encoding(false));
            pin = await ReadPinAsync(text);
            row["kind"] = pin.Kind.ToString();
            row["textStyle"] = pin.TextStyle?.ToString();
        }
        row["source"] = pin.OriginalText ?? recognized;
        bool korean = scenario.TargetKorean;
        PinTranslationDecision decision = recognized is not null
            ? PinTranslationService.Evaluate(recognized, korean) : PinTranslationService.Evaluate(pin, korean);
        row["availability"] = decision.Availability.ToString();
        row["direction"] = decision.Direction.ToString();
        if (decision.Availability != PinTranslationAvailability.Translatable) return;

        ITextTranslator translator = decision.Direction == PinTranslationDirection.EnglishToKorean ? enKo : koEn;
        var service = new PinTranslationService(translator, decision.Direction);
        var watch = Stopwatch.StartNew();
        PinTranslationResult? result = recognized is null
            ? await service.TranslateAsync(pin, CancellationToken.None)
            : await service.TranslateRecognizedAsync(recognized, pin, CancellationToken.None);
        row["translateMs"] = Math.Round(watch.Elapsed.TotalMilliseconds);
        if (result is null) { row["result"] = "null"; return; }
        string translated = result.Content.OriginalText ?? string.Empty;
        row["needsReview"] = result.NeedsReview;
        row["translated"] = translated;
        row["translatedKind"] = result.Content.TextStyle?.ToString();
        string source = pin.OriginalText ?? recognized ?? string.Empty;
        row["invariants"] = Invariants(source, translated, pin.IsMarkdown, decision.Direction);
        Save(result.Content.Image, Path.Combine(output, scenario.Id + "-translated.png"));
        Save(pin.Image, Path.Combine(output, scenario.Id + "-source.png"));
    }

    private static Dictionary<string, object?> Invariants(string source, string translated, bool markdown, PinTranslationDirection direction)
    {
        var checks = new Dictionary<string, object?>();
        // Protected values (numbers, units, dates, URLs, e-mails, paths, versions, placeholders) survive unchanged.
        var missing = new List<string>();
        foreach (string value in ProtectedTokens.Find(source).Select(static span => span.Text).Distinct(StringComparer.Ordinal))
        {
            if (!translated.Contains(value, StringComparison.Ordinal)) missing.Add(value);
        }
        checks["protectedMissing"] = missing;
        string trimmed = source.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using JsonDocument a = JsonDocument.Parse(source);
                using JsonDocument b = JsonDocument.Parse(translated);
                checks["jsonValid"] = true;
                checks["jsonSameShape"] = Shape(a.RootElement) == Shape(b.RootElement);
            }
            catch (JsonException) { checks["jsonValid"] = false; }
        }
        checks["lineCountSource"] = source.Split('\n').Length;
        checks["lineCountTranslated"] = translated.Split('\n').Length;
        if (markdown)
        {
            checks["fenceLinesSame"] = Fences(source) == Fences(translated);
        }
        return checks;
    }

    private static string Fences(string text) => string.Join("|", text.Split('\n').Select(static line => line.Trim()).Where(static line => line.StartsWith("```", StringComparison.Ordinal)));

    private static string Shape(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().Select(static property => property.Name + ":" + Shape(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Shape)) + "]",
        JsonValueKind.String => "s",
        _ => element.GetRawText(),
    };

    private static async Task<PinContent> ReadPinAsync(string text)
    {
        // A text-only clipboard like Notepad's: Unicode text, no HTML, no picture.
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        ClipboardImageReader.CapturedAttempt captured = ClipboardImageReader.CaptureFrom(data, 1, static () => null);
        ClipboardImageReader.PinReadAttempt attempt = await ClipboardImageReader.CompletePinAttemptAsync(
            captured, static _ => null,
            static (source, grid) => StaThreadTask.RunAsync(() => ClipboardTextRenderer.Render(source, grid), "scenario text"),
            _ => Task.FromResult(captured), renderMarkdown: null);
        Assert.NotNull(attempt.Content);
        return attempt.Content!;
    }

    private static BitmapSource LoadImage(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
