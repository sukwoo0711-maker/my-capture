using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
            // The size read-out: the UI face with tabular figures, so its digits keep one advance.
            TextBlock readout = Assert.IsType<TextBlock>(Assert.IsAssignableFrom<Border>(chrome.Windows[1].Content).Child);
            Assert.Equal(TypeScale.Caption, readout.FontSize);
            AssertTypeface(reference, "_uiTypeface", readout);
            Assert.Equal(FontNumeralAlignment.Tabular, Typography.GetNumeralAlignment(readout));
            // The hint: every word, Korean included, at the Caption size; only Latin key caps at Micro.
            var hint = Assert.IsType<NativeSelectorHint>(Assert.IsType<Border>(chrome.Windows[2].Content).Child);
            Typeface ui = UiTypeface(reference);
            Assert.NotEmpty(hint.WordsForTest);
            foreach (TextBlock word in hint.WordsForTest)
            {
                Assert.Equal(TypeScale.Caption, word.FontSize);
                Assert.Equal(ui.FontFamily.Source, word.FontFamily.Source);
                Assert.Equal(FontWeights.Medium, word.FontWeight);
            }
            Assert.NotEmpty(hint.KeycapBordersForTest);
            foreach (Border cap in hint.KeycapBordersForTest)
            {
                var key = Assert.IsType<TextBlock>(cap.Child);
                Assert.Equal(TypeScale.Micro, key.FontSize);
                Assert.Equal(FontWeights.SemiBold, key.FontWeight);
                Assert.Matches("^[A-Za-z0-9+←↑→↓]+$", key.Text);
            }
            foreach ((Window window, TextBlock label) in new[] { (chrome.Windows[1], readout), (chrome.Windows[2], hint.WordsForTest[0]) })
            {
                var content = Assert.IsAssignableFrom<Border>(window.Content);
                var transform = Assert.IsType<ScaleTransform>(content.LayoutTransform);
                Matrix device = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!.CompositionTarget.TransformToDevice;
                Assert.Equal(TypeScale.Caption * dpi / 96d, label.FontSize * transform.ScaleY * device.M22, 8);
            }
        }
        finally { reference.ReleaseResources(); }
    });

    private static Typeface UiTypeface(CaptureOverlayView reference) => Assert.IsType<Typeface>(typeof(CaptureOverlayView)
        .GetField("_uiTypeface", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reference));

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
