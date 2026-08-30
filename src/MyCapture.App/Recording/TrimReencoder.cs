using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// Re-encodes the <c>[inMs, outMs]</c> window of a recorded clip into a new MP4,
/// non-destructively (the source is untouched).
/// </summary>
/// <remarks>
/// <para>
/// Decodes with a <see cref="MediaPlayer"/> in scrubbing mode: the playhead is parked at
/// each successive frame time, the shown frame is rendered to a bitmap through a
/// <see cref="VisualBrush"/>, and the bitmap is fed to the same
/// <see cref="IVideoEncoder"/> the live recorder uses. This keeps the entire feature on
/// Windows-native, offline components with no extra dependency.
/// </para>
/// <para>
/// Runs on the calling (UI/dispatcher) thread because WPF media objects are affine to a
/// dispatcher. Between seeks it pumps the dispatcher briefly so the decoder can present
/// the requested frame before it is rendered. This is a deliberate simplicity/robustness
/// trade: frame-exact seeking of arbitrary MP4s is notoriously fragile, and a short pump
/// per frame is reliable at the frame rates this app records.
/// </para>
/// </remarks>
internal static class TrimReencoder
{
    public static void Reencode(
        string sourcePath,
        string outputPath,
        double inMs,
        double outMs,
        RecordingResult recording,
        Func<VideoEncoderOptions, IVideoEncoder> encoderFactory,
        ILogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(encoderFactory);

        int fps = Math.Max(1, recording.Fps);
        int width = recording.Width;
        int height = recording.Height;
        double frameMs = 1000.0 / fps;

        var player = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };
        var opened = false;
        player.MediaOpened += (_, _) => opened = true;
        player.Open(new Uri(sourcePath, UriKind.Absolute));

        // Pump until the media reports open (bounded so a failed open cannot hang).
        if (!PumpUntil(() => opened, TimeSpan.FromSeconds(5)))
        {
            player.Close();
            throw new InvalidOperationException("Timed out opening the source clip for trimming.");
        }

        int bitrate = VideoEncoderOptions.DeriveBitrate(width, height, fps);
        var options = new VideoEncoderOptions(outputPath, width, height, fps, bitrate);
        IVideoEncoder encoder = encoderFactory(options);

        try
        {
            long emitted = 0;
            for (double t = inMs; t <= outMs + 0.5; t += frameMs)
            {
                player.Position = TimeSpan.FromMilliseconds(t);

                // Let the scrubbing decoder present the requested position.
                PumpFor(TimeSpan.FromMilliseconds(15));

                BitmapSource frame = RenderPlayerFrame(player, width, height);
                byte[] pixels = ToBgra(frame, width, height, out int stride);

                double outputTimestamp = t - inMs;
                encoder.WriteFrame(new EncoderFrame(pixels, width, height, stride, outputTimestamp));
                emitted++;
            }

            encoder.Complete();
            log.LogInformation("Trim re-encoded {Frames} frame(s) [{In:0}..{Out:0}]ms -> {Path}", emitted, inMs, outMs, outputPath);
        }
        finally
        {
            encoder.Dispose();
            player.Close();
        }
    }

    private static BitmapSource RenderPlayerFrame(MediaPlayer player, int width, int height)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawVideo(player, new Rect(0, 0, width, height));
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static byte[] ToBgra(BitmapSource source, int width, int height, out int stride)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        stride = width * 4;
        byte[] buffer = new byte[stride * height];
        int copyW = Math.Min(width, bgra.PixelWidth);
        int copyH = Math.Min(height, bgra.PixelHeight);
        bgra.CopyPixels(new Int32Rect(0, 0, copyW, copyH), buffer, stride, 0);
        return buffer;
    }

    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            PumpFor(TimeSpan.FromMilliseconds(10));
        }

        return true;
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
}
