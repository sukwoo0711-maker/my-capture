using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// Issue #235: after a click in the custom color flyout's hex box, the next click on Apply went
/// to the box. The flyout opens with the code selected. A press that collapses that selection
/// leaves the box's text view (which draws the selection itself in this app) waiting for layout,
/// and WPF's TextBox releases its mouse capture on mouse up only while that view is valid, so a
/// release routed before the next layout pass kept capture on the box. These tests post presses
/// and releases to the editor HWND back to back, so WPF routes each release before that layout
/// pass, as it did with the self-test's injected input on Windows 10. No real input, cursor,
/// foreground or clipboard is used. The app-wide fix is TextBoxCaptureGuard; every WPF test
/// thread runs with it.
/// </summary>
public sealed class EditorColorFlyoutApplyInputTests : KoreanCaptionTest
{
    private const string Hex = "#22AA66";

    [Fact]
    public void ReleaseRoutedBeforeTheNextLayoutLeavesNoCaptureOnTheHexBox() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        TextBox input = ColorInput(host.Editor);
        using var mouse = new QueuedMouse(host.Window, input);
        Assert.True(input.Text.Length > 0);
        Assert.Equal(input.Text.Length, input.SelectionLength);
        mouse.Click(input);
        // The press reached WPF's TextBox editor (it collapsed the selection), and the
        // release was routed while the text view still waited for layout.
        Assert.Equal(0, input.SelectionLength);
        Assert.False(mouse.ViewValidAtRelease);
        Assert.Null(Mouse.Captured);
        Assert.False(mouse.NativeCaptureAfterGesture);
        Assert.Same(input, Keyboard.FocusedElement);
        Assert.True(host.Editor.IsCustomColorFlyoutOpenForTest);
    });

    [Fact]
    public void ReleaseTheTextBoxIgnoresStillEndsTheBoxCapture() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        TextBox input = ColorInput(host.Editor);
        Assert.True(input.CaptureMouse());
        input.Select(0, 0);
        // WPF's TextBox returns from this release without releasing: its view waits for layout,
        // and no preview release came first to finish that layout.
        Assert.False(QueuedMouse.IsViewValid(input));
        var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Mouse.MouseUpEvent,
        };
        input.RaiseEvent(up);
        Assert.True(up.Handled);
        Assert.False(input.IsMouseCaptured);
        Assert.Null(Mouse.Captured);
        Assert.Same(input, Keyboard.FocusedElement);
        Assert.True(host.Editor.IsCustomColorFlyoutOpenForTest);
    });

    [Fact]
    public void MouseApplyAfterAClickInTheBoxAppliesClosesAndReturnsFocus() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        TextBox input = ColorInput(host.Editor);
        Button apply = ApplyButton(host.Editor);
        int clicks = 0;
        apply.Click += (_, _) => clicks++;
        using var mouse = new QueuedMouse(host.Window, input);
        mouse.Click(input);
        Assert.False(mouse.ViewValidAtRelease);
        input.Text = Hex;
        mouse.Click(apply);
        Assert.Equal(1, clicks);
        Assert.False(mouse.NativeCaptureAfterGesture);
        AssertApplied(host, Hex);
    });

    [Fact]
    public void RepeatedClicksEachReachTheirOwnTarget() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        Button opener = host.Editor.CustomColorButtonForTest;
        TextBox input = ColorInput(host.Editor);
        Button apply = ApplyButton(host.Editor);
        int clicks = 0;
        apply.Click += (_, _) => clicks++;
        using var mouse = new QueuedMouse(host.Window, input);
        string[] colors = [Hex, "#3366CC", "#AA2244"];
        for (int round = 0; round < colors.Length; round++)
        {
            Point? first = null;
            if (round > 0)
            {
                // Reopened with the mouse, the box again has focus and its code selected.
                mouse.Click(opener);
                Assert.True(host.Editor.IsCustomColorFlyoutOpenForTest);
                Assert.Same(input, Keyboard.FocusedElement);
                Assert.Equal(colors[round - 1], input.Text);
                Assert.Equal(input.Text.Length, input.SelectionLength);
                // The middle round presses on the selected code, where WPF waits for the
                // release to place the caret, instead of past its end.
                if (round == 1)
                {
                    Rect character = input.GetRectFromCharacterIndex(1);
                    first = new Point(character.X + 2, character.Y + character.Height / 2);
                }
            }
            mouse.Click(input, first);
            Assert.Null(Mouse.Captured);
            Assert.Equal(0, input.SelectionLength);
            // A second press in the box, far from the first so it is not a double click.
            mouse.Click(input, new Point(input.ActualWidth - 8, input.ActualHeight / 2));
            Assert.Null(Mouse.Captured);
            Assert.Same(input, Keyboard.FocusedElement);
            input.Text = colors[round];
            mouse.Click(apply);
            Assert.Equal(round + 1, clicks);
            AssertApplied(host, colors[round]);
        }
    });

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Escape)]
    public void EnterOrEscapeAfterAClickInTheBoxFinishesTheFlyout(Key key) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        string before = AnnotationEditorControl.ColorCode(controller.StrokeColor);
        Assert.NotEqual(Hex, before);
        TextBox input = ColorInput(host.Editor);
        using var mouse = new QueuedMouse(host.Window, input);
        mouse.Click(input);
        Assert.False(mouse.ViewValidAtRelease);
        Assert.Null(Mouse.Captured);
        Assert.Same(input, Keyboard.FocusedElement);
        input.Text = Hex;
        Assert.True(Press(input, key, ime: false));
        if (key == Key.Enter)
        {
            AssertApplied(host, Hex);
            return;
        }

        Assert.False(host.Editor.IsCustomColorFlyoutOpenForTest);
        Assert.Equal(before, AnnotationEditorControl.ColorCode(controller.StrokeColor));
        Assert.Equal(before, input.Text);
        Assert.Equal(0, controller.Undo.UndoCount);
        Assert.Same(host.Editor.CustomColorButtonForTest, Keyboard.FocusedElement);
        Assert.Null(Mouse.Captured);
    });

    [Fact]
    public void CanvasPressAfterAClickInTheBoxOnlyDismissesAndTheNextDragDraws() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        Assert.True(Press(host.Editor, Key.R, ime: false));
        ColorRgba color = controller.StrokeColor;
        TextBox input = ColorInput(host.Editor);
        var surface = (FrameworkElement)host.Editor.PointerInputElement;
        using var mouse = new QueuedMouse(host.Window, input);
        mouse.Click(input);
        input.Text = Hex;
        Point canvas = host.Editor.ViewportPointOf(new PointD(320, 180));
        // The press lands on the canvas itself, not on the open flyout above it.
        DependencyObject hit = Assert.IsAssignableFrom<DependencyObject>(
            host.Window.InputHitTest(surface.TranslatePoint(canvas, host.Window)));
        Assert.True(ReferenceEquals(hit, surface) || surface.IsAncestorOf(hit));
        mouse.Click(surface, canvas);
        Assert.False(host.Editor.IsCustomColorFlyoutOpenForTest);
        Assert.Empty(controller.Document.Items);
        Assert.Equal(0, controller.Undo.UndoCount);
        Assert.Equal(color, controller.StrokeColor);
        Assert.Equal(EditorTool.Rectangle, controller.Tool);
        Assert.Null(Mouse.Captured);
        // The canvas is the user's again: the next drag draws one rectangle in the kept color.
        mouse.Drag(surface, host.Editor.ViewportPointOf(new PointD(160, 100)),
            host.Editor.ViewportPointOf(new PointD(420, 260)));
        RectangleAnnotation drawn = Assert.IsType<RectangleAnnotation>(Assert.Single(controller.Document.Items));
        Assert.Equal(color, drawn.Stroke);
        Assert.Null(Mouse.Captured);
    });

    [Fact]
    public void HangulTypedAfterAClickInTheBoxStaysThereAndApplyStillGetsItsClick() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        AnnotationEditorController controller = Controller(host.Editor);
        // Ellipse, so that the R jamo below would visibly switch to Rectangle if the editor took it.
        Assert.True(Press(host.Editor, Key.E, ime: false));
        TextBox input = ColorInput(host.Editor);
        Button apply = ApplyButton(host.Editor);
        using var mouse = new QueuedMouse(host.Window, input);
        mouse.Click(input);
        Assert.Null(Mouse.Captured);
        // Hangul mode: R arrives as an IME key and its jamo composes in the box.
        Assert.False(Press(input, Key.R, ime: true));
        var composition = new Composition(input, "ㄱ");
        Assert.False(Raise(input, composition, TextCompositionManager.PreviewTextInputStartEvent));
        composition.Finish("ㄱ");
        Assert.False(Raise(input, composition, TextCompositionManager.PreviewTextInputEvent));
        mouse.Drain();
        Assert.Equal(0, composition.CompleteCalls);
        Assert.Equal(EditorTool.Ellipse, controller.Tool);
        Assert.Same(input, Keyboard.FocusedElement);
        // The finished jamo reaches the box through WPF's text input, at the caret the click placed.
        string typed = input.Text.Insert(input.CaretIndex, "ㄱ");
        Assert.True(Raise(input, composition, TextCompositionManager.TextInputEvent));
        Assert.Equal(typed, input.Text);
        // That text is not a color: Apply, clicked with the mouse, says so and keeps the flyout open.
        mouse.Click(apply);
        Assert.True(host.Editor.IsCustomColorFlyoutOpenForTest);
        Assert.Equal(UiText.Get("Tools.InvalidColor"), host.Editor.StatusText);
        Assert.Null(Mouse.Captured);
        mouse.Click(input);
        Assert.Same(input, Keyboard.FocusedElement);
        input.Text = Hex;
        mouse.Click(apply);
        AssertApplied(host, Hex);
        Assert.Equal(EditorTool.Ellipse, controller.Tool);
    });

    private static Button ApplyButton(AnnotationEditorControl editor) =>
        Descendants(editor.CustomColorFlyoutContentForTest).OfType<Button>().Single();

    private static void AssertApplied(EditorWindowHost host, string hex)
    {
        Assert.False(host.Editor.IsCustomColorFlyoutOpenForTest);
        Assert.Equal(UiText.Format("Text_67CAE5B118A3", hex), host.Editor.StatusText);
        Assert.Equal(hex, AnnotationEditorControl.ColorCode(Controller(host.Editor).StrokeColor));
        Assert.Same(host.Editor.CustomColorButtonForTest, Keyboard.FocusedElement);
        Assert.True(host.Editor.IsKeyboardFocusWithin);
        Assert.Null(Mouse.Captured);
    }

    private static bool Raise(UIElement target, TextComposition composition, RoutedEvent routedEvent)
    {
        var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) { RoutedEvent = routedEvent };
        target.RaiseEvent(args);
        return args.Handled;
    }

    /// <summary>An IME composition that the test finishes itself.</summary>
    private sealed class Composition : TextComposition
    {
        public Composition(IInputElement source, string text)
            : base(InputManager.Current, source, string.Empty, TextCompositionAutoComplete.Off) => CompositionText = text;

        public int CompleteCalls { get; private set; }

        public void Finish(string result)
        {
            CompositionText = string.Empty;
            Text = result;
        }

        public override void Complete() => CompleteCalls++;
    }

}
