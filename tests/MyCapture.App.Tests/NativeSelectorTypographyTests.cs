using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class NativeSelectorTypographyTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    public void GuideAndReadoutUseTheSharedTypeScaleAndTheWpfTypefaces(uint dpi) => StaTestHost.Run(() =>
    {
        var bounds = new RectD(0, 0, 1280, 720);
        BitmapSource bitmap = BitmapSource.Create(1280, 720, 96, 96, PixelFormats.Bgr32, null,
            new byte[1280 * 720 * 4], 1280 * 4);
        bitmap.Freeze();
        var frame = new FrozenFrame(bitmap, bounds, null, 0);
        var reference = new CaptureOverlayView(frame, showMagnifier: false);
        using var owner = new NativeCaptureOverlay(bounds, false, false)
        {
            PlaceCursor = _ => false,
            ReadCursor = () => new PointD(320, 240),
            ReadMonitors = () => [new MonitorInfo("test", bounds, bounds, dpi, true)],
        };
        owner.AttachFrame(frame);
        try
        {
            using var chrome = new NativeSelectorChrome(owner, false, false);
            TextBlock readout = Assert.IsType<TextBlock>(Assert.IsAssignableFrom<Border>(chrome.Windows[1].Content).Child);
            TextBlock guide = Assert.IsType<TextBlock>(Assert.IsType<Border>(chrome.Windows[2].Content).Child);
            Assert.Equal(TypeScale.Caption, readout.FontSize);
            Assert.Equal(TypeScale.Caption, guide.FontSize);
            AssertTypeface(reference, "_monoTypeface", readout);
            AssertTypeface(reference, "_uiTypeface", guide);
            foreach (Window window in new[] { chrome.Windows[1], chrome.Windows[2] })
            {
                var border = Assert.IsAssignableFrom<Border>(window.Content);
                var label = Assert.IsType<TextBlock>(border.Child);
                var transform = Assert.IsType<ScaleTransform>(border.LayoutTransform);
                Matrix device = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!.CompositionTarget.TransformToDevice;
                Assert.Equal(TypeScale.Caption * dpi / 96d, label.FontSize * transform.ScaleY * device.M22, 8);
            }
        }
        finally { reference.ReleaseResources(); }
    });

    private static void AssertTypeface(CaptureOverlayView reference, string field, TextBlock label)
    {
        Typeface typeface = Assert.IsType<Typeface>(typeof(CaptureOverlayView)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reference));
        Assert.Equal(typeface.FontFamily.Source, label.FontFamily.Source);
        Assert.Equal(typeface.Weight, label.FontWeight);
        Assert.Equal(typeface.Style, label.FontStyle);
        Assert.Equal(typeface.Stretch, label.FontStretch);
    }
}
