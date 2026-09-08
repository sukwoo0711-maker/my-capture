using System.Buffers.Binary;
using MyCapture.Core.Recording;

namespace MyCapture.App.Recording;

internal sealed class VideoLayerLimitException(string message) : InvalidOperationException(message);

/// <summary>Checks the complete asset set before normalization, decoding, mutation or export.</summary>
internal static class VideoLayerResourceBudget
{
    internal const long MaximumEncodedCharacters = 64 * 1024 * 1024;
    internal const long MaximumDecodedBytes = 256 * 1024 * 1024;
    internal const long MaximumDocumentFileBytes = 72 * 1024 * 1024;

    internal static void Validate(IReadOnlyList<FrameEditLayer>? layers)
    {
        if (layers is null) { return; }
        if (layers.Count > VideoEditDocument.MaximumFrameLayerCount)
        {
            throw new VideoLayerLimitException(UiText.Get("Text_93B88ACD46F6"));
        }
        long encoded = 0;
        long decoded = 0;
        Span<char> prefix = stackalloc char[44];
        Span<byte> header = stackalloc byte[33];
        foreach (FrameEditLayer layer in layers)
        {
            string payload = layer?.OverlayPngBase64 ?? string.Empty;
            encoded += payload.Length;
            if (payload.Length > VideoEditDocument.MaximumFrameLayerEncodedLength || encoded > MaximumEncodedCharacters)
            {
                throw new VideoLayerLimitException(UiText.Get("Text_1DCD20923815"));
            }
            int count = 0;
            int inspected = 0;
            foreach (char character in payload)
            {
                if (++inspected > 4096)
                {
                    throw new VideoLayerLimitException(UiText.Get("Text_460404601E2D"));
                }
                if (char.IsWhiteSpace(character)) { continue; }
                prefix[count++] = character;
                if (count == prefix.Length) { break; }
            }
            if (count != prefix.Length || !Convert.TryFromBase64Chars(prefix, header, out int bytes) || bytes != 33
                || !header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                || !header.Slice(12, 4).SequenceEqual("IHDR"u8)) { continue; }
            if (!FrameEditLayerRenderer.HasSafePngDimensions(header))
            {
                throw new VideoLayerLimitException(UiText.Get("Text_62AE4531CA74"));
            }
            long width = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4));
            long height = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20, 4));
            decoded += width * height * (header[24] == 16 ? 8 : 4);
            if (decoded > MaximumDecodedBytes)
            {
                throw new VideoLayerLimitException(UiText.Get("Text_9984B98A4667"));
            }
        }
    }

    internal static void ValidateFileLength(long length)
    {
        if (length > MaximumDocumentFileBytes)
        {
            throw new VideoLayerLimitException(UiText.Get("Text_2CDB4215BF83"));
        }
    }
}
