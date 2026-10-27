using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Ocr;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;
using Xunit.Abstractions;
using WpfBitmapFrame = System.Windows.Media.Imaging.BitmapFrame;

namespace MyCapture.App.Tests;

public sealed class WindowsOcrBitmapBridgeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Bgr32", 5, 3)]
    [InlineData("Bgra32", 17, 7)]
    [InlineData("Pbgra32", 31, 2)]
    public void OpaqueScreenshotsMatchThePngPixelOracle(string format, int width, int height) => StaTestHost.Run(() =>
    {
        BitmapSource source = Create32Bit(format, width, height, transparent: false);
        using SoftwareBitmap direct = WindowsOcrBitmapBridge.TryCopyOpaquePixels(source, default)!;
        Assert.NotNull(direct);
        using SoftwareBitmap legacy = WindowsOcrBitmapBridge.CreateLegacyAsync(source, default).GetAwaiter().GetResult();
        AssertEquivalentPixels(legacy, direct);
        Assert.Equal(source.DpiX, direct.DpiX);
        Assert.Equal(source.DpiY, direct.DpiY);
        Assert.True(source.IsFrozen);
    });

    [Theory]
    [InlineData("Bgra32")]
    [InlineData("Pbgra32")]
    public void TransparentPixelsKeepTheLegacyPremultiplication(string format) => StaTestHost.Run(() =>
    {
        BitmapSource source = Create32Bit(format, 5, 3, transparent: true);
        // Covers alpha 0, 1, 64, 128 and 255, with nonzero colors at alpha zero for straight BGRA.
        Assert.Null(WindowsOcrBitmapBridge.TryCopyOpaquePixels(source, default));
        using SoftwareBitmap actual = WindowsOcrBitmapBridge.CreateAsync(source, default).GetAwaiter().GetResult();
        using SoftwareBitmap legacy = WindowsOcrBitmapBridge.CreateLegacyAsync(source, default).GetAwaiter().GetResult();
        AssertEquivalentPixels(legacy, actual);
    });

    [Theory]
    [InlineData("Gray8")]
    [InlineData("Indexed8")]
    [InlineData("Bgr24")]
    public void OtherPixelFormatsKeepTheCodecConversion(string format) => StaTestHost.Run(() =>
    {
        PixelFormat pixelFormat = format switch
        {
            "Gray8" => PixelFormats.Gray8,
            "Indexed8" => PixelFormats.Indexed8,
            _ => PixelFormats.Bgr24,
        };
        BitmapPalette? palette = format == "Indexed8"
            ? new BitmapPalette([Colors.Black, Colors.White, Colors.Red, Colors.Transparent]) : null;
        int stride = (7 * pixelFormat.BitsPerPixel + 7) / 8 + 5;
        byte[] pixels = new byte[stride * 3];
        for (int index = 0; index < pixels.Length; index++)
            pixels[index] = (byte)(format == "Indexed8" ? index % 4 : index * 17 % 256);
        BitmapSource source = BitmapSource.Create(7, 3, 120, 144, pixelFormat, palette, pixels, stride);
        source.Freeze();
        Assert.Null(WindowsOcrBitmapBridge.TryCopyOpaquePixels(source, default));
        using SoftwareBitmap actual = WindowsOcrBitmapBridge.CreateAsync(source, default).GetAwaiter().GetResult();
        using SoftwareBitmap legacy = WindowsOcrBitmapBridge.CreateLegacyAsync(source, default).GetAwaiter().GetResult();
        AssertEquivalentPixels(legacy, actual);
    });

    [Fact]
    public void FrozenDecoderFrameMatchesTheOwnerThreadPngOracle() => StaTestHost.Run(() =>
    {
        BitmapSource source = BitmapSource.Create(11, 7, 120, 144, PixelFormats.Gray8, null,
            Enumerable.Range(0, 77).Select(index => (byte)(index * 17)).ToArray(), 11);
        source.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(WpfBitmapFrame.Create(source));
        using var stream = new System.IO.MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        WpfBitmapFrame frame = decoder.Frames[0];
        frame.Freeze();
        // Populate metadata on the decoder's owner while producing the independent PNG
        // oracle. Frozen pixels alone do not make uncached decoder metadata free-threaded.
        Assert.Equal(PixelFormats.Gray8, frame.Format);
        using SoftwareBitmap ownerOracle = LegacyOnOwner(frame);
        using SoftwareBitmap frameActual = WindowsOcrBitmapBridge.CreateAsync(frame, default).GetAwaiter().GetResult();
        using SoftwareBitmap frameLegacy = WindowsOcrBitmapBridge.CreateLegacyAsync(frame, default).GetAwaiter().GetResult();
        AssertEquivalentPixels(ownerOracle, frameActual);
        AssertEquivalentPixels(frameLegacy, frameActual);
        int stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
        byte[] pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);
        BitmapSource detached = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, frame.DpiX, frame.DpiY,
            frame.Format, frame.Palette, pixels, stride);
        detached.Freeze();
        using SoftwareBitmap actual = WindowsOcrBitmapBridge.CreateAsync(detached, default).GetAwaiter().GetResult();
        using SoftwareBitmap legacy = WindowsOcrBitmapBridge.CreateLegacyAsync(detached, default).GetAwaiter().GetResult();
        AssertEquivalentPixels(legacy, actual);
        AssertEquivalentPixels(ownerOracle, actual);
    });

    [Fact]
    public void MutableSourceRemainsOwnedByItsDispatcher() => StaTestHost.Run(() =>
    {
        var source = new WriteableBitmap(Create32Bit("Bgra32", 7, 3, transparent: false));
        Assert.False(source.IsFrozen);
        using SoftwareBitmap sameThread = WindowsOcrBitmapBridge.TryCopyOpaquePixels(source, default)!;
        Assert.NotNull(sameThread);
        Assert.Throws<InvalidOperationException>(() =>
        {
            using SoftwareBitmap unexpected = WindowsOcrBitmapBridge.CreateAsync(source, default).GetAwaiter().GetResult();
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            using SoftwareBitmap unexpected = WindowsOcrBitmapBridge.CreateLegacyAsync(source, default).GetAwaiter().GetResult();
        });
        Assert.False(source.IsFrozen);
        // A failed worker request neither freezes nor changes the owner's bitmap.
        source.WritePixels(new Int32Rect(0, 0, 1, 1), new byte[] { 13, 27, 41, 255 }, 4, 0);
        byte[] pixel = new byte[4];
        source.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        Assert.Equal(new byte[] { 13, 27, 41, 255 }, pixel);
    });

    [Fact]
    public async Task CancellationBeforeDispatchCreatesNoResult()
    {
        BitmapSource? source = null;
        StaTestHost.Run(() => source = Create32Bit("Bgr32", 7, 3, transparent: false));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using SoftwareBitmap unexpected = await WindowsOcrBitmapBridge.CreateAsync(source!, cancellation.Token);
        });
        Assert.ThrowsAny<OperationCanceledException>(() => WindowsOcrBitmapBridge.TryCopyOpaquePixels(source!, cancellation.Token));
    }

    [Fact]
    public void NativeResultsAreIndependentAcrossRequestsAndDisposal() => StaTestHost.Run(() =>
    {
        BitmapSource source = Create32Bit("Pbgra32", 13, 5, transparent: false);
        byte[] expected;
        using (SoftwareBitmap first = WindowsOcrBitmapBridge.CreateAsync(source, default).GetAwaiter().GetResult())
            expected = Pixels(first);
        using SoftwareBitmap second = WindowsOcrBitmapBridge.CreateAsync(source, default).GetAwaiter().GetResult();
        Assert.Equal(expected, Pixels(second));
    });

    // Opt-in: MYCAPTURE_OCR_BRIDGE_BENCH=1 dotnet test ... --filter FullyQualifiedName~LargeBitmapBridgeBenchmark
    // Run alone: the managed allocation counter includes all threads in this test process.
    [Fact]
    public async Task LargeBitmapBridgeBenchmark()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_OCR_BRIDGE_BENCH") != "1")
        {
            output.WriteLine("Set MYCAPTURE_OCR_BRIDGE_BENCH=1 to measure the two warmed conversion paths.");
            return;
        }

        BitmapSource? source = null;
        StaTestHost.Run(() => source = Create32Bit("Bgr32", 3840, 2160, transparent: false));
        for (int warmup = 0; warmup < 2; warmup++)
        {
            using SoftwareBitmap legacy = await WindowsOcrBitmapBridge.CreateLegacyAsync(source!, default);
            using SoftwareBitmap direct = await WindowsOcrBitmapBridge.CreateAsync(source!, default);
        }

        const int rounds = 5;
        var measurements = new List<(string Path, double Ms, long Allocated)>();
        for (int round = 0; round < rounds; round++)
        {
            // Alternate order to reduce ordering bias. No forced GC or working-set trimming.
            foreach (bool direct in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                long before = GC.GetTotalAllocatedBytes(precise: true);
                long start = Stopwatch.GetTimestamp();
                using (SoftwareBitmap bitmap = direct
                    ? await WindowsOcrBitmapBridge.CreateAsync(source!, default)
                    : await WindowsOcrBitmapBridge.CreateLegacyAsync(source!, default))
                {
                    Assert.Equal(3840, bitmap.PixelWidth);
                }
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                measurements.Add((direct ? "direct" : "png", elapsed, allocated));
                output.WriteLine($"round={round + 1} path={(direct ? "direct" : "png")} elapsedMs={elapsed:F3} managedAllocatedBytes={allocated}");
            }
        }

        foreach (string path in new[] { "png", "direct" })
        {
            var samples = measurements.Where(item => item.Path == path).ToArray();
            output.WriteLine($"path={path} medianMs={samples.Select(item => item.Ms).Order().ElementAt(rounds / 2):F3} " +
                $"medianManagedAllocatedBytes={samples.Select(item => item.Allocated).Order().ElementAt(rounds / 2)} " +
                "image=3840x2160 warmups=2 rounds=5; native bytes are excluded from the managed allocation counter");
        }
    }

    private static BitmapSource Create32Bit(string format, int width, int height, bool transparent)
    {
        PixelFormat pixelFormat = format switch
        {
            "Bgr32" => PixelFormats.Bgr32,
            "Bgra32" => PixelFormats.Bgra32,
            _ => PixelFormats.Pbgra32,
        };
        int stride = checked(width * 4 + 12); // Deliberately padded input rows, including odd widths.
        byte[] pixels = new byte[checked(stride * height)];
        byte[] alphas = [0, 1, 64, 128, 255];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = y * stride + x * 4;
            byte alpha = transparent ? alphas[x % alphas.Length] : (byte)255;
            pixels[offset] = (byte)((x / 17 + y / 13) % 2 == 0 ? 240 : 32);
            pixels[offset + 1] = (byte)(x % 127 + 64);
            pixels[offset + 2] = (byte)(y % 127 + 64);
            pixels[offset + 3] = format == "Bgr32" ? (byte)(x % 251) : alpha;
            if (format == "Pbgra32" && transparent)
                for (int channel = 0; channel < 3; channel++)
                    pixels[offset + channel] = (byte)(pixels[offset + channel] * alpha / 255);
        }
        BitmapSource bitmap = BitmapSource.Create(width, height, 120, 144, pixelFormat, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static void AssertEquivalentPixels(SoftwareBitmap expected, SoftwareBitmap actual)
    {
        Assert.Equal(expected.PixelWidth, actual.PixelWidth);
        Assert.Equal(expected.PixelHeight, actual.PixelHeight);
        Assert.Equal(BitmapPixelFormat.Bgra8, actual.BitmapPixelFormat);
        Assert.Equal(BitmapAlphaMode.Premultiplied, actual.BitmapAlphaMode);
        Assert.Equal(Pixels(expected), Pixels(actual));
    }

    private static byte[] Pixels(SoftwareBitmap bitmap)
    {
        byte[] pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        return pixels;
    }

    // Encode on the decoder owner's STA for the frozen-frame oracle; the original worker
    // encoder cannot read that decoder's metadata even when the frame's pixels are frozen.
    private static SoftwareBitmap LegacyOnOwner(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
        encoder.Frames.Add(WpfBitmapFrame.Create(source));
        using var encoded = new System.IO.MemoryStream();
        encoder.Save(encoded);
        using var stream = new InMemoryRandomAccessStream();
        stream.WriteAsync(encoded.ToArray().AsBuffer()).AsTask().GetAwaiter().GetResult();
        stream.Seek(0);
        var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
        return decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask().GetAwaiter().GetResult();
    }
}
