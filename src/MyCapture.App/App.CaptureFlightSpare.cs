using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;

namespace MyCapture.App;

public partial class App
{
    /// <summary>
    /// How long the spare lift window stays after the last capture, as long as a translation model
    /// or the diagram renderer after its last use. Captures in a row find it ready; an app left
    /// alone holds none, since it keeps WPF's layered-window drawing resources (about 2.5 MB).
    /// </summary>
    internal static readonly TimeSpan FlightSpareIdleRelease = TimeSpan.FromSeconds(60);

    // Made on first use: the app's test shells skip field initializers.
    private CaptureFlightSpare? _flightSpare;
    private DispatcherTimer? _flightSpareRelease;
    private RectD? _lastLiftSource;
    private bool _flightSpareQueued;

    /// <summary>
    /// Makes the next capture's lift window (<see cref="CaptureFlightSpare"/>) on an idle turn after
    /// each capture, on the display of that capture, and closes it a minute after the last one.
    /// Without a card or a flight to come (another after-capture mode, animations off), the spare
    /// is closed at once.
    /// </summary>
    private void QueueCaptureFlightSpare()
    {
        // The app's test shells have no services, nor a dispatcher to queue on.
        if (_flightSpareQueued || _services is null) return;
        _flightSpareQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            _flightSpareQueued = false;
            if (_shutdownRequested || Dispatcher.HasShutdownStarted) return;
            if (_settings?.General.AfterCapture != AfterCaptureAction.QuickAccess
                || s_residentProbe?.Has("no-flight-spare") == true
                || !CaptureFlight.IsEligible(FluidMotion.AnimationsEnabled, CaptureFlight.ReadRemoteSession(), CaptureFlight.ReadLayeredSupport()))
            {
                _flightSpare?.Release();
                return;
            }
            // Not while the selector or a flight is on screen, which would miss frames meanwhile;
            // the flight queues the spare again when it ends.
            if (_overlay?.IsActive == true || _captureFlight is { IsDone: false }) return;
            if (_flightSpare?.IsReady == true)
            {
                ArmFlightSpareRelease();
                return;
            }
            try
            {
                MonitorInfo monitor = _lastLiftSource is { } source
                    && MonitorWindowPlacement.MonitorFor(CaptureFlight.ReadMonitors(), source) is { } last
                    ? last
                    : MonitorEnumerator.GetPrimary();
                if ((_flightSpare ??= new CaptureFlightSpare()).Prepare(new PointD(monitor.Bounds.Left, monitor.Bounds.Top), monitor.ScaleFactor))
                    ArmFlightSpareRelease();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // The flight is decoration: the next lift makes its own window.
                _log?.LogDebug(ex, "Could not prepare the capture lift window");
            }
        }));
    }

    private void ArmFlightSpareRelease()
    {
        if (_flightSpareRelease is null)
        {
            _flightSpareRelease = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = FlightSpareIdleRelease };
            _flightSpareRelease.Tick += OnFlightSpareIdle;
        }
        _flightSpareRelease.Stop();
        _flightSpareRelease.Start();
    }

    private void OnFlightSpareIdle(object? sender, EventArgs e)
    {
        _flightSpareRelease?.Stop();
        // A capture under way may still take the spare: a minute after it, or after the selector
        // closes without one, the spare is looked at again.
        if (_overlay?.IsActive == true || _captureFlight is { IsDone: false })
        {
            ArmFlightSpareRelease();
            return;
        }
        _flightSpare?.Release();
    }
}
