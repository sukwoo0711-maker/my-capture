using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
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
/// summary row. Nothing is asserted about translation quality; the run is for human review.
/// </summary>
public sealed class PinTranslationOverlayCorpusTests(ITestOutputHelper output)
{
    internal const string CorpusVariable = "MYCAPTURE_PIN_OVERLAY_CORPUS";
    private const int PdfPages = 2;

    [PinOverlayCorpusFact]
    public Task CorpusPicturesProduceReviewablePairs() => RunAsync(async () =>
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

        foreach (string file in Directory.EnumerateFiles(input).Order(StringComparer.OrdinalIgnoreCase))
        {
            string extension = Path.GetExtension(file).ToLowerInvariant();
            IReadOnlyList<(string Name, BitmapSource Image)> pictures = extension switch
            {
                ".png" or ".jpg" or ".jpeg" => [(Path.GetFileNameWithoutExtension(file), Load(file))],
                ".pdf" => await RenderPdfAsync(file),
                _ => [],
            };
            foreach ((string name, BitmapSource before) in pictures)
            {
                var watch = Stopwatch.StartNew();
                OcrResult recognized = await ocr.RecognizeAsync(OcrRequestFactory.FromBitmap(before, settings), CancellationToken.None);
                long ocrMs = watch.ElapsedMilliseconds;
                string size = $"{before.PixelWidth}x{before.PixelHeight}";
                if (recognized.Status != OcrStatus.Success)
                {
                    summary.Append(CultureInfo.InvariantCulture, $"{name}\t{size}\t0\t0\t-\tocr:{recognized.Status}\tFalse\t{ocrMs}\t0\n");
                    continue;
                }
                int blocks = PinTranslationOverlay.BuildBlocks(recognized).Count;
                string text = recognized.Text;
                // The app translates toward its display language, so a picture is tried in every direction
                // the language decision allows: a Korean UI translates the English parts, an English UI the Korean.
                PinTranslationDirection[] directions = [.. new[] { PinTranslationDirection.EnglishToKorean, PinTranslationDirection.KoreanToEnglish }
                    .Where(candidate => PinTranslationService.Evaluate(text, candidate == PinTranslationDirection.EnglishToKorean).Availability
                        == PinTranslationAvailability.Translatable)];
                if (directions.Length == 0)
                {
                    summary.Append(CultureInfo.InvariantCulture,
                        $"{name}\t{size}\t{recognized.Lines.Count}\t{blocks}\t-\tnot-translatable\tFalse\t{ocrMs}\t0\n");
                    continue;
                }
                foreach (PinTranslationDirection direction in directions)
                {
                    var service = new PinTranslationService(
                        direction == PinTranslationDirection.EnglishToKorean ? toKorean : toEnglish, direction);
                    watch.Restart();
                    PinTranslationResult? result = await service.TranslateRecognizedAsync(
                        recognized, PinContent.FromImage(before), CancellationToken.None);
                    long translateMs = watch.ElapsedMilliseconds;
                    string kind = result is null ? "nothing" : result.Content.Kind == PinContentKind.Image ? "overlay" : "document";
                    string tag = direction == PinTranslationDirection.EnglishToKorean ? "en-ko" : "ko-en";
                    string stem = name + "." + tag;
                    if (result?.Content.Kind == PinContentKind.Image)
                    {
                        Save(directory, stem + "-after.png", result.Content.Image);
                        Save(directory, stem + "-pair.png", Pair(before, result.Content.Image));
                    }
                    if (result?.Document?.OriginalText is { } document)
                        File.WriteAllText(DiagnosticOutputPaths.Child(directory, stem + "-text.txt"), document, Encoding.UTF8);
                    summary.Append(CultureInfo.InvariantCulture,
                        $"{name}\t{size}\t{recognized.Lines.Count}\t{blocks}\t{tag}\t{kind}\t{result?.NeedsReview}\t{ocrMs}\t{translateMs}\n");
                    output.WriteLine($"{name}: {tag} {kind}, {recognized.Lines.Count} lines, {blocks} blocks, OCR {ocrMs} ms, translate {translateMs} ms");
                }
            }
        }
        File.WriteAllText(DiagnosticOutputPaths.Child(directory, "summary.tsv"), summary.ToString(), Encoding.UTF8);
    });

    private static TextTranslationService Translator(string directory, string models, HttpClient http, TranslationModelSpec model)
    {
        string modelDirectory = DiagnosticOutputPaths.Child(models, model.FolderName);
        var store = new TranslationModelStore(AppPaths.CreateForRoot(directory), http, NullLogger.Instance,
            files: model.Files, folderName: model.FolderName);
        return new TextTranslationService(store,
            () => MarianTranslator.Load(modelDirectory, model.ModelFiles, MarianTranslator.DefaultThreads),
            NullLogger.Instance, TimeSpan.FromMinutes(10));
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

    private static async Task<IReadOnlyList<(string, BitmapSource)>> RenderPdfAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
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
