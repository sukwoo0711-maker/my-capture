using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyCapture.App.Diagnostics;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

public partial class App
{
    private ResidentRenderIdleController? _residentRenderIdle;
    private ResidentWindowActivity? _residentWindowActivity;
    private DispatcherTimer? _residentIdleTimer;
    private Exception? _residentIdleFailure;
    private RenderMode _residentOriginalRenderMode;

    private void InitializeResidentRenderIdle()
    {
        if (s_residentProbe is not { IdleRelease: not ResidentIdleReleaseVariant.None } probe) return;
        NativeMessageWindow shell = _services!.GetRequiredService<NativeMessageWindow>();
        _residentOriginalRenderMode = RenderOptions.ProcessRenderMode;
        var clock = Stopwatch.StartNew();
        _residentRenderIdle = new ResidentRenderIdleController(TimeSpan.FromSeconds(probe.IdleSeconds), () => clock.Elapsed,
            probe.IdleRelease == ResidentIdleReleaseVariant.ProcessMode
                ? () => RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly
                : shell.ReleaseRenderKeepAlive,
            probe.IdleRelease == ResidentIdleReleaseVariant.ProcessMode
                ? () => RenderOptions.ProcessRenderMode = _residentOriginalRenderMode
                : shell.RestoreRenderKeepAlive);
        _residentWindowActivity = new ResidentWindowActivity(BeforeResidentUse);
        _residentIdleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _residentIdleTimer.Tick += (_, _) =>
        {
            try
            {
                _residentRenderIdle.Poll(ResidentWindowActivity.HasVisibleProcessWindow(), ResidentWindowActivity.InputIdleTime());
            }
            catch (Exception error)
            {
                _residentIdleFailure ??= error;
                _residentIdleTimer.Stop();
            }
        };
        _residentIdleTimer.Start();
    }

    private void BeforeResidentUse() => _residentRenderIdle?.BeforeUse();

    private void CheckResidentIdleFailures()
    {
        Exception? error = _residentIdleFailure ?? _residentWindowActivity?.CallbackFailure
            ?? _services?.GetRequiredService<NativeMessageWindow>().CallbackFailure;
        if (error is not null) throw new InvalidOperationException("The resident idle-release prototype failed.", error);
    }

    private bool StopResidentRenderIdle()
    {
        _residentIdleTimer?.Stop();
        _residentRenderIdle = null;
        bool stopped = true;
        try
        {
            _residentWindowActivity?.Dispose();
            _residentWindowActivity = null;
        }
        catch (Exception error)
        {
            // A failed unhook stays disabled and rooted by both the app and ActiveHooks. It must
            // not prevent the original tray, pins, clipboard isolation and persistence cleanup.
            _residentIdleFailure ??= error;
            _log?.LogError(error, "Could not stop resident idle-release observation");
            stopped = false;
        }
        try
        {
            if (s_residentProbe?.IdleRelease == ResidentIdleReleaseVariant.ProcessMode)
                RenderOptions.ProcessRenderMode = _residentOriginalRenderMode;
        }
        catch (Exception error)
        {
            _residentIdleFailure ??= error;
            _log?.LogError(error, "Could not restore resident render mode during shutdown");
            stopped = false;
        }
        return stopped;
    }
}
