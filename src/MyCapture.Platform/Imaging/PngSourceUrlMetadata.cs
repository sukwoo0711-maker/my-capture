using System.IO;
using System.Text;

namespace MyCapture.Platform.Imaging;

/// <summary>
/// Reads and writes the source page URL as a PNG <c>tEXt</c> chunk under the
/// <see cref="Keyword"/> keyword.
/// </summary>
/// <remarks>
/// <para>
/// The queue record already stores the source page URL, but a record only exists inside
/// MyCapture. A tEXt chunk keeps the link attached to the bytes when a capture leaves the
/// app — file save, drag out, clipboard, or a shared file — so the origin can still be
/// recovered later.
/// </para>
/// <para>
/// The PNG spec restricts tEXt keywords and text to Latin-1, so callers pass a value that
/// <c>MyCapture.Core.Capture.SourcePageUrl.Normalize</c> already accepted
/// (<see cref="Uri.AbsoluteUri"/> percent-encodes non-ASCII). Anything outside Latin-1 is
/// skipped rather than written lossily.
/// </para>
/// </remarks>
public static class PngSourceUrlMetadata
{
    /// <summary>Keyword of the chunk that carries the capture's source page URL.</summary>
    public const string Keyword = "SourceURL";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private const int ChunkHeaderLength = 8;   // length + type
    private const int ChunkCrcLength = 4;
    private const int IhdrDataLength = 13;

    /// <summary>
    /// Returns <paramref name="png"/> with a tEXt chunk carrying <paramref name="sourceUrl"/>
    /// inserted directly after IHDR. The original array is returned when the URL is empty,
    /// not Latin-1, or the input is not a well-formed PNG.
    /// </summary>
    public static byte[] WithSourceUrl(byte[] png, string? sourceUrl)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (string.IsNullOrWhiteSpace(sourceUrl) || !IsLatin1(sourceUrl))
        {
            return png;
        }

        int insertAt = IhdrEnd(png);
        if (insertAt < 0)
        {
            return png;
        }

        byte[] chunk = BuildTextChunk(sourceUrl);
        byte[] result = new byte[png.Length + chunk.Length];
        Buffer.BlockCopy(png, 0, result, 0, insertAt);
        Buffer.BlockCopy(chunk, 0, result, insertAt, chunk.Length);
        Buffer.BlockCopy(png, insertAt, result, insertAt + chunk.Length, png.Length - insertAt);
        return result;
    }

    /// <summary>Reads the source page URL from PNG bytes, or an empty string when absent.</summary>
    public static string TryReadSourceUrl(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length < PngSignature.Length || !png.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
        {
            return string.Empty;
        }

        int offset = PngSignature.Length;
        while ((long)offset + ChunkHeaderLength + ChunkCrcLength <= png.Length)
        {
            // Length is a uint. Add it in 64 bits: a corrupt chunk must not wrap the
            // bounds check and walk off the buffer, or spin, on a clipboard PNG.
            long length = ReadUInt32BigEndian(png, offset);
            long chunkEnd = (long)offset + ChunkHeaderLength + length + ChunkCrcLength;
            if (length < 0 || chunkEnd < offset || chunkEnd > png.Length)
            {
                return string.Empty;
            }

            int dataLength = (int)length;
            int dataOffset = offset + ChunkHeaderLength;

            string type = Encoding.ASCII.GetString(png, offset + 4, 4);
            if (string.Equals(type, "tEXt", StringComparison.Ordinal))
            {
                string? value = ReadTextChunk(png, dataOffset, dataLength);
                if (value is not null)
                {
                    return value;
                }
            }
            else if (string.Equals(type, "IDAT", StringComparison.Ordinal)
                     || string.Equals(type, "IEND", StringComparison.Ordinal))
            {
                // tEXt must precede the image data; stop instead of walking the whole file.
                return string.Empty;
            }

            offset = (int)chunkEnd;
        }

        return string.Empty;
    }

    /// <summary>Reads the source page URL from a PNG file, or an empty string when absent.</summary>
    public static string TryReadSourceUrl(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            return TryReadSourceUrl(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private static string? ReadTextChunk(byte[] png, int dataOffset, int dataLength)
    {
        int separator = -1;
        for (int i = 0; i < dataLength; i++)
        {
            if (png[dataOffset + i] == 0)
            {
                separator = i;
                break;
            }
        }

        if (separator <= 0 || separator >= dataLength - 1)
        {
            return null;
        }

        string keyword = Encoding.Latin1.GetString(png, dataOffset, separator);
        return string.Equals(keyword, Keyword, StringComparison.Ordinal)
            ? Encoding.Latin1.GetString(png, dataOffset + separator + 1, dataLength - separator - 1)
            : null;
    }

    private static byte[] BuildTextChunk(string sourceUrl)
    {
        byte[] keyword = Encoding.Latin1.GetBytes(Keyword);
        byte[] text = Encoding.Latin1.GetBytes(sourceUrl);
        int dataLength = keyword.Length + 1 + text.Length;

        byte[] chunk = new byte[ChunkHeaderLength + dataLength + ChunkCrcLength];
        WriteUInt32BigEndian(chunk, 0, (uint)dataLength);
        chunk[4] = (byte)'t';
        chunk[5] = (byte)'E';
        chunk[6] = (byte)'X';
        chunk[7] = (byte)'t';
        keyword.CopyTo(chunk, ChunkHeaderLength);
        chunk[ChunkHeaderLength + keyword.Length] = 0;
        text.CopyTo(chunk, ChunkHeaderLength + keyword.Length + 1);
        WriteUInt32BigEndian(chunk, chunk.Length - ChunkCrcLength, Crc32(chunk, 4, 4 + dataLength));
        return chunk;
    }

    /// <summary>Offset just past the IHDR chunk, or -1 when the input is not a PNG with IHDR first.</summary>
    private static int IhdrEnd(byte[] png)
    {
        if (png.Length < PngSignature.Length + ChunkHeaderLength + IhdrDataLength + ChunkCrcLength
            || !png.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
        {
            return -1;
        }

        int offset = PngSignature.Length;
        if (ReadUInt32BigEndian(png, offset) != IhdrDataLength
            || !png.AsSpan(offset + 4, 4).SequenceEqual("IHDR"u8))
        {
            return -1;
        }

        return offset + ChunkHeaderLength + IhdrDataLength + ChunkCrcLength;
    }

    private static bool IsLatin1(string value)
    {
        foreach (char c in value)
        {
            if (c > 0xFF)
            {
                return false;
            }
        }

        return true;
    }

    private static long ReadUInt32BigEndian(byte[] buffer, int offset) =>
        ((long)buffer[offset] << 24) | ((long)buffer[offset + 1] << 16)
        | ((long)buffer[offset + 2] << 8) | buffer[offset + 3];

    private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] buffer, int offset, int count)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = offset; i < offset + count; i++)
        {
            crc ^= buffer[i];
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
            }
        }

        return ~crc;
    }
}
