using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// End-to-end verification of the video pipeline that cannot be exercised through the
/// interactive UI in an automated run: encode synthetic frames to a real MP4 with the
/// Media Foundation Sink Writer, then re-open and decode that file to prove it is a
/// valid, playable clip with the expected dimensions and a plausible duration.
/// </summary>
/// <remarks>
/// This is the automated substitute for a human pressing record. It requires Media
/// Foundation (present on all supported Windows SKUs) and runs on an STA thread because
/// WPF media objects are apartment-affine. If MF is genuinely unavailable the encoder
/// constructor throws and the test fails loudly rather than passing on assumption.
/// </remarks>
public sealed class MediaFoundationEncodeRoundTripTests
{
    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = WpfTestThread.Create(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }

    [Fact]
    public void Encode_ThenDecode_ProducesPlayableMp4WithExpectedDimensions() => RunSta(() =>
    {
        const int width = 320;
        const int height = 240;
        const int fps = 15;
        const int frameCount = 30; // ~2 seconds

        string dir = TestRecycleBin.CreateTempSubdirectory("mycapture-mftest-").FullName;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "roundtrip.mp4");

        try
        {
            var options = new VideoEncoderOptions(
                path,
                width,
                height,
                fps,
                VideoEncoderOptions.DeriveBitrate(width, height, fps));

            using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
            {
                int stride = width * 4;
                byte[] frame = new byte[stride * height];
                double frameMs = 1000.0 / fps;

                for (int i = 0; i < frameCount; i++)
                {
                    // Animate a moving colour so successive frames genuinely differ and the
                    // encoder is exercised beyond a single repeated keyframe.
                    FillGradient(frame, width, height, stride, i);
                    encoder.WriteFrame(new EncoderFrame(frame, width, height, stride, i * frameMs));
                }

                encoder.Complete();
            }

            // The file must exist and carry real encoded data.
            Assert.True(File.Exists(path), "encoder did not produce an output file");
            long size = new FileInfo(path).Length;
            Assert.True(size > 1_000, $"output MP4 is implausibly small ({size} bytes)");

            // Re-open and decode to prove it is a valid, playable clip.
            (bool opened, int mediaWidth, int mediaHeight, double durationMs) = ProbeMedia(path);

            Assert.True(opened, "the produced MP4 could not be re-opened by MediaPlayer");
            Assert.Equal(width, mediaWidth);
            Assert.Equal(height, mediaHeight);

            // 30 frames at 15fps span 2000ms; the container stores that to one 1/15000s unit.
            Assert.InRange(durationMs, 1_999.8, 2_000.01);
        }
        finally
        {
            try { TestRecycleBin.DeleteDirectory(dir, recursive: true); } catch (IOException) { }
        }
    });

    [Fact]
    public void Encode_PreservesOrientation_NoVerticalFlip_NoHorizontalMirror() => RunSta(() =>
    {
        // Encode frames whose TOP-LEFT quadrant is bright red and the rest near-black. After a
        // round-trip, the bright quadrant must still be top-left. This catches the classic MF
        // RGB32 bottom-up bug (would move it to bottom-left) and any horizontal mirror.
        const int width = 160, height = 120, fps = 15, frames = 20;
        string dir = TestRecycleBin.CreateTempSubdirectory("mc-orient-").FullName;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "orient.mp4");
        try
        {
            var options = new VideoEncoderOptions(path, width, height, fps, VideoEncoderOptions.DeriveBitrate(width, height, fps));
            using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
            {
                int stride = width * 4;
                byte[] frame = new byte[stride * height];
                double frameMs = 1000.0 / fps;
                for (int i = 0; i < frames; i++)
                {
                    FillTopLeftMarker(frame, width, height, stride);
                    encoder.WriteFrame(new EncoderFrame(frame, width, height, stride, i * frameMs));
                }

                encoder.Complete();
            }

            BitmapSource? decoded = RenderMiddleFrame(path, width, height);
            Assert.NotNull(decoded);

            // Sample the four quadrant centres.
            byte TopLeftR = SampleR(decoded!, width / 4, height / 4);
            byte TopRightR = SampleR(decoded!, width * 3 / 4, height / 4);
            byte BottomLeftR = SampleR(decoded!, width / 4, height * 3 / 4);

            // The bright-red marker must be strongest in the TOP-LEFT quadrant.
            Assert.True(TopLeftR > 150, $"top-left not bright (R={TopLeftR}) — frame may be blank/failed");
            Assert.True(TopLeftR > BottomLeftR + 40, $"vertical FLIP detected: top-left R={TopLeftR} vs bottom-left R={BottomLeftR}");
            Assert.True(TopLeftR > TopRightR + 40, $"horizontal MIRROR detected: top-left R={TopLeftR} vs top-right R={TopRightR}");
        }
        finally
        {
            try { TestRecycleBin.DeleteDirectory(dir, recursive: true); } catch (IOException) { }
        }
    });

    [Theory]
    [InlineData(false, 235, 16)]
    [InlineData(true, 255, 0)]
    public void FullRangeOptionStoresFullRangeLumaThatWpfShowsAsTrueWhiteAndBlack(bool fullRange, int whiteLuma, int blackLuma) => RunSta(() =>
    {
        // Left half white, right half black.
        const int width = 160, height = 120, fps = 15, frames = 15;
        string dir = TestRecycleBin.CreateTempSubdirectory("mc-range-").FullName;
        string path = Path.Combine(dir, "range.mp4");
        try
        {
            int stride = width * 4;
            byte[] frame = new byte[stride * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int p = y * stride + x * 4;
                    byte level = x < width / 2 ? (byte)255 : (byte)0;
                    frame[p] = frame[p + 1] = frame[p + 2] = level;
                    frame[p + 3] = 255;
                }
            var options = new VideoEncoderOptions(path, width, height, fps, VideoEncoderOptions.DeriveBitrate(width, height, fps)) { FullRange = fullRange };
            using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
            {
                for (int i = 0; i < frames; i++) encoder.WriteFrame(new EncoderFrame(frame, width, height, stride, i * 1000.0 / fps));
                encoder.Complete();
            }

            // The stored samples: full range reaches 0 and 255, studio range stops at 16 and 235.
            using (var reader = new MediaFoundationVideoFrameReader(path, new MediaFoundationVideoFrameReaderOptions { OutputFormat = DecodedVideoPixelFormat.Nv12 }))
            {
                DecodedVideoFrame decoded = reader.ReadFrameAt(0);
                Assert.InRange(decoded.Pixels[height / 2 * decoded.Stride + width / 4], whiteLuma - 3, whiteLuma + 3);
                Assert.InRange(decoded.Pixels[height / 2 * decoded.Stride + width * 3 / 4], Math.Max(0, blackLuma - 3), blackLuma + 3);
            }

            // WPF's player shows the samples without expanding studio range, so only full range
            // reaches true white and black there.
            if (fullRange)
            {
                BitmapSource shown = RenderMiddleFrame(path, width, height) ?? throw new Xunit.Sdk.XunitException("WPF could not open the clip");
                Assert.True(SampleR(shown, width / 4, height / 2) >= 248, $"white shows as {SampleR(shown, width / 4, height / 2)}");
                Assert.True(SampleR(shown, width * 3 / 4, height / 2) <= 8, $"black shows as {SampleR(shown, width * 3 / 4, height / 2)}");
            }
        }
        finally
        {
            try { TestRecycleBin.DeleteDirectory(dir, recursive: true); } catch (IOException) { }
        }
    });

    private static void FillTopLeftMarker(byte[] buffer, int width, int height, int stride)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int p = (y * stride) + (x * 4);
                bool topLeft = x < width / 2 && y < height / 2;
                buffer[p + 0] = 0x10;                    // B
                buffer[p + 1] = 0x10;                    // G
                buffer[p + 2] = (byte)(topLeft ? 0xFF : 0x10); // R bright only top-left
                buffer[p + 3] = 0xFF;                    // A
            }
        }
    }

    private static BitmapSource? RenderMiddleFrame(string path, int width, int height)
    {
        var player = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };
        bool opened = false, failed = false;
        player.MediaOpened += (_, _) => opened = true;
        player.MediaFailed += (_, _) => failed = true;
        player.Open(new Uri(Path.GetFullPath(path), UriKind.Absolute));
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!opened && !failed && DateTime.UtcNow < deadline)
        {
            PumpFor(TimeSpan.FromMilliseconds(20));
        }

        if (!opened)
        {
            player.Close();
            return null;
        }

        // Park on a frame in the middle of the clip and let the scrubber present it.
        player.Position = TimeSpan.FromMilliseconds(500);
        PumpFor(TimeSpan.FromMilliseconds(200));

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawVideo(player, new Rect(0, 0, width, height));
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        player.Close();
        return rtb;
    }

    private static byte SampleR(BitmapSource bmp, int x, int y)
    {
        var one = new BitmapImagePixel(bmp, x, y);
        return one.R;
    }

    private readonly struct BitmapImagePixel
    {
        public readonly byte B, G, R, A;

        public BitmapImagePixel(BitmapSource bmp, int x, int y)
        {
            BitmapSource src = bmp.Format == PixelFormats.Bgra32 || bmp.Format == PixelFormats.Pbgra32
                ? bmp
                : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            byte[] px = new byte[4];
            int cx = Math.Clamp(x, 0, src.PixelWidth - 1);
            int cy = Math.Clamp(y, 0, src.PixelHeight - 1);
            src.CopyPixels(new Int32Rect(cx, cy, 1, 1), px, 4, 0);
            B = px[0]; G = px[1]; R = px[2]; A = px[3];
        }
    }

    private static void FillGradient(byte[] buffer, int width, int height, int stride, int frameIndex)
    {
        byte shift = (byte)((frameIndex * 8) & 0xFF);
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int p = row + (x * 4);
                buffer[p + 0] = (byte)((x + shift) & 0xFF); // B
                buffer[p + 1] = (byte)((y + shift) & 0xFF); // G
                buffer[p + 2] = (byte)(shift);              // R
                buffer[p + 3] = 0xFF;                       // A
            }
        }
    }

    private static (bool Opened, int Width, int Height, double DurationMs) ProbeMedia(string path)
    {
        var player = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };
        bool opened = false;
        bool failed = false;
        player.MediaOpened += (_, _) => opened = true;
        player.MediaFailed += (_, _) => failed = true;
        player.Open(new Uri(path, UriKind.Absolute));

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!opened && !failed && DateTime.UtcNow < deadline)
        {
            PumpFor(TimeSpan.FromMilliseconds(20));
        }

        int w = player.NaturalVideoWidth;
        int h = player.NaturalVideoHeight;
        player.Close();

        // MediaPlayer proves WPF can play the file, but its NaturalDuration is Windows Media
        // Player's whole-second value; the container duration is the exact one.
        double durationMs = opened && !failed ? MediaFoundationVideoFrameReader.ReadContainerDurationMs(path) : 0;
        return (opened && !failed, w, h, durationMs);
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void TrimReencode_ProducesShorterPlayableClipFromSource() => RunSta(() =>
    {
        const int width = 320;
        const int height = 240;
        const int fps = 15;
        const int frameCount = 45; // ~3 seconds

        string dir = TestRecycleBin.CreateTempSubdirectory("mycapture-trimtest-").FullName;
        Directory.CreateDirectory(dir);
        string source = Path.Combine(dir, "source.mp4");
        string trimmed = Path.Combine(dir, "source_trim.mp4");

        try
        {
            // Produce a ~3s source clip.
            var options = new VideoEncoderOptions(source, width, height, fps, VideoEncoderOptions.DeriveBitrate(width, height, fps));
            using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
            {
                int stride = width * 4;
                byte[] frame = new byte[stride * height];
                double frameMs = 1000.0 / fps;
                for (int i = 0; i < frameCount; i++)
                {
                    FillGradient(frame, width, height, stride, i);
                    encoder.WriteFrame(new EncoderFrame(frame, width, height, stride, i * frameMs));
                }

                encoder.Complete();
            }

            (bool srcOpened, _, _, double srcDurationMs) = ProbeMedia(source);
            Assert.True(srcOpened, "source clip failed to open");

            // Trim the middle ~1 second [1000ms, 2000ms].
            var recording = new MyCapture.Platform.Recording.RecordingResult(source, srcDurationMs, fps, frameCount, width, height);
            MyCapture.App.Recording.TrimReencoder.Reencode(
                source,
                trimmed,
                inMs: 1000,
                outMs: 2000,
                recording,
                opts => new MediaFoundationVideoEncoder(opts, NullLogger.Instance),
                NullLogger.Instance);

            Assert.True(File.Exists(trimmed), "trim did not produce an output file");
            Assert.True(new FileInfo(trimmed).Length > 1_000, "trimmed MP4 is implausibly small");

            (bool trimOpened, int tw, int th, double trimDurationMs) = ProbeMedia(trimmed);
            Assert.True(trimOpened, "trimmed clip failed to open");
            Assert.Equal(width, tw);
            Assert.Equal(height, th);

            // The source spans 45 frames (3000ms) and the trim is exactly the 1000ms window.
            Assert.InRange(srcDurationMs, 2_999.8, 3_000.01);
            Assert.InRange(trimDurationMs, 999.8, 1_000.01);
        }
        finally
        {
            try { TestRecycleBin.DeleteDirectory(dir, recursive: true); } catch (IOException) { }
        }
    });

    [Fact]
    public void TrimReencode_FastPath_PreservesFrameContent() => RunSta(() =>
    {
        // The composition-bypass trim feeds decoded pixels straight to the encoder. An
        // asymmetric, per-frame pattern makes a vertical flip, stride shift, or channel
        // swap visible as large per-cell pixel differences against the source decode.
        const int width = 320;
        const int height = 240;
        const int fps = 15;
        const int frameCount = 45;

        string dir = TestRecycleBin.CreateTempSubdirectory("mycapture-trimpixels-").FullName;
        Directory.CreateDirectory(dir);
        string source = Path.Combine(dir, "source.mp4");
        string trimmed = Path.Combine(dir, "source_trim.mp4");

        try
        {
            var options = new VideoEncoderOptions(source, width, height, fps, VideoEncoderOptions.DeriveBitrate(width, height, fps));
            using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
            {
                int stride = width * 4;
                byte[] frame = new byte[stride * height];
                double frameMs = 1000.0 / fps;
                for (int i = 0; i < frameCount; i++)
                {
                    FillGradient(frame, width, height, stride, i);
                    encoder.WriteFrame(new EncoderFrame(frame, width, height, stride, i * frameMs));
                }

                encoder.Complete();
            }

            (_, _, _, double srcDurationMs) = ProbeMedia(source);
            var recording = new MyCapture.Platform.Recording.RecordingResult(source, srcDurationMs, fps, frameCount, width, height);
            MyCapture.App.Recording.TrimReencoder.Reencode(
                source,
                trimmed,
                inMs: 1000,
                outMs: 2000,
                recording,
                opts => new MediaFoundationVideoEncoder(opts, NullLogger.Instance),
                NullLogger.Instance);

            using var sourceReader = new MediaFoundationVideoFrameReader(source);
            using var trimmedReader = new MediaFoundationVideoFrameReader(trimmed);
            foreach (double outputTimeMs in new[] { 100.0, 500.0, 900.0 })
            {
                DecodedVideoFrame expected = sourceReader.ReadFrameAt(outputTimeMs + 1000);
                DecodedVideoFrame actual = trimmedReader.ReadFrameAt(outputTimeMs);
                Assert.Equal(expected.Width, actual.Width);
                Assert.Equal(expected.Height, actual.Height);
                AssertCellDifferencesBelow(expected, actual, 30, $"output t={outputTimeMs}ms");
            }
        }
        finally
        {
            try { TestRecycleBin.DeleteDirectory(dir, recursive: true); } catch (IOException) { }
        }
    });

    /// <summary>
    /// Compares the frames cell by cell with the mean absolute pixel difference. A lossy
    /// re-encode stays well under the bound; a flipped, shifted, or channel-swapped frame
    /// blows far past it because the fixture pattern is asymmetric in x, y and channel.
    /// </summary>
    private static void AssertCellDifferencesBelow(DecodedVideoFrame expected, DecodedVideoFrame actual,
        double bound, string because)
    {
        const int cells = 4;
        int cellWidth = expected.Width / cells;
        int cellHeight = expected.Height / cells;
        for (int cy = 0; cy < cells; cy++)
        {
            for (int cx = 0; cx < cells; cx++)
            {
                double total = 0;
                long count = 0;
                for (int y = cy * cellHeight; y < (cy + 1) * cellHeight; y += 3)
                {
                    int expectedRow = y * expected.Stride;
                    int actualRow = y * actual.Stride;
                    for (int x = cx * cellWidth; x < (cx + 1) * cellWidth; x += 3)
                    {
                        int e = expectedRow + (x * 4);
                        int a = actualRow + (x * 4);
                        total += Math.Abs(expected.Pixels[e] - actual.Pixels[a]);
                        total += Math.Abs(expected.Pixels[e + 1] - actual.Pixels[a + 1]);
                        total += Math.Abs(expected.Pixels[e + 2] - actual.Pixels[a + 2]);
                        count++;
                    }
                }

                double mean = count == 0 ? 0 : total / (count * 3);
                Assert.True(mean < bound, $"{because}: cell ({cx},{cy}) mean |diff| {mean:F1} exceeds {bound}");
            }
        }
    }
}
