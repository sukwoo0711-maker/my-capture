using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;

namespace MyCapture.App.Capture;

/// <summary>
/// Builds the selector's view once, off screen, while the resident app is idle after launch, so the
/// first capture does not pay first-use costs on the hotkey: the mode bar's templates and fonts, the
/// layout and drawing code, and the guide strips for this desktop.
/// </summary>
internal static class CaptureOverlayPrewarm
{
    private const int Width = 96, Height = 54;

    internal static void Run(RectD desktop)
    {
        RectD pixels = desktop.ToPixelBounds();
        IReadOnlyDictionary<string, Brush> palette = CaptureGuidePalette.Resolve();
        Brush shadow = palette["CaptureGuide.LineShadow"], line = palette["CaptureGuide.Line"];
        if (pixels.Width >= 1 && pixels.Height >= 1)
        {
            _ = CaptureGuideStrip.Get(shadow, line, (int)pixels.Width, horizontal: true);
            _ = CaptureGuideStrip.Get(shadow, line, (int)pixels.Height, horizontal: false);
        }

        var view = new CaptureOverlayView(Frame(), showMagnifier: true)
        {
            ReadCursor = () => new PointD(Width / 2, Height / 2),
            PlaceCursor = _ => false,
            SimulatedPointerInside = true,
            ShowModeBar = true,
        };
        try
        {
            view.InitializePointer();
            view.Measure(new Size(Width, Height));
            view.Arrange(new Rect(0, 0, Width, Height));
            var target = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
            target.Render(view);
        }
        finally
        {
            view.ReleaseResources();
        }
    }

    private static FrozenFrame Frame()
    {
        BitmapSource bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null,
            new byte[Width * Height * 4], Width * 4);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, new RectD(0, 0, Width, Height), null, 0);
    }
}
