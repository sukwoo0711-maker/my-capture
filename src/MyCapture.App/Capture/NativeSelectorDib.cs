using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Capture;

/// <summary>One display DIB, never a WPF visual/image source. Its original pixels remain immutable.</summary>
internal sealed class NativeSelectorDib : IDisposable
{
    private BitmapSource? _source;
    private readonly NativeSelectorPixels _pixels;
    private IntPtr _previous;
    private bool _initialized;
    private IReadOnlyList<NativeSelectorStamp> _stamps = [];
    private NativeSelectorStamp? _anchorStamp;
    // Allocated only after entering freeform. Signed fan-triangle windings let a new point
    // change only its closing triangle, including when a stroke crosses itself.
    private int[]? _freeformWinding;
    private bool _freeform;
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
    internal int InitializeCount => _initialized ? 1 : 0;

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

    internal void BeginFreeform()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _freeformWinding ??= new int[checked(Width * Height)];
        Array.Clear(_freeformWinding);
        _freeform = true;
    }

    internal void EndFreeform(Int32Rect dirty)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _freeform = false;
        Reveal = Int32Rect.Empty;
        dirty = NativeSelectorDirtyRegions.Clip(dirty, Width, Height);
        if (dirty.Width == 0 || dirty.Height == 0) return;
        _ = NativeSelectorInterop.GdiFlush();
        Update(dirty);
        CompositeStamps(dirty);
    }

    /// <summary>
    /// Append the signed triangle between the first, previous and new point. Only runs whose
    /// nonzero membership changed are recopied or invalidated. Half-open centre samples match
    /// the winding fill; neither point input nor this raster update allocates a geometry/mask.
    /// </summary>
    internal void AppendFreeformTriangle(PointD first, PointD previous, PointD next, IntPtr window)
    {
        if (!_freeform || _freeformWinding is null) return;
        double cross = (previous.X - first.X) * (next.Y - first.Y) - (next.X - first.X) * (previous.Y - first.Y);
        if (cross == 0) return;
        int direction = cross > 0 ? 1 : -1;
        int top = Math.Clamp((int)Math.Ceiling(Math.Min(first.Y, Math.Min(previous.Y, next.Y)) - 0.5), 0, Height);
        int bottom = Math.Clamp((int)Math.Ceiling(Math.Max(first.Y, Math.Max(previous.Y, next.Y)) - 0.5), 0, Height);
        _ = NativeSelectorInterop.GdiFlush();
        for (int y = top; y < bottom; y++)
        {
            double left = double.PositiveInfinity, right = double.NegativeInfinity;
            TriangleIntersection(first, previous, y + 0.5, ref left, ref right);
            TriangleIntersection(previous, next, y + 0.5, ref left, ref right);
            TriangleIntersection(next, first, y + 0.5, ref left, ref right);
            if (!(left < right)) continue;
            int start = Math.Clamp((int)Math.Ceiling(left - 0.5), 0, Width);
            int end = Math.Clamp((int)Math.Ceiling(right - 0.5), 0, Width);
            int changedStart = -1;
            for (int x = start; x < end; x++)
            {
                int offset = y * Width + x;
                int before = _freeformWinding[offset];
                int after = before + direction;
                _freeformWinding[offset] = after;
                bool changed = (before == 0) != (after == 0);
                if (changed && changedStart < 0) changedStart = x;
                else if (!changed && changedStart >= 0)
                {
                    UpdateFreeformRun(changedStart, y, x - changedStart, window);
                    changedStart = -1;
                }
            }
            if (changedStart >= 0) UpdateFreeformRun(changedStart, y, end - changedStart, window);
        }
    }

    private static void TriangleIntersection(PointD a, PointD b, double y, ref double left, ref double right)
    {
        if (!((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))) return;
        double x = a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
        left = Math.Min(left, x);
        right = Math.Max(right, x);
    }

    private void UpdateFreeformRun(int x, int y, int width, IntPtr window)
    {
        var dirty = new Int32Rect(x, y, width, 1);
        Update(dirty);
        CompositeStamps(dirty);
        if (window == IntPtr.Zero) return;
        var native = new NativeSelectorInterop.Rect(x, y, width, 1);
        _ = NativeSelectorInterop.InvalidateRect(window, ref native, false);
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
        for (int index = 0; index < _stamps.Count; index++) CompositeStamp(dirty, _stamps[index]);
        if (_anchorStamp is { } anchor) CompositeStamp(dirty, anchor);
    }

    internal IReadOnlyList<Int32Rect> SetAnchorStamp(NativeSelectorStamp? stamp)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (ReferenceEquals(_anchorStamp, stamp)) return [];
        stamp?.Validate();
        Int32Rect previous = _anchorStamp?.Bounds ?? Int32Rect.Empty;
        _anchorStamp = stamp;
        Int32Rect next = stamp?.Bounds ?? Int32Rect.Empty;
        Int32Rect[] dirty = new[] { previous, next }
            .Select(rect => NativeSelectorDirtyRegions.Clip(rect, Width, Height))
            .Where(rect => rect.Width > 0 && rect.Height > 0).Distinct().ToArray();
        _ = NativeSelectorInterop.GdiFlush();
        foreach (Int32Rect region in dirty) { Update(region); CompositeStamps(region); }
        return dirty;
    }

    private unsafe void CompositeStamp(Int32Rect dirty, NativeSelectorStamp stamp)
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

    private unsafe void Update(Int32Rect region, bool initializing = false)
    {
        int offset = checked(region.Y * Stride + region.X * 4);
        if (initializing) SelectorTimeline.Mark?.Invoke("native-dib-copy-start");
        _source!.CopyPixels(region, Bits + offset, ByteLength - offset, Stride);
        if (initializing) SelectorTimeline.Mark?.Invoke("native-dib-copy-complete");
        for (int y = region.Y; y < region.Y + region.Height; y++)
        {
            var row = new Span<uint>((byte*)Bits + y * Stride + region.X * 4, region.Width);
            if (_freeform && _freeformWinding is { } winding)
            {
                int run = 0;
                while (run < row.Length)
                {
                    bool inside = winding[y * Width + region.X + run] != 0;
                    int end = run + 1;
                    while (end < row.Length && (winding[y * Width + region.X + end] != 0) == inside) end++;
                    if (inside) NativeSelectorPixels.MakeOpaque(row[run..end]);
                    else _pixels.Dim(row[run..end]);
                    run = end;
                }
                continue;
            }
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
        _freeformWinding = null;
        _stamps = [];
        _anchorStamp = null;
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
