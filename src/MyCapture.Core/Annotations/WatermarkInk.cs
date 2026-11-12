namespace MyCapture.Core.Annotations;

/// <summary>How an inserted picture is combined with what lies under it.</summary>
public enum ImageBlend
{
    /// <summary>Drawn over the capture as it is, covering what lies beneath.</summary>
    Normal,

    /// <summary>
    /// Printed into the page: the picture becomes ink that darkens a light background and
    /// lightens a dark one, so text and lines under it stay as they are. See <see cref="WatermarkInk"/>.
    /// </summary>
    IntoPage,
}

/// <summary>
/// The pixel math of a watermark that blends into the page instead of lying on top of it.
/// </summary>
/// <remarks>
/// <para>
/// Over a light page the watermark multiplies (it can only darken); over a dark page it screens
/// (it can only lighten). Dark text on a light page therefore keeps its colour exactly, as does
/// light text on a dark editor, and the watermark shows in the empty background only.
/// </para>
/// <para>
/// With a single-colour ink both operations are ordinary alpha compositing: multiplying by grey
/// <c>1 - w</c> is drawing black at alpha <c>w</c>, and screening with grey <c>w</c> is drawing
/// white at alpha <c>w</c>. So the result is a premultiplied grey overlay that any renderer draws
/// with a plain image draw, whatever lies beneath it (other annotations included), and opacity
/// and rotation apply to it as to any picture. Only the choice between the two depends on the
/// page, read from the unannotated capture as a large-radius average brightness, so text strokes
/// do not flip the choice but a dark sidebar next to a light document does.
/// </para>
/// <para>
/// The picture's colours are reduced to ink strength: its distance in brightness from the paper
/// it was drawn on (the colour of its border when the border is opaque, otherwise white for a
/// mostly dark logo and black for a mostly light one), scaled so the strongest ink is 1. A logo
/// on a white or dark plate therefore loses the plate, and a white logo on transparency keeps its
/// shape.
/// </para>
/// </remarks>
public static class WatermarkInk
{
    /// <summary>Page brightness at or below which the ink is all light (screen).</summary>
    public const double DarkPageBelow = 0.40;

    /// <summary>Page brightness at or above which the ink is all dark (multiply).</summary>
    public const double LightPageAbove = 0.55;

    /// <summary>The radius, in capture pixels, of the average that decides light or dark.</summary>
    public const int PageRadius = 20;

    /// <summary>
    /// Ink strength per pixel (0 to 1) of a straight-alpha BGRA32 picture.
    /// </summary>
    public static float[] Compute(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0) return [];
        int count = checked(width * height);
        var lum = new float[count];
        var alpha = new float[count];
        double alphaSum = 0;
        double lumSum = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = bgra.Slice(y * stride, width * 4);
            for (int x = 0; x < width; x++)
            {
                int i = (y * width) + x;
                float a = row[(x * 4) + 3] / 255f;
                float l = Luma(row[(x * 4) + 2], row[(x * 4) + 1], row[x * 4]);
                lum[i] = l;
                alpha[i] = a;
                alphaSum += a;
                lumSum += a * l;
            }
        }

        // The paper: an opaque border is the plate the logo was drawn on.
        double borderAlpha = 0;
        double borderLum = 0;
        int borderCount = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (y != 0 && y != height - 1 && x != 0 && x != width - 1) continue;
                int i = (y * width) + x;
                borderAlpha += alpha[i];
                borderLum += alpha[i] * lum[i];
                borderCount++;
            }
        }

        double paper;
        if (borderCount > 0 && borderAlpha / borderCount >= 0.9)
        {
            paper = borderLum / borderAlpha;
        }
        else
        {
            double mean = alphaSum > 0 ? lumSum / alphaSum : 0;
            paper = mean < 0.5 ? 1.0 : 0.0;
        }

        var ink = new float[count];
        float max = 0;
        for (int i = 0; i < count; i++)
        {
            float raw = alpha[i] * (float)Math.Abs(lum[i] - paper);
            ink[i] = raw;
            if (raw > max) max = raw;
        }

        if (max < 0.04f)
        {
            // A picture the same brightness as its paper throughout: keep its shape.
            for (int i = 0; i < count; i++) ink[i] = alpha[i];
            return ink;
        }

        float scale = 1f / max;
        for (int i = 0; i < count; i++) ink[i] = Math.Min(1f, ink[i] * scale);
        return ink;
    }

    /// <summary>
    /// How much of the ink is dark at a page brightness of <paramref name="pageBrightness"/>:
    /// 1 over a light page, 0 over a dark one, a smooth step between.
    /// </summary>
    public static double DarkShare(double pageBrightness)
    {
        double t = Math.Clamp((pageBrightness - DarkPageBelow) / (LightPageAbove - DarkPageBelow), 0, 1);
        return t * t * (3 - (2 * t));
    }

    /// <summary>
    /// The premultiplied grey and alpha (0 to 1) that draw <paramref name="ink"/> with a dark
    /// share of <paramref name="darkShare"/>: black at <c>ink * darkShare</c> followed by white at
    /// <c>ink * (1 - darkShare)</c>, folded into one source-over layer.
    /// </summary>
    public static (double Grey, double Alpha) Overlay(double ink, double darkShare)
    {
        double dark = ink * darkShare;
        double light = ink * (1 - darkShare);
        double alpha = 1 - ((1 - dark) * (1 - light));
        if (alpha <= 0) return (0, 0);
        return (light / alpha, alpha);
    }

    /// <summary>
    /// Fills <paramref name="pbgra"/> (premultiplied BGRA32, tightly packed, one pixel per
    /// <paramref name="ink"/> value) with the overlay at one dark share for every pixel.
    /// </summary>
    public static void FillUniform(ReadOnlySpan<float> ink, double darkShare, Span<byte> pbgra)
    {
        float share = (float)Math.Clamp(darkShare, 0, 1);
        for (int i = 0; i < ink.Length; i++) Write(pbgra, i * 4, ink[i], share);
    }

    /// <summary>
    /// Fills <paramref name="pbgra"/> (premultiplied BGRA32, <paramref name="width"/> by
    /// <paramref name="height"/>, tightly packed) with the overlay for <paramref name="ink"/>,
    /// taking the dark share from a coarse grid: node (<c>gx</c>, <c>gy</c>) of
    /// <paramref name="shares"/> (row-major, <paramref name="columns"/> wide) lies at pixel
    /// (<see cref="GridNode"/>(gx), <see cref="GridNode"/>(gy)), and the pixels between nodes are
    /// interpolated bilinearly, one grid row at a time. The page map is already a wide average, so
    /// a grid of a few pixels changes nothing a person can see, and no pixel pays for a lookup.
    /// </summary>
    public static void FillGrid(
        ReadOnlySpan<float> ink, int width, int height, ReadOnlySpan<float> shares, int columns, int rows, int step, Span<byte> pbgra)
    {
        if (width <= 0 || height <= 0) return;
        Span<float> row = columns <= 1024 ? stackalloc float[columns] : new float[columns];
        Span<float> perPixel = width <= 4096 ? stackalloc float[width] : new float[width];

        // Where each column falls between grid nodes is the same on every row.
        int[] nodeOf = new int[width];
        float[] fractionOf = new float[width];
        for (int x = 0; x < width; x++) (nodeOf[x], fractionOf[x]) = Cell(x, width, columns, step);

        for (int y = 0; y < height; y++)
        {
            (int gy, float fy) = Cell(y, height, rows, step);
            int gy1 = Math.Min(gy + 1, rows - 1);
            for (int c = 0; c < columns; c++)
            {
                float top = shares[(gy * columns) + c];
                row[c] = top + ((shares[(gy1 * columns) + c] - top) * fy);
            }

            for (int x = 0; x < width; x++)
            {
                int gx = nodeOf[x];
                float left = row[gx];
                perPixel[x] = columns > 1 ? left + ((row[gx + 1] - left) * fractionOf[x]) : left;
            }

            int line = y * width;
            for (int x = 0; x < width; x++) Write(pbgra, (line + x) * 4, ink[line + x], perPixel[x]);
        }
    }

    /// <summary>The pixel position of grid node <paramref name="index"/> along an axis of <paramref name="length"/> pixels.</summary>
    public static int GridNode(int index, int length, int step) => Math.Min(index * step, Math.Max(0, length - 1));

    /// <summary>The number of grid nodes along an axis of <paramref name="length"/> pixels; the last lies on the last pixel.</summary>
    public static int GridCount(int length, int step) => length <= 1 ? 1 : ((length - 2) / step) + 2;

    private static (int Node, float Fraction) Cell(int position, int length, int count, int step)
    {
        if (count <= 1) return (0, 0);
        int node = Math.Min(position / step, count - 2);
        int from = GridNode(node, length, step);
        int to = GridNode(node + 1, length, step);
        return (node, to > from ? Math.Clamp((position - from) / (float)(to - from), 0f, 1f) : 0f);
    }

    /// <summary>
    /// One premultiplied overlay pixel: black at <c>ink * share</c> then white at
    /// <c>ink * (1 - share)</c>, which is grey <c>ink * (1 - share)</c> premultiplied at alpha
    /// <c>ink - ink * ink * share * (1 - share)</c> (as <see cref="Overlay"/> gives).
    /// </summary>
    private static void Write(Span<byte> pbgra, int offset, float ink, float share)
    {
        float light = ink * (1 - share);
        float alpha = ink - (ink * ink * share * (1 - share));
        byte c = (byte)((light * 255) + 0.5f);
        pbgra[offset] = c;
        pbgra[offset + 1] = c;
        pbgra[offset + 2] = c;
        pbgra[offset + 3] = (byte)((alpha * 255) + 0.5f);
    }

    /// <summary>Rec. 709 brightness of an sRGB colour, 0 to 1.</summary>
    public static float Luma(byte r, byte g, byte b) => ((0.2126f * r) + (0.7152f * g) + (0.0722f * b)) / 255f;
}

/// <summary>
/// The average brightness of a capture around each point, at a coarse grid, for
/// <see cref="WatermarkInk"/> to choose dark or light ink.
/// </summary>
public sealed class PageBrightness
{
    private readonly float[] _cells;
    private readonly int _columns;
    private readonly int _rows;
    private readonly int _cell;

    private PageBrightness(float[] cells, int columns, int rows, int cell)
    {
        _cells = cells;
        _columns = columns;
        _rows = rows;
        _cell = cell;
    }

    /// <summary>Builds the map of a BGRA32 capture (alpha ignored) held in one buffer.</summary>
    public static PageBrightness From(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        var builder = new Builder(width, height);
        if (width > 0 && height > 0) builder.AddRows(bgra, 0, height, stride);
        return builder.Build();
    }

    /// <summary>
    /// Builds the map from bands of rows, so a large capture is read a few dozen rows at a time
    /// instead of copied whole.
    /// </summary>
    public sealed class Builder
    {
        private readonly int _width;
        private readonly int _height;
        private readonly int _cell;
        private readonly int _columns;
        private readonly int _rows;
        private readonly float[] _sum;

        public Builder(int width, int height)
        {
            _width = Math.Max(0, width);
            _height = Math.Max(0, height);

            // Cells of a few pixels keep a 4K capture at about a hundred thousand samples.
            _cell = Math.Max(1, (int)Math.Ceiling(Math.Max(_width, _height) / 512.0));
            _columns = Math.Max(1, (_width + _cell - 1) / _cell);
            _rows = Math.Max(1, (_height + _cell - 1) / _cell);
            _sum = new float[_columns * _rows];
        }

        /// <summary>Adds <paramref name="rowCount"/> rows of BGRA32 pixels, the first being row <paramref name="firstRow"/>.</summary>
        public void AddRows(ReadOnlySpan<byte> bgra, int firstRow, int rowCount, int stride)
        {
            for (int r = 0; r < rowCount; r++)
            {
                int y = firstRow + r;
                if ((uint)y >= (uint)_height) continue;
                ReadOnlySpan<byte> row = bgra.Slice(r * stride, _width * 4);
                int line = (y / _cell) * _columns;
                for (int x = 0; x < _width; x++)
                {
                    int o = x * 4;
                    _sum[line + (x / _cell)] += WatermarkInk.Luma(row[o + 2], row[o + 1], row[o]);
                }
            }
        }

        public PageBrightness Build()
        {
            if (_width == 0 || _height == 0) return new PageBrightness([1f], 1, 1, 1);
            var cells = new float[_sum.Length];
            for (int r = 0; r < _rows; r++)
            {
                int cellHeight = Math.Min(_cell, _height - (r * _cell));
                for (int c = 0; c < _columns; c++)
                {
                    int cellWidth = Math.Min(_cell, _width - (c * _cell));
                    cells[(r * _columns) + c] = _sum[(r * _columns) + c] / (cellWidth * cellHeight);
                }
            }

            int radius = Math.Max(1, (int)Math.Round(WatermarkInk.PageRadius / (double)_cell));
            return new PageBrightness(BoxBlur(cells, _columns, _rows, radius), _columns, _rows, _cell);
        }
    }

    /// <summary>The average brightness around capture point (<paramref name="x"/>, <paramref name="y"/>), 0 to 1.</summary>
    public double At(double x, double y)
    {
        double gx = Math.Clamp((x / _cell) - 0.5, 0, _columns - 1);
        double gy = Math.Clamp((y / _cell) - 0.5, 0, _rows - 1);
        int x0 = (int)gx;
        int y0 = (int)gy;
        int x1 = Math.Min(x0 + 1, _columns - 1);
        int y1 = Math.Min(y0 + 1, _rows - 1);
        double fx = gx - x0;
        double fy = gy - y0;
        double top = (_cells[(y0 * _columns) + x0] * (1 - fx)) + (_cells[(y0 * _columns) + x1] * fx);
        double bottom = (_cells[(y1 * _columns) + x0] * (1 - fx)) + (_cells[(y1 * _columns) + x1] * fx);
        return (top * (1 - fy)) + (bottom * fy);
    }

    private static float[] BoxBlur(float[] source, int columns, int rows, int radius)
    {
        var horizontal = new float[source.Length];
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                double total = 0;
                int n = 0;
                for (int k = Math.Max(0, x - radius); k <= Math.Min(columns - 1, x + radius); k++)
                {
                    total += source[(y * columns) + k];
                    n++;
                }

                horizontal[(y * columns) + x] = (float)(total / n);
            }
        }

        var result = new float[source.Length];
        for (int x = 0; x < columns; x++)
        {
            for (int y = 0; y < rows; y++)
            {
                double total = 0;
                int n = 0;
                for (int k = Math.Max(0, y - radius); k <= Math.Min(rows - 1, y + radius); k++)
                {
                    total += horizontal[(k * columns) + x];
                    n++;
                }

                result[(y * columns) + x] = (float)(total / n);
            }
        }

        return result;
    }
}
