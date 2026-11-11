using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Lightweight picture pins: the host decision, the input tables and the placement rules, without native windows.</summary>
public sealed class PinLightweightHostRuleTests
{
    private static readonly PinSettings On = new() { LightweightImagePins = true };
    private static readonly PinSettings Off = new();

    [Fact]
    public void OnlyAPlainPictureWithTheSettingOnUsesTheNativeHost() => StaTestHost.Run(() =>
    {
        BitmapSource image = PinNativeTestImages.Picture(40, 30);
        PinContent picture = PinContent.FromImage(image);

        Assert.Equal(PinHostKind.Native, PinHostPolicy.Choose(picture, On, screenReader: false, collapsed: false));
        Assert.Equal(PinHostKind.Wpf, PinHostPolicy.Choose(picture, Off, screenReader: false, collapsed: false));
        Assert.Equal(PinHostKind.Wpf, PinHostPolicy.Choose(picture, On, screenReader: true, collapsed: false));
        Assert.Equal(PinHostKind.Wpf, PinHostPolicy.Choose(picture, On, screenReader: false, collapsed: true));

        PinContent[] wpfOnly =
        [
            PinContent.FromText(image, "text", isTable: false),
            PinContent.FromText(image, "a\tb", isTable: true),
            PinContent.FromMarkdown(image, "# title"),
            PinContent.FromTablePicture(image, "a\tb"),
            // A picture that keeps original text (a saved session of a recognized picture).
            PinContent.FromSession(image, PinContentKind.Image, "recognized", string.Empty, usesDeviceIndependentPixels: false),
            // A picture laid out in device-independent pixels.
            PinContent.FromSession(image, PinContentKind.Image, null, string.Empty, usesDeviceIndependentPixels: true),
            PinContent.FromTranslationOverlay(new PinOverlayComposition(image,
                new GeometryDrawing(Brushes.Black, null, new RectangleGeometry(new Rect(0, 0, 4, 4))), false, 1),
                "translated", picture),
            picture with { MermaidResults = new Dictionary<int, MermaidRenderResult> { [0] = new(image, null) } },
        ];
        foreach (PinContent content in wpfOnly)
        {
            Assert.Equal(PinHostKind.Wpf, PinHostPolicy.Choose(content, On, screenReader: false, collapsed: false));
            Assert.Equal(PinHostKind.Wpf, PinHostPolicy.Choose(content, Off, screenReader: false, collapsed: false));
        }
        // An empty diagram dictionary is no diagram.
        Assert.Equal(PinHostKind.Native, PinHostPolicy.Choose(
            picture with { MermaidResults = new Dictionary<int, MermaidRenderResult>() }, On, false, false));
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    public void SwitchGeometryIsThePhysicalSurfaceInDips(double scale)
    {
        var physical = new RectD(-1500, 300, 1200, 750);
        Rect dip = PinWindow.SwitchGeometry(physical, scale);
        Assert.Equal(physical.Left / scale, dip.Left, 9);
        Assert.Equal(physical.Top / scale, dip.Top, 9);
        Assert.Equal(physical.Width / scale, dip.Width, 9);
        Assert.Equal(physical.Height / scale, dip.Height, 9);
        // Back to physical pixels at the same scale gives the same surface, to the pixel.
        Assert.Equal(physical.Left, Math.Round(dip.Left * scale));
        Assert.Equal(physical.Top, Math.Round(dip.Top * scale));
        Assert.Equal(physical.Width, Math.Round(dip.Width * scale));
        Assert.Equal(physical.Height, Math.Round(dip.Height * scale));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void SwitchGeometryTreatsAnUnusableScaleAsOne(double scale)
    {
        Rect dip = PinWindow.SwitchGeometry(new RectD(10, 20, 30, 40), scale);
        Assert.Equal(new Rect(10, 20, 30, 40), dip);
    }

    [Theory]
    [InlineData(Key.Escape, ModifierKeys.None, NativePinKeyAction.Handle)]
    [InlineData(Key.Delete, ModifierKeys.None, NativePinKeyAction.Handle)]
    [InlineData(Key.Add, ModifierKeys.None, NativePinKeyAction.Handle)]
    [InlineData(Key.OemPlus, ModifierKeys.None, NativePinKeyAction.Handle)]
    [InlineData(Key.OemPlus, ModifierKeys.Control, NativePinKeyAction.Handle)]
    [InlineData(Key.Subtract, ModifierKeys.None, NativePinKeyAction.Handle)]
    [InlineData(Key.OemMinus, ModifierKeys.None, NativePinKeyAction.Handle)]
    [InlineData(Key.D0, ModifierKeys.None, NativePinKeyAction.Handle)]
    [InlineData(Key.NumPad0, ModifierKeys.None, NativePinKeyAction.Handle)]
    [InlineData(Key.F2, ModifierKeys.None, NativePinKeyAction.Switch)]
    [InlineData(Key.O, ModifierKeys.None, NativePinKeyAction.Handle)]
    [InlineData(Key.O, ModifierKeys.Control, NativePinKeyAction.None)]
    [InlineData(Key.Left, ModifierKeys.None, NativePinKeyAction.Handle)]
    [InlineData(Key.Right, ModifierKeys.Shift, NativePinKeyAction.Handle)]
    [InlineData(Key.Up, ModifierKeys.None, NativePinKeyAction.Handle)]
    [InlineData(Key.Down, ModifierKeys.None, NativePinKeyAction.Handle)]
    [InlineData(Key.C, ModifierKeys.Control, NativePinKeyAction.Handle)]
    [InlineData(Key.C, ModifierKeys.Control | ModifierKeys.Shift, NativePinKeyAction.Handle)]
    [InlineData(Key.S, ModifierKeys.Control, NativePinKeyAction.Handle)]
    [InlineData(Key.S, ModifierKeys.Control | ModifierKeys.Shift, NativePinKeyAction.Handle)]
    [InlineData(Key.C, ModifierKeys.None, NativePinKeyAction.None)]
    [InlineData(Key.S, ModifierKeys.None, NativePinKeyAction.None)]
    [InlineData(Key.A, ModifierKeys.None, NativePinKeyAction.None)]
    [InlineData(Key.A, ModifierKeys.Control, NativePinKeyAction.None)]
    [InlineData(Key.Enter, ModifierKeys.None, NativePinKeyAction.None)]
    [InlineData(Key.F4, ModifierKeys.Alt, NativePinKeyAction.None)]
    public void KeyTableRunsThePassiveKeysNatively(Key key, ModifierKeys modifiers, object expected) =>
        Assert.Equal(expected, NativePinHost.RouteKey(key, modifiers));

    /// <summary>
    /// Every key the native host runs is a key the WPF window handles with the same handler, so the two
    /// hosts give the same command; a key the WPF window ignores is passed on by the native host too.
    /// </summary>
    [Fact]
    public void EveryNativeKeyIsAPinKeyOfTheWpfHandler() => StaTestHost.Run(() =>
    {
        BitmapSource image = PinNativeTestImages.Picture(40, 30);
        var pin = new PinWindow(PinContent.FromImage(image), new MyCapture.Core.Pin.PinViewState(40, 30, 1, 1, .1), -10000, -10000);
        var copies = 0;
        pin.CopyRequested += (_, _) => copies++;
        try
        {
            foreach (Key key in new[] { Key.A, Key.Enter, Key.Tab, Key.Space, Key.F5 })
            {
                Assert.Equal(NativePinKeyAction.None, NativePinHost.RouteKey(key, ModifierKeys.None));
                Assert.False(pin.HandleKey(key, ModifierKeys.None));
            }
            Assert.Equal(NativePinKeyAction.Handle, NativePinHost.RouteKey(Key.C, ModifierKeys.Control));
            Assert.True(pin.HandleKey(Key.C, ModifierKeys.Control));
            Assert.Equal(1, copies);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(1, false, false, true, NativePinPressAction.Gesture)]
    [InlineData(2, false, false, false, NativePinPressAction.Gesture)]
    [InlineData(2, false, false, true, NativePinPressAction.Close)]
    [InlineData(1, true, false, true, NativePinPressAction.CtrlClick)]
    [InlineData(2, true, false, true, NativePinPressAction.Recognize)]
    [InlineData(2, true, false, false, NativePinPressAction.Recognize)]
    [InlineData(2, false, true, true, NativePinPressAction.SwitchFold)]
    [InlineData(2, true, true, true, NativePinPressAction.SwitchFold)]
    [InlineData(1, false, true, true, NativePinPressAction.Gesture)]
    public void PressTableFollowsThePinsMouseHandlers(int clicks, bool control, bool shift, bool closeOnDoubleClick,
        object expected) =>
        Assert.Equal(expected, NativePinHost.RoutePress(new NativePinPress(clicks, control, shift, new Point(5, 5)), closeOnDoubleClick));

    [Theory]
    [InlineData(1.0, 600, 400, 120, 30)]
    [InlineData(1.25, 750, 500, 150, 38)]
    [InlineData(1.5, 901, 601, 181, 45)]
    [InlineData(2.0, 1200, 800, 240, 60)]
    public void TheMessageSitsCentredOnThePinsBottomEdgeInWholePixels(double scale, double width, double height,
        double messageWidth, double messageHeight)
    {
        var pin = new RectD(-1500 * scale, 300 * scale, width, height);
        RectD placed = PinFeedbackSurface.Place(pin, messageWidth + .4, messageHeight + .4);
        Assert.Equal(Math.Ceiling(messageWidth + .4), placed.Width);
        Assert.Equal(Math.Ceiling(messageHeight + .4), placed.Height);
        Assert.Equal(pin.Top + pin.Height, placed.Top + placed.Height);
        Assert.InRange((placed.Left + (placed.Width / 2)) - (pin.Left + (pin.Width / 2)), -.5, .5);
        Assert.Equal(Math.Round(placed.Left), placed.Left);
        // Rounded inside the pin, as a WPF pin's layout rounds it, not at the screen coordinate.
        Assert.Equal(Math.Round((pin.Width - placed.Width) / 2), placed.Left - pin.Left);
    }

    [Fact]
    public void TheSwitchCheckMeasuresTheDifferenceFromBefore()
    {
        const int width = 4, height = 3, stride = width * 4;
        var before = new byte[stride * height];
        var after = (byte[])before.Clone();
        App.SwitchDifference same = App.CompareSwitchFrames(before, after, width, height, stride);
        Assert.Equal(0, same.ChangedPixels);
        Assert.Null(same.ChangedBox);
        after[(1 * stride) + (2 * 4) + 1] = 90; // (2,1) green
        after[(2 * stride) + (3 * 4) + 0] = 5;  // (3,2) blue, under the threshold
        App.SwitchDifference diff = App.CompareSwitchFrames(before, after, width, height, stride);
        Assert.Equal(1, diff.ChangedPixels);
        Assert.Equal(90, diff.MaxDifference);
        Assert.Equal(new RectD(2, 1, 1, 1), diff.ChangedBox);
        Assert.Equal(95 / (3d * width * height), diff.MeanDifference, 9);
    }
}
