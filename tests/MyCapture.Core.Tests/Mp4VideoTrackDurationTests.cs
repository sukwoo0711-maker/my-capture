using System.Buffers.Binary;
using System.Text;
using MyCapture.Core.Recording;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class Mp4VideoTrackDurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UsesVideoPresentationDurationInMovieTimescale(bool versionOne)
    {
        using var stream = Movie(versionOne, 10_000);
        Assert.Equal(10_000, Mp4VideoTrackDuration.ReadMilliseconds(stream));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(uint.MaxValue)]
    public void UndeclaredTrackDurationRequiresStrictFallback(uint duration)
    {
        using var stream = Movie(false, duration);
        Assert.Null(Mp4VideoTrackDuration.ReadMilliseconds(stream));
    }

    [Fact]
    public void RejectsTruncatedOrOutOfBoundsBoxes()
    {
        using var stream = Movie(false, 10_000);
        stream.SetLength(stream.Length - 1);
        Assert.Throws<InvalidDataException>(() => Mp4VideoTrackDuration.ReadMilliseconds(stream));
        using var shortHeader = new MemoryStream(new byte[7]);
        Assert.Throws<InvalidDataException>(() => Mp4VideoTrackDuration.ReadMilliseconds(shortHeader));
    }

    [Fact]
    public void HonorsCancellation()
    {
        using var stream = Movie(false, 10_000);
        Assert.Throws<OperationCanceledException>(() => Mp4VideoTrackDuration.ReadMilliseconds(stream, new(true)));
    }

    private static MemoryStream Movie(bool versionOne, uint videoDuration)
    {
        byte[] movieHeader = new byte[versionOne ? 32 : 20];
        movieHeader[0] = versionOne ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(movieHeader.AsSpan(versionOne ? 20 : 12), 1000);
        if (versionOne) BinaryPrimitives.WriteUInt64BigEndian(movieHeader.AsSpan(24), 10_100);
        else BinaryPrimitives.WriteUInt32BigEndian(movieHeader.AsSpan(16), 10_100);
        return new MemoryStream(Box("moov", [.. Box("mvhd", movieHeader),
            .. Track("soun", 10_100), .. Track("vide", videoDuration)]));

        byte[] Track(string handler, uint duration)
        {
            byte[] trackHeader = new byte[versionOne ? 36 : 24];
            trackHeader[0] = versionOne ? (byte)1 : (byte)0;
            if (versionOne) BinaryPrimitives.WriteUInt64BigEndian(trackHeader.AsSpan(28), duration);
            else BinaryPrimitives.WriteUInt32BigEndian(trackHeader.AsSpan(20), duration);
            byte[] handlerHeader = new byte[12];
            Encoding.ASCII.GetBytes(handler).CopyTo(handlerHeader, 8);
            return Box("trak", [.. Box("tkhd", trackHeader), .. Box("mdia", Box("hdlr", handlerHeader))]);
        }
    }

    private static byte[] Box(string type, byte[] payload)
    {
        byte[] bytes = new byte[payload.Length + 8];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
        payload.CopyTo(bytes, 8);
        return bytes;
    }
}
