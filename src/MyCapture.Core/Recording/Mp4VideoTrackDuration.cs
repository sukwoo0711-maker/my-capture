using System.Buffers.Binary;

namespace MyCapture.Core.Recording;

/// <summary>Reads the first video track's presentation duration without loading media payloads.</summary>
public static class Mp4VideoTrackDuration
{
    public static double? ReadMilliseconds(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Box? movie = Children(0, stream.Length).FirstOrDefault(box => box.Type == 0x6D6F6F76); // moov
        if (movie is null) throw new InvalidDataException("The MP4 movie header is missing.");
        Box[] headers = Children(movie.Start, movie.End).ToArray();
        Box? movieHeader = headers.FirstOrDefault(box => box.Type == 0x6D766864); // mvhd
        if (movieHeader is null) throw new InvalidDataException("The MP4 timescale is missing.");
        byte version = ReadVersion(movieHeader);
        uint timescale = ReadUInt32(movieHeader, version == 1 ? 20 : 12);
        if (timescale == 0) throw new InvalidDataException("The MP4 timescale is invalid.");
        foreach (Box track in headers.Where(box => box.Type == 0x7472616B)) // trak
        {
            Box[] parts = Children(track.Start, track.End).ToArray();
            Box? media = parts.FirstOrDefault(box => box.Type == 0x6D646961); // mdia
            if (media is null) continue;
            Box? handler = Children(media.Start, media.End).FirstOrDefault(box => box.Type == 0x68646C72); // hdlr
            if (handler is null || ReadUInt32(handler, 8) != 0x76696465) continue; // vide
            Box? header = parts.FirstOrDefault(box => box.Type == 0x746B6864); // tkhd
            if (header is null) throw new InvalidDataException("The MP4 video track header is missing.");
            version = ReadVersion(header);
            // tkhd uses the movie timescale and includes edit-list presentation timing.
            ulong duration = version == 1 ? ReadUInt64(header, 28) : ReadUInt32(header, 20);
            if (duration == 0 || duration == (version == 1 ? ulong.MaxValue : uint.MaxValue)) return null;
            return duration * 1000.0 / timescale;
        }
        throw new InvalidDataException("The MP4 has no video track.");

        IEnumerable<Box> Children(long start, long end)
        {
            int count = 0;
            while (start < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > 4096) throw new InvalidDataException("The MP4 contains too many header boxes.");
                if (end - start < 8) throw new InvalidDataException("The MP4 box header is truncated.");
                stream.Position = start;
                byte[] bytes = new byte[16];
                stream.ReadExactly(bytes.AsSpan(0, 8));
                ulong size = BinaryPrimitives.ReadUInt32BigEndian(bytes);
                uint type = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4));
                int headerSize = 8;
                if (size == 1)
                {
                    if (end - start < 16) throw new InvalidDataException("The MP4 box header is truncated.");
                    stream.ReadExactly(bytes.AsSpan(8, 8));
                    size = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(8));
                    headerSize = 16;
                }
                else if (size == 0) size = (ulong)(end - start);
                if (size < (ulong)headerSize || size > (ulong)(end - start))
                    throw new InvalidDataException("The MP4 box length is invalid.");
                yield return new Box(type, start + headerSize, start + (long)size);
                start += (long)size;
            }
        }

        byte[] Read(Box box, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (box.End - box.Start < offset + count) throw new InvalidDataException("The MP4 header is truncated.");
            stream.Position = box.Start + offset;
            byte[] bytes = new byte[count];
            stream.ReadExactly(bytes);
            return bytes;
        }

        byte ReadVersion(Box box)
        {
            byte value = Read(box, 0, 1)[0];
            return value <= 1 ? value : throw new InvalidDataException("The MP4 header version is unsupported.");
        }

        uint ReadUInt32(Box box, int offset) => BinaryPrimitives.ReadUInt32BigEndian(Read(box, offset, 4));
        ulong ReadUInt64(Box box, int offset) => BinaryPrimitives.ReadUInt64BigEndian(Read(box, offset, 8));
    }

    private sealed record Box(uint Type, long Start, long End);
}
