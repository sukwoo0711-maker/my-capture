using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MyCapture.App.Diagnostics;

/// <summary>Full-run observation only. No power configuration, input, timer-resolution or wake lock.</summary>
internal sealed class ResidentLongRunAwake : IDisposable
{
    private bool _subscribed;
    private bool _started;
    private ulong _start, _last;
    private int _suspends, _resumes;
    private long _firstPowerEvent, _lastPowerEvent;

    internal ResidentLongRunAwake()
    {
        // SystemEvents may deliver on its own event thread. The callback has no UI or IO work.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _subscribed = true;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Suspend) Interlocked.Increment(ref _suspends);
        else if (args.Mode == PowerModes.Resume) Interlocked.Increment(ref _resumes);
        else return;
        long timestamp = Stopwatch.GetTimestamp();
        _ = Interlocked.CompareExchange(ref _firstPowerEvent, timestamp, 0);
        Interlocked.Exchange(ref _lastPowerEvent, timestamp);
    }

    internal ResidentLongRunContract.AwakeSample BeginMeasurement()
    {
        if (!_subscribed || _started) throw new InvalidOperationException("Awake observer lifecycle differs.");
        _start = _last = ReadClock();
        _started = true;
        return Sample();
    }

    internal ResidentLongRunContract.AwakeSample Sample()
    {
        if (!_subscribed || !_started) throw new InvalidOperationException("Awake observation is not active.");
        ulong end = ReadClock();
        if (end < _last || end < _start) throw new InvalidOperationException("Awake clock moved backwards.");
        _last = end;
        var sample = new ResidentLongRunContract.AwakeSample(true, true, _start, end,
            (end - _start) / 10_000_000.0, Volatile.Read(ref _suspends), Volatile.Read(ref _resumes),
            Interlocked.Read(ref _firstPowerEvent), Interlocked.Read(ref _lastPowerEvent),
            Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        if (sample.SuspendCount != 0 || sample.ResumeCount != 0)
            throw new ResidentLongRunPowerInterruptedException(sample);
        return sample;
    }

    private static ulong ReadClock()
    {
        if (!QueryUnbiasedInterruptTime(out ulong value))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryUnbiasedInterruptTime failed.");
        return value;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    public void Dispose()
    {
        if (!_subscribed) return;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _subscribed = false;
    }
}

internal sealed class ResidentLongRunPowerInterruptedException(ResidentLongRunContract.AwakeSample sample)
    : InvalidOperationException("Suspend/resume was observed during the full-run lifetime.")
{
    internal ResidentLongRunContract.AwakeSample Sample { get; } = sample;
}
