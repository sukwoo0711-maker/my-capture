namespace MyCapture.Core.Recording;

/// <summary>
/// Estimates device frames per QPC second from capture timestamps, never packet arrival times.
/// A device generation/discontinuity needs a fresh estimator. Invalid or backwards observations
/// do not change its anchor. The owner can query phase as well as slope before accepting PCM.
/// With trusted observations at most 10 ms apart, the window spans at most 2.01 seconds.
/// A -500 to +500 ppm step therefore contributes at most 2.01 ms of transient rate lag;
/// timestamp quantization, nominal conversion and the corrector's lookahead are separate.
/// Sparse observations do not carry that lag bound, and a timestamp discontinuity requires
/// source silence and a fresh anchor at the platform boundary.
/// </summary>
public sealed class RecordingAudioDriftEstimator
{
    public const long ObservationWindow100ns = 20_000_000;
    private const int MaximumObservations = 256;
    private readonly Queue<(long Position, long Qpc100ns)> _observations = [];
    private long _lastPosition;
    private long _lastQpc100ns;
    private bool _hasAnchor;

    public RecordingAudioDriftEstimator(int nominalSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nominalSampleRate);
        NominalSampleRate = nominalSampleRate;
    }

    public int NominalSampleRate { get; }
    public bool HasEstimate { get; private set; }
    public double RateRatio { get; private set; } = 1;
    public double PartsPerMillion => (RateRatio - 1) * 1_000_000;

    public bool Observe(long devicePosition, long qpc100ns)
    {
        if (devicePosition < 0 || qpc100ns < 0) return false;
        if (!_hasAnchor)
        {
            _lastPosition = devicePosition;
            _lastQpc100ns = qpc100ns;
            _observations.Enqueue((devicePosition, qpc100ns));
            _hasAnchor = true;
            return true;
        }
        if (devicePosition <= _lastPosition || qpc100ns <= _lastQpc100ns) return false;
        var anchor = _observations.Peek();
        long cutoff = qpc100ns - ObservationWindow100ns;
        foreach (var observation in _observations)
        {
            if (observation.Qpc100ns > cutoff) break;
            anchor = observation;
        }
        double ratio = (devicePosition - anchor.Position) * (double)RecordingAudioFormat.TicksPerSecond
            / (qpc100ns - anchor.Qpc100ns) / NominalSampleRate;
        // A timestamp reset or a missing/discontinuous device position is not clock drift.
        if (!double.IsFinite(ratio) || Math.Abs(ratio - 1) > 0.01) return false;
        RateRatio = ratio;
        _lastPosition = devicePosition;
        _lastQpc100ns = qpc100ns;
        _observations.Enqueue((devicePosition, qpc100ns));
        while (_observations.Count > MaximumObservations
            || (_observations.Count > 1 && _observations.Peek().Qpc100ns < anchor.Qpc100ns))
            _observations.Dequeue();
        HasEstimate = true;
        return true;
    }

    public double DevicePositionAt(long qpc100ns)
    {
        if (!HasEstimate) throw new InvalidOperationException("Two valid clock observations are required.");
        return _lastPosition + (qpc100ns - _lastQpc100ns) * (NominalSampleRate * RateRatio / RecordingAudioFormat.TicksPerSecond);
    }

    /// <summary>Device-clock phase error in 48 kHz frames, independent of video frame drops.</summary>
    public double OutputPhaseError(long devicePosition, long qpc100ns) =>
        (devicePosition - DevicePositionAt(qpc100ns)) * RecordingAudioFormat.SampleRate / (NominalSampleRate * RateRatio);
}

/// <summary>
/// Small asynchronous correction after nominal conversion to 48 kHz stereo. It maintains a
/// continuous fractional input position across packets and ratio changes, so correction never
/// drops or repeats a whole sample. Linear interpolation adds at most one input frame of
/// lookahead (20.84 us); it is not the anti-aliasing filter for 44.1/96 kHz conversion.
/// That nominal conversion belongs to the platform resampler before this stage.
/// </summary>
public sealed class RecordingAudioDriftCorrector
{
    public const double MaximumCorrectionPpm = 1_000;
    private long _inputFrames;
    private double _nextInputPosition;
    private float _previousLeft;
    private float _previousRight;

    public long InputFrames => _inputFrames;
    public long OutputFrames { get; private set; }
    public double NextInputPosition => _nextInputPosition;

    public static int MaximumOutputFrames(int inputFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputFrames);
        return checked((int)Math.Ceiling(inputFrames / (1 - MaximumCorrectionPpm / 1_000_000)) + 2);
    }

    /// <summary>
    /// Consumes a complete input block. Caller supplies at least MaximumOutputFrames * 2
    /// floats of output storage. A positive drift ratio consumes input slightly faster.
    /// The unrendered last fractional position is completed with the next block's first sample.
    /// </summary>
    public int Process(ReadOnlySpan<float> inputStereo, Span<float> outputStereo, double rateRatio)
    {
        if (inputStereo.Length % 2 != 0) throw new ArgumentException("Complete stereo frames are required.", nameof(inputStereo));
        if (!double.IsFinite(rateRatio) || Math.Abs(rateRatio - 1) > MaximumCorrectionPpm / 1_000_000 + 1e-12)
            throw new ArgumentOutOfRangeException(nameof(rateRatio));
        int frames = inputStereo.Length / 2;
        if (outputStereo.Length < MaximumOutputFrames(frames) * 2)
            throw new ArgumentException("Output storage is too small.", nameof(outputStereo));
        if (frames == 0) return 0;
        long end = checked(_inputFrames + frames);
        int written = 0;
        while (_nextInputPosition <= end - 1)
        {
            long lower = (long)Math.Floor(_nextInputPosition);
            int index = checked((int)(lower - _inputFrames));
            float fraction = (float)(_nextInputPosition - lower);
            float left = index < 0 ? _previousLeft : inputStereo[index * 2];
            float right = index < 0 ? _previousRight : inputStereo[index * 2 + 1];
            int upper = Math.Min(index + 1, frames - 1);
            outputStereo[written * 2] = left + (inputStereo[upper * 2] - left) * fraction;
            outputStereo[written * 2 + 1] = right + (inputStereo[upper * 2 + 1] - right) * fraction;
            written++;
            _nextInputPosition += rateRatio;
        }
        _previousLeft = inputStereo[^2];
        _previousRight = inputStereo[^1];
        _inputFrames = end;
        OutputFrames = checked(OutputFrames + written);
        return written;
    }
}
