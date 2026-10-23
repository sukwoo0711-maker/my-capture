using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using Xunit;
using Xunit.Abstractions;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorInspectorLayoutBudgetTests(ITestOutputHelper output)
{
    private static readonly string[] Groups =
        ["color", "thickness", "shapeStyle", "fill", "fontFamily", "fontSize", "textStyle", "step", "blur", "mosaic", "crop"];

    [Theory]
    [InlineData("Delete")]
    [InlineData("Text")]
    public void SelectionChangingCommandsSynchronizeExactlyOnceBeforeReturning(string command) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = CreateHost(1200);
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        editor.ShortcutActions["Rectangle"]();
        controller.StrokeThickness = 3;
        controller.DefaultFontSize = 37;
        var shape = new RectangleAnnotation { Rect = new RectD(40, 40, 100, 80), StrokeThickness = 15 };
        controller.Document.Add(shape);
        controller.SetSelected(shape);
        Assert.Equal(15, Field<Slider>(editor, "_thicknessSlider").Value);
        Assert.True(Field<Button>(editor, "_deleteButton").IsEnabled);
        int synchronizations = editor.InspectorSynchronizationCount;

        editor.ShortcutActions[command]();

        // These checks precede a dispatcher/layout pass: the action must return current values.
        Assert.Equal(synchronizations + 1, editor.InspectorSynchronizationCount);
        Assert.Null(controller.Selected);
        Assert.False(Field<Button>(editor, "_deleteButton").IsEnabled);
        Assert.Equal(Visibility.Collapsed, Field<Button>(editor, "_deleteButton").Visibility);
        if (command == "Delete")
        {
            Assert.Empty(controller.Document.Items);
            Assert.Equal(1, controller.Undo.UndoCount);
            AssertGroups(editor, "color thickness shapeStyle fill");
            Assert.Equal(3, Field<Slider>(editor, "_thicknessSlider").Value);
        }
        else
        {
            Assert.Same(shape, Assert.Single(controller.Document.Items));
            Assert.Equal(0, controller.Undo.UndoCount);
            AssertGroups(editor, "color fontFamily fontSize textStyle");
            Assert.Equal(37, Field<Slider>(editor, "_fontSizeSlider").Value);
        }
    });

    [Theory]
    [InlineData(680)]
    [InlineData(1200)]
    public void ToolsAndSelectionsReuseTheMeasuredHeightBudget(int width) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = CreateHost(width);
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        var shape = new RectangleAnnotation { Rect = new RectD(40, 40, 100, 80), StrokeThickness = 17 };
        var text = new TextAnnotation { Rect = new RectD(180, 40, 150, 60), Text = "Synthetic caption", FontSize = 31 };
        controller.Document.Add(shape);
        controller.Document.Add(text);
        int measurements = editor.InspectorBudgetMeasureCount;
        double height = Field<Border>(editor, "_inspectorPanel").Height;
        Assert.True(measurements > 0);
        output.WriteLine($"{width} DIP window: inspector budget {height:0.##} DIP; uncapped desired height {Field<double>(editor, "_desiredInspectorHeight"):0.##} DIP.");

        foreach (EditorTool tool in Enum.GetValues<EditorTool>())
        {
            Invoke(editor, "SelectTool", tool);
            Settle(host);
            Assert.Equal(measurements, editor.InspectorBudgetMeasureCount);
            Assert.Equal(height, Field<Border>(editor, "_inspectorPanel").Height, 3);
        }
        editor.ShortcutActions["Select"]();
        foreach (AnnotationItem? selected in new AnnotationItem?[] { shape, text, shape, null })
        {
            controller.SetSelected(selected);
            if (selected == shape) Assert.Equal(17, Field<Slider>(editor, "_thicknessSlider").Value);
            if (selected == text) Assert.Equal(31, Field<Slider>(editor, "_fontSizeSlider").Value);
            Settle(host);
            Assert.Equal(measurements, editor.InspectorBudgetMeasureCount);
            Assert.Equal(height, Field<Border>(editor, "_inspectorPanel").Height, 3);
        }
    });

    [Fact]
    public void WidthDpiFontAndThemeChangesInvalidateTheBudgetAndMatchFreshTypography() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        using EditorWindowHost host = CreateHost(680);
        AnnotationEditorControl editor = host.Editor;
        int measurements = editor.InspectorBudgetMeasureCount;
        AssertRecomputed(() => host.Window.Width = 1200, "width");
        AssertRecomputed(() => SetDpiForLayout(host, 1.5), "DPI");
        SetDpiForLayout(host, 1.5);
        Settle(host);
        Assert.Equal(measurements, editor.InspectorBudgetMeasureCount);
        var font = new FontFamily("Consolas, Malgun Gothic");
        AssertRecomputed(() => host.Window.Resources["Font.Ui"] = font, "font resource");

        using (EditorWindowHost fresh = CreateHost(1200, font: font, dpi: 1.5))
        {
            Assert.Equal(Field<double>(fresh.Editor, "_desiredInspectorHeight"), Field<double>(editor, "_desiredInspectorHeight"), 3);
            Assert.Equal(Field<Border>(fresh.Editor, "_inspectorPanel").Height, Field<Border>(editor, "_inspectorPanel").Height, 3);
        }
        AssertRecomputed(() => ThemeService.ApplyResources(AppTheme.Daylight, host.Window.Resources, systemHighContrast: false), "theme");

        void AssertRecomputed(Action change, string reason)
        {
            change();
            Settle(host);
            Assert.True(editor.InspectorBudgetMeasureCount > measurements, $"The {reason} change did not remeasure the inspector.");
            measurements = editor.InspectorBudgetMeasureCount;
            Settle(host);
            Assert.Equal(measurements, editor.InspectorBudgetMeasureCount);
        }
    });

    [Theory]
    [InlineData(680, "Rectangle")]
    [InlineData(680, "Text")]
    [InlineData(680, "Select")]
    [InlineData(680, "Crop")]
    [InlineData(1200, "Rectangle")]
    [InlineData(1200, "Text")]
    [InlineData(1200, "Select")]
    [InlineData(1200, "Crop")]
    public void DividersSeparateOnlyVisibleNeighborsWithinEachWrappedRow(int width, string tool) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = CreateHost(width);
        host.Editor.ShortcutActions[tool]();
        Settle(host);
        var bar = Descendants(host.Editor).OfType<PropertyGroupsPanel>().Single();
        FrameworkElement[] visible = bar.Children.OfType<FrameworkElement>().Where(group => group.IsVisible && group.ActualWidth > 0).ToArray();
        var rows = visible.GroupBy(group => Math.Round(LayoutInformation.GetLayoutSlot(group).Top, 2)).ToArray();
        Assert.Equal(visible.Length - rows.Length, bar.DividerBounds.Count);
        foreach (Rect divider in bar.DividerBounds)
        {
            Assert.Equal(1, divider.Width);
            Assert.Equal(20, divider.Height);
        }
        foreach (var row in rows)
        {
            FrameworkElement[] neighbors = row.OrderBy(group => group.TranslatePoint(default, bar).X).ToArray();
            for (int index = 1; index < neighbors.Length; index++)
            {
                FrameworkElement previous = neighbors[index - 1], next = neighbors[index];
                Point previousPoint = previous.TranslatePoint(default, bar), nextPoint = next.TranslatePoint(default, bar);
                Rect divider = Assert.Single(bar.DividerBounds, rule =>
                    rule.Left >= previousPoint.X + previous.ActualWidth - 0.1
                    && rule.Right <= nextPoint.X + 0.1
                    && rule.Top >= nextPoint.Y - 0.1
                    && rule.Bottom <= nextPoint.Y + next.ActualHeight + 0.1);
                Assert.Equal(nextPoint.Y + next.ActualHeight / 2, divider.Top + divider.Height / 2, 3);
            }
        }
    });

    private static void AssertGroups(AnnotationEditorControl editor, string expected)
    {
        var active = expected.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        foreach (string group in Groups)
            Assert.Equal(active.Contains(group) ? Visibility.Visible : Visibility.Collapsed,
                Field<FrameworkElement>(editor, "_" + group + "Section").Visibility);
    }

    private static EditorWindowHost CreateHost(double width, FontFamily? font = null, double dpi = 1)
    {
        EditorWindowHost host = EditorWindowHost.Create();
        ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(AppTheme.Midnight, resources, systemHighContrast: false);
        if (font is not null) resources["Font.Ui"] = font;
        host.Window.Resources = resources;
        host.Window.Width = width;
        host.Window.Height = 800;
        SetDpiForLayout(host, dpi);
        Settle(host);
        return host;
    }

    private static void SetDpiForLayout(EditorWindowHost host, double scale)
    {
        VisualTreeHelper.SetRootDpi(host.Window, new DpiScale(scale, scale));
        Assert.Equal(scale, VisualTreeHelper.GetDpi(host.Editor).DpiScaleX, 3);
        Assert.Equal(scale, VisualTreeHelper.GetDpi(host.Editor).DpiScaleY, 3);
        // This synthetic setter updates DPI flags without a native window DPI/layout message.
        // Remeasure existing children as the shared menu DPI fixture does before checking cache reuse.
        foreach (UIElement element in Descendants(host.Window).Prepend(host.Window).OfType<UIElement>())
        {
            element.InvalidateMeasure();
            element.InvalidateArrange();
        }
    }

    private static void Settle(EditorWindowHost host) =>
        host.Window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(host.Window.UpdateLayout));
}
