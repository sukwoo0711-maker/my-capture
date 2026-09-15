namespace MyCapture.Core.Settings;

/// <summary>
/// User-facing OCR speed/accuracy stages. Upscale and extra preparation are derived
/// from this value so settings stay a single choice rather than a raw multiplier.
/// </summary>
public enum OcrQuality
{
    /// <summary>1×, no rotation search. Fastest, weakest on small or rotated text.</summary>
    Fast = 0,

    /// <summary>2× with rotation search. Middle stage for UI screenshots.</summary>
    Balanced = 1,

    /// <summary>4×, rotation search, and contrast stretch for receipts and faint type.</summary>
    Accurate = 2,

    /// <summary>Local lighting flatten, contrast, sharpen, then PP-OCR for difficult receipts.</summary>
    Enhanced = 3,
}

public static class OcrQualityNames
{
    public const string Fast = "fast";
    public const string Balanced = "balanced";
    public const string Accurate = "accurate";
    public const string Enhanced = "enhanced";

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

        if (string.Equals(value, Enhanced, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "careful", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "document", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "precise", StringComparison.OrdinalIgnoreCase))
        {
            return OcrQuality.Enhanced;
        }

        return OcrQuality.Balanced;
    }

    public static string ToSetting(OcrQuality quality) => quality switch
    {
        OcrQuality.Fast => Fast,
        OcrQuality.Accurate => Accurate,
        OcrQuality.Enhanced => Enhanced,
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

    public static bool EnhanceContrast(OcrQuality quality) =>
        quality is OcrQuality.Accurate or OcrQuality.Enhanced;

    public static bool LocalCorrection(OcrQuality quality) => quality == OcrQuality.Enhanced;

    /// <summary>
    /// Accurate and Enhanced modes download and run PP-OCRv5 Korean recognition instead of
    /// relying on Windows OCR alone.
    /// </summary>
    public static bool UseNeuralModel(OcrQuality quality) =>
        quality is OcrQuality.Accurate or OcrQuality.Enhanced;

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
    /// Fast. A multiplier that does not match that stage is treated as the implied
    /// quality. Balanced plus a non-2× multiplier is still inferred the same way.
    /// </summary>
    public static OcrQuality ResolveLoaded(OcrQuality quality, double upscale)
    {
        if (quality == OcrQuality.Fast && Math.Abs(upscale - 1.0) > 0.01)
        {
            return FromUpscale(upscale);
        }

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
