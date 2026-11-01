using System.Diagnostics;
using System.Threading;

namespace MyCapture.App;

/// <summary>
/// A launch notice owns its own native message loop: service construction and library loading
/// can block the WPF dispatcher without blocking either the delay or the notice's painting.
/// </summary>
internal sealed class LaunchStatusPresentation : IDisposable
{
    internal static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(400);
    internal static readonly TimeSpan SecondaryDuration = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private readonly ManualResetEvent _finished = new(false);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<ILaunchSurface> _createSurface;
    private readonly Action _visible;
    private readonly TimeSpan _delay;
    private readonly TimeSpan? _duration;
    private readonly Func<WaitHandle, int, bool> _wait;
    private readonly long _started = Stopwatch.GetTimestamp();
    private bool _complete;

    private LaunchStatusPresentation(bool quiet, bool secondary, Action visible, Func<ILaunchSurface>? createSurface,
        TimeSpan? delay, TimeSpan? duration, Func<WaitHandle, int, bool>? wait)
    {
        _visible = visible;
        _wait = wait ?? ((handle, milliseconds) => handle.WaitOne(milliseconds));
        _createSurface = createSurface ?? (() => new NativeLaunchSurface(secondary));
        _delay = delay ?? (secondary ? TimeSpan.Zero : ShowDelay);
        _duration = duration ?? (secondary ? SecondaryDuration : null);
        if (quiet)
        {
            _complete = true;
            _completion.SetResult();
            _finished.Dispose();
            return;
        }

        var thread = new Thread(Run) { IsBackground = true, Name = "MyCapture launch presentation" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal static LaunchStatusPresentation Start(bool quiet, Action visible, Func<ILaunchSurface>? createSurface = null,
        TimeSpan? delay = null, Func<WaitHandle, int, bool>? wait = null) =>
        new(quiet, false, visible, createSurface, delay, null, wait);

    internal static LaunchStatusPresentation Secondary(Action visible, Func<ILaunchSurface>? createSurface = null,
        TimeSpan? duration = null) => new(false, true, visible, createSurface, null, duration, null);

    internal Task Completion => _completion.Task;

    /// <summary>Serialized with showing: completing before the delay can never flash a late window.</summary>
    internal void Complete()
    {
        lock (_gate)
        {
            if (_complete) return;
            _complete = true;
            _finished.Set();
        }
    }

    public void Dispose() => Complete();

    private void Run()
    {
        ILaunchSurface? surface = null;
        try
        {
            if (FinishedBeforeDelay()) return;
            lock (_gate)
            {
                if (_complete) return;
                surface = _createSurface();
                surface.Show();
                _visible();
            }
            surface.Run(_finished, _duration);
        }
        catch (Exception ex)
        {
            // Presentation is optional; failures must not kill the resident from a worker thread.
            Debug.WriteLine($"Launch presentation unavailable: {ex.GetType().Name}");
        }
        finally
        {
            try { surface?.Dispose(); }
            finally
            {
                lock (_gate)
                {
                    _complete = true;
                    _finished.Dispose();
                }
                _completion.TrySetResult();
            }
        }
    }

    /// <summary>
    /// A wait timeout follows the system tick and can end slightly before a <see cref="Stopwatch"/>
    /// reports the delay; keep waiting for the remainder so the notice never appears early.
    /// </summary>
    private bool FinishedBeforeDelay()
    {
        TimeSpan remaining;
        do
        {
            remaining = _delay - Stopwatch.GetElapsedTime(_started);
            int milliseconds = remaining <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(remaining.TotalMilliseconds);
            if (_wait(_finished, milliseconds)) return true;
        }
        while (remaining > TimeSpan.Zero);
        return false;
    }
}

internal interface ILaunchSurface : IDisposable
{
    void Show();
    void Run(WaitHandle finished, TimeSpan? duration);
}
