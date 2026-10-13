using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace MyCapture.App.Settings;

/// <summary>One installed font: the family name the editor stores and the name shown for it.</summary>
internal sealed record FontChoice(string Value, string Label);

/// <summary>
/// The editing page's font list. Until the page first appears it holds only the saved font, so
/// opening the window never enumerates the installed fonts; the list is read once per process and
/// language and shown in a virtualizing drop-down.
/// </summary>
internal sealed partial class SettingsWindow
{
    private static readonly Dictionary<string, IReadOnlyList<FontChoice>> InstalledFontCache = new(StringComparer.Ordinal);
    private bool _fontListLoaded;
    private bool _syncingFont;

    /// <summary>The installed font families, sorted by the name shown in <paramref name="culture"/>.</summary>
    internal static IReadOnlyList<FontChoice> InstalledFonts(CultureInfo culture)
    {
        lock (InstalledFontCache)
        {
            if (InstalledFontCache.TryGetValue(culture.Name, out IReadOnlyList<FontChoice>? cached)) return cached;
            XmlLanguage shown = XmlLanguage.GetLanguage(culture.IetfLanguageTag.Length == 0 ? "en-US" : culture.IetfLanguageTag);
            XmlLanguage english = XmlLanguage.GetLanguage("en-US");
            var fonts = new List<FontChoice>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FontFamily family in Fonts.SystemFontFamilies)
            {
                string value = family.Source;
                if (string.IsNullOrWhiteSpace(value) || !seen.Add(value)) continue;
                string label = family.FamilyNames.TryGetValue(shown, out string? localized) && !string.IsNullOrWhiteSpace(localized)
                    ? localized
                    : family.FamilyNames.TryGetValue(english, out string? englishName) && !string.IsNullOrWhiteSpace(englishName)
                        ? englishName
                        : value;
                fonts.Add(new FontChoice(value, label));
            }

            StringComparer order = StringComparer.Create(culture, ignoreCase: true);
            IReadOnlyList<FontChoice> sorted = fonts.OrderBy(font => font.Label, order).ToArray();
            InstalledFontCache[culture.Name] = sorted;
            return sorted;
        }
    }

    /// <summary>
    /// The list shown for <paramref name="saved"/>: the installed fonts with the saved name kept
    /// exactly as written, first when no installed family has that name.
    /// </summary>
    internal static IReadOnlyList<FontChoice> FontChoicesFor(string saved, IReadOnlyList<FontChoice> installed)
    {
        string name = saved?.Trim() ?? string.Empty;
        if (name.Length == 0) return installed;
        int match = -1;
        for (int index = 0; index < installed.Count; index++)
        {
            if (string.Equals(installed[index].Value, name, StringComparison.OrdinalIgnoreCase)) { match = index; break; }
        }

        if (match >= 0 && installed[match].Value == name) return installed;
        var choices = new List<FontChoice>(installed.Count + 1);
        if (match < 0) choices.Add(new FontChoice(name, name));
        for (int index = 0; index < installed.Count; index++)
            choices.Add(index == match ? installed[index] with { Value = name } : installed[index]);
        return choices;
    }

    /// <summary>The page is on screen: read the installed fonts now, not while the window opens.</summary>
    private void OnFontFamilyBoxShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) OnFontFamilyBoxLoaded(sender, EventArgs.Empty);
    }

    private void OnFontFamilyBoxLoaded(object? sender, EventArgs e)
    {
        if (_fontListLoaded) return;
        _fontListLoaded = true;
        SyncFontFamilyBox();
    }

    /// <summary>Selects the draft's font, adding it to the list when it is not installed.</summary>
    private void SyncFontFamilyBox()
    {
        string saved = _draft.FontFamily ?? string.Empty;
        IReadOnlyList<FontChoice> choices = _fontListLoaded
            ? FontChoicesFor(saved, InstalledFonts(UiText.Culture))
            : saved.Trim().Length == 0 ? [] : [new FontChoice(saved.Trim(), saved.Trim())];
        _syncingFont = true;
        try
        {
            if (!ReferenceEquals(FontFamilyBox.ItemsSource, choices)
                && (FontFamilyBox.ItemsSource is not IReadOnlyList<FontChoice> shown || !shown.SequenceEqual(choices)))
                FontFamilyBox.ItemsSource = choices;
            FontFamilyBox.SelectedValue = saved.Trim();
        }
        finally
        {
            _syncingFont = false;
        }
    }

    private void OnFontFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingFont || FontFamilyBox.SelectedItem is not FontChoice choice) return;
        _draft.FontFamily = choice.Value;
    }
}
