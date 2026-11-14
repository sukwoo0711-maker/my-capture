using System.Diagnostics;
using System.Globalization;
using System.IO;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Diagnostics;

/// <summary>Explicit diagnostic only. Records timestamps and counts, never captured PCM or pixels.</summary>
internal sealed class RecordingAudioDiagnosticTrace(string directory) : IDisposable
{
    private readonly string _directory = DiagnosticOutputPaths.ExistingDirectory(directory);
    private readonly long[] _gapHistogram = new long[10_001];
    private long _previousWrite, _gapCount;
    private long _lastSubmitted = -1;
    private double _maxGap;
    private int _peakPcm;
    private bool _bound;
    internal ManualResetEventSlim FirstVideo { get; } = new();
    internal long VideoFrames { get; private set; }
    internal long AudioBlocks { get; private set; }
    internal long AudioFrames { get; private set; }
    internal long AudioEndSample { get; private set; }
    internal long? FirstVideoPts100ns { get; private set; }
    internal long? LastVideoPts100ns { get; private set; }
    internal long? FirstAudioPts100ns { get; private set; }
    internal long? LastAudioEnd100ns { get; private set; }
    internal bool AudioIsContiguous { get; private set; } = true;
    internal bool PresentationTimesOrdered { get; private set; } = true;
    internal AudioEncoderState AudioState { get; private set; }
    internal string? AudioUnavailableReason { get; private set; }

    internal IVideoEncoder Bind(IVideoEncoder sink)
    {
        if (_bound) throw new InvalidOperationException("A diagnostic trace has one recording owner.");
        _bound = true;
        StreamWriter stream;
        try
        {
            stream = new StreamWriter(new FileStream(DiagnosticOutputPaths.Child(_directory, "stream-timestamps.csv"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65_536), new System.Text.UTF8Encoding(false), 65_536);
            stream.WriteLine("stream,pts100ns,duration100ns,frames,writeQpc");
        }
        catch { sink.Dispose(); throw; }
        AudioState = (sink as IAudioVideoEncoder)?.AudioState ?? AudioEncoderState.Disabled;
        AudioUnavailableReason = (sink as IAudioVideoEncoder)?.AudioUnavailableReason;
        return new Sink(this, sink, stream);
    }

    internal object Snapshot() => new
    {
        VideoFrames, AudioBlocks, AudioFrames, AudioEndSample, FirstVideoPts100ns, LastVideoPts100ns,
        FirstAudioPts100ns, LastAudioEnd100ns, AudioIsContiguous, PresentationTimesOrdered, AudioState,
        AudioUnavailableReason, PeakPcm16 = _peakPcm, AllSubmittedAudioIsSilence = AudioFrames > 0 && _peakPcm == 0,
        VideoWriteIntervals = new { Samples = _gapCount, P50Ms = Percentile(.50), P95Ms = Percentile(.95),
            P99Ms = Percentile(.99), MaxMs = _maxGap, HistogramResolutionMs = .1 },
        TimestampFile = "stream-timestamps.csv",
    };

    internal bool AllAudioIsSilence => AudioFrames > 0 && _peakPcm == 0;

    private double? Percentile(double fraction)
    {
        if (_gapCount == 0) return null;
        long target = (long)Math.Ceiling(_gapCount * fraction), cumulative = 0;
        for (int index = 0; index < _gapHistogram.Length; index++)
        {
            cumulative += _gapHistogram[index];
            if (cumulative >= target) return index == _gapHistogram.Length - 1 ? _maxGap : index / 10d;
        }
        return _maxGap;
    }

    private void ObservePts(long pts)
    {
        PresentationTimesOrdered &= pts >= _lastSubmitted;
        _lastSubmitted = pts;
    }

    public void Dispose() => FirstVideo.Dispose();

    private sealed class Sink(RecordingAudioDiagnosticTrace trace, IVideoEncoder inner, StreamWriter stream) : IAudioVideoEncoder
    {
        private bool _disposed;
        public int Width => inner.Width;
        public int Height => inner.Height;
        public AudioEncoderState AudioState => (inner as IAudioVideoEncoder)?.AudioState ?? AudioEncoderState.Disabled;
        public string? AudioUnavailableReason => (inner as IAudioVideoEncoder)?.AudioUnavailableReason;
        public void WriteFrame(in EncoderFrame frame)
        {
            inner.WriteFrame(frame);
            long now = Stopwatch.GetTimestamp();
            long pts = checked((long)(frame.Format == VideoEncoderInputFormat.Nv12
                ? Math.Round(frame.TimestampMs * 10_000) : frame.TimestampMs * 10_000));
            trace.ObservePts(pts);
            trace.FirstVideoPts100ns ??= pts;
            trace.LastVideoPts100ns = pts;
            trace.VideoFrames++;
            if (trace._previousWrite != 0)
            {
                double gap = Stopwatch.GetElapsedTime(trace._previousWrite, now).TotalMilliseconds;
                trace._gapHistogram[Math.Clamp((int)Math.Ceiling(gap * 10), 0, trace._gapHistogram.Length - 1)]++;
                trace._maxGap = Math.Max(trace._maxGap, gap);
                trace._gapCount++;
            }
            trace._previousWrite = now;
            stream.WriteLine(string.Create(CultureInfo.InvariantCulture, $"video,{pts},,1,{now}"));
            trace.FirstVideo.Set();
        }
        public void WriteAudio(in RecordingAudioBlock block)
        {
            if (inner is not IAudioVideoEncoder audio) throw new InvalidOperationException("Audio sink is unavailable.");
            audio.WriteAudio(block);
            trace.ObservePts(block.Timestamp100ns);
            trace.AudioIsContiguous &= block.StartSample == trace.AudioEndSample;
            trace.FirstAudioPts100ns ??= block.Timestamp100ns;
            trace.LastAudioEnd100ns = RecordingAudioFormat.SampleTime100ns(block.EndSample);
            trace.AudioBlocks++;
            trace.AudioFrames += block.FrameCount;
            trace.AudioEndSample = block.EndSample;
            foreach (short sample in block.Samples.Span) trace._peakPcm = Math.Max(trace._peakPcm, Math.Abs((int)sample));
            stream.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"audio,{block.Timestamp100ns},{block.Duration100ns},{block.FrameCount},{Stopwatch.GetTimestamp()}"));
        }
        public void Complete() { inner.Complete(); stream.Flush(); }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { inner.Dispose(); } finally { stream.Dispose(); }
        }
    }
}
