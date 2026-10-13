using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureOverlayPrewarmTests
{
    [Fact]
    public void Prewarm_RendersOffScreen_AndLeavesTheDesktopGuideStripsCached() => StaTestHost.Run(() =>
    {
        // An odd size no other test uses, so the strips can only come from this prewarm.
        var desktop = new RectD(-17, 3, 1987, 1103);
        CaptureOverlayPrewarm.Run(desktop);
        IReadOnlyDictionary<string, Brush> palette = CaptureGuidePalette.Resolve();
        Brush shadow = palette["CaptureGuide.LineShadow"], line = palette["CaptureGuide.Line"];
        BitmapSource horizontal = CaptureGuideStrip.Get(shadow, line, 1987, horizontal: true);
        BitmapSource vertical = CaptureGuideStrip.Get(shadow, line, 1103, horizontal: false);
        if (shadow is SolidColorBrush && line is SolidColorBrush)
        {
            // A second prewarm reuses them rather than rasterising again.
            CaptureOverlayPrewarm.Run(desktop);
            Assert.Same(horizontal, CaptureGuideStrip.Get(shadow, line, 1987, horizontal: true));
            Assert.Same(vertical, CaptureGuideStrip.Get(shadow, line, 1103, horizontal: false));
        }
        Assert.Equal(1987, horizontal.PixelWidth);
        Assert.Equal(1103, vertical.PixelHeight);
    });
}
