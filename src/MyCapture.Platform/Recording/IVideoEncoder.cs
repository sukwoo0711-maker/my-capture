namespace MyCapture.Platform.Recording;

/// <summary>Pixel layout of the frames an encoder accepts.</summary>
public enum VideoEncoderInputFormat
{
    /// <summary>Top-down BGRA32 rows of <see cref="EncoderFrame.Stride"/> bytes; alpha is ignored.</summary>
    Bgra32 = 0,

    /// <summary>
    /// Y'CbCr 4:2:0 (NV12): <c>Height</c> luma rows, then <c>Height / 2</c> rows of interleaved
    /// U, V pairs, every row <see cref="EncoderFrame.Stride"/> bytes apart. Width and height must
    /// be even. The samples reach the H.264 encoder unchanged and the stream declares no colour
    /// description, so they must follow the Media Foundation default that the RGB input's own
    /// conversion uses: studio range, BT.709 from 720 coded lines and BT.601 below.
    /// </summary>
    Nv12 = 1,
}

/// <summary>
/// A single frame handed to an encoder, top-down, tightly packed: BGRA32 by default, or NV12
/// for an encoder opened with <see cref="VideoEncoderOptions.InputFormat"/> set to it.
/// </summary>
/// <param name="Pixels">
/// Raw BGRA32 pixels, <c>Stride * Height</c> bytes, top-down row order; for NV12 the luma plane
/// followed by the interleaved chroma plane.
/// </param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="Stride">Bytes per row (<c>Width * 4</c>; <c>Width</c> for packed NV12).</param>
/// <param name="TimestampMs">Presentation time from clip start, in milliseconds.</param>
public readonly record struct EncoderFrame(
    byte[] Pixels,
    int Width,
    int Height,
    int Stride,
    double TimestampMs)
{
    /// <summary>Layout of <see cref="Pixels"/>; must match the encoder's input format.</summary>
    public VideoEncoderInputFormat Format { get; init; }
}

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
    /// Layout every <see cref="IVideoEncoder.WriteFrame"/> call supplies. BGRA32 (the default)
    /// lets Media Foundation convert to Y'CbCr; NV12 skips that conversion, for frames that are
    /// already Y'CbCr such as decoded H.264.
    /// </summary>
    public VideoEncoderInputFormat InputFormat { get; init; }

    /// <summary>
    /// Lets Media Foundation pick a GPU H.264 encoder when one is present (the default). The
    /// tutorial clip generator turns it off so every machine produces the software encoder's
    /// output for the same frames.
    /// </summary>
    public bool AllowHardwareEncoder { get; init; } = true;

    /// <summary>
    /// The H.264 profile to request (Media Foundation eAVEncH264VProfile, for example 100 for
    /// High); null keeps the encoder's default.
    /// </summary>
    public int? H264Profile { get; init; }

    /// <summary>
    /// Constant-quality rate control from 1 to 100 instead of the average bitrate; null keeps the
    /// bitrate mode recordings use. Static screen content then costs almost nothing.
    /// </summary>
    public int? ConstantQuality { get; init; }

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
