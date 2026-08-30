using MyCapture.Core.Primitives;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Capture;

/// <summary>Injects one scroll step into a previously resolved target window.</summary>
public interface IScrollInputSink
{
    bool ScrollDown(IntPtr targetWindow, PointD screenPoint, int notches);
}

/// <summary>
/// Posts WM_MOUSEWHEEL to the child under the captured viewport after confirming that child
/// still belongs to the originally selected top-level window. This avoids global SendInput
/// accidentally scrolling the tray or another app while the user invokes cancellation.
/// </summary>
public sealed class NativeScrollInputSink : IScrollInputSink
{
    public bool ScrollDown(IntPtr targetWindow, PointD screenPoint, int notches)
    {
        if (targetWindow == IntPtr.Zero)
        {
            return false;
        }

        if (notches == 0)
        {
            return true;
        }

        var point = new NativeMethods.POINT
        {
            X = (int)Math.Round(screenPoint.X),
            Y = (int)Math.Round(screenPoint.Y),
        };
        IntPtr child = NativeMethods.WindowFromPoint(point);
        if (child == IntPtr.Zero)
        {
            return false;
        }

        IntPtr root = NativeMethods.GetAncestor(child, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero)
        {
            root = child;
        }

        if (root != targetWindow)
        {
            return false;
        }

        int delta = checked(-notches * NativeMethods.WHEEL_DELTA);
        uint packedDelta = unchecked((uint)(delta & 0xFFFF) << 16);
        uint packedPoint = unchecked((uint)(ushort)point.X | ((uint)(ushort)point.Y << 16));

        return NativeMethods.PostMessage(
            child,
            NativeMethods.WM_MOUSEWHEEL,
            new IntPtr(unchecked((int)packedDelta)),
            new IntPtr(unchecked((int)packedPoint)));
    }
}
