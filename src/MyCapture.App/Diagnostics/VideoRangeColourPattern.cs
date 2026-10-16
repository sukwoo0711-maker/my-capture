using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Diagnostics;

/// <summary>The exact 8x2 RangePatternClip fixture, shared by visual regression tests and H7.</summary>
internal static class VideoRangeColourPattern
{
    internal static readonly (string Name, byte R, byte G, byte B)[] Patches =
    [
        ("black", 0, 0, 0), ("grey 16", 16, 16, 16), ("grey 64", 64, 64, 64), ("grey 128", 128, 128, 128),
        ("grey 192", 192, 192, 192), ("grey 235", 235, 235, 235), ("white", 255, 255, 255), ("red", 255, 0, 0),
        ("green", 0, 255, 0), ("blue", 0, 0, 255), ("yellow", 255, 255, 0), ("cyan", 0, 255, 255),
        ("magenta", 255, 0, 255), ("orange", 0xF2, 0xB1, 0x34), ("navy", 0x1D, 0x2A, 0x3C), ("accent", 0x2E, 0x6F, 0xD8),
    ];

    internal static string Encode(string path, int width, int height, bool fullRange)
    {
        int stride = width * 4;
        byte[] frame = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (_, byte r, byte g, byte b) = Patches[(y * 2 / height * 8) + (x * 8 / width)];
                int o = (y * stride) + (x * 4);
                frame[o] = b;
                frame[o + 1] = g;
                frame[o + 2] = r;
                frame[o + 3] = 255;
            }
        }

        var options = new VideoEncoderOptions(path, width, height, 30, VideoEncoderOptions.DeriveBitrate(width, height, 30))
        {
            FullRange = fullRange,
        };
        using var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance);
        for (int i = 0; i < 30; i++)
        {
            encoder.WriteFrame(new EncoderFrame(frame, width, height, stride, i * 1000.0 / 30));
        }

        encoder.Complete();
        return path;
    }

    internal static VideoRangeObservation Observe(MediaElement media)
    {
        const int width = 320, height = 180;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(media) { Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        byte[] bgra = new byte[Patches.Length * 4];
        for (int i = 0; i < Patches.Length; i++)
            bitmap.CopyPixels(new Int32Rect((2 * (i % 8) + 1) * width / 16,
                (2 * (i / 8) + 1) * height / 4, 1, 1), bgra, 4, i * 4);
        return Observe(bgra);
    }

    internal static VideoRangeObservation Observe(byte[] bgra)
    {
        if (bgra.Length != Patches.Length * 4) throw new ArgumentException("Sixteen BGRA patches are required.", nameof(bgra));
        var patches = new VideoRangePatch[Patches.Length];
        for (int i = 0; i < patches.Length; i++)
            patches[i] = new(bgra[i * 4 + 2], bgra[i * 4 + 1], bgra[i * 4], bgra[i * 4 + 3]);
        // Identical 0/64/192/255 greys allow an H7 comparison to the bar fixture. All sixteen
        // raw RGBA patches remain in the report, including channels affected by chroma conversion.
        byte[] greys = new byte[16];
        for (int i = 0; i < 4; i++) Array.Copy(bgra, i * 8, greys, i * 4, 4);
        return new(patches, StudioRangeCalibrationProbe.Classify(greys)?.ToString() ?? "unknown");
    }
}
