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
        var thread = new Thread(() =>
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

        string dir = Path.Combine(Path.GetTempPath(), "mycapture-mftest-" + Guid.NewGuid().ToString("N"));
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

            // Duration should be in the right ballpark for 30 frames at 15fps (~2s).
            // Allow a wide tolerance because container timebase rounding varies by encoder.
            Assert.True(durationMs >= 1_000 && durationMs <= 4_000, $"unexpected duration {durationMs}ms");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    });

    [Fact]
    public void Encode_PreservesOrientation_NoVerticalFlip_NoHorizontalMirror() => RunSta(() =>
    {
        // Encode frames whose TOP-LEFT quadrant is bright red and the rest near-black. After a
        // round-trip, the bright quadrant must still be top-left. This catches the classic MF
        // RGB32 bottom-up bug (would move it to bottom-left) and any horizontal mirror.
        const int width = 160, height = 120, fps = 15, frames = 20;
        string dir = Path.Combine(Path.GetTempPath(), "mc-orient-" + Guid.NewGuid().ToString("N"));
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
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
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
        double durationMs = player.NaturalDuration.HasTimeSpan
            ? player.NaturalDuration.TimeSpan.TotalMilliseconds
            : 0;

        player.Close();
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

        string dir = Path.Combine(Path.GetTempPath(), "mycapture-trimtest-" + Guid.NewGuid().ToString("N"));
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

            // The trimmed clip must be clearly shorter than the source and roughly the 1s window.
            Assert.True(trimDurationMs < srcDurationMs, "trimmed clip is not shorter than the source");
            Assert.True(trimDurationMs <= 2_000, $"trimmed duration {trimDurationMs}ms exceeds the selected window");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    });
}
