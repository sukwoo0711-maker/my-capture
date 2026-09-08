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
            throw new VideoLayerLimitException("영상 그래픽 레이어는 최대 100개입니다. 레이어를 줄여 주세요.");
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
                throw new VideoLayerLimitException("영상 이미지 데이터가 너무 큽니다. 전체 레이어 데이터는 64MB 이하여야 합니다. 이미지 크기나 레이어 수를 줄여 주세요.");
            }
            int count = 0;
            int inspected = 0;
            foreach (char character in payload)
            {
                if (++inspected > 4096)
                {
                    throw new VideoLayerLimitException("이미지 레이어 데이터의 헤더에 공백이 너무 많습니다. 이미지를 다시 추가해 주세요.");
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
                throw new VideoLayerLimitException("이미지 레이어의 크기가 너무 큽니다. 레이어 하나는 최대 8192px, 디코딩 메모리 64MB 이하여야 합니다.");
            }
            long width = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4));
            long height = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20, 4));
            decoded += width * height * (header[24] == 16 ? 8 : 4);
            if (decoded > MaximumDecodedBytes)
            {
                throw new VideoLayerLimitException("영상 이미지 레이어의 메모리가 256MB를 초과합니다. 이미지 크기나 레이어 수를 줄인 뒤 다시 시도해 주세요.");
            }
        }
    }

    internal static void ValidateFileLength(long length)
    {
        if (length > MaximumDocumentFileBytes)
        {
            throw new VideoLayerLimitException("영상 편집 파일이 72MB를 초과하여 열 수 없습니다. 원본 영상과 편집 파일은 변경되지 않았습니다.");
        }
    }
}
