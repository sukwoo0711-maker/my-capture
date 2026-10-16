using System.Collections.Concurrent;

namespace MyCapture.App.Recording;

internal enum VideoPlayerKind { Gallery, Editor }
internal readonly record struct RangeVerificationCacheKey(VideoPlayerKind Player, bool StudioRange, int ScaleBucket);
internal sealed class RangeVerificationCache
{
    private readonly ConcurrentDictionary<RangeVerificationCacheKey, VideoRangeCorrection> _values = new();
    internal VideoRangeCorrection Guess(RangeVerificationCacheKey key) => _values.TryGetValue(key, out var value)
        ? value : key.StudioRange ? VideoRangeCorrection.Expand : VideoRangeCorrection.None;
    internal void Store(RangeVerificationCacheKey key, VideoRangeCorrection correction) => _values[key] = correction;
}

/// <summary>Dispatcher-owned state machine. Clock, presentation signal, sampler and decoder are injectable.</summary>
internal sealed class PresentedFrameVerification : IDisposable
{
    private static readonly double[] DelaysMs = [300, 500, 2000, 5000];
    private readonly Func<double> _clock;
    private readonly Func<bool> _hasPresented;
    private readonly Func<RangeVerificationFrame?> _sample;
    private readonly Func<RangeVerificationFrame, CancellationToken, Task<RangeVerificationResult>> _decode;
    private readonly Action<VideoRangeCorrection> _apply;
    private readonly Action<RangeVerificationResult> _log;
    private readonly RangeVerificationCache _cache;
    private readonly RangeVerificationCacheKey _key;
    private CancellationTokenSource? _pending;
    private Task<RangeVerificationResult>? _decodeTask;
    private double _due;
    private long _generation;
    private bool _closed, _running;
    private int _attempts;
    private readonly TaskCompletionSource<RangeVerificationResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PresentedFrameVerification(RangeVerificationCacheKey key, RangeVerificationCache cache,
        Func<double> clock, Func<bool> hasPresented, Func<RangeVerificationFrame?> sample,
        Func<RangeVerificationFrame, CancellationToken, Task<RangeVerificationResult>> decode,
        Action<VideoRangeCorrection> apply, Action<RangeVerificationResult> log)
    {
        _key = key; _cache = cache; _clock = clock; _hasPresented = hasPresented;
        _sample = sample; _decode = decode; _apply = apply; _log = log;
        _due = clock() + DelaysMs[0];
        apply(cache.Guess(key));
    }

    internal Task<RangeVerificationResult> Completion => _completion.Task;
    internal RangeVerificationResult? LastResult { get; private set; }
    internal int Samples { get; private set; }
    internal int Decodes { get; private set; }
    internal bool IsRunning => _running || _decodeTask is { IsCompleted: false };

    internal void PositionRequested()
    {
        if (_closed || Completion.IsCompleted) return;
        _generation++;
        _pending?.Cancel();
        _due = _clock() + DelaysMs[Math.Min(_attempts, DelaysMs.Length - 1)];
    }

    internal async Task TickAsync()
    {
        if (_closed || IsRunning || Completion.IsCompleted || _clock() < _due || !_hasPresented()) return;
        _running = true;
        long generation = ++_generation;
        using var pending = new CancellationTokenSource();
        _pending = pending;
        RangeVerificationFrame? sample = null;
        RangeVerificationResult? result = null;
        try
        {
            Samples++;
            sample = _sample(); // Synchronous: effect must be restored before this returns.
            if (sample is not null)
            {
                Decodes++;
                _decodeTask = _decode(sample, pending.Token);
                result = await _decodeTask.WaitAsync(TimeSpan.FromSeconds(3), pending.Token);
            }
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception ex)
        {
            result = new(null, new(255, 255, 255), [], 0, 0, 0, sample?.PositionMs ?? 0,
                "verification-error: " + ex.GetType().Name, ex.ToString());
        }
        finally
        {
            pending.Cancel();
            if (ReferenceEquals(_pending, pending)) _pending = null;
            _running = false;
        }
        if (_closed || generation != _generation) return;
        _attempts++;
        result ??= new(null, new(255, 255, 255), [], 0, 0, 0, sample?.PositionMs ?? 0, "frame-not-presented");
        LastResult = result = result with { Attempts = _attempts };
        if (result.Correction is { } correction)
        {
            _cache.Store(_key, correction);
            _apply(correction);
            _log(result);
            _completion.TrySetResult(result);
        }
        else if (_attempts >= DelaysMs.Length) _completion.TrySetResult(result);
        else _due = _clock() + DelaysMs[_attempts];
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _generation++;
        _pending?.Cancel();
        _completion.TrySetCanceled();
    }
}
