using System.Windows.Threading;

namespace MyCapture.App.Threading;

/// <summary>
/// Runs an action once on the calling thread's dispatcher after a delay, at Background priority.
/// Disposing the returned handle cancels it. Callers expose it as a replaceable scheduler so
/// tests can move time themselves instead of waiting on the wall clock.
/// </summary>
internal static class DispatcherDelay
{
    internal static IDisposable Schedule(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return new TimerHandle(timer);
    }

    private sealed class TimerHandle(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
