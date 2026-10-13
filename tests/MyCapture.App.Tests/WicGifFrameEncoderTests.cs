using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// The direct WIC encoder against WPF's <see cref="GifBitmapEncoder"/>: byte-identical single
/// frames, and identical bytes when many MTA threads encode at once beside WPF imaging on an
/// STA thread. Set MYCAPTURE_GIF_STRESS=1 for the 128,000-encode concurrency run.
/// </summary>
public sealed class WicGifFrameEncoderTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("noise", 320, 180, 0)]
    [InlineData("gradient", 256, 64, 0)]
    [InlineData("many-colours", 640, 360, 0)]
    [InlineData("colours-200", 200, 120, 0)]
    [InlineData("colours-255", 255, 9, 0)]
    [InlineData("colours-256", 256, 9, 0)]
    [InlineData("colours-257", 257, 9, 0)]
    [InlineData("solid", 40, 30, 0)]
    [InlineData("alpha", 96, 64, 0)]
    [InlineData("transparent", 33, 21, 0)]
    [InlineData("noise", 1, 1, 0)]
    [InlineData("solid", 1, 1, 0)]
    [InlineData("noise", 17, 11, 0)]
    [InlineData("document", 333, 187, 0)]
    [InlineData("document", 960, 540, 0)]
    [InlineData("noise", 960, 540, 0)]
    [InlineData("noise", 50, 20, 12)]
    [InlineData("alpha", 31, 7, 4)]
    public void Encode_MatchesTheWpfGifEncoderByteForByte(string kind, int width, int height, int padding)
    {
        int stride = (width * 4) + padding;
        byte[] pixels = Frame(kind, width, height, stride, seed: width + height);
        byte[] expected = RunSta(() => WpfEncode(pixels, width, height, stride));
        Assert.True(expected.Length > 13, "WPF produced no GIF.");

        // The same thread kind WPF uses, and an MTA worker thread.
        Assert.Equal(expected, RunSta(() => WicGifFrameEncoder.Encode(pixels, width, height, stride)));
        Assert.Equal(expected, RunMta(() => WicGifFrameEncoder.Encode(pixels, width, height, stride)));
        Assert.Equal(0, WicGifFrameEncoder.Failures);
    }

    [Fact]
    public void Encode_RejectsBuffersSmallerThanTheImage()
    {
        Assert.Throws<ArgumentException>(() => WicGifFrameEncoder.Encode(new byte[15], 2, 2, 8));
        Assert.Throws<ArgumentException>(() => WicGifFrameEncoder.Encode(new byte[16], 2, 2, 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => WicGifFrameEncoder.Encode(new byte[16], 0, 2, 8));
    }

    /// <summary>
    /// Thread-safety evidence for encoding on the GIF pixel workers. Several MTA threads encode
    /// a rotating set of frames (1x1 to 960x540, opaque and with alpha) through the shared
    /// factory while an STA thread keeps composing and WPF-encoding layered frames; every output
    /// must equal the sequential WPF bytes. WPF imaging on several STA threads at once was
    /// measured to fail about once in 600 exports (see <see cref="GifExportEngine"/>).
    /// </summary>
    [Fact]
    public void ConcurrentEncodes_OnWorkerThreadsMatchSequentialWpfBytes()
    {
        bool stress = Environment.GetEnvironmentVariable("MYCAPTURE_GIF_STRESS") == "1";
        const int threads = 8;
        int perThread = stress ? 16_000 : 120;
        (string Kind, int Width, int Height)[] shapes =
        [
            ("noise", 320, 180), ("gradient", 320, 180), ("document", 320, 180), ("document-moved", 320, 180),
            ("alpha", 96, 64), ("colours-257", 257, 9), ("noise", 1, 1), ("document", 333, 187),
            ("document", 960, 540), ("noise", 960, 540),
        ];
        byte[][] frames = shapes.Select((shape, index) => Frame(shape.Kind, shape.Width, shape.Height, shape.Width * 4, seed: index)).ToArray();
        byte[][] references = RunSta(() => frames.Select((pixels, index) => WpfEncode(pixels, shapes[index].Width, shapes[index].Height, shapes[index].Width * 4)).ToArray());

        int mismatches = 0;
        int encodes = 0;
        string? firstMismatch = null;
        var failures = new List<Exception>();
        using var stop = new CancellationTokenSource();
        int wpfIterations = 0;
        var wpfThread = WpfTestThread.Create(() =>
        {
            try
            {
                wpfIterations = WpfImagingLoop(stop.Token);
            }
            catch (Exception ex)
            {
                lock (failures)
                {
                    failures.Add(ex);
                }
            }
        });
        wpfThread.SetApartmentState(ApartmentState.STA);
        wpfThread.Start();

        long started = Stopwatch.GetTimestamp();
        var workers = Enumerable.Range(0, threads).Select(thread => new Thread(() =>
        {
            try
            {
                for (int iteration = 0; iteration < perThread; iteration++)
                {
                    int index = ((thread * 7) + (iteration * 3)) % frames.Length;
                    (string _, int width, int height) = shapes[index];
                    byte[] encoded = WicGifFrameEncoder.Encode(frames[index], width, height, width * 4);
                    _ = Interlocked.Increment(ref encodes);
                    if (!encoded.AsSpan().SequenceEqual(references[index]))
                    {
                        _ = Interlocked.Increment(ref mismatches);
                        _ = Interlocked.CompareExchange(ref firstMismatch, $"thread {thread}, iteration {iteration}, frame {index}", null);
                    }
                }
            }
            catch (Exception ex)
            {
                lock (failures)
                {
                    failures.Add(ex);
                }
            }
        })).ToList();
        workers.ForEach(worker => worker.Start());
        workers.ForEach(worker => worker.Join());
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        stop.Cancel();
        wpfThread.Join();

        output.WriteLine(
            $"WIC concurrent encodes: {encodes} on {threads} MTA threads in {elapsed:0} ms, mismatches {mismatches}, " +
            $"concurrent STA WPF compose+encode iterations {wpfIterations}, failures {failures.Count}");
        Assert.Empty(failures);
        Assert.True(mismatches == 0, $"{mismatches} concurrent encode(s) differed; first at {firstMismatch}.");
        Assert.Equal(threads * perThread, encodes);
        Assert.True(wpfIterations > 0, "The STA thread did no WPF imaging during the run.");
        Assert.Equal(0, WicGifFrameEncoder.Failures);
    }

    /// <summary>
    /// Composes a layered frame with WPF and encodes it with WPF's encoder until stopped; every
    /// result must equal the first.
    /// </summary>
    private static int WpfImagingLoop(CancellationToken stop)
    {
        const int width = 160;
        const int height = 90;
        byte[] noise = Frame("noise", width, height, width * 4, seed: 99);
        var overlay = new TimedTextOverlay { Text = "STRESS", StartMs = 0, EndMs = 1000 };
        var request = new VideoFrameRenderRequest("unused.mp4", 0, 1000, 10, width, height, width, height, [overlay], []);
        var compositor = new VideoFrameCompositor(width, height);
        byte[]? first = null;
        int iterations = 0;
        while (!stop.IsCancellationRequested)
        {
            BitmapSource composed = compositor.Compose(request, new Dictionary<Guid, BitmapSource>(), noise, width, height, width * 4, 500);
            byte[] encoded = AnimatedGifWriter.EncodeFrame(composed).ToArray();
            first ??= encoded;
            if (!encoded.AsSpan().SequenceEqual(first))
            {
                throw new InvalidDataException($"WPF output changed at STA iteration {iterations}.");
            }

            iterations++;
        }

        return iterations;
    }

    internal static byte[] WpfEncode(byte[] pixels, int width, int height, int stride) =>
        AnimatedGifWriter.EncodeFrame(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride)).ToArray();

    /// <summary>Deterministic test images; padding bytes are filled with noise.</summary>
    internal static byte[] Frame(string kind, int width, int height, int stride, int seed)
    {
        var random = new Random(seed);
        byte[] pixels = new byte[stride * height];
        random.NextBytes(pixels);
        byte[] document = kind.StartsWith("document", StringComparison.Ordinal) ? new byte[width * height * 4] : [];
        if (document.Length > 0)
        {
            GifExportEngineBenchmarkTests.PaintDocumentFrame(document, width, height, kind == "document" ? 3 : 40);
        }

        int distinct = kind.StartsWith("colours-", StringComparison.Ordinal) ? int.Parse(kind["colours-".Length..], System.Globalization.CultureInfo.InvariantCulture) : 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * stride) + (x * 4);
                int pixel = (y * width) + x;
                (byte b, byte g, byte r, byte a) = kind switch
                {
                    "noise" => (pixels[offset], pixels[offset + 1], pixels[offset + 2], (byte)255),
                    "gradient" => ((byte)(x * 255 / Math.Max(1, width - 1)), (byte)(y * 255 / Math.Max(1, height - 1)), (byte)((x + y) & 0xFF), (byte)255),
                    "many-colours" => ((byte)(x * 7), (byte)(y * 5), (byte)((x * y) >> 3), (byte)255),
                    "solid" => ((byte)0x12, (byte)0x80, (byte)0xF0, (byte)255),
                    "alpha" => (pixels[offset], pixels[offset + 1], pixels[offset + 2], (byte)(pixel % 5 == 0 ? 0 : (pixel * 37) & 0xFF)),
                    "transparent" => (pixels[offset], pixels[offset + 1], pixels[offset + 2], (byte)0),
                    _ when document.Length > 0 => (document[pixel * 4], document[(pixel * 4) + 1], document[(pixel * 4) + 2], (byte)255),
                    _ when distinct > 0 => Distinct(pixel % distinct),
                    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                };
                pixels[offset] = b;
                pixels[offset + 1] = g;
                pixels[offset + 2] = r;
                pixels[offset + 3] = a;
            }
        }

        return pixels;

        static (byte, byte, byte, byte) Distinct(int index) =>
            ((byte)((index * 97) & 0xFF), (byte)((index * 13) & 0xFF), (byte)(index & 0xFF), (byte)255);
    }

    private static T RunSta<T>(Func<T> body) => Run(body, ApartmentState.STA);

    private static T RunMta<T>(Func<T> body) => Run(body, ApartmentState.MTA);

    private static T Run<T>(Func<T> body, ApartmentState apartment)
    {
        T result = default!;
        Exception? failure = null;
        var thread = WpfTestThread.Create(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(apartment);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"{apartment} body threw: {failure}");
        }

        return result;
    }
}
