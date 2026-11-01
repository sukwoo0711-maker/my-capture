using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Platform.Recording;

namespace MyCapture.TutorialGenerator;

/// <summary>
/// Encodes frames to H.264 MP4 with the product's Media Foundation encoder, forced to the
/// software MFT so output does not depend on the GPU. A held frame is written once per 40 ms.
/// The clips are full range: the app's MediaElement player shows studio-range video washed out.
/// </summary>
internal sealed class ClipWriter : IDisposable
{
    private readonly MediaFoundationVideoEncoder _encoder;
    private readonly int _width, _height;
    private int _index;

    internal ClipWriter(string path, int width, int height, int bitrate, int? quality, int? profile)
    {
        _width = width;
        _height = height;
        _encoder = new MediaFoundationVideoEncoder(
            new VideoEncoderOptions(path, width, height, TutorialStoryboard.Fps, bitrate)
            {
                AllowHardwareEncoder = false, ConstantQuality = quality, H264Profile = profile, QualityVsSpeed = quality is null ? null : 100,
                FullRange = true,
            },
            NullLogger<MediaFoundationVideoEncoder>.Instance);
    }

    internal int FramesWritten => _index;

    internal void Write(BitmapSource frame, int repeat)
    {
        if (frame.PixelWidth != _width || frame.PixelHeight != _height)
            throw new InvalidDataException($"Frame {frame.PixelWidth}x{frame.PixelHeight} does not match {_width}x{_height}.");
        BitmapSource bgra = frame.Format == PixelFormats.Bgra32 || frame.Format == PixelFormats.Pbgra32
            ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[_width * _height * 4];
        bgra.CopyPixels(pixels, _width * 4, 0);
        // Stage.Render starts every frame opaque; WPF text over it can report 254.
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 240)
                throw new InvalidDataException($"Every tutorial frame must be opaque; pixel ({i / 4 % _width}, {i / 4 / _width}) has alpha {pixels[i]}.");
            pixels[i] = 255;
        }
        for (int i = 0; i < repeat; i++)
        {
            _encoder.WriteFrame(new EncoderFrame(pixels, _width, _height, _width * 4, _index * (1000.0 / TutorialStoryboard.Fps)));
            _index++;
        }
    }

    internal void Complete() => _encoder.Complete();

    public void Dispose() => _encoder.Dispose();
}

/// <summary>
/// Reads frames back from a finished MP4 with the product's decoder, for review sheets. It asks
/// for hardware decoding: the software path ignores the stream's full-range flag and shows the
/// clips with crushed blacks, unlike the app's player.
/// </summary>
internal static class ClipReview
{
    internal static MediaFoundationVideoFrameReader Open(string mp4) =>
        new(mp4, new MediaFoundationVideoFrameReaderOptions { PreferHardwareDecode = true });

    internal static BitmapSource Frame(MediaFoundationVideoFrameReader reader, double ms)
    {
        DecodedVideoFrame frame = reader.ReadFrameAt(ms);
        byte[] pixels = frame.Pixels;
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        BitmapSource bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, pixels, frame.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A sheet of decoded frames at <paramref name="times"/>, three per row at half size.</summary>
    internal static void SaveSheet(string mp4, IReadOnlyList<(double Ms, string Label)> times, string png)
    {
        using MediaFoundationVideoFrameReader reader = Open(mp4);
        const int columns = 3;
        double cellWidth = reader.Width / 2.0, cellHeight = reader.Height / 2.0 + 22;
        int rows = (times.Count + columns - 1) / columns;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, cellWidth * columns, cellHeight * rows));
            for (int i = 0; i < times.Count; i++)
            {
                BitmapSource frame = Frame(reader, Math.Min(times[i].Ms, reader.DurationMs - 1));
                double x = i % columns * cellWidth, y = i / columns * cellHeight;
                dc.DrawImage(frame, new Rect(x, y + 22, cellWidth, cellHeight - 22));
                dc.DrawText(new FormattedText($"{times[i].Ms / 1000:0.00}s {times[i].Label}", System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.Black, 1), new Point(x + 4, y + 3));
            }
        }

        var sheet = new RenderTargetBitmap((int)(cellWidth * columns), (int)(cellHeight * rows), 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual);
        SavePng(sheet, png);
    }

    /// <summary>One row per clip: decoded first, middle and last frames at guide-preview size.</summary>
    internal static void SaveContactSheet(IReadOnlyList<string> clips, string png)
    {
        if (clips.Count == 0) return;
        const int cellWidth = 480, cellHeight = 298;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, cellWidth * 3, cellHeight * clips.Count));
            for (int row = 0; row < clips.Count; row++)
            {
                using MediaFoundationVideoFrameReader reader = Open(clips[row]);
                double[] times = [0, reader.DurationMs / 2, Math.Max(0, reader.DurationMs - 40)];
                for (int column = 0; column < times.Length; column++)
                {
                    double x = column * cellWidth, y = row * cellHeight;
                    string label = $"{Path.GetFileNameWithoutExtension(clips[row])}  {times[column] / 1000:0.00}s";
                    dc.DrawText(new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.Black, 1), new Point(x + 5, y + 5));
                    dc.DrawImage(Frame(reader, times[column]), new Rect(x, y + 28, cellWidth, 270));
                }
            }
        }
        var sheet = new RenderTargetBitmap(cellWidth * 3, cellHeight * clips.Count, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(visual);
        SavePng(sheet, png);
    }

    /// <summary>
    /// The player's first-frame still: the clip's first rendered frame as JPEG. The player shows
    /// it until the video opens, and the full-range video's first frame matches it.
    /// </summary>
    internal static void SavePoster(BitmapSource firstFrame, string jpg)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = PosterQuality };
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(firstFrame, PixelFormats.Bgr32, null, 0)));
        using var stream = new FileStream(jpg, FileMode.Create, FileAccess.Write);
        encoder.Save(stream);
    }

    internal const int PosterQuality = 90;

    internal static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        encoder.Save(stream);
    }
}
