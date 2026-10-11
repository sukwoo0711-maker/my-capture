using System.Diagnostics;
using System.Windows.Threading;

namespace MyCapture.App.Themes;

/// <summary>
/// Spends a transient message's display time only while nobody is reading it.
/// </summary>
/// <remarks>
/// The owner says when the message is being read (pointer over it, keyboard focus in it); the
/// countdown then stands still and resumes with the time that was left. A message without a
/// display time (progress, a failure to act on) never expires. Tests drive
/// <see cref="Advance"/> directly instead of waiting on the dispatcher.
/// </remarks>
internal sealed class FeedbackCountdown
{
    /// <summary>How often a running countdown samples the clock.</summary>
    internal static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _isBeingRead;
    private readonly Action _expired;
    private long _lastTick;

    internal FeedbackCountdown(Dispatcher dispatcher, Func<bool> isBeingRead, Action expired)
    {
        _isBeingRead = isBeingRead;
        _expired = expired;
        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TickInterval };
        _timer.Tick += (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            TimeSpan elapsed = Stopwatch.GetElapsedTime(_lastTick, now);
            _lastTick = now;
            Advance(elapsed);
        };
    }

    /// <summary>Display time left, or <see langword="null"/> when nothing is counting down.</summary>
    internal TimeSpan? Remaining { get; private set; }

    /// <summary>Whether a message is counting down (paused or not).</summary>
    internal bool IsRunning => _timer.IsEnabled;

    /// <summary>Starts a fresh countdown; <see langword="null"/> keeps the message until <see cref="Stop"/>.</summary>
    internal void Start(TimeSpan? displayTime)
    {
        Remaining = displayTime;
        if (displayTime is null)
        {
            _timer.Stop();
            return;
        }

        _lastTick = Stopwatch.GetTimestamp();
        _timer.Start();
    }

    internal void Stop()
    {
        _timer.Stop();
        Remaining = null;
    }

    /// <summary>Spends <paramref name="elapsed"/> unless the message is being read; expires at zero.</summary>
    internal void Advance(TimeSpan elapsed)
    {
        if (Remaining is not { } remaining || _isBeingRead())
        {
            return;
        }

        remaining -= elapsed;
        if (remaining > TimeSpan.Zero)
        {
            Remaining = remaining;
            return;
        }

        Stop();
        _expired();
    }
}
