using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyCapture.App.Pinning;

/// <summary>
/// The pixels behind one <see cref="PinContent"/> (and its <c>with</c> copies). Normally a frozen bitmap.
/// A lightweight pin whose picture is opaque keeps only the native source its window draws from
/// (design 9, increment 5): the bitmap is then built again from that source whenever something reads it,
/// in the same format, resolution, bytes and, for a decoded frame, metadata, and is not kept afterwards.
/// While a built bitmap is still referenced elsewhere, readers get that same instance back.
/// </summary>
internal sealed class PinPicture
{
    private static long _rebuilds;
    private readonly object _gate = new();
    private BitmapSource? _bitmap;
    private NativePinPixelSource? _source;
    private WeakReference<BitmapSource>? _rebuilt;
    private bool _frame;
    private bool _kept;
    private bool _frameCaptured;
    private BitmapMetadata? _metadata;
    private ReadOnlyCollection<ColorContext>? _colorContexts;

    internal PinPicture(BitmapSource bitmap)
    {
        _bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
        PixelWidth = bitmap.PixelWidth;
        PixelHeight = bitmap.PixelHeight;
        Format = bitmap.Format;
        DpiX = bitmap.DpiX;
        DpiY = bitmap.DpiY;
        Width = bitmap.Width;
        Height = bitmap.Height;
        if (bitmap is BitmapFrame decoded) CaptureFrame(decoded);
    }

    // A decoded frame encodes with its metadata and colour contexts (BitmapFrame.Create copies them), so a
    // rebuilt one must carry them too. The product's decoders read a frame's metadata once on the decoding
    // thread (ImageCodec.KeepFrameMetadata), after which every thread can read it; it is copied here and
    // frozen, and the frame itself is left as it is. A frame that was not read there cannot be read here from
    // another thread: such a picture keeps its bitmap (TryDrop).
    private void CaptureFrame(BitmapFrame decoded)
    {
        _frame = true;
        try
        {
            if (decoded.Metadata is BitmapMetadata read)
            {
                BitmapMetadata copy = read.Clone();
                copy.Freeze();
                _metadata = copy;
            }
            _colorContexts = decoded.ColorContexts;
            _frameCaptured = true;
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException
            or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            _metadata = null;
            _colorContexts = null;
        }
    }

    internal int PixelWidth { get; }
    internal int PixelHeight { get; }
    internal PixelFormat Format { get; }
    internal double DpiX { get; }
    internal double DpiY { get; }

    /// <summary>Size in DIPs, as <see cref="BitmapSource.Width"/>.</summary>
    internal double Width { get; }

    internal double Height { get; }

    /// <summary>Bitmaps built from the native source in this process (diagnostics and tests).</summary>
    internal static long RebuildCount => Interlocked.Read(ref _rebuilds);

    /// <summary>The bitmap is not held; only the native source is.</summary>
    internal bool IsBitmapDropped
    {
        get { lock (_gate) return _bitmap is null; }
    }

    /// <summary>The pixels as a frozen bitmap, built from the native source when the bitmap was dropped.</summary>
    internal BitmapSource Bitmap
    {
        get
        {
            lock (_gate) return _bitmap ?? LiveRebuilt() ?? Rebuild();
        }
    }

    /// <summary>The bitmap if one exists now (held, or built earlier and still referenced); never builds one.</summary>
    internal BitmapSource? Peek
    {
        get { lock (_gate) return _bitmap ?? LiveRebuilt(); }
    }

    /// <summary>
    /// Lets the bitmap go and keeps a share of <paramref name="source"/> instead, when the source is an exact
    /// copy of this bitmap (opaque, same format and size). Returns whether the bitmap is no longer held.
    /// </summary>
    internal bool TryDrop(NativePinPixelSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            // A kept bitmap stays: a WPF window shows it, so dropping it would save nothing.
            // Only what the picture captured when it was made is read: the frame may belong to another thread.
            // The formats of an exact copy have no palette.
            if (_kept || _bitmap is null) return false;
            if (!source.IsExactOpaqueCopy || source.ExactFormat != Format
                || source.Width != PixelWidth || source.Height != PixelHeight
                || (_frame && !_frameCaptured)) return false;
            NativePinPixelSource share = source.Share();
            share.AddMemoryPressure();
            _source = share;
            _bitmap = null;
            _rebuilt = null;
            return true;
        }
    }

    /// <summary>A further owner of the native source for another window showing this picture, if it is dropped.</summary>
    internal NativePinPixelSource? ShareSource()
    {
        lock (_gate) return _bitmap is null ? _source?.Share() : null;
    }

    /// <summary>
    /// Holds a bitmap again, for good (a pin moving to its WPF window): built once from the native source.
    /// The source stays until <see cref="ReleaseSource"/>, because the native window still draws from it.
    /// </summary>
    internal BitmapSource Keep()
    {
        lock (_gate)
        {
            _kept = true;
            if (_bitmap is { } held) return held;
            _bitmap = LiveRebuilt() ?? Rebuild();
            _rebuilt = null;
            return _bitmap;
        }
    }

    /// <summary>Gives up the native source once the bitmap is held again (<see cref="Keep"/>).</summary>
    internal void ReleaseSource()
    {
        NativePinPixelSource? source;
        lock (_gate)
        {
            if (_bitmap is null) return;
            source = _source;
            _source = null;
            _metadata = null;
            _colorContexts = null;
        }
        source?.Dispose();
    }

    private BitmapSource? LiveRebuilt() => _rebuilt is { } weak && weak.TryGetTarget(out BitmapSource? alive) ? alive : null;

    private BitmapSource Rebuild()
    {
        NativePinPixelSource source = _source ?? throw new ObjectDisposedException(nameof(PinPicture));
        BitmapSource bitmap = source.CreateBitmap(DpiX, DpiY);
        if (_frame)
        {
            BitmapFrame frame = BitmapFrame.Create(bitmap, null, _metadata, _colorContexts);
            frame.Freeze();
            bitmap = frame;
        }
        Interlocked.Increment(ref _rebuilds);
        _rebuilt = new WeakReference<BitmapSource>(bitmap);
        return bitmap;
    }
}
