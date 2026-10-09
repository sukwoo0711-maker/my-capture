using System.Globalization;

namespace MyCapture.Core.Settings;

/// <summary>The independent capture-guide/brand override: empty or opaque #RRGGBB.</summary>
public static class CaptureGuideColorValue
{
    public const string DefaultCustomColor = "#9789FF";

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        string text = value?.Trim() ?? string.Empty;
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length != 6 || !text.All(Uri.IsHexDigit)
            || !uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
            return false;
        normalized = $"#{rgb:X6}";
        return true;
    }
}