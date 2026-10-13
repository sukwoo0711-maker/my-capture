using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Tests;

/// <summary>
/// A clip whose every frame carries its own index as a 12-bit bar code across the top of the
/// picture, so a test can read which frame a player presents from any rendering of the video.
/// </summary>
internal static class FrameIndexClip
{
    private const int Bits = 12;

    /// <summary>Encodes one frame per timestamp; the stream ends one nominal frame after the last.</summary>
    internal static void Encode(string path, int width, int height, int fps, IReadOnlyList<double> timestampsMs)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        var options = new VideoEncoderOptions(path, width, height, fps, VideoEncoderOptions.DeriveBitrate(width, height, fps));
        using var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance);
        for (int index = 0; index < timestampsMs.Count; index++)
        {
            Draw(pixels, width, height, stride, index);
            encoder.WriteFrame(new EncoderFrame(pixels, width, height, stride, timestampsMs[index]));
        }

        encoder.Complete();
    }

    /// <summary>Evenly spaced frames at <paramref name="fps"/>.</summary>
    internal static double[] Constant(int fps, int frames) =>
        [.. Enumerable.Range(0, frames).Select(i => i * 1000.0 / fps)];

    /// <summary>Reads the index of the frame shown in <paramref name="bitmap"/> (a rendering of the whole frame).</summary>
    internal static int Read(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        return Read(pixels, width, height, stride);
    }

    /// <summary>Reads the index from top-down 32-bit BGR(A) pixels, such as a decoded frame.</summary>
    internal static int Read(byte[] pixels, int width, int height, int stride)
    {
        int index = 0;
        int y = height / 12;
        for (int bit = 0; bit < Bits; bit++)
        {
            int x = (int)((bit + 0.5) * width / Bits);
            if (pixels[(y * stride) + (x * 4) + 1] > 128)
            {
                index |= 1 << (Bits - 1 - bit);
            }
        }

        return index;
    }

    private static void Draw(byte[] pixels, int width, int height, int stride, int index)
    {
        int band = height / 6;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * stride) + (x * 4);
                byte value;
                if (y < band)
                {
                    int bit = x * Bits / width;
                    value = ((index >> (Bits - 1 - bit)) & 1) == 1 ? (byte)255 : (byte)0;
                    pixels[o] = pixels[o + 1] = pixels[o + 2] = value;
                }
                else
                {
                    // Moving diagonal texture below the code, so successive frames really differ.
                    value = (byte)(((x + y + (index * 7)) & 0x3F) + 64);
                    pixels[o] = value;
                    pixels[o + 1] = (byte)(255 - value);
                    pixels[o + 2] = (byte)(index * 3);
                }

                pixels[o + 3] = 255;
            }
        }
    }
}
