namespace MyCapture.Core.Themes;

/// <summary>WCAG 2 contrast helpers used to keep every palette readable.</summary>
public static class ThemeContrast
{
    public static double Ratio(ThemeColor foreground, ThemeColor background)
    {
        double lighter = Math.Max(RelativeLuminance(foreground), RelativeLuminance(background));
        double darker = Math.Min(RelativeLuminance(foreground), RelativeLuminance(background));
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// Composites a possibly translucent surface over an opaque backdrop, then measures
    /// text contrast. Glass themes must stay readable on both light and dark Mica.
    /// </summary>
    public static double RatioOverBackdrop(ThemeColor foreground, ThemeColor surface, ThemeColor backdrop)
    {
        return Ratio(foreground, Composite(surface, backdrop));
    }

    public static ThemeColor Composite(ThemeColor overlay, ThemeColor backdrop)
    {
        double alpha = overlay.A / 255d;
        return ThemeColor.Rgb(
            Blend(overlay.R, backdrop.R, alpha),
            Blend(overlay.G, backdrop.G, alpha),
            Blend(overlay.B, backdrop.B, alpha));
    }

    public static double RelativeLuminance(ThemeColor color)
    {
        return (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));
    }

    private static byte Blend(byte over, byte under, double alpha) =>
        (byte)Math.Clamp((int)Math.Round((over * alpha) + (under * (1 - alpha))), 0, 255);

    private static double Linear(byte channel)
    {
        double value = channel / 255d;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
