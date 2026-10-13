using Microsoft.Extensions.Logging;

namespace MyCapture.Platform.Recording;

/// <summary>Optional decoder configuration for <see cref="MediaFoundationVideoFrameReader"/>.</summary>
/// <remarks>
/// The default instance reproduces the original software reader exactly: CPU decode, the basic
/// Source Reader colour converter and native output size.
/// </remarks>
public sealed record MediaFoundationVideoFrameReaderOptions
{
    public static MediaFoundationVideoFrameReaderOptions Default { get; } = new();

    /// <summary>
    /// Attaches a D3D11 device manager so the decoder MFT can use DXVA and the advanced video
    /// processor converts (and optionally resizes) frames on the GPU. When any part of that
    /// setup fails the reader silently uses the original software path and records why in
    /// <see cref="MediaFoundationVideoFrameReader.HardwareFallbackReason"/>.
    /// </summary>
    public bool PreferHardwareDecode { get; init; }

    /// <summary>
    /// Requested output width in pixels, or zero for the source display width. Only the
    /// hardware video processor honours a resize; check <see cref="MediaFoundationVideoFrameReader.Width"/>
    /// and rescale when it still reports the source size.
    /// </summary>
    public int OutputWidth { get; init; }

    /// <summary>Requested output height in pixels, or zero for the source display height.</summary>
    public int OutputHeight { get; init; }

    /// <summary>Receives a warning when hardware decode was requested but is unavailable.</summary>
    public ILogger? Log { get; init; }

    /// <summary>
    /// Requested frame layout. <see cref="DecodedVideoPixelFormat.Nv12"/> returns the decoder's
    /// own YUV output (bit-exact between GPU and CPU H.264 decode) and skips every RGB
    /// converter, so the caller can convert only the frames it keeps. Frames report their actual
    /// <see cref="DecodedVideoFrame.Format"/>: a decoder that cannot produce NV12 still yields
    /// BGR32. A resize is only honoured for BGR32.
    /// </summary>
    public DecodedVideoPixelFormat OutputFormat { get; init; }
}

/// <summary>Pixel layout of <see cref="DecodedVideoFrame.Pixels"/>.</summary>
public enum DecodedVideoPixelFormat
{
    /// <summary>Top-down 32-bit B, G, R, X rows of <see cref="DecodedVideoFrame.Stride"/> bytes.</summary>
    Bgr32 = 0,

    /// <summary>
    /// Packed 4:2:0: <c>Width x Height</c> luma bytes (stride <c>Width</c>), then
    /// <c>ceil(Height / 2)</c> rows of interleaved U, V pairs, <c>2 x ceil(Width / 2)</c> bytes each.
    /// </summary>
    Nv12 = 1,
}

/// <summary>Y'CbCr matrix and range a decoded NV12 stream declares (or the decoder assumes).</summary>
public readonly record struct VideoYuvEncoding(bool Bt709, bool FullRange);

/// <summary>
/// What a player needs to know about a clip before it presents it: the exact container duration
/// (0 when none is advertised) and whether the video is studio range (16–235), which WPF's
/// MediaElement presents without expanding to full range.
/// </summary>
public readonly record struct VideoSourceInfo(double DurationMs, bool StudioRange);
