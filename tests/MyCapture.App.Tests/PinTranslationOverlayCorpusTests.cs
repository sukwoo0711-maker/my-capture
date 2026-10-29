using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// Explicit corpus run: every PNG/JPG (and the first pages of every PDF) in
/// MYCAPTURE_PIN_OVERLAY_CORPUS goes through real Windows OCR, the language decision and the real
/// model in the direction the picture needs, and leaves an after image, a side-by-side pair and a
/// summary row. An explicit c1 manifest additionally enables the measured source-area coverage gate.
/// </summary>
public sealed class PinTranslationOverlayCorpusTests(ITestOutputHelper output)
{
    internal const string CorpusVariable = "MYCAPTURE_PIN_OVERLAY_CORPUS";
    internal const string ComparisonVariable = "MYCAPTURE_PIN_OVERLAY_COMPARISON";
    private const int PdfPages = 2;

    [PinOverlayCorpusFact]
    public Task CorpusPicturesProduceReviewablePairs() => RunCorpusAsync(replayDirectory: null);

    internal Task RunCorpusAsync(string? replayDirectory) => RunAsync(async () =>
    {
        string input = Environment.GetEnvironmentVariable(CorpusVariable)!;
        string directory = DiagnosticOutputPaths.Create(
            Environment.GetEnvironmentVariable(PinTranslationOverlayEvidenceTests.OutputVariable)!);
        string models = Environment.GetEnvironmentVariable(PinTranslationOverlayEvidenceTests.ModelsVariable)!;
        Assert.False(string.IsNullOrWhiteSpace(models), "Set the model folder variable.");
        using var http = new HttpClient(new RejectingHandler());
        using TextTranslationService toKorean = Translator(directory, models, http, TranslationModelCatalog.EnglishToKorean);
        using TextTranslationService toEnglish = Translator(directory, models, http, TranslationModelCatalog.KoreanToEnglish);
        // The app's recognizer: Windows OCR plus the neural engine, with default OCR settings. The neural
        // model is read from <models>\ocr-models, a copy outside the user's profile.
        var neural = new NeuralOcrEngine(new OcrModelStore(AppPaths.CreateForRoot(models), NullLogger<OcrModelStore>.Instance),
            NullLogger<NeuralOcrEngine>.Instance);
        IOcrService ocr = new CaptureOcrService(new WindowsOcrService(NullLogger<WindowsOcrService>.Instance), neural,
            NullLogger<CaptureOcrService>.Instance);
        var settings = new MyCapture.Core.Settings.OcrSettings();
        var summary = new StringBuilder("name\tsize\tlines\tblocks\tdirection\tresult\tneedsReview\tocrMs\ttranslateMs\n");
        var decisions = new StringBuilder("name\tdirection\tversion\treason\tx\ty\twidth\theight\ttext\n");
        string? baselinePath = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusBaseline.AssemblyVariable);
        var baseline = string.IsNullOrWhiteSpace(baselinePath) ? null : new PinTranslationOverlayCorpusBaseline(baselinePath);
        if (baseline is not null) output.WriteLine("Retained baseline assembly: " + baseline.Version);
        string? manifest = Environment.GetEnvironmentVariable(PinOverlayCorpusCoverage.ManifestVariable);
        string? comparisonDirectory = Environment.GetEnvironmentVariable(ComparisonVariable);
        var coverage = string.IsNullOrWhiteSpace(manifest) ? null : new PinOverlayCorpusCoverage(manifest);
        if (coverage is not null) Assert.NotNull(baseline);
        string? translationCache = Environment.GetEnvironmentVariable(PinOverlayCorpusTranslations.CacheVariable)
            ?? (replayDirectory is null ? null : directory);

        foreach (string file in Directory.EnumerateFiles(input).Order(StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<(string Name, BitmapSource Image)> pictures = await LoadPicturesAsync(file, replayDirectory ?? comparisonDirectory);
            foreach ((string name, BitmapSource before) in pictures)
            {
                var watch = Stopwatch.StartNew();
                OcrResult recognized = replayDirectory is null
                    ? await ocr.RecognizeAsync(OcrRequestFactory.FromBitmap(before, settings).WithLatinRecovery(), CancellationToken.None)
                    : ReadRecognized(Path.Combine(replayDirectory, name + "-ocr.json"));
                long ocrMs = replayDirectory is null ? watch.ElapsedMilliseconds : 0;
                string size = $"{before.PixelWidth}x{before.PixelHeight}";
                if (recognized.Status != OcrStatus.Success)
                {
                    summary.Append(CultureInfo.InvariantCulture, $"{name}\t{size}\t0\t0\t-\tocr:{recognized.Status}\tFalse\t{ocrMs}\t0\n");
                    continue;
                }
                int blocks = PinTranslationOverlay.BuildBlocks(recognized, before).Count;
                File.WriteAllText(DiagnosticOutputPaths.Child(directory, name + "-ocr.json"),
                    JsonSerializer.Serialize(recognized, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                Save(directory, name + "-source.png", before);
                string text = recognized.Text;
                // The app translates toward its display language, so a picture is tried in every direction
                // the language decision allows: a Korean UI translates the English parts, an English UI the Korean.
                PinTranslationDirection[] directions = [.. new[] { PinTranslationDirection.EnglishToKorean, PinTranslationDirection.KoreanToEnglish }
                    .Where(candidate => PinTranslationService.Evaluate(text, candidate == PinTranslationDirection.EnglishToKorean).Availability
                        == PinTranslationAvailability.Translatable)
                    .Concat((coverage?.Directions(name) ?? []).Select(tag => tag == "en-ko"
                        ? PinTranslationDirection.EnglishToKorean : PinTranslationDirection.KoreanToEnglish)).Distinct()];
                if (directions.Length == 0)
                {
                    summary.Append(CultureInfo.InvariantCulture,
                        $"{name}\t{size}\t{recognized.Lines.Count}\t{blocks}\t-\tnot-translatable\tFalse\t{ocrMs}\t0\n");
                    continue;
                }
                foreach (PinTranslationDirection direction in directions)
                {
                    string tag = direction == PinTranslationDirection.EnglishToKorean ? "en-ko" : "ko-en";
                    string stem = name + "." + tag;
                    var translator = new PinOverlayCorpusTranslations(
                        direction == PinTranslationDirection.EnglishToKorean ? toKorean : toEnglish, directory, stem, translationCache);
                    var recovery = new List<PinTranslationRecoveryDecision>();
                    var service = new PinTranslationService(translator, direction) { ObserveRecovery = recovery.Add };
                    var baselineDecisions = new List<PinOverlayCorpusDecision>();
                    var afterDecisions = new List<PinOverlayCorpusDecision>();
                    BitmapSource? baselineImage = null;
                    if (baseline is not null)
                    {
                        OcrResult baselineRecognized = comparisonDirectory is null ? recognized
                            : ReadRecognized(Path.Combine(comparisonDirectory, name + "-ocr.json"));
                        var replay = await baseline.RunAsync(baselineRecognized, before, translator, direction);
                        baselineDecisions.AddRange(replay.Decisions);
                        foreach (var decision in replay.Decisions)
                            Decision("before", decision.Reason, decision.Bounds, decision.Text);
                        if (replay.Decisions.Count > 0)
                        {
                            baselineImage = replay.Image;
                            Save(directory, stem + (comparisonDirectory is null ? "-baseline.png" : "-r4.png"), replay.Image);
                        }
                    }
                    if (comparisonDirectory is not null)
                    {
                        string previous = Path.Combine(comparisonDirectory, stem + "-after.png");
                        baselineImage = File.Exists(previous) ? Load(previous) : before;
                        Save(directory, stem + "-baseline.png", baselineImage);
                    }
                    watch.Restart();
                    PinTranslationResult? result = await service.TranslateRecognizedAsync(
                        recognized, PinContent.FromImage(before), CancellationToken.None,
                        (block, reason) =>
                        {
                            Decision("after", reason, block.Bounds, block.Text);
                            afterDecisions.Add(new(block.Text, block.Bounds, reason,
                                block.Lines.Sum(line => line.Bounds.Width * line.Bounds.Height)));
                        });
                    coverage?.Add(name, tag, baselineDecisions, afterDecisions);
                    File.WriteAllText(DiagnosticOutputPaths.Child(directory, stem + "-recovery.json"),
                        JsonSerializer.Serialize(recovery, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                    translator.Save();
                    long translateMs = watch.ElapsedMilliseconds;
                    string kind = result is null ? "nothing" : result.Content.Kind == PinContentKind.Image ? "overlay" : "document";
                    if (result?.Content.Kind == PinContentKind.Image)
                    {
                        Save(directory, stem + "-after.png", result.Content.Image);
                        Save(directory, stem + "-pair.png", Pair(before, result.Content.Image));
                        if (baselineImage is not null)
                            Save(directory, stem + "-change.png", Pair(baselineImage, result.Content.Image));
                    }
                    if (result?.Document?.OriginalText is { } document)
                        File.WriteAllText(DiagnosticOutputPaths.Child(directory, stem + "-text.txt"), document, Encoding.UTF8);
                    summary.Append(CultureInfo.InvariantCulture,
                        $"{name}\t{size}\t{recognized.Lines.Count}\t{blocks}\t{tag}\t{kind}\t{result?.NeedsReview}\t{ocrMs}\t{translateMs}\n");
                    output.WriteLine($"{name}: {tag} {kind}, {recognized.Lines.Count} lines, {blocks} blocks, OCR {ocrMs} ms, translate {translateMs} ms");
                    output.WriteLine($"  exact translation units: cached={translator.CachedUnits}, local model={translator.ModelUnits}");

                    void Decision(string version, string reason, OcrRect bounds, string source) =>
                        decisions.Append(CultureInfo.InvariantCulture,
                            $"{name}\t{tag}\t{version}\t{reason}\t{bounds.X:F3}\t{bounds.Y:F3}\t{bounds.Width:F3}\t{bounds.Height:F3}\t{source.Replace('\t', ' ').ReplaceLineEndings(" ")}\n");
                }
            }
        }
        File.WriteAllText(DiagnosticOutputPaths.Child(directory, "summary.tsv"), summary.ToString(), Encoding.UTF8);
        File.WriteAllText(DiagnosticOutputPaths.Child(directory, "decisions.tsv"), decisions.ToString(), Encoding.UTF8);
        if (replayDirectory is not null)
            await ProbeRejectedW9Async(replayDirectory, directory, translationCache, toKorean);
        coverage?.WriteAndAssert(directory, output);
    });

    private async Task ProbeRejectedW9Async(string replayDirectory, string directory, string? cacheDirectory, ITextTranslator fallback)
    {
        string[] rejected = File.ReadLines(Path.Combine(replayDirectory, "decisions.tsv")).Skip(1)
            .Select(line => line.Split('\t')).Where(fields => fields[0] == "pdf-en-irs-w9-p1" && fields[1] == "en-ko"
                && fields[2] == "after" && fields[3] == "all-rejected").Select(fields => fields[8]).ToArray();
        string[] units = rejected.SelectMany(source => new[] { source, OcrLatinTextRepair.Repair(source) })
            .SelectMany(TranslationSegmenter.Sentences).Distinct(StringComparer.Ordinal).ToArray();
        var translator = new PinOverlayCorpusTranslations(fallback, directory, "pdf-en-irs-w9-p1.en-ko-r5-probe", cacheDirectory);
        IReadOnlyList<TranslatedText> translated = await translator.TranslateAsync(units, null, CancellationToken.None);
        MethodInfo preserves = typeof(PinTranslationService).GetMethod("PreservesProtectedValues", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo count = typeof(PinTranslationService).GetMethod("CountOccurrences", BindingFlags.Static | BindingFlags.NonPublic)!;
        var additional = (System.Text.RegularExpressions.Regex)typeof(PinTranslationService)
            .GetMethod("AdditionalLiteral", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        var records = translated.Select(item => new
        {
            item.Source, item.Text, item.Missing, item.Invented, item.NeedsReview,
            PreservesProtectedValues = (bool)preserves.Invoke(null, [item.Source, item.Text])!,
            MismatchedCounts = ProtectedTokens.Find(item.Source).Select(span => span.Text)
                .Concat(additional.Matches(item.Source).Select(match => match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}')))
                .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal)
                .Select(value => new
                {
                    Value = value,
                    SourceCount = (int)count.Invoke(null, [item.Source, value])!,
                    TranslatedCount = (int)count.Invoke(null, [item.Text, value])!,
                }).Where(value => value.SourceCount != value.TranslatedCount).ToArray(),
            TokenCheck = ProtectedTokens.Check(item.Source, item.Text),
        }).ToArray();
        File.WriteAllText(DiagnosticOutputPaths.Child(directory, "w9-rejected-probe.json"),
            JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        foreach (var record in records.Where(item => item.Source.StartsWith("Request for", StringComparison.Ordinal)
            || item.Source.StartsWith("Part II", StringComparison.Ordinal)))
            output.WriteLine($"W9 PROBE {JsonSerializer.Serialize(record)}");
    }

    internal static OcrResult ReadRecognized(string path)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = json.RootElement;
        OcrLine[] lines = root.GetProperty("Lines").Deserialize<OcrLine[]>()!;
        double? angle = root.TryGetProperty("TextAngle", out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble() : null;
        return OcrResult.Success(root.GetProperty("Text").GetString()!, root.GetProperty("LanguageTag").GetString()!,
            lines, TimeSpan.Zero, angle);
    }

    private static TextTranslationService Translator(string directory, string models, HttpClient http, TranslationModelSpec model)
    {
        string modelDirectory = DiagnosticOutputPaths.Child(models, model.FolderName);
        var store = new TranslationModelStore(AppPaths.CreateForRoot(directory), http, NullLogger.Instance,
            files: model.Files, folderName: model.FolderName);
        return new TextTranslationService(store,
            () => MarianTranslator.Load(modelDirectory, model.ModelFiles, MarianTranslator.DefaultThreads),
            NullLogger.Instance, TimeSpan.FromMinutes(10));
    }

    internal static async Task<IReadOnlyList<(string Name, BitmapSource Image)>> LoadPicturesAsync(
        string file, string? replayDirectory)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        string extension = Path.GetExtension(file).ToLowerInvariant();
        string? SavedSource(string pictureName)
        {
            string? path = replayDirectory is null ? null : Path.Combine(replayDirectory, pictureName + "-source.png");
            return path is not null && File.Exists(path) ? path : null;
        }

        if (extension is ".png" or ".jpg" or ".jpeg")
            return [(name, Load(SavedSource(name) ?? file))];
        if (extension != ".pdf") return [];
        if (replayDirectory is not null)
        {
            string[] recordedPages = Enumerable.Range(1, PdfPages).Select(page => $"{name}-p{page}")
                .Where(page => File.Exists(Path.Combine(replayDirectory, page + "-ocr.json"))).ToArray();
            // A PDF render can vary by a few pixels. Replay the recorded bitmap whenever it
            // exists so layout-only fixes are measured against the actual OCR run's pixels.
            if (recordedPages.Length > 0 && recordedPages.All(page => SavedSource(page) is not null))
                return recordedPages.Select(page => (page, Load(SavedSource(page)!))).ToArray();
        }

        IReadOnlyList<(string Name, BitmapSource Image)> rendered = await RenderPdfAsync(file);
        return rendered.Select(page => (page.Name,
            SavedSource(page.Name) is { } saved ? Load(saved) : page.Image)).ToArray();
    }

    private static BitmapSource Load(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    internal static async Task<IReadOnlyList<(string, BitmapSource)>> RenderPdfAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        PdfDocument document = await PdfDocument.LoadFromFileAsync(file);
        var pages = new List<(string, BitmapSource)>();
        for (uint index = 0; index < Math.Min(document.PageCount, PdfPages); index++)
        {
            using PdfPage page = document.GetPage(index);
            using var stream = new InMemoryRandomAccessStream();
            // About 150 DPI: a PDF page is 72 points per inch.
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = (uint)Math.Round(page.Size.Width * 150 / 72) });
            stream.Seek(0);
            var decoder = BitmapDecoder.Create(stream.AsStream(), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            frame.Freeze();
            pages.Add(($"{Path.GetFileNameWithoutExtension(path)}-p{index + 1}", frame));
        }
        return pages;
    }

    private static BitmapSource Pair(BitmapSource before, BitmapSource after)
    {
        const double maxWidth = 1800;
        double scale = Math.Min(1, maxWidth / (before.PixelWidth * 2 + 12));
        double width = before.PixelWidth * scale, height = before.PixelHeight * scale;
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.Red, null, new Rect(0, 0, width * 2 + 12, height));
            context.DrawImage(before, new Rect(0, 0, width, height));
            context.DrawImage(after, new Rect(width + 12, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * 2 + 12), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Save(string directory, string name, BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }

    private static Task RunAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = WpfTestThread.Create(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(true); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }
}

public sealed class PinOverlayCorpusFactAttribute : FactAttribute
{
    public PinOverlayCorpusFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusTests.CorpusVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PinTranslationOverlayEvidenceTests.OutputVariable)))
            Skip = "Set MYCAPTURE_PIN_OVERLAY_CORPUS and MYCAPTURE_PIN_OVERLAY_EVIDENCE to run the review corpus.";
    }
}
