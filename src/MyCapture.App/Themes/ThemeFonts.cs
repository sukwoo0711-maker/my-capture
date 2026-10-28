using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
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
/// <para>
/// The text is also drawn unhinted (<see cref="Hinting"/>). Hinted rasterization, which WPF uses
/// for Fixed hinting, for Auto hinting of static text on screen and for every Display-formatted
/// run, drops the thin ㅡ of the Regular weight at body sizes: at 100% scale 14 px 으 drew as a
/// raised ㅇ ("버전ㅇ로", "핀ㅇ로") and 13 to 14.5 px 스 as ㅅ, depending on where the baseline falls
/// in the pixel. Unhinted glyphs keep every stroke at every size, weight and scale.
/// </para>
/// <para>
/// Hinting is a render option of a visual tree, not an inherited property: a window's setting
/// reaches every element drawn in the window, but not a popup, which draws in a tree of its own.
/// Menus and tooltips take it from their styles; every other popup (submenus, drop-down lists,
/// flyouts) takes it from <see cref="PopupTextProperty"/>, which the implicit Popup style sets.
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

    /// <summary>
    /// The glyph hinting every surface that draws with <see cref="Ui"/> uses: none, so no stroke
    /// of the Regular weight is lost at small sizes (see the remarks).
    /// </summary>
    public static TextHintingMode Hinting => TextHintingMode.Animated;

    /// <summary>Formats <paramref name="element"/>'s text as the standard windows do (inherited).</summary>
    internal static void ApplyText(DependencyObject element)
    {
        TextOptions.SetTextFormattingMode(element, Formatting);
        TextOptions.SetTextRenderingMode(element, Rendering);
        TextOptions.SetTextHintingMode(element, Hinting);
        if (element is FrameworkElement frameworkElement)
            frameworkElement.Language = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);
        Editing.LiteralShaping.Apply(element);
    }

    /// <summary>
    /// When true on a <see cref="Popup"/>, the root of the popup's own visual tree gets
    /// <see cref="Hinting"/> each time it opens, as a window's root does. The implicit Popup style
    /// sets it.
    /// </summary>
    public static readonly DependencyProperty PopupTextProperty = DependencyProperty.RegisterAttached(
        "PopupText", typeof(bool), typeof(ThemeFonts), new PropertyMetadata(false, OnPopupTextChanged));

    public static bool GetPopupText(DependencyObject element) => (bool)element.GetValue(PopupTextProperty);

    public static void SetPopupText(DependencyObject element, bool value) => element.SetValue(PopupTextProperty, value);

    private static void OnPopupTextChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Popup popup) return;
        popup.Opened -= OnPopupOpened;
        if (e.NewValue is not true) return;
        popup.Opened += OnPopupOpened;
        if (popup.IsOpen) ApplyPopupHinting(popup);
    }

    private static void OnPopupOpened(object? sender, EventArgs e)
    {
        if (sender is Popup popup) ApplyPopupHinting(popup);
    }

    private static void ApplyPopupHinting(Popup popup)
    {
        if (popup.Child is not { } child) return;
        // The popup's root (its PopupRoot), so the content and anything it later adds draw unhinted.
        DependencyObject root = child;
        while (VisualTreeHelper.GetParent(root) is { } parent) root = parent;
        if (root.ReadLocalValue(TextOptions.TextHintingModeProperty) == DependencyProperty.UnsetValue)
            TextOptions.SetTextHintingMode(root, Hinting);
    }

    private static FontFamily Create(string fallback) =>
        Directory.Exists(Path.Combine(FontCatalog.BundledFontRoot, "Pretendard"))
            ? new FontFamily(new Uri(FontCatalog.BundledFontRoot, UriKind.Absolute), $"./Pretendard/#Pretendard, {fallback}")
            : new FontFamily(fallback);
}
