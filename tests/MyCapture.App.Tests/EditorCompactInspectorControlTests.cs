using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorCompactInspectorControlTests
{
    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void TextStyleIconsExposeToggleStateAndSpaceKeepsFocusWithOneUndo(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var host = new InspectorContractHost();
        AnnotationEditorController controller = Controller(host.Editor);
        TextAnnotation text = SelectText(host);
        string[] fields = ["_textBold", "_textBackground", "_textOutline"];
        string[] ids = ["AnnotationTextBold", "AnnotationTextBackground", "AnnotationTextOutline"];
        string[] names = language == "ko-KR" ? ["굵게", "글자 배경", "외곽선"] : ["Bold", "Text background", "Outline"];
        Func<TextAnnotation, bool>[] values = [caption => caption.Bold, caption => caption.Background.A > 0, caption => caption.HasOutline];
        for (int index = 0; index < fields.Length; index++)
        {
            ToggleButton toggle = Field<ToggleButton>(host.Editor, fields[index]);
            Assert.Equal(ids[index], AutomationProperties.GetAutomationId(toggle));
            Assert.Equal(names[index], AutomationProperties.GetName(toggle));
            Assert.Equal(names[index], toggle.ToolTip);
            Assert.Equal(32, toggle.ActualWidth);
            Assert.Equal(32, toggle.ActualHeight);
            Assert.True(toggle.Focusable && toggle.IsTabStop);
            Assert.IsAssignableFrom<FrameworkElement>(toggle.Content);
            Assert.DoesNotContain(Descendants(toggle).OfType<TextBlock>(), label => label.Text == names[index]);
            var peer = new ToggleButtonAutomationPeer(toggle);
            var provider = Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle));
            Assert.Equal(names[index], peer.GetName());
            bool beforeValue = values[index](text);
            Assert.Equal(beforeValue ? ToggleState.On : ToggleState.Off, provider.ToggleState);
            string before = controller.Document.ToJson();
            int undoCount = controller.Undo.UndoCount;
            Assert.True(toggle.Focus());
            host.Settle();
            Border focusRing = Assert.IsType<Border>(toggle.Template.FindName("FocusRing", toggle));
            Assert.Equal(1, focusRing.Opacity);

            PressSpace(toggle);

            Assert.Equal(!beforeValue, values[index](text));
            Assert.Equal(!beforeValue, toggle.IsChecked);
            Assert.Equal(beforeValue ? ToggleState.Off : ToggleState.On, provider.ToggleState);
            Assert.Same(toggle, Keyboard.FocusedElement);
            Assert.Equal(undoCount + 1, controller.Undo.UndoCount);
            Assert.True(controller.PerformUndo());
            Assert.Equal(before, controller.Document.ToJson());
            Assert.Equal(beforeValue, toggle.IsChecked);
        }
    });

    [Theory]
    [InlineData("ko-KR", "선택 항목 삭제", "선택 항목 삭제 (Del)")]
    [InlineData("en-US", "Delete selection", "Delete selection (Del)")]
    public void TrashIconIsLastAfterADividerAndDeletesTheSelectionWithOneUndo(
        string language, string name, string tooltip) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using var host = new InspectorContractHost(width: 2000);
        AnnotationEditorController controller = Controller(host.Editor);
        SelectText(host);
        controller.Document.Add(new RectangleAnnotation { Rect = new RectD(250, 80, 100, 70) });
        controller.SelectAll();
        host.Settle();
        Button delete = Field<Button>(host.Editor, "_deleteButton");
        Assert.Equal("AnnotationDeleteSelection", AutomationProperties.GetAutomationId(delete));
        Assert.Equal(name, AutomationProperties.GetName(delete));
        Assert.Equal(tooltip, delete.ToolTip);
        Assert.Equal("Del", AutomationProperties.GetAcceleratorKey(delete));
        Assert.Equal(32, delete.ActualWidth);
        Assert.Equal(32, delete.ActualHeight);
        Assert.True(delete.Focusable && delete.IsTabStop);
        Assert.IsAssignableFrom<FrameworkElement>(delete.Content);
        Assert.NotEmpty(Descendants(delete).OfType<Viewbox>());
        Assert.DoesNotContain(Descendants(delete).OfType<TextBlock>(), label => !string.IsNullOrWhiteSpace(label.Text));
        Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(delete).GetPattern(PatternInterface.Invoke));
        FrameworkElement[] groups = host.PropertyGroups.Children.OfType<FrameworkElement>()
            .Where(group => group.IsVisible && group.ActualWidth > 0).ToArray();
        Assert.Same(delete, groups[^1]);
        Rect previous = groups[^2].TransformToAncestor(host.PropertyGroups).TransformBounds(new Rect(groups[^2].RenderSize));
        Rect current = delete.TransformToAncestor(host.PropertyGroups).TransformBounds(new Rect(delete.RenderSize));
        Rect divider = host.PropertyGroups.DividerBounds[^1];
        Assert.InRange(divider.Left, previous.Right, current.Left);
        Assert.Equal(20, divider.Height);
        string before = controller.Document.ToJson();
        Assert.Equal(2, controller.SelectedItems.Count);

        delete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Empty(controller.Document.Items);
        Assert.Empty(controller.SelectedItems);
        Assert.Equal(1, controller.Undo.UndoCount);
        Assert.True(controller.PerformUndo());
        Assert.Equal(before, controller.Document.ToJson());
    });

    [Theory]
    [InlineData(Key.Up, "", 33)]
    [InlineData(Key.Down, "", 31)]
    [InlineData(Key.None, "AnnotationFontSizeIncrease", 33)]
    [InlineData(Key.None, "AnnotationFontSizeDecrease", 31)]
    public void FontSizeStepperCommitsPendingTextRefitsAndUndoesExactlyOnce(Key key, string buttonId, double expected) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage("ko-KR");
        using var host = new InspectorContractHost();
        AnnotationEditorController controller = Controller(host.Editor);
        TextAnnotation text = SelectText(host);
        TextBox box = Descendants(host.Editor).OfType<TextBox>()
            .Single(input => AutomationProperties.GetAutomationId(input) == "AnnotationFontSizeValue");
        Border field = Descendants(host.Editor).OfType<Border>()
            .Single(candidate => AutomationProperties.GetAutomationId(candidate) == "AnnotationFontSizeField");
        Slider model = Field<Slider>(host.Editor, "_fontSizeSlider");
        Assert.DoesNotContain(model, Descendants(host.Editor).OfType<Slider>());
        Assert.Equal(32, field.ActualHeight);
        Assert.Equal(24, model.Value);
        string before = controller.Document.ToJson();
        RectD beforeBounds = text.Rect;
        int valueChanges = 0;
        model.ValueChanged += (_, _) => valueChanges++;
        Assert.True(box.Focus());
        box.Text = "32";
        Assert.Equal(0, controller.Undo.UndoCount);
        if (key != Key.None) Assert.True(Press(box, key, ime: false));
        else
        {
            Button button = Descendants(field).OfType<Button>()
                .Single(candidate => AutomationProperties.GetAutomationId(candidate) == buttonId);
            Assert.Equal(32, button.ActualWidth);
            Assert.Equal(32, button.ActualHeight);
            Assert.False(button.Focusable || button.IsTabStop);
            Assert.True(button.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }

        Assert.Equal(expected, text.FontSize);
        Assert.Equal(expected, model.Value);
        Assert.Equal(SliderValueEntry.Format(expected), box.Text);
        Assert.NotEqual(beforeBounds, text.Rect);
        Assert.Same(box, Keyboard.FocusedElement);
        Assert.Equal(1, valueChanges);
        Assert.Equal(1, controller.Undo.UndoCount);
        host.Editor.Focus();
        Assert.Equal(1, valueChanges);
        Assert.Equal(1, controller.Undo.UndoCount);
        Assert.True(controller.PerformUndo());
        Assert.Equal(before, controller.Document.ToJson());
        Assert.Equal("24", box.Text);
    });

    private static TextAnnotation SelectText(InspectorContractHost host)
    {
        AnnotationEditorController controller = Controller(host.Editor);
        var text = new TextAnnotation
        {
            Text = "Synthetic caption", Rect = new RectD(40, 40, 200, 50), FontSize = 24,
            AutoSize = true, Bold = false, Background = ColorRgba.Transparent, HasOutline = false,
        };
        controller.Document.Add(text);
        controller.SetSelected(text);
        host.Settle();
        return text;
    }

    private static void PressSpace(ToggleButton toggle)
    {
        Assert.True(Press(toggle, Key.Space, ime: false));
        KeyEventArgs preview = KeyArgs(toggle, Key.Space, ime: false, Keyboard.PreviewKeyUpEvent);
        toggle.RaiseEvent(preview);
        if (!preview.Handled) toggle.RaiseEvent(KeyArgs(toggle, Key.Space, ime: false, Keyboard.KeyUpEvent));
    }
}
