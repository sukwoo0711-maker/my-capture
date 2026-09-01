using System.Buffers.Binary;
using System.IO;
using System.Windows.Media.Imaging;

namespace MyCapture.App.Recording;

/// <summary>
/// Writes a fixed-size animated GIF without retaining previously added frames.
/// </summary>
/// <remarks>
/// <para>
/// WPF's <see cref="GifBitmapEncoder"/> provides the platform palette quantizer and LZW
/// encoder, but it cannot attach dependable per-frame timing metadata. Each source frame is
/// therefore encoded as one temporary, single-frame GIF. This writer validates that payload,
/// promotes its colour table to a local table, and immediately appends the image data behind
/// an explicit Graphic Control Extension.
/// </para>
/// <para>
/// The temporary encoded payload is released before <see cref="AddFrame"/> returns, so memory
/// use is bounded by one encoded frame. Instances are not thread-safe; callers must also obey
/// the usual dispatcher-affinity rules of the supplied <see cref="BitmapSource"/>.
/// </para>
/// </remarks>
internal sealed class AnimatedGifWriter : IDisposable
{
    private const byte ExtensionIntroducer = 0x21;
    private const byte GraphicControlLabel = 0xF9;
    private const byte ApplicationExtensionLabel = 0xFF;
    private const byte ImageSeparator = 0x2C;
    private const byte Trailer = 0x3B;

    private readonly Stream _output;
    private readonly ushort _width;
    private readonly ushort _height;
    private readonly ushort _frameDelayCentiseconds;
    private readonly ushort _loopCount;
    private readonly bool _leaveOpen;

    private int _frameCount;
    private bool _completed;
    private bool _faulted;
    private bool _disposed;

    /// <summary>
    /// Creates a streaming animated GIF writer and writes its container header immediately.
    /// </summary>
    /// <param name="output">Writable destination positioned where the GIF should begin.</param>
    /// <param name="width">Logical canvas width, from 1 through 65,535 pixels.</param>
    /// <param name="height">Logical canvas height, from 1 through 65,535 pixels.</param>
    /// <param name="frameDelayCentiseconds">
    /// Delay written to every frame in hundredths of a second, from 0 through 65,535.
    /// </param>
    /// <param name="loopCount">
    /// NETSCAPE2.0 repeat count, from 0 through 65,535; zero means repeat indefinitely.
    /// </param>
    /// <param name="leaveOpen">Whether disposing this writer leaves <paramref name="output"/> open.</param>
    internal AnimatedGifWriter(
        Stream output,
        int width,
        int height,
        int frameDelayCentiseconds,
        int loopCount = 0,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The GIF destination must be writable.", nameof(output));
        }

        _width = ToGifUnsignedShort(width, nameof(width), minimum: 1);
        _height = ToGifUnsignedShort(height, nameof(height), minimum: 1);
        _frameDelayCentiseconds = ToGifUnsignedShort(
            frameDelayCentiseconds,
            nameof(frameDelayCentiseconds),
            minimum: 0);
        _loopCount = ToGifUnsignedShort(loopCount, nameof(loopCount), minimum: 0);
        _output = output;
        _leaveOpen = leaveOpen;

        WriteHeader();
        WriteLoopExtension();
    }

    /// <summary>Adds one full-canvas frame and writes it to the destination immediately.</summary>
    internal void AddFrame(BitmapSource frame) => AddFrame(frame, _frameDelayCentiseconds);

    /// <summary>Adds one full-canvas frame with an explicit per-frame delay.</summary>
    internal void AddFrame(BitmapSource frame, int frameDelayCentiseconds)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ThrowIfCannotWrite();
        ushort delay = ToGifUnsignedShort(
            frameDelayCentiseconds,
            nameof(frameDelayCentiseconds),
            minimum: 0);

        if (frame.PixelWidth != _width || frame.PixelHeight != _height)
        {
            throw new ArgumentException(
                $"GIF frames must be exactly {_width}x{_height} pixels; " +
                $"the supplied frame is {frame.PixelWidth}x{frame.PixelHeight}.",
                nameof(frame));
        }

        // The platform encoder owns quantisation and LZW compression. Only this one-frame
        // payload is buffered; all earlier frames have already been written to _output.
        using var encodedStream = new MemoryStream();
        var encoder = new GifBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        encoder.Save(encodedStream);

        if (!encodedStream.TryGetBuffer(out ArraySegment<byte> segment) || segment.Array is null)
        {
            throw new InvalidDataException("The platform GIF encoder did not expose its output buffer.");
        }

        ReadOnlySpan<byte> encoded = segment.Array.AsSpan(segment.Offset, checked((int)encodedStream.Length));
        EncodedGifFrame parsed = ParseSingleFrame(encoded, _width, _height);

        // Parsing must finish before the first destination byte for this frame is written. An
        // invalid or truncated platform payload therefore cannot leave a half-added GIF frame.
        try
        {
            WriteGraphicControlExtension(parsed.HasTransparency, parsed.TransparentColorIndex, delay);
            WriteImageBlock(encoded, parsed);
            _frameCount++;
        }
        catch
        {
            // Once an output stream rejects a write, its position and persisted prefix are not
            // generally recoverable. Prevent a later Complete call from presenting it as valid.
            _faulted = true;
            throw;
        }
    }

    /// <summary>Writes the GIF trailer and flushes the destination.</summary>
    /// <remarks>Calling this method more than once is harmless.</remarks>
    internal void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            return;
        }

        if (_faulted)
        {
            throw new InvalidOperationException("The GIF cannot be completed after an output failure.");
        }

        if (_frameCount == 0)
        {
            throw new InvalidOperationException("An animated GIF must contain at least one frame.");
        }

        try
        {
            _output.WriteByte(Trailer);
            _output.Flush();
            _completed = true;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>Marks a partial animation as abandoned so disposal never appends a trailer.</summary>
    internal void Abort()
    {
        if (!_disposed && !_completed)
        {
            _faulted = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // Do not manufacture an invalid zero-frame file during cleanup. Explicit Complete
            // still reports that programming error, while Dispose remains safe in failed setup.
            if (!_completed && !_faulted && _frameCount > 0)
            {
                Complete();
            }
        }
        finally
        {
            _disposed = true;
            if (!_leaveOpen)
            {
                _output.Dispose();
            }
        }
    }

    /// <summary>
    /// Validates a complete single-frame GIF and locates the blocks needed by the streaming
    /// container. Kept internal so malformed codec output can be verified independently.
    /// </summary>
    internal static EncodedGifFrame ParseSingleFrame(
        ReadOnlySpan<byte> encoded,
        int expectedWidth,
        int expectedHeight)
    {
        ushort width = ToGifUnsignedShort(expectedWidth, nameof(expectedWidth), minimum: 1);
        ushort height = ToGifUnsignedShort(expectedHeight, nameof(expectedHeight), minimum: 1);

        if (encoded.Length < 14 ||
            !(encoded[..6].SequenceEqual("GIF87a"u8) || encoded[..6].SequenceEqual("GIF89a"u8)))
        {
            throw Malformed("Missing GIF87a/GIF89a header or logical screen descriptor.");
        }

        int index = 6;
        ushort logicalWidth = ReadUInt16(encoded, ref index, "logical screen width");
        ushort logicalHeight = ReadUInt16(encoded, ref index, "logical screen height");
        if (logicalWidth != width || logicalHeight != height)
        {
            throw Malformed(
                $"Encoded logical screen is {logicalWidth}x{logicalHeight}, expected {width}x{height}.");
        }

        byte logicalPacked = ReadByte(encoded, ref index, "logical screen packed field");
        _ = ReadByte(encoded, ref index, "background colour index");
        _ = ReadByte(encoded, ref index, "pixel aspect ratio");

        int globalPaletteOffset = -1;
        int globalPaletteLength = 0;
        if ((logicalPacked & 0x80) != 0)
        {
            globalPaletteLength = ColourTableLength(logicalPacked);
            globalPaletteOffset = index;
            SkipBytes(encoded, ref index, globalPaletteLength, "global colour table");
        }

        bool hasTransparency = false;
        byte transparentColourIndex = 0;
        bool foundImage = false;
        bool foundTrailer = false;
        EncodedGifFrame result = default;

        while (index < encoded.Length)
        {
            byte introducer = ReadByte(encoded, ref index, "block introducer");
            switch (introducer)
            {
                case ExtensionIntroducer:
                    {
                        byte label = ReadByte(encoded, ref index, "extension label");
                        if (label == GraphicControlLabel)
                        {
                            ParseGraphicControlExtension(
                                encoded,
                                ref index,
                                ref hasTransparency,
                                ref transparentColourIndex);
                        }
                        else
                        {
                            SkipSubBlocks(encoded, ref index, "extension");
                        }

                        break;
                    }

                case ImageSeparator:
                    {
                        if (foundImage)
                        {
                            throw Malformed("A platform single-frame GIF contained more than one image.");
                        }

                        ushort left = ReadUInt16(encoded, ref index, "image left offset");
                        ushort top = ReadUInt16(encoded, ref index, "image top offset");
                        ushort imageWidth = ReadUInt16(encoded, ref index, "image width");
                        ushort imageHeight = ReadUInt16(encoded, ref index, "image height");
                        byte imagePacked = ReadByte(encoded, ref index, "image packed field");

                        if (left != 0 || top != 0 || imageWidth != width || imageHeight != height)
                        {
                            throw Malformed(
                                $"Encoded image rectangle ({left},{top}) {imageWidth}x{imageHeight} " +
                                $"does not fill the expected {width}x{height} canvas.");
                        }

                        int paletteOffset;
                        int paletteLength;
                        int paletteSizeCode;
                        if ((imagePacked & 0x80) != 0)
                        {
                            paletteLength = ColourTableLength(imagePacked);
                            paletteSizeCode = imagePacked & 0x07;
                            paletteOffset = index;
                            SkipBytes(encoded, ref index, paletteLength, "local colour table");
                        }
                        else
                        {
                            if (globalPaletteOffset < 0)
                            {
                                throw Malformed("The encoded image has neither a local nor global colour table.");
                            }

                            paletteOffset = globalPaletteOffset;
                            paletteLength = globalPaletteLength;
                            paletteSizeCode = logicalPacked & 0x07;
                        }

                        if (hasTransparency && transparentColourIndex >= paletteLength / 3)
                        {
                            throw Malformed("The transparent colour index lies outside the selected palette.");
                        }

                        int imageDataOffset = index;
                        byte minimumCodeSize = ReadByte(encoded, ref index, "LZW minimum code size");
                        if (minimumCodeSize is < 2 or > 8)
                        {
                            throw Malformed($"Invalid GIF LZW minimum code size {minimumCodeSize}.");
                        }

                        bool hasImageData = SkipSubBlocks(encoded, ref index, "image data");
                        if (!hasImageData)
                        {
                            throw Malformed("The encoded image contains no LZW data sub-block.");
                        }

                        result = new EncodedGifFrame(
                            paletteOffset,
                            paletteLength,
                            paletteSizeCode,
                            imagePacked,
                            imageDataOffset,
                            index - imageDataOffset,
                            hasTransparency,
                            transparentColourIndex);
                        foundImage = true;
                        break;
                    }

                case Trailer:
                    if (!foundImage)
                    {
                        throw Malformed("The GIF trailer appeared before an image frame.");
                    }

                    foundTrailer = true;
                    if (index != encoded.Length)
                    {
                        throw Malformed("Unexpected bytes follow the GIF trailer.");
                    }

                    break;

                default:
                    throw Malformed($"Unexpected GIF block introducer 0x{introducer:X2}.");
            }

            if (foundTrailer)
            {
                break;
            }
        }

        if (!foundImage || !foundTrailer)
        {
            throw Malformed("The single-frame GIF is missing its image or trailer.");
        }

        return result;
    }

    private static ushort ToGifUnsignedShort(int value, string parameterName, int minimum)
    {
        if (value < minimum || value > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"The value must be between {minimum} and {ushort.MaxValue}.");
        }

        return (ushort)value;
    }

    private void ThrowIfCannotWrite()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("No frames can be added after the GIF is complete.");
        }

        if (_faulted)
        {
            throw new InvalidOperationException("No frames can be added after an output failure.");
        }
    }

    private void WriteHeader()
    {
        Span<byte> header = stackalloc byte[13]
        {
            (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
            0, 0, 0, 0,
            0x70, // no global table; colour resolution advertises eight source bits
            0,
            0,
        };
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..8], _width);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..10], _height);
        _output.Write(header);
    }

    private void WriteLoopExtension()
    {
        Span<byte> extension = stackalloc byte[19]
        {
            ExtensionIntroducer,
            ApplicationExtensionLabel,
            0x0B,
            (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A',
            (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0',
            0x03,
            0x01,
            0,
            0,
            0x00,
        };
        BinaryPrimitives.WriteUInt16LittleEndian(extension[16..18], _loopCount);
        _output.Write(extension);
    }

    private void WriteGraphicControlExtension(
        bool hasTransparency,
        byte transparentColourIndex,
        ushort frameDelayCentiseconds)
    {
        Span<byte> extension = stackalloc byte[8]
        {
            ExtensionIntroducer,
            GraphicControlLabel,
            0x04,
            (byte)(0x04 | (hasTransparency ? 0x01 : 0x00)), // disposal=keep, optional transparency
            0,
            0,
            hasTransparency ? transparentColourIndex : (byte)0,
            0x00,
        };
        BinaryPrimitives.WriteUInt16LittleEndian(extension[4..6], frameDelayCentiseconds);
        _output.Write(extension);
    }

    private void WriteImageBlock(ReadOnlySpan<byte> encoded, EncodedGifFrame frame)
    {
        Span<byte> descriptor = stackalloc byte[10]
        {
            ImageSeparator,
            0, 0, // left
            0, 0, // top
            0, 0, // width
            0, 0, // height
            0,
        };
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[5..7], _width);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[7..9], _height);
        descriptor[9] = (byte)(
            0x80 |                         // promote the selected palette to a local table
            (frame.SourceImagePacked & 0x60) | // preserve interlace and sort flags
            frame.PaletteSizeCode);

        _output.Write(descriptor);
        _output.Write(encoded.Slice(frame.PaletteOffset, frame.PaletteLength));
        _output.Write(encoded.Slice(frame.ImageDataOffset, frame.ImageDataLength));
    }

    private static void ParseGraphicControlExtension(
        ReadOnlySpan<byte> encoded,
        ref int index,
        ref bool hasTransparency,
        ref byte transparentColourIndex)
    {
        byte blockSize = ReadByte(encoded, ref index, "graphic control block size");
        if (blockSize != 4)
        {
            throw Malformed($"Graphic Control Extension block size is {blockSize}, expected 4.");
        }

        byte packed = ReadByte(encoded, ref index, "graphic control packed field");
        _ = ReadUInt16(encoded, ref index, "graphic control delay");
        byte colourIndex = ReadByte(encoded, ref index, "transparent colour index");
        byte terminator = ReadByte(encoded, ref index, "graphic control terminator");
        if (terminator != 0)
        {
            throw Malformed("Graphic Control Extension is missing its zero terminator.");
        }

        hasTransparency = (packed & 0x01) != 0;
        transparentColourIndex = colourIndex;
    }

    private static int ColourTableLength(byte packed) => 3 * (1 << ((packed & 0x07) + 1));

    private static byte ReadByte(ReadOnlySpan<byte> data, ref int index, string description)
    {
        if ((uint)index >= (uint)data.Length)
        {
            throw Malformed($"Unexpected end of GIF while reading {description}.");
        }

        return data[index++];
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, ref int index, string description)
    {
        if (data.Length - index < sizeof(ushort))
        {
            throw Malformed($"Unexpected end of GIF while reading {description}.");
        }

        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(index, sizeof(ushort)));
        index += sizeof(ushort);
        return value;
    }

    private static void SkipBytes(ReadOnlySpan<byte> data, ref int index, int count, string description)
    {
        if (count < 0 || data.Length - index < count)
        {
            throw Malformed($"Unexpected end of GIF while reading {description}.");
        }

        index += count;
    }

    private static bool SkipSubBlocks(ReadOnlySpan<byte> data, ref int index, string description)
    {
        bool foundData = false;
        while (true)
        {
            byte length = ReadByte(data, ref index, $"{description} sub-block length");
            if (length == 0)
            {
                return foundData;
            }

            foundData = true;
            SkipBytes(data, ref index, length, $"{description} sub-block");
        }
    }

    private static InvalidDataException Malformed(string message) => new(message);

    internal readonly record struct EncodedGifFrame(
        int PaletteOffset,
        int PaletteLength,
        int PaletteSizeCode,
        byte SourceImagePacked,
        int ImageDataOffset,
        int ImageDataLength,
        bool HasTransparency,
        byte TransparentColorIndex);
}
