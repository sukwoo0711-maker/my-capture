using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyCapture.App.Pinning;

/// <summary>
/// CPU rendering of the picture and its authored chrome into a caller-owned, top-down PBGRA DIB.
/// The source is never changed. There is no WPF visual, render target, black backing or shadow.
/// </summary>
internal static class NativePinRaster
{
    internal const string QualityFilter = "Separable Lanczos3, scale-aware antialiasing, premultiplied BGRA";
    internal const string LiveFilter = "Bilinear, premultiplied BGRA";
    internal const int EdgeCoverageSamples = 8;

    /// <summary>
    /// Writes only width*4 bytes of each destination row; stride padding belongs to the caller.
    /// The caller owns the DIB and serializes rendering/presentation/disposal. A frozen source can
    /// be read from a worker, but the native HWND and its selected GDI objects stay on their owner.
    /// </summary>
    internal static unsafe double Render(BitmapSource source, IntPtr destination, int width, int height,
        int stride, double dpiScale, Color outline, double cornerRadiusDip, bool highQuality,
        double contentScale = 1)
    {
        Validate(source, destination, width, height, stride);
        if (!double.IsFinite(dpiScale) || dpiScale <= 0) throw new ArgumentOutOfRangeException(nameof(dpiScale));
        if (!double.IsFinite(cornerRadiusDip) || cornerRadiusDip < 0) throw new ArgumentOutOfRangeException(nameof(cornerRadiusDip));
        if (!double.IsFinite(contentScale) || contentScale <= 0 || contentScale > 1)
            throw new ArgumentOutOfRangeException(nameof(contentScale));

        long started = Stopwatch.GetTimestamp();
        NativePinResampler.Write(source, destination, width, height, stride, highQuality, contentScale);
        ApplyChrome((byte*)destination, width, height, stride, dpiScale, outline, cornerRadiusDip, contentScale);
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    internal static double Render(NativePinPixelSource.Lease source, IntPtr destination, int width, int height,
        int stride, double dpiScale, Color outline, double cornerRadiusDip, bool highQuality,
        double contentScale = 1, CancellationToken cancellationToken = default) =>
        RenderCached(source, destination, width, height, stride, dpiScale, outline, cornerRadiusDip,
            highQuality, out _, contentScale, cancellationToken);

    internal static double RenderCached(NativePinPixelSource.Lease source, IntPtr destination, int width, int height,
        int stride, double dpiScale, Color outline, double cornerRadiusDip, bool highQuality,
        out NativePinEdgeCache edges, double contentScale = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateTarget(destination, width, height, stride);
        ValidateChrome(dpiScale, cornerRadiusDip, contentScale);
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        NativePinResampler.Write(source, destination, width, height, stride, highQuality, contentScale, cancellationToken);
        edges = NativePinEdgeCache.CaptureAndApply(destination, width, height, stride, dpiScale, outline,
            cornerRadiusDip, contentScale, cancellationToken);
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>
    /// Worker entry point. Coverage was prepared by the window's STA during its live render;
    /// this path uses only numeric arrays and owned pixel memory, never a WPF DispatcherObject.
    /// </summary>
    internal static double RenderPrepared(NativePinPixelSource.Lease source, IntPtr destination,
        int width, int height, int stride, Color outline, bool highQuality, Chrome preparedCoverage,
        out NativePinEdgeCache edges, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(preparedCoverage);
        ValidateTarget(destination, width, height, stride);
        preparedCoverage.ValidateDimensions(width, height, stride);
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        NativePinResampler.Write(source, destination, width, height, stride, highQuality,
            preparedCoverage.ContentScale, cancellationToken);
        edges = NativePinEdgeCache.CapturePreparedAndApply(destination, width, height, stride,
            preparedCoverage, outline, cancellationToken);
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>Evidence/test helper. Production uses Render directly and retains no managed frame.</summary>
    internal static unsafe byte[] RenderPixels(BitmapSource source, int width, int height,
        double dpiScale, Color outline, double cornerRadiusDip, bool highQuality, double contentScale = 1)
    {
        int stride = checked(width * 4);
        byte[] result = new byte[checked(stride * height)];
        fixed (byte* pixels = result)
            Render(source, (IntPtr)pixels, width, height, stride, dpiScale, outline, cornerRadiusDip, highQuality, contentScale);
        return result;
    }

    /// <summary>Evidence/test helper for comparing the resampling filter without border or clip.</summary>
    internal static unsafe byte[] ResamplePixels(BitmapSource source, int width, int height, bool highQuality)
    {
        int stride = checked(width * 4);
        byte[] result = new byte[checked(stride * height)];
        fixed (byte* pixels = result)
        {
            Validate(source, (IntPtr)pixels, width, height, stride);
            NativePinResampler.Write(source, (IntPtr)pixels, width, height, stride, highQuality, 1);
        }
        return result;
    }

    private static void Validate(BitmapSource source, IntPtr destination, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsFrozen) throw new ArgumentException("A frozen source bitmap is required.", nameof(source));
        ValidateTarget(destination, width, height, stride);
    }

    internal static void ValidateTarget(IntPtr destination, int width, int height, int stride)
    {
        if (destination == IntPtr.Zero) throw new ArgumentException("A destination DIB is required.", nameof(destination));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (stride < checked(width * 4)) throw new ArgumentOutOfRangeException(nameof(stride));
        _ = checked((long)stride * height);
    }

    private static void ValidateChrome(double dpiScale, double cornerRadiusDip, double contentScale)
    {
        if (!double.IsFinite(dpiScale) || dpiScale <= 0) throw new ArgumentOutOfRangeException(nameof(dpiScale));
        if (!double.IsFinite(cornerRadiusDip) || cornerRadiusDip < 0) throw new ArgumentOutOfRangeException(nameof(cornerRadiusDip));
        if (!double.IsFinite(contentScale) || contentScale <= 0 || contentScale > 1)
            throw new ArgumentOutOfRangeException(nameof(contentScale));
    }

    private static unsafe void ApplyChrome(byte* destination, int width, int height, int stride,
        double dpiScale, Color outline, double cornerRadiusDip, double contentScale) =>
        new Chrome(width, height, stride, dpiScale, cornerRadiusDip, contentScale).Apply((IntPtr)destination, outline);

    internal readonly record struct EdgeRun(int Y, int X, int Length, int RawOffset);

    /// <summary>
    /// The part of a pin's surface that a buffer holds. A live frame holds only what can be on a
    /// screen; a settled frame holds the whole surface (<see cref="IsWhole"/>).
    /// </summary>
    internal readonly record struct View(int Left, int Top, int Width, int Height, int SurfaceWidth, int SurfaceHeight)
    {
        internal bool IsWhole => Left == 0 && Top == 0 && Width == SurfaceWidth && Height == SurfaceHeight;
        internal static View Whole(int width, int height) => new(0, 0, width, height, width, height);

        /// <summary>
        /// The surface at <paramref name="left"/>, <paramref name="top"/> cut to the clip rectangle,
        /// both in the same physical coordinates. A surface that the clip does not touch at all
        /// keeps its whole area: nothing of it can be seen, and its callers expect its size.
        /// </summary>
        internal static View Clip(int left, int top, int surfaceWidth, int surfaceHeight,
            long clipLeft, long clipTop, long clipRight, long clipBottom)
        {
            long x0 = Math.Max(left, clipLeft), y0 = Math.Max(top, clipTop);
            long x1 = Math.Min((long)left + surfaceWidth, clipRight), y1 = Math.Min((long)top + surfaceHeight, clipBottom);
            return x1 <= x0 || y1 <= y0 ? Whole(surfaceWidth, surfaceHeight)
                : new((int)(x0 - left), (int)(y0 - top), (int)(x1 - x0), (int)(y1 - y0), surfaceWidth, surfaceHeight);
        }

        internal void Validate()
        {
            if (SurfaceWidth < 1 || SurfaceHeight < 1 || Width < 1 || Height < 1 || Left < 0 || Top < 0
                || (long)Left + Width > SurfaceWidth || (long)Top + Height > SurfaceHeight)
                throw new ArgumentOutOfRangeException(nameof(View), "The view must lie inside its surface.");
        }
    }

    /// <summary>Small immutable coverage tables for the changed strips, transferable from worker to STA.</summary>
    internal sealed class Chrome
    {
        private readonly int _stride;
        private readonly byte[] _coverage;
        internal EdgeRun[] Runs { get; }
        internal int RawByteLength { get; }
        internal long ByteLength => _coverage.LongLength + (Runs.LongLength * 16);
        /// <summary>The buffer's size; its rows and runs are in the buffer's own coordinates.</summary>
        internal int Width { get; }
        internal int Height { get; }
        internal double ContentScale { get; }

        internal Chrome(int width, int height, int stride, double dpiScale, double cornerRadiusDip,
            double contentScale, CancellationToken cancellationToken = default)
            : this(View.Whole(width, height), stride, dpiScale, cornerRadiusDip, contentScale, cancellationToken) { }

        /// <summary>
        /// Coverage for the strips of the surface's outline that fall inside <paramref name="view"/>.
        /// The geometry is the whole surface's, so a clipped buffer gets exactly the pixels the
        /// whole surface would have at the same place; the work is bounded by the view's perimeter.
        /// </summary>
        internal Chrome(View view, int stride, double dpiScale, double cornerRadiusDip,
            double contentScale, CancellationToken cancellationToken = default)
        {
        ValidateChrome(dpiScale, cornerRadiusDip, contentScale);
        view.Validate();
        if (stride < checked(view.Width * 4)) throw new ArgumentOutOfRangeException(nameof(stride));
        _stride = stride;
        Width = view.Width; Height = view.Height; ContentScale = contentScale;
        int width = view.SurfaceWidth, height = view.SurfaceHeight;
        // PinWindow scales the whole chrome around its centre during exit. The image is clipped
        // half a DIP inside the outline; the one-DIP outline overlays the image, not an inset image.
        double scale = dpiScale * contentScale;
        double left = (width * (1 - contentScale)) / 2;
        double top = (height * (1 - contentScale)) / 2;
        double right = width - left, bottom = height - top;
        double radius = cornerRadiusDip * scale;
        Rect outerBounds = new(left, top, right - left, bottom - top);
        Rect centreBounds = Inset(outerBounds, .5 * scale);
        double imageRadius = Math.Max(0, radius - (.5 * scale));
        var image = new EdgeShape(new RectangleGeometry(centreBounds, imageRadius, imageRadius),
            centreBounds, imageRadius, 0);
        // Border.OnRender draws at the half-stroke inset with radius r: outer/inner curves
        // have radii r +/- half a stroke and the same centre. Preserve their cubic controls
        // until device quantization; GetWidenedPathGeometry flattens too early at higher DPI.
        // These are small CPU path descriptions; no WPF visual or render target is created.
        double outerRadius = radius > 0 ? radius + (.5 * scale) : 0;
        double innerRadius = Math.Max(0, radius - (.5 * scale));
        var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
        ring.Children.Add(new RectangleGeometry(outerBounds, outerRadius, outerRadius));
        ring.Children.Add(new RectangleGeometry(Inset(outerBounds, scale), innerRadius, innerRadius));
        var stroke = new EdgeShape(ring, outerBounds, outerRadius, scale);
        // Keep one-stroke straight strips plus the small corner blocks, not radius-wide full
        // rows. The source pixels and two coverage bytes per changed pixel remain bounded by
        // the perimeter. Exit shrink additionally needs its transparent outside strips.
        int xBand = (int)Math.Clamp(Math.Ceiling(left + radius + scale + 1), 0, width);
        int yBand = (int)Math.Clamp(Math.Ceiling(top + radius + scale + 1), 0, height);
        int xEdge = (int)Math.Clamp(Math.Ceiling(left + scale), 0, width);
        int yEdge = (int)Math.Clamp(Math.Ceiling(top + scale), 0, height);
        var runs = new List<EdgeRun>(checked(view.Height * 2));
        int rawLength = 0;
        int viewRight = view.Left + view.Width;
        for (int y = view.Top; y < view.Top + view.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int band = y < yBand || y >= height - yBand ? xBand : xEdge;
            if (y < yEdge || y >= height - yEdge || band * 2 >= width)
                Add(y, 0, width);
            else
            {
                Add(y, 0, band);
                Add(y, width - band, band);
            }
        }
        Runs = runs.ToArray();
        RawByteLength = rawLength;
        _coverage = new byte[rawLength / 2];
        foreach (EdgeRun run in Runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int surfaceY = run.Y + view.Top;
            for (int x = 0; x < run.Length; x++)
            {
                int offset = (run.RawOffset / 2) + (x * 2), surfaceX = run.X + view.Left + x;
                _coverage[offset] = (byte)Math.Round(image.Coverage(surfaceX, surfaceY) * 64);
                _coverage[offset + 1] = (byte)Math.Round(stroke.Coverage(surfaceX, surfaceY) * 64);
            }
        }

        // Takes a run of the surface and keeps the part inside the view, in the view's coordinates.
        void Add(int y, int x, int length)
        {
            int from = Math.Max(x, view.Left), to = Math.Min(x + length, viewRight);
            if (to <= from) return;
            runs.Add(new EdgeRun(y - view.Top, from - view.Left, to - from, rawLength));
            rawLength = checked(rawLength + ((to - from) * 4));
        }
        }

        internal void ValidateDimensions(int width, int height, int stride)
        {
            if (width != Width || height != Height || stride != _stride)
                throw new ArgumentException("Prepared coverage must match the destination dimensions and stride.");
        }

        internal unsafe void Apply(IntPtr destination, Color outline, CancellationToken cancellationToken = default)
        {
            Color premultiplied = Color.FromArgb(outline.A, (byte)((outline.R * outline.A + 127) / 255),
                (byte)((outline.G * outline.A + 127) / 255), (byte)((outline.B * outline.A + 127) / 255));
            foreach (EdgeRun run in Runs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte* start = (byte*)destination + ((long)run.Y * _stride) + (run.X * 4);
                for (int x = 0; x < run.Length; x++)
                {
                    int offset = (run.RawOffset / 2) + (x * 2);
                    Composite(start + (x * 4), _coverage[offset], _coverage[offset + 1], premultiplied);
                }
            }
        }
    }

    private static unsafe void Composite(byte* pixel, int imageCoverage, int strokeCoverage, Color outline)
    {
        if (imageCoverage == 64 && strokeCoverage == 0) return;
        // Image clipping, solid-brush premultiplication, stroke coverage and source-over are
        // separate byte stages in WPF. Rounding only the final floating-point expression can
        // differ even on straight edges (for example Glass rest red 146 instead of 147).
        // Coverage is an integer count out of 64. These rounded integer operations are exactly
        // the former byte stages, without floating-point floor/division on every edge channel.
        int strokeAlpha = (strokeCoverage * outline.A + 32) >> 6;
        int inverseStroke = 255 - strokeAlpha;
        int alpha = strokeAlpha + (((((pixel[3] * imageCoverage + 32) >> 6) * inverseStroke) + 127) / 255);
        pixel[0] = (byte)Math.Min(alpha, ((outline.B * strokeCoverage + 32) >> 6)
            + (((((pixel[0] * imageCoverage + 32) >> 6) * inverseStroke) + 127) / 255));
        pixel[1] = (byte)Math.Min(alpha, ((outline.G * strokeCoverage + 32) >> 6)
            + (((((pixel[1] * imageCoverage + 32) >> 6) * inverseStroke) + 127) / 255));
        pixel[2] = (byte)Math.Min(alpha, ((outline.R * strokeCoverage + 32) >> 6)
            + (((((pixel[2] * imageCoverage + 32) >> 6) * inverseStroke) + 127) / 255));
        pixel[3] = (byte)alpha;
    }

    private static Rect Inset(Rect bounds, double amount) =>
        new(bounds.Left + amount, bounds.Top + amount,
            Math.Max(0, bounds.Width - (2 * amount)), Math.Max(0, bounds.Height - (2 * amount)));

    private readonly struct EdgeShape
    {
        private readonly Rect _bounds, _inner;
        private readonly double _cornerX, _cornerY;
        private readonly bool _stroke;
        private readonly Line[] _edges;

        internal EdgeShape(Geometry geometry, Rect bounds, double cornerBand, double strokeWidth)
        {
            _bounds = Snap(bounds);
            _stroke = strokeWidth > 0;
            _inner = Snap(Inset(bounds, strokeWidth));
            _cornerX = Math.Min(cornerBand, bounds.Width / 2);
            _cornerY = Math.Min(cornerBand, bounds.Height / 2);
            var edges = new List<Line>();
            // WPF quantizes cubic controls before subdivision as well as the resulting edges.
            // Quantizing only flattened points changes 144-DPI coverage by several code values.
            PathGeometry path = PathGeometry.CreateFromGeometry(geometry);
            foreach (PathFigure figure in path.Figures)
            {
                figure.StartPoint = Snap(figure.StartPoint);
                foreach (PathSegment segment in figure.Segments)
                {
                    if (segment is BezierSegment bezier)
                    {
                        bezier.Point1 = Snap(bezier.Point1);
                        bezier.Point2 = Snap(bezier.Point2);
                        bezier.Point3 = Snap(bezier.Point3);
                    }
                    else if (segment is LineSegment line) line.Point = Snap(line.Point);
                    else if (segment is PolyBezierSegment polyBezier)
                        for (int i = 0; i < polyBezier.Points.Count; i++) polyBezier.Points[i] = Snap(polyBezier.Points[i]);
                    else if (segment is PolyLineSegment polyLine)
                        for (int i = 0; i < polyLine.Points.Count; i++) polyLine.Points[i] = Snap(polyLine.Points[i]);
                }
            }
            // The standard absolute flattening tolerance is 0.25 device pixel. Small rounded
            // corners become chords, which differ measurably from ideal circular arcs.
            foreach (PathFigure figure in path.GetFlattenedPathGeometry().Figures)
            {
                Point previous = Snap(figure.StartPoint);
                foreach (PathSegment segment in figure.Segments)
                {
                    if (segment is PolyLineSegment polyline)
                        foreach (Point point in polyline.Points) Add(point);
                    else if (segment is LineSegment line) Add(line.Point);
                }
                if (figure.IsClosed) Add(figure.StartPoint);

                void Add(Point point)
                {
                    point = Snap(point);
                    if (previous.Y != point.Y) edges.Add(new Line(previous, point));
                    previous = point;
                }
            }
            _edges = edges.ToArray();
        }

        internal double Coverage(int x, int y)
        {
            if (_bounds.Width <= 0 || _bounds.Height <= 0 || x + 1 <= _bounds.Left || y + 1 <= _bounds.Top
                || x >= _bounds.Right || y >= _bounds.Bottom)
                return 0;
            // The long straight edges have exact area and do not traverse path segments.
            if ((_cornerX == 0 && _cornerY == 0)
                || (x >= _bounds.Left + _cornerX && x + 1 <= _bounds.Right - _cornerX)
                || (y >= _bounds.Top + _cornerY && y + 1 <= _bounds.Bottom - _cornerY))
                return RectangleCoverage(_bounds, x, y) - (_stroke ? RectangleCoverage(_inner, x, y) : 0);

            // Match WPF's fixed-point coverage: vertices at 1/16 device pixel and an 8x8
            // top-left-anchored sample grid. Centered supersampling visibly fattens corners.
            // Count horizontal sample intervals rather than testing every point against a path.
            // Both the rounded rectangle and widened ring use non-overlapping even/odd spans.
            Span<double> crossings = stackalloc double[_edges.Length];
            int covered = 0;
            for (int sy = 0; sy < EdgeCoverageSamples; sy++)
            {
                double sampleY = y + (sy / (double)EdgeCoverageSamples);
                int count = 0;
                foreach (Line edge in _edges)
                    if ((edge.From.Y > sampleY) != (edge.To.Y > sampleY))
                        crossings[count++] = edge.From.X + ((sampleY - edge.From.Y) * edge.Slope);
                crossings[..count].Sort();
                for (int i = 0; i + 1 < count; i += 2)
                    covered += Samples(crossings[i], crossings[i + 1], x);
            }
            return Math.Clamp(covered / (double)(EdgeCoverageSamples * EdgeCoverageSamples), 0, 1);
        }

        private static double RectangleCoverage(Rect bounds, int x, int y) =>
            Samples(bounds.Left, bounds.Right, x) * Samples(bounds.Top, bounds.Bottom, y)
                / (double)(EdgeCoverageSamples * EdgeCoverageSamples);

        private static int Samples(double start, double end, int pixel) =>
            (int)Math.Clamp(Math.Ceiling((end - pixel) * EdgeCoverageSamples), 0, EdgeCoverageSamples)
                - (int)Math.Clamp(Math.Ceiling((start - pixel) * EdgeCoverageSamples), 0, EdgeCoverageSamples);

        private static double Snap(double value) => Math.Floor((value * 16) + .5) / 16;
        private static Point Snap(Point point) => new(Snap(point.X), Snap(point.Y));
        private static Rect Snap(Rect bounds) => new(new Point(Snap(bounds.Left), Snap(bounds.Top)),
            new Point(Snap(bounds.Right), Snap(bounds.Bottom)));

        private readonly record struct Line(Point From, Point To)
        {
            internal double Slope { get; } = (To.X - From.X) / (To.Y - From.Y);
        }
    }
}
