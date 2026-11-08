using System.Windows.Automation.Provider;

namespace MyCapture.App.Capture;

internal sealed partial class NativeCaptureOverlay
{
    private IRawElementProviderSimple? _automationProvider;

    // UIA asks for this on demand; normal construction, first paint, and chrome readiness do not.
    internal IRawElementProviderSimple AutomationProvider =>
        _automationProvider ??= new NativeSelectorAutomationProvider(this);

    internal bool AutomationProviderCreatedForTest => _automationProvider is not null;

    internal IntPtr HandleAutomationMessage(IntPtr hwnd, IntPtr wParam, IntPtr lParam)
    {
        if (_closed || hwnd != Handle || unchecked((int)lParam.ToInt64()) != AutomationInteropProvider.RootObjectId)
            return IntPtr.Zero;
        return AutomationInteropProvider.ReturnRawElementProvider(hwnd, wParam, lParam, AutomationProvider);
    }
}
