using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using MyCapture.Core.Annotations;
using MyCapture.Core.Undo;

namespace MyCapture.App.Editing;

internal sealed partial class AnnotationEditorControl
{
    private FrameworkElement BuildFontFamilySection()
    {
        var panel = PropertyGroup("AnnotationFontFamilyProperties");
        panel.Children.Add(PropertyLabel(UiText.Get("EditorText_FontFamily")));
        var preview = new FrameworkElementFactory(typeof(TextBlock));
        preview.SetBinding(TextBlock.TextProperty, new Binding(nameof(FontFamily.Source)));
        preview.SetBinding(TextBlock.FontFamilyProperty, new Binding());
        preview.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        _textFontFamily = new ComboBox
        {
            MinHeight = 32,
            Width = 160,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            MaxDropDownHeight = 320,
            IsTextSearchEnabled = true,
            ItemTemplate = new DataTemplate { VisualTree = preview },
        };
        TextSearch.SetTextPath(_textFontFamily, nameof(FontFamily.Source));
        VirtualizingPanel.SetIsVirtualizing(_textFontFamily, true);
        VirtualizingPanel.SetVirtualizationMode(_textFontFamily, VirtualizationMode.Recycling);
        AutomationName(_textFontFamily, UiText.Get("EditorText_FontFamily"));
        // The list is filled the first time the section is used: the inspector selects the
        // caption's family as soon as text styling applies (an empty list misses, which fills
        // it), and opening the list refreshes it. Until then the section is collapsed. The
        // installed families are sorted once per process, starting off the UI thread here.
        PrewarmSystemFontFamilies(CultureInfo.CurrentCulture);
        _textFontFamily.DropDownOpened += (_, _) =>
            RefreshFontFamilies((_controller.Selected as TextAnnotation)?.FontFamily ?? _controller.DefaultFontFamily);
        _textFontFamily.SelectionChanged += (_, _) =>
        {
            if (_syncingInspector || _textFontFamily.SelectedItem is not FontFamily font) return;
            string family = font.Source;
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

    private void RefreshFontFamilies(string selected)
    {
        bool wasSyncing = _syncingInspector;
        _syncingInspector = true;
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var families = new List<FontFamily>();
            void Add(string name)
            {
                if (!string.IsNullOrWhiteSpace(name) && names.Add(name)) families.Add(new FontFamily(name));
            }
            Add(_controller.DefaultFontFamily);
            foreach (string recent in _recentFontFamilies.Take(5)) Add(recent);
            foreach (FontFamily font in SortedSystemFontFamilies(CultureInfo.CurrentCulture))
            {
                // The cached families were made from these names, as this list made them.
                if (!string.IsNullOrWhiteSpace(font.Source) && names.Add(font.Source)) families.Add(font);
            }
            Add(selected);
            _textFontFamily.ItemsSource = families;
            SelectFontFamily(selected);
        }
        finally { _syncingInspector = wasSyncing; }
    }

    private static readonly object SystemFontFamiliesGate = new();
    private static CultureInfo? s_systemFontFamiliesCulture;
    private static FontFamily[]? s_systemFontFamilies;

    /// <summary>
    /// The installed font families sorted by name as <paramref name="culture"/> compares them
    /// ignoring case, each made from its name, built once per process and culture.
    /// </summary>
    /// <remarks>
    /// Every editor used to enumerate, sort and recreate the whole system list as it opened.
    /// WPF reads <see cref="Fonts.SystemFontFamilies"/> once per process, so that list never
    /// changed within a session and the cached copy holds the same names in the same order.
    /// <see cref="FontFamily"/> is not tied to a dispatcher, so the copy can be built on any
    /// thread and shared by every editor.
    /// </remarks>
    internal static IReadOnlyList<FontFamily> SortedSystemFontFamilies(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        lock (SystemFontFamiliesGate)
        {
            if (s_systemFontFamilies is { } cached && Equals(s_systemFontFamiliesCulture, culture)) return cached;
            StringComparer comparer = StringComparer.Create(culture, ignoreCase: true);
            FontFamily[] families = Fonts.SystemFontFamilies.OrderBy(font => font.Source, comparer)
                .Select(font => new FontFamily(font.Source)).ToArray();
            s_systemFontFamiliesCulture = culture;
            s_systemFontFamilies = families;
            return families;
        }
    }

    /// <summary>
    /// Starts building <see cref="SortedSystemFontFamilies"/> on a worker thread when it is not
    /// cached yet, so the first editor that needs the list rarely waits for it.
    /// </summary>
    private static void PrewarmSystemFontFamilies(CultureInfo culture)
    {
        lock (SystemFontFamiliesGate)
        {
            if (s_systemFontFamilies is not null && Equals(s_systemFontFamiliesCulture, culture)) return;
        }

        _ = Task.Run(() =>
        {
            try { SortedSystemFontFamilies(culture); }
            catch (Exception ex)
            {
                // The UI thread builds the list itself when it is needed and reports any failure then.
                System.Diagnostics.Trace.TraceWarning("Font list prewarm failed: {0}", ex);
            }
        });
    }

    private void SelectFontFamily(string family)
    {
        FontFamily? match = _textFontFamily.Items.OfType<FontFamily>()
            .FirstOrDefault(font => string.Equals(font.Source, family, StringComparison.OrdinalIgnoreCase));
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
    {
        if (!corner)
        {
            ShortcutPresentation.Attach(button, scope, command);
            return;
        }
        UIElement content = (UIElement)button.Content;
        button.Content = null;
        var hint = ShortcutPresentation.Hint(scope, command);
        hint.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = button });
        var grid = new Grid { Width = 32, Height = 32 };
        if (content is FrameworkElement glyph)
        {
            glyph.HorizontalAlignment = HorizontalAlignment.Left;
            glyph.VerticalAlignment = VerticalAlignment.Top;
        }
        hint.HorizontalAlignment = HorizontalAlignment.Right;
        hint.VerticalAlignment = VerticalAlignment.Bottom;
        hint.Height = 12;
        hint.LineHeight = 12;
        hint.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        grid.Children.Add(content);
        grid.Children.Add(hint);
        button.Content = grid;
        ShortcutPresentation.Describe(button, scope, command);
    }

    private static Geometry FallbackClose() => Geometry.Parse("M5,5 L15,15 M15,5 L5,15");
}
