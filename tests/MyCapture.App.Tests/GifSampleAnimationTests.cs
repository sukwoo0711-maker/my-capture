using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GifSampleAnimationTests
{
    private static readonly (byte R, byte G, byte B)[] Palette = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (0, 0, 0)];

    [Fact]
    public void Timeline_PlaysTinyDelaysAtTheBrowserMinimumAndLoops()
    {
        Assert.Equal(100, GifPlaybackTimeline.DisplayMs(0));
        Assert.Equal(100, GifPlaybackTimeline.DisplayMs(1));
        Assert.Equal(20, GifPlaybackTimeline.DisplayMs(2));
        var timeline = new GifPlaybackTimeline([10, 10, 20]);

        Assert.Equal(400, timeline.TotalMs);
        Assert.Equal(200, timeline.StartOf(2));
        Assert.Equal(400, timeline.EndOf(2));
        Assert.Equal(new[] { 0, 0, 1, 2, 0, 0, 0 },
            new[] { -5d, 99, 100, 399, 400, 450, double.NaN }.Select(timeline.IndexAt));
        Assert.Throws<ArgumentException>(() => new GifPlaybackTimeline([]));
    }

    [Fact]
    public void Decoder_ReturnsDetachedFramesAndDelaysOfAWriterAnimation() => RunSta(() =>
    {
        string root = OwnedTestDirectory.Create("mc-gif-sample-decoder-");
        try
        {
            string path = Path.Combine(root, "writer.gif");
            using (FileStream output = File.Create(path))
            using (var writer = new AnimatedGifWriter(output, 16, 12, 10))
            {
                writer.AddFrame(Solid(16, 12, 255, 0, 0), 10);
                writer.AddFrame(Solid(16, 12, 0, 0, 255), 20);
                writer.AddFrame(Solid(16, 12, 0, 255, 0), 2);
                writer.Complete();
            }

            GifSampleFrames frames = GifSampleDecoder.Decode(path);
            TestRecycleBin.DeleteFile(path); // Decoded frames must not need the file.

            Assert.Equal((16, 12), (frames.PixelWidth, frames.PixelHeight));
            Assert.Equal(new[] { 10, 20, 2 }, frames.DelaysCentiseconds);
            Assert.Equal(320, frames.Timeline.TotalMs);
            Assert.All(frames.Frames, frame => Assert.True(frame.IsFrozen));
            Assert.Equal(new[] { (255, 0, 0), (0, 0, 255), (0, 255, 0) },
                frames.Frames.Select(frame => Pixel(frame, 8, 6)).Select(p => ((int)p.R, (int)p.G, (int)p.B)));
            Assert.Equal(1.0, GifMotionAnalysis.ChangedFraction(frames), 6);
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Decoder_ReadsExternalBrowserClampedDelays(int delay) => RunSta(() =>
    {
        // Product writes require >= 2 cs. External GIFs can still contain 0/1 cs;
        // construct that format directly instead of violating the writer contract.
        byte[] bytes = BuildGif(2, 1,
            new TestFrame(0, 0, 2, 1, [0, 0], Disposal: 1, Transparent: null, Delay: 10),
            new TestFrame(0, 0, 2, 1, [1, 1], Disposal: 1, Transparent: null, Delay: delay));
        GifSampleFrames frames = GifSampleDecoder.Decode(bytes);
        Assert.Equal(new[] { 10, delay }, frames.DelaysCentiseconds);
        Assert.Equal(200, frames.Timeline.TotalMs);
        Assert.All(frames.Frames, frame => Assert.True(frame.IsFrozen));
        Assert.Equal(Color.FromRgb(255, 0, 0), Pixel(frames.Frames[0], 0, 0));
        Assert.Equal(Color.FromRgb(0, 255, 0), Pixel(frames.Frames[1], 0, 0));
    });

    [Fact]
    public void Decoder_CompositesOffsetsTransparencyAndDisposalLikeABrowser() => RunSta(() =>
    {
        string root = OwnedTestDirectory.Create("mc-gif-sample-composite-");
        try
        {
            string path = Path.Combine(root, "partial.gif");
            File.WriteAllBytes(path, BuildGif(4, 2,
                // Full red canvas, kept.
                new TestFrame(0, 0, 4, 2, [0, 0, 0, 0, 0, 0, 0, 0], Disposal: 1, Transparent: null, Delay: 5),
                // 2x1 at (1,0): transparent then blue; restored to background afterwards.
                new TestFrame(1, 0, 2, 1, [3, 2], Disposal: 2, Transparent: 3, Delay: 5),
                // 1x1 green at (0,1), drawn then restored to the previous canvas.
                new TestFrame(0, 1, 1, 1, [1], Disposal: 3, Transparent: null, Delay: 5),
                // Black at (3,1) over whatever disposal left.
                new TestFrame(3, 1, 1, 1, [3], Disposal: 1, Transparent: null, Delay: 5)));

            GifSampleFrames frames = GifSampleDecoder.Decode(path);

            Assert.Equal((4, 2), (frames.PixelWidth, frames.PixelHeight));
            Assert.Equal(4, frames.Frames.Count);
            Color red = Color.FromRgb(255, 0, 0), blue = Color.FromRgb(0, 0, 255), green = Color.FromRgb(0, 255, 0), black = Color.FromRgb(0, 0, 0);
            Assert.Equal(new[] { red, red, blue, red, red, red, red, red }, Pixels(frames.Frames[1]));
            // Frame 2 disposed its rectangle to transparent background before frame 3.
            Color clear = Color.FromArgb(0, 0, 0, 0);
            Assert.Equal(new[] { red, clear, clear, red, green, red, red, red }, Pixels(frames.Frames[2]));
            // Frame 3 restored the previous canvas, so green disappears again.
            Assert.Equal(new[] { red, clear, clear, red, red, red, red, black }, Pixels(frames.Frames[3]));
        }
        finally { OwnedTestDirectory.Delete(root); }
    });

    [Fact]
    public void MotionAnalysis_IgnoresStillFramesAndSmallNoise() => RunSta(() =>
    {
        BitmapSource gray = Solid(32, 32, 120, 120, 120);
        BitmapSource noisy = Solid(32, 32, 122, 122, 122);
        BitmapSource bright = Solid(32, 32, 240, 240, 240);
        var frames = new GifSampleFrames([gray, noisy, gray, bright], [10, 10, 10, 10], 32, 32);

        // Only the last of three transitions moves beyond the luma threshold.
        Assert.Equal(1.0 / 3, GifMotionAnalysis.ChangedFraction(frames), 6);
        Assert.Equal(0, GifMotionAnalysis.ChangedFraction(new GifSampleFrames([gray], [10], 32, 32)));
    });

    [Fact]
    public void MotionAnalysis_CountsAMergedImageAsStillFramesAtTheNominalDelay() => RunSta(() =>
    {
        BitmapSource gray = Solid(32, 32, 120, 120, 120);
        BitmapSource bright = Solid(32, 32, 240, 240, 240);

        // Gray for one frame, bright held for three merged frames, gray again: of the four
        // frame-to-frame transitions only two moved.
        var merged = new GifSampleFrames([gray, bright, gray], [10, 30, 10], 32, 32);
        Assert.Equal(1.0, GifMotionAnalysis.ChangedFraction(merged), 6);
        Assert.Equal(0.5, GifMotionAnalysis.ChangedFraction(merged, GifMotionAnalysis.LumaThreshold, default, 10), 6);

        // At 2x the nominal delay halves; a short layer-boundary frame still counts once.
        var fast = new GifSampleFrames([gray, bright, gray], [5, 2, 15], 32, 32);
        Assert.Equal(2.0 / 4, GifMotionAnalysis.ChangedFraction(fast, GifMotionAnalysis.LumaThreshold, default, 5), 6);
        Assert.Equal(100.0 / 10 / 2, GifPreviewSample.NominalDelayCentiseconds(GifExportQuality.Standard, 2));
    });

    [Fact]
    public void Player_SeeksPausesAndNeverPlaysASingleFrame() => RunSta(() =>
    {
        var player = new GifSamplePlayer(Dispatcher.CurrentDispatcher);
        var shown = new List<int>();
        player.FrameChanged += shown.Add;

        player.Load(new GifPlaybackTimeline([10]));
        player.Play();
        Assert.False(player.IsPlaying);

        player.Load(new GifPlaybackTimeline([10, 10, 10]));
        player.Play();
        Assert.True(player.IsPlaying);
        player.Seek(2);
        Assert.Equal(2, player.Index);
        player.Seek(9);
        Assert.Equal(2, player.Index);
        player.Pause();
        Assert.False(player.IsPlaying);
        Assert.Equal(new[] { 0, 0, 2, 2 }, shown);
    });

    private sealed record TestFrame(int Left, int Top, int Width, int Height, byte[] Indices, int Disposal, int? Transparent, int Delay);

    /// <summary>Writes a GIF with a 4-colour global table, resetting LZW before every pixel.</summary>
    private static byte[] BuildGif(int width, int height, params TestFrame[] frames)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("GIF89a"u8.ToArray());
        writer.Write((ushort)width);
        writer.Write((ushort)height);
        writer.Write((byte)0x91); // Global table, 2-bit colour resolution, 4 entries.
        writer.Write((byte)0);
        writer.Write((byte)0);
        foreach ((byte r, byte g, byte b) in Palette)
        {
            writer.Write(r);
            writer.Write(g);
            writer.Write(b);
        }

        foreach (TestFrame frame in frames)
        {
            writer.Write([0x21, 0xF9, 0x04]);
            writer.Write((byte)((frame.Disposal << 2) | (frame.Transparent is null ? 0 : 1)));
            writer.Write((ushort)frame.Delay);
            writer.Write((byte)(frame.Transparent ?? 0));
            writer.Write((byte)0);
            writer.Write((byte)0x2C);
            writer.Write((ushort)frame.Left);
            writer.Write((ushort)frame.Top);
            writer.Write((ushort)frame.Width);
            writer.Write((ushort)frame.Height);
            writer.Write((byte)0);
            writer.Write((byte)2); // LZW minimum code size.
            var codes = new List<int>();
            foreach (byte index in frame.Indices)
            {
                codes.Add(4); // Clear keeps every code 3 bits wide.
                codes.Add(index);
            }

            codes.Add(5);
            var data = new List<byte>();
            int bits = 0, buffer = 0;
            foreach (int code in codes)
            {
                buffer |= code << bits;
                bits += 3;
                while (bits >= 8)
                {
                    data.Add((byte)(buffer & 0xFF));
                    buffer >>= 8;
                    bits -= 8;
                }
            }

            if (bits > 0) data.Add((byte)buffer);
            writer.Write((byte)data.Count);
            writer.Write(data.ToArray());
            writer.Write((byte)0);
        }

        writer.Write((byte)0x3B);
        writer.Flush();
        return stream.ToArray();
    }

    private static BitmapSource Solid(int width, int height, byte r, byte g, byte b)
    {
        byte[] pixels = Enumerable.Range(0, width * height).SelectMany(_ => new[] { b, g, r, (byte)255 }).ToArray();
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color[] Pixels(BitmapSource frame)
    {
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        bgra.CopyPixels(pixels, frame.PixelWidth * 4, 0);
        return Enumerable.Range(0, frame.PixelWidth * frame.PixelHeight)
            .Select(index => pixels[(index * 4) + 3] == 0
                ? Color.FromArgb(0, 0, 0, 0)
                : Color.FromRgb(pixels[(index * 4) + 2], pixels[(index * 4) + 1], pixels[index * 4]))
            .ToArray();
    }

    private static Color Pixel(BitmapSource frame, int x, int y) => Pixels(frame)[(y * frame.PixelWidth) + x];

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = WpfTestThread.Create(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(1)), "STA test timed out");
        if (failure is not null) throw new Xunit.Sdk.XunitException("STA body threw: " + failure);
    }
}
