using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// How long the real reading of a dense 4K picture goes on after its pin is cancelled or closed. Runs only with
/// MYCAPTURE_PIN_TRANSLATION_TAIL_OUT (output directory) and MYCAPTURE_PIN_OVERLAY_MODELS (read-only parent of the
/// OCR models); the translator is a fake, so no translation model or network is used.
/// </summary>
public sealed class PinTranslationReadingTailTests : KoreanCaptionTest
{
    private const string OutputVariable = "MYCAPTURE_PIN_TRANSLATION_TAIL_OUT";

    [Fact]
    public Task CancelAndCloseDuringTheReadingOfADense4KPicture() => RunAsync(async () =>
    {
        string? requested = Environment.GetEnvironmentVariable(OutputVariable);
        string? modelsVariable = Environment.GetEnvironmentVariable(PinTranslationOverlayEvidenceTests.ModelsVariable);
        if (string.IsNullOrWhiteSpace(requested) || string.IsNullOrWhiteSpace(modelsVariable)) return;
        string output = DiagnosticOutputPaths.Create(requested);
        string models = DiagnosticOutputPaths.ExistingDirectory(modelsVariable);
        using var neural = new NeuralOcrEngine(new OcrModelStore(AppPaths.CreateForRoot(models), NullLogger<OcrModelStore>.Instance),
            NullLogger<NeuralOcrEngine>.Instance);
        var ocr = new TimedOcr(new CaptureOcrService(new WindowsOcrService(NullLogger<WindowsOcrService>.Instance), neural,
            NullLogger<CaptureOcrService>.Instance));
        var lines = new List<string> { "run\tend\treadingMs\tworkAfterMs\trecognizerAfterMs\tengineAfterMs\ttokenCancelled\tstate\tfeedback" };
        foreach (string end in new[] { "full", "cancel", "close", "cancel", "close" })
        {
            var manager = new PinManager(() => new PinSettings(), NullLogger.Instance)
            {
                ShowPinForTest = static (_, _) => { },
                ActivatePinForTest = static _ => { },
            };
            manager.ConfigureTranslation(new InstalledTranslator());
            long recognizerEnded = 0;
            ocr.Ended = 0;
            bool tokenCancelled = false;
            manager.ConfigureImageTranslation(async (image, windowsReading, token) =>
            {
                try { return await PinTranslationEngines.RecognizeResultAsync(ocr, image, new OcrSettings(), token, windowsReading); }
                finally
                {
                    tokenCancelled = token.IsCancellationRequested;
                    Interlocked.Exchange(ref recognizerEnded, Stopwatch.GetTimestamp());
                }
            });
            try
            {
                // A new picture each run: the pin's recognition memory would otherwise reuse a reading.
                PinWindow pin = manager.PinImage(DenseArticle(lines.Count));
                long started = Stopwatch.GetTimestamp();
                pin.InvokeTranslationAction();
                if (end == "full")
                {
                    await pin.TranslationWork.WaitAsync(TimeSpan.FromMinutes(3));
                    lines.Add(string.Join('\t', lines.Count, end, Ms(started, Interlocked.Read(ref recognizerEnded)), 0, 0, 0,
                        tokenCancelled, pin.TranslationState, pin.FeedbackForTest.Text));
                    continue;
                }
                await Task.Delay(3000);
                long stopped = Stopwatch.GetTimestamp();
                if (end == "cancel") pin.InvokeTranslationAction();
                else pin.Close();
                await pin.TranslationWork.WaitAsync(TimeSpan.FromMinutes(3));
                long workEnded = Stopwatch.GetTimestamp();
                var clock = Stopwatch.StartNew();
                while ((Interlocked.Read(ref recognizerEnded) == 0 || ocr.Ended == 0) && clock.Elapsed < TimeSpan.FromMinutes(3))
                    await Task.Delay(20);
                lines.Add(string.Join('\t', lines.Count, end, Ms(started, stopped), Ms(stopped, workEnded),
                    Ms(stopped, Interlocked.Read(ref recognizerEnded)), Ms(stopped, ocr.Ended), tokenCancelled, pin.TranslationState, pin.FeedbackForTest.Text));
            }
            finally { manager.CloseAll(); }
        }
        await File.WriteAllLinesAsync(DiagnosticOutputPaths.Child(output, "reading-tail.tsv"), lines, Encoding.UTF8);
    });

    private static long Ms(long from, long to) => to == 0 ? -1 : (long)Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;

    private static readonly string[] Words = ["the", "report", "window", "saves", "team", "before", "after", "meeting",
        "document", "settings", "customer", "update", "network", "slowly", "every", "morning", "shared", "folder"];

    /// <summary>Lines of English prose filling a 3840x2160 page at 18 px, like a dense article screenshot.</summary>
    private static BitmapSource DenseArticle(int seed)
    {
        var random = new Random(seed);
        var visual = new DrawingVisual();
        var typeface = new Typeface("Segoe UI");
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 3840, 2160));
            for (double y = 30; y < 2160 - 40; y += 29)
            {
                var line = new StringBuilder();
                while (line.Length < 330)
                {
                    line.Append(line.Length == 0 ? "The" : Words[random.Next(Words.Length)]);
                    line.Append(random.Next(9) == 0 ? ". " : " ");
                }
                drawing.DrawText(new FormattedText(line.ToString().TrimEnd(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, 18, Brushes.Black, 1), new Point(40, y));
            }
        }
        var bitmap = new RenderTargetBitmap(3840, 2160, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
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

    /// <summary>The engine call itself: when it really ends, after the pin and its recognition memory stopped waiting.</summary>
    private sealed class TimedOcr(IOcrService inner) : IOcrService
    {
        private long _ended;
        internal long Ended { get => Interlocked.Read(ref _ended); set => Interlocked.Exchange(ref _ended, value); }
        public bool IsAvailable => inner.IsAvailable;
        public IReadOnlyList<string> SupportedLanguages => inner.SupportedLanguages;
        public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            try { return await inner.RecognizeAsync(request, cancellationToken); }
            finally { Ended = Stopwatch.GetTimestamp(); }
        }
    }

    /// <summary>An installed model that returns every sentence unchanged; the reading, not the translation, is measured.</summary>
    private sealed class InstalledTranslator : ITextTranslator
    {
        public long ModelDownloadBytes => 1;
        public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The model is installed.");
        public Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
            IProgress<TranslationProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TranslatedText>>([.. sources.Select(source => new TranslatedText(source, "번역된 문장입니다.", [], []))]);
    }
}
