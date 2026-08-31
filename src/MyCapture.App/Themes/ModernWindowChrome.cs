using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MyCapture.Core.Platform;

namespace MyCapture.App.Themes;

/// <summary>
/// Opt-in attached behavior for native dark captions and Windows 11 rounded corners.
/// Unsupported DWM attributes fail silently so remote or non-DWM sessions retain normal
/// WPF chrome.
/// </summary>
public static class ModernWindowChrome
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(ModernWindowChrome),
        new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) =>
        element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) =>
        (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not Window window || e.NewValue is not true)
        {
            return;
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Apply(window);
        }
        else
        {
            window.SourceInitialized += OnWindowSourceInitialized;
        }
    }

    private static void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.SourceInitialized -= OnWindowSourceInitialized;
            Apply(window);
        }
    }

    private static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindows()
            || !WindowsSupportPolicy.IsSupportedHost(Environment.OSVersion.Version))
        {
            return;
        }

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int enabled = 1;
        // Attribute 20 is the documented DWMWA_USE_IMMERSIVE_DARK_MODE value; 19 is the
        // pre-release value some DWM builds still answer, kept as a silent fallback.
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }

        int rounded = 2; // DWMWCP_ROUND: available on every supported (Windows 11) host.
        _ = DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
