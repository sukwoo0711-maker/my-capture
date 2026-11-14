using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class MediaFoundationAudioReaderTests(ITestOutputHelper output)
{
    private static readonly Guid Aac = new("00001610-0000-0010-8000-00AA00389B71");

    [Theory]
    [InlineData(44_100, 1)]
    [InlineData(48_000, 2)]
    [InlineData(96_000, 2)]
    [InlineData(48_000, 6)]
    public void NativePcmIsConvertedTo48kStereoWithTimestamps(int sampleRate, int channels)
    {
        string directory = OwnedTestDirectory.Create("mc-audio-reader-pcm-");
        string path = Path.Combine(directory, "source.wav");
        WriteWave(path, sampleRate, channels);
        using var reader = new MediaFoundationAudioReader(path);
        Assert.NotNull(reader.Audio);
        Assert.Equal(sampleRate, reader.Audio.SampleRate);
        Assert.Equal(channels, reader.Audio.Channels);
        long frames = 0, last = long.MinValue;
        int peak = 0;
        while (reader.ReadNext() is { } block)
        {
            Assert.Equal(0, block.Samples.Length % 2);
            Assert.True(block.Timestamp100ns >= last);
            Assert.InRange(Math.Abs(block.Timestamp100ns - RecordingAudioFormat.SampleTime100ns(frames)), 0, 209);
            last = block.Timestamp100ns;
            frames += block.FrameCount;
            foreach (short value in block.Samples) peak = Math.Max(peak, Math.Abs((int)value));
        }
        Assert.InRange(frames, 47_999, 48_001);
        Assert.True(peak > 1_000);
        Assert.Null(reader.ReadNext());
        output.WriteLine($"native={sampleRate}/{channels}; decodedFrames={frames}; peak={peak}");
    }

    [Fact]
    public void ThirtyMinuteAacRoundTripKeepsStartAndEndPeaksWithin40msOfStoredFlashes()
    {
        string directory = OwnedTestDirectory.Create("mc-audio-reader-long-");
        string path = Path.Combine(directory, "thirty-minutes.mp4");
        var elapsed = Stopwatch.StartNew();
        Encode(path, 1_800, 1, 1_799);
        (int total, int audio) = MediaFoundationAudioReader.CountStreamsForTest(path);
        Assert.Equal(2, total);
        Assert.Equal(1, audio);
        AudioSourceInfo info = Assert.IsType<AudioSourceInfo>(MediaFoundationAudioReader.ReadSourceInfo(path));
        Assert.Equal(Aac, info.Subtype);
        Assert.Equal(48_000, info.SampleRate);
        Assert.Equal(2, info.Channels);
        VideoSourceInfo source = MediaFoundationVideoFrameReader.ReadSourceInfo(path);
        Assert.Equal(info, source.Audio);
        Assert.InRange(source.DurationMs, 1_799_999, 1_800_050);
        double firstFlash, lastFlash;
        using (var video = new MediaFoundationVideoFrameReader(path))
        {
            DecodedVideoFrame first = video.ReadFrameAt(1_000);
            DecodedVideoFrame last = video.ReadFrameAt(1_799_000);
            Assert.True(first.Pixels[0] > 180, "The first stored flash must decode as bright.");
            Assert.True(last.Pixels[0] > 180, "The last stored flash must decode as bright.");
            firstFlash = first.TimestampMs;
            lastFlash = last.TimestampMs;
        }
        var firstPeak = (Amplitude: 0, TimeMs: double.NaN);
        var lastPeak = (Amplitude: 0, TimeMs: double.NaN);
        long frames = 0, previous = long.MinValue;
        long? start = null;
        using (var reader = new MediaFoundationAudioReader(path))
        {
            while (reader.ReadNext() is { } block)
            {
                start ??= block.Timestamp100ns;
                Assert.True(block.Timestamp100ns >= previous);
                previous = block.Timestamp100ns;
                frames += block.FrameCount;
                for (int frame = 0; frame < block.FrameCount; frame++)
                {
                    int amplitude = Math.Abs((int)block.Samples[frame * 2]);
                    double time = block.Timestamp100ns / 10_000d + frame * 1_000d / 48_000;
                    if (time < 2_000 && amplitude > firstPeak.Amplitude) firstPeak = (amplitude, time);
                    if (time > 1_798_000 && amplitude > lastPeak.Amplitude) lastPeak = (amplitude, time);
                }
            }
        }
        Assert.NotNull(start);
        Assert.InRange(Math.Abs(start.Value / 10_000d), 0, 40);
        Assert.True(firstPeak.Amplitude > 1_000 && lastPeak.Amplitude > 1_000);
        double firstSkew = firstPeak.TimeMs - firstFlash, lastSkew = lastPeak.TimeMs - lastFlash;
        Assert.InRange(Math.Abs(firstSkew), 0, 40);
        Assert.InRange(Math.Abs(lastSkew), 0, 40);
        Assert.InRange(Math.Abs(lastSkew - firstSkew), 0, 1_000d / 48_000);
        // AAC carries whole codec frames, so its decoded tail may include less than one frame of padding.
        Assert.InRange(frames, 48_000L * 1_800, 48_000L * 1_800 + 1_024);
        output.WriteLine($"firstFlashMs={firstFlash:R}; firstPeakMs={firstPeak.TimeMs:R}; firstSkewMs={firstSkew:R}; " +
            $"lastFlashMs={lastFlash:R}; lastPeakMs={lastPeak.TimeMs:R}; lastSkewMs={lastSkew:R}; " +
            $"decodedFrames={frames}; firstTimestamp100ns={start}; wallSeconds={elapsed.Elapsed.TotalSeconds:F3}");
        TestDiagnosticArtifacts.WriteJson(directory, "sync.json", new
        {
            firstFlash, firstPeakAmplitude = firstPeak.Amplitude, firstPeakTimeMs = firstPeak.TimeMs, firstSkew,
            lastFlash, lastPeakAmplitude = lastPeak.Amplitude, lastPeakTimeMs = lastPeak.TimeMs, lastSkew, frames, start,
            wallSeconds = elapsed.Elapsed.TotalSeconds,
        });
    }

    [Fact]
    public void AudioStartOffsetIsPreservedAndCancellationDoesNotConsume()
    {
        string directory = OwnedTestDirectory.Create("mc-audio-reader-offset-");
        string path = Path.Combine(directory, "offset.mp4");
        using (var encoder = CreateEncoder(path))
        {
            encoder.WriteFrame(new EncoderFrame(new byte[64 * 64 * 4], 64, 64, 256, 0));
            short[] pcm = new short[48_000 * 2];
            pcm[0] = pcm[1] = 25_000;
            encoder.WriteAudio(new RecordingAudioBlock(24_000, pcm));
            encoder.WriteFrame(new EncoderFrame(new byte[64 * 64 * 4], 64, 64, 256, 1_000));
            encoder.Complete();
        }
        using var reader = new MediaFoundationAudioReader(path);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.ReadNext(canceled.Token));
        DecodedAudioBlock first = Assert.IsType<DecodedAudioBlock>(reader.ReadNext());
        Assert.InRange(first.Timestamp100ns / 10_000d, 460, 540);
        output.WriteLine($"nonzeroSourceStartMs={first.Timestamp100ns / 10_000d:R}");
    }

    [Fact]
    public void MultipleAudioStreamsSelectTheFirstStream()
    {
        string directory = OwnedTestDirectory.Create("mc-audio-reader-multi-");
        string single = Path.Combine(directory, "single.mp4"), multiple = Path.Combine(directory, "multiple.mp4");
        Encode(single, 3, 1, 1);
        DuplicateAudioTrack(single, multiple);
        Assert.Equal((3, 2), MediaFoundationAudioReader.CountStreamsForTest(multiple));
        using var first = new MediaFoundationAudioReader(single);
        using var multi = new MediaFoundationAudioReader(multiple);
        Assert.Equal(first.Audio, multi.Audio);
        while (first.ReadNext() is { } expected)
        {
            DecodedAudioBlock actual = Assert.IsType<DecodedAudioBlock>(multi.ReadNext());
            Assert.Equal(expected.Timestamp100ns, actual.Timestamp100ns);
            Assert.Equal(expected.Samples, actual.Samples);
        }
        Assert.Null(multi.ReadNext());
    }

    private static MediaFoundationVideoEncoder CreateEncoder(string path) => new(
        new VideoEncoderOptions(path, 64, 64, 1, 128_000) { AllowHardwareEncoder = false, Audio = new AudioEncoderOptions() },
        NullLogger.Instance);

    private static void Encode(string path, int seconds, int firstImpulse, int lastImpulse)
    {
        using var encoder = CreateEncoder(path);
        Assert.Equal(AudioEncoderState.Available, encoder.AudioState);
        byte[] black = new byte[64 * 64 * 4], white = Enumerable.Repeat((byte)255, black.Length).ToArray();
        short[] silence = new short[48_000 * 2], impulse = new short[48_000 * 2];
        impulse[0] = impulse[1] = 25_000;
        for (int second = 0; second < seconds; second++)
        {
            bool flash = second == firstImpulse || second == lastImpulse;
            encoder.WriteFrame(new EncoderFrame(flash ? white : black, 64, 64, 256, second * 1_000d));
            encoder.WriteAudio(new RecordingAudioBlock((long)second * 48_000, flash ? impulse : silence));
        }
        encoder.Complete();
    }

    private static void WriteWave(string path, int sampleRate, int channels)
    {
        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
        int bytes = sampleRate * channels * 2;
        writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((ushort)1); writer.Write((ushort)channels); writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2); writer.Write((ushort)(channels * 2)); writer.Write((ushort)16);
        writer.Write("data"u8); writer.Write(bytes);
        for (int frame = 0; frame < sampleRate; frame++)
        for (int channel = 0; channel < channels; channel++)
            writer.Write((short)(channel < 2 ? Math.Sin(frame * Math.Tau * 440 / sampleRate) * 12_000 : 0));
    }

    // Duplicate the encoded audio track with a new track ID. Both tracks deliberately reference
    // the same immutable media bytes; changing only track metadata avoids a codec dependency in
    // the multi-track fixture. The reader checks the actual returned native stream index too.
    private static void DuplicateAudioTrack(string source, string target)
    {
        byte[] bytes = File.ReadAllBytes(source);
        var top = Boxes(bytes, 0, bytes.Length).ToArray();
        var moov = Assert.Single(top, box => box.Type == "moov");
        Assert.Equal(bytes.Length, moov.Offset + moov.Size); // chunk offsets stay unchanged
        var track = Assert.Single(Boxes(bytes, moov.Offset + 8, moov.Offset + moov.Size),
            box => box.Type == "trak" && Encoding.ASCII.GetString(bytes, box.Offset, box.Size).Contains("soun", StringComparison.Ordinal));
        byte[] duplicate = bytes.AsSpan(track.Offset, track.Size).ToArray();
        var movieHeader = Assert.Single(Boxes(bytes, moov.Offset + 8, moov.Offset + moov.Size), box => box.Type == "mvhd");
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(movieHeader.Offset + movieHeader.Size - 4, 4), 11);
        var header = Assert.Single(Boxes(duplicate, 8, duplicate.Length), box => box.Type == "tkhd");
        int trackIdOffset = header.Offset + (duplicate[header.Offset + 8] == 1 ? 28 : 20);
        BinaryPrimitives.WriteUInt32BigEndian(duplicate.AsSpan(trackIdOffset, 4), 10);
        using var destination = File.Create(target);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(moov.Offset, 4), checked((uint)(moov.Size + duplicate.Length)));
        destination.Write(bytes);
        destination.Write(duplicate);
    }

    private static IEnumerable<(int Offset, int Size, string Type)> Boxes(byte[] bytes, int offset, int end)
    {
        while (offset < end)
        {
            int size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)));
            Assert.InRange(size, 8, end - offset);
            yield return (offset, size, Encoding.ASCII.GetString(bytes, offset + 4, 4));
            offset += size;
        }
        Assert.Equal(end, offset);
    }
}
