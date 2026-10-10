using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;

namespace MyCapture.App.Recording;

/// <summary>
/// What a GIF viewer shows after each image of <see cref="GifFrameDifferencing.ErrorBounded"/>
/// output, and the choice of which pixels the next image must repaint.
/// </summary>
/// <remarks>
/// <para>
/// Every changed frame is first quantised whole by the platform encoder, exactly as the
/// full-frame output did; call that the candidate. Repainting a pixel with the candidate's
/// colour gives the full-frame error there; keeping it gives the error of the colour already on
/// screen. Pixels where the colour on screen is at least as close to the source are kept, and
/// the squared error they save is spent keeping the pixels that would be worse by the smallest
/// amounts. The kept pixels therefore never add more squared error than they save: every
/// composited frame is at least as close to the source (its PSNR at least as high) as the
/// full-frame output, while still areas stop shimmering and cost almost nothing. So that a
/// frame-wide saving can never pay for a visible stale spot (a pointer left behind, say), a
/// pixel is also repainted whenever any channel on screen is more than
/// <see cref="MaximumExtraDeviation"/> levels further from the source than the candidate's.
/// </para>
/// <para>
/// The repainted pixels keep the candidate's colours exactly. The image carries only the
/// colours they use, in a local table just large enough for them plus one transparent entry
/// for the kept pixels inside their bounding rectangle, and is re-compressed with
/// <see cref="GifLzw"/>; a small repaint therefore costs tens of bytes instead of a 768-byte
/// table. The platform encoder leaves one of its 256 entries unused as its transparent index
/// (measured on Windows 11), so the colours always fit; if a candidate ever used all 256 or
/// its own transparent index, it is written whole.
/// </para>
/// <para>
/// The per-pixel arithmetic runs on a few thread-pool threads over fixed row bands. It only
/// reads and writes managed arrays (no WPF), and every decision is an exact integer function of
/// the pixels, so the output does not depend on the number of threads.
/// </para>
/// </remarks>
internal sealed class GifBoundedCanvas
{
    /// <summary>
    /// Largest extra per-channel deviation a kept pixel may show beyond the candidate's, in
    /// levels; the upper end of the 0 to 16 tolerance range KGySoft recommends for GIF deltas.
    /// </summary>
    internal const int MaximumExtraDeviation = 16;

    private const int ExactLevels = 1 << 16;
    private const int MustRepaint = int.MaxValue;

    private readonly int _width;
    private readonly int _height;
    private readonly uint[] _shown;
    private readonly int[] _cost;
    private readonly Band[] _bands;
    private readonly int[] _histogram = new int[ExactLevels];

    internal GifBoundedCanvas(int width, int height)
    {
        _width = width;
        _height = height;
        _shown = new uint[checked(width * height)];
        _cost = new int[_shown.Length];
        int bands = Math.Clamp(height / 32, 1, 8);
        _bands = new Band[bands];
        for (int band = 0; band < bands; band++)
        {
            _bands[band] = new Band(band * height / bands, (band + 1) * height / bands);
        }
    }

    /// <summary>
    /// A decoded candidate: its palette as BGRA, one index per pixel, and whether a pixel uses
    /// the index the candidate declares transparent (never seen for opaque input).
    /// </summary>
    internal sealed record Candidate(byte[] Payload, AnimatedGifWriter.EncodedGifFrame Parsed, uint[] Palette, byte[] Indices, bool UsesTransparentIndex);

    /// <summary>
    /// The image that keeps the composited frame within the candidate's error, or
    /// <see cref="GifFrameUpdate.Unchanged"/> when the screen already is at least as close.
    /// <see cref="Commit"/> applies it once it has been written.
    /// </summary>
    internal sealed record Plan(GifFrameUpdate Update, Candidate Candidate, GifPixelRect Repainted, bool[]? Kept);

    /// <summary>Reads a full-canvas candidate's indices, or returns null when it cannot.</summary>
    internal Candidate? Read(byte[] payload, AnimatedGifWriter.EncodedGifFrame parsed)
    {
        // Interlaced rows are stored out of order; the platform encoder does not write them.
        if ((parsed.SourceImagePacked & 0x40) != 0)
        {
            return null;
        }

        byte[] indices;
        try
        {
            indices = GifLzw.Decode(payload.AsSpan(parsed.ImageDataOffset, parsed.ImageDataLength), _shown.Length);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        var palette = new uint[parsed.PaletteLength / 3];
        for (int entry = 0; entry < palette.Length; entry++)
        {
            int offset = parsed.PaletteOffset + (entry * 3);
            palette[entry] = Pack(payload[offset + 2], payload[offset + 1], payload[offset]);
        }

        int reserved = parsed.HasTransparency ? parsed.TransparentColorIndex : -1;
        bool usesTransparent = false;
        foreach (byte index in indices)
        {
            if (index >= palette.Length)
            {
                return null;
            }

            usesTransparent |= index == reserved;
        }

        return new Candidate(payload, parsed, palette, indices, usesTransparent);
    }

    /// <summary>Shows a whole candidate, as after a full-canvas image.</summary>
    internal void Reset(Candidate candidate)
    {
        for (int pixel = 0; pixel < _shown.Length; pixel++)
        {
            _shown[pixel] = candidate.Palette[candidate.Indices[pixel]];
        }
    }

    /// <summary>
    /// Chooses the pixels to repaint for <paramref name="source"/> (32-bit BGRA rows), or
    /// returns null when the candidate must be written whole.
    /// </summary>
    internal Plan? Choose(Candidate candidate, byte[] source, int stride)
    {
        if (candidate.UsesTransparentIndex)
        {
            return null;
        }

        // Cost of keeping a pixel: its squared error on screen minus the candidate's.
        _ = Parallel.ForEach(_bands, band => band.Measure(this, candidate, source, stride));
        Array.Clear(_histogram);
        long saved = 0;
        long overflowSum = 0;
        foreach (Band band in _bands)
        {
            saved += band.Saved;
            overflowSum += band.OverflowSum;
            for (int level = 1; level < ExactLevels; level++)
            {
                _histogram[level] += band.Histogram[level];
            }
        }

        // Keep whole cost levels, cheapest first, while the saved error covers them; then keep
        // pixels of the first level that does not fit, in raster order, while it still does.
        long spent = 0;
        int keepUpTo = 0;
        for (int level = 1; level < ExactLevels; level++)
        {
            long total = (long)level * _histogram[level];
            if (spent + total > saved)
            {
                break;
            }

            spent += total;
            keepUpTo = level;
        }

        bool everyLevel = keepUpTo == ExactLevels - 1;
        bool keepOverflow = everyLevel && spent + overflowSum <= saved;
        var kept = new bool[_shown.Length];
        if (!keepOverflow)
        {
            int partial = everyLevel ? ExactLevels : keepUpTo + 1;
            for (int pixel = 0; pixel < _cost.Length && saved - spent >= partial; pixel++)
            {
                int cost = _cost[pixel];
                bool candidateLevel = everyLevel ? cost >= ExactLevels && cost != MustRepaint : cost == partial;
                if (candidateLevel && spent + cost <= saved)
                {
                    spent += cost;
                    kept[pixel] = true;
                }
            }
        }

        _ = Parallel.ForEach(_bands, band => band.Decide(this, kept, keepUpTo, keepOverflow));
        int left = _width, top = _height, right = -1, bottom = -1;
        foreach (Band band in _bands)
        {
            if (band.Right >= 0)
            {
                left = Math.Min(left, band.Left);
                right = Math.Max(right, band.Right);
                top = Math.Min(top, band.Top);
                bottom = Math.Max(bottom, band.Bottom);
            }
        }

        if (right < 0)
        {
            return new Plan(GifFrameUpdate.Unchanged, candidate, default, null);
        }

        var bounds = new GifPixelRect(left, top, right - left + 1, bottom - top + 1);

        // A local table of just the repainted colours, in candidate order, plus a transparent
        // entry when kept pixels fall inside the rectangle.
        Span<int> map = stackalloc int[256];
        map.Fill(-1);
        bool keepsInside = false;
        for (int row = 0; row < bounds.Height; row++)
        {
            int pixel = ((bounds.Top + row) * _width) + bounds.Left;
            for (int column = 0; column < bounds.Width; column++, pixel++)
            {
                if (kept[pixel])
                {
                    keepsInside = true;
                }
                else
                {
                    map[candidate.Indices[pixel]] = 0;
                }
            }
        }

        int colours = 0;
        for (int index = 0; index < map.Length; index++)
        {
            if (map[index] == 0)
            {
                map[index] = colours++;
            }
        }

        int entries = colours + (keepsInside ? 1 : 0);
        if (entries > 256)
        {
            return null;
        }

        int transparent = keepsInside ? colours : -1;
        int sizeCode = 0;
        while (1 << (sizeCode + 1) < entries)
        {
            sizeCode++;
        }

        byte[] table = new byte[3 * (1 << (sizeCode + 1))];
        for (int index = 0; index < candidate.Palette.Length; index++)
        {
            if (map[index] >= 0)
            {
                candidate.Payload.AsSpan(candidate.Parsed.PaletteOffset + (index * 3), 3).CopyTo(table.AsSpan(map[index] * 3));
            }
        }

        byte[] region = new byte[checked(bounds.Width * bounds.Height)];
        for (int row = 0; row < bounds.Height; row++)
        {
            int pixel = ((bounds.Top + row) * _width) + bounds.Left;
            int target = row * bounds.Width;
            for (int column = 0; column < bounds.Width; column++, pixel++, target++)
            {
                region[target] = (byte)(kept[pixel] ? transparent : map[candidate.Indices[pixel]]);
            }
        }

        byte[] imageData = GifLzw.Encode(region, Math.Max(2, sizeCode + 1));
        byte[] payload = SingleFrame(bounds, table, sizeCode, transparent, imageData);
        return new Plan(new GifFrameUpdate(bounds, payload), candidate, bounds, kept);
    }

    /// <summary>Shows the repainted pixels of a written plan.</summary>
    internal void Commit(Plan plan)
    {
        if (plan.Kept is not { } kept)
        {
            return;
        }

        GifPixelRect bounds = plan.Repainted;
        for (int y = bounds.Top; y < bounds.Top + bounds.Height; y++)
        {
            int pixel = (y * _width) + bounds.Left;
            for (int x = 0; x < bounds.Width; x++, pixel++)
            {
                if (!kept[pixel])
                {
                    _shown[pixel] = plan.Candidate.Palette[plan.Candidate.Indices[pixel]];
                }
            }
        }
    }

    /// <summary>A complete single-frame GIF of the region, in the platform encoder's layout.</summary>
    private static byte[] SingleFrame(GifPixelRect bounds, byte[] table, int paletteSizeCode, int transparent, byte[] imageData)
    {
        using var stream = new MemoryStream(13 + 8 + 10 + table.Length + imageData.Length + 1);
        Span<byte> header = stackalloc byte[13];
        "GIF89a"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..8], (ushort)bounds.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..10], (ushort)bounds.Height);
        stream.Write(header);
        stream.Write([0x21, 0xF9, 0x04, (byte)(transparent >= 0 ? 0x01 : 0x00), 0x00, 0x00, (byte)Math.Max(0, transparent), 0x00]);
        Span<byte> descriptor = stackalloc byte[10];
        descriptor[0] = 0x2C;
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[5..7], (ushort)bounds.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[7..9], (ushort)bounds.Height);
        descriptor[9] = (byte)(0x80 | paletteSizeCode);
        stream.Write(descriptor);
        stream.Write(table);
        stream.Write(imageData);
        stream.WriteByte(0x3B);
        return stream.ToArray();
    }

    private static uint Pack(byte blue, byte green, byte red) =>
        blue | ((uint)green << 8) | ((uint)red << 16) | 0xFF000000u;

    /// <summary>One fixed band of rows and its partial sums.</summary>
    private sealed class Band(int firstRow, int endRow)
    {
        internal int[] Histogram { get; } = new int[ExactLevels];

        internal long Saved { get; private set; }

        internal long OverflowSum { get; private set; }

        internal int Left { get; private set; }

        internal int Top { get; private set; }

        internal int Right { get; private set; }

        internal int Bottom { get; private set; }

        internal void Measure(GifBoundedCanvas canvas, Candidate candidate, byte[] source, int stride)
        {
            Array.Clear(Histogram);
            long saved = 0;
            long overflow = 0;
            int width = canvas._width;
            uint[] shown = canvas._shown;
            int[] costs = canvas._cost;
            uint[] palette = candidate.Palette;
            byte[] indices = candidate.Indices;
            for (int y = firstRow; y < endRow; y++)
            {
                int offset = y * stride;
                int pixel = y * width;
                for (int x = 0; x < width; x++, pixel++, offset += 4)
                {
                    uint kept = shown[pixel];
                    uint repaint = palette[indices[pixel]];
                    int b = source[offset], g = source[offset + 1], r = source[offset + 2];
                    int kb = (int)(kept & 0xFF) - b, kg = (int)((kept >> 8) & 0xFF) - g, kr = (int)((kept >> 16) & 0xFF) - r;
                    int rb = (int)(repaint & 0xFF) - b, rg = (int)((repaint >> 8) & 0xFF) - g, rr = (int)((repaint >> 16) & 0xFF) - r;
                    int cost = (kb * kb) + (kg * kg) + (kr * kr) - ((rb * rb) + (rg * rg) + (rr * rr));
                    if (cost < 0)
                    {
                        saved -= cost;
                    }
                    else if (cost > 0)
                    {
                        int keptDeviation = Math.Max(Math.Abs(kb), Math.Max(Math.Abs(kg), Math.Abs(kr)));
                        int repaintDeviation = Math.Max(Math.Abs(rb), Math.Max(Math.Abs(rg), Math.Abs(rr)));
                        if (keptDeviation > repaintDeviation + MaximumExtraDeviation)
                        {
                            cost = MustRepaint;
                        }
                        else if (cost >= ExactLevels)
                        {
                            overflow += cost;
                        }
                        else
                        {
                            Histogram[cost]++;
                        }
                    }

                    costs[pixel] = cost;
                }
            }

            Saved = saved;
            OverflowSum = overflow;
        }

        internal void Decide(GifBoundedCanvas canvas, bool[] kept, int keepUpTo, bool keepOverflow)
        {
            int width = canvas._width;
            int[] costs = canvas._cost;
            int left = width, top = int.MaxValue, right = -1, bottom = -1;
            for (int y = firstRow; y < endRow; y++)
            {
                int pixel = y * width;
                for (int x = 0; x < width; x++, pixel++)
                {
                    int cost = costs[pixel];
                    if (kept[pixel] || cost <= keepUpTo || (keepOverflow && cost != MustRepaint))
                    {
                        kept[pixel] = true;
                        continue;
                    }

                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = y;
                }
            }

            (Left, Top, Right, Bottom) = (left, top, right, bottom);
        }
    }
}
