namespace MyCapture.Core.Recording;

/// <summary>
/// Where the frame thumbnails of a trim timeline's filmstrip go and which frame each one shows.
/// Pure and UI-free: the strip's pixel width and the tile height decide how many aspect-correct
/// tiles fit, every tile samples the frame at the centre of its share of the clip, and the tiles
/// are stretched so that exactly <see cref="TileCount"/> of them fill the strip.
/// </summary>
/// <remarks>
/// A tile is as wide as the source aspect makes it at <see cref="TileHeight"/>, clamped to
/// [<see cref="MinTileWidth"/>, <see cref="MaxTileWidth"/>] so a very wide or a very narrow
/// source still yields a readable strip. The count is clamped to [1, <see cref="MaxTileCount"/>]
/// so a huge strip decodes a bounded number of frames.
/// </remarks>
public sealed class FilmstripLayout
{
    public const double MinTileWidth = 24.0;
    public const double MaxTileWidth = 160.0;
    public const int MaxTileCount = 48;

    private FilmstripLayout(
        double durationMs,
        int sourceWidth,
        int sourceHeight,
        double stripWidth,
        double tileHeight,
        int fps)
    {
        DurationMs = durationMs;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        StripWidth = stripWidth;
        TileHeight = tileHeight;
        Fps = fps;
        TileWidth = Math.Clamp(tileHeight * sourceWidth / sourceHeight, MinTileWidth, MaxTileWidth);
        TileCount = Math.Clamp((int)Math.Ceiling(stripWidth / TileWidth), 1, MaxTileCount);
        SlotWidth = stripWidth / TileCount;
        LastSampleTimeMs = Math.Max(0, durationMs - FrameStepCalculator.FrameDurationMs(fps));
    }

    public double DurationMs { get; }

    public int SourceWidth { get; }

    public int SourceHeight { get; }

    /// <summary>Width of the strip the tiles fill, in the caller's pixel unit.</summary>
    public double StripWidth { get; }

    /// <summary>Height of every tile, in the caller's pixel unit.</summary>
    public double TileHeight { get; }

    public int Fps { get; }

    /// <summary>Aspect-correct tile width at <see cref="TileHeight"/>, clamped to the readable range.</summary>
    public double TileWidth { get; }

    /// <summary>Number of tiles that fill the strip.</summary>
    public int TileCount { get; }

    /// <summary>Width of one tile's share of the strip: <see cref="StripWidth"/> / <see cref="TileCount"/>.</summary>
    public double SlotWidth { get; }

    /// <summary>Latest time a tile samples: one frame before the end, so the last tile decodes a real frame.</summary>
    public double LastSampleTimeMs { get; }

    /// <summary>
    /// Plans a filmstrip. Every argument must be positive and finite; a non-positive
    /// <paramref name="fps"/> or a strip or tile of no size is rejected rather than guessed.
    /// </summary>
    public static FilmstripLayout Create(
        double durationMs,
        int sourceWidth,
        int sourceHeight,
        double stripWidth,
        double tileHeight,
        int fps)
    {
        RequirePositive(durationMs, nameof(durationMs));
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceHeight, 1);
        RequirePositive(stripWidth, nameof(stripWidth));
        RequirePositive(tileHeight, nameof(tileHeight));
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        return new FilmstripLayout(durationMs, sourceWidth, sourceHeight, stripWidth, tileHeight, fps);
    }

    /// <summary>
    /// Clip time the tile at <paramref name="index"/> shows: the centre of its share of the clip,
    /// never later than <see cref="LastSampleTimeMs"/>. Non-decreasing in <paramref name="index"/>,
    /// so a forward-only decoder can read the tiles in order.
    /// </summary>
    public double SampleTimeMs(int index)
    {
        RequireIndex(index);
        return Math.Min((index + 0.5) / TileCount * DurationMs, LastSampleTimeMs);
    }

    /// <summary>Left edge of the tile at <paramref name="index"/> along the strip.</summary>
    public double TileLeft(int index)
    {
        RequireIndex(index);
        return index * StripWidth / TileCount;
    }

    /// <summary>Right edge of the tile at <paramref name="index"/>; the last tile ends exactly at <see cref="StripWidth"/>.</summary>
    public double TileRight(int index)
    {
        RequireIndex(index);
        return index == TileCount - 1 ? StripWidth : (index + 1) * StripWidth / TileCount;
    }

    /// <summary>True when <paramref name="other"/> would decode the same frames at the same size.</summary>
    public bool SamplesSameFramesAs(FilmstripLayout? other) =>
        other is not null
        && other.TileCount == TileCount
        && other.DurationMs == DurationMs
        && other.Fps == Fps
        && other.TileHeight == TileHeight
        && other.SourceWidth == SourceWidth
        && other.SourceHeight == SourceHeight;

    private void RequireIndex(int index)
    {
        if (index < 0 || index >= TileCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The filmstrip has {TileCount} tile(s).");
        }
    }

    private static void RequirePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "The value must be positive and finite.");
        }
    }
}
