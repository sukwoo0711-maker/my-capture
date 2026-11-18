using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Ocr;
using ZXing;
using ZXing.Common;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class BarcodeRecognitionTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Formats() => ImageCodeDecoder.Formats.Select(format => new object[] { format });

    [Fact]
    public void Ean8_EnabledBehindScanLineConfirmation()
    {
        Assert.Contains(BarcodeFormat.EAN_8, ImageCodeDecoder.Formats);
        Assert.Equal("96385074", Assert.Single(ImageCodeDecoder.Decode(BarcodeFixture.Symbol(BarcodeFormat.EAN_8, "96385074", 4), CancellationToken.None)).Text);
    }

    [Theory]
    [InlineData(BarcodeFormat.EAN_13, 0)] [InlineData(BarcodeFormat.EAN_13, 90)]
    [InlineData(BarcodeFormat.EAN_8, 0)] [InlineData(BarcodeFormat.EAN_8, 90)]
    [InlineData(BarcodeFormat.UPC_A, 0)] [InlineData(BarcodeFormat.CODE_128, 0)] [InlineData(BarcodeFormat.CODE_128, 270)]
    public void OneDimensional_SingleScanLineIsNotEnough(BarcodeFormat format, int angle)
    {
        // A strip two pixels deep is a valid single-row read, but the lines at 25% and 75% of the
        // extent across the bars are blank page, so the read is not confirmed.
        BitmapSource symbol = BarcodeFixture.Symbol(format, BarcodeFixture.Payload(format), 3);
        var strip = new CroppedBitmap(symbol, new Int32Rect(0, 49, symbol.PixelWidth, 2)); strip.Freeze();
        BitmapSource thin = angle == 0 ? strip : Frozen(new TransformedBitmap(strip, new RotateTransform(angle)));
        BitmapSource full = angle == 0 ? symbol : Frozen(new TransformedBitmap(symbol, new RotateTransform(angle)));
        int size = Math.Max(symbol.PixelWidth, 100) + 200;
        Assert.Empty(ImageCodeDecoder.Decode(BarcodeFixture.Canvas(size, size, (thin, 100, 100)), CancellationToken.None));
        // Control: the same symbol at full bar height is read at the same place.
        Assert.Contains(ImageCodeDecoder.Decode(BarcodeFixture.Canvas(size, size, (full, 100, 100)), CancellationToken.None),
            code => code.Text == BarcodeFixture.Payload(format));

        static BitmapSource Frozen(BitmapSource image) { image.Freeze(); return image; }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void EveryEnabledFormat_RotationsAndPolarity(BarcodeFormat format)
    {
        string payload = BarcodeFixture.Payload(format);
        foreach (int angle in new[] { 0, 90, 180, 270 })
        foreach (bool inverted in new[] { false, true })
        {
            BitmapSource image = BarcodeFixture.Symbol(format, payload, 4, inverted: inverted, angle: angle);
            var codes = ImageCodeDecoder.Decode(image, CancellationToken.None);
            Assert.True(codes.Any(code => code.Text == payload), $"{format}, {angle}, inverted={inverted}: {string.Join(" | ", codes.Select(c => c.Text))}");
            Assert.All(codes, code => Assert.True(OcrGeometry.IsValid(code.Bounds)));
        }
    }

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void Qr_ModuleSizesWithAndWithoutQuietZone(int module)
    {
        foreach (int margin in new[] { 0, 4 })
        {
            var codes = ImageCodeDecoder.Decode(BarcodeFixture.Symbol(BarcodeFormat.QR_CODE, "exact QR payload", module, margin), CancellationToken.None);
            Assert.Equal("exact QR payload", Assert.Single(codes).Text);
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void EveryEnabledFormat_AwayFromImageCentre(BarcodeFormat format)
    {
        BitmapSource symbol = BarcodeFixture.Symbol(format, BarcodeFixture.Payload(format), 3);
        var image = BarcodeFixture.Canvas(Math.Max(1200, symbol.PixelWidth + 100), 900, (symbol, 40, 45));
        Assert.Contains(ImageCodeDecoder.Decode(image, CancellationToken.None), code => code.Text == BarcodeFixture.Payload(format));
    }

    [Theory]
    [InlineData(BarcodeFormat.CODE_128)]
    [InlineData(BarcodeFormat.EAN_13)]
    [InlineData(BarcodeFormat.UPC_A)]
    public void OneDimensionalBounds_CoverBarsWithoutCoveringWholePage(BarcodeFormat format)
    {
        foreach (int angle in new[] { 0, 90, 180, 270 })
        {
            BitmapSource symbol = BarcodeFixture.Symbol(format, BarcodeFixture.Payload(format), 3, angle: angle);
            BitmapSource image = BarcodeFixture.Canvas(1400, 1400, (symbol, 100, 200));
            var code = Assert.Single(ImageCodeDecoder.Decode(image, CancellationToken.None));
            byte[] pixels = new byte[symbol.PixelWidth * symbol.PixelHeight];
            symbol.CopyPixels(pixels, symbol.PixelWidth, 0);
            int left = symbol.PixelWidth, right = 0, top = symbol.PixelHeight, bottom = 0;
            for (int y = 0; y < symbol.PixelHeight; y++)
            for (int x = 0; x < symbol.PixelWidth; x++)
            {
                if (pixels[y * symbol.PixelWidth + x] >= 128) continue;
                left = Math.Min(left, x); right = Math.Max(right, x + 1);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y + 1);
            }
            Assert.True(code.Bounds.X <= 100 + left && code.Bounds.Right >= 100 + right
                && code.Bounds.Y <= 200 + top && code.Bounds.Bottom >= 200 + bottom,
                $"Rotation {angle}; ink ({left},{top})-({right},{bottom}); decoded {code.Bounds}");
            Assert.InRange(code.Bounds.Width, right - left, symbol.PixelWidth + 20);
            Assert.InRange(code.Bounds.Height, bottom - top, symbol.PixelHeight + 20);
        }
    }

    [Theory]
    [InlineData("UTF-8", "안녕하세요 한글 QR 서울 123")]
    [InlineData("CP949", "안녕하세요 한글 QR 똠 123")]
    [InlineData("EUC-KR", "안녕하세요 서울 123")]
    [InlineData("UTF-8", "first line\nsecond line\n\tthird line  ")]
    [InlineData("UTF-8", "ab\0cd\u0001ef\u007f\tgh\r\nij")]
    public void Qr_PayloadEncodingAndControlCharacters(string encoding, string text)
    {
        foreach (bool eci in new[] { false, true })
        {
            var image = BarcodeFixture.Symbol(BarcodeFormat.QR_CODE, text, 5, encoding: encoding, eci: eci);
            var result = Assert.Single(ImageCodeDecoder.Decode(image, CancellationToken.None));
            Assert.Equal(new string(text.Where(c => !char.IsControl(c) || c is '\t' or '\r' or '\n').ToArray()), result.Text);
        }
    }

    [Theory]
    [InlineData(2)] [InlineData(3)]
    public void SeveralQrCodes_AllLocationsSurviveIncludingEqualPayloads(int count)
    {
        BitmapSource image = BarcodeFixture.Canvas(1000, 650, Enumerable.Range(0, count)
            .Select(index => (BarcodeFixture.Symbol(BarcodeFormat.QR_CODE, index == 2 ? "payload 0" : $"payload {index}", 5), 30 + index * 300, 180)).ToArray());
        var codes = ImageCodeDecoder.Decode(image, CancellationToken.None);
        Assert.Equal(count, codes.Count);
        Assert.Equal(count, codes.Select(code => code.Bounds).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void ThreeSeparatedCodes_ReportSupportedMultiplicity(BarcodeFormat format)
    {
        BitmapSource symbol = BarcodeFixture.Symbol(format, BarcodeFixture.Payload(format), 2);
        int column = symbol.PixelWidth + 90;
        BitmapSource image = BarcodeFixture.Canvas(column * 3, Math.Max(500, symbol.PixelHeight + 80),
            (symbol, 30, 40), (symbol, column + 30, 40), (symbol, column * 2 + 30, 40));
        var codes = ImageCodeDecoder.Decode(image, CancellationToken.None);
        Assert.InRange(codes.Count, 1, 3);
        if (format == BarcodeFormat.QR_CODE) Assert.Equal(3, codes.Count);
        output.WriteLine($"{format}: {codes.Count}/3 separated equal-payload symbols.");
    }

    [Theory]
    [InlineData(1920, 1080, 2)] [InlineData(1920, 1080, 3)]
    [InlineData(3840, 2160, 4)] [InlineData(3840, 2160, 5)]
    public void Qr_InsideScreenshotWithRenderedText(int width, int height, int module) => Ocr410Fixture.RunSta(() =>
    {
        BitmapSource image = BarcodeFixture.Screenshot(width, height, module);
        Assert.Contains(ImageCodeDecoder.Decode(image, CancellationToken.None), code => code.Text == "synthetic code payload");
    });

    [Fact]
    public void NoFalsePositives_AllShippedUiPicturesAndRenderedClutter() => Ocr410Fixture.RunSta(() =>
    {
        string root = BarcodeFixture.RepositoryRoot();
        string[] files = new[] { "docs", "build", "src/MyCapture.App/Assets/SettingsGuides" }
            .SelectMany(relative => Directory.EnumerateFiles(Path.Combine(root, relative), "*.png", SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Order().ToArray();
        Assert.True(files.Length >= 20, $"Only {files.Length} shipped UI pictures found.");
        // Scan the whole corpus before asserting so a failure names every misfiring picture.
        var misfires = new List<string>();
        foreach (string path in files)
        {
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path); image.EndInit(); image.Freeze();
            var codes = ImageCodeDecoder.Decode(image, CancellationToken.None);
            if (codes.Count != 0) misfires.Add($"{Path.GetRelativePath(root, path)}: {string.Join(" | ", codes.Select(code => code.Text))}");
        }
        Assert.True(misfires.Count == 0, $"False positives in {misfires.Count} of {files.Length} shipped PNGs:\n{string.Join("\n", misfires)}");
        for (int kind = 0; kind < 4; kind++)
            Assert.Empty(ImageCodeDecoder.Decode(BarcodeFixture.Clutter(kind), CancellationToken.None));
        output.WriteLine($"False-positive corpus: {files.Length} shipped PNGs + 4 rendered clutter images; zero codes.");
    });

    [Fact]
    public void ReleaseMeasurements_ThirtyRunsPerImage() => Ocr410Fixture.RunSta(() =>
    {
        foreach ((int width, int height) in new[] { (1920, 1080), (3840, 2160) })
        foreach (bool withCode in new[] { false, true })
        {
            BitmapSource image = BarcodeFixture.Screenshot(width, height, withCode ? 6 : 0);
            for (int warmup = 0; warmup < 5; warmup++) _ = ImageCodeDecoder.Decode(image, CancellationToken.None);
            var milliseconds = new List<double>();
            var allocations = new List<long>();
            for (int run = 0; run < 30; run++)
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                var codes = ImageCodeDecoder.Decode(image, CancellationToken.None);
                milliseconds.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
                Assert.Equal(withCode ? 1 : 0, codes.Count);
            }
            milliseconds.Sort(); allocations.Sort();
            output.WriteLine($"{width}x{height}, code={withCode}, n=30: median={(milliseconds[14] + milliseconds[15]) / 2:F3} ms; worst={milliseconds[^1]:F3} ms; allocated median={(allocations[14] + allocations[15]) / 2} B; max={allocations[^1]} B.");
        }
    });
}

internal static class BarcodeFixture
{
    internal static string Payload(BarcodeFormat format) => format switch
    {
        BarcodeFormat.EAN_13 => "5901234123457", BarcodeFormat.EAN_8 => "96385074",
        BarcodeFormat.UPC_A => "036000291452", _ => "synthetic code payload",
    };

    internal static BitmapSource Symbol(BarcodeFormat format, string text, int module, int margin = 4,
        bool inverted = false, int angle = 0, string encoding = "UTF-8", bool eci = false)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var hints = new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.MARGIN] = margin,
            [EncodeHintType.CHARACTER_SET] = encoding,
            [EncodeHintType.DISABLE_ECI] = !eci,
        };
        BitMatrix matrix = new MultiFormatWriter().encode(text, format, 0, 0, hints);
        int width = matrix.Width * module, height = matrix.Height * module;
        if (matrix.Height == 1) height = 100;
        byte[] pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            pixels[y * width + x] = (byte)(matrix[x / module, matrix.Height == 1 ? 0 : y / module] ^ inverted ? 0 : 255);
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        bitmap.Freeze();
        if (angle == 0) return bitmap;
        var rotated = new TransformedBitmap(bitmap, new RotateTransform(angle)); rotated.Freeze(); return rotated;
    }

    internal static BitmapSource Canvas(int width, int height, params (BitmapSource Image, int X, int Y)[] symbols)
    {
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        foreach ((BitmapSource image, int left, int top) in symbols)
        {
            byte[] row = new byte[image.PixelWidth];
            for (int y = 0; y < image.PixelHeight; y++)
            {
                image.CopyPixels(new Int32Rect(0, y, image.PixelWidth, 1), row, row.Length, 0);
                row.CopyTo(pixels, (top + y) * width + left);
            }
        }
        BitmapSource result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        result.Freeze(); return result;
    }

    internal static BitmapSource Screenshot(int width, int height, int module)
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            DrawText(drawing, "Synthetic recognition sample", 35, 40, 24);
            DrawText(drawing, "Two lines of text around a local code", 35, height - 80, 24);
            if (module > 0)
            {
                BitmapSource code = Symbol(BarcodeFormat.QR_CODE, "synthetic code payload", module);
                drawing.DrawImage(code, new Rect(width / 2, height / 2, code.PixelWidth, code.PixelHeight));
            }
        }
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze(); return result;
    }

    internal static BitmapSource Clutter(int kind)
    {
        const int width = 1400, height = 900;
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            if (kind == 3)
            {
                for (int x = 15; x < width - 15; x += 7)
                    drawing.DrawRectangle(Brushes.Black, null, new Rect(x, 100, 3, 300));
            }
            else
            {
                for (int y = 25; y < height - 20; y += 24)
                    DrawText(drawing, kind == 2 ? $"if (value[{y}] != null) {{ return source.Select(x => x.Text); }} // test data"
                        : $"{y:D4}  Dense text document 한글 문장  ABCDEFG 0123456789  Sample text without codes", 25, y, 17);
                if (kind == 1)
                {
                    var pen = new Pen(Brushes.Black, 1);
                    for (int x = 10; x < width; x += 90) drawing.DrawLine(pen, new Point(x, 10), new Point(x, height - 10));
                    for (int y = 10; y < height; y += 24) drawing.DrawLine(pen, new Point(10, y), new Point(width - 10, y));
                }
            }
        }
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze(); return result;
    }

    private static void DrawText(DrawingContext drawing, string text, double x, double y, double size) =>
        drawing.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Malgun Gothic"), size, Brushes.Black, 1), new Point(x, y));

    internal static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MyCapture.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Source checkout required.");
    }
}
