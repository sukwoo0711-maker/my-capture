using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinSnapTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void SyntheticDragSnapsToWorkEdgeUnlessShiftOrSettingDisablesIt(bool enabled, bool shift) => StaTestHost.Run(() =>
    {
        var input = new SyntheticPinDragInput();
        var pin = new PinWindow(PinContent.FromImage(PinCollapseTests.Image()), new PinViewState(320, 180, 1, 1, .1),
            100, 100, () => new PinSettings { SnapWhileDragging = enabled }, input) { ShowActivated = false };
        int reads = 0;
        pin.ReadSnapDisplays = static () => [new("test", new RectD(0, 0, 1200, 800), new RectD(0, 0, 1200, 760), 1)];
        pin.ReadSnapTargets = () => { reads++; return []; };
        try
        {
            pin.Show();
            input.Modifiers = shift ? ModifierKeys.Shift : ModifierKeys.None;
            input.Down(pin);
            MoveTo(pin, input, 5, 100);
            Assert.Equal(enabled && !shift ? 0 : 5, pin.PhysicalBounds.Left);
            Assert.Equal(enabled ? 1 : 0, reads);
            if (enabled)
            {
                input.Modifiers = shift ? ModifierKeys.None : ModifierKeys.Shift;
                input.Move(pin); // Shift changes the result even at the same pointer position.
                Assert.Equal(shift ? 0 : 5, pin.PhysicalBounds.Left);
            }
            input.Up(pin);
            Assert.Equal(enabled ? 1 : 0, reads);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ManagerTargetsIncludeCollapsedButExcludeHiddenPins(bool hidden, bool collapsed) => StaTestHost.Run(() =>
    {
        var inputs = new List<SyntheticPinDragInput>();
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance)
        {
            CreateDragInputForTest = () => { var input = new SyntheticPinDragInput(); inputs.Add(input); return input; },
            BeforeShowForTest = pin => pin.ShowActivated = false,
            ActivatePinForTest = _ => { },
        };
        try
        {
            PinWindow moving = manager.PinImage(PinCollapseTests.Image());
            PinWindow target = manager.PinImage(PinCollapseTests.Image());
            if (collapsed) target.ToggleCollapsed();
            target.MovePhysicalForTest(600, 150);
            if (hidden) target.HidePin();
            moving.ReadSnapDisplays = static () => [];
            var source = moving.ReadSnapTargets;
            int reads = 0;
            moving.ReadSnapTargets = () => { reads++; return source(); };
            SyntheticPinDragInput input = inputs[0];
            var bounds = moving.PhysicalBounds;
            int width = bounds.Right - bounds.Left;
            int desired = target.PhysicalBounds.Left - width - 5;
            input.Down(moving);
            MoveTo(moving, input, desired, 150);
            Assert.Equal(desired + (hidden ? 0 : 5), moving.PhysicalBounds.Left);
            MoveTo(moving, input, desired + 1, 150);
            Assert.Equal(1, reads);
            int draggingLeft = moving.PhysicalBounds.Left;
            moving.HandleKeyDown(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(moving), 0, Key.Right)
                { RoutedEvent = Keyboard.KeyDownEvent }, ModifierKeys.None);
            Assert.True(moving.PhysicalBounds.Left > draggingLeft);
            Assert.Equal(1, reads);
            input.Up(moving);
            int left = moving.PhysicalBounds.Left;
            moving.HandleKeyDown(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(moving), 0, Key.Right)
                { RoutedEvent = Keyboard.KeyDownEvent }, ModifierKeys.None);
            Assert.True(moving.PhysicalBounds.Left > left);
            Assert.Equal(1, reads);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SnapSettingRoundTripsSettingsStore(bool enabled)
    {
        string root = OwnedTestDirectory.Create("pin-snap-settings-");
        try
        {
            var store = new SettingsStore(AppPaths.CreateForRoot(root), NullLogger<SettingsStore>.Instance);
            store.Save(new AppSettings { Pin = { SnapWhileDragging = enabled } });
            Assert.Equal(enabled, store.Load().Pin.SnapWhileDragging);
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    internal static void MoveTo(PinWindow pin, SyntheticPinDragInput input, int left, int top)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(pin);
        input.CursorPosition = ((int)Math.Round(left + input.Anchor.X * dpi.DpiScaleX),
            (int)Math.Round(top + input.Anchor.Y * dpi.DpiScaleY));
        input.Move(pin);
    }
}
