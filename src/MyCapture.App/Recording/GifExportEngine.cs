using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>GIF engine tunables. <see cref="Default"/> is what every production export uses.</summary>
internal sealed record GifEngineOptions
{
    internal static GifEngineOptions Default { get; } = new();

    /// <summary>Decode through D3D11/DXVA when available; software decode is the automatic fallback.</summary>
    internal bool PreferHardwareDecode { get; init; } = true;

    /// <summary>
    /// Read the decoder's own NV12 output and convert it on the encoder threads with
    /// <see cref="Nv12FrameConverter"/> (default), which reproduces the system software
    /// converter exactly at a fraction of its cost. When false, the reader's RGB converter is
    /// used instead: the CPU converter for software decode (the original path) or the GPU
    /// video processor for hardware decode, whose near-duplicate colours cost the shared 1080p
    /// clip 8 dB of GIF PSNR and 9% of size. Kept for comparison.
    /// </summary>
    internal bool DecodeNv12 { get; init; } = true;

    /// <summary>
    /// Let the GPU video processor resize to the base size (implies RGB output). Off by default:
    /// on the shared 1080p clip it lowered GIF PSNR and grew the file. Kept for benchmarking.
    /// </summary>
    internal bool GpuResize { get; init; }

    /// <summary>Encoder worker threads; zero picks half the logical processors, from 1 to 4.</summary>
    internal int EncoderThreads { get; init; }

    internal int ResolveEncoderThreads() =>
        EncoderThreads > 0
            ? Math.Min(EncoderThreads, 16)
            : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
}

/// <summary>A hardware-decoded export failed after the decoder opened; software can retry it.</summary>
internal sealed class GifHardwareDecodeException : InvalidOperationException
{
    internal GifHardwareDecodeException(Exception inner)
        : base("Hardware video decode failed during GIF export.", inner)
    {
    }
}

/// <summary>
/// One frame buffer: 32-bit BGRX/BGRA, or packed NV12 straight from the decoder. Buffers are
/// never mutated once published. <paramref name="Opaque"/> is true when every fourth byte of a
/// BGRA buffer is already 255 (scaler and converter output).
/// </summary>
internal readonly record struct GifSourceFrame(
    byte[] Pixels,
    int Width,
    int Height,
    int Stride,
    DecodedVideoPixelFormat Format = DecodedVideoPixelFormat.Bgr32,
    VideoYuvEncoding Yuv = default,
    bool Opaque = false);

/// <summary>Immutable description of one GIF export run.</summary>
/// <param name="Request">Source, interval, output size, canvas size and edit layers.</param>
/// <param name="BaseWidth">Decode normalisation width shared by every preset (cache size).</param>
/// <param name="BaseHeight">Decode normalisation height shared by every preset.</param>
/// <param name="SourceTimesMs">Output frame source times, strictly monotonic.</param>
/// <param name="FrameDelaysCentiseconds">GIF delay of each output frame.</param>
internal sealed record GifRenderPlan(
    VideoFrameRenderRequest Request,
    int BaseWidth,
    int BaseHeight,
    IReadOnlyList<double> SourceTimesMs,
    IReadOnlyList<int> FrameDelaysCentiseconds);

/// <summary>Where one export's frames came from and what it cost.</summary>
internal sealed record GifRenderStatistics(
    string FrameSource,
    int Frames,
    int DecodedFrames,
    int EncoderThreads,
    double ElapsedMs);

/// <summary>
/// Streams a GIF frame schedule as decode → normalise → scale → compose → encode → append.
/// </summary>
/// <remarks>
/// <para>
/// The calling thread decodes (or reads cached frames) in schedule order and appends encoded
/// frames to the writer strictly in output order. A small pool of dedicated STA threads does the
/// CPU-heavy middle: NV12 conversion to the base size, area-average scaling to the preset, WPF
/// composition only for frames with an active text or frame layer, and the WPF/WIC GIF encode. Each worker owns its composition buffers and creates
/// every <see cref="BitmapSource"/> it encodes, so WPF thread affinity is respected; frozen layer
/// bitmaps are shared read-only.
/// </para>
/// <para>
/// Memory is bounded: at most <c>workers + 2</c> frames are in flight between decode and append,
/// and finished frames wait for their turn only within that window. Workers shut their lazily
/// created dispatchers down on exit, exactly like <c>StaThreadTask</c>.
/// </para>
/// <para>
/// All presets decode to the same base size (the recording fitted within 960 px) and scale down
/// from it with <see cref="BgraFrameScaler"/>, so a one-shot export and a cached session export of
/// the same preset produce byte-identical files.
/// </para>
/// </remarks>
internal static class GifExportEngine
{
    private static int s_liveWorkerThreads;

    /// <summary>Encoder workers currently running in this process; zero between exports.</summary>
    internal static int LiveWorkerThreads => Volatile.Read(ref s_liveWorkerThreads);

    internal static GifRenderStatistics Render(
        GifRenderPlan plan,
        AnimatedGifWriter writer,
        GifFrameCache? cache,
        GifEngineOptions options,
        ILogger log,
        IProgress<VideoFrameRenderProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<double>? decodeSuperset = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        VideoFrameRenderPipeline.ValidateSchedule(plan.Request, plan.SourceTimesMs);
        if (plan.FrameDelaysCentiseconds.Count != plan.SourceTimesMs.Count)
        {
            throw new ArgumentException("Every GIF frame needs exactly one delay.", nameof(plan));
        }

        long started = Stopwatch.GetTimestamp();
        IReadOnlyList<double> times = plan.SourceTimesMs;
        int total = times.Count;

        // Fastest reuse: every frame of this preset is already encoded (speed-only change or a
        // return to an earlier preset). Only the container and delays are written.
        if (cache?.TryGetEncoded(plan.Request.Width, plan.Request.Height, times) is { } encodedHit)
        {
            for (int index = 0; index < total; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.AddEncodedFrame(encodedHit[index], plan.FrameDelaysCentiseconds[index]);
                progress?.Report(new VideoFrameRenderProgress(index + 1, total, times[index]));
            }

            return Finish("cache-encoded", 0, 0);
        }

        GifSourceFrame[]? decodedHit = cache?.TryGetDecoded(plan.BaseWidth, plan.BaseHeight, times);
        IReadOnlyList<double> decodeTimes = times;
        bool buildDecodedCache = false;
        if (decodedHit is null
            && cache is not null
            && decodeSuperset is not null
            && IsOrderedSubset(times, decodeSuperset)
            && cache.CanHoldDecoded(plan.BaseWidth, plan.BaseHeight, decodeSuperset.Count))
        {
            // Decode once at the densest cadence so later 5/10 fps and speed changes reuse it.
            decodeTimes = decodeSuperset;
            buildDecodedCache = true;
        }

        IReadOnlyDictionary<Guid, BitmapSource> layerBitmaps =
            FrameEditLayerRenderer.Decode(plan.Request.FrameEditLayers);
        int threads = options.ResolveEncoderThreads();
        using var run = new PipelineRun(plan, layerBitmaps, threads, cancellationToken);
        var encodedForCache = cache is not null ? new byte[total][] : null;
        var decodedForCache = buildDecodedCache ? new GifSourceFrame[decodeTimes.Count] : null;
        MediaFoundationVideoFrameReader? reader = null;
        string frameSource = "cache-decoded";
        int decodedFrames = 0;
        try
        {
            if (decodedHit is null)
            {
                reader = OpenReader(plan, options, log);
                frameSource = reader.IsHardwareAccelerated ? "hardware" : "software";
            }

            int maxInFlight = threads + 2;
            int drained = 0;
            int written = 0;
            int nextOutput = 0;
            for (int slot = 0; slot < decodeTimes.Count; slot++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (slot - drained >= maxInFlight)
                {
                    Drain(run.WaitFor(drained));
                }

                double sourceTime = decodeTimes[slot];
                GifSourceFrame frame;
                if (decodedHit is not null)
                {
                    frame = decodedHit[slot];
                }
                else
                {
                    DecodedVideoFrame decoded;
                    try
                    {
                        decoded = reader!.ReadFrameAt(sourceTime, cancellationToken);
                    }
                    catch (Exception ex) when (reader!.IsHardwareAccelerated
                                               && ex is InvalidOperationException
                                                   or System.IO.InvalidDataException
                                                   or System.Runtime.InteropServices.COMException)
                    {
                        // Device loss or a driver-specific surface problem after a successful
                        // hardware open; the exporter retries the whole file in software.
                        throw new GifHardwareDecodeException(ex);
                    }

                    frame = new GifSourceFrame(
                        decoded.Pixels,
                        decoded.Width,
                        decoded.Height,
                        decoded.Stride,
                        decoded.Format,
                        reader.YuvEncoding);
                    decodedFrames++;
                }

                int output = -1;
                if (nextOutput < total && times[nextOutput] == sourceTime)
                {
                    output = nextOutput++;
                }

                run.Submit(new FrameJob(slot, output, sourceTime, frame, buildDecodedCache));
                while (drained <= slot && run.TryTake(drained, out FrameResult? ready))
                {
                    Drain(ready!);
                }
            }

            while (drained < decodeTimes.Count)
            {
                Drain(run.WaitFor(drained));
            }

            if (written != total)
            {
                throw new InvalidOperationException("The GIF schedule was not fully rendered.");
            }

            void Drain(FrameResult result)
            {
                if (result.Encoded is { } encoded)
                {
                    writer.AddEncodedFrame(encoded, plan.FrameDelaysCentiseconds[result.Output]);
                    if (encodedForCache is not null)
                    {
                        encodedForCache[result.Output] = encoded;
                    }

                    written++;
                    progress?.Report(new VideoFrameRenderProgress(written, total, result.SourceTimeMs));
                }

                if (decodedForCache is not null && result.Base is { } baseFrame)
                {
                    decodedForCache[result.Slot] = baseFrame;
                }

                drained++;
            }
        }
        finally
        {
            reader?.Dispose();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (decodedForCache is not null)
        {
            cache!.StoreDecoded(plan.BaseWidth, plan.BaseHeight, decodeTimes, decodedForCache);
        }

        if (encodedForCache is not null)
        {
            cache!.StoreEncoded(plan.Request.Width, plan.Request.Height, times, encodedForCache);
        }

        return Finish(frameSource, decodedFrames, threads);

        GifRenderStatistics Finish(string source, int decoded, int encoderThreads)
        {
            var statistics = new GifRenderStatistics(
                source,
                total,
                decoded,
                encoderThreads,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            log.LogInformation(
                "GIF rendered {Frames} frame(s) at {Width}x{Height} from {Source}: {Decoded} decoded, {Threads} encoder thread(s), {Elapsed:0} ms",
                statistics.Frames,
                plan.Request.Width,
                plan.Request.Height,
                statistics.FrameSource,
                statistics.DecodedFrames,
                statistics.EncoderThreads,
                statistics.ElapsedMs);
            return statistics;
        }
    }

    /// <summary>
    /// Opens the source decoder: native-size NV12 by default (GPU or CPU decode, bit-identical),
    /// or RGB from the reader's converters for the comparison options. Workers normalise both.
    /// </summary>
    private static MediaFoundationVideoFrameReader OpenReader(GifRenderPlan plan, GifEngineOptions options, ILogger log)
    {
        var readerOptions = new MediaFoundationVideoFrameReaderOptions
        {
            PreferHardwareDecode = options.PreferHardwareDecode,
            OutputWidth = options.GpuResize ? plan.BaseWidth : 0,
            OutputHeight = options.GpuResize ? plan.BaseHeight : 0,
            OutputFormat = options.DecodeNv12 && !options.GpuResize
                ? DecodedVideoPixelFormat.Nv12
                : DecodedVideoPixelFormat.Bgr32,
            Log = log,
        };
        var reader = new MediaFoundationVideoFrameReader(plan.Request.SourcePath, readerOptions);
        if (reader.Width <= 0 || reader.Height <= 0)
        {
            reader.Dispose();
            throw new System.IO.InvalidDataException("The source video reported invalid dimensions.");
        }

        return reader;
    }

    /// <summary>True when every value of <paramref name="subset"/> occurs, in order, in <paramref name="superset"/>.</summary>
    internal static bool IsOrderedSubset(IReadOnlyList<double> subset, IReadOnlyList<double> superset)
    {
        int cursor = 0;
        foreach (double value in subset)
        {
            while (cursor < superset.Count && superset[cursor] < value)
            {
                cursor++;
            }

            if (cursor == superset.Count || superset[cursor] != value)
            {
                return false;
            }

            cursor++;
        }

        return true;
    }

    /// <summary>
    /// Returns <paramref name="frame"/> at exactly <paramref name="width"/> x
    /// <paramref name="height"/> by area averaging, or null when that would need enlargement
    /// (only possible when container metadata disagrees with the decoded size).
    /// </summary>
    internal static GifSourceFrame? FitFrame(GifSourceFrame frame, int width, int height)
    {
        if (frame.Format == DecodedVideoPixelFormat.Nv12)
        {
            throw new ArgumentException("Convert NV12 frames with NormalizeFrame first.", nameof(frame));
        }

        if (frame.Width == width && frame.Height == height)
        {
            return frame;
        }

        if (frame.Width < width || frame.Height < height)
        {
            return null;
        }

        byte[] scaled = BgraFrameScaler.Downscale(frame.Pixels, frame.Width, frame.Height, frame.Stride, width, height);
        return new GifSourceFrame(scaled, width, height, width * 4, Opaque: true);
    }

    /// <summary>
    /// Brings a decoded frame to the shared base size as BGRA: NV12 is converted (and reduced
    /// in the same pass), BGR32 is area-averaged. A frame smaller than the base size (only when
    /// container metadata disagrees with the stream) is returned at its own size for the
    /// compositor to stretch, as the original exporter did.
    /// </summary>
    internal static GifSourceFrame NormalizeFrame(GifSourceFrame frame, int width, int height)
    {
        if (frame.Format != DecodedVideoPixelFormat.Nv12)
        {
            return FitFrame(frame, width, height) ?? frame;
        }

        bool fits = width <= frame.Width && height <= frame.Height;
        int targetWidth = fits ? width : frame.Width;
        int targetHeight = fits ? height : frame.Height;
        byte[] converted = Nv12FrameConverter.ToBgra(
            frame.Pixels, frame.Width, frame.Height, frame.Yuv, targetWidth, targetHeight);
        return new GifSourceFrame(converted, targetWidth, targetHeight, targetWidth * 4, Opaque: true);
    }

    /// <summary>
    /// Scales, composes when needed, and encodes one output frame on a worker thread. A frame
    /// with nothing drawn over it skips <see cref="RenderTargetBitmap"/> and goes straight to the
    /// encoder as an opaque BGR32 bitmap.
    /// </summary>
    internal static byte[] EncodeOutputFrame(
        GifSourceFrame frame,
        VideoFrameRenderRequest request,
        IReadOnlyDictionary<Guid, BitmapSource> layerBitmaps,
        double sourceTimeMs,
        ref VideoFrameCompositor? compositor)
    {
        BitmapSource bitmap;
        GifSourceFrame? fitted = FitFrame(frame, request.Width, request.Height);
        if (fitted is { } target
            && VideoFrameRenderPipeline.CanBypassComposition(request, layerBitmaps, target.Width, target.Height, sourceTimeMs))
        {
            // The WIC GIF encoder quantises BGR32 and BGRA32 input differently even for equal
            // colours. Composition always produced opaque BGRA32, so a bypassed frame must too
            // for the encoded bytes to stay identical. Decoder buffers carry X=0 and may be
            // shared, so they are copied; BitmapSource.Create copies the opaque ones itself.
            byte[] opaque = target.Opaque ? target.Pixels : OpaqueCopy(target);
            bitmap = BitmapSource.Create(
                target.Width,
                target.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                opaque,
                target.Opaque ? target.Stride : target.Width * 4);
        }
        else
        {
            // Scale with the area averager first when possible so composed and bypassed frames
            // share one resampling filter; the compositor then draws 1:1.
            GifSourceFrame source = fitted ?? frame;
            compositor ??= new VideoFrameCompositor(request.Width, request.Height);
            bitmap = compositor.Compose(
                request,
                layerBitmaps,
                source.Pixels,
                source.Width,
                source.Height,
                source.Stride,
                sourceTimeMs);
        }

        return AnimatedGifWriter.EncodeFrame(bitmap).ToArray();
    }

    /// <summary>Tightly packed copy of a BGRX frame with every fourth byte set to opaque 255.</summary>
    internal static byte[] OpaqueCopy(GifSourceFrame frame)
    {
        int rowBytes = checked(frame.Width * 4);
        byte[] copy = GC.AllocateUninitializedArray<byte>(checked(rowBytes * frame.Height));
        for (int row = 0; row < frame.Height; row++)
        {
            Buffer.BlockCopy(frame.Pixels, row * frame.Stride, copy, row * rowBytes, rowBytes);
        }

        for (int offset = 3; offset < copy.Length; offset += 4)
        {
            copy[offset] = 255;
        }

        return copy;
    }

    private sealed record FrameJob(int Slot, int Output, double SourceTimeMs, GifSourceFrame Frame, bool KeepBase);

    private sealed record FrameResult(int Slot, int Output, double SourceTimeMs, byte[]? Encoded, GifSourceFrame? Base);

    /// <summary>Worker threads, the job queue and the slot-ordered result window of one export.</summary>
    private sealed class PipelineRun : IDisposable
    {
        private readonly GifRenderPlan _plan;
        private readonly IReadOnlyDictionary<Guid, BitmapSource> _layerBitmaps;
        private readonly BlockingCollection<FrameJob> _jobs = new(new ConcurrentQueue<FrameJob>());
        private readonly Dictionary<int, FrameResult> _results = [];
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop;
        private readonly CancellationTokenRegistration _wakeOnStop;
        private readonly List<Thread> _threads = [];
        private ExceptionDispatchInfo? _failure;

        internal PipelineRun(
            GifRenderPlan plan,
            IReadOnlyDictionary<Guid, BitmapSource> layerBitmaps,
            int threadCount,
            CancellationToken cancellationToken)
        {
            _plan = plan;
            _layerBitmaps = layerBitmaps;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _wakeOnStop = _stop.Token.Register(() =>
            {
                lock (_gate)
                {
                    Monitor.PulseAll(_gate);
                }
            });
            for (int index = 0; index < threadCount; index++)
            {
                var thread = new Thread(WorkerMain)
                {
                    IsBackground = true,
                    Name = $"MyCapture GIF encoder {index + 1}",
                };
                thread.SetApartmentState(ApartmentState.STA);
                _threads.Add(thread);
                thread.Start();
            }
        }

        internal void Submit(FrameJob job)
        {
            ThrowIfFailed();
            try
            {
                _jobs.Add(job, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                // A worker failure also stops the run; report that failure, not a cancellation.
                lock (_gate)
                {
                    ThrowIfFailed();
                }

                throw;
            }
        }

        internal bool TryTake(int slot, out FrameResult? result)
        {
            lock (_gate)
            {
                ThrowIfFailed();
                return _results.Remove(slot, out result);
            }
        }

        internal FrameResult WaitFor(int slot)
        {
            lock (_gate)
            {
                while (true)
                {
                    ThrowIfFailed();
                    if (_results.Remove(slot, out FrameResult? result))
                    {
                        return result;
                    }

                    _stop.Token.ThrowIfCancellationRequested();
                    _ = Monitor.Wait(_gate);
                }
            }
        }

        public void Dispose()
        {
            _jobs.CompleteAdding();
            _stop.Cancel();
            foreach (Thread thread in _threads)
            {
                thread.Join();
            }

            _wakeOnStop.Dispose();
            _stop.Dispose();
            _jobs.Dispose();
            lock (_gate)
            {
                _results.Clear();
            }
        }

        private void ThrowIfFailed() => _failure?.Throw();

        private void WorkerMain()
        {
            _ = Interlocked.Increment(ref s_liveWorkerThreads);
            VideoFrameCompositor? compositor = null;
            try
            {
                foreach (FrameJob job in _jobs.GetConsumingEnumerable(_stop.Token))
                {
                    // Every preset scales from the same base frame, so cached and freshly
                    // decoded exports of one preset are byte-identical.
                    GifSourceFrame baseFrame = NormalizeFrame(job.Frame, _plan.BaseWidth, _plan.BaseHeight);
                    byte[]? encoded = job.Output >= 0
                        ? EncodeOutputFrame(baseFrame, _plan.Request, _layerBitmaps, job.SourceTimeMs, ref compositor)
                        : null;
                    var result = new FrameResult(
                        job.Slot,
                        job.Output,
                        job.SourceTimeMs,
                        encoded,
                        job.KeepBase ? baseFrame : null);
                    lock (_gate)
                    {
                        _results[job.Slot] = result;
                        Monitor.PulseAll(_gate);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _failure ??= ExceptionDispatchInfo.Capture(ex);
                    Monitor.PulseAll(_gate);
                }

                _stop.Cancel();
            }
            finally
            {
                // Imaging lazily creates a Dispatcher on this thread; thread exit alone does not
                // run its shutdown callbacks (see StaThreadTask).
                Dispatcher? dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
                if (dispatcher is { HasShutdownFinished: false })
                {
                    dispatcher.InvokeShutdown();
                }

                _ = Interlocked.Decrement(ref s_liveWorkerThreads);
            }
        }
    }
}
