using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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

public sealed class EditorLazyInspectorTests
{
    [Fact]
    public Task ActualPrewarmSuppliesTheThemedEditorWindowsFirstLayout() => SettingsThemePreviewTests.RunIsolated(
        nameof(ActualPrewarmSuppliesTheThemedEditorWindowsFirstLayout), () =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
                });
            ThemeService.ApplyResources(AppTheme.Midnight, application.Resources, systemHighContrast: false);
            WithSelectDefault(() =>
            {
                AnnotationEditorPrewarm.Run();
                var bitmap = Solid(640, 360, 0x30);
                var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
                var window = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), region, bitmap);
                try
                {
                    AnnotationEditorControl editor = window.Editor;
                    Assert.Same(application.FindResource("Window.Standard"), window.Style);
                    Assert.Equal((double)application.FindResource("FontSize.Body"), TextElement.GetFontSize(editor));
                    Assert.Equal(ThemeFonts.Formatting, TextOptions.GetTextFormattingMode(editor));
                    Assert.True(editor.UseLayoutRounding);
                    Assert.True(editor.SnapsToDevicePixels);
                    // The real window supplies inheritance/resources before native Show. Layout
                    // here uses the prewarm's default DPI, independent of monitor placement.
                    Layout(editor);
                    AssertDeferred(editor);
                    Assert.Equal(1, editor.InspectorBudgetCacheHitCount);

                    int measurements = editor.InspectorBudgetMeasureCount;
                    // Another formatting mode measures the inspector again.
                    TextOptions.SetTextFormattingMode(editor, TextFormattingMode.Display);
                    Layout(editor);
                    Assert.Equal(measurements + 1, editor.InspectorBudgetMeasureCount);
                    Assert.Equal(12, editor.InspectorCreatedGroupCount);
                }
                finally { window.Close(); }
            });
            application.Shutdown();
        }, typeof(EditorLazyInspectorTests));

    [Fact]
    public void CacheEvictionDoesNotAccessResourceKeysFromAStoppedDispatcher()
    {
        // Each Run shuts its STA dispatcher down before returning. The old process-wide
        // dictionary failed on the second thread when eviction hashed the first thread's Style.
        for (int thread = 0; thread < 2; thread++)
        {
            StaTestHost.Run(() => WithSelectDefault(() =>
            {
                AnnotationEditorControl editor = Create();
                Style? lastStyle = null;
                for (int context = 0; context < 34; context++)
                {
                    lastStyle = new Style(typeof(TextBlock));
                    editor.Resources["Inspector.SectionLabel"] = lastStyle;
                    editor.InvalidateMeasure();
                    Layout(editor);
                    Assert.Equal(context + 1, editor.InspectorBudgetMeasureCount);
                    Assert.Equal(0, editor.InspectorBudgetCacheHitCount);
                }

                // Eviction must keep recent entries usable by another editor on the same STA.
                AnnotationEditorControl warm = Create(lastStyle);
                Layout(warm);
                AssertDeferred(warm);
                Assert.Equal(1, warm.InspectorBudgetCacheHitCount);
                Assert.Equal(Field<double>(editor, "_desiredInspectorHeight"), Field<double>(warm, "_desiredInspectorHeight"));
            }));
        }
    }

    [Fact]
    public void ConstructionAndCachedLayoutDoNotBuildUnusedPropertyControls() => StaTestHost.Run(() =>
    {
        WithSelectDefault(() =>
        {
            // A unique resource identity makes the cold measurement deterministic without
            // resetting process caches or relying on which other test ran first.
            var labelStyle = new Style(typeof(TextBlock));
            AnnotationEditorControl first = Create(labelStyle);
            AssertDeferred(first);
            Layout(first);
            Assert.Equal(12, first.InspectorCreatedGroupCount);
            Assert.Equal(0, first.InspectorBudgetCacheHitCount);

            AnnotationEditorControl second = Create(labelStyle);
            AssertDeferred(second);
            Layout(second);
            AssertDeferred(second);
            Assert.Equal(1, second.InspectorBudgetCacheHitCount);
            Assert.Equal(Field<double>(first, "_desiredInspectorHeight"), Field<double>(second, "_desiredInspectorHeight"));

            AnnotationEditorControl wider = Create(labelStyle);
            wider.Measure(new Size(1200, 640));
            wider.Arrange(new Rect(0, 0, 1200, 640));
            AssertDeferred(wider);
            Assert.Equal(1, wider.InspectorBudgetCacheHitCount);

            second.ShortcutActions["Text"]();
            Assert.Equal(4, second.InspectorCreatedGroupCount);
            DataTemplate preview = Field<ComboBox>(second, "_textFontFamily").ItemTemplate;
            Assert.NotNull(preview);
            Assert.Same(Field<ComboBox>(first, "_textFontFamily").ItemTemplate, preview);
            Geometry firstOutline = Outline(first), secondOutline = Outline(second);
            Assert.True(firstOutline.IsFrozen);
            Assert.Same(firstOutline, secondOutline);
            second.ShortcutActions["Select"]();
            second.ShortcutActions["Text"]();
            Assert.Equal(4, second.InspectorCreatedGroupCount);
            Assert.Same(preview, Field<ComboBox>(second, "_textFontFamily").ItemTemplate);
        });
    });

    [Theory]
    [InlineData("Text", "color fontFamily fontSize textStyle", 4)]
    [InlineData("Crop", "crop", 1)]
    [InlineData("Blur", "blur", 1)]
    [InlineData("Mosaic", "mosaic", 1)]
    [InlineData("StepNumber", "color step", 2)]
    [InlineData("Rectangle", "color thickness shapeStyle fill", 4)]
    [InlineData("Arrow", "color thickness arrowHead", 3)]
    public void FirstToolUseBuildsOnlyItsGroupsAndKeepsFocusAndAutomation(string tool, string groups, int count) =>
        StaTestHost.Run(() => WithSelectDefault(() =>
        {
            using EditorWindowHost seed = EditorWindowHost.Create();
            using EditorWindowHost host = EditorWindowHost.Create();
            AnnotationEditorControl editor = host.Editor;
            AssertDeferred(editor);
            Assert.True(editor.InspectorBudgetCacheHitCount > 0);
            double budget = Field<Border>(editor, "_inspectorPanel").Height;
            editor.ShortcutActions[tool]();
            host.Window.UpdateLayout();
            Assert.Equal(count, editor.InspectorCreatedGroupCount);
            AssertGroups(editor, groups);
            Assert.Equal(budget, Field<Border>(editor, "_inspectorPanel").Height);

            foreach (string group in groups.Split(' '))
            {
                FrameworkElement section = Field<FrameworkElement>(editor, "_" + group + "Section");
                Control[] controls = Descendants(section).OfType<Control>()
                    .Where(control => control.IsVisible && control.IsEnabled && control.Focusable && control.IsTabStop
                        && control is TextBox or ComboBox or ToggleButton or Slider).ToArray();
                foreach (Control control in controls)
                {
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)));
                    control.BringIntoView();
                    host.Window.UpdateLayout();
                    Assert.True(control.Focus());
                    Assert.Same(control, Keyboard.FocusedElement);
                    if (control is not TextBox) Assert.False(InputMethod.GetIsInputMethodEnabled(control));
                }
            }
            if (tool == "Crop") Assert.Equal("640", Field<TextBox>(editor, "_cropWidth").Text);
        }));

    [Theory]
    [InlineData("text", "color fontFamily fontSize textStyle", 4)]
    [InlineData("number", "color fontSize", 2)]
    [InlineData("shape", "color thickness shapeStyle fill", 4)]
    [InlineData("pen", "color thickness", 2)]
    [InlineData("line", "color thickness arrowHead", 3)]
    [InlineData("blur", "blur", 1)]
    [InlineData("image", "", 0)]
    public void FirstSelectionBuildsOnlyApplicableGroups(string kind, string groups, int count) =>
        StaTestHost.Run(() => WithSelectDefault(() =>
        {
            AnnotationEditorControl editor = Create();
            AnnotationEditorController controller = Controller(editor);
            AnnotationItem item = kind switch
            {
                "text" => new TextAnnotation { Text = "Caption", FontSize = 29, Rect = new RectD(30, 30, 120, 40) },
                "number" => new TextAnnotation { Text = "1", IsStepNumber = true, FontSize = 29, Rect = new RectD(30, 30, 40, 40) },
                "shape" => new RectangleAnnotation { StrokeThickness = 17, Rect = new RectD(30, 30, 120, 40) },
                "pen" => new PenAnnotation(),
                "line" => new PolylineAnnotation(),
                "blur" => new BlurAnnotation { Radius = 23, Rect = new RectD(30, 30, 120, 40) },
                _ => new ImageAnnotation { Rect = new RectD(30, 30, 120, 40) },
            };
            controller.Document.Add(item);
            controller.SetSelected(item);
            Assert.Equal(count, editor.InspectorCreatedGroupCount);
            AssertGroups(editor, groups);
            Assert.Equal(0, controller.Undo.UndoCount);
            Assert.True(Field<Button>(editor, "_deleteButton").IsEnabled);
            if (item is TextAnnotation) Assert.Equal(29, Field<Slider>(editor, "_fontSizeSlider").Value);
            if (item is ShapeAnnotation) Assert.Equal(17, Field<Slider>(editor, "_thicknessSlider").Value);
            if (item is BlurAnnotation) Assert.Equal(23, Field<Slider>(editor, "_blurSlider").Value);
        }));

    private static void AssertDeferred(AnnotationEditorControl editor)
    {
        Assert.Equal(0, editor.InspectorCreatedGroupCount);
        Assert.Null(Field<ComboBox?>(editor, "_textFontFamily"));
        Assert.Null(Field<ToggleButton?>(editor, "_textOutline"));
        Assert.Null(Field<Slider?>(editor, "_blurSlider"));
        Assert.Null(Field<Slider?>(editor, "_mosaicSlider"));
        Assert.Null(Field<Border?>(editor, "_stepPreview"));
    }

    private static void AssertGroups(AnnotationEditorControl editor, string groups)
    {
        var expected = groups.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        foreach (string group in new[] { "color", "thickness", "shapeStyle", "arrowHead", "fill", "step", "fontFamily", "fontSize", "textStyle", "blur", "mosaic", "crop" })
            Assert.Equal(expected.Contains(group) ? Visibility.Visible : Visibility.Collapsed,
                Field<FrameworkElement>(editor, "_" + group + "Section").Visibility);
    }

    private static Geometry Outline(AnnotationEditorControl editor) =>
        ((Grid)Field<ToggleButton>(editor, "_textOutline").Content).Children
            .OfType<System.Windows.Shapes.Path>().Single().Data;

    private static AnnotationEditorControl Create(Style? labelStyle = null)
    {
        var bitmap = Solid(640, 360, 0x30);
        var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        var editor = new AnnotationEditorControl(new FrozenFrame(bitmap, region, null, 0), region, bitmap);
        if (labelStyle is not null) editor.Resources["Inspector.SectionLabel"] = labelStyle;
        return editor;
    }

    private static void Layout(AnnotationEditorControl editor)
    {
        editor.Measure(new Size(1000, 640));
        editor.Arrange(new Rect(0, 0, 1000, 640));
    }

    private static void WithSelectDefault(Action action)
    {
        Func<AnnotationDefaults>? read = AnnotationEditorPreferences.Read;
        Action<AnnotationDefaults>? write = AnnotationEditorPreferences.Write;
        Func<bool>? sourceTitle = AnnotationEditorPreferences.ReadShowSourceWindowTitle;
        AnnotationEditorPreferences.Read = () => new AnnotationDefaults { LastTool = "Select" };
        AnnotationEditorPreferences.Write = _ => { };
        AnnotationEditorPreferences.ReadShowSourceWindowTitle = () => false;
        try { action(); }
        finally
        {
            AnnotationEditorPreferences.Read = read;
            AnnotationEditorPreferences.Write = write;
            AnnotationEditorPreferences.ReadShowSourceWindowTitle = sourceTitle;
        }
    }
}
