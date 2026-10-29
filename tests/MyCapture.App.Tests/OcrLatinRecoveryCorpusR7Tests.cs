using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>Opt-in engine evidence using retained corpus pixels and installed offline models.</summary>
public sealed class OcrLatinRecoveryCorpusR7Tests(ITestOutputHelper output)
{
    internal const string BaselineVariable = "MYCAPTURE_PIN_OCR_R7_BASELINE";

    [OcrLatinRecoveryCorpusR7Fact]
    public void CompareWindowsAndNeuralReadingsAtSuspectTokens() => StaTestHost.Run(() =>
    {
        string baseline = DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable(BaselineVariable)!);
        string models = DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable(PinTranslationOverlayEvidenceTests.ModelsVariable)!);
        string directory = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(
            DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable(PinTranslationOverlayEvidenceTests.OutputVariable)!), "ocr-diagnosis"));
        using var http = new HttpClient(new RejectNetwork());
        using var neural = new NeuralOcrEngine(new OcrModelStore(AppPaths.CreateForRoot(models), http,
            NullLogger<OcrModelStore>.Instance), NullLogger<NeuralOcrEngine>.Instance);
        Assert.True(neural.CanRun, "The supplied offline OCR model folder must be complete; no download is permitted.");
        var windows = new WindowsOcrService(NullLogger<WindowsOcrService>.Instance);
        Assert.True(windows.IsAvailable);
        output.WriteLine("Windows language packs: " + string.Join(", ", windows.SupportedLanguages));
        var summary = new StringBuilder("name\tround6Suspects\twindowsSuspects\tneuralSuspects\tproductSuspects\tchangedWords\tneuralCalls\twallMs\n");
        foreach (string entry in Directory.EnumerateFiles(baseline, "*-source.png").Order(StringComparer.Ordinal))
        {
            string file = DiagnosticOutputPaths.Child(baseline, Path.GetFileName(entry));
            string name = Path.GetFileName(file)[..^"-source.png".Length];
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(file);
            image.EndInit();
            image.Freeze();
            OcrResult? windowsResult = null, neuralResult = null;
            int calls = 0;
            var recordingWindows = new RecordingEngine(windows, result => windowsResult = result);
            var service = new CaptureOcrService(recordingWindows, async (request, token) =>
            {
                calls++;
                OcrResult read = await neural.RecognizeAsync(request, token).ConfigureAwait(false);
                neuralResult ??= read;
                Save(calls == 1 ? "neural-full" : "neural-strip-" + (calls - 1).ToString(CultureInfo.InvariantCulture), read);
                return read;
            }, () => neural.CanRun, () => neural.IsReady, NullLogger<CaptureOcrService>.Instance);
            var watch = Stopwatch.StartNew();
            OcrResult product = service.RecognizeAsync(OcrRequestFactory.FromBitmap(image, new OcrSettings()).WithLatinRecovery())
                .GetAwaiter().GetResult();
            watch.Stop();
            Assert.NotNull(windowsResult);
            Save("windows", windowsResult);
            Save("product", product);
            if (neuralResult is not null) Save("neural", neuralResult);
            OcrResult round6 = PinTranslationOverlayCorpusTests.ReadRecognized(DiagnosticOutputPaths.Child(baseline, name + "-ocr.json"));
            int changed = windowsResult.Lines.Zip(product.Lines).Sum(pair => pair.First.Words.Zip(pair.Second.Words)
                .Count(words => words.First.Text != words.Second.Text));
            int beforeCount = OcrLatinWordRecovery.CountSuspects(windowsResult), afterCount = OcrLatinWordRecovery.CountSuspects(product);
            summary.Append(CultureInfo.InvariantCulture,
                $"{name}\t{OcrLatinWordRecovery.CountSuspects(round6)}\t{beforeCount}\t{(neuralResult is null ? -1 : OcrLatinWordRecovery.CountSuspects(neuralResult))}\t{afterCount}\t{changed}\t{calls}\t{watch.Elapsed.TotalMilliseconds:F3}\n");
            output.WriteLine($"{name}: suspect tokens {beforeCount} -> {afterCount}; changed {changed}; neural calls {calls}");

            void Save(string engine, OcrResult result) => File.WriteAllText(
                DiagnosticOutputPaths.Child(directory, name + "-" + engine + "-ocr.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }
        File.WriteAllText(DiagnosticOutputPaths.Child(directory, "summary.tsv"), summary.ToString(), Encoding.UTF8);
    });

    private sealed class RecordingEngine(IOcrService engine, Action<OcrResult> record) : IOcrService
    {
        public bool IsAvailable => engine.IsAvailable;
        public IReadOnlyList<string> SupportedLanguages => engine.SupportedLanguages;
        public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            OcrResult result = await engine.RecognizeAsync(request, cancellationToken).ConfigureAwait(false);
            record(result);
            return result;
        }
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }
}

public sealed class OcrLatinRecoveryCorpusR7FactAttribute : FactAttribute
{
    public OcrLatinRecoveryCorpusR7FactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OcrLatinRecoveryCorpusR7Tests.BaselineVariable)) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PinTranslationOverlayEvidenceTests.OutputVariable)) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PinTranslationOverlayEvidenceTests.ModelsVariable)))
            Skip = "Set MYCAPTURE_PIN_OCR_R7_BASELINE, MYCAPTURE_PIN_OVERLAY_EVIDENCE and MYCAPTURE_PIN_OVERLAY_MODELS for engine evidence.";
    }
}
