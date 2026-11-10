using MyCapture.Core.Recording;

namespace MyCapture.Platform.Recording;

/// <summary>
/// Bounded producer queues for an audio-enabled recording. Producers transfer copies, never a
/// borrowed capture buffer. Only the constructing thread drains, completes and disposes the sink.
/// No capture device, worker thread or wait is created here.
/// </summary>
public sealed class RecordingEncoderQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly IAudioVideoEncoder _sink;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly int _videoCapacity, _audioCapacity, _maximumAudioFrames;
    private readonly long _maximumBytes;
    private readonly Queue<(EncoderFrame Frame, long Timestamp)> _video = new();
    private readonly Queue<RecordingAudioBlock> _audio = new();
    private long _bytes, _lastVideoTimestamp = -1, _lastAudioEnd, _watermark, _lastWritten = -1;
    private long _peakBufferedBytes, _maximumQueuedLead100ns;
    private int _peakBufferedAudioFrames;
    private int _audioFrames;
    private bool _closing, _completed, _disposed;
    private Exception? _failure;

    public RecordingEncoderQueue(IAudioVideoEncoder sink, int videoCapacity = 3, int audioCapacity = 10,
        int maximumAudioFrames = RecordingAudioFormat.SampleRate / 10, long maximumBufferedBytes = 128 * 1024 * 1024)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        if (sink.AudioState != AudioEncoderState.Available)
            throw new ArgumentException("An audio queue requires an available audio stream.", nameof(sink));
        ArgumentOutOfRangeException.ThrowIfLessThan(videoCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(audioCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAudioFrames, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBufferedBytes, 1);
        _videoCapacity = videoCapacity; _audioCapacity = audioCapacity;
        _maximumAudioFrames = maximumAudioFrames; _maximumBytes = maximumBufferedBytes;
    }

    public long PeakBufferedBytes { get { lock (_gate) return _peakBufferedBytes; } }
    public int PeakBufferedAudioFrames { get { lock (_gate) return _peakBufferedAudioFrames; } }
    public long MaximumQueuedLead100ns { get { lock (_gate) return _maximumQueuedLead100ns; } }
    public int PendingVideoCount { get { lock (_gate) return _video.Count; } }
    public int PendingAudioCount { get { lock (_gate) return _audio.Count; } }
    public long BufferedBytes { get { lock (_gate) return _bytes; } }

    /// <summary>False means capacity or stale input, with no ownership transfer or blocking.</summary>
    public bool TryEnqueueVideo(in EncoderFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame.Pixels);
        if (!double.IsFinite(frame.TimestampMs) || frame.TimestampMs < 0)
            throw new ArgumentOutOfRangeException(nameof(frame));
        long timestamp = checked((long)(frame.Format == VideoEncoderInputFormat.Nv12
            ? Math.Round(frame.TimestampMs * 10_000) : frame.TimestampMs * 10_000));
        lock (_gate)
        {
            RequireOpen();
            if (timestamp < _watermark || timestamp <= _lastVideoTimestamp
                || _video.Count >= _videoCapacity || frame.Pixels.LongLength > _maximumBytes - _bytes) return false;
            var owned = frame with { Pixels = (byte[])frame.Pixels.Clone() };
            _video.Enqueue((owned, timestamp));
            _lastVideoTimestamp = timestamp;
            _bytes += owned.Pixels.LongLength;
            TrackPeak();
            return true;
        }
    }

    public bool TryEnqueueAudio(in RecordingAudioBlock block)
    {
        long timestamp = block.Timestamp100ns;
        _ = RecordingAudioFormat.SampleTime100ns(block.EndSample);
        long bytes = (long)block.Samples.Length * sizeof(short);
        lock (_gate)
        {
            RequireOpen();
            if (timestamp < _watermark || block.StartSample < _lastAudioEnd) return false;
            if (block.FrameCount == 0) return true;
            if (_audio.Count >= _audioCapacity || block.FrameCount > _maximumAudioFrames - _audioFrames
                || bytes > _maximumBytes - _bytes) return false;
            var owned = new RecordingAudioBlock(block.StartSample, block.Samples.ToArray());
            _audio.Enqueue(owned);
            _lastAudioEnd = block.EndSample;
            _audioFrames += block.FrameCount;
            _bytes += bytes;
            TrackPeak();
            return true;
        }
    }

    /// <summary>
    /// The caller promises BOTH producers have supplied everything before this exclusive common
    /// watermark, including any required silence. Drain never waits for an absent source. Input
    /// before the watermark is rejected afterwards; equal-time samples remain admissible.
    /// </summary>
    public void DrainThrough(long exclusiveTimestamp100ns)
    {
        RequireOwner();
        ArgumentOutOfRangeException.ThrowIfNegative(exclusiveTimestamp100ns);
        lock (_gate)
        {
            RequireOpen();
            if (exclusiveTimestamp100ns < _watermark) throw new ArgumentOutOfRangeException(nameof(exclusiveTimestamp100ns));
            _watermark = exclusiveTimestamp100ns;
        }
        Drain(exclusiveTimestamp100ns);
    }

    public void Complete()
    {
        RequireOwner();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failure is not null) throw new InvalidOperationException("The encoder queue failed.", _failure);
            if (_completed) return;
            _closing = true;
        }
        try
        {
            Drain(long.MaxValue);
            _sink.Complete();
            lock (_gate) _completed = true;
        }
        catch (Exception ex) { lock (_gate) _failure = ex; throw; }
    }

    private void Drain(long limit)
    {
        while (true)
        {
            EncoderFrame video = default;
            RecordingAudioBlock audio = default;
            bool isVideo;
            long timestamp;
            lock (_gate)
            {
                long videoTime = _video.TryPeek(out var head) ? head.Timestamp : long.MaxValue;
                long audioTime = _audio.TryPeek(out var pcm) ? pcm.Timestamp100ns : long.MaxValue;
                timestamp = Math.Min(videoTime, audioTime);
                if (timestamp >= limit) return;
                isVideo = videoTime <= audioTime;
                if (isVideo)
                {
                    video = _video.Dequeue().Frame;
                    _bytes -= video.Pixels.LongLength;
                }
                else
                {
                    audio = _audio.Dequeue();
                    _bytes -= (long)audio.Samples.Length * sizeof(short);
                    _audioFrames -= audio.FrameCount;
                }
            }
            try
            {
                if (timestamp < _lastWritten) throw new InvalidOperationException("Encoder timestamps moved backwards.");
                if (isVideo) _sink.WriteFrame(video); else _sink.WriteAudio(audio);
                _lastWritten = timestamp;
            }
            catch (Exception ex) { lock (_gate) _failure = ex; throw; }
        }
    }

    private void TrackPeak()
    {
        _peakBufferedBytes = Math.Max(_peakBufferedBytes, _bytes);
        _peakBufferedAudioFrames = Math.Max(_peakBufferedAudioFrames, _audioFrames);
        long first = Math.Min(_video.TryPeek(out var frame) ? frame.Timestamp : long.MaxValue,
            _audio.TryPeek(out var pcm) ? pcm.Timestamp100ns : long.MaxValue);
        long end = Math.Max(_video.Count > 0 ? _lastVideoTimestamp : 0,
            _audio.Count > 0 ? RecordingAudioFormat.SampleTime100ns(_lastAudioEnd) : 0);
        if (first != long.MaxValue) _maximumQueuedLead100ns = Math.Max(_maximumQueuedLead100ns, end - first);
    }

    private void RequireOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failure is not null) throw new InvalidOperationException("The encoder queue failed.", _failure);
        if (_closing) throw new InvalidOperationException("The encoder queue is closed.");
    }

    private void RequireOwner()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Only the owner thread may drain or dispose the encoder queue.");
    }

    public void Dispose()
    {
        RequireOwner();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _closing = true;
            _video.Clear(); _audio.Clear(); _bytes = 0; _audioFrames = 0;
        }
        // The sink keeps its existing best-effort finalization on failure or early disposal.
        _sink.Dispose();
    }
}
