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
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(SectionLabel(UiText.Get("EditorText_FontFamily")));
        var preview = new FrameworkElementFactory(typeof(TextBlock));
        preview.SetBinding(TextBlock.TextProperty, new Binding(nameof(FontFamily.Source)));
        preview.SetBinding(TextBlock.FontFamilyProperty, new Binding());
        preview.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        _textFontFamily = new ComboBox
        {
            MinHeight = 32,
            MaxDropDownHeight = 320,
            IsTextSearchEnabled = true,
            ItemTemplate = new DataTemplate { VisualTree = preview },
        };
        TextSearch.SetTextPath(_textFontFamily, nameof(FontFamily.Source));
        VirtualizingPanel.SetIsVirtualizing(_textFontFamily, true);
        VirtualizingPanel.SetVirtualizationMode(_textFontFamily, VirtualizationMode.Recycling);
        AutomationName(_textFontFamily, UiText.Get("EditorText_FontFamily"));
        RefreshFontFamilies(_controller.DefaultFontFamily);
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
            foreach (FontFamily font in Fonts.SystemFontFamilies.OrderBy(font => font.Source, StringComparer.CurrentCultureIgnoreCase))
                Add(font.Source);
            Add(selected);
            _textFontFamily.ItemsSource = families;
            SelectFontFamily(selected);
        }
        finally { _syncingInspector = wasSyncing; }
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
        UIElement content = (UIElement)button.Content;
        button.Content = null;
        var hint = ShortcutPresentation.Hint(scope, command);
        hint.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = button });
        if (corner)
        {
            var grid = new Grid { Width = 32, Height = 32 };
            if (content is FrameworkElement glyph)
            {
                glyph.HorizontalAlignment = HorizontalAlignment.Left;
                glyph.VerticalAlignment = VerticalAlignment.Top;
            }
            hint.HorizontalAlignment = HorizontalAlignment.Right;
            hint.VerticalAlignment = VerticalAlignment.Bottom;
            hint.LineHeight = 12;
            hint.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            grid.Children.Add(content);
            grid.Children.Add(hint);
            button.Content = grid;
        }
        else
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(content);
            stack.Children.Add(hint);
            button.Content = stack;
        }
        ShortcutPresentation.Describe(button, scope, command);
    }

    private static Geometry FallbackClose() => Geometry.Parse("M5,5 L15,15 M15,5 L5,15");
}
