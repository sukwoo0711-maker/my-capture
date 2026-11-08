using System.Windows.Input;

namespace MyCapture.App.Capture;

internal sealed partial class NativeCaptureOverlay
{
    private bool? _pendingChromeFocus;

    private void FocusChrome(bool reverse)
    {
        if (_chrome?.FocusFromBackground(reverse) == true) _pendingChromeFocus = null;
        else _pendingChromeFocus = reverse;
    }

    private void ReturnFocusAfterPointer()
    {
        if (_chrome?.KeyboardActivation != true && InputManager.Current.MostRecentInputDevice is MouseDevice) Activate();
    }
}
