using System.Globalization;
using MyCapture.Core.Settings;

namespace MyCapture.Core.Capture;

/// <summary>
/// Formats one sampled screen pixel for the capture overlay's colour copy.
/// </summary>
/// <remarks>
/// The output is meant to be pasted into CSS, design tools or code, so it is always
/// culture-invariant: hex uses upper-case digits like the magnifier label, and RGB/HSL use
/// the CSS functional notation with comma separators.
/// </remarks>
public static class ColorSampleFormatter
{
    public static string Format(byte red, byte green, byte blue, ColorFormat format)
    {
        switch (format)
        {
            case ColorFormat.Rgb:
                return string.Create(CultureInfo.InvariantCulture, $"rgb({red}, {green}, {blue})");
            case ColorFormat.Hsl:
                (int hue, int saturation, int lightness) = ToHsl(red, green, blue);
                return string.Create(CultureInfo.InvariantCulture, $"hsl({hue}, {saturation}%, {lightness}%)");
            default:
                // Undefined values (a hand-edited settings file) fall back to the default format.
                return string.Create(CultureInfo.InvariantCulture, $"#{red:X2}{green:X2}{blue:X2}");
        }
    }

    /// <summary>
    /// Converts 8-bit sRGB channels to whole-number HSL: hue in degrees [0, 359], saturation
    /// and lightness in percent [0, 100].
    /// </summary>
    public static (int Hue, int Saturation, int Lightness) ToHsl(byte red, byte green, byte blue)
    {
        byte maxChannel = Math.Max(red, Math.Max(green, blue));
        byte minChannel = Math.Min(red, Math.Min(green, blue));
        double max = maxChannel / 255.0;
        double min = minChannel / 255.0;
        double lightness = (max + min) / 2;
        double delta = max - min;
        double hue = 0;
        double saturation = 0;
        if (maxChannel != minChannel)
        {
            double r = red / 255.0;
            double g = green / 255.0;
            double b = blue / 255.0;
            saturation = delta / (1 - Math.Abs((2 * lightness) - 1));
            if (maxChannel == red) hue = 60 * (((g - b) / delta) % 6);
            else if (maxChannel == green) hue = 60 * (((b - r) / delta) + 2);
            else hue = 60 * (((r - g) / delta) + 4);
            if (hue < 0) hue += 360;
        }

        int roundedHue = (int)Math.Round(hue, MidpointRounding.AwayFromZero) % 360;
        int roundedSaturation = Math.Clamp((int)Math.Round(saturation * 100, MidpointRounding.AwayFromZero), 0, 100);
        int roundedLightness = Math.Clamp((int)Math.Round(lightness * 100, MidpointRounding.AwayFromZero), 0, 100);
        return (roundedHue, roundedSaturation, roundedLightness);
    }
}
