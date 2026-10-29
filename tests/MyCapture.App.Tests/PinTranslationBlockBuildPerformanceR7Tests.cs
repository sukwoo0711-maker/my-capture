using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class PinTranslationBlockBuildPerformanceR7Tests(ITestOutputHelper output)
{
    [PinOverlayReplayFact]
    public async Task RecordedPdfAndSixtyLineScreenReportBlockBuildingTime()
    {
        string directory = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusReplayTests.ReplayVariable)!;
        var samples = new List<(string Name, BitmapSource Image, OcrResult Ocr)>();
        StaTestHost.Run(() =>
        {
            const string name = "pdf-en-irs-w9-p2";
            var pdf = new BitmapImage();
            pdf.BeginInit();
            pdf.CacheOption = BitmapCacheOption.OnLoad;
            pdf.UriSource = new Uri(Path.Combine(directory, name + "-source.png"));
            pdf.EndInit();
            pdf.Freeze();
            Assert.Equal((1700, 2200), (pdf.PixelWidth, pdf.PixelHeight));
            samples.Add((name, pdf, PinTranslationOverlayCorpusTests.ReadRecognized(Path.Combine(directory, name + "-ocr.json"))));

            var visual = new DrawingVisual();
            var lines = new List<OcrLine>();
            using (DrawingContext drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1920, 1080));
                for (int index = 0; index < 60; index++)
                {
                    const string text = "Save the document before closing the window.";
                    var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        ClipboardTextRenderer.TextTypeface, 23, Brushes.Black, 1);
                    var origin = new Point(20 + index / 20 * 630, 20 + index % 20 * 50);
                    drawing.DrawText(formatted, origin);
                    Rect ink = formatted.BuildGeometry(origin).Bounds;
                    var bounds = new OcrRect(ink.X, ink.Y, ink.Width, ink.Height);
                    lines.Add(new(text, bounds, [new(text, bounds)]));
                }
            }
            var screen = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
            screen.Render(visual);
            screen.Freeze();
            samples.Add(("sixty-line-screen", screen, OcrResult.Success(string.Join("\n", lines.Select(line => line.Text)),
                "en-US", lines, TimeSpan.Zero)));
        });
        foreach (var sample in samples)
        {
            await Task.Run(() =>
            {
                _ = PinTranslationOverlay.BuildBlocks(sample.Ocr, sample.Image);
                var elapsed = new List<double>();
                int blockCount = 0;
                for (int round = 0; round < 7; round++)
                {
                    var watch = Stopwatch.StartNew();
                    IReadOnlyList<PinOverlayBlock> blocks = PinTranslationOverlay.BuildBlocks(sample.Ocr, sample.Image);
                    elapsed.Add(watch.Elapsed.TotalMilliseconds);
                    Assert.NotEmpty(blocks);
                    blockCount = blocks.Count;
                }
                output.WriteLine($"BuildBlocks {sample.Name}: {sample.Image.PixelWidth}x{sample.Image.PixelHeight}; "
                    + $"{sample.Ocr.Lines.Count} lines, {blockCount} blocks; 7 warm samples; median {elapsed.Order().ElementAt(3):F3} ms; "
                    + $"min {elapsed.Min():F3}, max {elapsed.Max():F3}; worker thread {Environment.CurrentManagedThreadId}.");
            });
        }
    }
}
