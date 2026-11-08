using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using MyCapture.App.Themes;

namespace MyCapture.App.Capture;

/// <summary>The native background's UIA identity. External clients never keep a capture session alive.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class NativeSelectorAutomationProvider : IRawElementProviderSimple
{
    private readonly WeakReference<NativeCaptureOverlay> _owner;
    private readonly IntPtr _handle;
    private readonly string _name;

    internal NativeSelectorAutomationProvider(NativeCaptureOverlay owner)
    {
        _owner = new(owner);
        _handle = owner.Handle;
        _name = UiText.Get("Text_3816B940D53C");
    }

    public ProviderOptions ProviderOptions => ProviderOptions.ServerSideProvider;

    public IRawElementProviderSimple HostRawElementProvider
    {
        get
        {
            VerifyAvailable();
            return AutomationInteropProvider.HostProviderFromHandle(_handle);
        }
    }

    public object? GetPatternProvider(int patternId)
    {
        VerifyAvailable();
        return null;
    }

    public object? GetPropertyValue(int propertyId)
    {
        VerifyAvailable();
        if (propertyId == AutomationElementIdentifiers.NameProperty.Id) return _name;
        if (propertyId == AutomationElementIdentifiers.ControlTypeProperty.Id) return ControlType.Window.Id;
        if (propertyId == AutomationElementIdentifiers.ClassNameProperty.Id) return nameof(NativeCaptureOverlay);
        if (propertyId == AutomationElementIdentifiers.FrameworkIdProperty.Id) return "Win32";
        if (propertyId == AutomationElementIdentifiers.NativeWindowHandleProperty.Id) return unchecked((int)_handle.ToInt64());
        if (propertyId == AutomationElementIdentifiers.ProcessIdProperty.Id) return Environment.ProcessId;
        if (propertyId == AutomationElementIdentifiers.IsKeyboardFocusableProperty.Id) return true;
        if (propertyId == AutomationElementIdentifiers.HasKeyboardFocusProperty.Id) return HasKeyboardFocus();
        if (propertyId == AutomationElementIdentifiers.IsEnabledProperty.Id) return IsWindowEnabled(_handle);
        if (propertyId == AutomationElementIdentifiers.IsOffscreenProperty.Id) return !IsWindowVisible(_handle);
        if (propertyId == AutomationElementIdentifiers.IsControlElementProperty.Id
            || propertyId == AutomationElementIdentifiers.IsContentElementProperty.Id) return true;
        return null;
    }

    private void VerifyAvailable()
    {
        if (!_owner.TryGetTarget(out NativeCaptureOverlay? owner) || owner.IsClosed || owner.Handle != _handle
            || _handle == IntPtr.Zero || !NativeSelectorInterop.IsWindow(_handle))
            throw new ElementNotAvailableException();
    }

    private bool HasKeyboardFocus()
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        uint thread = GetWindowThreadProcessId(_handle, out _);
        return thread != 0 && GetGUIThreadInfo(thread, ref info) && info.Focus == _handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        internal uint Size, Flags;
        internal IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal NativeSelectorInterop.Rect CaretRectangle;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
}
