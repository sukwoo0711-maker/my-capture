using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class AnimatedGifWriterTests
{
    [Fact]
    public void Complete_WritesAllFramesWithDelayLoopDimensionsAndSingleTrailer() => RunSta(() =>
    {
        const int width = 17;
        const int height = 11;
        const int delay = 7;
        const int loopCount = 4;

        using var output = new MemoryStream();
        using (var writer = new AnimatedGifWriter(
                   output,
                   width,
                   height,
                   delay,
                   loopCount,
                   leaveOpen: true))
        {
            writer.AddFrame(Solid(width, height, 0xE0, 0x20, 0x20));
            long afterFirstFrame = output.Length;
            writer.AddFrame(Solid(width, height, 0x20, 0xD0, 0x30));
            long afterSecondFrame = output.Length;
            writer.AddFrame(Solid(width, height, 0x20, 0x40, 0xE0));
            writer.Complete();

            Assert.True(afterFirstFrame > 32, "the first frame was not streamed to the destination");
            Assert.True(afterSecondFrame > afterFirstFrame, "the second frame was retained instead of streamed");
        }

        byte[] bytes = output.ToArray();
        ParsedAnimation parsed = ParseAnimation(bytes);

        Assert.Equal("GIF89a", Encoding.ASCII.GetString(bytes, 0, 6));
        Assert.Equal(width, parsed.Width);
        Assert.Equal(height, parsed.Height);
        Assert.Equal(loopCount, parsed.LoopCount);
        Assert.Equal(3, parsed.FrameDimensions.Count);
        Assert.All(parsed.FrameDimensions, dimensions => Assert.Equal((width, height), dimensions));
        Assert.Equal([delay, delay, delay], parsed.FrameDelays);
        Assert.True(parsed.HasTrailer);
        Assert.Equal(0x3B, bytes[^1]);
    });

    [Fact]
    public void AddFrame_WritesIndependentPerFrameDelays() => RunSta(() =>
    {
        using var output = new MemoryStream();
        using (var writer = new AnimatedGifWriter(
                   output,
                   9,
                   7,
                   frameDelayCentiseconds: 10,
                   leaveOpen: true))
        {
            writer.AddFrame(Solid(9, 7, 0xE0, 0x20, 0x20), frameDelayCentiseconds: 5);
            writer.AddFrame(Solid(9, 7, 0x20, 0xD0, 0x30), frameDelayCentiseconds: 3);
            writer.AddFrame(Solid(9, 7, 0x20, 0x40, 0xE0));
            writer.Complete();
        }

        ParsedAnimation parsed = ParseAnimation(output.ToArray());
        Assert.Equal([5, 3, 10], parsed.FrameDelays);
    });

    [Fact]
    public void Dispose_CompletesExactlyOnceAndLeavesRequestedStreamOpen() => RunSta(() =>
    {
        using var output = new MemoryStream();
        var writer = new AnimatedGifWriter(output, 8, 6, 3, loopCount: 0, leaveOpen: true);
        writer.AddFrame(Solid(8, 6, 0x44, 0x66, 0x88));

        writer.Dispose();
        long completedLength = output.Length;
        writer.Dispose();

        Assert.Equal(completedLength, output.Length);
        Assert.True(output.CanWrite);
        Assert.Equal(0x3B, output.ToArray()[^1]);

        ParsedAnimation parsed = ParseAnimation(output.ToArray());
        Assert.Single(parsed.FrameDimensions);
        Assert.Equal(0, parsed.LoopCount);
        Assert.Equal([3], parsed.FrameDelays);
    });

    [Fact]
    public void AddFrame_RejectsDimensionMismatchWithoutAppendingPartialFrame() => RunSta(() =>
    {
        using var output = new MemoryStream();
        using var writer = new AnimatedGifWriter(output, 12, 9, 5, leaveOpen: true);
        writer.AddFrame(Solid(12, 9, 0x10, 0x20, 0x30));
        long beforeRejectedFrame = output.Length;

        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => writer.AddFrame(Solid(11, 9, 0xAA, 0xBB, 0xCC)));

        Assert.Contains("12x9", failure.Message, StringComparison.Ordinal);
        Assert.Equal(beforeRejectedFrame, output.Length);

        writer.AddFrame(Solid(12, 9, 0x90, 0x70, 0x50));
        writer.Complete();

        ParsedAnimation parsed = ParseAnimation(output.ToArray());
        Assert.Equal(2, parsed.FrameDimensions.Count);
        Assert.Equal([5, 5], parsed.FrameDelays);
    });

    [Fact]
    public void ParseSingleFrame_RejectsTruncatedOrDimensionallyInvalidCodecBytes() => RunSta(() =>
    {
        byte[] valid = EncodeSingleFrame(Solid(10, 7, 0x33, 0x77, 0xBB));
        byte[] truncated = valid[..^2];

        Assert.Throws<InvalidDataException>(() =>
        {
            _ = AnimatedGifWriter.ParseSingleFrame(truncated, 10, 7);
        });
        Assert.Throws<InvalidDataException>(() =>
        {
            _ = AnimatedGifWriter.ParseSingleFrame(valid, 9, 7);
        });
    });

    [Theory]
    [InlineData(0, 10, 1, 0)]
    [InlineData(10, 0, 1, 0)]
    [InlineData(65_536, 10, 1, 0)]
    [InlineData(10, 65_536, 1, 0)]
    [InlineData(10, 10, -1, 0)]
    [InlineData(10, 10, 65_536, 0)]
    [InlineData(10, 10, 1, -1)]
    [InlineData(10, 10, 1, 65_536)]
    public void Constructor_RejectsValuesOutsideGifUnsignedShortRanges(
        int width,
        int height,
        int delay,
        int loopCount)
    {
        using var output = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using var writer = new AnimatedGifWriter(
                output,
                width,
                height,
                delay,
                loopCount,
                leaveOpen: true);
        });
        Assert.Empty(output.ToArray());
    }

    private static BitmapSource Solid(int width, int height, byte red, byte green, byte blue)
    {
        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = 0xFF;
        }

        BitmapSource bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] EncodeSingleFrame(BitmapSource frame)
    {
        var encoder = new GifBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Independent structural reader for the generated animation. It deliberately does not use
    /// AnimatedGifWriter.ParseSingleFrame, so production and verification cannot share a parser bug.
    /// </summary>
    private static ParsedAnimation ParseAnimation(ReadOnlySpan<byte> bytes)
    {
        Assert.True(bytes.Length >= 14, "GIF is too short");
        Assert.True(bytes[..6].SequenceEqual("GIF89a"u8), "expected a GIF89a header");

        int index = 6;
        int width = ReadUInt16(bytes, ref index);
        int height = ReadUInt16(bytes, ref index);
        byte packed = ReadByte(bytes, ref index);
        _ = ReadByte(bytes, ref index);
        _ = ReadByte(bytes, ref index);
        if ((packed & 0x80) != 0)
        {
            Skip(bytes, ref index, ColourTableLength(packed));
        }

        var delays = new List<int>();
        var dimensions = new List<(int Width, int Height)>();
        int? pendingDelay = null;
        int? loopCount = null;
        bool trailer = false;

        while (index < bytes.Length)
        {
            byte introducer = ReadByte(bytes, ref index);
            switch (introducer)
            {
                case 0x21:
                    {
                        byte label = ReadByte(bytes, ref index);
                        if (label == 0xF9)
                        {
                            Assert.Equal(4, ReadByte(bytes, ref index));
                            _ = ReadByte(bytes, ref index);
                            pendingDelay = ReadUInt16(bytes, ref index);
                            _ = ReadByte(bytes, ref index);
                            Assert.Equal(0, ReadByte(bytes, ref index));
                        }
                        else if (label == 0xFF)
                        {
                            int identifierLength = ReadByte(bytes, ref index);
                            Assert.True(identifierLength > 0);
                            ReadOnlySpan<byte> identifier = Slice(bytes, ref index, identifierLength);
                            List<byte> data = ReadSubBlocks(bytes, ref index);
                            if (identifier.SequenceEqual("NETSCAPE2.0"u8))
                            {
                                Assert.True(data.Count >= 3 && data[0] == 1, "malformed NETSCAPE2.0 loop data");
                                loopCount = data[1] | (data[2] << 8);
                            }
                        }
                        else
                        {
                            _ = ReadSubBlocks(bytes, ref index);
                        }

                        break;
                    }

                case 0x2C:
                    {
                        _ = ReadUInt16(bytes, ref index); // left
                        _ = ReadUInt16(bytes, ref index); // top
                        int frameWidth = ReadUInt16(bytes, ref index);
                        int frameHeight = ReadUInt16(bytes, ref index);
                        byte imagePacked = ReadByte(bytes, ref index);
                        if ((imagePacked & 0x80) != 0)
                        {
                            Skip(bytes, ref index, ColourTableLength(imagePacked));
                        }

                        int minimumCodeSize = ReadByte(bytes, ref index);
                        Assert.InRange(minimumCodeSize, 2, 8);
                        List<byte> imageData = ReadSubBlocks(bytes, ref index);
                        Assert.NotEmpty(imageData);

                        dimensions.Add((frameWidth, frameHeight));
                        delays.Add(Assert.IsType<int>(pendingDelay));
                        pendingDelay = null;
                        break;
                    }

                case 0x3B:
                    trailer = true;
                    Assert.Equal(bytes.Length, index);
                    break;

                default:
                    throw new Xunit.Sdk.XunitException($"unexpected GIF introducer 0x{introducer:X2}");
            }

            if (trailer)
            {
                break;
            }
        }

        Assert.True(trailer, "GIF trailer was not found");
        Assert.Null(pendingDelay);
        return new ParsedAnimation(width, height, loopCount, delays, dimensions, trailer);
    }

    private static byte ReadByte(ReadOnlySpan<byte> bytes, ref int index)
    {
        Assert.True(index < bytes.Length, "unexpected end of GIF");
        return bytes[index++];
    }

    private static int ReadUInt16(ReadOnlySpan<byte> bytes, ref int index)
    {
        ReadOnlySpan<byte> value = Slice(bytes, ref index, sizeof(ushort));
        return BinaryPrimitives.ReadUInt16LittleEndian(value);
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> bytes, ref int index, int length)
    {
        Assert.True(length >= 0 && bytes.Length - index >= length, "truncated GIF block");
        ReadOnlySpan<byte> result = bytes.Slice(index, length);
        index += length;
        return result;
    }

    private static void Skip(ReadOnlySpan<byte> bytes, ref int index, int length) =>
        _ = Slice(bytes, ref index, length);

    private static List<byte> ReadSubBlocks(ReadOnlySpan<byte> bytes, ref int index)
    {
        var result = new List<byte>();
        while (true)
        {
            int length = ReadByte(bytes, ref index);
            if (length == 0)
            {
                return result;
            }

            result.AddRange(Slice(bytes, ref index, length).ToArray());
        }
    }

    private static int ColourTableLength(byte packed) => 3 * (1 << ((packed & 0x07) + 1));

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }

    private sealed record ParsedAnimation(
        int Width,
        int Height,
        int? LoopCount,
        IReadOnlyList<int> FrameDelays,
        IReadOnlyList<(int Width, int Height)> FrameDimensions,
        bool HasTrailer);
}
