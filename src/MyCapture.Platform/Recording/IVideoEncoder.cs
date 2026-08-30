namespace MyCapture.Platform.Recording;

/// <summary>
/// A single BGRA32 frame handed to an encoder, top-down, tightly packed.
/// </summary>
/// <param name="Pixels">
/// Raw BGRA32 pixels, <c>Stride * Height</c> bytes, top-down row order.
/// </param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="Stride">Bytes per row (<c>Width * 4</c>).</param>
/// <param name="TimestampMs">Presentation time from clip start, in milliseconds.</param>
public readonly record struct EncoderFrame(
    byte[] Pixels,
    int Width,
    int Height,
    int Stride,
    double TimestampMs);

/// <summary>
/// Sink for recorded frames. Kept as an interface — like <c>IHotkeyRegistrar</c> — so
/// the recorder's pacing and drop logic can be exercised with a fake that records
/// timestamps, never touching Media Foundation.
/// </summary>
/// <remarks>
/// Implementations are not thread-safe; the recorder drives one from a single
/// dedicated capture thread.
/// </remarks>
public interface IVideoEncoder : IDisposable
{
    /// <summary>Frame width the encoder was opened for.</summary>
    int Width { get; }

    /// <summary>Frame height the encoder was opened for.</summary>
    int Height { get; }

    /// <summary>Writes one frame. Timestamp is milliseconds from clip start.</summary>
    void WriteFrame(in EncoderFrame frame);

    /// <summary>Flushes and finalises the output file. Safe to call once.</summary>
    void Complete();
}

/// <summary>
/// Parameters an encoder is opened with.
/// </summary>
public sealed record VideoEncoderOptions(
    string OutputPath,
    int Width,
    int Height,
    int Fps,
    int BitrateBitsPerSecond)
{
    /// <summary>
    /// A frame-size- and rate-aware default bitrate when none is specified.
    /// </summary>
    /// <remarks>
    /// Roughly 0.1 bits per pixel per frame, floored at 1 Mbps and capped at 24 Mbps.
    /// Screen content is highly compressible (large flat regions, repeated frames),
    /// so this stays small for UI clips while keeping text crisp on large regions.
    /// </remarks>
    public static int DeriveBitrate(int width, int height, int fps)
    {
        long perFrame = (long)Math.Max(1, width) * Math.Max(1, height);
        long bits = (long)(perFrame * Math.Max(1, fps) * 0.1);
        return (int)Math.Clamp(bits, 1_000_000, 24_000_000);
    }
}
