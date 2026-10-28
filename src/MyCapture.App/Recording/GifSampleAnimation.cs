using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MyCapture.App.Recording;

/// <summary>
/// Display timing of an animated GIF as browsers show it, with loop lookup.
/// </summary>
internal sealed class GifPlaybackTimeline
{
    /// <summary>
    /// Chromium, Firefox and WebKit show a frame that requests 0 or 1 centisecond (10 ms or
    /// less) for 100 ms instead; WebKit/Blink BitmapImage documents "We follow Firefox's
    /// behavior". The preview uses the same rule so it plays at the speed viewers will see.
    /// </summary>
    internal const double ClampedFrameMs = 100;

    private readonly double[] _ends;

    internal GifPlaybackTimeline(IReadOnlyList<int> delaysCentiseconds)
    {
        ArgumentNullException.ThrowIfNull(delaysCentiseconds);
        if (delaysCentiseconds.Count == 0)
        {
            throw new ArgumentException("At least one frame is required.", nameof(delaysCentiseconds));
        }

        _ends = new double[delaysCentiseconds.Count];
        double end = 0;
        for (int index = 0; index < _ends.Length; index++)
        {
            end += DisplayMs(delaysCentiseconds[index]);
            _ends[index] = end;
        }
    }

    internal int Count => _ends.Length;

    internal double TotalMs => _ends[^1];

    internal static double DisplayMs(int delayCentiseconds) =>
        delayCentiseconds <= 1 ? ClampedFrameMs : delayCentiseconds * 10.0;

    internal double StartOf(int index) => index <= 0 ? 0 : _ends[Math.Min(index, _ends.Length) - 1];

    internal double EndOf(int index) => _ends[Math.Clamp(index, 0, _ends.Length - 1)];

    /// <summary>Frame shown <paramref name="elapsedMs"/> after the first frame, looping forever.</summary>
    internal int IndexAt(double elapsedMs)
    {
        if (!double.IsFinite(elapsedMs) || elapsedMs <= 0)
        {
            return 0;
        }

        double position = elapsedMs % TotalMs;
        int found = Array.BinarySearch(_ends, position);
        // An exact boundary belongs to the next frame; otherwise BinarySearch returns the
        // complement of the first end that is greater than the position.
        int index = found >= 0 ? found + 1 : ~found;
        return index >= _ends.Length ? 0 : index;
    }
}

/// <summary>Detached frozen frames of a GIF, composited the way a browser displays them.</summary>
internal sealed class GifSampleFrames
{
    internal GifSampleFrames(IReadOnlyList<BitmapSource> frames, IReadOnlyList<int> delaysCentiseconds, int pixelWidth, int pixelHeight)
    {
        Frames = frames;
        DelaysCentiseconds = delaysCentiseconds;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Timeline = new GifPlaybackTimeline(delaysCentiseconds);
    }

    internal IReadOnlyList<BitmapSource> Frames { get; }

    internal IReadOnlyList<int> DelaysCentiseconds { get; }

    internal int PixelWidth { get; }

    internal int PixelHeight { get; }

    internal GifPlaybackTimeline Timeline { get; }
}

/// <summary>
/// Decodes an animated GIF into display frames. WPF's <see cref="GifBitmapDecoder"/> returns
/// each frame's own rectangle without applying offsets, transparency or disposal, so a GIF
/// written with partial frames would play wrongly if its frames were shown directly.
/// </summary>
internal static class GifSampleDecoder
{
    /// <summary>Bounds preview memory even if a caller passes an unexpectedly long GIF.</summary>
    internal const int MaximumFrames = 400;
    internal const long MaximumDecodedBytes = 128L * 1024 * 1024;

    private const int DisposeToBackground = 2;
    private const int DisposeToPrevious = 3;

    /// <summary>Decodes on the calling STA thread and returns frozen frames only.</summary>
    internal static GifSampleFrames Decode(string path, CancellationToken cancellationToken = default)
    {
        // The sample is a few megabytes. Reading it whole releases the owned staging file
        // before decoding, so cancellation or disposal never waits on an open handle.
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("The GIF has no frames.");
        }

        int width = ReadInt(decoder.Metadata as BitmapMetadata, "/logscrdesc/Width") ?? decoder.Frames[0].PixelWidth;
        int height = ReadInt(decoder.Metadata as BitmapMetadata, "/logscrdesc/Height") ?? decoder.Frames[0].PixelHeight;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("The GIF reports an empty canvas.");
        }

        // Worst-case composited BGRA snapshots plus working canvases. Keep the file for
        // export, but fail the preview clearly before allocating hundreds of full frames.
        if (decoder.Frames.Count > MaximumFrames
            || (long)width * height * 4 * (decoder.Frames.Count + 3L) > MaximumDecodedBytes)
        {
            throw new GifExportLimitException(UiText.Get("GifExport_PreviewMemoryLimit"));
        }

        int canvasStride = checked(width * 4);
        byte[] canvas = new byte[checked(canvasStride * height)];
        int count = Math.Min(decoder.Frames.Count, MaximumFrames);
        var frames = new List<BitmapSource>(count);
        var delays = new List<int>(count);
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BitmapFrame frame = decoder.Frames[index];
            var metadata = frame.Metadata as BitmapMetadata;
            int left = ReadInt(metadata, "/imgdesc/Left") ?? 0;
            int top = ReadInt(metadata, "/imgdesc/Top") ?? 0;
            int disposal = ReadInt(metadata, "/grctlext/Disposal") ?? 0;
            int frameWidth = frame.PixelWidth;
            int frameHeight = frame.PixelHeight;
            BitmapSource bgra = frame.Format == PixelFormats.Bgra32
                ? frame
                : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int frameStride = checked(frameWidth * 4);
            byte[] pixels = new byte[checked(frameStride * frameHeight)];
            bgra.CopyPixels(pixels, frameStride, 0);

            byte[]? restore = disposal == DisposeToPrevious ? (byte[])canvas.Clone() : null;
            bool replacesCanvas = left == 0 && top == 0 && frameWidth == width && frameHeight == height && IsOpaque(pixels);
            if (replacesCanvas)
            {
                Buffer.BlockCopy(pixels, 0, canvas, 0, canvas.Length);
                // A full opaque indexed frame keeps its palette form: a quarter of the memory.
                frames.Add(IsIndexed(frame.Format) ? DetachedCopy(frame) : Snapshot(canvas, width, height));
            }
            else
            {
                Composite(pixels, frameWidth, frameHeight, left, top, canvas, width, height);
                frames.Add(Snapshot(canvas, width, height));
            }

            delays.Add(ReadInt(metadata, "/grctlext/Delay") ?? 0);
            if (disposal == DisposeToBackground)
            {
                Clear(canvas, width, height, left, top, frameWidth, frameHeight);
            }
            else if (restore is not null)
            {
                canvas = restore;
            }
        }

        return new GifSampleFrames(frames, delays, width, height);
    }

    private static int? ReadInt(BitmapMetadata? metadata, string query)
    {
        try
        {
            return metadata?.GetQuery(query) switch
            {
                ushort value => value,
                byte value => value,
                short value => value,
                int value => value,
                _ => null,
            };
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static bool IsOpaque(byte[] bgra)
    {
        for (int alpha = 3; alpha < bgra.Length; alpha += 4)
        {
            if (bgra[alpha] != 255)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIndexed(PixelFormat format) =>
        format == PixelFormats.Indexed8 || format == PixelFormats.Indexed4
        || format == PixelFormats.Indexed2 || format == PixelFormats.Indexed1;

    private static BitmapSource DetachedCopy(BitmapSource frame)
    {
        int stride = checked(((frame.PixelWidth * frame.Format.BitsPerPixel) + 7) / 8);
        byte[] pixels = new byte[checked(stride * frame.PixelHeight)];
        frame.CopyPixels(pixels, stride, 0);
        BitmapSource copy = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96, frame.Format, frame.Palette, pixels, stride);
        copy.Freeze();
        return copy;
    }

    private static BitmapSource Snapshot(byte[] canvas, int width, int height)
    {
        BitmapSource copy = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, canvas, width * 4);
        copy.Freeze();
        return copy;
    }

    /// <summary>GIF transparency is binary: opaque frame pixels replace the canvas.</summary>
    private static void Composite(byte[] frame, int frameWidth, int frameHeight, int left, int top, byte[] canvas, int width, int height)
    {
        for (int y = 0; y < frameHeight; y++)
        {
            int canvasY = top + y;
            if (canvasY < 0 || canvasY >= height)
            {
                continue;
            }

            for (int x = 0; x < frameWidth; x++)
            {
                int canvasX = left + x;
                int source = ((y * frameWidth) + x) * 4;
                if (canvasX < 0 || canvasX >= width || frame[source + 3] == 0)
                {
                    continue;
                }

                Buffer.BlockCopy(frame, source, canvas, ((canvasY * width) + canvasX) * 4, 4);
            }
        }
    }

    private static void Clear(byte[] canvas, int width, int height, int left, int top, int frameWidth, int frameHeight)
    {
        int x0 = Math.Clamp(left, 0, width), x1 = Math.Clamp(left + frameWidth, 0, width);
        for (int y = Math.Clamp(top, 0, height); y < Math.Clamp(top + frameHeight, 0, height); y++)
        {
            Array.Clear(canvas, ((y * width) + x0) * 4, (x1 - x0) * 4);
        }
    }
}

/// <summary>
/// Measures how much of the picture moves in a decoded sample, to pick the preset order.
/// Frames are compared as 4x4 block luma means (a quarter of the resolution, as in
/// research-gif.md), which absorbs GIF dither and MP4 re-decode noise in still areas.
/// </summary>
internal static class GifMotionAnalysis
{
    internal const int BlockSize = 4;

    /// <summary>
    /// A block changes when its mean luma moves by more than this many levels. On the evidence
    /// clips still pages measured at most 0.2% changed blocks at 3 levels (1.1% at 2, from MP4
    /// re-decode and dither noise), while a slow photo pan only registered from 4 levels down.
    /// </summary>
    internal const double LumaThreshold = 3;

    /// <summary>Mean share of blocks that change between consecutive frames (0 to 1).</summary>
    internal static double ChangedFraction(GifSampleFrames frames, CancellationToken cancellationToken = default) =>
        ChangedFraction(frames, LumaThreshold, cancellationToken);

    /// <inheritdoc cref="ChangedFraction(GifSampleFrames, CancellationToken)"/>
    /// <param name="frames">Decoded sample.</param>
    /// <param name="lumaThreshold">Block mean change, in luma levels, that counts as motion.</param>
    /// <param name="cancellationToken">Stops the analysis between frames.</param>
    /// <param name="nominalDelayCentiseconds">
    /// The exporter's frame delay before merging (100 / fps / speed). The GIF writer merges
    /// identical frames into one longer image, so an image held for n frame delays stands for
    /// n - 1 still transitions; counting them keeps the share per exported frame, as before the
    /// merge. Zero treats every image as one frame.
    /// </param>
    internal static double ChangedFraction(
        GifSampleFrames frames,
        double lumaThreshold,
        CancellationToken cancellationToken = default,
        double nominalDelayCentiseconds = 0)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Frames.Count < 2)
        {
            return 0;
        }

        double[]? previous = null;
        double changed = 0;
        long transitions = -1;
        for (int frameIndex = 0; frameIndex < frames.Frames.Count; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            transitions += nominalDelayCentiseconds > 0
                ? Math.Max(1, (long)Math.Round(frames.DelaysCentiseconds[frameIndex] / nominalDelayCentiseconds, MidpointRounding.AwayFromZero))
                : 1;
            double[] blocks = BlockLuma(frames.Frames[frameIndex]);
            if (previous is not null && previous.Length == blocks.Length)
            {
                int moved = 0;
                for (int index = 0; index < blocks.Length; index++)
                {
                    if (Math.Abs(blocks[index] - previous[index]) > lumaThreshold) moved++;
                }

                changed += (double)moved / Math.Max(1, blocks.Length);
            }

            previous = blocks;
        }

        return changed / Math.Max(1, transitions);
    }

    private static double[] BlockLuma(BitmapSource frame)
    {
        BitmapSource bgra = frame.Format == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight, stride = width * 4;
        byte[] pixels = new byte[checked(stride * height)];
        bgra.CopyPixels(pixels, stride, 0);
        int columns = Math.Max(1, width / BlockSize), rows = Math.Max(1, height / BlockSize);
        var sums = new double[columns * rows];
        var counts = new int[columns * rows];
        for (int y = 0; y < height; y++)
        {
            int row = Math.Min(rows - 1, y / BlockSize);
            for (int x = 0; x < width; x++)
            {
                int offset = (y * stride) + (x * 4);
                int block = (row * columns) + Math.Min(columns - 1, x / BlockSize);
                sums[block] += (0.114 * pixels[offset]) + (0.587 * pixels[offset + 1]) + (0.299 * pixels[offset + 2]);
                counts[block]++;
            }
        }

        for (int block = 0; block < sums.Length; block++)
        {
            sums[block] /= Math.Max(1, counts[block]);
        }

        return sums;
    }
}

/// <summary>
/// Plays frames on the dispatcher at their display durations. Positions come from a
/// stopwatch, so timer jitter delays a frame change slightly but never accumulates drift.
/// </summary>
internal sealed class GifSamplePlayer
{
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private GifPlaybackTimeline? _timeline;
    private double _originMs;

    internal GifSamplePlayer(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render, dispatcher);
        _timer.Tick += (_, _) => Advance();
    }

    internal event Action<int>? FrameChanged;

    internal int Index { get; private set; }

    internal bool IsPlaying { get; private set; }

    internal GifPlaybackTimeline? Timeline => _timeline;

    internal void Load(GifPlaybackTimeline? timeline)
    {
        Pause();
        _timeline = timeline;
        Index = 0;
        FrameChanged?.Invoke(Index);
    }

    internal void Play()
    {
        if (_timeline is not { Count: > 1 } || IsPlaying)
        {
            return;
        }

        IsPlaying = true;
        _originMs = _timeline.StartOf(Index);
        _clock.Restart();
        Schedule(_originMs);
    }

    internal void Pause()
    {
        _timer.Stop();
        _clock.Stop();
        IsPlaying = false;
    }

    internal void Seek(int index)
    {
        if (_timeline is null)
        {
            return;
        }

        Index = Math.Clamp(index, 0, _timeline.Count - 1);
        if (IsPlaying)
        {
            _originMs = _timeline.StartOf(Index);
            _clock.Restart();
            Schedule(_originMs);
        }

        FrameChanged?.Invoke(Index);
    }

    private void Advance()
    {
        _timer.Stop();
        if (!IsPlaying || _timeline is null)
        {
            return;
        }

        double position = _originMs + _clock.Elapsed.TotalMilliseconds;
        int index = _timeline.IndexAt(position);
        if (index != Index)
        {
            Index = index;
            FrameChanged?.Invoke(Index);
        }

        Schedule(position);
    }

    private void Schedule(double position)
    {
        if (_timeline is null)
        {
            return;
        }

        double inLoop = position % _timeline.TotalMs;
        double remaining = _timeline.EndOf(_timeline.IndexAt(position)) - inLoop;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, remaining));
        _timer.Start();
    }
}
