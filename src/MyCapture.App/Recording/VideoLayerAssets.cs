using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

internal static class VideoLayerAssets
{
    internal static BitmapSource CreateShape(bool ellipse)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            var fill = new SolidColorBrush(Color.FromArgb(110, 30, 160, 255));
            var pen = new Pen(Brushes.DeepSkyBlue, 5);
            if (ellipse) { dc.DrawEllipse(fill, pen, new Point(128, 96), 124, 92); }
            else { dc.DrawRectangle(fill, pen, new Rect(4, 4, 248, 184)); }
        }
        var bitmap = new RenderTargetBitmap(256, 192, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    internal static BitmapSource ReadImage(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 24 * 1024 * 1024) { throw new InvalidDataException(UiText.Get("Text_9C4458907087")); }
        if (stream.Length >= 33)
        {
            Span<byte> header = stackalloc byte[33];
            stream.ReadExactly(header);
            stream.Position = 0;
            if (header[0] == 137 && header[1] == 80 && !FrameEditLayerRenderer.HasSafePngDimensions(header))
            {
                throw new InvalidDataException(UiText.Get("Text_6C7D2ADB7EB3"));
            }
        }
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        BitmapFrame frame = decoder.Frames[0];
        if (frame.PixelWidth is <= 0 or > 8192 || frame.PixelHeight is <= 0 or > 8192
            || (long)frame.PixelWidth * frame.PixelHeight > 16_777_216)
        {
            throw new InvalidDataException(UiText.Get("Text_2B7B1B697484"));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int stride = checked(frame.PixelWidth * 4);
        var pixels = new byte[checked(stride * frame.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        cancellationToken.ThrowIfCancellationRequested();
        BitmapSource bitmap = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    internal static FrameEditLayer CreateLayer(BitmapSource image, string name, double time, double duration, int width, int height)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        string encoded = Convert.ToBase64String(stream.ToArray());
        if (encoded.Length > VideoEditDocument.MaximumFrameLayerEncodedLength)
        {
            throw new InvalidDataException(UiText.Get("Text_E1E40E743D3B"));
        }
        double layerWidth = Math.Min(width * 0.4, image.PixelWidth);
        double layerHeight = layerWidth * image.PixelHeight / image.PixelWidth;
        if (layerHeight > height * 0.6) { layerWidth *= height * 0.6 / layerHeight; layerHeight = height * 0.6; }
        double start = Math.Clamp(time, 0, Math.Max(0, duration - 1));
        return new FrameEditLayer
        {
            Name = name,
            StartMs = start,
            EndMs = Math.Min(duration, start + 3000),
            OverlayPngBase64 = encoded,
            Bounds = new((width - layerWidth) / (2 * width), (height - layerHeight) / (2 * height), layerWidth / width, layerHeight / height),
        };
    }
}
