using System.Windows;
using System.Windows.Media;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Capture;

/// <summary>
/// One hidden lift window made while nothing of a capture is on screen, so the next capture's lift
/// only loads it with the image and shows it (<see cref="CaptureFlight.TryLift"/>). Without one,
/// the lift waits about 35 ms for a new layered window right after the selector closes, the longest
/// step between the selection and the card.
/// </summary>
/// <remarks>
/// The spare is 1 x 1 and never shown, so it holds no surface, but while it lives WPF keeps its
/// layered-window drawing resources (about 2.5 MB), which is what makes the next window fast; the
/// app closes it a minute after the last capture. A window takes its scale from its display, and a
/// window moved to another scale is resized by Windows, so the spare lifts a capture only at the
/// scale it has; otherwise it is closed and the lift makes its own window as before.
/// </remarks>
internal sealed class CaptureFlightSpare
{
    internal static Func<Window, double> ReadScale { get; set; } = window => VisualTreeHelper.GetDpi(window).DpiScaleX;

    private CaptureFlightDecoration? _window;

    internal bool IsReady => _window is { IsClosed: false };

    internal CaptureFlightDecoration? WindowForTest => _window;

    /// <summary>Makes the spare at <paramref name="at"/> (physical pixels) unless one is ready. False when none was made.</summary>
    internal bool Prepare(PointD at, double scale)
    {
        if (IsReady) return false;
        CaptureFlightDecoration window = CaptureFlightDecoration.CreateSpare(at, scale);
        // A window a capture could see is never shown (ShowDecoration); there is no use keeping it.
        if (!window.IsCaptureExcluded)
        {
            window.Close();
            return false;
        }
        _window = window;
        return true;
    }

    /// <summary>
    /// Hands over the spare when it is at <paramref name="scale"/>. Null when there is none to use,
    /// and a spare at another scale is closed: the caller makes its own window.
    /// </summary>
    internal CaptureFlightDecoration? Take(double scale)
    {
        CaptureFlightDecoration? window = _window;
        _window = null;
        if (window is not { IsClosed: false }) return null;
        if (Math.Abs(ReadScale(window) - scale) < 0.001) return window;
        window.Close();
        return null;
    }

    internal void Release()
    {
        CaptureFlightDecoration? window = _window;
        _window = null;
        if (window is { IsClosed: false }) window.Close();
    }
}
