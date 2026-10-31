using MyCapture.App.Themes;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

internal static class RecordingWatermarkAssets
{
    internal static BitmapSource? Load(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        string? path = StampStore.RegisteredPath(folder);
        if (path is null) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource image = decoder.Frames[0];
        image.Freeze();
        return image;
    }
}

/// <summary>Independent frozen pixels or vector text, held unchanged until the encoder finishes.</summary>
internal sealed class RecordingWatermarkSnapshot
{
    private RecordingWatermarkSnapshot(RecordingWatermarkSettings settings, ImageSource content, double width, double height)
    {
        Settings = settings;
        Content = content;
        SourceWidth = width;
        SourceHeight = height;
    }

    internal RecordingWatermarkSettings Settings { get; }
    internal ImageSource Content { get; }
    internal double SourceWidth { get; }
    internal double SourceHeight { get; }
    internal RectD Place(RectD region) => Settings.Place(region.Width, region.Height, SourceWidth, SourceHeight);

    internal static RecordingWatermarkSnapshot Create(RecordingWatermarkSettings value, BitmapSource? image)
    {
        RecordingWatermarkSettings settings = value.Normalize();
        if (settings.Source == RecordingWatermarkSource.Image)
        {
            if (image is null || image.PixelWidth <= 0 || image.PixelHeight <= 0)
                throw new InvalidOperationException(UiText.Get("RecordingWatermark_ImageError"));
            int stride = checked((image.PixelWidth * image.Format.BitsPerPixel + 7) / 8);
            byte[] pixels = new byte[checked(stride * image.PixelHeight)];
            image.CopyPixels(pixels, stride, 0);
            BitmapSource copy = BitmapSource.Create(image.PixelWidth, image.PixelHeight, 96, 96,
                image.Format, image.Palette, pixels, stride);
            copy.Freeze();
            return new(settings, copy, copy.PixelWidth, copy.PixelHeight);
        }

        if (string.IsNullOrWhiteSpace(settings.Text))
            throw new InvalidOperationException(UiText.Get("RecordingWatermark_TextError"));
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(settings.TextColor));
        brush.Freeze();
        var formatted = new FormattedText(settings.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), TypeScale.Content.WatermarkEmSize, brush, 1);
        Geometry glyphs = formatted.BuildGeometry(new Point(0, 0));
        var drawing = new GeometryDrawing(brush, null, glyphs);
        drawing.Freeze();
        var vector = new DrawingImage(drawing);
        vector.Freeze();
        return new(settings, vector, Math.Max(1, vector.Width), Math.Max(1, vector.Height));
    }
}
