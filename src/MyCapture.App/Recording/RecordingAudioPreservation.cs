using System.IO;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>Editing must not turn a source with sound into a successful silent result.</summary>
internal sealed class RecordingAudioPreservationException : IOException
{
    internal RecordingAudioPreservationException(Exception? inner = null)
        : base(UiText.Get("RecordingAudio_SaveFailed"), inner) { }
}

/// <summary>Persisted in the edit journal; null in an old journal means probe its immutable source.</summary>
internal sealed record RecordingAudioExpectation(bool Present, int SampleRate, int Channels)
{
    internal static RecordingAudioExpectation FromSource(string source,
        Func<string, AudioSourceInfo?>? nativeProbe = null)
    {
        try
        {
            AudioSourceInfo? audio = nativeProbe is null
                ? MediaFoundationAudioReader.ReadSourceInfo(source) : nativeProbe(source);
            return audio is null
                ? new(false, 0, 0)
                : new(true, RecordingAudioFormat.SampleRate, RecordingAudioFormat.Channels);
        }
        catch (OperationCanceledException) { throw; }
        catch (RecordingAudioPreservationException) { throw; }
        catch (Exception error) { throw new RecordingAudioPreservationException(error); }
    }

    internal bool IsValid => Present ? SampleRate == 48_000 && Channels == 2 : SampleRate == 0 && Channels == 0;

    internal void Validate(string result, double durationMs, CancellationToken token = default)
    {
        if (!IsValid) throw new RecordingAudioPreservationException();
        if (!Present) return;
        try
        {
            AudioSourceInfo? info = MediaFoundationAudioReader.ReadSourceInfo(result);
            if (info is null || info.SampleRate != SampleRate || info.Channels != Channels
                || info.Subtype != new Guid("00001610-0000-0010-8000-00AA00389B71"))
                throw new InvalidDataException("The completed MP4 has no matching AAC stream.");
            using var reader = new MediaFoundationAudioReader(result);
            DecodedAudioBlock first = reader.ReadNext(token)
                ?? throw new InvalidDataException("The completed audio stream is empty.");
            // AAC priming/padding is at most a codec frame at either end. The PCM schedule is
            // sample exact; the tolerance here concerns the compressed file's decode boundary.
            const long codecFrameTicks = 1_024L * 10_000_000 / 48_000;
            if (Math.Abs(first.Timestamp100ns) > codecFrameTicks)
                throw new InvalidDataException("The completed audio does not decode at the start.");
            double expectedEnd = durationMs * 10_000;
            bool CoversEnd(DecodedAudioBlock block) => block.Timestamp100ns <= expectedEnd
                && block.EndTimestamp100ns + codecFrameTicks >= expectedEnd;
            bool endCovered = CoversEnd(first);
            reader.Seek(Math.Max(0, durationMs - 100));
            while (reader.ReadNext(token) is { } block) endCovered |= CoversEnd(block);
            if (!endCovered)
                throw new InvalidDataException("The completed audio does not decode at the end.");
        }
        catch (OperationCanceledException) { throw; }
        catch (RecordingAudioPreservationException) { throw; }
        catch (Exception error) { throw new RecordingAudioPreservationException(error); }
    }
}

/// <summary>
/// Thread-owned, bounded PCM mapping shared by every MP4 video decode path. Only the currently
/// decoded block and one 20ms output block are retained. Output boundaries come from the common
/// cumulative cut map, never from rounded per-range lengths.
/// </summary>
internal sealed class RecordingAudioEditEncoder : IVideoEncoder
{
    private readonly IAudioVideoEncoder _encoder;
    private readonly MediaFoundationAudioReader _reader;
    private readonly IReadOnlyList<RecordingAudioKeptRange> _ranges;
    private readonly CancellationToken _token;
    private readonly int _fps;
    private readonly short[] _buffer = new short[960 * 2];
    private DecodedAudioBlock? _decoded;
    private bool _eof;
    private bool _readAny;
    private long _outputSample;
    private int _range;
    private int _frames;

    internal RecordingAudioEditEncoder(IVideoEncoder encoder, string source,
        IReadOnlyList<RecordingAudioKeptRange> ranges, int fps, CancellationToken token)
    {
        if (encoder is not IAudioVideoEncoder audio || audio.AudioState != AudioEncoderState.Available)
            throw new RecordingAudioPreservationException();
        _encoder = audio;
        _ranges = ranges;
        _fps = fps;
        _token = token;
        try { _reader = new MediaFoundationAudioReader(source); }
        catch (Exception error) { throw new RecordingAudioPreservationException(error); }
        if (_reader.Audio is null)
        {
            _reader.Dispose();
            throw new RecordingAudioPreservationException();
        }
    }

    public int Width => _encoder.Width;
    public int Height => _encoder.Height;

    public void WriteFrame(in EncoderFrame frame)
    {
        _encoder.WriteFrame(frame);
        _frames++;
        // Match the existing fixed video cadence, including the final held picture frame.
        long through = checked((long)Math.Round((decimal)_frames * 48_000 / _fps,
            MidpointRounding.AwayFromZero));
        try { WriteThrough(through); }
        catch (OperationCanceledException) { throw; }
        catch (RecordingAudioPreservationException) { throw; }
        catch (Exception error) { throw new RecordingAudioPreservationException(error); }
    }

    private void WriteThrough(long through)
    {
        while (_outputSample < through)
        {
            _token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(960, through - _outputSample);
            Array.Clear(_buffer);
            for (int index = 0; index < count; index++)
            {
                long output = _outputSample + index;
                while (_range < _ranges.Count && output >= _ranges[_range].OutputEndSample) _range++;
                if (_range == _ranges.Count) break; // Last-frame padding is silence, never removed sound.
                RecordingAudioKeptRange range = _ranges[_range];
                long sourceTicks = range.SourceStart100ns
                    + RecordingAudioFormat.SampleTime100ns(output - range.OutputStartSample);
                if (sourceTicks >= range.SourceEnd100ns) continue;
                CopySample(sourceTicks, index * 2);
            }
            _encoder.WriteAudio(new RecordingAudioBlock(_outputSample, _buffer.AsMemory(0, count * 2)));
            _outputSample += count;
        }
    }

    private void CopySample(long sourceTicks, int outputOffset)
    {
        // Sequential decode also validates the removed parts of the source without retaining
        // them. Source PTS, including a positive leading offset, decides whether to write silence.
        while (!_eof && (_decoded is null || sourceTicks >= _decoded.EndTimestamp100ns))
        {
            _decoded = _reader.ReadNext(_token);
            _eof = _decoded is null;
            if (_decoded is not null) _readAny = true;
            else if (!_readAny) throw new InvalidDataException("The source audio stream could not be decoded.");
        }
        if (_decoded is null || sourceTicks < _decoded.Timestamp100ns) return;
        long sample = RecordingAudioFormat.SampleAt100ns(sourceTicks - _decoded.Timestamp100ns);
        if (sample >= _decoded.FrameCount) return;
        int offset = checked((int)sample * 2);
        _buffer[outputOffset] = _decoded.Samples[offset];
        _buffer[outputOffset + 1] = _decoded.Samples[offset + 1];
    }

    public void Complete()
    {
        try { _encoder.Complete(); }
        catch (Exception error) { throw new RecordingAudioPreservationException(error); }
    }

    public void Dispose()
    {
        try { _reader.Dispose(); }
        finally { _encoder.Dispose(); }
    }
}
