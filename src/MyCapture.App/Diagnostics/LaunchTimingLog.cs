using System.Diagnostics;

namespace MyCapture.App.Diagnostics;

internal enum LaunchActivationKind { Regular, Background, StartupTask, Secondary }
internal enum LaunchVisibleKind { Starting, SecondaryNotice, Presentation }

/// <summary>At most two fixed-schema lines, buffered until the file logger is available.</summary>
internal sealed class LaunchTimingLog
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly object _gate = new();
    private readonly List<string> _pending = [];
    private Action<string>? _append;
    private bool _firstVisible;

    internal void Activation(LaunchActivationKind kind) => Record(SafeLogLine.LaunchActivation(DateTimeOffset.UtcNow, kind));

    internal void FirstVisible(LaunchVisibleKind kind)
    {
        lock (_gate)
        {
            if (_firstVisible) return;
            _firstVisible = true;
            Record(SafeLogLine.LaunchFirstVisible(DateTimeOffset.UtcNow, kind,
                (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds));
        }
    }

    internal void Attach(Action<string> append)
    {
        lock (_gate)
        {
            _append = append;
            foreach (string line in _pending) append(line);
            _pending.Clear();
        }
    }

    private void Record(string line)
    {
        lock (_gate)
        {
            if (_append is { } append) append(line);
            else _pending.Add(line);
        }
    }
}
