using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinDragLifecycleTests
{
    [Fact]
    public void CaptureFailure_DoesNotStartGestureOrMoveWindow() => StaTestHost.Run(() =>
    {
        var input = new SyntheticPinDragInput { CaptureSucceeds = false };
        var pin = NewPin(input);
        try
        {
            pin.Show();
            var initial = WindowStyleFacade.GetWindowBounds(pin.Handle);
            input.Down(pin);
            input.CursorPosition = (initial.Left + 100, initial.Top + 70);
            input.Move(pin);
            Assert.Equal(initial, WindowStyleFacade.GetWindowBounds(pin.Handle));
            Assert.False(input.Captured);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData("up")]
    [InlineData("lost")]
    [InlineData("released-button")]
    [InlineData("missing-capture")]
    public void EndingGesture_PreventsSubsequentMovementAndAllowsFreshDrag(string ending) => StaTestHost.Run(() =>
    {
        var input = new SyntheticPinDragInput();
        var pin = NewPin(input);
        try
        {
            pin.Show();
            input.Down(pin);
            input.CursorPosition = (400, 250);
            input.Move(pin);
            var moved = WindowStyleFacade.GetWindowBounds(pin.Handle);
            switch (ending)
            {
                case "up": input.Up(pin); break;
                case "lost": input.LoseCapture(pin); break;
                case "released-button": input.Pressed = false; break;
                case "missing-capture": input.Captured = false; break;
            }
            input.CursorPosition = (600, 350);
            input.Move(pin);
            Assert.Equal(moved, WindowStyleFacade.GetWindowBounds(pin.Handle));
            Assert.False(input.Captured);
            Assert.Null(typeof(PinWindow).GetField("_dragDesktop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pin));
            input.Pressed = true;
            input.Down(pin);
            input.Move(pin);
            Assert.NotEqual(moved, WindowStyleFacade.GetWindowBounds(pin.Handle));
            input.Up(pin);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void NativeCaptureLoss_ClearsGestureWithoutAMouseUp() => StaTestHost.Run(() =>
    {
        var pin = NewPin(null);
        try
        {
            pin.Show();
            typeof(PinWindow).GetMethod("OnMouseLeftButtonDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(pin, [new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent }]);
            Assert.True(pin.IsMouseCaptured);
            pin.ReleaseMouseCapture();
            Assert.False(pin.IsMouseCaptured);
            Assert.False((bool)typeof(PinWindow).GetField("_dragging", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pin)!);
            Assert.Null(typeof(PinWindow).GetField("_dragDesktop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pin));
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void CloseDuringGesture_ReleasesCapture() => StaTestHost.Run(() =>
    {
        var input = new SyntheticPinDragInput();
        var pin = NewPin(input);
        pin.Show();
        input.Down(pin);
        pin.Close();
        Assert.False(input.Captured);
        Assert.Equal(1, input.ReleaseCount);
    });

    private static PinWindow NewPin(IPinDragInput? input)
    {
        var bitmap = new WriteableBitmap(160, 100, 96, 96, PixelFormats.Bgr32, null);
        bitmap.Freeze();
        return new PinWindow(PinContent.FromImage(bitmap), new PinViewState(160, 100, 1, 1, 0.1), 200, 150,
            () => new PinSettings { CloseOnDoubleClick = false }, input) { ShowActivated = false };
    }
}
