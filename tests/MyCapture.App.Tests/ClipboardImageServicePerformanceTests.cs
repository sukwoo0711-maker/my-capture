using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Platform.Imaging;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>Skips unless <c>MYCAPTURE_CLIPBOARD_PERF</c> is set; each case renders and encodes large pictures many times.</summary>
internal sealed class ClipboardPerfTheoryAttribute : TheoryAttribute
{
    public ClipboardPerfTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYCAPTURE_CLIPBOARD_PERF")))
            Skip = "Opt-in clipboard copy measurement: set MYCAPTURE_CLIPBOARD_PERF=1.";
    }
}

public sealed class ClipboardImageServicePerformanceTests(ITestOutputHelper output)
{
    [ClipboardPerfTheory]
    [InlineData(800, 400, false, false)]
    [InlineData(1600, 3000, false, false)]
    [InlineData(2400, 4000, true, false)]
    [InlineData(800, 400, false, true)]
    [InlineData(1600, 3000, false, true)]
    [InlineData(2400, 4000, true, true)]
    public void ReportsEncodingAndTimeToSingleInjectedWrite(int width, int height, bool table, bool cached) => StaTestHost.Run(() =>
    {
        const string sourceUrl = "https://example.com/specification";
        const string original = "exact\ttext\r\n";
        var first = new List<(double Encode, double Write, int Bytes)>();
        var repeat = new List<(double Encode, double Write, int Bytes)>();
        for (int sample = 0; sample < 7; sample++)
        {
            BitmapSource bitmap = CreateFixture(width, height, table);
            using var cache = new ClipboardImageService.PngCache();
            first.Add(Copy(bitmap, cached ? cache : null));
            repeat.Add(Copy(bitmap, cached ? cache : null));
        }
        Report("first", first);
        Report("repeat", repeat);

        (double Encode, double Write, int Bytes) Copy(BitmapSource bitmap, ClipboardImageService.PngCache? cache)
        {
            double encodeMs = 0;
            double writeMs = 0;
            int bytes = 0;
            int writes = 0;
            long started = Stopwatch.GetTimestamp();
            Assert.True(ClipboardImageService.CopyImageWithTextAsync(bitmap, sourceUrl, original, data =>
            {
                writeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                writes++;
                Assert.Equal(original, data.GetData(DataFormats.UnicodeText, autoConvert: false));
                Assert.Same(bitmap, data.GetData(DataFormats.Bitmap, autoConvert: false));
                var stream = Assert.IsAssignableFrom<Stream>(data.GetData("PNG", autoConvert: false));
                bytes = checked((int)stream.Length);
            }, encodePng: (image, url) =>
            {
                long encodeStarted = Stopwatch.GetTimestamp();
                byte[] png = ImageCodec.EncodePng(image, url);
                encodeMs = Stopwatch.GetElapsedTime(encodeStarted).TotalMilliseconds;
                bytes = png.Length;
                return png;
            }, pngCache: cache).GetAwaiter().GetResult());
            Assert.Equal(1, writes);
            return (encodeMs, writeMs, bytes);
        }

        void Report(string phase, List<(double Encode, double Write, int Bytes)> samples)
        {
            output.WriteLine(FormattableString.Invariant(
                $"{width}x{height} {(table ? "table" : "code")} cached={cached} {phase}: n={samples.Count}; encode_ms={samples.Select(s => s.Encode).Order().ElementAt(3):F3}; write_ms={samples.Select(s => s.Write).Order().ElementAt(3):F3}; png_bytes={samples.Select(s => s.Bytes).Order().ElementAt(3)}"));
        }
    });

    private static BitmapSource CreateFixture(int width, int height, bool table)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(table ? Brushes.White : new SolidColorBrush(Color.FromRgb(30, 30, 30)), null,
                new Rect(0, 0, width, height));
            var font = new Typeface(table ? "Segoe UI" : "Consolas");
            var gridPen = new Pen(Brushes.LightGray, 1);
            for (int row = 0, y = 16; y < height; row++, y += table ? 32 : 24)
            {
                if (table)
                {
                    if (row % 2 == 0) dc.DrawRectangle(Brushes.AliceBlue, null, new Rect(0, y - 8, width, 32));
                    dc.DrawLine(gridPen, new Point(0, y - 8), new Point(width, y - 8));
                    for (int x = 8; x < width; x += 180)
                    {
                        DrawText($"R{row:D3} C{x / 180:D2} = {row * 17 + x}", x, y, Brushes.Black);
                        dc.DrawLine(gridPen, new Point(x - 8, 0), new Point(x - 8, height));
                    }
                }
                else
                {
                    DrawText($"{row + 1,4}   public int Value{row} => items[{row % 13}].Count + {row * 7}; // specification", 16, y,
                        row % 3 == 0 ? Brushes.LightSkyBlue : row % 3 == 1 ? Brushes.LightGreen : Brushes.Gainsboro);
                }
            }
            void DrawText(string text, int x, int y, Brush brush) => dc.DrawText(new FormattedText(text,
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, font, 15, brush, 1), new Point(x, y));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
