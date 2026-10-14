namespace MyCapture.Core.Settings;

public enum ImageExportFormat { Automatic, Png, Jpeg }
public enum ImageExportBackground { Transparent, White, LightGray, DarkGray, Accent }
public enum ImageExportPreset { Original, Messenger, Document }

/// <summary>Settings for an exported copy; never applied to library media.</summary>
public sealed record ImageExportOptions
{
    public ImageExportFormat Format { get; init; }
    public int LongEdge { get; init; }
    public int ReductionPercent { get; init; }
    public bool IncludeSourceUrl { get; init; }
    public int Margin { get; init; }
    public int CornerRadius { get; init; }
    public ImageExportBackground Background { get; init; }
    public bool Shadow { get; init; }

    public ImageExportOptions Normalize() => this with
    {
        Format = Enum.IsDefined(Format) ? Format : ImageExportFormat.Automatic,
        LongEdge = LongEdge is 800 or 1280 or 1920 or 2560 ? LongEdge : 0,
        ReductionPercent = Math.Clamp(ReductionPercent, 0, 90),
        Margin = Math.Clamp(Margin, 0, 128),
        CornerRadius = Math.Clamp(CornerRadius, 0, 48),
        Background = Enum.IsDefined(Background) ? Background : ImageExportBackground.Transparent,
    };

    public static ImageExportOptions FromPreset(ImageExportPreset preset) => preset switch
    {
        ImageExportPreset.Messenger => new() { Format = ImageExportFormat.Jpeg, LongEdge = 1280 },
        ImageExportPreset.Document => new() { Format = ImageExportFormat.Png, LongEdge = 1920 },
        _ => new(),
    };

    public static (int Width, int Height) Fit(int width, int height, int longEdge)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (longEdge <= 0 || Math.Max(width, height) <= longEdge) return (width, height);
        double scale = (double)longEdge / Math.Max(width, height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    public (int Width, int Height) OutputSize(int width, int height)
    {
        ImageExportOptions options = Normalize();
        (int w, int h) = Fit(width, height, options.LongEdge);
        return (checked(w + 2 * options.Margin), checked(h + 2 * options.Margin));
    }
}
