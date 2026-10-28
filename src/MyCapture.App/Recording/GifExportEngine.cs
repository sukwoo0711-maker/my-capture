using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    /// Read the decoder's own NV12 output and convert it on the pixel workers with
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

    /// <summary>
    /// Pixel worker threads for colour conversion, scaling and candidate encoding; zero picks
    /// half the logical processors, from 1 to 4. They never touch WPF.
    /// </summary>
    internal int WorkerThreads { get; init; }

    /// <summary>
    /// How consecutive frames become GIF images. <see cref="GifFrameDifferencing.ErrorBounded"/>
    /// (default) merges identical frames and repaints only what the full-frame error bound
    /// needs; the other modes are kept for size and quality comparisons.
    /// </summary>
    internal GifFrameDifferencing Differencing { get; init; } = GifFrameDifferencing.ErrorBounded;

    /// <summary>
    /// Which caller of the platform GIF codec encodes images. <see cref="GifImageEncoder.Wic"/>
    /// (default) and <see cref="GifImageEncoder.Wpf"/> write identical bytes; WPF encodes every
    /// image on the calling thread and is kept for comparison.
    /// </summary>
    internal GifImageEncoder Encoder { get; init; } = GifImageEncoder.Wic;

    /// <summary>
    /// Encode (and read) the whole-canvas image of every frame with nothing drawn over it on the
    /// pixel workers (default), so the calling thread only differences and appends. When false,
    /// or with <see cref="GifImageEncoder.Wpf"/>, every image is encoded on the calling thread.
    /// The output is the same.
    /// </summary>
    internal bool EncodeOnWorkers { get; init; } = true;

    internal int ResolveWorkerThreads() =>
        WorkerThreads > 0
            ? Math.Min(WorkerThreads, 16)
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
/// <param name="Images">GIF images written; fewer than <paramref name="Frames"/> when identical frames merged.</param>
/// <param name="PreEncodedImages">Images whose encoding the pixel workers supplied.</param>
/// <param name="WorkerEncodes">Encodes the pixel workers ran; more than <paramref name="PreEncodedImages"/> only when the writer merged or cropped a frame the workers could not foresee.</param>
internal sealed record GifRenderStatistics(
    string FrameSource,
    int Frames,
    int DecodedFrames,
    int WorkerThreads,
    double ElapsedMs,
    int Images,
    int PreEncodedImages = 0,
    int WorkerEncodes = 0);

/// <summary>
/// Streams a GIF frame schedule as decode → normalise → scale → compose → difference → encode → append.
/// </summary>
/// <remarks>
/// <para>
/// Three kinds of thread share the work. One decode thread runs the Media Foundation reader
/// (or reads cached frames) in schedule order. A few pixel workers do the CPU-heavy, WPF-free
/// middle on plain managed arrays: NV12 conversion to the base size, area-average scaling to the
/// preset, the opaque copy the encoder needs and, for every frame with nothing drawn over it,
/// the whole-canvas GIF encode and its LZW reading (<see cref="AnimatedGifWriter.EncodeCandidate"/>,
/// which calls WIC directly). The calling STA thread does every WPF operation — composition
/// only for frames with an active text or frame layer — encodes those composed frames itself,
/// and hands frames to <see cref="AnimatedGifWriter"/> strictly in output order, exactly as the
/// original single-threaded exporter did, so the writer's frame differencing (which depends on
/// the previous frame) sees the same sequence and writes the same bytes on every path.
/// </para>
/// <para>
/// WPF imaging stays on one thread on purpose. Running composition and GifBitmapEncoder on
/// several STA threads at once corrupted the native heap (0xC0000374) or changed a frame's GIF
/// palette in roughly one export in 600 under load, with software or hardware decode and with or
/// without frame layers; the test assembly disables parallel tests for the same reason. The
/// workers' encodes do not use WPF: <see cref="WicGifFrameEncoder"/> makes the WIC calls WPF
/// makes, through a free-threaded factory, with every per-image object owned by one call. Its
/// bytes equal WPF's, and 128,000 concurrent encodes on eight MTA threads beside STA WPF imaging
/// matched the sequential WPF bytes with no failure (WicGifFrameEncoderTests,
/// MYCAPTURE_GIF_STRESS=1). A frame equal to the previous output frame is not encoded at all,
/// since the writer merges it.
/// </para>
/// <para>
/// Memory is bounded: at most <c>workers + 2</c> frames are in flight between decode and append.
/// All presets decode to the same base size (the recording fitted within 960 px) and scale down
/// from it with <see cref="BgraFrameScaler"/>, so a one-shot export and a cached session export of
/// the same preset produce byte-identical files.
/// </para>
/// </remarks>
internal static class GifExportEngine
{
    private static int s_liveWorkerThreads;

    /// <summary>Decode and pixel threads currently running in this process; zero between exports.</summary>
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
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("GIF composition and encoding require an STA thread.");
        }

        VideoFrameRenderPipeline.ValidateSchedule(plan.Request, plan.SourceTimesMs);
        if (plan.FrameDelaysCentiseconds.Count != plan.SourceTimesMs.Count)
        {
            throw new ArgumentException("Every GIF frame needs exactly one delay.", nameof(plan));
        }

        long started = Stopwatch.GetTimestamp();
        IReadOnlyList<double> times = plan.SourceTimesMs;
        int total = times.Count;

        // Fastest reuse: this exact frame sequence is already encoded (speed-only change or a
        // return to an earlier preset). Only the container and delays are written.
        if (cache?.TryGetEncoded(plan.Request.Width, plan.Request.Height, times) is { } encodedHit)
        {
            for (int index = 0; index < total; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.AddEncodedFrame(encodedHit[index], plan.FrameDelaysCentiseconds[index]);
                progress?.Report(new VideoFrameRenderProgress(index + 1, total, times[index]));
            }

            return Finish("cache-encoded", 0, 0, 0);
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
        bool[]? encodeOnWorker = null;
        if (options.EncodeOnWorkers && options.Encoder == GifImageEncoder.Wic && writer.AcceptsEncodedCandidates)
        {
            // Frames with nothing drawn over them reach the writer as the workers' own buffers
            // (ComposeOutputFrame bypasses WPF), so their whole-canvas encode can run there.
            encodeOnWorker = new bool[total];
            for (int index = 0; index < total; index++)
            {
                encodeOnWorker[index] = VideoFrameRenderPipeline.CanBypassComposition(
                    plan.Request, layerBitmaps, plan.Request.Width, plan.Request.Height, times[index]);
            }
        }

        int threads = options.ResolveWorkerThreads();
        // Enforce the encoded budget during collection, not only in StoreEncoded after
        // every payload has already been retained. High-fps/long exports otherwise grow
        // without bound despite the final cache being limited to 64 MiB.
        long encodedCacheBytes = (long)total * 64;
        var encodedForCache = cache?.CanHoldEncoded(encodedCacheBytes) == true ? new GifFrameUpdate[total] : null;
        var decodedForCache = buildDecodedCache ? new GifSourceFrame[decodeTimes.Count] : null;
        VideoFrameCompositor? compositor = null;
        int written = 0;
        string frameSource;
        int decodedFrames;
        int workerEncodes;
        using (var run = new PixelPipeline(
                   plan,
                   decodeTimes,
                   decodedHit,
                   buildDecodedCache,
                   options,
                   log,
                   threads,
                   new CandidatePolicy(encodeOnWorker, writer.ReadsCandidates, writer.MergesIdenticalFrames),
                   cancellationToken))
        {
            for (int slot = 0; slot < decodeTimes.Count; slot++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PixelResult result = run.Take(slot);
                if (result.Output >= 0)
                {
                    byte[] pixels = ComposeOutputFrame(
                        result.Target,
                        plan.Request,
                        layerBitmaps,
                        result.SourceTimeMs,
                        ref compositor);
                    GifFrameUpdate update = writer.AddFrame(
                        pixels,
                        plan.Request.Width * 4,
                        plan.FrameDelaysCentiseconds[result.Output],
                        result.Candidate);
                    if (encodedForCache is not null)
                    {
                        encodedCacheBytes += update.Payload.Length;
                        if (cache!.CanHoldEncoded(encodedCacheBytes))
                            encodedForCache[result.Output] = update;
                        else
                            encodedForCache = null; // Release collected payloads; keep streaming.
                    }

                    written++;
                    progress?.Report(new VideoFrameRenderProgress(written, total, result.SourceTimeMs));
                }

                if (decodedForCache is not null && result.Base is { } baseFrame)
                {
                    decodedForCache[slot] = baseFrame;
                }
            }

            run.Complete();
            frameSource = run.FrameSource;
            decodedFrames = run.DecodedFrames;
            workerEncodes = run.WorkerEncodes;
        }

        if (written != total)
        {
            throw new InvalidOperationException("The GIF schedule was not fully rendered.");
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

        return Finish(frameSource, decodedFrames, threads, workerEncodes);

        GifRenderStatistics Finish(string source, int decoded, int workerThreads, int encodedOnWorkers)
        {
            var statistics = new GifRenderStatistics(
                source,
                total,
                decoded,
                workerThreads,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                writer.ImageCount,
                writer.PreEncodedImages,
                encodedOnWorkers);
            log.LogInformation(
                "GIF rendered {Frames} frame(s) as {Images} image(s) at {Width}x{Height} from {Source}: {Decoded} decoded, {Threads} pixel thread(s), {PreEncoded} encoded on them, {Elapsed:0} ms",
                statistics.Frames,
                statistics.Images,
                plan.Request.Width,
                plan.Request.Height,
                statistics.FrameSource,
                statistics.DecodedFrames,
                statistics.WorkerThreads,
                statistics.PreEncodedImages,
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
    /// The WPF-free part of one output frame, run on a pixel worker: the preset-size frame as
    /// opaque BGRA when it can be scaled down, otherwise the base frame for the compositor to
    /// stretch.
    /// </summary>
    internal static GifSourceFrame PrepareOutputFrame(GifSourceFrame baseFrame, int width, int height)
    {
        if (FitFrame(baseFrame, width, height) is not { } target)
        {
            return baseFrame;
        }

        return target.Opaque
            ? target
            : new GifSourceFrame(OpaqueCopy(target), target.Width, target.Height, target.Width * 4, Opaque: true);
    }

    /// <summary>
    /// Returns one output frame as tightly packed opaque BGRA, composing it on the calling STA
    /// thread when something is drawn over it. A frame with nothing drawn over it skips
    /// <see cref="RenderTargetBitmap"/> and WPF entirely: composition would only copy it, and
    /// the composed result is the same opaque pixels.
    /// </summary>
    internal static byte[] ComposeOutputFrame(
        GifSourceFrame frame,
        VideoFrameRenderRequest request,
        IReadOnlyDictionary<Guid, BitmapSource> layerBitmaps,
        double sourceTimeMs,
        ref VideoFrameCompositor? compositor)
    {
        GifSourceFrame? fitted = FitFrame(frame, request.Width, request.Height);
        if (fitted is { } target
            && VideoFrameRenderPipeline.CanBypassComposition(request, layerBitmaps, target.Width, target.Height, sourceTimeMs))
        {
            // Decoder buffers carry X=0 and may be shared, so they are copied with opaque
            // alpha; scaled and converted frames already are opaque and tightly packed.
            return target.Opaque && target.Stride == target.Width * 4 ? target.Pixels : OpaqueCopy(target);
        }

        // Scale with the area averager first when possible so composed and bypassed frames
        // share one resampling filter; the compositor then draws 1:1.
        GifSourceFrame source = fitted ?? frame;
        compositor ??= new VideoFrameCompositor(request.Width, request.Height);
        BitmapSource bitmap = compositor.Compose(
            request,
            layerBitmaps,
            source.Pixels,
            source.Width,
            source.Height,
            source.Stride,
            sourceTimeMs);
        int stride = request.Width * 4;
        byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(stride * request.Height));
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
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

    /// <summary>
    /// True when two decoded frames carry the same pixels and layout, so that normalising and
    /// scaling them gives the same output frame.
    /// </summary>
    internal static bool SameSourceFrame(GifSourceFrame first, GifSourceFrame second) =>
        first.Width == second.Width
        && first.Height == second.Height
        && first.Stride == second.Stride
        && first.Format == second.Format
        && first.Yuv == second.Yuv
        && first.Opaque == second.Opaque
        && (ReferenceEquals(first.Pixels, second.Pixels) || first.Pixels.AsSpan().SequenceEqual(second.Pixels));

    /// <param name="PreviousOutput">The decoded frame of the previous output frame, if any.</param>
    private sealed record PixelJob(int Slot, int Output, double SourceTimeMs, GifSourceFrame Frame, GifSourceFrame? PreviousOutput);

    private sealed record PixelResult(
        int Slot,
        int Output,
        double SourceTimeMs,
        GifSourceFrame Target,
        GifSourceFrame? Base,
        GifEncodedCandidate? Candidate);

    /// <summary>
    /// Which output frames the workers encode for the writer. <paramref name="EncodeOutput"/> is
    /// null when they encode none; <paramref name="Read"/> adds the error-bounded reading;
    /// <paramref name="SkipRepeats"/> skips a frame that will merge with an equal predecessor.
    /// </summary>
    private sealed record CandidatePolicy(bool[]? EncodeOutput, bool Read, bool SkipRepeats);

    /// <summary>
    /// The decode thread, the pixel workers and the slot-ordered result window of one export.
    /// None of these threads touches WPF.
    /// </summary>
    private sealed class PixelPipeline : IDisposable
    {
        private readonly GifRenderPlan _plan;
        private readonly IReadOnlyList<double> _decodeTimes;
        private readonly GifSourceFrame[]? _cachedFrames;
        private readonly bool _keepBase;
        private readonly GifEngineOptions _options;
        private readonly ILogger _log;
        private readonly CandidatePolicy _candidates;
        private readonly int _maxInFlight;
        private readonly BlockingCollection<PixelJob> _jobs = new(new ConcurrentQueue<PixelJob>());
        private readonly Dictionary<int, PixelResult> _results = [];
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop;
        private readonly CancellationTokenRegistration _wakeOnStop;
        private readonly List<Thread> _threads = [];
        private ExceptionDispatchInfo? _failure;
        private int _taken;
        private int _workerEncodes;

        internal PixelPipeline(
            GifRenderPlan plan,
            IReadOnlyList<double> decodeTimes,
            GifSourceFrame[]? cachedFrames,
            bool keepBase,
            GifEngineOptions options,
            ILogger log,
            int workerCount,
            CandidatePolicy candidates,
            CancellationToken cancellationToken)
        {
            _plan = plan;
            _decodeTimes = decodeTimes;
            _cachedFrames = cachedFrames;
            _keepBase = keepBase;
            _options = options;
            _log = log;
            _candidates = candidates;
            _maxInFlight = workerCount + 2;
            FrameSource = cachedFrames is null ? "software" : "cache-decoded";
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _wakeOnStop = _stop.Token.Register(() =>
            {
                lock (_gate)
                {
                    Monitor.PulseAll(_gate);
                }
            });
            Start(DecodeMain, "MyCapture GIF decode");
            for (int index = 0; index < workerCount; index++)
            {
                Start(WorkerMain, $"MyCapture GIF pixels {index + 1}");
            }
        }

        /// <summary>Set by the decode thread; read after <see cref="Complete"/>.</summary>
        internal string FrameSource { get; private set; }

        internal int DecodedFrames { get; private set; }

        /// <summary>Candidates the workers encoded; final once every slot has been taken.</summary>
        internal int WorkerEncodes => Volatile.Read(ref _workerEncodes);

        /// <summary>Waits for the result of <paramref name="slot"/> (taken strictly in order).</summary>
        internal PixelResult Take(int slot)
        {
            lock (_gate)
            {
                while (true)
                {
                    _failure?.Throw();
                    if (_results.Remove(slot, out PixelResult? result))
                    {
                        // Frees one window slot for the decode thread.
                        _taken = slot + 1;
                        Monitor.PulseAll(_gate);
                        return result;
                    }

                    _stop.Token.ThrowIfCancellationRequested();
                    _ = Monitor.Wait(_gate);
                }
            }
        }

        /// <summary>Waits for the decode thread so its statistics are final.</summary>
        internal void Complete()
        {
            _threads[0].Join();
            lock (_gate)
            {
                _failure?.Throw();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _jobs.CompleteAdding();
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

        private void Start(ThreadStart body, string name)
        {
            var thread = new Thread(() =>
            {
                _ = Interlocked.Increment(ref s_liveWorkerThreads);
                try
                {
                    body();
                }
                finally
                {
                    _ = Interlocked.Decrement(ref s_liveWorkerThreads);
                }
            })
            {
                IsBackground = true,
                Name = name,
            };
            _threads.Add(thread);
            thread.Start();
        }

        private void DecodeMain()
        {
            MediaFoundationVideoFrameReader? reader = null;
            try
            {
                if (_cachedFrames is null)
                {
                    reader = OpenReader(_plan, _options, _log);
                    FrameSource = reader.IsHardwareAccelerated ? "hardware" : "software";
                }

                IReadOnlyList<double> outputTimes = _plan.SourceTimesMs;
                int nextOutput = 0;
                GifSourceFrame? previousOutput = null;
                for (int slot = 0; slot < _decodeTimes.Count; slot++)
                {
                    lock (_gate)
                    {
                        while (slot - _taken >= _maxInFlight)
                        {
                            _stop.Token.ThrowIfCancellationRequested();
                            _ = Monitor.Wait(_gate);
                        }
                    }

                    _stop.Token.ThrowIfCancellationRequested();
                    double sourceTime = _decodeTimes[slot];
                    GifSourceFrame frame = _cachedFrames is not null ? _cachedFrames[slot] : Decode(reader!, sourceTime);
                    int output = -1;
                    GifSourceFrame? previous = null;
                    if (nextOutput < outputTimes.Count && outputTimes[nextOutput] == sourceTime)
                    {
                        output = nextOutput++;
                        previous = previousOutput;
                        previousOutput = frame;
                    }

                    _jobs.Add(new PixelJob(slot, output, sourceTime, frame, previous), _stop.Token);
                }

                _jobs.CompleteAdding();
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
            finally
            {
                reader?.Dispose();
            }
        }

        private GifSourceFrame Decode(MediaFoundationVideoFrameReader reader, double sourceTime)
        {
            DecodedVideoFrame decoded;
            try
            {
                decoded = reader.ReadFrameAt(sourceTime, _stop.Token);
            }
            catch (Exception ex) when (reader.IsHardwareAccelerated
                                       && ex is InvalidOperationException
                                           or System.IO.InvalidDataException
                                           or System.Runtime.InteropServices.COMException)
            {
                // Device loss or a driver-specific surface problem after a successful
                // hardware open; the exporter retries the whole file in software.
                throw new GifHardwareDecodeException(ex);
            }

            DecodedFrames++;
            return new GifSourceFrame(
                decoded.Pixels,
                decoded.Width,
                decoded.Height,
                decoded.Stride,
                decoded.Format,
                reader.YuvEncoding);
        }

        private void WorkerMain()
        {
            try
            {
                foreach (PixelJob job in _jobs.GetConsumingEnumerable(_stop.Token))
                {
                    // Every preset scales from the same base frame, so cached and freshly
                    // decoded exports of one preset are byte-identical.
                    GifSourceFrame baseFrame = NormalizeFrame(job.Frame, _plan.BaseWidth, _plan.BaseHeight);
                    GifSourceFrame target = job.Output >= 0
                        ? PrepareOutputFrame(baseFrame, _plan.Request.Width, _plan.Request.Height)
                        : baseFrame;
                    var result = new PixelResult(
                        job.Slot,
                        job.Output,
                        job.SourceTimeMs,
                        target,
                        _keepBase ? baseFrame : null,
                        EncodeCandidate(job, target));
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
                Fail(ex);
            }
        }

        /// <summary>
        /// The writer's whole-canvas encode of an output frame that will reach it unchanged (no
        /// composition, already the output size, opaque and tightly packed, so
        /// <see cref="ComposeOutputFrame"/> passes this very buffer on), or null. A frame whose
        /// source equals the previous output frame's is skipped when the writer will merge it.
        /// </summary>
        private GifEncodedCandidate? EncodeCandidate(PixelJob job, GifSourceFrame target)
        {
            int width = _plan.Request.Width;
            int height = _plan.Request.Height;
            if (job.Output < 0
                || _candidates.EncodeOutput is not { } encode
                || !encode[job.Output]
                || target.Width != width
                || target.Height != height
                || !target.Opaque
                || target.Stride != width * 4)
            {
                return null;
            }

            if (_candidates.SkipRepeats
                && job.Output > 0
                && encode[job.Output - 1]
                && job.PreviousOutput is { } previous
                && SameSourceFrame(previous, job.Frame))
            {
                return null;
            }

            GifEncodedCandidate? candidate = AnimatedGifWriter.EncodeCandidate(
                target.Pixels, target.Stride, width, height, _candidates.Read);
            if (candidate is not null)
            {
                _ = Interlocked.Increment(ref _workerEncodes);
            }

            return candidate;
        }

        private void Fail(Exception ex)
        {
            lock (_gate)
            {
                _failure ??= ExceptionDispatchInfo.Capture(ex);
                Monitor.PulseAll(_gate);
            }

            _stop.Cancel();
        }
    }
}
