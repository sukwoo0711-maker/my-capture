using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class NeuralOcrEngineLiveTests(ITestOutputHelper output)
{
    [NeuralOcrLiveFact]
    public void RealKoreanModel_InitializesSession()
    {
        using LiveModels fixture = CreateModels();
        NeuralOcrEngine engine = fixture.Engine;

        Assert.True(engine.TryInitialize(), "PP-OCR session should initialize from the downloaded Korean model.");
        Assert.True(engine.IsReady);
    }

    [NeuralOcrLiveFact]
    public void RealKoreanModel_WritesReceiptProbeReport()
    {
        using LiveModels fixture = CreateModels();
        NeuralOcrEngine engine = fixture.Engine;

        Assert.True(engine.TryInitialize());
        StaTestHost.Run(() =>
        {
            BitmapSource image = ReceiptBitmap();
            string dir = OwnedTestDirectory.Create("ocr-probe-");
            try
            {
                // codeql[cs/path-injection] -- isolated OwnedTestDirectory workspace
                string imagePath = Path.Combine(dir, "receipt.png");
                MyCapture.Platform.Imaging.ImageCodec.SavePng(image, imagePath);
                OcrResult result = engine.RecognizeAsync(
                        OcrRequest.FromBitmap(image, 1.0, ["ko-KR"], false, true, true),
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                string report = string.Join(
                    Environment.NewLine,
                    [
                        $"Status: {result.Status}",
                        $"Language: {result.LanguageTag}",
                        $"HasText: {result.HasText}",
                        $"Lines: {result.Lines.Count}",
                        "Text:",
                        result.Text,
                    ]);
                // codeql[cs/path-injection] -- isolated OwnedTestDirectory workspace
                File.WriteAllText(Path.Combine(dir, "ocr-probe-report.txt"), report);
                Assert.Equal(OcrStatus.Success, result.Status);
                Assert.True(result.HasText);
            }
            finally
            {
                OwnedTestDirectory.Delete(dir);
            }
        });
    }

    [NeuralOcrLiveFact]
    public void RealKoreanModel_ReadsPrintedReceiptWords()
    {
        using LiveModels fixture = CreateModels();
        NeuralOcrEngine engine = fixture.Engine;

        Assert.True(engine.TryInitialize());
        StaTestHost.Run(() =>
        {
            OcrResult result = engine.RecognizeAsync(
                    OcrRequest.FromBitmap(ReceiptBitmap(), 1.0, ["ko-KR"], false, true, true),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Assert.Equal(OcrStatus.Success, result.Status);
            Assert.True(result.HasText, result.Message ?? "empty neural result");
            Assert.Contains("12345", result.Text, StringComparison.Ordinal);
        });
    }

    [NeuralOcrLiveFact]
    public void RealKoreanModel_LeavesAfterTheIdleSpellAndReadsAgain()
    {
        using var released = new ManualResetEventSlim();
        using LiveModels fixture = CreateModels(engineIdleRelease: TimeSpan.FromMilliseconds(300));
        NeuralOcrEngine engine = fixture.Engine;
        engine.EngineReleased += released.Set;

        StaTestHost.Run(() =>
        {
            BitmapSource receipt = ReceiptBitmap();
            OcrResult first = Recognize(engine, receipt);
            Assert.True(released.Wait(TimeSpan.FromSeconds(10)), "The whole-image sessions should leave once idle.");
            Assert.False(engine.IsReady);

            OcrResult again = Recognize(engine, receipt);
            Assert.Equal(OcrStatus.Success, again.Status);
            Assert.Equal(first.Text, again.Text);
        });
    }

    [NeuralOcrLiveFact]
    public void RealKoreanModel_ReadsACaptureWhoseFourthByteIsZero()
    {
        using LiveModels fixture = CreateModels();
        NeuralOcrEngine engine = fixture.Engine;

        StaTestHost.Run(() =>
        {
            // A desktop capture is Bgr32, and nothing defines its fourth byte.
            BitmapSource capture = WithFourthByteZero(ReceiptBitmap());
            OcrResult whole = Recognize(engine, capture);
            Assert.Contains("12345", whole.Text, StringComparison.Ordinal);

            OcrResult rows = engine.ReadRowsAsync(Request(capture), whole.Lines, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            Assert.Contains("12345", rows.Text, StringComparison.Ordinal);
        });
    }

    private static OcrRequest Request(BitmapSource image) =>
        OcrRequest.FromBitmap(image, 1.0, ["ko-KR"], false, true, true);

    private static OcrResult Recognize(NeuralOcrEngine engine, BitmapSource image) =>
        engine.RecognizeAsync(Request(image), CancellationToken.None).GetAwaiter().GetResult();

    private static BitmapSource WithFourthByteZero(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        byte[] pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 0;
        BitmapSource capture = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        capture.Freeze();
        return capture;
    }

    /// <summary>
    /// Opt-in: set MYCAPTURE_OCR_LIVE_MODELS to a local folder that holds the catalog's model
    /// files. The files are copied into an owned test profile; the store and engine, including their
    /// background verification and retired-file cleanup, never see the source folder or the
    /// real MyCapture profile, and every download request is rejected.
    /// </summary>
    private LiveModels CreateModels(TimeSpan? engineIdleRelease = null)
    {
        string? setting = Environment.GetEnvironmentVariable(NeuralOcrLiveFactAttribute.ModelsVariable);
        Assert.False(string.IsNullOrWhiteSpace(setting),
            $"Required live OCR validation needs {NeuralOcrLiveFactAttribute.ModelsVariable}. No inference was executed.");

        string models = Path.GetFullPath(setting!);
        string root = OwnedTestDirectory.Create("ocr-live-");
        HttpClient? http = null;
        try
        {
            AppPaths paths = CopyModelsToIsolatedProfile(models, root);
            output.WriteLine($"Required OCR models: catalog={OcrModelCatalog.CatalogVersion}, files={string.Join(';', OcrModelCatalog.Required.Select(file => file.FileName))}");
            foreach (OcrModelFile file in OcrModelCatalog.Required.Concat(OcrModelCatalog.Optional))
            {
                string path = Path.Combine(paths.OcrModelsRoot, file.FileName);
                if (!File.Exists(path) && OcrModelCatalog.Optional.Contains(file)) continue;
                using FileStream stream = File.OpenRead(path);
                Assert.Equal(file.ExpectedBytes, stream.Length);
                string hash = Convert.ToHexString(SHA256.HashData(stream));
                Assert.Equal(file.ExpectedSha256, hash, ignoreCase: true);
                output.WriteLine($"Verified OCR model: catalog={OcrModelCatalog.CatalogVersion}, file={file.FileName}, bytes={stream.Length}, sha256={hash}");
            }
            http = new HttpClient(new RejectingHandler());
            var store = new OcrModelStore(paths, http, NullLogger.Instance);
            Assert.True(store.HasRequiredModels, "The opt-in folder must contain every required, verified OCR model.");
            return new LiveModels(root, http,
                new NeuralOcrEngine(store, NullLogger<NeuralOcrEngine>.Instance, engineIdleRelease: engineIdleRelease));
        }
        catch
        {
            http?.Dispose();
            OwnedTestDirectory.Delete(root);
            throw;
        }
    }

    internal static AppPaths CopyModelsToIsolatedProfile(string models, string root)
    {
        // Read the supplied weights, but allow verification and retired-file cleanup only in a
        // fresh owned profile. Required files must exist; the optional server detector is
        // copied when present so recognition uses the same models as an installed profile.
        // Source files are confined to the supplied folder like other diagnostic inputs.
        AppPaths paths = AppPaths.CreateForRoot(root);
        Directory.CreateDirectory(paths.OcrModelsRoot);
        foreach (OcrModelFile file in OcrModelCatalog.Required)
            File.Copy(DiagnosticOutputPaths.Child(models, file.FileName), Path.Combine(paths.OcrModelsRoot, file.FileName));
        foreach (OcrModelFile file in OcrModelCatalog.Optional)
        {
            string source = DiagnosticOutputPaths.Child(models, file.FileName);
            if (File.Exists(source)) File.Copy(source, Path.Combine(paths.OcrModelsRoot, file.FileName));
        }
        return paths;
    }

    private sealed class LiveModels(string root, HttpClient http, NeuralOcrEngine engine) : IDisposable
    {
        internal NeuralOcrEngine Engine => engine;

        public void Dispose()
        {
            // Dispose does not join background verification; finish it before deleting copies.
            try { engine.ModelVerificationTask.GetAwaiter().GetResult(); }
            finally
            {
                engine.Dispose();
                http.Dispose();
                OwnedTestDirectory.Delete(root);
            }
        }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Live OCR tests never download models.");
    }

    private static BitmapSource ReceiptBitmap()
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, 640, 360));
            var text = new FormattedText(
                "영수증 RECEIPT\n합계 12345원",
                System.Globalization.CultureInfo.GetCultureInfo("ko-KR"),
                System.Windows.FlowDirection.LeftToRight,
                new Typeface("Malgun Gothic"),
                32,
                Brushes.Black,
                1.0);
            dc.DrawText(text, new System.Windows.Point(40, 48));
        }

        var bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
