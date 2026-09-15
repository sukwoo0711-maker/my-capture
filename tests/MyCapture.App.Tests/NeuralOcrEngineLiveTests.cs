using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class NeuralOcrEngineLiveTests
{
    [Fact]
    public void RealKoreanModel_InitializesSession()
    {
        using NeuralOcrEngine? engine = CreateIfModelsPresent();
        if (engine is null)
        {
            return;
        }

        Assert.True(engine.TryInitialize(), "PP-OCR session should initialize from the downloaded Korean model.");
        Assert.True(engine.IsReady);
    }

    [Fact]
    public void RealKoreanModel_WritesReceiptProbeReport()
    {
        using NeuralOcrEngine? engine = CreateIfModelsPresent();
        if (engine is null)
        {
            return;
        }

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
        using NeuralOcrEngine? engine = CreateIfModelsPresent();
        if (engine is null)
        {
            return;
        }

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

    private static NeuralOcrEngine? CreateIfModelsPresent()
    {
        AppPaths paths = AppPaths.CreateDefault();
        var store = new OcrModelStore(paths, NullLogger<OcrModelStore>.Instance);
        return store.HasRequiredModels
            ? new NeuralOcrEngine(store, NullLogger<NeuralOcrEngine>.Instance)
            : null;
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
