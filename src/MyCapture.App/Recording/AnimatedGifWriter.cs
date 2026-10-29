using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyCapture.App.Recording;

/// <summary>
/// What one input frame changed on the GIF canvas, as <see cref="AnimatedGifWriter"/> decided:
/// nothing (<see cref="IsUnchanged"/>: its delay extends the image already on screen), or a
/// rectangle and a complete single-frame GIF of exactly that rectangle.
/// </summary>
/// <remarks>
/// An update depends only on pixels, never on delays, so a cached sequence replayed through
/// <see cref="AnimatedGifWriter.AddEncodedFrame"/> with other delays writes the same bytes as a
/// fresh render with those delays. It is only valid after the same predecessor frames.
/// </remarks>
internal sealed record GifFrameUpdate(GifPixelRect Bounds, byte[] Payload)
{
    internal static GifFrameUpdate Unchanged { get; } = new(default, []);

    internal bool IsUnchanged => Payload.Length == 0;
}

/// <summary>Which caller of the platform GIF codec encodes each image; both give the same bytes.</summary>
internal enum GifImageEncoder
{
    /// <summary>
    /// <see cref="WicGifFrameEncoder"/>: WIC called directly, usable from any thread (default).
    /// Falls back to <see cref="Wpf"/> for an image whose direct call fails.
    /// </summary>
    Wic,

    /// <summary>WPF's <see cref="GifBitmapEncoder"/> on the writer's thread, as before; kept for comparison.</summary>
    Wpf,
}

/// <summary>
/// A whole-canvas image encoded ahead of <see cref="AnimatedGifWriter.AddFrame(byte[], int, int, GifEncodedCandidate?)"/>,
/// possibly on another thread. It depends only on the pixels of <see cref="Source"/>, which
/// must not change afterwards; the writer uses it only for that exact buffer.
/// </summary>
internal sealed class GifEncodedCandidate
{
    internal GifEncodedCandidate(
        byte[] source,
        int stride,
        int width,
        int height,
        byte[] payload,
        AnimatedGifWriter.EncodedGifFrame parsed,
        bool decoded,
        GifBoundedCanvas.Candidate? read)
    {
        Source = source;
        Stride = stride;
        Width = width;
        Height = height;
        Payload = payload;
        Parsed = parsed;
        Decoded = decoded;
        Read = read;
    }

    internal byte[] Source { get; }

    internal int Stride { get; }

    internal int Width { get; }

    internal int Height { get; }

    /// <summary>The single-frame GIF the writer would have encoded for the whole canvas.</summary>
    internal byte[] Payload { get; }

    internal AnimatedGifWriter.EncodedGifFrame Parsed { get; }

    /// <summary>Whether <see cref="Read"/> holds the error-bounded reading of the payload.</summary>
    internal bool Decoded { get; }

    /// <summary><see cref="GifBoundedCanvas.ReadCandidate"/> of the payload; null when unreadable.</summary>
    internal GifBoundedCanvas.Candidate? Read { get; }
}

/// <summary>
/// Writes a fixed-size animated GIF without retaining previously added frames.
/// </summary>
/// <remarks>
/// <para>
/// The platform (WIC) GIF codec provides the palette quantizer and LZW encoder, but it cannot
/// attach dependable per-frame timing metadata or image offsets. Each image is therefore
/// encoded as one temporary, single-frame GIF. This writer validates that payload, promotes its
/// colour table to a local table, and appends the image data behind an explicit Graphic Control
/// Extension and Image Descriptor.
/// </para>
/// <para>
/// With <see cref="GifFrameDifferencing.ErrorBounded"/> (the default) only the first image
/// covers the canvas. A frame whose source pixels equal the previous frame's adds its delay to
/// the image on screen. Any other frame is quantised whole as before, and then only the pixels
/// that keep the composited frame within that full-frame image's error are repainted, as one
/// rectangle at its offset with disposal 1 (do not dispose) and the kept pixels transparent; see
/// <see cref="GifBoundedCanvas"/>. Still areas therefore keep their colours instead of
/// shimmering with each frame's new palette, the file grows with motion rather than with
/// length alone, and no frame ends up further from the source than the full-frame output.
/// </para>
/// <para>
/// The newest image is held back until the next different frame (or <see cref="Complete"/>)
/// fixes its delay, so memory is bounded by one encoded image plus one canvas of source pixels.
/// Instances are not thread-safe. Images are encoded with <see cref="WicGifFrameEncoder"/>,
/// which calls WIC exactly as WPF's <see cref="GifBitmapEncoder"/> does and gives the same
/// bytes; unlike WPF imaging it may also run on other threads, so a caller can encode
/// whole-canvas candidates ahead of time with <see cref="EncodeCandidate"/> and pass them to
/// <see cref="AddFrame(byte[], int, int, GifEncodedCandidate?)"/>. The writer's own encodes
/// (and the WPF fallback) run on the calling thread, which must also obey the
/// dispatcher-affinity rules of a supplied <see cref="BitmapSource"/>; WPF imaging itself must
/// stay on one thread (see <see cref="GifExportEngine"/>).
/// </para>
/// </remarks>
internal sealed class AnimatedGifWriter : IDisposable
{
    private const byte ExtensionIntroducer = 0x21;
    private const byte GraphicControlLabel = 0xF9;
    private const byte ApplicationExtensionLabel = 0xFF;
    private const byte ImageSeparator = 0x2C;
    private const byte Trailer = 0x3B;

    private readonly Stream _output;
    private readonly ushort _width;
    private readonly ushort _height;
    private readonly ushort _frameDelayCentiseconds;
    private readonly ushort _loopCount;
    private readonly bool _leaveOpen;
    private readonly GifFrameDifferencing _differencing;
    private readonly GifImageEncoder _encoder;

    /// <summary>
    /// Source pixels of the latest frame (tightly packed BGRA), or null before the first frame
    /// and after a replayed update, when the next pixel frame is written in full.
    /// </summary>
    private byte[]? _canvas;
    private GifBoundedCanvas? _bounded;
    private bool _boundedShown;
    private bool _boundedUnavailable;
    private PendingImage? _pending;
    private int _frameCount;
    private int _imageCount;
    private int _preEncodedImages;
    private bool _completed;
    private bool _faulted;
    private bool _disposed;

    /// <summary>
    /// Creates a streaming animated GIF writer and writes its container header immediately.
    /// </summary>
    /// <param name="output">Writable destination positioned where the GIF should begin.</param>
    /// <param name="width">Logical canvas width, from 1 through 65,535 pixels.</param>
    /// <param name="height">Logical canvas height, from 1 through 65,535 pixels.</param>
    /// <param name="frameDelayCentiseconds">
    /// Delay written to every frame in hundredths of a second, from 2 through 65,535.
    /// </param>
    /// <param name="loopCount">
    /// NETSCAPE2.0 repeat count, from 0 through 65,535; zero means repeat indefinitely.
    /// </param>
    /// <param name="leaveOpen">Whether disposing this writer leaves <paramref name="output"/> open.</param>
    /// <param name="differencing">How consecutive frames become GIF images.</param>
    /// <param name="encoder">Which caller of the platform codec encodes images; same bytes either way.</param>
    internal AnimatedGifWriter(
        Stream output,
        int width,
        int height,
        int frameDelayCentiseconds,
        int loopCount = 0,
        bool leaveOpen = false,
        GifFrameDifferencing differencing = GifFrameDifferencing.ErrorBounded,
        GifImageEncoder encoder = GifImageEncoder.Wic)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The GIF destination must be writable.", nameof(output));
        }

        _width = ToGifUnsignedShort(width, nameof(width), minimum: 1);
        _height = ToGifUnsignedShort(height, nameof(height), minimum: 1);
        _frameDelayCentiseconds = ToGifUnsignedShort(
            frameDelayCentiseconds,
            nameof(frameDelayCentiseconds),
            minimum: 2);
        _loopCount = ToGifUnsignedShort(loopCount, nameof(loopCount), minimum: 0);
        if (!Enum.IsDefined(differencing))
        {
            throw new ArgumentOutOfRangeException(nameof(differencing));
        }

        if (!Enum.IsDefined(encoder))
        {
            throw new ArgumentOutOfRangeException(nameof(encoder));
        }

        _differencing = differencing;
        _encoder = encoder;
        _output = output;
        _leaveOpen = leaveOpen;

        WriteHeader();
        WriteLoopExtension();
    }

    /// <summary>
    /// GIF images written or held so far. Fewer than the frames when identical frames were
    /// merged; a hold longer than 655.35 s adds a repeat of the same image.
    /// </summary>
    internal int ImageCount => _imageCount;

    /// <summary>Images whose encoding came from a <see cref="GifEncodedCandidate"/> instead of this writer.</summary>
    internal int PreEncodedImages => _preEncodedImages;

    /// <summary>
    /// Whether <see cref="GifEncodedCandidate"/>s can be used: only with the direct WIC encoder,
    /// so a <see cref="GifImageEncoder.Wpf"/> writer encodes every image with WPF.
    /// </summary>
    internal bool AcceptsEncodedCandidates => _encoder == GifImageEncoder.Wic;

    /// <summary>Whether candidates should carry their error-bounded reading (<see cref="GifEncodedCandidate.Decoded"/>).</summary>
    internal bool ReadsCandidates => _differencing == GifFrameDifferencing.ErrorBounded;

    /// <summary>
    /// Whether a frame equal to the previous one is written as unchanged, without encoding; a
    /// caller then need not encode a candidate for it.
    /// </summary>
    internal bool MergesIdenticalFrames => _differencing != GifFrameDifferencing.None;

    private GifPixelRect FullCanvas => new(0, 0, _width, _height);

    /// <summary>Adds one full-canvas frame with the default delay.</summary>
    internal void AddFrame(BitmapSource frame) => AddFrame(frame, _frameDelayCentiseconds);

    /// <summary>Adds one full-canvas frame with an explicit per-frame delay.</summary>
    internal GifFrameUpdate AddFrame(BitmapSource frame, int frameDelayCentiseconds)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ThrowIfCannotWrite();
        ushort delay = ToGifUnsignedShort(
            frameDelayCentiseconds,
            nameof(frameDelayCentiseconds),
            minimum: 2);

        if (frame.PixelWidth != _width || frame.PixelHeight != _height)
        {
            throw new ArgumentException(
                $"GIF frames must be exactly {_width}x{_height} pixels; " +
                $"the supplied frame is {frame.PixelWidth}x{frame.PixelHeight}.",
                nameof(frame));
        }

        BitmapSource bgra = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int stride = _width * 4;
        byte[] pixels = new byte[checked(stride * _height)];
        bgra.CopyPixels(pixels, stride, 0);
        return AddFrameCore(pixels, stride, delay, encoded: null);
    }

    /// <summary>
    /// Adds one full-canvas frame given as 32-bit BGRA rows (normally opaque) and returns what
    /// it changed. The buffer is only read; the writer keeps its own copy of changed pixels.
    /// </summary>
    internal GifFrameUpdate AddFrame(byte[] pixels, int stride, int frameDelayCentiseconds) =>
        AddFrame(pixels, stride, frameDelayCentiseconds, encoded: null);

    /// <summary>
    /// Adds one frame like <see cref="AddFrame(byte[], int, int)"/>, taking the whole-canvas
    /// encoding from <paramref name="encoded"/> when it was made from this very buffer. The
    /// written bytes are the same with or without it: the candidate only replaces the encode
    /// this writer would otherwise run, and is ignored whenever that encode would be skipped,
    /// cover a smaller rectangle or use another encoder.
    /// </summary>
    internal GifFrameUpdate AddFrame(byte[] pixels, int stride, int frameDelayCentiseconds, GifEncodedCandidate? encoded)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ThrowIfCannotWrite();
        ushort delay = ToGifUnsignedShort(
            frameDelayCentiseconds,
            nameof(frameDelayCentiseconds),
            minimum: 2);
        if (stride < _width * 4 || pixels.Length < checked((stride * (_height - 1)) + (_width * 4)))
        {
            throw new ArgumentException(
                $"GIF frames must be exactly {_width}x{_height} pixels; the buffer is too small for that stride.",
                nameof(pixels));
        }

        bool usable = encoded is not null
            && AcceptsEncodedCandidates
            && ReferenceEquals(encoded.Source, pixels)
            && encoded.Stride == stride
            && encoded.Width == _width
            && encoded.Height == _height;
        return AddFrameCore(pixels, stride, delay, usable ? encoded : null);
    }

    /// <summary>
    /// Encodes the whole of a <paramref name="width"/> x <paramref name="height"/> BGRA frame
    /// as a writer candidate, on any thread, with the direct WIC encoder; and, when
    /// <paramref name="read"/> is set, reads it for error-bounded differencing. Touches no
    /// writer state. Returns null when the direct encoder is unavailable or fails, leaving the
    /// encode to the writer. <paramref name="pixels"/> must not change afterwards.
    /// </summary>
    internal static GifEncodedCandidate? EncodeCandidate(byte[] pixels, int stride, int width, int height, bool read)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (!WicGifFrameEncoder.IsAvailable)
        {
            return null;
        }

        byte[] payload;
        try
        {
            payload = WicGifFrameEncoder.Encode(pixels, width, height, stride);
        }
        catch (Exception ex) when (WicGifFrameEncoder.IsInteropFailure(ex))
        {
            return null;
        }

        // A payload the writer cannot parse would fail there too; let the writer's own encode
        // report it in frame order.
        EncodedGifFrame parsed;
        try
        {
            parsed = ParseSingleFrame(payload, width, height);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        GifBoundedCanvas.Candidate? candidate = read
            ? GifBoundedCanvas.ReadCandidate(payload, parsed, checked(width * height))
            : null;
        return new GifEncodedCandidate(pixels, stride, width, height, payload, parsed, read, candidate);
    }

    /// <summary>
    /// Encodes one image as a complete single-frame GIF with WPF's <see cref="GifBitmapEncoder"/>
    /// (the reference and fallback for <see cref="WicGifFrameEncoder"/>). Touches no writer state;
    /// must run on a thread that may use WPF imaging.
    /// </summary>
    internal static ReadOnlyMemory<byte> EncodeFrame(BitmapSource frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using var encodedStream = new MemoryStream();
        var encoder = new GifBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        encoder.Save(encodedStream);

        if (!encodedStream.TryGetBuffer(out ArraySegment<byte> segment) || segment.Array is null)
        {
            throw new InvalidDataException("The platform GIF encoder did not expose its output buffer.");
        }

        return segment.Array.AsMemory(segment.Offset, checked((int)encodedStream.Length));
    }

    /// <summary>
    /// Replays an update returned by an earlier <c>AddFrame</c> of the same frame sequence, with
    /// an explicit delay. A pixel frame added after a replay is written in full.
    /// </summary>
    internal void AddEncodedFrame(GifFrameUpdate update, int frameDelayCentiseconds)
    {
        ArgumentNullException.ThrowIfNull(update);
        ThrowIfCannotWrite();
        ushort delay = ToGifUnsignedShort(
            frameDelayCentiseconds,
            nameof(frameDelayCentiseconds),
            minimum: 2);
        if (update.IsUnchanged)
        {
            if (_pending is null)
            {
                throw new InvalidOperationException("The first GIF frame cannot be an unchanged update.");
            }

            ExtendPending(delay);
            _frameCount++;
            return;
        }

        GifPixelRect bounds = update.Bounds;
        if (bounds.IsEmpty || bounds.Left < 0 || bounds.Top < 0
            || bounds.Left + bounds.Width > _width || bounds.Top + bounds.Height > _height)
        {
            throw new ArgumentException("The update rectangle lies outside the GIF canvas.", nameof(update));
        }

        if (_pending is null && bounds != FullCanvas)
        {
            throw new ArgumentException("The first GIF frame must cover the whole canvas.", nameof(update));
        }

        EncodedGifFrame parsed = ParseSingleFrame(update.Payload, bounds.Width, bounds.Height);
        Append(update, parsed, delay);
        _canvas = null;
        _boundedShown = false;
        _frameCount++;
    }

    /// <summary>Writes the held image and the GIF trailer, then flushes the destination.</summary>
    /// <remarks>Calling this method more than once is harmless.</remarks>
    internal void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            return;
        }

        if (_faulted)
        {
            throw new InvalidOperationException("The GIF cannot be completed after an output failure.");
        }

        if (_frameCount == 0)
        {
            throw new InvalidOperationException("An animated GIF must contain at least one frame.");
        }

        try
        {
            if (_pending is { } last)
            {
                WriteImage(last, last.Delay);
                _pending = null;
            }

            _output.WriteByte(Trailer);
            _output.Flush();
            _completed = true;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>Marks a partial animation as abandoned so disposal never appends a trailer.</summary>
    internal void Abort()
    {
        if (!_disposed && !_completed)
        {
            _faulted = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // Do not manufacture an invalid zero-frame file during cleanup. Explicit Complete
            // still reports that programming error, while Dispose remains safe in failed setup.
            if (!_completed && !_faulted && _frameCount > 0)
            {
                Complete();
            }
        }
        finally
        {
            _disposed = true;
            _pending = null;
            _canvas = null;
            _bounded = null;
            if (!_leaveOpen)
            {
                _output.Dispose();
            }
        }
    }

    private GifFrameUpdate AddFrameCore(byte[] pixels, int stride, ushort delay, GifEncodedCandidate? encoded)
    {
        // Without a readable candidate the bounded mode degrades to whole frames, which still
        // merges identical frames and never loses quality.
        GifFrameDifferencing mode = _differencing == GifFrameDifferencing.ErrorBounded && _boundedUnavailable
            ? GifFrameDifferencing.MergeIdentical
            : _differencing;
        GifPixelRect bounds = FullCanvas;
        if (_canvas is not null && mode != GifFrameDifferencing.None)
        {
            GifPixelRect changed = GifFrameDifference.ChangedBounds(_canvas, _width * 4, pixels, stride, _width, _height);
            if (changed.IsEmpty)
            {
                ExtendPending(delay);
                _frameCount++;
                return GifFrameUpdate.Unchanged;
            }

            if (mode == GifFrameDifferencing.ChangedRectangle)
            {
                bounds = changed;
            }
        }

        // The platform encoder owns quantisation and LZW compression. Parsing finishes before
        // the first destination byte of this frame is written, so an invalid or truncated
        // platform payload cannot leave a half-added GIF frame. A whole-canvas encode of these
        // exact pixels made ahead of time is the same payload.
        byte[] payload;
        EncodedGifFrame parsed;
        if (encoded is not null && bounds == FullCanvas)
        {
            payload = encoded.Payload;
            parsed = encoded.Parsed;
            _preEncodedImages++;
        }
        else
        {
            encoded = null;
            payload = EncodeRegion(pixels, stride, bounds);
            parsed = ParseSingleFrame(payload, bounds.Width, bounds.Height);
        }

        if (mode == GifFrameDifferencing.ErrorBounded)
        {
            return AddBoundedFrame(pixels, stride, delay, payload, parsed, encoded);
        }

        var update = new GifFrameUpdate(bounds, payload);
        Append(update, parsed, delay);
        RememberCanvas(pixels, stride, bounds);
        _frameCount++;
        return update;
    }

    /// <summary>Writes the part of a whole-frame candidate that the error bound requires.</summary>
    private GifFrameUpdate AddBoundedFrame(
        byte[] pixels,
        int stride,
        ushort delay,
        byte[] candidatePayload,
        EncodedGifFrame parsed,
        GifEncodedCandidate? encoded)
    {
        _bounded ??= new GifBoundedCanvas(_width, _height);
        GifBoundedCanvas.Candidate? candidate = encoded is { Decoded: true }
            ? encoded.Read
            : _bounded.Read(candidatePayload, parsed);
        GifBoundedCanvas.Plan? plan = candidate is not null && _boundedShown
            ? _bounded.Choose(candidate, pixels, stride)
            : null;
        GifFrameUpdate update;
        if (plan is null)
        {
            // The first frame, or a candidate that cannot be masked: write it whole.
            update = new GifFrameUpdate(FullCanvas, candidatePayload);
            Append(update, parsed, delay);
            if (candidate is null)
            {
                _boundedUnavailable = true;
                _boundedShown = false;
            }
            else
            {
                _bounded.Reset(candidate);
                _boundedShown = true;
            }
        }
        else if (plan.Update.IsUnchanged)
        {
            update = plan.Update;
            ExtendPending(delay);
        }
        else
        {
            update = plan.Update;
            Append(update, ParseSingleFrame(update.Payload, update.Bounds.Width, update.Bounds.Height), delay);
            _bounded.Commit(plan);
        }

        RememberCanvas(pixels, stride, FullCanvas);
        _frameCount++;
        return update;
    }

    /// <summary>
    /// Encodes the <paramref name="bounds"/> part of a frame. Every image is encoded from
    /// BGRA32: the WIC GIF encoder quantises BGR32 and BGRA32 input differently even for equal
    /// colours, so a single input format keeps equal pixels at equal bytes on every path
    /// (including <see cref="EncodeCandidate"/>).
    /// </summary>
    private byte[] EncodeRegion(byte[] pixels, int stride, GifPixelRect bounds)
    {
        int rowBytes = bounds.Width * 4;
        byte[] region = pixels;
        int regionStride = stride;
        if (bounds != FullCanvas)
        {
            region = GC.AllocateUninitializedArray<byte>(checked(rowBytes * bounds.Height));
            regionStride = rowBytes;
            for (int row = 0; row < bounds.Height; row++)
            {
                Buffer.BlockCopy(
                    pixels,
                    ((bounds.Top + row) * stride) + (bounds.Left * 4),
                    region,
                    row * rowBytes,
                    rowBytes);
            }
        }

        if (_encoder == GifImageEncoder.Wic && WicGifFrameEncoder.IsAvailable)
        {
            try
            {
                return WicGifFrameEncoder.Encode(region, bounds.Width, bounds.Height, regionStride);
            }
            catch (Exception ex) when (WicGifFrameEncoder.IsInteropFailure(ex))
            {
                // WPF drives the same codec through the same calls, so the fallback writes the
                // same bytes; a codec failure it shares surfaces from there.
            }
        }

        BitmapSource bitmap = BitmapSource.Create(
            bounds.Width,
            bounds.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            region,
            regionStride);
        return EncodeFrame(bitmap).ToArray();
    }

    private void RememberCanvas(byte[] pixels, int stride, GifPixelRect bounds)
    {
        if (_differencing == GifFrameDifferencing.None)
        {
            return;
        }

        int canvasStride = _width * 4;
        _canvas ??= GC.AllocateUninitializedArray<byte>(canvasStride * _height);
        int rowBytes = bounds.Width * 4;
        for (int row = bounds.Top; row < bounds.Top + bounds.Height; row++)
        {
            Buffer.BlockCopy(
                pixels,
                (row * stride) + (bounds.Left * 4),
                _canvas,
                (row * canvasStride) + (bounds.Left * 4),
                rowBytes);
        }
    }

    /// <summary>Writes the held image with its final delay and holds the new one.</summary>
    private void Append(GifFrameUpdate update, EncodedGifFrame parsed, ushort delay)
    {
        try
        {
            if (_pending is { } previous)
            {
                WriteImage(previous, previous.Delay);
            }

            _pending = new PendingImage(update, parsed, delay);
            _imageCount++;
        }
        catch
        {
            // Once an output stream rejects a write, its position and persisted prefix are not
            // generally recoverable. Prevent a later Complete call from presenting it as valid.
            _faulted = true;
            throw;
        }
    }

    /// <summary>Adds an unchanged frame's delay to the image on screen.</summary>
    private void ExtendPending(ushort delay)
    {
        PendingImage pending = _pending
            ?? throw new InvalidOperationException("An unchanged frame needs an earlier image.");
        int total = pending.Delay + delay;
        if (total > ushort.MaxValue)
        {
            // GIF delays are 16-bit. Drawing the same image again changes no pixel, so a longer
            // hold is written as the image followed by a repeat of it.
            try
            {
                // Avoid a 1 cs remainder, which browsers display for 100 ms.
                int written = total - ushort.MaxValue == 1 ? ushort.MaxValue - 1 : ushort.MaxValue;
                WriteImage(pending, written);
                total -= written;
                _imageCount++;
            }
            catch
            {
                _faulted = true;
                throw;
            }

        }

        pending.Delay = total;
    }

    /// <summary>
    /// Validates a complete single-frame GIF and locates the blocks needed by the streaming
    /// container. Kept internal so malformed codec output can be verified independently.
    /// </summary>
    internal static EncodedGifFrame ParseSingleFrame(
        ReadOnlySpan<byte> encoded,
        int expectedWidth,
        int expectedHeight)
    {
        ushort width = ToGifUnsignedShort(expectedWidth, nameof(expectedWidth), minimum: 1);
        ushort height = ToGifUnsignedShort(expectedHeight, nameof(expectedHeight), minimum: 1);

        if (encoded.Length < 14 ||
            !(encoded[..6].SequenceEqual("GIF87a"u8) || encoded[..6].SequenceEqual("GIF89a"u8)))
        {
            throw Malformed("Missing GIF87a/GIF89a header or logical screen descriptor.");
        }

        int index = 6;
        ushort logicalWidth = ReadUInt16(encoded, ref index, "logical screen width");
        ushort logicalHeight = ReadUInt16(encoded, ref index, "logical screen height");
        if (logicalWidth != width || logicalHeight != height)
        {
            throw Malformed(
                $"Encoded logical screen is {logicalWidth}x{logicalHeight}, expected {width}x{height}.");
        }

        byte logicalPacked = ReadByte(encoded, ref index, "logical screen packed field");
        _ = ReadByte(encoded, ref index, "background colour index");
        _ = ReadByte(encoded, ref index, "pixel aspect ratio");

        int globalPaletteOffset = -1;
        int globalPaletteLength = 0;
        if ((logicalPacked & 0x80) != 0)
        {
            globalPaletteLength = ColourTableLength(logicalPacked);
            globalPaletteOffset = index;
            SkipBytes(encoded, ref index, globalPaletteLength, "global colour table");
        }

        bool hasTransparency = false;
        byte transparentColourIndex = 0;
        bool foundImage = false;
        bool foundTrailer = false;
        EncodedGifFrame result = default;

        while (index < encoded.Length)
        {
            byte introducer = ReadByte(encoded, ref index, "block introducer");
            switch (introducer)
            {
                case ExtensionIntroducer:
                    {
                        byte label = ReadByte(encoded, ref index, "extension label");
                        if (label == GraphicControlLabel)
                        {
                            ParseGraphicControlExtension(
                                encoded,
                                ref index,
                                ref hasTransparency,
                                ref transparentColourIndex);
                        }
                        else
                        {
                            SkipSubBlocks(encoded, ref index, "extension");
                        }

                        break;
                    }

                case ImageSeparator:
                    {
                        if (foundImage)
                        {
                            throw Malformed("A platform single-frame GIF contained more than one image.");
                        }

                        ushort left = ReadUInt16(encoded, ref index, "image left offset");
                        ushort top = ReadUInt16(encoded, ref index, "image top offset");
                        ushort imageWidth = ReadUInt16(encoded, ref index, "image width");
                        ushort imageHeight = ReadUInt16(encoded, ref index, "image height");
                        byte imagePacked = ReadByte(encoded, ref index, "image packed field");

                        if (left != 0 || top != 0 || imageWidth != width || imageHeight != height)
                        {
                            throw Malformed(
                                $"Encoded image rectangle ({left},{top}) {imageWidth}x{imageHeight} " +
                                $"does not fill the expected {width}x{height} canvas.");
                        }

                        int paletteOffset;
                        int paletteLength;
                        int paletteSizeCode;
                        if ((imagePacked & 0x80) != 0)
                        {
                            paletteLength = ColourTableLength(imagePacked);
                            paletteSizeCode = imagePacked & 0x07;
                            paletteOffset = index;
                            SkipBytes(encoded, ref index, paletteLength, "local colour table");
                        }
                        else
                        {
                            if (globalPaletteOffset < 0)
                            {
                                throw Malformed("The encoded image has neither a local nor global colour table.");
                            }

                            paletteOffset = globalPaletteOffset;
                            paletteLength = globalPaletteLength;
                            paletteSizeCode = logicalPacked & 0x07;
                        }

                        if (hasTransparency && transparentColourIndex >= paletteLength / 3)
                        {
                            throw Malformed("The transparent colour index lies outside the selected palette.");
                        }

                        int imageDataOffset = index;
                        byte minimumCodeSize = ReadByte(encoded, ref index, "LZW minimum code size");
                        if (minimumCodeSize is < 2 or > 8)
                        {
                            throw Malformed($"Invalid GIF LZW minimum code size {minimumCodeSize}.");
                        }

                        bool hasImageData = SkipSubBlocks(encoded, ref index, "image data");
                        if (!hasImageData)
                        {
                            throw Malformed("The encoded image contains no LZW data sub-block.");
                        }

                        result = new EncodedGifFrame(
                            paletteOffset,
                            paletteLength,
                            paletteSizeCode,
                            imagePacked,
                            imageDataOffset,
                            index - imageDataOffset,
                            hasTransparency,
                            transparentColourIndex);
                        foundImage = true;
                        break;
                    }

                case Trailer:
                    if (!foundImage)
                    {
                        throw Malformed("The GIF trailer appeared before an image frame.");
                    }

                    foundTrailer = true;
                    if (index != encoded.Length)
                    {
                        throw Malformed("Unexpected bytes follow the GIF trailer.");
                    }

                    break;

                default:
                    throw Malformed($"Unexpected GIF block introducer 0x{introducer:X2}.");
            }

            if (foundTrailer)
            {
                break;
            }
        }

        if (!foundImage || !foundTrailer)
        {
            throw Malformed("The single-frame GIF is missing its image or trailer.");
        }

        return result;
    }

    private static ushort ToGifUnsignedShort(int value, string parameterName, int minimum)
    {
        if (value < minimum || value > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"The value must be between {minimum} and {ushort.MaxValue}.");
        }

        return (ushort)value;
    }

    private void ThrowIfCannotWrite()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("No frames can be added after the GIF is complete.");
        }

        if (_faulted)
        {
            throw new InvalidOperationException("No frames can be added after an output failure.");
        }
    }

    private void WriteHeader()
    {
        Span<byte> header = stackalloc byte[13]
        {
            (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
            0, 0, 0, 0,
            0x70, // no global table; colour resolution advertises eight source bits
            0,
            0,
        };
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..8], _width);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..10], _height);
        _output.Write(header);
    }

    private void WriteLoopExtension()
    {
        Span<byte> extension = stackalloc byte[19]
        {
            ExtensionIntroducer,
            ApplicationExtensionLabel,
            0x0B,
            (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A',
            (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0',
            0x03,
            0x01,
            0,
            0,
            0x00,
        };
        BinaryPrimitives.WriteUInt16LittleEndian(extension[16..18], _loopCount);
        _output.Write(extension);
    }

    private void WriteImage(PendingImage image, int delay)
    {
        WriteGraphicControlExtension(image.Parsed.HasTransparency, image.Parsed.TransparentColorIndex, (ushort)delay);
        WriteImageBlock(image.Update.Payload, image.Parsed, image.Update.Bounds);
    }

    private void WriteGraphicControlExtension(
        bool hasTransparency,
        byte transparentColourIndex,
        ushort frameDelayCentiseconds)
    {
        Span<byte> extension = stackalloc byte[8]
        {
            ExtensionIntroducer,
            GraphicControlLabel,
            0x04,
            (byte)(0x04 | (hasTransparency ? 0x01 : 0x00)), // disposal 1 (keep), optional transparency
            0,
            0,
            hasTransparency ? transparentColourIndex : (byte)0,
            0x00,
        };
        BinaryPrimitives.WriteUInt16LittleEndian(extension[4..6], frameDelayCentiseconds);
        _output.Write(extension);
    }

    private void WriteImageBlock(ReadOnlySpan<byte> encoded, EncodedGifFrame frame, GifPixelRect bounds)
    {
        Span<byte> descriptor = stackalloc byte[10]
        {
            ImageSeparator,
            0, 0, // left
            0, 0, // top
            0, 0, // width
            0, 0, // height
            0,
        };
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[1..3], (ushort)bounds.Left);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[3..5], (ushort)bounds.Top);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[5..7], (ushort)bounds.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[7..9], (ushort)bounds.Height);
        descriptor[9] = (byte)(
            0x80 |                         // promote the selected palette to a local table
            (frame.SourceImagePacked & 0x60) | // preserve interlace and sort flags
            frame.PaletteSizeCode);

        _output.Write(descriptor);
        _output.Write(encoded.Slice(frame.PaletteOffset, frame.PaletteLength));
        _output.Write(encoded.Slice(frame.ImageDataOffset, frame.ImageDataLength));
    }

    private static void ParseGraphicControlExtension(
        ReadOnlySpan<byte> encoded,
        ref int index,
        ref bool hasTransparency,
        ref byte transparentColourIndex)
    {
        byte blockSize = ReadByte(encoded, ref index, "graphic control block size");
        if (blockSize != 4)
        {
            throw Malformed($"Graphic Control Extension block size is {blockSize}, expected 4.");
        }

        byte packed = ReadByte(encoded, ref index, "graphic control packed field");
        _ = ReadUInt16(encoded, ref index, "graphic control delay");
        byte colourIndex = ReadByte(encoded, ref index, "transparent colour index");
        byte terminator = ReadByte(encoded, ref index, "graphic control terminator");
        if (terminator != 0)
        {
            throw Malformed("Graphic Control Extension is missing its zero terminator.");
        }

        hasTransparency = (packed & 0x01) != 0;
        transparentColourIndex = colourIndex;
    }

    private static int ColourTableLength(byte packed) => 3 * (1 << ((packed & 0x07) + 1));

    private static byte ReadByte(ReadOnlySpan<byte> data, ref int index, string description)
    {
        if ((uint)index >= (uint)data.Length)
        {
            throw Malformed($"Unexpected end of GIF while reading {description}.");
        }

        return data[index++];
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, ref int index, string description)
    {
        if (data.Length - index < sizeof(ushort))
        {
            throw Malformed($"Unexpected end of GIF while reading {description}.");
        }

        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(index, sizeof(ushort)));
        index += sizeof(ushort);
        return value;
    }

    private static void SkipBytes(ReadOnlySpan<byte> data, ref int index, int count, string description)
    {
        if (count < 0 || data.Length - index < count)
        {
            throw Malformed($"Unexpected end of GIF while reading {description}.");
        }

        index += count;
    }

    private static bool SkipSubBlocks(ReadOnlySpan<byte> data, ref int index, string description)
    {
        bool foundData = false;
        while (true)
        {
            byte length = ReadByte(data, ref index, $"{description} sub-block length");
            if (length == 0)
            {
                return foundData;
            }

            foundData = true;
            SkipBytes(data, ref index, length, $"{description} sub-block");
        }
    }

    private static InvalidDataException Malformed(string message) => new(message);

    /// <summary>The newest image, held until the next different frame fixes its delay.</summary>
    private sealed class PendingImage(GifFrameUpdate update, EncodedGifFrame parsed, int delay)
    {
        internal GifFrameUpdate Update { get; } = update;

        internal EncodedGifFrame Parsed { get; } = parsed;

        internal int Delay { get; set; } = delay;
    }

    internal readonly record struct EncodedGifFrame(
        int PaletteOffset,
        int PaletteLength,
        int PaletteSizeCode,
        byte SourceImagePacked,
        int ImageDataOffset,
        int ImageDataLength,
        bool HasTransparency,
        byte TransparentColorIndex);
}
