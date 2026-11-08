using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;

namespace MyCapture.App.Capture;

/// <summary>One display DIB, never a WPF visual/image source. Its original pixels remain immutable.</summary>
internal sealed class NativeSelectorDib : IDisposable
{
    private BitmapSource? _source;
    private readonly NativeSelectorPixels _pixels;
    private IntPtr _previous;
    private bool _initialized;
    private IReadOnlyList<NativeSelectorStamp> _stamps = [];
    internal IntPtr Bitmap { get; private set; }
    internal IntPtr Dc { get; private set; }
    internal IntPtr Bits { get; private set; }
    internal int Width { get; }
    internal int Height { get; }
    internal int Stride { get; }
    internal int ByteLength { get; }
    internal Int32Rect Reveal { get; private set; }
    internal bool IsDisposed { get; private set; }
    internal long PixelsUpdated { get; private set; }

    internal NativeSelectorDib(BitmapSource source, SolidColorBrush dimmer, bool deferPixels = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dimmer);
        Width = source.PixelWidth;
        Height = source.PixelHeight;
        Stride = checked(Width * 4);
        ByteLength = checked(Stride * Height);
        _source = source.Format == PixelFormats.Bgr32 ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        if (!_source.IsFrozen) _source.Freeze();
        _pixels = new NativeSelectorPixels(dimmer.Color,
            (int)Math.Round(dimmer.Color.A * Math.Clamp(dimmer.Opacity, 0, 1)));
        try
        {
            Dc = NativeSelectorInterop.CreateCompatibleDC(IntPtr.Zero);
            if (Dc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = new NativeSelectorInterop.BitmapInfo
            {
                Size = 40, Width = Width, Height = -Height, Planes = 1, BitCount = 32,
            };
            Bitmap = NativeSelectorInterop.CreateDIBSection(Dc, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            Bits = bits;
            if (Bitmap == IntPtr.Zero || Bits == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _previous = NativeSelectorInterop.SelectObject(Dc, Bitmap);
            if (_previous == IntPtr.Zero || _previous == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!deferPixels) InitializePixels();
        }
        catch { Dispose(); throw; }
    }

    // The owning STA creates all GDI objects. Only the immutable source read and raw pixel fill
    // may run on a worker; its owner joins that task before paint, mutation, or disposal.
    internal void InitializePixels()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_initialized) throw new InvalidOperationException("The DIB pixels are already initialized.");
        Update(new Int32Rect(0, 0, Width, Height), initializing: true);
        _initialized = true;
    }

    internal IReadOnlyList<Int32Rect> SetReveal(Int32Rect rectangle)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Int32Rect next = NativeSelectorDirtyRegions.Clip(rectangle, Width, Height);
        IReadOnlyList<Int32Rect> dirty = NativeSelectorDirtyRegions.Changed(Reveal, next, Width, Height);
        Reveal = next;
        // Finish reads by the previous BitBlt before writing the DIB directly.
        _ = NativeSelectorInterop.GdiFlush();
        foreach (Int32Rect region in dirty) { Update(region); CompositeStamps(region); }
        return dirty;
    }

    internal IReadOnlyList<Int32Rect> SetStamps(IReadOnlyList<NativeSelectorStamp> stamps)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_stamps.SequenceEqual(stamps)) return [];
        foreach (NativeSelectorStamp stamp in stamps) stamp.Validate();
        Int32Rect[] dirty = _stamps.Concat(stamps).Select(stamp => NativeSelectorDirtyRegions.Clip(stamp.Bounds, Width, Height))
            .Where(rect => rect.Width > 0 && rect.Height > 0).Distinct().ToArray();
        _stamps = stamps.ToArray();
        _ = NativeSelectorInterop.GdiFlush();
        foreach (Int32Rect region in dirty) { Update(region); CompositeStamps(region); }
        return dirty;
    }

    private unsafe void CompositeStamps(Int32Rect dirty)
    {
        foreach (NativeSelectorStamp stamp in _stamps)
        {
            Int32Rect box = stamp.Bounds;
            int left = Math.Max(dirty.X, box.X), top = Math.Max(dirty.Y, box.Y);
            int right = Math.Min(dirty.X + dirty.Width, box.X + box.Width);
            int bottom = Math.Min(dirty.Y + dirty.Height, box.Y + box.Height);
            for (int y = top; y < bottom; y++)
            {
                byte* destination = (byte*)Bits + y * Stride;
                for (int x = left; x < right; x++)
                {
                    int sourceOffset = ((y - box.Y) * box.Width + x - box.X) * 4;
                    int alpha = stamp.Pixels[sourceOffset + 3];
                    for (int channel = 0; channel < 3; channel++)
                        destination[x * 4 + channel] = (byte)Math.Min(255, stamp.Pixels[sourceOffset + channel]
                            + (destination[x * 4 + channel] * (255 - alpha) + 127) / 255);
                    destination[x * 4 + 3] = 255;
                }
            }
        }
    }

    private unsafe void Update(Int32Rect region, bool initializing = false)
    {
        int offset = checked(region.Y * Stride + region.X * 4);
        if (initializing) SelectorTimeline.Mark?.Invoke("native-dib-copy-start");
        _source!.CopyPixels(region, Bits + offset, ByteLength - offset, Stride);
        if (initializing) SelectorTimeline.Mark?.Invoke("native-dib-copy-complete");
        for (int y = region.Y; y < region.Y + region.Height; y++)
        {
            var row = new Span<uint>((byte*)Bits + y * Stride + region.X * 4, region.Width);
            if (y < Reveal.Y || y >= Reveal.Y + Reveal.Height || Reveal.Width == 0)
            {
                _pixels.Dim(row);
                continue;
            }
            int left = Math.Clamp(Reveal.X - region.X, 0, region.Width);
            int right = Math.Clamp(Reveal.X + Reveal.Width - region.X, 0, region.Width);
            _pixels.Dim(row[..left]);
            NativeSelectorPixels.MakeOpaque(row[left..right]);
            _pixels.Dim(row[right..]);
        }
        if (initializing) SelectorTimeline.Mark?.Invoke("native-dib-dim-complete");
        PixelsUpdated += (long)region.Width * region.Height;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _ = NativeSelectorInterop.GdiFlush();
        if (_previous != IntPtr.Zero && _previous != new IntPtr(-1) && Dc != IntPtr.Zero)
            _ = NativeSelectorInterop.SelectObject(Dc, _previous);
        if (Bitmap != IntPtr.Zero) _ = NativeSelectorInterop.DeleteObject(Bitmap);
        if (Dc != IntPtr.Zero) _ = NativeSelectorInterop.DeleteDC(Dc);
        // GDI can batch object deletion too; close must finish releasing the large backing store.
        _ = NativeSelectorInterop.GdiFlush();
        Bitmap = Dc = Bits = _previous = IntPtr.Zero;
        _source = null;
        _stamps = [];
    }
}

/// <summary>A bounded, premultiplied BGRA chrome image, never a desktop-sized WPF bitmap.</summary>
internal sealed record NativeSelectorStamp(Int32Rect Bounds, byte[] Pixels)
{
    internal void Validate()
    {
        if (Bounds.Width < 1 || Bounds.Height < 1 || Pixels.Length != checked(Bounds.Width * Bounds.Height * 4))
            throw new ArgumentException("Invalid chrome stamp dimensions.");
    }
}
