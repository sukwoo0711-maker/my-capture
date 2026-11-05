using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Editing;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class EditorInlineColorFlyoutTests : KoreanCaptionTest
{
    [Theory]
    [InlineData(640, 100, 680, 460, 448, 132)]
    [InlineData(640, 400, 680, 460, 448, 284)]
    [InlineData(0, 20, 680, 460, 0, 52)]
    [InlineData(-5, 440, 680, 460, 0, 324)]
    [InlineData(100, 60, 500, 120, 100, 0)]
    public void PlacementPrefersBelowThenAboveAndClampsToRoot(double x, double y,
        double rootWidth, double rootHeight, double expectedX, double expectedY)
    {
        Point actual = AnnotationEditorControl.PlaceCustomColorFlyout(new Rect(x, y, 32, 32),
            new Size(232, 116), new Size(rootWidth, rootHeight));
        Assert.Equal(new Point(expectedX, expectedY), actual);
        Assert.InRange(actual.X, 0, rootWidth - 232);
        Assert.InRange(actual.Y, 0, rootHeight - 116);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void ActualHostSharesOwnerAndRemainsInsideResizedLayout(double scale) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        host.Editor.LayoutTransform = new ScaleTransform(scale, scale);
        host.Window.Width = 680 * scale;
        host.Window.Height = 640 * scale;
        host.Window.UpdateLayout();
        TextBox input = ColorInput(host.Editor);
        FrameworkElement flyout = host.Editor.CustomColorFlyoutContentForTest;
        Assert.Equal(232, flyout.Width);
        Assert.True(InputMethod.GetIsInputMethodEnabled(input));
        Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(flyout));
        Assert.Same(input, Keyboard.FocusedElement);
        Assert.Equal(0, input.SelectionStart);
        Assert.Equal(input.Text.Length, input.SelectionLength);
        Assert.True(host.Editor.IsAncestorOf(flyout) && flyout.IsAncestorOf(input));
        HwndSource owner = Assert.IsType<HwndSource>(PresentationSource.FromVisual(host.Window));
        Assert.Same(owner, PresentationSource.FromVisual(flyout));
        Assert.Same(owner, PresentationSource.FromVisual(input));
        Assert.Null(Mouse.Captured);
        Assert.DoesNotContain(Descendants(host.Editor), value => value is Popup popup && popup.Child == flyout);
        Inside();
        host.Window.Width = 900 * scale;
        host.Window.Height = 520 * scale;
        host.Window.UpdateLayout();
        host.Window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        host.Window.UpdateLayout();
        Inside();

        void Inside()
        {
            Rect actual = flyout.TransformToAncestor(host.Editor).TransformBounds(new Rect(flyout.RenderSize));
            Assert.InRange(actual.Left, 0, host.Editor.ActualWidth);
            Assert.InRange(actual.Top, 0, host.Editor.ActualHeight);
            Assert.InRange(actual.Right, 0, host.Editor.ActualWidth);
            Assert.InRange(actual.Bottom, 0, host.Editor.ActualHeight);
        }
    });

    [Fact]
    public void OpenerReclickClosesWithoutApplyingOrReopening() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        var controller = Controller(host.Editor);
        var color = controller.StrokeColor; var tool = controller.Tool;
        TextBox input = ColorInput(host.Editor);
        input.Text = "#22AA66";
        host.Editor.CustomColorButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        host.Window.UpdateLayout();
        Assert.False(host.Editor.IsCustomColorFlyoutOpenForTest);
        Assert.Same(host.Editor.CustomColorButtonForTest, Keyboard.FocusedElement);
        Assert.Equal(color, controller.StrokeColor);
        Assert.Equal(tool, controller.Tool);
        Assert.Equal(0, controller.Undo.UndoCount);
    });

    [Fact]
    public void FirstOutsideMouseGestureIsConsumedBeforeCanvasInput() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        var controller = Controller(host.Editor);
        var color = controller.StrokeColor; var tool = controller.Tool;
        ColorInput(host.Editor).Text = "#22AA66";
        UIElement surface = host.Editor.PointerInputElement;
        int unhandledDown = 0, unhandledUp = 0;
        surface.AddHandler(Mouse.MouseDownEvent, new MouseButtonEventHandler((_, _) => unhandledDown++));
        surface.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, _) => unhandledUp++));
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
        surface.RaiseEvent(down);
        Assert.True(down.Handled);
        Assert.False(host.Editor.IsCustomColorFlyoutOpenForTest);
        down.RoutedEvent = Mouse.MouseDownEvent;
        surface.RaiseEvent(down);
        var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 1, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent };
        surface.RaiseEvent(up);
        Assert.True(up.Handled);
        up.RoutedEvent = Mouse.MouseUpEvent;
        surface.RaiseEvent(up);
        Assert.Equal(0, unhandledDown);
        Assert.Equal(0, unhandledUp);
        Assert.Empty(controller.Document.Items);
        Assert.Equal(0, controller.Undo.UndoCount);
        Assert.Equal(color, controller.StrokeColor);
        Assert.Equal(tool, controller.Tool);
        Assert.Null(Mouse.Captured);
        // A later independent gesture is not swallowed after the dismissing release.
        var next = new MouseButtonEventArgs(Mouse.PrimaryDevice, 2, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
        surface.RaiseEvent(next);
        Assert.False(next.Handled);
    });

    [Fact]
    public void UnloadClosesDetachesAndReloadReconnectsWithoutDuplicatingTheLayer() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        ColorInput(host.Editor);
        FrameworkElement content = host.Editor.CustomColorFlyoutContentForTest;
        host.Window.Content = null;
        Drain();
        Assert.False(host.Editor.IsCustomColorFlyoutOpenForTest);
        Assert.Null(Field<Window?>(host.Editor, "_colorFlyoutOwner"));
        Assert.Null(Field<HwndSource?>(host.Editor, "_colorFlyoutOwnerSource"));
        host.Window.Content = host.Editor;
        Drain();
        ColorInput(host.Editor);
        Assert.Same(content, host.Editor.CustomColorFlyoutContentForTest);
        Assert.Same(host.Window, Field<Window>(host.Editor, "_colorFlyoutOwner"));
        Assert.Single(Descendants(host.Editor), value => ReferenceEquals(value, content));
        host.Window.Close();
        Assert.False(host.Editor.IsCustomColorFlyoutOpenForTest);
        Assert.Null(Field<Window?>(host.Editor, "_colorFlyoutOwner"));
        Assert.Null(Field<HwndSource?>(host.Editor, "_colorFlyoutOwnerSource"));

        void Drain()
        {
            host.Window.UpdateLayout();
            host.Window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        }
    });

    [Fact]
    public void DeactivationHandlerClosesWithoutApplyingOrForcingOpenerFocus() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        var controller = Controller(host.Editor);
        var color = controller.StrokeColor;
        ColorInput(host.Editor).Text = "#22AA66";
        Assert.True(host.Editor.Focus());
        Assert.Same(host.Editor, Keyboard.FocusedElement);
        Invoke(host.Editor, "OnColorFlyoutOwnerDeactivated", host.Window, EventArgs.Empty);
        Assert.False(host.Editor.IsCustomColorFlyoutOpenForTest);
        Assert.Same(host.Editor, Keyboard.FocusedElement);
        Assert.Equal(color, controller.StrokeColor);
        Assert.Equal(0, controller.Undo.UndoCount);
    });

    [Theory]
    [InlineData(0x00A1)]
    [InlineData(0x00A4)]
    [InlineData(0x00A7)]
    [InlineData(0x00AB)]
    public void NonclientHandlerClosesAndLeavesTheSystemMessageUnhandled(int message) => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create();
        ColorInput(host.Editor);
        var method = typeof(AnnotationEditorControl).GetMethod("ColorFlyoutOwnerHook",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        object[] args = [new WindowInteropHelper(host.Window).Handle, message, IntPtr.Zero, IntPtr.Zero, false];
        Assert.Equal(IntPtr.Zero, method.Invoke(host.Editor, args));
        Assert.False(Assert.IsType<bool>(args[4]));
        Assert.False(host.Editor.IsCustomColorFlyoutOpenForTest);
        Assert.Equal(0, Controller(host.Editor).Undo.UndoCount);
    });
}
