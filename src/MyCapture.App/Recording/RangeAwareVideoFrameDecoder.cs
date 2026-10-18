using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// Reads native YUV before RGB conversion so full-range imports are not expanded as studio
/// range by Media Foundation's RGB32 converter. The same fixed-point converter is used by GIF
/// export; its studio-range output reproduces the software RGB32 path byte for byte.
/// </summary>
internal static class RangeAwareVideoFrameDecoder
{
    internal static MediaFoundationVideoFrameReader Open(string sourcePath) =>
        new(sourcePath, new MediaFoundationVideoFrameReaderOptions
        {
            OutputFormat = DecodedVideoPixelFormat.Nv12,
        });

    /// <summary>
    /// Returns opaque BGR32 at native size, preserving sample timing. Conversion precedes WPF
    /// scaling/composition. A source without NV12 output keeps the reader's RGB fallback.
    /// </summary>
    internal static DecodedVideoFrame ReadBgr32(
        MediaFoundationVideoFrameReader reader,
        double sourceTimeMs,
        CancellationToken cancellationToken = default)
    {
        DecodedVideoFrame frame = reader.ReadFrameAt(sourceTimeMs, cancellationToken);
        if (frame.Format != DecodedVideoPixelFormat.Nv12)
        {
            return frame;
        }

        return frame with
        {
            Pixels = Nv12FrameConverter.ToBgra(
                frame.Pixels, frame.Width, frame.Height, reader.YuvEncoding, frame.Width, frame.Height),
            Stride = checked(frame.Width * 4),
            Format = DecodedVideoPixelFormat.Bgr32,
        };
    }
}
