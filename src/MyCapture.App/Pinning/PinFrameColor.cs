using System.Windows.Media;
using MyCapture.Core.Themes;

namespace MyCapture.App.Pinning;

/// <summary>
/// The colour of a pin's one-DIP outline. The dedicated brand tone identifies a MyCapture window
/// over other apps: partly transparent at rest and opaque on hover or activation.
/// </summary>
/// <remarks>
/// A contrast theme (the Windows one or the app's High contrast palette) keeps the system pair it
/// had: the neutral border colour at rest and the emphasis colour when emphasized, both opaque,
/// because a contrast palette is never drawn translucent. While a Windows contrast theme is on the
/// two tokens already hold WindowText and Highlight.
/// </remarks>
internal static class PinFrameColor
{
    /// <summary>The dedicated brand outline token, independent of focus and selection.</summary>
    internal const string AccentKey = "Pin.Frame";

    /// <summary>The token a contrast theme draws the resting outline with.</summary>
    internal const string NeutralKey = "Border.Subtle";

    /// <summary>The share of the accent's opacity the resting outline keeps.</summary>
    internal const double RestOpacity = 0.6;

    /// <summary>The accent when no token resolves: the default palette's (Glass, red accent).</summary>
    internal static readonly Color FallbackAccent = ToColor(ThemeCatalog.ColorsFor(AppTheme.Glass, AppAccent.Red)[AccentKey]);

    /// <summary>The neutral border when no token resolves: the default palette's.</summary>
    internal static readonly Color FallbackNeutral = ToColor(ThemeCatalog.ColorsFor(AppTheme.Glass)[NeutralKey]);

    /// <summary>
    /// The outline colour for the resolved <paramref name="accent"/> and <paramref name="neutral"/>
    /// token brushes; a missing or non-solid brush falls back to the default palette.
    /// </summary>
    internal static Color Resolve(Brush? accent, Brush? neutral, bool contrast, bool emphasized)
    {
        Color accentColor = Solid(accent) ?? FallbackAccent;
        if (contrast)
        {
            return emphasized ? accentColor : Solid(neutral) ?? FallbackNeutral;
        }

        return emphasized
            ? accentColor
            : Color.FromArgb((byte)Math.Round(accentColor.A * RestOpacity), accentColor.R, accentColor.G, accentColor.B);
    }

    private static Color? Solid(Brush? brush)
    {
        if (brush is not SolidColorBrush solid) return null;
        Color color = solid.Color;
        // A token brush may carry its translucency as brush opacity rather than colour alpha.
        return Color.FromArgb((byte)Math.Round(color.A * Math.Clamp(solid.Opacity, 0, 1)), color.R, color.G, color.B);
    }

    private static Color ToColor(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);
}
