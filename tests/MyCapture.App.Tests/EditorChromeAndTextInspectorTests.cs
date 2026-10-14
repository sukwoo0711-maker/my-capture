using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorChromeAndTextInspectorTests : KoreanCaptionTest
{
    [Fact]
    public void UnselectedTextSettingsPersistAndApplyToTheNextCaption() => StaTestHost.Run(() =>
    {
        using var preferences = new PreferencesScope(new AnnotationDefaults());
        string family;
        using (var host = EditorHost.Create())
        {
            SelectTextTool(host.Editor);
            Assert.Null(Controller(host.Editor).Selected);
            ComboBox fonts = Field<ComboBox>(host.Editor, "_textFontFamily");
            Assert.True(fonts.IsVisible);
            family = fonts.Items.Cast<FontFamily>().First(font => font.Source != "Malgun Gothic").Source;
            fonts.SelectedItem = fonts.Items.Cast<FontFamily>().Single(font => font.Source == family);
            Field<Slider>(host.Editor, "_fontSizeSlider").Value = 32;
            Toggle(host.Editor, "_textBold", true);
            Toggle(host.Editor, "_textBackground", true);
            Toggle(host.Editor, "_textOutline", false);
            Assert.Empty(Controller(host.Editor).Document.Items);
            Assert.False(Controller(host.Editor).CanUndo);
        }

        AnnotationDefaults persisted = preferences.Read();
        Assert.Equal(family, persisted.FontFamily);
        Assert.Equal(32, persisted.FontSize);
        Assert.True(persisted.TextBold);
        Assert.True(persisted.TextBackground);
        Assert.False(persisted.TextOutline);
        Assert.Contains(family, persisted.RecentFontFamilies);

        using var reopened = EditorHost.Create();
        SelectTextTool(reopened.Editor);
        Assert.Equal(family, Assert.IsType<FontFamily>(Field<ComboBox>(reopened.Editor, "_textFontFamily").SelectedItem).Source);
        Assert.Equal(32, Field<Slider>(reopened.Editor, "_fontSizeSlider").Value);
        Assert.True(Field<CheckBox>(reopened.Editor, "_textBold").IsChecked);
        Assert.True(Field<CheckBox>(reopened.Editor, "_textBackground").IsChecked);
        Assert.False(Field<CheckBox>(reopened.Editor, "_textOutline").IsChecked);

        reopened.Editor.SimulatePointerDown(reopened.Editor.ViewportPointOf(new PointD(60, 60)), 1, shift: false);
        TextBox input = Field<TextBox>(reopened.Editor, "_activeTextBox");
        Assert.True(Field<CheckBox>(reopened.Editor, "_textBold").IsChecked);
        Assert.True(Field<CheckBox>(reopened.Editor, "_textBackground").IsChecked);
        Assert.False(Field<CheckBox>(reopened.Editor, "_textOutline").IsChecked);
        input.Text = "새 텍스트";
        Invoke(reopened.Editor, "CommitActiveText");
        TextAnnotation caption = Assert.IsType<TextAnnotation>(Assert.Single(Controller(reopened.Editor).Document.Items));
        Assert.Equal(family, caption.FontFamily);
        Assert.Equal(32, caption.FontSize);
        Assert.True(caption.Bold);
        Assert.True(caption.Background.A > 0);
        Assert.False(caption.HasOutline);
        Assert.Equal(TextOutlineStyle.Consistent, caption.OutlineStyle);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FontPickerKeepsCurrentAndRecentFamiliesFirstAndPreviewsInstalledFonts(bool reusePreview) => StaTestHost.Run(() =>
    {
        string[] installed = Fonts.SystemFontFamilies.Select(font => font.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(installed.Length >= 3);
        using var preferences = new PreferencesScope(new AnnotationDefaults
        {
            FontFamily = installed[^1],
            RecentFontFamilies = [installed[1], installed[0], installed[1]],
        });
        using var host = EditorHost.Create();
        SelectTextTool(host.Editor);
        ComboBox picker = Field<ComboBox>(host.Editor, "_textFontFamily");
        FontFamily[] choices = picker.Items.Cast<FontFamily>().ToArray();
        Assert.Equal(installed[^1], choices[0].Source);
        Assert.Equal(installed[1], choices[1].Source);
        Assert.Equal(installed[0], choices[2].Source);
        Assert.Equal(choices.Length, choices.Select(font => font.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(installed, name => Assert.Contains(choices, font => font.Source.Equals(name, StringComparison.OrdinalIgnoreCase)));

        Assert.NotNull(picker.ItemTemplate);
        ContentPresenter? preview = null;
        foreach (FontFamily family in choices.Take(3))
        {
            // Apply the template through WPF so its factory is sealed before instantiation.
            if (preview is null || !reusePreview)
                preview = new ContentPresenter { ContentTemplate = picker.ItemTemplate };
            preview.Content = family;
            preview.Measure(new Size(240, 100));
            preview.Arrange(new Rect(preview.DesiredSize));
            TextBlock label = Assert.Single(Tree<TextBlock>(preview), text => text.Text.Contains(family.Source, StringComparison.Ordinal));
            Assert.Equal(family.Source, label.FontFamily.Source);
        }
    });

    [Fact]
    public void SelectedFontChangeRefitsAndOneUndoRestoresTheFamilyAndBounds() => StaTestHost.Run(() =>
    {
        using var preferences = new PreferencesScope(new AnnotationDefaults());
        using var host = EditorHost.Create();
        var controller = Controller(host.Editor);
        var caption = new TextAnnotation
        {
            Text = "Caption",
            FontFamily = "Malgun Gothic",
            Rect = new RectD(40, 40, 120, 40),
            AutoSize = true,
        };
        controller.Document.Add(caption);
        controller.SetSelected(caption);
        Assert.False(controller.CanUndo);
        ComboBox picker = Field<ComboBox>(host.Editor, "_textFontFamily");
        FontFamily next = picker.Items.Cast<FontFamily>().First(font => font.Source != caption.FontFamily);
        RectD original = caption.Rect;
        controller.TextFitter = text => new RectD(text.Rect.X, text.Rect.Y,
            text.FontFamily == next.Source ? 220 : 120, 40);
        int undoCount = controller.Undo.UndoCount;

        picker.SelectedItem = next;

        Assert.Equal(next.Source, caption.FontFamily);
        Assert.Equal(220, caption.Rect.Width);
        Assert.Equal(undoCount + 1, controller.Undo.UndoCount);
        Assert.True(controller.PerformUndo());
        Assert.Equal("Malgun Gothic", caption.FontFamily);
        Assert.Equal(original, caption.Rect);
        Assert.Equal("Malgun Gothic", Assert.IsType<FontFamily>(picker.SelectedItem).Source);
        Assert.True(controller.PerformRedo());
        Assert.Equal(next.Source, caption.FontFamily);
        Assert.Equal(220, caption.Rect.Width);
    });

    [Theory]
    [InlineData(1000, 640)]
    [InlineData(680, 460)]
    public void RailKeepsEveryHintAndHitTargetInsideItsBoundsWhenHintsAreEnabled(int width, int height) => StaTestHost.Run(() =>
    {
        using var preferences = new PreferencesScope(new AnnotationDefaults());
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        try
        {
            ShortcutPresentation.ReadEnabled = () => true;
            ShortcutPresentation.Refresh();
            using var host = EditorHost.Create(width, height);
            Border rail = Tree<Border>(host.Editor).Single(border => border.Name == "AnnotationToolRail");
            Dictionary<EditorTool, ToggleButton> tools = Field<Dictionary<EditorTool, ToggleButton>>(host.Editor, "_toolButtons");
            Assert.Equal(Enum.GetValues<EditorTool>().Length - 1, tools.Count);
            foreach (ToggleButton tool in tools.Values)
            {
                string key = AutomationProperties.GetAcceleratorKey(tool);
                Assert.False(string.IsNullOrWhiteSpace(key));
                Assert.Contains(key, Assert.IsType<string>(tool.ToolTip), StringComparison.Ordinal);
                Assert.True(tool.Focusable);
                Assert.True(tool.ActualWidth >= 36 && tool.ActualHeight >= 36);
                AssertInside(tool, rail);
                TextBlock hint = Assert.Single(Tree<TextBlock>(tool), text => text.Text == key);
                Assert.True(hint.IsVisible);
                AssertInside(hint, tool);
                Assert.Single(Tree<Viewbox>(tool));
                Assert.Null(tool.Template.FindName("Indicator", tool));
                Assert.NotNull(tool.Template.FindName("FocusRing", tool));
            }

            ToggleButton selected = Assert.Single(tools.Values, button => button.IsChecked == true);
            Border tile = Assert.IsType<Border>(selected.Template.FindName("CheckedLayer", selected));
            Assert.True(VisibleBrush(tile.Background));
            Assert.Equal(1, tile.Opacity);
            AssertInside(rail, host.Editor);
        }
        finally
        {
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    [Fact]
    public void RailAndCommandBarHintsFollowPreferenceChanges() => StaTestHost.Run(() =>
    {
        using var preferences = new PreferencesScope(new AnnotationDefaults());
        Func<bool>? previous = ShortcutPresentation.ReadEnabled;
        bool enabled = true;
        try
        {
            ShortcutPresentation.ReadEnabled = () => enabled;
            using var host = EditorHost.Create();
            var hints = new List<TextBlock>();
            foreach (string name in new[] { "AnnotationToolRail", "AnnotationCommandBar" })
            {
                Border container = Tree<Border>(host.Editor).Single(border => border.Name == name);
                int previousCount = hints.Count;
                foreach (ButtonBase button in Tree<ButtonBase>(container).Where(button => button.IsVisible))
                {
                    string key = AutomationProperties.GetAcceleratorKey(button);
                    if (string.IsNullOrEmpty(key)) continue;
                    ShortcutDefinition definition = ShortcutCatalog.All.First(entry =>
                        entry.Scope == ShortcutScope.Annotation && entry.Hint == key);
                    if (!ShortcutPresentation.ShowsCaption(definition)) continue;
                    hints.Add(Assert.Single(Tree<TextBlock>(button), text => text.Text == key));
                }
                Assert.True(hints.Count > previousCount, $"{name} has no captioned commands.");
            }
            Assert.True(hints.Count > Enum.GetValues<EditorTool>().Length);
            foreach (bool show in new[] { true, false, true })
            {
                enabled = show;
                ShortcutPresentation.Refresh();
                Assert.All(hints, hint => Assert.Equal(show ? Visibility.Visible : Visibility.Collapsed, hint.Visibility));
            }
        }
        finally
        {
            ShortcutPresentation.ReadEnabled = previous;
            ShortcutPresentation.Refresh();
        }
    });

    [Theory]
    [InlineData(1000, 640)]
    [InlineData(680, 460)]
    public void CommandsKeepButtonAffordancesIconsAndBoundKeysAtBothWidths(int width, int height) => StaTestHost.Run(() =>
    {
        using var preferences = new PreferencesScope(new AnnotationDefaults());
        using var host = EditorHost.Create(width, height);
        Border bar = Tree<Border>(host.Editor).Single(border => border.Name == "AnnotationCommandBar");
        Button[] commands = Tree<Button>(bar).Where(button => button.IsVisible).ToArray();
        Assert.True(commands.Length >= 10);
        foreach (Button command in commands)
        {
            AssertInside(command, bar);
            Assert.NotNull(command.ToolTip);
            Assert.NotEmpty(Tree<Viewbox>(command));
            Assert.Contains(Tree<Border>(command), border => border.IsVisible
                && (VisibleBrush(border.Background) || VisibleBrush(border.BorderBrush) && border.BorderThickness != new Thickness(0)));
            string key = AutomationProperties.GetAcceleratorKey(command);
            if (!string.IsNullOrEmpty(key))
            {
                TextBlock hint = Assert.Single(Tree<TextBlock>(command), text => text.Text == key);
                Assert.True(hint.IsVisible);
                AssertInside(hint, command);
            }

            if (width >= 1000)
            {
                Assert.Contains(Tree<TextBlock>(command), text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text) && text.Text != key);
            }
        }
    });

    [Fact]
    public void TextInspectorVisualEvidenceUsesOnlySyntheticContent() => StaTestHost.Run(() =>
    {
        using var preferences = new PreferencesScope(new AnnotationDefaults());
        var document = AnnotationDocument.CreateFor(640, 360);
        ColorRgba[] colors = [ColorRgba.FromRgb(0xEF, 0x44, 0x44), ColorRgba.FromRgb(0xFA, 0xCC, 0x15),
            ColorRgba.White, ColorRgba.FromRgb(0x12, 0x12, 0x12)];
        for (int index = 0; index < colors.Length; index++)
        {
            document.Add(new TextAnnotation
            {
                Text = "기록을 남기는 텍스트",
                FontSize = 30,
                Foreground = colors[index],
                Rect = new RectD(40, 35 + index * 75, 500, 55),
                HasOutline = true,
                OutlineStyle = TextOutlineStyle.Consistent,
            });
        }

        using var host = EditorHost.Create(document: document);
        Controller(host.Editor).SetSelected(document.Items[0]);
        host.Window.UpdateLayout();
        SaveEvidence(host.Editor, "text-inspector.png");
        host.Window.Width = 680;
        host.Window.Height = 460;
        host.Window.UpdateLayout();
        SaveEvidence(host.Editor, "text-inspector-compact.png");
    });

    private static void SelectTextTool(AnnotationEditorControl editor) =>
        Field<Dictionary<EditorTool, ToggleButton>>(editor, "_toolButtons")[EditorTool.Text]
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static void Toggle(AnnotationEditorControl editor, string field, bool value)
    {
        CheckBox control = Field<CheckBox>(editor, field);
        Assert.True(control.IsVisible);
        control.IsChecked = value;
        control.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static bool VisibleBrush(Brush? brush) => brush is SolidColorBrush { Color.A: > 0, Opacity: > 0 };

    private static void AssertInside(FrameworkElement element, FrameworkElement parent)
    {
        Point origin = element.TranslatePoint(new Point(), parent);
        Assert.True(origin.X >= -0.5 && origin.Y >= -0.5
            && origin.X + element.ActualWidth <= parent.ActualWidth + 0.5
            && origin.Y + element.ActualHeight <= parent.ActualHeight + 0.5,
            $"{element.GetType().Name} ({element.Name}) at {origin}, {element.ActualWidth}×{element.ActualHeight} exceeds {parent.Name}, {parent.ActualWidth}×{parent.ActualHeight}.");
    }

    private static IEnumerable<T> Tree<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        foreach (DependencyObject child in Descendants(root))
        {
            if (child is T item) yield return item;
        }
    }

    private static void SaveEvidence(FrameworkElement editor, string name)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MyCapture.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        string directory = Path.Combine(root.FullName, "artifacts", "editor-ui");
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(editor.ActualWidth), (int)Math.Ceiling(editor.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(editor);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(Path.Combine(directory, name));
        encoder.Save(output);
        Assert.True(output.Length > 0);
    }

    private sealed class PreferencesScope : IDisposable
    {
        private readonly Func<AnnotationDefaults>? _read = AnnotationEditorPreferences.Read;
        private readonly Action<AnnotationDefaults>? _write = AnnotationEditorPreferences.Write;
        private readonly Func<bool>? _sourceTitle = AnnotationEditorPreferences.ReadShowSourceWindowTitle;
        private string _json;

        internal PreferencesScope(AnnotationDefaults defaults)
        {
            _json = JsonSerializer.Serialize(defaults);
            AnnotationEditorPreferences.Read = Read;
            AnnotationEditorPreferences.Write = value => _json = JsonSerializer.Serialize(value);
            AnnotationEditorPreferences.ReadShowSourceWindowTitle = () => false;
        }

        internal AnnotationDefaults Read() => JsonSerializer.Deserialize<AnnotationDefaults>(_json)!;

        public void Dispose()
        {
            AnnotationEditorPreferences.Read = _read;
            AnnotationEditorPreferences.Write = _write;
            AnnotationEditorPreferences.ReadShowSourceWindowTitle = _sourceTitle;
        }
    }

    private sealed class EditorHost : IDisposable
    {
        private readonly bool? _animations;
        private EditorHost(Window window, AnnotationEditorControl editor, bool? animations)
        {
            Window = window;
            Editor = editor;
            _animations = animations;
        }
        internal Window Window { get; }
        internal AnnotationEditorControl Editor { get; }

        internal static EditorHost Create(int width = 1000, int height = 640, AnnotationDocument? document = null)
        {
            bool? animations = FluidMotion.AnimationsEnabledOverrideForTest;
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            BitmapSource bitmap = Solid(640, 360, 0x90);
            var region = new RectD(0, 0, 640, 360);
            var editor = new AnnotationEditorControl(new FrozenFrame(bitmap, region, null, 0), region, bitmap, document, null);
            ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
            ThemeService.ApplyResources(AppTheme.Midnight, resources, systemHighContrast: false);
            var window = new Window
            {
                Content = editor,
                Resources = resources,
                Width = width,
                Height = height,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Left = -4000,
                Top = -4000,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            window.Show();
            window.UpdateLayout();
            return new EditorHost(window, editor, animations);
        }

        public void Dispose()
        {
            Window.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = _animations;
        }
    }
}
