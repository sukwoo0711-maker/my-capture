using MyCapture.Core.Primitives;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Display;

/// <summary>Reads the current mouse cursor position in virtual-desktop physical pixels.</summary>
/// <remarks>
/// A tiny wrapper over <c>GetCursorPos</c> so App code can ask "where is the pointer" without
/// reaching into the interop layer, and window/scroll modes can hit-test the exact point the
/// user is aiming at rather than a monitor-centre approximation.
/// </remarks>
public static class CursorLocator
{
    public static PointD GetPosition()
    {
        if (NativeMethods.GetCursorPos(out NativeMethods.POINT pt))
        {
            return new PointD(pt.X, pt.Y);
        }

        // No cursor (headless / locked session): fall back to the primary monitor centre so
        // callers still get a sane point instead of throwing.
        return MonitorEnumerator.GetPrimary().Bounds.Center;
    }
}
