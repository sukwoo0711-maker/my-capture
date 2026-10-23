using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.Core.Annotations;
using MyCapture.Core.Undo;

namespace MyCapture.App.Editing;

internal sealed partial class AnnotationEditorControl
{
    private static readonly ConditionalWeakTable<Dispatcher, DataTemplate> FontPreviewTemplates = new();

    private FrameworkElement BuildFontFamilySection()
    {
        var panel = PropertyGroup("AnnotationFontFamilyProperties");
        panel.Children.Add(PropertyLabel(UiText.Get("EditorText_FontFamily")));
        _textFontFamily = new ComboBox
        {
            MinHeight = 32,
            Width = 160,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            MaxDropDownHeight = 320,
            IsTextSearchEnabled = true,
            ItemTemplate = FontPreviewTemplates.GetValue(Dispatcher, static _ => FontCatalog.PreviewTemplate()),
            SelectedValuePath = nameof(FontChoice.Value),
        };
        TextSearch.SetTextPath(_textFontFamily, nameof(FontChoice.Label));
        VirtualizingPanel.SetIsVirtualizing(_textFontFamily, true);
        VirtualizingPanel.SetVirtualizationMode(_textFontFamily, VirtualizationMode.Recycling);
        AutomationName(_textFontFamily, UiText.Get("EditorText_FontFamily"));
        // Prepare the shared recommendations without blocking the first editor's construction.
        PrewarmSystemFontFamilies(UiText.Culture);
        _textFontFamily.DropDownOpened += (_, _) =>
            RefreshFontFamilies((_controller.Selected as TextAnnotation)?.FontFamily ?? _controller.DefaultFontFamily);
        _textFontFamily.SelectionChanged += (_, _) =>
        {
            if (_syncingInspector || _textFontFamily.SelectedItem is not FontChoice font) return;
            if (font.IsMore)
            {
                _showAllTextFonts = true;
                RefreshFontFamilies((_controller.Selected as TextAnnotation)?.FontFamily ?? _controller.DefaultFontFamily);
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
                {
                    if (_textFontFamily.IsVisible) _textFontFamily.IsDropDownOpen = true;
                }));
                return;
            }
            string family = font.Value;
            CommitActiveText();
            ApplyTextFontFamily(family);
            _recentFontFamilies.RemoveAll(name => string.Equals(name, family, StringComparison.OrdinalIgnoreCase));
            _recentFontFamilies.Insert(0, family);
            if (_recentFontFamilies.Count > 5) _recentFontFamilies.RemoveRange(5, _recentFontFamilies.Count - 5);
            RememberPreferences();
            UpdateInspector();
        };
        ReturnFocusAfterMousePick(_textFontFamily);
        panel.Children.Add(_textFontFamily);
        return panel;
    }

    private bool _showAllTextFonts;

    private void RefreshFontFamilies(string selected)
    {
        bool wasSyncing = _syncingInspector;
        _syncingInspector = true;
        try
        {
            _textFontFamily.ItemsSource = FontCatalog.ChoicesFor(selected, _showAllTextFonts
                ? FontCatalog.AvailableFonts(UiText.Culture) : FontCatalog.RecommendedFonts(UiText.Culture));
            _textFontFamily.SelectedValue = selected.Trim();
        }
        finally { _syncingInspector = wasSyncing; }
    }

    internal static IReadOnlyList<FontChoice> SortedSystemFontFamilies(CultureInfo culture) =>
        FontCatalog.InstalledFonts(culture);

    /// <summary>
    /// Prepares the shared cached recommendations before the first text selection.
    /// </summary>
    private static void PrewarmSystemFontFamilies(CultureInfo culture)
    {
        _ = Task.Run(() =>
        {
            try { FontCatalog.RecommendedFonts(culture); }
            catch (Exception ex)
            {
                // The UI thread builds the list itself when it is needed and reports any failure then.
                System.Diagnostics.Trace.TraceWarning("Font list prewarm failed: {0}", ex);
            }
        });
    }

    private void SelectFontFamily(string family)
    {
        FontChoice? match = _textFontFamily.Items.OfType<FontChoice>()
            .FirstOrDefault(font => !font.IsMore && string.Equals(font.Value, family, StringComparison.Ordinal));
        if (match is null)
        {
            RefreshFontFamilies(family);
            return;
        }
        _textFontFamily.SelectedItem = match;
    }

    private void ApplyTextFontFamily(string family)
    {
        if (_controller.Selected is not TextAnnotation { IsStepNumber: false } text)
        {
            _controller.DefaultFontFamily = family;
            return;
        }
        if (string.Equals(text.FontFamily, family, StringComparison.Ordinal)) return;
        var probe = (TextAnnotation)text.Clone();
        probe.FontFamily = family;
        var before = (text.FontFamily, TextLayout.Of(text));
        var after = (family, _controller.FitText(probe, probe.Text, probe.FontSize, probe.AutoSize, probe.Rect));
        _controller.Undo.Execute(new PropertyChangeCommand<TextAnnotation, (string Family, TextLayout Layout)>(
            text, UiText.Get("EditorText_FontFamily"), static (target, value) =>
            {
                target.FontFamily = value.Family;
                TextLayout.Apply(target, value.Layout);
            }, before, after));
        _surface.InvalidateVisual();
    }

    private void ApplyTextOutline(bool enabled)
    {
        if (_controller.Selected is not TextAnnotation { IsStepNumber: false } text)
        {
            _defaultTextOutline = enabled;
            return;
        }
        if (text.HasOutline == enabled) return;
        _controller.Undo.Execute(new PropertyChangeCommand<TextAnnotation, (bool Enabled, TextOutlineStyle Style)>(
            text, UiText.Get("EditorText_Outline"), static (target, value) =>
            {
                target.HasOutline = value.Enabled;
                target.OutlineStyle = value.Style;
            }, (text.HasOutline, text.OutlineStyle), (enabled, TextOutlineStyle.Consistent)));
        _surface.InvalidateVisual();
    }

    private static void AttachEditorShortcut(ButtonBase button, ShortcutScope scope, string command, bool corner = false)
        => ShortcutPresentation.Attach(button, scope, command, corner);

    private static Geometry FallbackClose() => Geometry.Parse("M5,5 L15,15 M15,5 L5,15");
}
