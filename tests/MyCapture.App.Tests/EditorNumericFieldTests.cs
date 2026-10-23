using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorNumericFieldTests : KoreanCaptionTest
{
    [Fact]
    public void ThicknessIsOneBorderedFieldWithFullSizeButtonsAndOneTabStop() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        Controller(host.Editor).Tool = EditorTool.Rectangle;
        Invoke(host.Editor, "UpdateInspector");
        host.Window.UpdateLayout();
        TextBox box = ThicknessBox(host.Editor);
        Border field = ThicknessField(host.Editor);
        var content = Assert.IsType<StackPanel>(field.Child);
        Button[] buttons = content.Children.OfType<Button>().ToArray();
        Assert.Equal(2, buttons.Length);
        Assert.Same(box, Assert.Single(content.Children.OfType<TextBox>()));
        Assert.Equal("px", Assert.Single(content.Children.OfType<TextBlock>()).Text);
        Assert.Equal(new Thickness(1), field.BorderThickness);
        Assert.Equal(32, field.ActualHeight);
        Assert.Equal(new Thickness(0), box.BorderThickness);
        Assert.True(box.IsTabStop && box.Focusable);
        Assert.All(buttons, button =>
        {
            Assert.False(button.Focusable);
            Assert.False(button.IsTabStop);
            Assert.Equal(32, button.ActualWidth);
            Assert.Equal(32, button.ActualHeight);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
            Assert.False(string.IsNullOrWhiteSpace(button.ToolTip as string));
        });
        Border[] dividers = content.Children.OfType<Border>().ToArray();
        Assert.Equal(2, dividers.Length);
        Assert.All(dividers, divider => { Assert.Equal(1, divider.ActualWidth); Assert.Equal(20, divider.ActualHeight); });
        var peer = new TextBoxAutomationPeer(box);
        Assert.Equal(UiText.Get("Text_70EC2D232B4F"), peer.GetName());
        Assert.IsAssignableFrom<IValueProvider>(peer.GetPattern(PatternInterface.Value));
    });

    [Theory]
    [InlineData(Key.Up, 13)]
    [InlineData(Key.Down, 11)]
    [InlineData(Key.PageUp, 17)]
    [InlineData(Key.PageDown, 7)]
    [InlineData(Key.Home, 1)]
    [InlineData(Key.End, 64)]
    public void NavigationKeysApplyThePendingValueAsExactlyOneMultiSelectionUndo(Key key, double expected) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = SelectTwoShapes(host.Editor);
        string before = controller.Document.ToJson();
        TextBox box = ThicknessBox(host.Editor);
        Assert.True(box.Focus());
        box.Text = "12";
        Assert.True(Press(box, key, ime: false));
        Assert.All(controller.Document.Items.OfType<ShapeAnnotation>(), shape => Assert.Equal(expected, shape.StrokeThickness));
        Assert.Equal(1, controller.Undo.UndoCount);
        Assert.Equal(SliderValueEntry.Format(expected), box.Text);
        Assert.Same(box, Keyboard.FocusedElement);
        host.Editor.Focus();
        Assert.Equal(1, controller.Undo.UndoCount);
        Assert.True(controller.PerformUndo());
        Assert.Equal(before, controller.Document.ToJson());
    });

    [Theory]
    [InlineData("AnnotationThicknessDecrease", 11)]
    [InlineData("AnnotationThicknessIncrease", 13)]
    public void ButtonStepPreservesInputFocusAndAppliesPendingTextOnce(string id, double expected) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = SelectTwoShapes(host.Editor);
        TextBox box = ThicknessBox(host.Editor);
        Assert.True(box.Focus());
        box.Text = "12";
        Button button = Descendants(ThicknessField(host.Editor)).OfType<Button>()
            .Single(candidate => AutomationProperties.GetAutomationId(candidate) == id);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Same(box, Keyboard.FocusedElement);
        Assert.All(controller.Document.Items.OfType<ShapeAnnotation>(), shape => Assert.Equal(expected, shape.StrokeThickness));
        Assert.Equal(1, controller.Undo.UndoCount);
        host.Editor.Focus();
        Assert.Equal(1, controller.Undo.UndoCount);
    });

    [Theory]
    [InlineData(1, "AnnotationThicknessDecrease", 11)]
    [InlineData(64, "AnnotationThicknessIncrease", 13)]
    public void PendingValueEnablesTheStepButtonEvenWhenCommittedValueIsAtItsLimit(
        double committed, string id, double expected) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = SelectTwoShapes(host.Editor);
        foreach (ShapeAnnotation shape in controller.Document.Items.OfType<ShapeAnnotation>())
            shape.StrokeThickness = committed;
        Invoke(host.Editor, "UpdateInspector");
        string before = controller.Document.ToJson();
        Slider slider = Field<Slider>(host.Editor, "_thicknessSlider");
        TextBox box = ThicknessBox(host.Editor);
        Button button = Descendants(ThicknessField(host.Editor)).OfType<Button>()
            .Single(candidate => AutomationProperties.GetAutomationId(candidate) == id);
        Assert.Equal(committed, slider.Value);
        Assert.False(button.IsEnabled);
        int valueChanges = 0;
        slider.ValueChanged += (_, _) => valueChanges++;

        Assert.True(box.Focus());
        box.Text = "12";
        Assert.True(button.IsEnabled);
        Assert.Equal(committed, slider.Value);
        Assert.Equal(0, valueChanges);
        Assert.Equal(0, controller.Undo.UndoCount);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal(expected, slider.Value);
        Assert.Equal(SliderValueEntry.Format(expected), box.Text);
        Assert.Same(box, Keyboard.FocusedElement);
        Assert.All(controller.Document.Items.OfType<ShapeAnnotation>(), shape => Assert.Equal(expected, shape.StrokeThickness));
        Assert.Equal(1, valueChanges);
        Assert.Equal(1, controller.Undo.UndoCount);
        host.Editor.Focus();
        Assert.Equal(1, valueChanges);
        Assert.Equal(1, controller.Undo.UndoCount);
        Assert.True(controller.PerformUndo());
        Assert.Equal(before, controller.Document.ToJson());
    });

    [Fact]
    public void WheelChangesValuesOnlyWhileTheValueBoxHasKeyboardFocus() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = SelectTwoShapes(host.Editor);
        TextBox box = ThicknessBox(host.Editor);
        host.Editor.Focus();
        RaiseWheel(box, 120);
        Assert.Equal("3", box.Text);
        Assert.Equal(0, controller.Undo.UndoCount);
        Assert.True(box.Focus());
        box.Text = "12";
        Assert.True(RaiseWheel(box, 120).Handled);
        Assert.Equal("13", box.Text);
        Assert.Equal(1, controller.Undo.UndoCount);
        // Undo/redo verifies the combined pending-text/step operation and closes the normal
        // 600-ms style merge window before the next distinct operation.
        Assert.True(controller.PerformUndo());
        Assert.All(controller.Document.Items.OfType<ShapeAnnotation>(), shape => Assert.Equal(3, shape.StrokeThickness));
        Assert.True(controller.PerformRedo());
        Assert.Equal("13", box.Text);
        Assert.True(RaiseWheel(box, -120).Handled);
        Assert.Equal("12", box.Text);
        Assert.Equal(2, controller.Undo.UndoCount);
        host.Editor.Focus();
        RaiseWheel(box, -120);
        Assert.Equal("12", box.Text);
        Assert.Equal(2, controller.Undo.UndoCount);
    });

    [Theory]
    [InlineData(AppTheme.Midnight)]
    [InlineData(AppTheme.Daylight)]
    [InlineData(AppTheme.Glass)]
    [InlineData(AppTheme.GlassLight)]
    public void NumericBoundaryKeepsThreeToOneContrastAndFollowsLivePaletteChanges(AppTheme theme) => StaTestHost.Run(() =>
    {
        var entry = new SliderValueEntry(new Slider { Minimum = 1, Maximum = 64, Value = 3 }, "px", "Thickness");
        Border field = Assert.IsAssignableFrom<Border>(entry.Element);
        foreach (AppTheme current in new[] { theme, AppTheme.GlassLight, theme })
        {
            IReadOnlyDictionary<string, ThemeColor> colors = ThemeCatalog.ColorsFor(current);
            foreach ((string key, ThemeColor value) in colors)
                field.Resources[key] = new SolidColorBrush(Color.FromArgb(value.A, value.R, value.G, value.B));
            field.Measure(new Size(400, 32));
            field.Arrange(new Rect(field.DesiredSize));
            var actual = Assert.IsType<SolidColorBrush>(field.BorderBrush);
            ThemeColor boundary = ThemeColor.Argb(actual.Color.A, actual.Color.R, actual.Color.G, actual.Color.B);
            foreach (ThemeColor backdrop in new[] { ThemeColor.Rgb(0, 0, 0), ThemeColor.Rgb(255, 255, 255) })
            {
                ThemeColor raised = ThemeContrast.Composite(colors["Surface.Raised"], ThemeContrast.Composite(colors["Surface.Base"], backdrop));
                ThemeColor sunken = ThemeContrast.Composite(colors["Surface.Sunken"], raised);
                foreach (ThemeColor adjacent in new[] { raised, sunken })
                {
                    double contrast = ThemeContrast.Ratio(ThemeContrast.Composite(boundary, adjacent), adjacent);
                    Assert.True(contrast >= 3, $"{current}: numeric boundary contrast {contrast:F3}:1.");
                }
            }
            Assert.Same(field.Resources[current is AppTheme.Daylight or AppTheme.GlassLight ? "Border.Strong" : "Border.Control"], actual);
            NumberStepperField.SetValueFieldError(field, invalid: true);
            Assert.Same(field.Resources["State.Danger"], field.BorderBrush);
            NumberStepperField.SetValueFieldError(field, invalid: false);
            Assert.Same(actual, field.BorderBrush);
        }
    });

    [Fact]
    public void ButtonsFollowChangingRangeAndFocusHighlightsTheWholeFieldWithoutResizing() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        SelectTwoShapes(host.Editor);
        Slider slider = Field<Slider>(host.Editor, "_thicknessSlider");
        Border field = ThicknessField(host.Editor);
        TextBox box = ThicknessBox(host.Editor);
        Button[] buttons = ((Panel)field.Child).Children.OfType<Button>().ToArray();
        slider.Value = slider.Minimum;
        Assert.False(buttons[0].IsEnabled);
        Assert.True(buttons[1].IsEnabled);
        slider.Value = slider.Maximum;
        Assert.True(buttons[0].IsEnabled);
        Assert.False(buttons[1].IsEnabled);
        slider.Value = 12;
        slider.Minimum = 12;
        Assert.False(buttons[0].IsEnabled);
        slider.Minimum = 1;
        Assert.True(buttons[0].IsEnabled);

        var normal = new SolidColorBrush(Colors.Gray);
        var focused = new SolidColorBrush(Colors.White);
        host.Editor.Resources["Border.Control"] = normal;
        host.Editor.Resources["Border.Focus"] = focused;
        host.Editor.Focus();
        host.Window.UpdateLayout();
        Size before = field.RenderSize;
        Assert.Same(normal, field.BorderBrush);
        Assert.True(box.Focus());
        host.Window.UpdateLayout();
        Assert.Same(focused, field.BorderBrush);
        Assert.Equal(before, field.RenderSize);
        host.Editor.Focus();
        Assert.Same(normal, field.BorderBrush);
    });

    [Fact]
    public void CustomColorButtonUsesAnEighteenDipDashedSwatchInsideThePaletteRingArea() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        Button button = host.Editor.CustomColorButtonForTest;
        Grid glyph = Assert.IsType<Grid>(button.Content);
        Ellipse circle = Assert.Single(glyph.Children.OfType<Ellipse>());
        Assert.Equal(32, button.Width);
        Assert.Equal(32, button.Height);
        Assert.Equal(26, glyph.Width);
        Assert.Equal(26, glyph.Height);
        Assert.Equal(18, circle.Width);
        Assert.Equal(18, circle.Height);
        Assert.Equal(new double[] { 2, 2 }, circle.StrokeDashArray);
        Assert.Equal("+", Assert.Single(glyph.Children.OfType<TextBlock>()).Text);
        Assert.Equal(UiText.Get("EditorProperty_CustomColor"), AutomationProperties.GetName(button));
        Assert.Equal("AnnotationCustomColorOpen", AutomationProperties.GetAutomationId(button));
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.True(host.Editor.CustomColorPopupForTest.IsOpen);
    });

    private static AnnotationEditorController SelectTwoShapes(AnnotationEditorControl editor)
    {
        AnnotationEditorController controller = Controller(editor);
        controller.Document.Add(new RectangleAnnotation { Rect = new RectD(40, 40, 100, 80), StrokeThickness = 3 });
        controller.Document.Add(new EllipseAnnotation { Rect = new RectD(200, 40, 100, 80), StrokeThickness = 3 });
        controller.SelectAll();
        Window.GetWindow(editor).UpdateLayout();
        return controller;
    }

    private static TextBox ThicknessBox(AnnotationEditorControl editor) => Descendants(editor).OfType<TextBox>()
        .Single(box => AutomationProperties.GetAutomationId(box) == "AnnotationThicknessValue");

    private static Border ThicknessField(AnnotationEditorControl editor) => Descendants(editor).OfType<Border>()
        .Single(border => AutomationProperties.GetAutomationId(border) == "AnnotationThicknessField");

    private static MouseWheelEventArgs RaiseWheel(UIElement target, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = target,
        };
        target.RaiseEvent(args);
        return args;
    }
}
