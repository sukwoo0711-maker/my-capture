using System.IO.MemoryMappedFiles;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Capture;

/// <summary>
/// A retained tile image per 64 physical pixels. Mutating a path/clip would dirty its entire
/// accumulated bounds in WPF; these independent image sources dirty only their local tile.
/// All storage and visuals are created on mode entry, never on a pointer move.
/// </summary>
internal sealed class CaptureFreeformPreview : IDisposable
{
    internal const int TileSize = 64;
    private readonly BitmapSource _source;
    private readonly int _width, _height, _columns;
    private readonly int[] _winding;
    private readonly byte[] _borderCoverage, _glowCoverage;
    private readonly uint[] _sourceRow;
    private readonly Tile[] _tiles;
    private readonly int[] _dirtyTiles;
    private readonly MemoryMappedFile _section;
    private readonly MemoryMappedViewAccessor _sectionView;
    private readonly IntPtr _pixels;
    private readonly int _stride;
    private int _dirtyCount;
    private readonly ScaleTransform _scale;
    private readonly Color _border, _glow;
    private readonly double _borderRadius, _glowRadius;

    internal ContainerVisual Visual { get; } = new();
    internal int LastChangedTileCount { get; private set; }
    internal int TileCount => _tiles.Length;
    internal long PixelsWritten { get; private set; }
    internal bool IsDisposed { get; private set; }
    internal RectD LastChangedTileBounds { get; private set; }
    internal RectD ChangedTileBounds(int index) => _tiles[_dirtyTiles[index]].Bounds;

    internal CaptureFreeformPreview(BitmapSource source, double dipPerPixelX, double dipPerPixelY, Pen border, Pen innerGlow)
    {
        _source = source.Format == PixelFormats.Pbgra32 ? source
            : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        if (!_source.IsFrozen) _source.Freeze();
        _width = source.PixelWidth;
        _height = source.PixelHeight;
        _columns = (_width + TileSize - 1) / TileSize;
        int rows = (_height + TileSize - 1) / TileSize;
        _winding = new int[checked(_width * _height)];
        _borderCoverage = new byte[_winding.Length];
        _glowCoverage = new byte[_winding.Length];
        _sourceRow = new uint[_width];
        _stride = checked(_width * 4);
        _tiles = new Tile[checked(_columns * rows)];
        _dirtyTiles = new int[_tiles.Length];
        _scale = new ScaleTransform(dipPerPixelX, dipPerPixelY);
        Visual.Transform = _scale;
        _border = BrushColor(border.Brush);
        _glow = BrushColor(innerGlow.Brush);
        double pixel = Math.Max(double.Epsilon, Math.Min(dipPerPixelX, dipPerPixelY));
        _borderRadius = border.Thickness / pixel / 2;
        _glowRadius = innerGlow.Thickness / pixel / 2;
        // Each WIC tile maps this same anonymous section with the desktop stride. The
        // extra row allows the last partial tile's stride-sized final row to remain in
        // range. There is no backing file and no per-tile kernel section handle.
        _section = MemoryMappedFile.CreateNew(null, checked((long)_stride * (_height + 1L)));
        try
        {
            _sectionView = _section.CreateViewAccessor();
            _pixels = _sectionView.SafeMemoryMappedViewHandle.DangerousGetHandle();
            for (int row = 0; row < rows; row++)
            for (int column = 0; column < _columns; column++)
            {
                int x = column * TileSize, y = row * TileSize;
                var tile = new Tile(x, y, Math.Min(TileSize, _width - x), Math.Min(TileSize, _height - y),
                    _section.SafeMemoryMappedFileHandle.DangerousGetHandle(), _stride);
                _tiles[row * _columns + column] = tile;
                Visual.Children.Add(tile.Visual);
            }
        }
        catch
        {
            Visual.Children.Clear();
            Array.Clear(_tiles);
            _sectionView?.Dispose();
            _section.Dispose();
            throw;
        }
    }

    internal void UpdateScale(double x, double y)
    {
        if (_scale.ScaleX != x) _scale.ScaleX = x;
        if (_scale.ScaleY != y) _scale.ScaleY = y;
    }

    internal void Append(PointD first, PointD previous, PointD next)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _dirtyCount = 0;
        try
        {
            AddTriangle(first, previous, next);
            AddStroke(previous, next);
        }
        finally { PresentChangedTiles(); }
    }

    internal unsafe void Clear()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        Array.Clear(_winding);
        Array.Clear(_borderCoverage);
        Array.Clear(_glowCoverage);
        _dirtyCount = 0;
        try
        {
            for (int index = 0; index < _tiles.Length; index++)
            {
                Tile tile = _tiles[index];
                if (!tile.Used) continue;
                MarkDirty(index);
                for (int y = 0; y < tile.Height; y++)
                    new Span<uint>((byte*)_pixels + (tile.Y + y) * _stride + tile.X * 4, tile.Width).Clear();
                tile.Include(0, 0);
                tile.Include(tile.Width - 1, tile.Height - 1);
                tile.Used = false;
            }
        }
        finally { PresentChangedTiles(); }
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Visual.Children.Clear();
        Array.Clear(_tiles);
        // InteropBitmap/WIC owns its own mapping for retained composition resources;
        // releasing our writable view and section cannot invalidate those references.
        _sectionView.Dispose();
        _section.Dispose();
    }

    private void AddTriangle(PointD a, PointD b, PointD c)
    {
        double cross = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
        if (cross == 0) return;
        int direction = cross > 0 ? 1 : -1;
        int top = Math.Clamp((int)Math.Ceiling(Math.Min(a.Y, Math.Min(b.Y, c.Y)) - 0.5), 0, _height);
        int bottom = Math.Clamp((int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y)) - 0.5), 0, _height);
        for (int y = top; y < bottom; y++)
        {
            double left = double.PositiveInfinity, right = double.NegativeInfinity;
            Intersect(a, b, y + 0.5, ref left, ref right);
            Intersect(b, c, y + 0.5, ref left, ref right);
            Intersect(c, a, y + 0.5, ref left, ref right);
            if (!(left < right)) continue;
            int start = Math.Clamp((int)Math.Ceiling(left - 0.5), 0, _width);
            int end = Math.Clamp((int)Math.Ceiling(right - 0.5), 0, _width);
            int run = -1;
            for (int x = start; x < end; x++)
            {
                int offset = y * _width + x;
                int before = _winding[offset], after = before + direction;
                _winding[offset] = after;
                bool changed = (before == 0) != (after == 0);
                if (changed && run < 0) run = x;
                else if (!changed && run >= 0) { UpdateRun(run, y, x - run); run = -1; }
            }
            if (run >= 0) UpdateRun(run, y, end - run);
        }
    }

    private static void Intersect(PointD a, PointD b, double y, ref double left, ref double right)
    {
        if (!((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))) return;
        double x = a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
        left = Math.Min(left, x);
        right = Math.Max(right, x);
    }

    private void AddStroke(PointD a, PointD b)
    {
        double reach = Math.Max(_borderRadius, _glowRadius) + 0.5;
        double dx = b.X - a.X, dy = b.Y - a.Y, lengthSquared = dx * dx + dy * dy;
        int top = Math.Clamp((int)Math.Floor(Math.Min(a.Y, b.Y) - reach), 0, _height);
        int bottom = Math.Clamp((int)Math.Ceiling(Math.Max(a.Y, b.Y) + reach), 0, _height);
        for (int y = top; y < bottom; y++)
        {
            // Bound this scanline's segment, rather than visiting a diagonal's whole rectangle.
            double t0 = 0, t1 = 1;
            if (dy != 0)
            {
                double one = (y + 0.5 - reach - a.Y) / dy, two = (y + 0.5 + reach - a.Y) / dy;
                t0 = Math.Clamp(Math.Min(one, two), 0, 1);
                t1 = Math.Clamp(Math.Max(one, two), 0, 1);
            }
            double x0 = a.X + t0 * dx, x1 = a.X + t1 * dx;
            int start = Math.Clamp((int)Math.Floor(Math.Min(x0, x1) - reach), 0, _width);
            int end = Math.Clamp((int)Math.Ceiling(Math.Max(x0, x1) + reach), 0, _width);
            int run = -1;
            for (int x = start; x < end; x++)
            {
                double t = lengthSquared == 0 ? 0 : Math.Clamp(((x + 0.5 - a.X) * dx + (y + 0.5 - a.Y) * dy) / lengthSquared, 0, 1);
                double px = x + 0.5 - a.X - t * dx, py = y + 0.5 - a.Y - t * dy;
                double distance = Math.Sqrt(px * px + py * py);
                byte border = Coverage(_borderRadius, distance), glow = Coverage(_glowRadius, distance);
                int offset = y * _width + x;
                bool changed = border > _borderCoverage[offset] || glow > _glowCoverage[offset];
                if (changed)
                {
                    _borderCoverage[offset] = Math.Max(border, _borderCoverage[offset]);
                    _glowCoverage[offset] = Math.Max(glow, _glowCoverage[offset]);
                    if (run < 0) run = x;
                }
                else if (run >= 0) { UpdateRun(run, y, x - run); run = -1; }
            }
            if (run >= 0) UpdateRun(run, y, end - run);
        }
    }

    private static byte Coverage(double radius, double distance) =>
        (byte)Math.Round(Math.Clamp(radius + 0.5 - distance, 0, 1) * 255);

    private unsafe void UpdateRun(int x, int y, int width)
    {
        _source.CopyPixels(new Int32Rect(x, y, width, 1), _sourceRow, _width * 4, 0);
        for (int local = 0; local < width; local++)
        {
            int absoluteX = x + local, offset = y * _width + absoluteX;
            uint pixel = _winding[offset] != 0 ? _sourceRow[local] : 0;
            // The existing selection's three-pixel low-opacity accent is clipped to its inside.
            if (_winding[offset] != 0) pixel = Over(pixel, _glow, _glowCoverage[offset]);
            pixel = Over(pixel, _border, _borderCoverage[offset]);
            int index = y / TileSize * _columns + absoluteX / TileSize;
            Tile tile = _tiles[index];
            uint* destination = (uint*)((byte*)_pixels + y * _stride) + absoluteX;
            if (*destination == pixel) continue;
            MarkDirty(index);
            *destination = pixel;
            tile.Include(absoluteX - tile.X, y - tile.Y);
            tile.Used = true;
            PixelsWritten++;
        }
    }

    private static uint Over(uint below, Color color, byte coverage)
    {
        uint alpha = (uint)(color.A * coverage + 127) / 255, inverse = 255 - alpha;
        uint blue = ((uint)color.B * alpha + (below & 255) * inverse + 127) / 255;
        uint green = ((uint)color.G * alpha + ((below >> 8) & 255) * inverse + 127) / 255;
        uint red = ((uint)color.R * alpha + ((below >> 16) & 255) * inverse + 127) / 255;
        uint outputAlpha = alpha + (((below >> 24) * inverse + 127) / 255);
        return blue | green << 8 | red << 16 | outputAlpha << 24;
    }

    private static Color BrushColor(Brush brush) => brush is SolidColorBrush solid
        ? Color.FromArgb((byte)Math.Round(solid.Color.A * solid.Opacity), solid.Color.R, solid.Color.G, solid.Color.B)
        : throw new ArgumentException("Freeform outlines require the selector's solid guide brushes.", nameof(brush));

    private void MarkDirty(int index)
    {
        Tile tile = _tiles[index];
        if (tile.Dirty) return;
        tile.Dirty = true;
        tile.Left = tile.Width;
        tile.Top = tile.Height;
        tile.Right = tile.Bottom = 0;
        _dirtyTiles[_dirtyCount++] = index;
    }

    private void PresentChangedTiles()
    {
        int changed = 0;
        double left = _width, top = _height, right = 0, bottom = 0;
        int count = _dirtyCount;
        for (int index = 0; index < count; index++)
        {
            int tileIndex = _dirtyTiles[index];
            Tile tile = _tiles[tileIndex];
            try
            {
                if (tile.Left >= tile.Right || tile.Top >= tile.Bottom) continue;
                // WriteableBitmap.Lock creates a SafeMILHandle for every changed tile
                // after a render boundary. Section-backed Invalidate submits the same
                // local dirty rect without that per-pointer managed allocation.
                tile.Bitmap.Invalidate(new Int32Rect(tile.Left, tile.Top, tile.Right - tile.Left, tile.Bottom - tile.Top));
                _dirtyTiles[changed++] = tileIndex;
                left = Math.Min(left, tile.X); top = Math.Min(top, tile.Y);
                right = Math.Max(right, tile.X + tile.Width); bottom = Math.Max(bottom, tile.Y + tile.Height);
            }
            finally { tile.Dirty = false; }
        }
        LastChangedTileCount = changed;
        LastChangedTileBounds = changed == 0 ? RectD.Empty : new RectD(left, top, right - left, bottom - top);
        GC.KeepAlive(_sectionView);
        GC.KeepAlive(_section);
    }

    private sealed class Tile
    {
        internal readonly int X, Y, Width, Height;
        internal readonly InteropBitmap Bitmap;
        internal readonly DrawingVisual Visual = new();
        internal bool Dirty, Used;
        internal int Left, Top, Right, Bottom;
        internal RectD Bounds => new(X, Y, Width, Height);

        internal Tile(int x, int y, int width, int height, IntPtr section, int stride)
        {
            X = x; Y = y; Width = width; Height = height;
            Bitmap = (InteropBitmap)Imaging.CreateBitmapSourceFromMemorySection(section, width, height,
                PixelFormats.Pbgra32, stride, checked(y * stride + x * 4));
            Visual.Clip = new RectangleGeometry(new Rect(x, y, width, height));
            RenderOptions.SetBitmapScalingMode(Visual, BitmapScalingMode.NearestNeighbor);
            using DrawingContext dc = Visual.RenderOpen();
            dc.DrawImage(Bitmap, new Rect(x, y, width, height));
        }

        internal void Include(int x, int y)
        { Left = Math.Min(Left, x); Top = Math.Min(Top, y); Right = Math.Max(Right, x + 1); Bottom = Math.Max(Bottom, y + 1); }
    }
}
