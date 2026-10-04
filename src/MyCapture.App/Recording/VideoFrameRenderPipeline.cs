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
    IReadOnlyList<TimedTextOverlay> TextOverlays,
    IReadOnlyList<FrameEditLayer> FrameEditLayers);

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
        CancellationToken cancellationToken = default,
        Action<DecodedVideoFrame, int>? acceptRawFrame = null)
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
            cancellationToken,
            acceptRawFrame);
    }

    /// <summary>Renders a monotonic, caller-defined source schedule in one decoder session.</summary>
    /// <remarks>
    /// <para>
    /// When <paramref name="acceptRawFrame"/> is supplied and the request needs no
    /// composition — no text overlays, no edit layers, and output at the decoded size —
    /// decoded frames bypass WPF composition entirely. A plain trim export then pays only
    /// decode plus encode, which is the dominant win on low-end machines.
    /// </para>
    /// <para>
    /// Otherwise each composed frame goes to <paramref name="acceptFrame"/>, which must
    /// consume it before the next call: the pipeline reuses its composition buffers across
    /// frames so a long export does not churn the large object heap.
    /// </para>
    /// </remarks>
    internal static int RenderScheduled(
        VideoFrameRenderRequest request,
        IReadOnlyList<double> sourceTimes,
        Action<BitmapSource, int> acceptFrame,
        IProgress<VideoFrameRenderProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Action<DecodedVideoFrame, int>? acceptRawFrame = null)
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

        IReadOnlyDictionary<Guid, BitmapSource> frameLayerBitmaps =
            FrameEditLayerRenderer.Decode(request.FrameEditLayers);

        if (acceptRawFrame is not null
            && CanBypassComposition(request, reader.Width, reader.Height))
        {
            for (int index = 0; index < sourceTimes.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double sourceTime = sourceTimes[index];
                DecodedVideoFrame decoded = reader.ReadFrameAt(sourceTime, cancellationToken);
                if (decoded.Width != request.Width || decoded.Height != request.Height)
                {
                    // A mid-stream media-type change resized the decoder output; the raw
                    // path cannot rescale, and feeding mismatched buffers to the encoder
                    // would corrupt the output silently.
                    throw new InvalidDataException(
                        "The source video changed resolution mid-stream; a plain trim export needs a constant frame size.");
                }

                acceptRawFrame(decoded, index);
                progress?.Report(new VideoFrameRenderProgress(index + 1, sourceTimes.Count, sourceTime));
            }

            return sourceTimes.Count;
        }

        var source = new WriteableBitmap(reader.Width, reader.Height, 96, 96, PixelFormats.Bgr32, null);
        var visual = new DrawingVisual();
        var target = new RenderTargetBitmap(request.Width, request.Height, 96, 96, PixelFormats.Pbgra32);
        for (int index = 0; index < sourceTimes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double sourceTime = sourceTimes[index];

            DecodedVideoFrame decoded = reader.ReadFrameAt(sourceTime, cancellationToken);
            if (decoded.Width != source.PixelWidth || decoded.Height != source.PixelHeight)
            {
                // A mid-stream media-type change resized the decoder output; rebuild the
                // reusable source bitmap for the new dimensions.
                source = new WriteableBitmap(decoded.Width, decoded.Height, 96, 96, PixelFormats.Bgr32, null);
            }

            source.WritePixels(new Int32Rect(0, 0, decoded.Width, decoded.Height), decoded.Pixels, decoded.Stride, 0);
            using (DrawingContext dc = visual.RenderOpen())
            {
                DrawComposedFrame(
                    dc,
                    source,
                    request.Width,
                    request.Height,
                    request.CanvasWidth,
                    request.CanvasHeight,
                    request.TextOverlays,
                    request.FrameEditLayers,
                    frameLayerBitmaps,
                    sourceTime);
            }

            target.Clear();
            target.Render(visual);
            BitmapSource frame = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
            acceptFrame(frame, index);
            progress?.Report(new VideoFrameRenderProgress(index + 1, sourceTimes.Count, sourceTime));
        }

        return sourceTimes.Count;
    }

    /// <summary>
    /// True when the schedule needs no WPF composition: nothing is drawn over the frames
    /// and the output matches the decoded size, so decoded pixels can feed the encoder
    /// directly. Only meaningful when a raw-frame callback was supplied.
    /// </summary>
    internal static bool CanBypassComposition(VideoFrameRenderRequest request, int sourceWidth, int sourceHeight) =>
        request.TextOverlays.Count == 0
        && request.FrameEditLayers.Count == 0
        && request.Width == sourceWidth
        && request.Height == sourceHeight;

    internal static BitmapSource RenderSingleFrame(
        string sourcePath,
        double sourceTimeMs,
        int width,
        int height,
        IReadOnlyList<TimedTextOverlay>? overlays = null,
        IReadOnlyList<FrameEditLayer>? frameEditLayers = null,
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
            overlays ?? [],
            frameEditLayers ?? []);
        _ = Render(request, (frame, _) => result = frame, cancellationToken: cancellationToken);
        return FrozenCopy(result ?? throw new InvalidOperationException("No video frame was rendered."));
    }

    /// <summary>
    /// Copies a composed frame into a standalone frozen bitmap. The render pipeline reuses
    /// its composition buffers across frames, so a caller that keeps a frame beyond the
    /// render loop (thumbnails, previews, anything crossing an STA task boundary) needs its
    /// own pixels.
    /// </summary>
    internal static BitmapSource FrozenCopy(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        int stride = checked(source.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(pixels, stride, 0);
        BitmapSource copy = BitmapSource.Create(
            source.PixelWidth,
            source.PixelHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        copy.Freeze();
        return copy;
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

    internal static byte[] ToBgra32(BitmapSource source, int width, int height, out int stride, byte[]? destination = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        stride = checked(width * 4);
        byte[] buffer = destination is not null && destination.Length >= checked(stride * height)
            ? destination
            : new byte[checked(stride * height)];
        int copyWidth = Math.Min(width, bgra.PixelWidth);
        int copyHeight = Math.Min(height, bgra.PixelHeight);
        bgra.CopyPixels(new Int32Rect(0, 0, copyWidth, copyHeight), buffer, stride, 0);
        return buffer;
    }

    internal static BitmapSource RenderFrame(
        ImageSource source,
        int width,
        int height,
        int canvasWidth,
        int canvasHeight,
        IReadOnlyList<TimedTextOverlay> overlays,
        IReadOnlyList<FrameEditLayer> frameEditLayers,
        IReadOnlyDictionary<Guid, BitmapSource> frameLayerBitmaps,
        double sourceTimeMs)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            DrawComposedFrame(
                dc,
                source,
                width,
                height,
                canvasWidth,
                canvasHeight,
                overlays,
                frameEditLayers,
                frameLayerBitmaps,
                sourceTimeMs);
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static void DrawComposedFrame(
        DrawingContext dc,
        ImageSource source,
        int width,
        int height,
        int canvasWidth,
        int canvasHeight,
        IReadOnlyList<TimedTextOverlay> overlays,
        IReadOnlyList<FrameEditLayer> frameEditLayers,
        IReadOnlyDictionary<Guid, BitmapSource> frameLayerBitmaps,
        double sourceTimeMs)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
        dc.DrawImage(source, new Rect(0, 0, width, height));
        double scaleX = (double)width / canvasWidth;
        double scaleY = (double)height / canvasHeight;
        dc.PushTransform(new ScaleTransform(scaleX, scaleY));
        FrameEditLayerRenderer.Draw(
            dc,
            frameEditLayers,
            frameLayerBitmaps,
            sourceTimeMs,
            canvasWidth,
            canvasHeight);
        TimedTextOverlayRenderer.Draw(
            dc,
            overlays,
            sourceTimeMs,
            canvasWidth,
            canvasHeight,
            pixelsPerDip: 1.0);
        dc.Pop();
    }

    private static void Validate(VideoFrameRenderRequest request)
    {
        VideoLayerResourceBudget.Validate(request.FrameEditLayers);
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

/// <summary>Shared transparent frame-layer compositor used by preview, MP4 and GIF export.</summary>
internal static class FrameEditLayerRenderer
{
    internal static IReadOnlyDictionary<Guid, BitmapSource> Decode(
        IReadOnlyList<FrameEditLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        VideoLayerResourceBudget.Validate(layers);
        var decoded = new Dictionary<Guid, BitmapSource>();
        foreach (FrameEditLayer layer in layers)
        {
            if (decoded.ContainsKey(layer.Id)
                || string.IsNullOrWhiteSpace(layer.OverlayPngBase64)
                || layer.OverlayPngBase64.Length > VideoEditDocument.MaximumFrameLayerEncodedLength)
            {
                continue;
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(layer.OverlayPngBase64);
                if (!HasSafePngDimensions(bytes)) { continue; }
                using var stream = new MemoryStream(bytes, writable: false);
                var decoder = new PngBitmapDecoder(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                BitmapSource bitmap = decoder.Frames[0];
                bitmap.Freeze();
                decoded[layer.Id] = bitmap;
            }
            catch (Exception ex) when (ex is FormatException
                                       or NotSupportedException
                                       or InvalidOperationException
                                       or FileFormatException
                                       or ArgumentException
                                       or IOException
                                       or OverflowException)
            {
                // Persisted edit metadata is untrusted. A damaged optional layer is ignored so
                // the source video remains playable and editable.
            }
        }

        return decoded;
    }

    // PNG's IHDR precedes compressed pixels, so reject expansion bombs before WIC allocates them.
    internal static bool HasSafePngDimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) { return false; }
        uint width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4));
        uint height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4));
        ulong decodedBytesPerPixel = bytes[24] == 16 ? 8UL : 4UL;
        return width is > 0 and <= 8192 && height is > 0 and <= 8192
            && (ulong)width * height * decodedBytesPerPixel <= 64 * 1024 * 1024;
    }

    internal static void Draw(
        DrawingContext dc,
        IReadOnlyList<FrameEditLayer> layers,
        IReadOnlyDictionary<Guid, BitmapSource> bitmaps,
        double sourceTimeMs,
        double width,
        double height)
    {
        ArgumentNullException.ThrowIfNull(dc);
        foreach (FrameEditLayer layer in layers)
        {
            if (layer.IsActiveAt(sourceTimeMs) && bitmaps.TryGetValue(layer.Id, out BitmapSource? bitmap))
            {
                dc.DrawImage(bitmap, GetBounds(layer.Bounds, width, height));
            }
        }
    }

    internal static Rect GetBounds(VideoLayerBounds? bounds, double width, double height) =>
        bounds?.Normalize() is { } b
            ? new Rect(b.X * width, b.Y * height, b.Width * width, b.Height * height)
            : new Rect(0, 0, width, height);
}

/// <summary>Shared WYSIWYG text compositor used by preview, MP4 render and GIF export.</summary>
internal static class TimedTextOverlayRenderer
{
    private static readonly Typeface OverlayTypeface = new(
        new FontFamily("Malgun Gothic"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    private static readonly SolidColorBrush OverlayBackground = CreateOverlayBackground();

    private static SolidColorBrush CreateOverlayBackground()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0xC8, 0x08, 0x08, 0x08));
        brush.Freeze();
        return brush;
    }

    internal static IReadOnlyList<TimedTextOverlay> ActiveAt(
        IReadOnlyList<TimedTextOverlay> overlays,
        double sourceTimeMs)
    {
        if (overlays is null || overlays.Count == 0)
        {
            return [];
        }

        var active = new List<TimedTextOverlay>();
        for (int index = 0; index < overlays.Count; index++)
        {
            TimedTextOverlay overlay = overlays[index];
            if (overlay is not null && overlay.IsActiveAt(sourceTimeMs))
            {
                active.Add(overlay);
            }
        }

        return active;
    }

    internal static void Draw(
        DrawingContext dc,
        IReadOnlyList<TimedTextOverlay> overlays,
        double sourceTimeMs,
        double width,
        double height,
        double pixelsPerDip,
        IDictionary<Guid, Rect>? bounds = null)
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

        double maxBoxWidth = Math.Max(1, width - (horizontalMargin * 2));
        double maxTextWidth = Math.Max(1, maxBoxWidth - (paddingX * 2));
        double maxBoxHeight = Math.Min(height, Math.Max(height * 0.4, (fontSize * 1.4) + (paddingY * 2)));
        double maxTextHeight = Math.Max(fontSize * 1.4, maxBoxHeight - (paddingY * 2));
        double dip = Math.Max(1, pixelsPerDip);

        var placementOffsets = new Dictionary<VideoTextPlacement, double>();
        foreach (TimedTextOverlay overlay in active)
        {
            if (string.IsNullOrWhiteSpace(overlay.Text))
            {
                continue;
            }

            var text = new FormattedText(
                overlay.Text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                OverlayTypeface,
                fontSize,
                Brushes.White,
                dip)
            {
                TextAlignment = TextAlignment.Center,
                MaxTextWidth = maxTextWidth,
                MaxTextHeight = maxTextHeight,
            };

            double boxWidth = Math.Max(1, Math.Min(maxBoxWidth, text.Width + (paddingX * 2)));
            double boxHeight = Math.Max(1, Math.Min(maxBoxHeight, text.Height + (paddingY * 2)));
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
            Rect destination = overlay.Bounds is null
                ? box
                : FrameEditLayerRenderer.GetBounds(overlay.Bounds, width, height);
            if (bounds is not null)
            {
                bounds[overlay.Id] = destination;
            }

            dc.PushTransform(new MatrixTransform(
                destination.Width / box.Width, 0, 0, destination.Height / box.Height,
                destination.X - (box.X * destination.Width / box.Width),
                destination.Y - (box.Y * destination.Height / box.Height)));
            dc.DrawRoundedRectangle(OverlayBackground, null, box, paddingY, paddingY);

            double glyphOriginX = x + ((boxWidth - text.MaxTextWidth) / 2);
            double glyphOriginY = y + ((boxHeight - text.Height) / 2);
            dc.DrawText(text, new Point(glyphOriginX, glyphOriginY));
            dc.Pop();

            placementOffsets[overlay.Placement] = offset + boxHeight + Math.Max(6, height * 0.01);
        }
    }

    internal static IReadOnlyDictionary<Guid, Rect> GetBounds(
        IReadOnlyList<TimedTextOverlay> overlays, double time, double width, double height)
    {
        var result = new Dictionary<Guid, Rect>();
        var visual = new DrawingVisual();
        using DrawingContext dc = visual.RenderOpen();
        Draw(dc, overlays, time, width, height, 1, result);
        return result;
    }
}
