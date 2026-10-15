using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Settings;

namespace MyCapture.App.Editing;

internal static class ImageExportRenderer
{
    internal static BitmapSource Render(BitmapSource image, ImageExportOptions options, Color accent)
    {
        options = options.Normalize();
        (int width, int height) = ImageExportOptions.Fit(image.PixelWidth, image.PixelHeight, options.LongEdge);
        if (width == image.PixelWidth && height == image.PixelHeight && options.Margin == 0
            && options.CornerRadius == 0 && options.Background == ImageExportBackground.Transparent && !options.Shadow)
            return image;
        (int outputWidth, int outputHeight) = options.OutputSize(image.PixelWidth, image.PixelHeight);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (DrawingContext drawing = visual.RenderOpen())
        {
            Color background = options.Background switch
            {
                ImageExportBackground.White => Colors.White,
                ImageExportBackground.LightGray => Color.FromRgb(240, 240, 240),
                ImageExportBackground.DarkGray => Color.FromRgb(48, 48, 48),
                ImageExportBackground.Accent => accent,
                _ => Colors.Transparent,
            };
            drawing.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, outputWidth, outputHeight));
            var bounds = new Rect(options.Margin, options.Margin, width, height);
            double radius = Math.Min(options.CornerRadius, Math.Min(width, height) / 2d);
            if (options.Shadow && options.Margin > 0)
            {
                // Layered translucent contours keep the shadow inside the requested canvas.
                int spread = Math.Min(12, options.Margin);
                for (int i = spread; i >= 1; i--)
                {
                    var shadow = bounds;
                    shadow.Inflate(i, i);
                    drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(7, 0, 0, 0)), null,
                        shadow, radius + i, radius + i);
                }
            }
            drawing.PushClip(new RectangleGeometry(bounds, radius, radius));
            drawing.DrawImage(image, bounds);
            drawing.Pop();
        }
        var result = new RenderTargetBitmap(outputWidth, outputHeight, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }
}
