using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>Properties remain reachable after moving from the side inspector to the canvas bar.</summary>
public sealed class EditorContextualPropertyBarTests : KoreanCaptionTest
{
    [Theory]
    [InlineData("Select", "")]
    [InlineData("Rectangle", "color thickness shapeStyle fill")]
    [InlineData("Ellipse", "color thickness shapeStyle fill")]
    [InlineData("Arrow", "color thickness arrowHead")]
    [InlineData("Pen", "color thickness")]
    [InlineData("Highlighter", "color thickness")]
    [InlineData("Text", "color fontFamily fontSize textStyle")]
    [InlineData("StepNumber", "color step")]
    [InlineData("Blur", "blur")]
    [InlineData("Mosaic", "mosaic")]
    [InlineData("Crop", "crop")]
    [InlineData("Image", "")]
    public void EveryToolExposesOnlyItsSupportedPropertyGroups(string tool, string expectedGroups) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        SelectTool(host.Editor, tool);
        WrapPanel bar = PropertyBar(host.Editor);
        var expected = expectedGroups.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        foreach (string group in new[] { "color", "thickness", "shapeStyle", "arrowHead", "fill", "fontFamily", "fontSize", "textStyle", "step", "blur", "mosaic", "crop" })
        {
            FrameworkElement section = Field<FrameworkElement>(host.Editor, "_" + group + "Section");
            Assert.Equal(expected.Contains(group), section.IsVisible);
            Assert.True(bar.IsAncestorOf(section), $"{tool}: {group} is outside the property bar.");
        }

        Assert.False(host.Editor.CustomColorPopupForTest.IsOpen);
        Assert.False(host.Editor.CustomColorInputForTest.IsVisible);
        foreach (Control control in InteractiveControls(bar).Where(control => control.IsVisible))
            AssertAccessibleControl(control, tool);
    });

    [Fact]
    public void CustomColorFlyoutAppliesHexAsOneUndoAndKeepsInvalidInputOpen() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        var shape = new RectangleAnnotation { Rect = new RectD(40, 40, 100, 80), Stroke = ColorRgba.Black };
        controller.Document.Add(shape);
        controller.SetSelected(shape);
        TextBox input = ColorInput(host.Editor);
        Popup popup = host.Editor.CustomColorPopupForTest;
        Button apply = Descendants(popup.Child).OfType<Button>().Single();
        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(apply)));
        input.Text = "#12";
        apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.True(popup.IsOpen);
        Assert.Equal(UiText.Get("Tools.InvalidColor"), host.Editor.StatusText);
        Assert.Equal(ColorRgba.Black, shape.Stroke);
        Assert.Equal(0, controller.Undo.UndoCount);

        input.Text = "#8055aacc";
        apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.False(popup.IsOpen);
        Assert.Equal(new ColorRgba(0x80, 0x55, 0xAA, 0xCC), shape.Stroke);
        Assert.Equal("#8055AACC", input.Text);
        Assert.Equal(1, controller.Undo.UndoCount);
        Assert.Same(host.Editor.CustomColorButtonForTest, Keyboard.FocusedElement);
        Assert.True(controller.PerformUndo());
        Assert.Equal(ColorRgba.Black, shape.Stroke);
        Assert.True(controller.PerformRedo());
        Assert.Equal(new ColorRgba(0x80, 0x55, 0xAA, 0xCC), shape.Stroke);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CustomColorFlyoutEscapeCancelsWithoutAnUndoAndRestoresTriggerFocus(bool fromApplyButton) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        ColorRgba before = controller.StrokeColor;
        TextBox input = ColorInput(host.Editor);
        UIElement target = fromApplyButton
            ? Descendants(host.Editor.CustomColorPopupForTest.Child).OfType<Button>().Single()
            : input;
        Assert.True(target.Focus());
        input.Text = "#778899";
        Assert.True(Press(target, Key.Escape, ime: false));
        Assert.False(host.Editor.CustomColorPopupForTest.IsOpen);
        Assert.Equal(before, controller.StrokeColor);
        Assert.Equal(AnnotationEditorControl.ColorCode(before), input.Text);
        Assert.Equal(0, controller.Undo.UndoCount);
        Assert.Same(host.Editor.CustomColorButtonForTest, Keyboard.FocusedElement);
        Assert.True(Press(host.Editor.CustomColorButtonForTest, Key.T, ime: false));
        Assert.Equal(EditorTool.Text, controller.Tool);
    });

    [Fact]
    public void SelectedSwatchUsesATwoPixelTextPrimaryRingAndFollowsTheResource() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        var primary = new SolidColorBrush(Color.FromRgb(0x20, 0x30, 0x40));
        host.Editor.Resources["Text.Primary"] = primary;
        Button[] swatches = Descendants(PropertyBar(host.Editor)).OfType<Button>().Where(button => button.Tag is ColorRgba).ToArray();
        Assert.Equal(6, swatches.Length);
        Button selected = swatches[2];
        selected.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        host.Window.UpdateLayout();
        Border ring = Assert.IsType<Border>(selected.Content);
        Assert.Equal(new Thickness(2), ring.BorderThickness);
        Assert.Same(primary, ring.BorderBrush);
        Assert.True(ring.CornerRadius.TopLeft >= ring.ActualWidth / 2);
        Assert.Equal((ColorRgba)selected.Tag, Controller(host.Editor).StrokeColor);
        Assert.All(swatches.Where(button => !ReferenceEquals(button, selected)), button =>
            Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(Assert.IsType<Border>(button.Content).BorderBrush).Color));

        var changedPrimary = new SolidColorBrush(Color.FromRgb(0xE0, 0xD0, 0xC0));
        host.Editor.Resources["Text.Primary"] = changedPrimary;
        host.Window.UpdateLayout();
        Assert.Same(changedPrimary, ring.BorderBrush);
    });

    [Theory]
    [InlineData("entry", 12)]
    [InlineData("increase", 4)]
    [InlineData("decrease", 2)]
    [InlineData("dirtyUp", 13)]
    [InlineData("dirtyDown", 11)]
    public void ThicknessEntryAndStepperChangeSelectedShapesAsOneUndo(string action, double expected) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        var first = new RectangleAnnotation { Rect = new RectD(40, 40, 100, 80), StrokeThickness = 3 };
        var second = new EllipseAnnotation { Rect = new RectD(200, 40, 100, 80), StrokeThickness = 3 };
        controller.Document.Add(first);
        controller.Document.Add(second);
        controller.SelectAll();
        string before = controller.Document.ToJson();
        if (action is "entry" or "dirtyUp" or "dirtyDown")
        {
            TextBox value = Descendants(PropertyBar(host.Editor)).OfType<TextBox>()
                .Single(box => AutomationProperties.GetAutomationId(box) == "AnnotationThicknessValue");
            Assert.True(value.Focus());
            value.Text = "12";
            Key key = action switch { "dirtyUp" => Key.Up, "dirtyDown" => Key.Down, _ => Key.Enter };
            Assert.True(Press(value, key, ime: false));
        }
        else
        {
            string id = action == "increase" ? "AnnotationThicknessIncrease" : "AnnotationThicknessDecrease";
            Button button = Descendants(PropertyBar(host.Editor)).OfType<Button>()
                .Single(button => AutomationProperties.GetAutomationId(button) == id);
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        Assert.Equal(expected, first.StrokeThickness);
        Assert.Equal(expected, second.StrokeThickness);
        Assert.Equal(1, controller.Undo.UndoCount);
        string after = controller.Document.ToJson();
        Assert.True(controller.PerformUndo());
        Assert.Equal(before, controller.Document.ToJson());
        Assert.True(controller.PerformRedo());
        Assert.Equal(after, controller.Document.ToJson());
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveTextColorFlyoutCommitsCaptionAndKeepsColorAsSeparateUndo(bool editingExisting) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        controller.StrokeColor = ColorRgba.Black;
        if (editingExisting)
        {
            var existing = new TextAnnotation
            {
                Text = "Original caption", Rect = new RectD(40, 40, 180, 40),
                Foreground = ColorRgba.Black, AutoSize = true,
            };
            controller.Document.Add(existing);
            controller.SetSelected(existing);
            host.Editor.ShortcutActions["EditText"]();
        }
        else
        {
            Invoke(host.Editor, "PlaceTextBox", new PointD(40, 40));
        }
        host.Window.UpdateLayout();
        TextBox live = Field<TextBox>(host.Editor, "_activeTextBox");
        TextAnnotation caption = Assert.IsType<TextAnnotation>(controller.Selected);
        string originalText = caption.Text;
        RectD originalBounds = caption.Rect;
        Assert.True(live.Focus());
        live.Text = "Typed caption";

        // Moving to the separate popup HWND follows the existing lost-focus commit contract.
        TextBox input = ColorInput(host.Editor);
        Assert.True(input.Focus());
        Assert.Null(Field<TextBox?>(host.Editor, "_activeTextBox"));
        Assert.Null(controller.EditingText);
        Assert.Same(caption, controller.Selected);
        Assert.Equal("Typed caption", caption.Text);
        Assert.Equal(ColorRgba.Black, caption.Foreground);
        Assert.True(host.Editor.CustomColorPopupForTest.IsOpen);
        int committedUndoCount = editingExisting ? 1 : 2; // New caption adds a creation command first.
        Assert.Equal(committedUndoCount, controller.Undo.UndoCount);

        input.Text = "#55AACC";
        Assert.True(Press(input, Key.Enter, ime: false));
        Assert.Equal(ColorRgba.FromRgb(0x55, 0xAA, 0xCC), caption.Foreground);
        Assert.Equal(committedUndoCount + 1, controller.Undo.UndoCount);
        Assert.True(controller.PerformUndo());
        Assert.Equal(ColorRgba.Black, caption.Foreground);
        Assert.Equal("Typed caption", caption.Text);
        Assert.True(controller.PerformUndo());
        Assert.Equal(originalText, caption.Text);
        Assert.Equal(originalBounds, caption.Rect);
        if (!editingExisting)
        {
            Assert.True(controller.PerformUndo());
            Assert.Empty(controller.Document.Items);
        }
        else Assert.Same(caption, Assert.Single(controller.Document.Items));
        Assert.Equal(0, controller.Undo.UndoCount);
    });

    [Fact]
    public void NewStepUsesChosenColorAndReopenedStepsKeepTheirSavedColors() => StaTestHost.Run(() =>
    {
        string saved;
        var oldColor = new ColorRgba(0x80, 0x7C, 0x6C, 0xFF);
        var chosen = ColorRgba.FromRgb(0x55, 0xAA, 0xCC);
        var document = AnnotationDocument.CreateFor(640, 360);
        document.Add(new TextAnnotation { IsStepNumber = true, Text = "1", Rect = new RectD(20, 20, 40, 40), Background = oldColor });
        using (EditorWindowHost host = EditorWindowHost.Create(document: document))
        {
            SelectTool(host.Editor, "StepNumber");
            TextBox input = ColorInput(host.Editor);
            input.Text = "#55AACC";
            Assert.True(Press(input, Key.Enter, ime: false));
            AnnotationEditorController controller = Controller(host.Editor);
            controller.PointerDown(new PointD(160, 100));
            controller.PointerUp(new PointD(160, 100));
            TextAnnotation next = Assert.IsType<TextAnnotation>(controller.Selected);
            Assert.True(next.IsStepNumber);
            Assert.Equal("2", next.Text);
            Assert.Equal(chosen, next.Background);
            Assert.Equal(oldColor, Assert.IsType<TextAnnotation>(controller.Document.Items[0]).Background);
            saved = controller.Document.ToJson();
        }

        using EditorWindowHost reopened = EditorWindowHost.Create(document: AnnotationDocument.TryFromJson(saved));
        AnnotationEditorController restored = Controller(reopened.Editor);
        TextAnnotation[] steps = restored.Document.Items.OfType<TextAnnotation>().ToArray();
        Assert.Equal(new[] { oldColor, chosen }, steps.Select(step => step.Background));
        foreach (TextAnnotation step in steps) restored.SetSelected(step);
        Assert.Equal(saved, restored.Document.ToJson());
        Assert.Equal(0, restored.Undo.UndoCount);
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void NarrowBarWrapsAndAllPropertiesRemainReachableAtSimulatedLayoutScales(double scale) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        // A layout transform exercises scaled WPF measure/arrange at the same 680 DIP content
        // width. This is synthetic scaling, not a claim about the host monitor's native DPI.
        host.Editor.LayoutTransform = new ScaleTransform(scale, scale);
        host.Window.Width = 680 * scale;
        host.Window.Height = 640 * scale;
        foreach (string tool in new[] { "Rectangle", "Text", "Blur", "Mosaic", "StepNumber", "Crop" })
        {
            SelectTool(host.Editor, tool);
            WrapPanel bar = PropertyBar(host.Editor);
            ScrollViewer viewport = Descendants(host.Editor).OfType<ScrollViewer>().Single(viewer => viewer.Name == "AnnotationPropertyBarScroll");
            Assert.Equal(ScrollBarVisibility.Disabled, viewport.HorizontalScrollBarVisibility);
            Assert.Equal(0, viewport.ScrollableWidth);
            if (tool is "Rectangle" or "Text")
            {
                double[] rows = bar.Children.OfType<FrameworkElement>().Where(group => group.IsVisible)
                    .Select(group => Math.Round(group.TranslatePoint(default, bar).Y)).Distinct().ToArray();
                Assert.True(rows.Length >= 2, $"{tool} did not wrap at scale {scale}.");
            }
            foreach (Control control in InteractiveControls(bar).Where(control => control.IsVisible))
            {
                AssertAccessibleControl(control, $"{tool} at scale {scale}");
                control.BringIntoView();
                host.Window.UpdateLayout();
                Rect bounds = control.TransformToAncestor(viewport).TransformBounds(new Rect(control.RenderSize));
                Assert.True(bounds.Left >= -1 && bounds.Right <= viewport.ActualWidth + 1
                    && bounds.Top >= -1 && bounds.Bottom <= viewport.ActualHeight + 1,
                    $"{tool} at scale {scale}: {AutomationProperties.GetName(control)} is outside the visible property bar: {bounds} / {viewport.RenderSize}.");
            }
        }
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void OpenHexFlyoutControlsAreNamedFocusableAndReachableAtSimulatedLayoutScales(double scale) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Editor.LayoutTransform = new ScaleTransform(scale, scale);
        host.Window.Width = 680 * scale;
        host.Window.Height = 640 * scale;
        SelectTool(host.Editor, "Rectangle");
        TextBox input = ColorInput(host.Editor);
        var flyout = Assert.IsAssignableFrom<FrameworkElement>(host.Editor.CustomColorPopupForTest.Child);
        flyout.UpdateLayout();

        // Popup content lives in a separate visual tree, so scanning the bar cannot cover it.
        Assert.DoesNotContain(input, InteractiveControls(PropertyBar(host.Editor)));
        Control[] controls = InteractiveControls(flyout).Where(control => control.IsVisible).ToArray();
        Assert.Equal(2, controls.Length);
        Assert.Contains(input, controls);
        Assert.Single(controls.OfType<Button>());
        foreach (Control control in controls)
        {
            AssertAccessibleControl(control, $"HEX flyout at scale {scale}");
            Assert.True(control.Focus());
            Assert.Same(control, Keyboard.FocusedElement);
            control.BringIntoView();
            flyout.UpdateLayout();
            Rect bounds = control.TransformToAncestor(flyout).TransformBounds(new Rect(control.RenderSize));
            Assert.True(bounds.Left >= -1 && bounds.Right <= flyout.ActualWidth + 1
                && bounds.Top >= -1 && bounds.Bottom <= flyout.ActualHeight + 1,
                $"HEX flyout at scale {scale}: {AutomationProperties.GetName(control)} is outside the flyout: {bounds} / {flyout.RenderSize}.");
        }
    });

    [Theory]
    [InlineData("thicknessInput")]
    [InlineData("mosaicSlider")]
    [InlineData("hexInput")]
    [InlineData("hexApply")]
    public void AccessibilityAuditIncludesAndRejectsControlsWithMissingNames(string targetName) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        SelectTool(host.Editor, targetName == "mosaicSlider" ? "Mosaic" : "Rectangle");
        DependencyObject root = PropertyBar(host.Editor);
        Control target;
        if (targetName is "hexInput" or "hexApply")
        {
            TextBox input = ColorInput(host.Editor);
            var flyout = Assert.IsAssignableFrom<FrameworkElement>(host.Editor.CustomColorPopupForTest.Child);
            flyout.UpdateLayout();
            root = flyout;
            target = targetName == "hexInput" ? input : InteractiveControls(flyout).OfType<Button>().Single();
        }
        else
        {
            target = targetName == "mosaicSlider"
                ? Field<Slider>(host.Editor, "_mosaicSlider")
                : InteractiveControls(root).OfType<TextBox>()
                    .Single(box => AutomationProperties.GetAutomationId(box) == "AnnotationThicknessValue");
        }

        Assert.True(target.IsVisible);
        AssertAccessibleControl(target, targetName);
        AutomationProperties.SetName(target, string.Empty);

        Assert.Contains(target, InteractiveControls(root));
        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertAccessibleControl(target, targetName));
        Assert.Contains("has no accessible name", failure.Message);
    });

    private static WrapPanel PropertyBar(AnnotationEditorControl editor) =>
        Descendants(editor).OfType<WrapPanel>().Single(panel => panel.Name == "AnnotationPropertyBar");

    private static IEnumerable<Control> InteractiveControls(DependencyObject root)
    {
        if (root is Control control && control is ButtonBase or TextBox or ComboBox or Slider)
        {
            yield return control;
            // Audit the exposed control once; its template parts (for example, a slider's
            // repeat buttons or a combo box's toggle) share that control's accessible peer.
            yield break;
        }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            foreach (Control descendant in InteractiveControls(child)) yield return descendant;
        }
    }

    private static void AssertAccessibleControl(Control control, string context)
    {
        string name = AutomationProperties.GetName(control);
        Assert.False(string.IsNullOrWhiteSpace(name), $"{context}: {control.GetType().Name} has no accessible name.");
        if (AutomationProperties.GetAutomationId(control) is "AnnotationThicknessDecrease" or "AnnotationThicknessIncrease"
            or "AnnotationFontSizeDecrease" or "AnnotationFontSizeIncrease")
        {
            // The numeric value is the one keyboard stop for the integrated stepper.
            Assert.False(control.Focusable);
            Assert.False(control.IsTabStop);
        }
        else Assert.True(control.Focusable, $"{context}: {name} cannot take keyboard focus.");
        Assert.True(control.ActualWidth >= 24 && control.ActualHeight >= 24,
            $"{context}: {name} target is {control.ActualWidth} × {control.ActualHeight}.");
    }

    private static void SelectTool(AnnotationEditorControl editor, string name)
    {
        if (name == "Crop") Assert.True(editor.HandleShortcut(Key.X, ModifierKeys.None));
        else
        {
            Controller(editor).SetSelected(null);
            // Setting the tool directly avoids opening an image-file picker for the Image case.
            Controller(editor).Tool = Enum.Parse<EditorTool>(name);
            Invoke(editor, "UpdateInspector");
        }
        Window.GetWindow(editor).UpdateLayout();
    }
}
