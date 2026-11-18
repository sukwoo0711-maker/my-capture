using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The 40 DIP floor of user zoom (PinGeometry.MinShortSideDip) on both hosts: the zoom readout stays on one
/// line inside the smallest pin a user zoom leaves, and a long strip the app fitted below the floor keeps that
/// fit. Keys go to the pin's own handler; no real pointer, keyboard focus or foreground is used, and every
/// window opens off the desktop without activation.
/// </summary>
public sealed class PinZoomFloorTests
{
    private static readonly RectD OffscreenArea = new(-12000, -12000, 1600, 1000);
    private static readonly MonitorInfo Offscreen = new("PINFLOOR-OFFSCREEN", OffscreenArea, OffscreenArea, 96, false);

    [Theory]
    [InlineData(304, 112, 100, 22, 16, true)] // the usual size: the full text
    [InlineData(108.57, 40, 100, 22, 16, false)] // landscape at the floor: the zoom alone
    [InlineData(40, 108.57, 100, 22, 16, false)] // portrait at the floor: the zoom alone, less spacing
    [InlineData(1920, 25.6, 120, 22, 16, true)] // a strip fitted below the floor: less vertical spacing
    public void TheReadoutFitsOneLineInsideThePin(double roomWidth, double roomHeight, double fullWidth,
        double compactWidth, double lineHeight, bool full)
    {
        PinWindow.FeedbackFit fit = PinWindow.FitZoomReadout(roomWidth, roomHeight, fullWidth, compactWidth, lineHeight);
        Assert.Equal(full, fit.Full);
        double text = full ? fullWidth : compactWidth;
        // Two outline DIPs and two for device-pixel rounding stay free on each axis.
        Assert.InRange(text + fit.Padding.Left + fit.Padding.Right + fit.Margin.Left + fit.Margin.Right + 4, 0, roomWidth);
        Assert.InRange(lineHeight + fit.Padding.Top + fit.Padding.Bottom + fit.Margin.Bottom + 4, 0, roomHeight);
        Assert.Equal(fit.Padding.Left, fit.Padding.Right);
        Assert.Equal(fit.Margin.Left, fit.Margin.Right);
    }

    [Fact]
    public void TheUsualSpacingStaysWhileThereIsRoomAndPaddingGoesBeforeMargin()
    {
        PinWindow.FeedbackFit usual = PinWindow.FitZoomReadout(304, 112, 100, 22, 16);
        Assert.Equal(new Thickness(9, 4, 9, 4), usual.Padding);
        Assert.Equal(new Thickness(8, 0, 8, 8), usual.Margin);
        Assert.Equal(usual with { Full = false }, PinWindow.FitZoomReadout(108.57, 40, 100, 22, 16));
        PinWindow.FeedbackFit portrait = PinWindow.FitZoomReadout(40, 108.57, 100, 22, 16);
        Assert.Equal(new Thickness(7, 4, 7, 4), portrait.Padding);
        Assert.Equal(new Thickness(0, 0, 0, 8), portrait.Margin);
    }

    [Theory]
    [InlineData(304, 112)]
    [InlineData(112, 304)]
    public void AtTheFloorTheWpfReadoutIsOneLineInsideThePin(int width, int height) => Run(() =>
    {
        PinContent content = PinContent.FromImage(PinNativeTestImages.Picture(width, height));
        var pin = new PinWindow(content, new PinViewState(content.LayoutWidthDip, content.LayoutHeightDip, 1, 1, 0.1),
            -11900, -11900, () => new PinSettings { CloseOnDoubleClick = false, SnapWhileDragging = false })
        { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            pin.Show();
            pin.UpdateLayout();
            Assert.True(pin.HandleKey(Key.D0, ModifierKeys.None));
            // A landscape pin has room for the whole readout at 100%; a 112 DIP wide one shows the zoom alone.
            Assert.Equal(width > height ? pin.ZoomReadout(1) : PinWindow.ZoomPercent(1), pin.FeedbackTextForTest);
            AssertReadoutOnOneLineInside(pin, pin, WindowArea(pin));

            for (int i = 0; i < 20; i++) Assert.True(pin.HandleKey(Key.OemMinus, ModifierKeys.None));
            Assert.Equal(pin.State.MinimumZoom, pin.State.Zoom, 9);
            Assert.Equal(PinGeometry.MinShortSideDip, Math.Min(pin.State.WidthDip, pin.State.HeightDip), 6);
            Assert.Equal(PinWindow.ZoomPercent(pin.State.Zoom), pin.FeedbackTextForTest);
            AssertReadoutOnOneLineInside(pin, pin, WindowArea(pin));
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(304, 112)]
    [InlineData(112, 304)]
    public void AtTheFloorTheNativeReadoutIsOneLineInsideThePin(int width, int height) => Run(() =>
    {
        PinManager manager = Manager(lightweight: true, Offscreen);
        try
        {
            PinWindow pin = manager.PinImage(PinNativeTestImages.Picture(width, height));
            NativePinWindow native = pin.NativeWindowForTest!;
            native.LiveClipForTest = OffscreenArea;
            native.CompleteTransitions();

            for (int i = 0; i < 20; i++) Assert.True(pin.HandleKey(Key.OemMinus, ModifierKeys.None));
            Assert.Equal(pin.State.MinimumZoom, pin.State.Zoom, 9);
            Assert.Equal(PinGeometry.MinShortSideDip, Math.Min(pin.State.WidthDip, pin.State.HeightDip), 6);
            Assert.Equal(PinWindow.ZoomPercent(pin.State.Zoom), pin.FeedbackTextForTest);
            AssertNativeReadoutOnOneLineInside(pin, native);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALongStripFittedBelowTheFloorZoomsInButNeverSmallerThanItsFit(bool lightweight) => Run(() =>
    {
        // 3000 × 40 DIP on a 1920 × 1040 DIP work area fits at 64%: 1920 × 25.6 DIP, under the 40 DIP floor.
        var screen = new RectD(-12000, -12000, 1920, 1080);
        var monitor = new MonitorInfo("PINFLOOR-STRIP", screen, screen with { Height = 1040 }, 96, false);
        PinManager manager = Manager(lightweight, monitor);
        try
        {
            PinWindow pin = manager.PinImage(PinNativeTestImages.Picture(3000, 40));
            Assert.Equal(lightweight, pin.IsNativeHosted);
            if (pin.NativeWindowForTest is { } started)
            {
                started.LiveClipForTest = screen;
                started.CompleteTransitions();
            }
            Assert.Equal(0.64, pin.State.Zoom, 9);
            Assert.Equal(1920, pin.State.WidthDip, 6);

            Assert.True(pin.HandleKey(Key.OemMinus, ModifierKeys.None));
            Assert.Equal(0.64, pin.State.Zoom, 9);
            Assert.True(pin.HandleKey(Key.OemPlus, ModifierKeys.None));
            Assert.Equal(0.704, pin.State.Zoom, 9);
            for (int i = 0; i < 3; i++) Assert.True(pin.HandleKey(Key.OemMinus, ModifierKeys.None));
            Assert.Equal(0.64, pin.State.Zoom, 9);
            Assert.Equal(pin.ZoomReadout(0.64), pin.FeedbackTextForTest);

            // The 25.6 DIP strip still holds its readout on one line.
            if (pin.NativeWindowForTest is { } native) AssertNativeReadoutOnOneLineInside(pin, native);
            else
            {
                pin.UpdateLayout();
                AssertReadoutOnOneLineInside(pin, pin, WindowArea(pin));
            }
        }
        finally { manager.CloseAllForExit(); }
    });

    private static Rect WindowArea(Window window) => new(0, 0, window.ActualWidth, window.ActualHeight);

    // The readout is one line, nothing of it is cut, and it lies wholly inside `area` of `root`.
    private static void AssertReadoutOnOneLineInside(PinWindow pin, Visual root, Rect area)
    {
        var chip = Assert.IsType<Border>(pin.FeedbackSurfaceForTest);
        chip.UpdateLayout();
        Assert.Equal(Visibility.Visible, chip.Visibility);
        TextBlock text = ((DockPanel)chip.Child).Children.OfType<TextBlock>().Single();
        Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
        Assert.Null(LayoutInformation.GetLayoutClip(text));
        Assert.InRange(text.ActualHeight, 1, pin.MeasureFeedbackText(pin.FeedbackTextForTest).Height * 1.5);
        Rect placed = chip.TransformToAncestor(root).TransformBounds(new Rect(chip.RenderSize));
        Assert.InRange(placed.Left, area.Left - 0.5, area.Right);
        Assert.InRange(placed.Top, area.Top - 0.5, area.Bottom);
        Assert.InRange(placed.Right, area.Left, area.Right + 0.5);
        Assert.InRange(placed.Bottom, area.Top, area.Bottom + 0.5);
    }

    // The native pin's readout window sits on the pin's bottom edge, inside the pin, with the readout whole in it.
    private static void AssertNativeReadoutOnOneLineInside(PinWindow pin, NativePinWindow native)
    {
        PinNativeSpeedTestWait.Until(() => pin.FeedbackWindowForTest is { IsVisible: true });
        PinFeedbackSurface surface = pin.FeedbackWindowForTest!;
        surface.UpdateLayout();
        PinNativeSpeedTestWait.Until(() => SurfaceRect(surface).Bottom == native.Bounds.Bottom);
        AssertReadoutOnOneLineInside(pin, surface, WindowArea(surface));
        RectD placed = SurfaceRect(surface), bounds = native.Bounds;
        Assert.InRange(placed.Left, bounds.Left, bounds.Right);
        Assert.InRange(placed.Top, bounds.Top, bounds.Bottom);
        Assert.InRange(placed.Right, bounds.Left, bounds.Right);
    }

    private static RectD SurfaceRect(Window window)
    {
        Assert.True(NativePinWindowInterop.GetWindowRect(new WindowInteropHelper(window).Handle, out var rect));
        return new RectD(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    private static PinManager Manager(bool lightweight, MonitorInfo monitor) =>
        new(() => new PinSettings { LightweightImagePins = lightweight, SnapWhileDragging = false, CloseOnDoubleClick = false },
            NullLogger.Instance)
        {
            ReadPlacementMonitorForTest = () => monitor,
            ReadRestoreMonitorsForTest = () => [monitor],
            ActivatePinForTest = static _ => { },
            BeforeShowForTest = static pin => { pin.ShowActivated = false; pin.ShowInTaskbar = false; },
        };

    private static void Run(Action test) => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<IntPtr, double?>? scale = NativePinWindow.ReadWindowScaleForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        NativePinWindow.ReadWindowScaleForTest = static _ => null;
        try
        {
            test();
            PinNativeSpeedTestWait.Until(() => NativePinWindow.LiveWindowCount == 0);
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            NativePinWindow.ReadWindowScaleForTest = scale;
        }
    });
}
