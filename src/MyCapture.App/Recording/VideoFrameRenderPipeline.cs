using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>Immutable request for source-timeline video frame composition.</summary>
internal sealed record VideoFrameRenderRequest(
    string SourcePath,
    double StartMs,
    double EndMs,
    int FramesPerSecond,
    int Width,
    int Height,
    int CanvasWidth,
    int CanvasHeight,
    IReadOnlyList<TimedTextOverlay> TextOverlays);

internal sealed record VideoFrameRenderProgress(int CompletedFrames, int TotalFrames, double SourceTimeMs);

internal sealed record VideoMediaInfo(int Width, int Height, double DurationMs);

/// <summary>
/// Decodes timestamped Media Foundation samples and composites the same timed text used by
/// preview. Call this on an STA thread for WPF bitmap composition; no frame collection is retained.
/// </summary>
internal static class VideoFrameRenderPipeline
{
    internal static int Render(
        VideoFrameRenderRequest request,
        Action<BitmapSource, double> acceptFrame,
        IProgress<VideoFrameRenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(acceptFrame);
        Validate(request);

        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("Video frame composition requires an STA thread.");
        }

        double frameMs = 1000.0 / request.FramesPerSecond;
        double span = request.EndMs - request.StartMs;
        int totalFrames = Math.Max(1, checked((int)Math.Ceiling((span / frameMs) - 1e-9)));
        var sourceTimes = new double[totalFrames];
        for (int index = 0; index < totalFrames; index++)
        {
            sourceTimes[index] = request.StartMs + (index * frameMs);
        }

        return RenderScheduled(
            request,
            sourceTimes,
            (frame, index) => acceptFrame(frame, index * frameMs),
            progress,
            cancellationToken);
    }

    /// <summary>Renders a monotonic, caller-defined source schedule in one decoder session.</summary>
    internal static int RenderScheduled(
        VideoFrameRenderRequest request,
        IReadOnlyList<double> sourceTimes,
        Action<BitmapSource, int> acceptFrame,
        IProgress<VideoFrameRenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sourceTimes);
        ArgumentNullException.ThrowIfNull(acceptFrame);
        Validate(request);

        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("Video frame composition requires an STA thread.");
        }

        if (sourceTimes.Count == 0)
        {
            throw new ArgumentException("At least one source frame time is required.", nameof(sourceTimes));
        }

        double previous = double.NegativeInfinity;
        for (int index = 0; index < sourceTimes.Count; index++)
        {
            double sourceTime = sourceTimes[index];
            if (!double.IsFinite(sourceTime)
                || sourceTime < request.StartMs
                || sourceTime >= request.EndMs
                || sourceTime + 1e-9 < previous)
            {
                throw new ArgumentException(
                    "Source frame times must be finite, monotonic, and inside the render interval.",
                    nameof(sourceTimes));
            }

            previous = sourceTime;
        }

        using var reader = new MediaFoundationVideoFrameReader(request.SourcePath);
        if (reader.Width <= 0 || reader.Height <= 0)
        {
            throw new InvalidDataException("The source video reported invalid dimensions.");
        }

        for (int index = 0; index < sourceTimes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double sourceTime = sourceTimes[index];

            DecodedVideoFrame decoded = reader.ReadFrameAt(sourceTime, cancellationToken);
            BitmapSource source = BitmapSource.Create(
                decoded.Width,
                decoded.Height,
                96,
                96,
                PixelFormats.Bgr32,
                palette: null,
                decoded.Pixels,
                decoded.Stride);
            source.Freeze();
            BitmapSource frame = RenderFrame(
                source,
                request.Width,
                request.Height,
                request.CanvasWidth,
                request.CanvasHeight,
                request.TextOverlays,
                sourceTime);
            acceptFrame(frame, index);
            progress?.Report(new VideoFrameRenderProgress(index + 1, sourceTimes.Count, sourceTime));
        }

        return sourceTimes.Count;
    }

    internal static BitmapSource RenderSingleFrame(
        string sourcePath,
        double sourceTimeMs,
        int width,
        int height,
        IReadOnlyList<TimedTextOverlay>? overlays = null,
        CancellationToken cancellationToken = default)
    {
        BitmapSource? result = null;
        var request = new VideoFrameRenderRequest(
            sourcePath,
            Math.Max(0, sourceTimeMs),
            Math.Max(0, sourceTimeMs) + 1,
            1,
            width,
            height,
            width,
            height,
            overlays ?? []);
        _ = Render(request, (frame, _) => result = frame, cancellationToken: cancellationToken);
        return result ?? throw new InvalidOperationException("No video frame was rendered.");
    }

    internal static VideoMediaInfo Probe(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("Video probing requires an STA thread.");
        }

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The source video is unavailable.", sourcePath);
        }

        using var reader = new MediaFoundationVideoFrameReader(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        double duration = reader.DurationMs > 0
            ? reader.DurationMs
            : ProbeContainerDuration(sourcePath, cancellationToken);
        if (reader.Width <= 0
            || reader.Height <= 0
            || !double.IsFinite(duration)
            || duration <= 0)
        {
            throw new InvalidDataException("The video has invalid dimensions or duration.");
        }

        // Decode both ends, not only container metadata. This rejects truncated MP4 files whose
        // header still advertises plausible dimensions and duration.
        _ = reader.ReadFrameAt(0, cancellationToken);
        DecodedVideoFrame finalFrame = reader.ReadFrameAt(Math.Max(0, duration - 1), cancellationToken);
        ValidateDecodedTail(duration, finalFrame);
        return new VideoMediaInfo(reader.Width, reader.Height, duration);
    }

    /// <summary>
    /// Rejects a nominally decodable container when seeking near its advertised end only returns
    /// a much older cached sample. The tolerance covers timestamp rounding, but remains a fraction
    /// of the decoded sample's own duration so a missing final frame is not accepted.
    /// </summary>
    internal static void ValidateDecodedTail(double containerDurationMs, DecodedVideoFrame finalFrame)
    {
        ArgumentNullException.ThrowIfNull(finalFrame);
        if (!double.IsFinite(containerDurationMs) || containerDurationMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(containerDurationMs));
        }

        if (!double.IsFinite(finalFrame.TimestampMs)
            || finalFrame.TimestampMs < 0
            || !double.IsFinite(finalFrame.SampleDurationMs)
            || finalFrame.SampleDurationMs <= 0
            || !double.IsFinite(finalFrame.EndTimestampMs))
        {
            throw new InvalidDataException(
                "The final decoded video sample did not report a usable timeline duration.");
        }

        double toleranceMs = Math.Clamp(finalFrame.SampleDurationMs * 0.25, 2.0, 50.0);
        if (finalFrame.EndTimestampMs + toleranceMs < containerDurationMs)
        {
            throw new InvalidDataException(
                $"The decoded video ends at {finalFrame.EndTimestampMs:F3} ms, before the "
                + $"container duration of {containerDurationMs:F3} ms.");
        }
    }

    internal static byte[] ToBgra32(BitmapSource source, int width, int height, out int stride)
    {
        ArgumentNullException.ThrowIfNull(source);
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        stride = checked(width * 4);
        byte[] buffer = new byte[checked(stride * height)];
        int copyWidth = Math.Min(width, bgra.PixelWidth);
        int copyHeight = Math.Min(height, bgra.PixelHeight);
        bgra.CopyPixels(new Int32Rect(0, 0, copyWidth, copyHeight), buffer, stride, 0);
        return buffer;
    }

    private static BitmapSource RenderFrame(
        ImageSource source,
        int width,
        int height,
        int canvasWidth,
        int canvasHeight,
        IReadOnlyList<TimedTextOverlay> overlays,
        double sourceTimeMs)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            dc.DrawImage(source, new Rect(0, 0, width, height));
            double scaleX = (double)width / canvasWidth;
            double scaleY = (double)height / canvasHeight;
            dc.PushTransform(new ScaleTransform(scaleX, scaleY));
            TimedTextOverlayRenderer.Draw(
                dc,
                overlays,
                sourceTimeMs,
                canvasWidth,
                canvasHeight,
                pixelsPerDip: 1.0);
            dc.Pop();
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static void Validate(VideoFrameRenderRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath) || !File.Exists(request.SourcePath))
        {
            throw new FileNotFoundException("The source video is unavailable.", request.SourcePath);
        }

        if (!double.IsFinite(request.StartMs)
            || !double.IsFinite(request.EndMs)
            || request.StartMs < 0
            || request.EndMs <= request.StartMs)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "A positive source interval is required.");
        }

        if (request.FramesPerSecond is < 1 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Frame rate must be between 1 and 120.");
        }

        if (request.Width is < 1 or > 65_535
            || request.Height is < 1 or > 65_535
            || request.CanvasWidth is < 1 or > 65_535
            || request.CanvasHeight is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Frame dimensions are invalid.");
        }
    }

    private static double ProbeContainerDuration(string sourcePath, CancellationToken cancellationToken)
    {
        var player = new MediaPlayer { Volume = 0, ScrubbingEnabled = true };
        bool opened = false;
        Exception? failure = null;
        player.MediaOpened += (_, _) => opened = true;
        player.MediaFailed += (_, e) => failure = e.ErrorException;
        try
        {
            player.Open(new Uri(Path.GetFullPath(sourcePath), UriKind.Absolute));
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (!opened && failure is null && DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(10),
                };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    frame.Continue = false;
                };
                timer.Start();
                Dispatcher.PushFrame(frame);
            }

            if (failure is not null)
            {
                throw new InvalidDataException("The video container could not be opened.", failure);
            }

            if (!opened || !player.NaturalDuration.HasTimeSpan)
            {
                return 0;
            }

            return player.NaturalDuration.TimeSpan.TotalMilliseconds;
        }
        finally
        {
            player.Close();
        }
    }

}

/// <summary>Shared WYSIWYG text compositor used by preview, MP4 render and GIF export.</summary>
internal static class TimedTextOverlayRenderer
{
    internal static IReadOnlyList<TimedTextOverlay> ActiveAt(
        IReadOnlyList<TimedTextOverlay> overlays,
        double sourceTimeMs) => overlays.Where(overlay => overlay.IsActiveAt(sourceTimeMs)).ToList();

    internal static void Draw(
        DrawingContext dc,
        IReadOnlyList<TimedTextOverlay> overlays,
        double sourceTimeMs,
        double width,
        double height,
        double pixelsPerDip)
    {
        ArgumentNullException.ThrowIfNull(dc);
        IReadOnlyList<TimedTextOverlay> active = ActiveAt(overlays, sourceTimeMs);
        if (active.Count == 0 || width <= 0 || height <= 0)
        {
            return;
        }

        double fontSize = Math.Clamp(height * 0.052, 18, 72);
        double horizontalMargin = Math.Max(16, width * 0.06);
        double verticalMargin = Math.Max(14, height * 0.05);
        double paddingX = Math.Max(10, fontSize * 0.45);
        double paddingY = Math.Max(6, fontSize * 0.24);
        var typeface = new Typeface(new FontFamily("Malgun Gothic"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var foreground = Brushes.White;
        var background = new SolidColorBrush(Color.FromArgb(0xC8, 0x08, 0x08, 0x08));
        background.Freeze();

        var placementOffsets = new Dictionary<VideoTextPlacement, double>();
        foreach (TimedTextOverlay overlay in active)
        {
            var text = new FormattedText(
                overlay.Text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                foreground,
                Math.Max(1, pixelsPerDip))
            {
                TextAlignment = TextAlignment.Center,
                MaxTextWidth = Math.Max(1, width - (horizontalMargin * 2) - (paddingX * 2)),
                MaxTextHeight = Math.Max(fontSize * 1.4, height * 0.35),
            };

            double boxWidth = Math.Min(width - (horizontalMargin * 2), text.Width + (paddingX * 2));
            double boxHeight = Math.Min(height * 0.4, text.Height + (paddingY * 2));
            placementOffsets.TryGetValue(overlay.Placement, out double offset);
            double x = (width - boxWidth) / 2;
            double y = overlay.Placement switch
            {
                VideoTextPlacement.Top => verticalMargin + offset,
                VideoTextPlacement.Center => ((height - boxHeight) / 2) + offset,
                _ => height - verticalMargin - boxHeight - offset,
            };
            y = Math.Clamp(y, 0, Math.Max(0, height - boxHeight));

            var box = new Rect(x, y, boxWidth, boxHeight);
            dc.DrawRoundedRectangle(background, null, box, paddingY, paddingY);
            dc.DrawText(text, new Point(x + paddingX, y + paddingY));
            placementOffsets[overlay.Placement] = offset + boxHeight + Math.Max(6, height * 0.01);
        }
    }
}
