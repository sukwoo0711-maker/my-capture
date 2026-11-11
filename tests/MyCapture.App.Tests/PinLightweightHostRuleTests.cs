using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Lightweight picture pins, increment 1: the host decision and the input tables, without windows.</summary>
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
    [InlineData(Key.Escape, ModifierKeys.None, NativePinKeyAction.Close)]
    [InlineData(Key.Delete, ModifierKeys.None, NativePinKeyAction.Close)]
    [InlineData(Key.Add, ModifierKeys.None, NativePinKeyAction.ZoomIn)]
    [InlineData(Key.OemPlus, ModifierKeys.None, NativePinKeyAction.ZoomIn)]
    [InlineData(Key.OemPlus, ModifierKeys.Control, NativePinKeyAction.ZoomIn)]
    [InlineData(Key.Subtract, ModifierKeys.None, NativePinKeyAction.ZoomOut)]
    [InlineData(Key.OemMinus, ModifierKeys.None, NativePinKeyAction.ZoomOut)]
    [InlineData(Key.D0, ModifierKeys.None, NativePinKeyAction.ResetZoom)]
    [InlineData(Key.NumPad0, ModifierKeys.None, NativePinKeyAction.ResetZoom)]
    [InlineData(Key.F2, ModifierKeys.None, NativePinKeyAction.Switch)]
    [InlineData(Key.O, ModifierKeys.None, NativePinKeyAction.Switch)]
    [InlineData(Key.O, ModifierKeys.Control, NativePinKeyAction.None)]
    [InlineData(Key.Left, ModifierKeys.None, NativePinKeyAction.Switch)]
    [InlineData(Key.Right, ModifierKeys.Shift, NativePinKeyAction.Switch)]
    [InlineData(Key.Up, ModifierKeys.None, NativePinKeyAction.Switch)]
    [InlineData(Key.Down, ModifierKeys.None, NativePinKeyAction.Switch)]
    [InlineData(Key.C, ModifierKeys.Control, NativePinKeyAction.Switch)]
    [InlineData(Key.C, ModifierKeys.Control | ModifierKeys.Shift, NativePinKeyAction.Switch)]
    [InlineData(Key.S, ModifierKeys.Control, NativePinKeyAction.Switch)]
    [InlineData(Key.C, ModifierKeys.None, NativePinKeyAction.None)]
    [InlineData(Key.S, ModifierKeys.None, NativePinKeyAction.None)]
    [InlineData(Key.A, ModifierKeys.None, NativePinKeyAction.None)]
    [InlineData(Key.Enter, ModifierKeys.None, NativePinKeyAction.None)]
    [InlineData(Key.F4, ModifierKeys.Alt, NativePinKeyAction.None)]
    public void KeyTableForIncrementOne(Key key, ModifierKeys modifiers, object expected) =>
        Assert.Equal(expected, NativePinHost.RouteKey(key, modifiers));

    [Theory]
    [InlineData(1, false, false, true, NativePinPressAction.Gesture)]
    [InlineData(2, false, false, false, NativePinPressAction.Gesture)]
    [InlineData(2, false, false, true, NativePinPressAction.Close)]
    [InlineData(1, true, false, true, NativePinPressAction.Ignore)]
    [InlineData(2, true, false, true, NativePinPressAction.SwitchRecognize)]
    [InlineData(2, true, false, false, NativePinPressAction.SwitchRecognize)]
    [InlineData(2, false, true, true, NativePinPressAction.SwitchFold)]
    [InlineData(2, true, true, true, NativePinPressAction.SwitchFold)]
    [InlineData(1, false, true, true, NativePinPressAction.Gesture)]
    public void PressTableFollowsThePinsMouseHandlers(int clicks, bool control, bool shift, bool closeOnDoubleClick,
        object expected) =>
        Assert.Equal(expected, NativePinHost.RoutePress(new NativePinPress(clicks, control, shift, new Point(5, 5)), closeOnDoubleClick));
}
