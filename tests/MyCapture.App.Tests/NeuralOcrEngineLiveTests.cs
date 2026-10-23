using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class NeuralOcrEngineLiveTests
{
    [Fact]
    public void RealKoreanModel_InitializesSession()
    {
        using LiveModels? fixture = CreateIfModelsPresent();
        if (fixture is null)
        {
            return;
        }
        NeuralOcrEngine engine = fixture.Engine;

        Assert.True(engine.TryInitialize(), "PP-OCR session should initialize from the downloaded Korean model.");
        Assert.True(engine.IsReady);
    }

    [Fact]
    public void RealKoreanModel_WritesReceiptProbeReport()
    {
        using LiveModels? fixture = CreateIfModelsPresent();
        if (fixture is null)
        {
            return;
        }
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

    [Fact]
    public void RealKoreanModel_ReadsPrintedReceiptWords()
    {
        using LiveModels? fixture = CreateIfModelsPresent();
        if (fixture is null)
        {
            return;
        }
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

    /// <summary>
    /// Opt-in: set MYCAPTURE_OCR_LIVE_MODELS to a local folder that holds the catalog's model
    /// files. The files are copied into an owned test profile; the store and engine, including their
    /// background verification and retired-file cleanup, never see the source folder or the
    /// real MyCapture profile, and every download request is rejected.
    /// </summary>
    private static LiveModels? CreateIfModelsPresent()
    {
        string? setting = Environment.GetEnvironmentVariable("MYCAPTURE_OCR_LIVE_MODELS");
        if (string.IsNullOrWhiteSpace(setting)) return null;

        string models = Path.GetFullPath(setting);
        string root = OwnedTestDirectory.Create("ocr-live-");
        HttpClient? http = null;
        try
        {
            AppPaths paths = CopyModelsToIsolatedProfile(models, root);
            http = new HttpClient(new RejectingHandler());
            var store = new OcrModelStore(paths, http, NullLogger.Instance);
            Assert.True(store.HasRequiredModels, "The opt-in folder must contain every required, verified OCR model.");
            return new LiveModels(root, http, new NeuralOcrEngine(store, NullLogger<NeuralOcrEngine>.Instance));
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
