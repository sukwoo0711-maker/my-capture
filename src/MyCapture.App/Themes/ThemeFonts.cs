using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace MyCapture.App.Themes;

/// <summary>
/// The UI typeface (Font.Ui and Font.Display): the bundled Pretendard with Regular, Medium,
/// SemiBold and Bold files.
/// </summary>
/// <remarks>
/// <para>
/// Malgun Gothic has no SemiBold, so the previous Segoe UI + Malgun Gothic chain drew every
/// SemiBold Korean label in Malgun Gothic Bold. Pretendard has each requested weight.
/// </para>
/// <para>
/// Pretendard is a CFF font, and Display text formatting distorts some of its Hangul strokes
/// (the ㅅ in 스), so the surfaces that use it format text with Ideal metrics. At 100% scale
/// ClearType then smears its horizontal strokes into a bold blur, so they draw with grayscale
/// antialiasing, which keeps Regular and SemiBold apart (as WinUI text does). Its default
/// ligatures and contextual alternates rewrite "0x1F" and "1280x720" with a multiplication
/// sign; <see cref="ApplyText"/> turns them off for labels and editing boxes.
/// </para>
/// <para>A copy without the font files falls back to the previous system chain.</para>
/// </remarks>
public static class ThemeFonts
{
    private const string UiFallback = "Segoe UI Variable Text, Segoe UI, Malgun Gothic, sans-serif";
    private const string DisplayFallback = "Segoe UI Variable Display, Segoe UI, Malgun Gothic, sans-serif";

    public static FontFamily Ui { get; } = Create(UiFallback);

    public static FontFamily Display { get; } = Create(DisplayFallback);

    /// <summary>The text formatting every surface that draws with <see cref="Ui"/> uses.</summary>
    public static TextFormattingMode Formatting => TextFormattingMode.Ideal;

    /// <summary>The antialiasing every surface that draws with <see cref="Ui"/> uses.</summary>
    public static TextRenderingMode Rendering => TextRenderingMode.Grayscale;

    /// <summary>Formats <paramref name="element"/>'s text as the standard windows do (inherited).</summary>
    internal static void ApplyText(DependencyObject element)
    {
        TextOptions.SetTextFormattingMode(element, Formatting);
        TextOptions.SetTextRenderingMode(element, Rendering);
        TextOptions.SetTextHintingMode(element, TextHintingMode.Fixed);
        if (element is FrameworkElement frameworkElement)
            frameworkElement.Language = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);
        Editing.LiteralShaping.Apply(element);
    }

    private static FontFamily Create(string fallback) =>
        Directory.Exists(Path.Combine(FontCatalog.BundledFontRoot, "Pretendard"))
            ? new FontFamily(new Uri(FontCatalog.BundledFontRoot, UriKind.Absolute), $"./Pretendard/#Pretendard, {fallback}")
            : new FontFamily(fallback);
}
