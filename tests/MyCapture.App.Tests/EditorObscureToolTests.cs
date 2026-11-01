using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorObscureToolTests
{
    [Theory]
    [InlineData("ko-KR", "모자이크 M · 흐림 B")]
    [InlineData("en-US", "Pixelate M · Blur B")]
    public void ObscureDescriptionNamesBothKeysAndClickKeepsLastStyle(string language, string keys) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        using EditorWindowHost host = EditorWindowHost.Create();
        var editor = host.Editor;
        ToggleButton button = editor.ToolButton(EditorTool.Mosaic);
        Assert.Contains(keys, Assert.IsType<string>(button.ToolTip));
        Assert.Contains(keys, AutomationProperties.GetName(button));
        Assert.Contains("M", AutomationProperties.GetAcceleratorKey(button));
        Assert.Contains("B", AutomationProperties.GetAcceleratorKey(button));
        TextBlock caption = ((Grid)button.Content).Children.OfType<TextBlock>().Single();
        Assert.Contains("M", caption.Text);
        Assert.Contains("B", caption.Text);
        Assert.True(editor.HandleShortcut(Key.B, ModifierKeys.None));
        Assert.True(editor.HandleShortcut(Key.V, ModifierKeys.None));
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(EditorTool.Blur, Controller(editor).Tool);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ClosingUnchangedStyleListRestoresFreshShortcutsWithoutRepeatingEscape(bool blur, bool acceptSameItem) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        var editor = host.Editor;
        editor.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        editor.HandleShortcut(blur ? Key.B : Key.M, ModifierKeys.None);
        host.Window.UpdateLayout();
        ComboBox choice = Style(editor, blur ? "_blurSection" : "_mosaicSection");
        Assert.True(choice.Focus());
        choice.IsDropDownOpen = true;
        host.Window.UpdateLayout();
        // Opening queues WPF's item focus; complete that input before sending the next key.
        editor.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var focusedItem = Assert.IsAssignableFrom<ComboBoxItem>(Keyboard.FocusedElement);
        Assert.Same(choice, ItemsControl.ItemsControlFromItemContainer(focusedItem));
        int closes = 0;
        editor.CloseRequested += (_, _) => closes++;
        Assert.True(Press(focusedItem, acceptSameItem ? Key.Enter : Key.Escape, ime: false));
        host.Window.UpdateLayout();
        editor.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.False(choice.IsDropDownOpen);
        Assert.Same(editor, Keyboard.FocusedElement);
        Assert.Equal(blur ? EditorTool.Blur : EditorTool.Mosaic, Controller(editor).Tool);
        Assert.Equal(0, closes);

        KeyEventArgs repeat = KeyArgs(editor, Key.Escape, false, Keyboard.PreviewKeyDownEvent);
        typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(repeat, [true]);
        Assert.True(editor.HandleKeyDown(repeat));
        Assert.Equal(blur ? EditorTool.Blur : EditorTool.Mosaic, Controller(editor).Tool);
        Assert.Equal(0, closes);
        Assert.True(Press(editor, Key.M, ime: false));
        Assert.Equal(EditorTool.Mosaic, Controller(editor).Tool);
        Assert.True(Press(editor, Key.B, ime: false));
        Assert.Equal(EditorTool.Blur, Controller(editor).Tool);
        Assert.True(Press(editor, Key.Escape, ime: false));
        Assert.Equal(EditorTool.Select, Controller(editor).Tool);
        Assert.Equal(0, closes);
    });

    [Fact]
    public void ClosingStyleListDoesNotTakeFocusBackFromAnotherControl() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        var editor = host.Editor;
        // Complete the host's initial ContentRendered focus before testing the list callback.
        editor.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        editor.HandleShortcut(Key.M, ModifierKeys.None);
        host.Window.UpdateLayout();
        ComboBox choice = Style(editor, "_mosaicSection");
        Assert.True(choice.Focus());
        choice.IsDropDownOpen = true;
        host.Window.UpdateLayout();
        editor.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var focusedItem = Assert.IsAssignableFrom<ComboBoxItem>(Keyboard.FocusedElement);
        Assert.Same(choice, ItemsControl.ItemsControlFromItemContainer(focusedItem));
        Assert.True(Press(focusedItem, Key.Escape, ime: false));
        Slider other = Field<Slider>(editor, "_mosaicSlider");
        Assert.True(other.Focus());
        Assert.Same(other, Keyboard.FocusedElement);
        editor.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Same(other, Keyboard.FocusedElement);
    });

    [Fact]
    public void OneRailEntrySwitchesStylesAndRemembersIndependentValuesAndLastStyle() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        var editor = host.Editor;
        Assert.True(editor.HandleShortcut(Key.M, ModifierKeys.None));
        Assert.Equal(EditorTool.Mosaic, Controller(editor).Tool);
        Field<Slider>(editor, "_mosaicSlider").Value = 23;
        Style(editor, "_mosaicSection").SelectedIndex = 1;
        Assert.Equal(EditorTool.Blur, Controller(editor).Tool);
        Field<Slider>(editor, "_blurSlider").Value = 37;
        Style(editor, "_blurSection").SelectedIndex = 0;
        Assert.Equal(23, Field<Slider>(editor, "_mosaicSlider").Value);
        Assert.True(editor.HandleShortcut(Key.B, ModifierKeys.None));
        Assert.Equal(EditorTool.Blur, Controller(editor).Tool);
        Assert.Equal(37, Field<Slider>(editor, "_blurSlider").Value);
        Assert.Equal(23, Field<int>(editor, "_mosaicBlockSize"));
        Assert.True(editor.ToolButton(EditorTool.Mosaic).IsChecked);
        Assert.Same(editor.ToolButton(EditorTool.Mosaic), editor.ToolButton(EditorTool.Blur));
        Assert.Contains(UiText.Get("EditorObscure_Name"), AutomationProperties.GetName(editor.ToolButton(EditorTool.Mosaic)));
        editor.HandleShortcut(Key.V, ModifierKeys.None);
        editor.ToolButton(EditorTool.Mosaic).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(EditorTool.Blur, Controller(editor).Tool);
    });

    [Theory]
    [InlineData(-10, 5, 30, 20, 0, 5, 20, 20)]
    [InlineData(630, 350, 30, 20, 630, 350, 10, 10)]
    [InlineData(10.8, 20.4, 5.4, 8.8, 10, 20, 7, 10)]
    [InlineData(16.2, 29.2, -5.4, -8.8, 10, 20, 7, 10)]
    public void MosaicPatchUsesIntersectionAndCoversBothFractionalEndpoints(double x, double y, double w, double h,
        double left, double top, double width, double height) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Editor.ApplyMosaic(new RectD(x, y, w, h), 8);
        EditorMosaicAsyncTests.WaitFor(host.Editor.MosaicOperationsSettledAsync());
        var patch = Assert.IsType<ImageAnnotation>(Assert.Single(Controller(host.Editor).Document.Items));
        Assert.Equal(new RectD(left, top, width, height), patch.Rect);
        Assert.Equal((int)width, patch.SourceWidth);
        Assert.Equal((int)height, patch.SourceHeight);
    });

    [Theory]
    [InlineData(650, 10, 20, 20)]
    [InlineData(-40, 10, 20, 20)]
    [InlineData(0, 0, 0, 20)]
    public void NonintersectingOrEmptyMosaicDoesNotAddPatch(double x, double y, double w, double h) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Editor.ApplyMosaic(new RectD(x, y, w, h), 8);
        Assert.Empty(Controller(host.Editor).Document.Items);
        Assert.False(host.Editor.HasMosaicWork);
    });

    [Fact]
    public void DragPreviewAtImageEdgeMatchesCommittedIntersection() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        var editor = host.Editor;
        editor.HandleShortcut(Key.M, ModifierKeys.None);
        editor.SimulatePointerDown(editor.ViewportPointOf(new PointD(600, 300)));
        editor.SimulatePointerMove(editor.ViewportPointOf(new PointD(680, 390)), leftPressed: true);
        var expected = new RectD(600, 300, 40, 60);
        Assert.Equal(expected, Field<AnnotationEditorSurface>(editor, "_surface").MosaicGestureRegion);
        editor.SimulatePointerUp(editor.ViewportPointOf(new PointD(680, 390)));
        EditorMosaicAsyncTests.WaitFor(editor.MosaicOperationsSettledAsync());
        Assert.Equal(expected, Assert.IsType<ImageAnnotation>(Assert.Single(Controller(editor).Document.Items)).Rect);
    });

    private static ComboBox Style(AnnotationEditorControl editor, string section) =>
        ((Panel)Field<FrameworkElement>(editor, section)).Children.OfType<ComboBox>().Single();

    [Fact]
    public void SwitchingFromFocusedStyleChoiceReturnsShortcutsToTheCanvas() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        var editor = host.Editor;
        editor.HandleShortcut(Key.M, ModifierKeys.None);
        host.Window.UpdateLayout();
        ComboBox choice = Style(editor, "_mosaicSection");
        Assert.True(choice.Focus());
        choice.SelectedIndex = 1;
        host.Window.UpdateLayout();
        Assert.Equal(EditorTool.Blur, Controller(editor).Tool);
        Assert.True(editor.HandleShortcut(Key.M, ModifierKeys.None));
        Assert.Equal(EditorTool.Mosaic, Controller(editor).Tool);
        editor.HandleShortcut(Key.B, ModifierKeys.None);
        host.Window.UpdateLayout();
        choice = Style(editor, "_blurSection");
        Assert.True(choice.Focus());
        choice.SelectedIndex = 0;
        host.Window.UpdateLayout();
        Assert.Equal(EditorTool.Mosaic, Controller(editor).Tool);
        Assert.True(editor.HandleShortcut(Key.B, ModifierKeys.None));
        Assert.Equal(EditorTool.Blur, Controller(editor).Tool);
    });
}
