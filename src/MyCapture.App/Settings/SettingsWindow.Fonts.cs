using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MyCapture.Core.Settings;

namespace MyCapture.App.Settings;

/// <summary>
/// The editing page's font list. Until the page first appears it holds only the saved font, so
/// opening the window never enumerates the installed fonts. Shared recommendations are shown
/// first; the last item expands the virtualizing drop-down to the full installed list.
/// </summary>
internal sealed partial class SettingsWindow
{
    private bool _fontListLoaded;
    private bool _syncingFont;
    private bool _showAllFonts;

    /// <summary>The installed font families, sorted by the name shown in <paramref name="culture"/>.</summary>
    internal static IReadOnlyList<FontChoice> InstalledFonts(CultureInfo culture) => FontCatalog.InstalledFonts(culture);

    /// <summary>
    /// The list shown for <paramref name="saved"/>: the installed fonts with the saved name kept
    /// exactly as written, first when no installed family has that name.
    /// </summary>
    internal static IReadOnlyList<FontChoice> FontChoicesFor(string saved, IReadOnlyList<FontChoice> installed) =>
        FontCatalog.ChoicesFor(saved, installed);

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
            ? FontChoicesFor(saved, _showAllFonts ? FontCatalog.AvailableFonts(UiText.Culture) : FontCatalog.RecommendedFonts(UiText.Culture))
            : saved.Trim().Length == 0 ? [] : [new FontChoice(saved.Trim(), saved.Trim())];
        _syncingFont = true;
        try
        {
            FontFamilyBox.ItemTemplate ??= FontCatalog.PreviewTemplate();
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
        if (choice.IsMore)
        {
            _showAllFonts = true;
            SyncFontFamilyBox();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (FontFamilyBox.IsVisible) FontFamilyBox.IsDropDownOpen = true;
            }));
            return;
        }
        _draft.FontFamily = choice.Value;
    }

    private void OnDefaultFontClick(object sender, RoutedEventArgs e)
    {
        _draft.FontFamily = AnnotationDefaults.DefaultFontFamily;
        SyncFontFamilyBox();
    }
}
