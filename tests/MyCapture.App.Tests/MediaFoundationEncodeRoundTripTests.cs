using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
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
