using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
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
    IReadOnlyList<FrameEditLayer> FrameEditLayers,
    IReadOnlyList<VideoTimeRange>? RemovedRanges = null);

internal sealed record VideoFrameRenderProgress(int CompletedFrames, int TotalFrames, double SourceTimeMs);

internal sealed record VideoMediaInfo(int Width, int Height, double DurationMs);

/// <summary>MP4 render tunables. <see cref="Default"/> is what every production export uses.</summary>
internal sealed record Mp4RenderOptions
{
    internal static Mp4RenderOptions Default { get; } = new();

    /// <summary>
    /// Decode to NV12 and give every frame with nothing drawn over it to an NV12 encoder input
    /// unchanged; only frames with an active layer get those layers drawn onto their samples.
    /// When false, or when the source cannot take that path, every frame is decoded to RGB and
    /// the encoder converts it back, as before. Kept for comparison.
    /// </summary>
    internal bool PassThroughNv12 { get; init; } = true;

    /// <summary>
    /// Decode NV12 through D3D11/DXVA when available. Software decode is the automatic fallback,
    /// both when the device cannot be opened and when it fails during the export.
    /// </summary>
    internal bool PreferHardwareDecode { get; init; } = true;

    /// <summary>
    /// On the NV12 path, render the active layers once per distinct set and blend them onto the
    /// decoded samples with <see cref="Nv12LayerCompositor"/> (default). When false, every layered
    /// frame is composed in full by the WPF compositor, about 30 ms at 1080p. Kept for comparison.
    /// </summary>
    internal bool BlendCachedLayers { get; init; } = true;
}

/// <summary>Where one MP4 render's frames came from and how many needed composition.</summary>
internal sealed record Mp4RenderStatistics(string FrameSource, int Frames, int ComposedFrames);

/// <summary>A hardware-decoded MP4 render failed after the decoder opened; software can retry it.</summary>
internal sealed class Mp4HardwareDecodeException : InvalidOperationException
{
    internal Mp4HardwareDecodeException(Exception inner)
        : base("Hardware video decode failed during MP4 export.", inner)
    {
    }
}

/// <summary>
/// Decodes timestamped Media Foundation samples and composites the same timed text used by
/// preview. Call this on an STA thread for WPF bitmap composition; no frame collection is retained.
/// </summary>
internal static class VideoFrameRenderPipeline
{
    /// <summary>Decoded NV12 frames the MP4 decode thread may hold ahead of the encoder.</summary>
    private const int DecodeAheadFrames = 4;

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
        return RenderScheduled(
            request,
            BuildSchedule(request),
            (frame, index) => acceptFrame(frame, index * frameMs),
            progress,
            cancellationToken,
            acceptRawFrame);
    }

    /// <summary>Maps the output cadence onto kept source segments; each segment end is exclusive.</summary>
    internal static double[] BuildSchedule(VideoFrameRenderRequest request)
    {
        VideoCutTimeline timeline = VideoCutTimeline.Create(request.StartMs, request.EndMs, request.RemovedRanges);
        double frameMs = 1000.0 / request.FramesPerSecond;
        double span = timeline.OutputDurationMs;
        int totalFrames = Math.Max(1, checked((int)Math.Ceiling((span / frameMs) - 1e-9)));
        var sourceTimes = new double[totalFrames];
        for (int index = 0; index < totalFrames; index++)
        {
            sourceTimes[index] = timeline.ToSourceTime(index * frameMs);
        }

        return sourceTimes;
    }

    /// <summary>
    /// Renders the request's interval into an encoder that <paramref name="openEncoder"/> opens
    /// once the decoder has shown which input layout it can feed. Frame <c>i</c> is written at
    /// <c>i / fps</c>, exactly like <see cref="Render"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default path reads the decoder's own NV12 and hands every frame with nothing drawn over
    /// it to an NV12 encoder input untouched, so a plain trim is decode plus encode with no colour
    /// conversion at all (the RGB path converted every frame to RGB and the Sink Writer converted
    /// it straight back). A frame with an active text or frame layer gets those layers, rendered
    /// by the shared WPF layer code on this STA thread once per distinct active set, blended onto
    /// the 2x2 blocks they cover by <see cref="Nv12LayerCompositor"/>; the rest of the frame keeps
    /// the decoder's samples. Decoding runs on <see cref="Mp4FrameDecoder"/>'s thread a few
    /// frames ahead, so it overlaps encoder submission; WPF and the encoder stay on this thread.
    /// </para>
    /// <para>
    /// The original RGB path remains for sources that cannot pass through unchanged: no NV12
    /// decoder output, an odd or rescaled frame size, a colour description the encoder would
    /// not reproduce, or an encoder that rejects NV12 input. The caller owns the encoder it
    /// opens: completion, disposal and, for <see cref="Mp4HardwareDecodeException"/>, a software
    /// retry into a fresh encoder.
    /// </para>
    /// </remarks>
    internal static Mp4RenderStatistics RenderToEncoder(
        VideoFrameRenderRequest request,
        Func<VideoEncoderInputFormat, IVideoEncoder> openEncoder,
        Mp4RenderOptions options,
        ILogger log,
        IProgress<VideoFrameRenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(openEncoder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        Validate(request);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("Video frame composition requires an STA thread.");
        }

        double[] sourceTimes = BuildSchedule(request);
        IReadOnlyDictionary<Guid, BitmapSource> frameLayerBitmaps =
            FrameEditLayerRenderer.Decode(request.FrameEditLayers);
        if (options.PassThroughNv12 && (request.Width & 1) == 0 && (request.Height & 1) == 0)
        {
            using Mp4FrameDecoder decoder = Mp4FrameDecoder.Start(
                request.SourcePath,
                new MediaFoundationVideoFrameReaderOptions
                {
                    PreferHardwareDecode = options.PreferHardwareDecode,
                    OutputFormat = DecodedVideoPixelFormat.Nv12,
                    Log = log,
                },
                sourceTimes,
                DecodeAheadFrames,
                cancellationToken);
            Mp4ReaderInfo info = decoder.WaitForOpen(cancellationToken);
            if (CanPassThroughNv12(request, info)
                && TryOpenEncoder(openEncoder, VideoEncoderInputFormat.Nv12, log) is { } nv12Encoder)
            {
                return RenderNv12(
                    request,
                    sourceTimes,
                    decoder,
                    info,
                    nv12Encoder,
                    frameLayerBitmaps,
                    options,
                    progress,
                    cancellationToken);
            }
        }

        MediaFoundationVideoFrameReader? reader = null;
        try
        {
            reader = new MediaFoundationVideoFrameReader(request.SourcePath);
            if (reader.Width <= 0 || reader.Height <= 0)
            {
                throw new InvalidDataException("The source video reported invalid dimensions.");
            }

            IVideoEncoder encoder = openEncoder(VideoEncoderInputFormat.Bgra32);
            double frameMs = 1000.0 / request.FramesPerSecond;
            byte[]? pixelBuffer = null;
            int composed = 0;
            int emitted = RenderFrames(
                request,
                sourceTimes,
                reader,
                frameLayerBitmaps,
                (frame, index) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pixelBuffer = ToBgra32(frame, request.Width, request.Height, out int stride, pixelBuffer);
                    encoder.WriteFrame(new EncoderFrame(pixelBuffer, request.Width, request.Height, stride, index * frameMs));
                    composed++;
                },
                progress,
                cancellationToken,
                (decoded, index) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Nothing is drawn over this frame and it has the output size: decoded
                    // BGRA pixels go straight to the encoder, skipping WPF composition.
                    encoder.WriteFrame(new EncoderFrame(
                        decoded.Pixels,
                        decoded.Width,
                        decoded.Height,
                        decoded.Stride,
                        index * frameMs));
                });
            return new Mp4RenderStatistics("rgb-software", emitted, composed);
        }
        finally
        {
            reader?.Dispose();
        }
    }

    /// <summary>
    /// True when decoded NV12 can be encoded unchanged: the decoder kept NV12 at the output size,
    /// and its colour encoding is the one an undeclared stream of that size is decoded with, which
    /// is all the encoder signals. A source that declares anything else takes the RGB path, whose
    /// converters translate it.
    /// </summary>
    internal static bool CanPassThroughNv12(VideoFrameRenderRequest request, Mp4ReaderInfo info)
    {
        ArgumentNullException.ThrowIfNull(request);
        return info.Format == DecodedVideoPixelFormat.Nv12
            && info.Width == request.Width
            && info.Height == request.Height
            && (request.Width & 1) == 0
            && (request.Height & 1) == 0
            && info.Yuv == DefaultYuvEncoding(request.Height);
    }

    /// <summary>
    /// The Media Foundation convention for a stream without a colour description: studio range,
    /// BT.709 from 720 coded lines (H.264 codes whole 16-line macroblock rows) and BT.601 below.
    /// The Sink Writer's RGB to Y'CbCr converter follows it too: 1080p RGB input encodes as
    /// BT.709 and 320x240 as BT.601.
    /// </summary>
    internal static VideoYuvEncoding DefaultYuvEncoding(int height) =>
        new(Bt709: checked((height + 15) & ~15) >= 720, FullRange: false);

    private static IVideoEncoder? TryOpenEncoder(
        Func<VideoEncoderInputFormat, IVideoEncoder> openEncoder,
        VideoEncoderInputFormat format,
        ILogger log)
    {
        try
        {
            return openEncoder(format);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            log.LogWarning(ex, "The video encoder rejected {Format} input; using RGB input instead.", format);
            return null;
        }
    }

    private static Mp4RenderStatistics RenderNv12(
        VideoFrameRenderRequest request,
        double[] sourceTimes,
        Mp4FrameDecoder decoder,
        Mp4ReaderInfo info,
        IVideoEncoder encoder,
        IReadOnlyDictionary<Guid, BitmapSource> frameLayerBitmaps,
        Mp4RenderOptions options,
        IProgress<VideoFrameRenderProgress>? progress,
        CancellationToken cancellationToken)
    {
        int width = request.Width;
        int height = request.Height;
        double frameMs = 1000.0 / request.FramesPerSecond;
        VideoYuvEncoding encoding = info.Yuv;
        Nv12LayerCompositor? layerCompositor = null;
        VideoFrameCompositor? compositor = null;
        byte[]? composedBgra = null;
        byte[]? composedNv12 = null;
        int composed = 0;
        for (int index = 0; index < sourceTimes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double sourceTime = sourceTimes[index];
            DecodedVideoFrame decoded = decoder.Take(cancellationToken);
            if (decoded.Format != DecodedVideoPixelFormat.Nv12 || decoded.Width != width || decoded.Height != height)
            {
                // A mid-stream media-type change resized the decoder output; feeding mismatched
                // buffers to the encoder would corrupt the output silently.
                throw new InvalidDataException(
                    "The source video changed resolution mid-stream; an MP4 export needs a constant frame size.");
            }

            EncoderFrame frame;
            if (CanBypassComposition(request, frameLayerBitmaps, width, height, sourceTime))
            {
                frame = new EncoderFrame(decoded.Pixels, width, height, decoded.Stride, index * frameMs)
                {
                    Format = VideoEncoderInputFormat.Nv12,
                };
            }
            else
            {
                composedNv12 ??= new byte[BgraToNv12Converter.RequiredLength(width, height)];
                if (options.BlendCachedLayers)
                {
                    layerCompositor ??= new Nv12LayerCompositor(width, height);
                    layerCompositor.Compose(request, frameLayerBitmaps, decoded.Pixels, encoding, sourceTime, composedNv12);
                }
                else
                {
                    byte[] bgra = Nv12FrameConverter.ToBgra(decoded.Pixels, width, height, encoding, width, height);
                    compositor ??= new VideoFrameCompositor(width, height);
                    BitmapSource composedFrame = compositor.Compose(
                        request, frameLayerBitmaps, bgra, width, height, width * 4, sourceTime);
                    composedBgra = ToBgra32(composedFrame, width, height, out int stride, composedBgra);
                    BgraToNv12Converter.Convert(composedBgra, stride, width, height, encoding, composedNv12);
                }

                frame = new EncoderFrame(composedNv12, width, height, width, index * frameMs)
                {
                    Format = VideoEncoderInputFormat.Nv12,
                };
                composed++;
            }

            cancellationToken.ThrowIfCancellationRequested();
            encoder.WriteFrame(frame);
            progress?.Report(new VideoFrameRenderProgress(index + 1, sourceTimes.Length, sourceTime));
        }

        return new Mp4RenderStatistics(
            info.IsHardwareAccelerated ? "nv12-hardware" : "nv12-software",
            sourceTimes.Length,
            composed);
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

        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("Video frame composition requires an STA thread.");
        }

        ValidateSchedule(request, sourceTimes);

        using var reader = new MediaFoundationVideoFrameReader(request.SourcePath);
        if (reader.Width <= 0 || reader.Height <= 0)
        {
            throw new InvalidDataException("The source video reported invalid dimensions.");
        }

        return RenderFrames(
            request,
            sourceTimes,
            reader,
            FrameEditLayerRenderer.Decode(request.FrameEditLayers),
            acceptFrame,
            progress,
            cancellationToken,
            acceptRawFrame);
    }

    /// <summary>The <see cref="RenderScheduled"/> loop over an open BGR32 reader.</summary>
    private static int RenderFrames(
        VideoFrameRenderRequest request,
        IReadOnlyList<double> sourceTimes,
        MediaFoundationVideoFrameReader reader,
        IReadOnlyDictionary<Guid, BitmapSource> frameLayerBitmaps,
        Action<BitmapSource, int> acceptFrame,
        IProgress<VideoFrameRenderProgress>? progress,
        CancellationToken cancellationToken,
        Action<DecodedVideoFrame, int>? acceptRawFrame)
    {
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

        var compositor = new VideoFrameCompositor(request.Width, request.Height);
        for (int index = 0; index < sourceTimes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double sourceTime = sourceTimes[index];

            DecodedVideoFrame decoded = reader.ReadFrameAt(sourceTime, cancellationToken);
            if (acceptRawFrame is not null
                && CanBypassComposition(request, frameLayerBitmaps, decoded.Width, decoded.Height, sourceTime))
            {
                // Nothing is drawn over this frame and it already has the output size, so
                // composition would be an exact copy (see the per-frame overload).
                acceptRawFrame(decoded, index);
            }
            else
            {
                acceptFrame(compositor.Compose(request, frameLayerBitmaps, decoded, sourceTime), index);
            }

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

    /// <summary>
    /// Per-frame form: true when the frame at <paramref name="sourceTimeMs"/> already has the
    /// output size and nothing would be drawn over it — no active overlay with visible text and
    /// no active frame layer whose bitmap decoded. Composing such a frame only copies it (black
    /// fill, 1:1 image), so skipping composition cannot change its pixels. Text-free segments of
    /// an edited clip therefore avoid <see cref="RenderTargetBitmap"/> entirely.
    /// </summary>
    internal static bool CanBypassComposition(
        VideoFrameRenderRequest request,
        IReadOnlyDictionary<Guid, BitmapSource> frameLayerBitmaps,
        int frameWidth,
        int frameHeight,
        double sourceTimeMs)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(frameLayerBitmaps);
        if (request.Width != frameWidth || request.Height != frameHeight)
        {
            return false;
        }

        foreach (TimedTextOverlay overlay in request.TextOverlays)
        {
            if (overlay is not null
                && overlay.IsActiveAt(sourceTimeMs)
                && !string.IsNullOrWhiteSpace(overlay.Text))
            {
                return false;
            }
        }

        foreach (FrameEditLayer layer in request.FrameEditLayers)
        {
            if (layer is not null
                && layer.IsActiveAt(sourceTimeMs)
                && frameLayerBitmaps.ContainsKey(layer.Id))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Validates a request plus a caller-defined monotonic schedule inside its interval.</summary>
    internal static void ValidateSchedule(VideoFrameRenderRequest request, IReadOnlyList<double> sourceTimes)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sourceTimes);
        Validate(request);
        if (sourceTimes.Count == 0)
        {
            throw new ArgumentException("At least one source frame time is required.", nameof(sourceTimes));
        }

        VideoCutTimeline timeline = VideoCutTimeline.Create(request.StartMs, request.EndMs, request.RemovedRanges);
        double previous = double.NegativeInfinity;
        for (int index = 0; index < sourceTimes.Count; index++)
        {
            double sourceTime = sourceTimes[index];
            if (!double.IsFinite(sourceTime)
                || sourceTime < request.StartMs
                || sourceTime >= request.EndMs
                || timeline.IsRemoved(sourceTime)
                || sourceTime + 1e-9 < previous)
            {
                throw new ArgumentException(
                    "Source frame times must be finite, monotonic, and inside the render interval.",
                    nameof(sourceTimes));
            }

            previous = sourceTime;
        }
    }

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
        // header still advertises plausible dimensions and duration. The final request seeks to
        // the key frame before the end, so validation decodes about one key-frame interval
        // instead of the whole clip; a missing tail still surfaces as an early final sample.
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
                "The final decoded video sample did not report a usable timeline duration. "
                + DescribeDecodedTail(containerDurationMs, finalFrame));
        }

        double toleranceMs = Math.Clamp(finalFrame.SampleDurationMs * 0.25, 2.0, 50.0);
        if (finalFrame.EndTimestampMs + toleranceMs < containerDurationMs)
        {
            throw new InvalidDataException(
                $"The decoded video ends at {finalFrame.EndTimestampMs:F3} ms, before the "
                + $"container duration of {containerDurationMs:F3} ms. "
                + DescribeDecodedTail(containerDurationMs, finalFrame));
        }
    }

    internal static string DescribeDecodedTail(double containerDurationMs, DecodedVideoFrame finalFrame) =>
        FormattableString.Invariant($"container={containerDurationMs:F3} ms, lastPts={finalFrame.TimestampMs:F3} ms, sampleDuration={finalFrame.SampleDurationMs:F3} ms, inferredDuration={finalFrame.SampleDurationInferred}, decodedEnd={finalFrame.EndTimestampMs:F3} ms, tolerance={Math.Clamp(finalFrame.SampleDurationMs * 0.25, 2.0, 50.0):F3} ms");

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

    internal static void DrawComposedFrame(
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
        DrawLayers(
            dc,
            width,
            height,
            canvasWidth,
            canvasHeight,
            overlays,
            frameEditLayers,
            frameLayerBitmaps,
            sourceTimeMs);
    }

    /// <summary>
    /// The frame layers and timed text active at <paramref name="sourceTimeMs"/>, scaled from the
    /// editor canvas to the output size: everything <see cref="DrawComposedFrame"/> draws over
    /// the video.
    /// </summary>
    internal static void DrawLayers(
        DrawingContext dc,
        int width,
        int height,
        int canvasWidth,
        int canvasHeight,
        IReadOnlyList<TimedTextOverlay> overlays,
        IReadOnlyList<FrameEditLayer> frameEditLayers,
        IReadOnlyDictionary<Guid, BitmapSource> frameLayerBitmaps,
        double sourceTimeMs)
    {
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

/// <summary>
/// Reusable composition buffers for one thread: the decoded-frame bitmap, the drawing visual and
/// the render target shared by MP4 rendering and GIF export. The returned bitmap
/// aliases the render target, so it must be consumed (encoded or copied) before the next
/// <see cref="Compose(VideoFrameRenderRequest, IReadOnlyDictionary{Guid, BitmapSource}, byte[], int, int, int, double)"/>
/// call on the same instance. Instances are bound to the thread that created them.
/// </summary>
internal sealed class VideoFrameCompositor
{
    private readonly DrawingVisual _visual = new();
    private readonly RenderTargetBitmap _target;
    private WriteableBitmap? _source;

    internal VideoFrameCompositor(int width, int height)
    {
        _target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
    }

    internal BitmapSource Compose(
        VideoFrameRenderRequest request,
        IReadOnlyDictionary<Guid, BitmapSource> frameLayerBitmaps,
        DecodedVideoFrame decoded,
        double sourceTimeMs) =>
        Compose(request, frameLayerBitmaps, decoded.Pixels, decoded.Width, decoded.Height, decoded.Stride, sourceTimeMs);

    /// <summary>
    /// Draws a BGR32 frame (stretched to the request size) plus the active frame layers and
    /// timed text, and returns the opaque BGRA32 result.
    /// </summary>
    internal BitmapSource Compose(
        VideoFrameRenderRequest request,
        IReadOnlyDictionary<Guid, BitmapSource> frameLayerBitmaps,
        byte[] pixels,
        int width,
        int height,
        int stride,
        double sourceTimeMs)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_source is null || _source.PixelWidth != width || _source.PixelHeight != height)
        {
            // A mid-stream media-type change resized the decoder output; rebuild the
            // reusable source bitmap for the new dimensions.
            _source = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        }

        _source.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        using (DrawingContext dc = _visual.RenderOpen())
        {
            VideoFrameRenderPipeline.DrawComposedFrame(
                dc,
                _source,
                request.Width,
                request.Height,
                request.CanvasWidth,
                request.CanvasHeight,
                request.TextOverlays,
                request.FrameEditLayers,
                frameLayerBitmaps,
                sourceTimeMs);
        }

        _target.Clear();
        _target.Render(_visual);
        return new FormatConvertedBitmap(_target, PixelFormats.Bgra32, null, 0);
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
