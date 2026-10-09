using Microsoft.Win32.SafeHandles;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Recording;

/// <summary>
/// Waits between recorded frames with millisecond-or-better precision.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Thread.Sleep(int)"/> wakes on the system timer tick, 15.6 ms by default. A
/// 33.3 ms wait then lasts about 46.9 ms and a 16.7 ms wait about 31.2 ms, so an idle desktop
/// recording that targets 30 fps produced 21.4 fps and one that targets 60 fps produced
/// 33.6 fps. Only a process that happened to run a WPF render loop got a finer tick.
/// </para>
/// <para>
/// A waitable timer created with <c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>, which every
/// build from the Windows 11 22000 support floor provides, wakes close to the requested time
/// for this handle only. It deliberately avoids <c>timeBeginPeriod</c>, which
/// raises the timer resolution for the whole system and its power use. If the timer cannot be
/// created or armed, the delay falls back to the previous whole-millisecond sleep.
/// </para>
/// <para>
/// Owned and used by the recorder's capture thread only.
/// </para>
/// </remarks>
internal sealed class RecordingFrameDelay : IDisposable
{
    // Below this a wait costs more than it saves; the recorder simply re-checks the clock.
    private const double MinimumHighResolutionWaitMs = 0.05;

    // Waits are already capped by the recorder; this only bounds a timer that never fires.
    private const uint TimeoutSlackMs = 100;

    private SafeWaitHandle? _timer;

    private RecordingFrameDelay(SafeWaitHandle? timer)
    {
        _timer = timer;
    }

    /// <summary>True while waits use the high-resolution timer rather than the sleep fallback.</summary>
    public bool IsHighResolution => _timer is not null;

    /// <summary>
    /// Creates the high-resolution timer, or the sleep fallback when it is unavailable or
    /// <paramref name="allowHighResolution"/> is false.
    /// </summary>
    public static RecordingFrameDelay Create(bool allowHighResolution = true)
    {
        if (!allowHighResolution)
        {
            return new RecordingFrameDelay(null);
        }

        SafeWaitHandle timer = NativeMethods.CreateWaitableTimerEx(
            IntPtr.Zero,
            null,
            NativeMethods.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            NativeMethods.TIMER_MODIFY_STATE | NativeMethods.SYNCHRONIZE);
        if (timer.IsInvalid)
        {
            timer.Dispose();
            return new RecordingFrameDelay(null);
        }

        return new RecordingFrameDelay(timer);
    }

    /// <summary>Blocks the calling thread for about <paramref name="milliseconds"/>.</summary>
    /// <remarks>
    /// The fallback keeps the previous behaviour exactly: whole milliseconds only, and a
    /// sub-millisecond remainder returns immediately so the caller re-checks its clock.
    /// </remarks>
    public void Wait(double milliseconds)
    {
        if (!(milliseconds > 0))
        {
            return;
        }

        if (_timer is { } timer)
        {
            if (milliseconds < MinimumHighResolutionWaitMs)
            {
                return;
            }

            // Negative due time is relative, in 100 ns units.
            long dueTime = -(long)Math.Ceiling(milliseconds * TimeSpan.TicksPerMillisecond);
            if (NativeMethods.SetWaitableTimer(timer, in dueTime, 0, IntPtr.Zero, IntPtr.Zero, false)
                && NativeMethods.WaitForSingleObject(timer, (uint)Math.Ceiling(milliseconds) + TimeoutSlackMs) != NativeMethods.WAIT_FAILED)
            {
                return;
            }

            // The handle stopped working: keep recording with the coarse sleep from now on.
            timer.Dispose();
            _timer = null;
        }

        if (milliseconds >= 1)
        {
            Thread.Sleep((int)milliseconds);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
