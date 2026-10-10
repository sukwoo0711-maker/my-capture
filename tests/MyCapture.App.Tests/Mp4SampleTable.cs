using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MyCapture.App.Tests;

/// <summary>
/// Minimal ISO-BMFF reader for the first video track's presentation times and sync-sample (key
/// frame) table. Tests use it to prove where Media Foundation may land after a seek without
/// trusting the decoder under test.
/// </summary>
internal sealed record Mp4SampleTable(IReadOnlyList<double> PresentationMs, IReadOnlyList<int> SyncSampleIndexes)
{
    /// <summary>Presentation times of key frames, ascending. Every sample is a key frame when stss is absent.</summary>
    internal IReadOnlyList<double> KeyFrameMs =>
        SyncSampleIndexes.Select(index => PresentationMs[index]).Order().ToArray();

    internal static Mp4SampleTable Read(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        (int Start, int End) moov = Find(file, 0, file.Length, "moov") ?? throw new InvalidDataException("moov box is missing.");
        int cursor = moov.Start;
        while (Find(file, cursor, moov.End, "trak") is { } trak)
        {
            cursor = trak.End;
            (int Start, int End) mdia = Require(file, trak, "mdia");
            (int Start, int End) hdlr = Require(file, mdia, "hdlr");
            if (!file.AsSpan(hdlr.Start + 8, 4).SequenceEqual("vide"u8))
            {
                continue;
            }

            (int Start, int End) mdhd = Require(file, mdia, "mdhd");
            uint timescale = file[mdhd.Start] == 1
                ? ReadUInt32(file, mdhd.Start + 20)
                : ReadUInt32(file, mdhd.Start + 12);
            (int Start, int End) stbl = Require(file, Require(file, mdia, "minf"), "stbl");

            var presentationTicks = new List<long>();
            (int Start, int End) stts = Require(file, stbl, "stts");
            long tick = 0;
            uint entries = ReadUInt32(file, stts.Start + 4);
            for (int entry = 0; entry < entries; entry++)
            {
                uint count = ReadUInt32(file, stts.Start + 8 + (entry * 8));
                uint delta = ReadUInt32(file, stts.Start + 12 + (entry * 8));
                for (int sample = 0; sample < count; sample++)
                {
                    presentationTicks.Add(tick);
                    tick += delta;
                }
            }

            if (Find(file, stbl.Start, stbl.End, "ctts") is { } ctts)
            {
                bool signed = file[ctts.Start] == 1;
                uint offsets = ReadUInt32(file, ctts.Start + 4);
                int sampleIndex = 0;
                for (int entry = 0; entry < offsets; entry++)
                {
                    uint count = ReadUInt32(file, ctts.Start + 8 + (entry * 8));
                    long offset = signed
                        ? BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(ctts.Start + 12 + (entry * 8), 4))
                        : ReadUInt32(file, ctts.Start + 12 + (entry * 8));
                    for (int sample = 0; sample < count; sample++)
                    {
                        presentationTicks[sampleIndex++] += offset;
                    }
                }
            }

            var sync = new List<int>();
            if (Find(file, stbl.Start, stbl.End, "stss") is { } stss)
            {
                uint count = ReadUInt32(file, stss.Start + 4);
                for (int entry = 0; entry < count; entry++)
                {
                    sync.Add(checked((int)ReadUInt32(file, stss.Start + 8 + (entry * 4)) - 1));
                }
            }
            else
            {
                sync.AddRange(Enumerable.Range(0, presentationTicks.Count));
            }

            return new Mp4SampleTable(
                presentationTicks.Select(value => value * 1000.0 / timescale).ToArray(),
                sync);
        }

        throw new InvalidDataException("No video track.");
    }

    private static (int Start, int End) Require(byte[] file, (int Start, int End) parent, string type) =>
        Find(file, parent.Start, parent.End, type) ?? throw new InvalidDataException($"{type} box is missing.");

    /// <summary>Returns the body range (after the box header) of the first direct child box.</summary>
    internal static (int Start, int End)? Find(byte[] file, int start, int end, string type)
    {
        byte[] name = Encoding.ASCII.GetBytes(type);
        int offset = start;
        while (offset + 8 <= end)
        {
            long size = ReadUInt32(file, offset);
            int header = 8;
            if (size == 1)
            {
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(offset + 8, 8)));
                header = 16;
            }
            else if (size == 0)
            {
                size = end - offset;
            }

            if (size < header || offset + size > end)
            {
                throw new InvalidDataException("Malformed MP4 box.");
            }

            if (file.AsSpan(offset + 4, 4).SequenceEqual(name))
            {
                return (offset + header, checked((int)(offset + size)));
            }

            offset = checked((int)(offset + size));
        }

        return null;
    }

    internal static uint ReadUInt32(byte[] file, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset, 4));

    /// <summary>
    /// Rewrites every declared duration (mvhd, tkhd, mdhd and edit lists) to
    /// <paramref name="factor"/> times its value while leaving the sample tables intact. The
    /// result models a damaged file whose header still advertises a plausible, longer duration
    /// than the samples it actually contains.
    /// </summary>
    internal static void ScaleDeclaredDurations(string path, int factor)
    {
        byte[] file = File.ReadAllBytes(path);
        (int Start, int End) moov = Find(file, 0, file.Length, "moov") ?? throw new InvalidDataException("moov box is missing.");
        ScaleDuration(file, Require(file, moov, "mvhd"), version0Offset: 16, version1Offset: 24, factor);
        int cursor = moov.Start;
        while (Find(file, cursor, moov.End, "trak") is { } trak)
        {
            cursor = trak.End;
            ScaleDuration(file, Require(file, trak, "tkhd"), version0Offset: 20, version1Offset: 28, factor);
            (int Start, int End) mdia = Require(file, trak, "mdia");
            ScaleDuration(file, Require(file, mdia, "mdhd"), version0Offset: 16, version1Offset: 24, factor);
            if (Find(file, trak.Start, trak.End, "edts") is { } edts
                && Find(file, edts.Start, edts.End, "elst") is { } elst)
            {
                bool wide = file[elst.Start] == 1;
                uint entries = ReadUInt32(file, elst.Start + 4);
                int entrySize = wide ? 20 : 12;
                for (int entry = 0; entry < entries; entry++)
                {
                    int offset = elst.Start + 8 + (entry * entrySize);
                    ScaleField(file, offset, wide, factor);
                }
            }
        }

        File.WriteAllBytes(path, file);
    }

    private static void ScaleDuration(byte[] file, (int Start, int End) box, int version0Offset, int version1Offset, int factor)
    {
        bool wide = file[box.Start] == 1;
        ScaleField(file, box.Start + (wide ? version1Offset : version0Offset), wide, factor);
    }

    private static void ScaleField(byte[] file, int offset, bool wide, int factor)
    {
        if (wide)
        {
            ulong value = BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(offset, 8));
            BinaryPrimitives.WriteUInt64BigEndian(file.AsSpan(offset, 8), checked(value * (ulong)factor));
        }
        else
        {
            uint value = ReadUInt32(file, offset);
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(offset, 4), checked(value * (uint)factor));
        }
    }
}
