using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Tests;

/// <summary>
/// Encodes a screen-recording-like clip: a bright page, dark text bars that scroll a few pixels
/// per frame, and a moving rectangle. Content changes every frame so no sample is trivially
/// skippable by the decoder.
/// </summary>
internal static class SyntheticDocumentClip
{
    internal static RecordingResult Encode(string path, int width, int height, int fps, int frameCount)
    {
        var options = new VideoEncoderOptions(
            path,
            width,
            height,
            fps,
            VideoEncoderOptions.DeriveBitrate(width, height, fps));
        int stride = width * 4;
        var pixels = new byte[stride * height];
        Span<uint> image = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
        using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
        {
            for (int frame = 0; frame < frameCount; frame++)
            {
                Draw(image, width, height, frame);
                encoder.WriteFrame(new EncoderFrame(pixels, width, height, stride, frame * 1000.0 / fps));
            }

            encoder.Complete();
        }

        return new RecordingResult(path, frameCount * 1000.0 / fps, fps, frameCount, width, height);
    }

    private static void Draw(Span<uint> image, int width, int height, int frame)
    {
        const uint Page = 0xFFF4F2EE;
        const uint Ink = 0xFF303030;
        const uint Box = 0xFF2060D0;
        const int LinePitch = 32;
        image.Fill(Page);

        int scroll = frame * 6;
        for (int top = -(scroll % LinePitch); top < height; top += LinePitch)
        {
            int line = (top + scroll) / LinePitch;
            uint seed = unchecked((uint)(line * 7919) + 17);
            int x = width / 24;
            while (x < width - (width / 24))
            {
                seed = unchecked((seed * 1103515245) + 12345);
                int word = Math.Min(30 + (int)((seed >> 16) & 0x7F), width - (width / 24) - x);
                for (int y = Math.Max(0, top + 9); y < Math.Min(height, top + 21); y++)
                {
                    image.Slice((y * width) + x, word).Fill(Ink);
                }

                x += word + 14;
            }
        }

        int boxWidth = width / 8;
        int boxHeight = height / 6;
        int boxX = (frame * 14) % Math.Max(1, width - boxWidth);
        int boxY = Math.Clamp(
            (height / 2) - (boxHeight / 2) + (int)(Math.Sin(frame / 15.0) * height / 5),
            0,
            height - boxHeight);
        for (int y = boxY; y < boxY + boxHeight; y++)
        {
            image.Slice((y * width) + boxX, boxWidth).Fill(Box);
        }
    }
}
