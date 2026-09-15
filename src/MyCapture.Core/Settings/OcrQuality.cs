namespace MyCapture.Core.Settings;

/// <summary>
/// User-facing OCR speed/accuracy stages. Upscale and extra preparation are derived
/// from this value so settings stay a single choice rather than a raw multiplier.
/// </summary>
public enum OcrQuality
{
    /// <summary>1×, no rotation search. Fastest, weakest on small or rotated text.</summary>
    Fast = 0,

    /// <summary>2× with rotation search. Default balance for UI screenshots.</summary>
    Balanced = 1,

    /// <summary>4×, rotation search, and contrast stretch for receipts and faint type.</summary>
    Accurate = 2,
}

public static class OcrQualityNames
{
    public const string Fast = "fast";
    public const string Balanced = "balanced";
    public const string Accurate = "accurate";

    public static OcrQuality Parse(string? value)
    {
        if (string.Equals(value, Fast, StringComparison.OrdinalIgnoreCase))
        {
            return OcrQuality.Fast;
        }

        if (string.Equals(value, Accurate, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "slow", StringComparison.OrdinalIgnoreCase))
        {
            return OcrQuality.Accurate;
        }

        return OcrQuality.Balanced;
    }

    public static string ToSetting(OcrQuality quality) => quality switch
    {
        OcrQuality.Fast => Fast,
        OcrQuality.Accurate => Accurate,
        _ => Balanced,
    };
}

public static class OcrQualityProfile
{
    public static double UpscaleFactor(OcrQuality quality) => quality switch
    {
        OcrQuality.Fast => 1.0,
        OcrQuality.Accurate => 4.0,
        _ => 2.0,
    };

    public static bool SearchRotatedOrientations(OcrQuality quality) => quality != OcrQuality.Fast;

    public static bool EnhanceContrast(OcrQuality quality) => quality == OcrQuality.Accurate;

    /// <summary>
    /// Accurate/receipt mode downloads and runs PP-OCRv5 Korean recognition instead of
    /// relying on Windows OCR alone.
    /// </summary>
    public static bool UseNeuralModel(OcrQuality quality) => quality == OcrQuality.Accurate;

    public static OcrQuality FromUpscale(double upscale)
    {
        if (upscale <= 1.25)
        {
            return OcrQuality.Fast;
        }

        if (upscale >= 3.0)
        {
            return OcrQuality.Accurate;
        }

        return OcrQuality.Balanced;
    }

    /// <summary>
    /// Older files stored only <c>upscaleFactor</c>. A missing quality deserialises as
    /// Balanced, so a non-default multiplier is treated as the implied stage.
    /// </summary>
    public static OcrQuality ResolveLoaded(OcrQuality quality, double upscale)
    {
        if (quality == OcrQuality.Balanced && Math.Abs(upscale - 2.0) > 0.01)
        {
            return FromUpscale(upscale);
        }

        return quality;
    }

    public static void Synchronize(OcrSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.UpscaleFactor = UpscaleFactor(settings.Quality);
    }
}
