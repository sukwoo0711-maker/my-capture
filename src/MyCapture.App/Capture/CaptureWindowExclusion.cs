using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Themes;

namespace MyCapture.App.Capture;

internal static class CaptureWindowExclusion
{
    private sealed class AppliedResult { internal bool Succeeded; }
    private static readonly ConditionalWeakTable<Window, AppliedResult> Applied = new();

    internal static bool WasApplied(Window window) =>
        Applied.TryGetValue(window, out AppliedResult? result) && result.Succeeded;

    internal static void RecordResult(Window window, bool succeeded) =>
        Applied.GetValue(window, static _ => new AppliedResult()).Succeeded = succeeded;

    private static Func<bool> s_layeredWindowProbe = ProbeLayeredWindow;
    private static Lazy<bool> s_supportsLayeredWindows = CreateCapabilityCache();

    internal static ILogger Logger { get; set; } = NullLogger.Instance;

    // Replacing the probe starts a new cache; tests restore the original delegate in finally.
    internal static Func<bool> LayeredWindowProbe
    {
        get => s_layeredWindowProbe;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            s_layeredWindowProbe = value;
            s_supportsLayeredWindows = CreateCapabilityCache();
        }
    }

    internal static bool SupportsLayeredWindows
    {
        get
        {
            // All callers construct WPF surfaces. Marshal an off-thread first read to the app's
            // UI dispatcher; isolated STA tests can also evaluate the probe without an Application.
            if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
                return dispatcher.Invoke(() => SupportsLayeredWindows);
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Capture exclusion capability must be probed on a UI thread.");
            return s_supportsLayeredWindows.Value;
        }
    }

    private static Lazy<bool> CreateCapabilityCache() => new(() =>
    {
        bool supported = s_layeredWindowProbe();
        Logger.LogInformation("Capture exclusion mode: {Mode}",
            supported ? "layered windows" : "opaque windows with recording control region");
        return supported;
    });

    private static bool ProbeLayeredWindow()
    {
        var window = new Window
        {
            Style = null,
            AllowsTransparency = true,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = Brushes.Transparent,
        };
        try
        {
            // No Show(): this is a hidden per-pixel-alpha HWND, including on Windows 10,
            // where display affinity returns misleading ERROR_NOT_ENOUGH_MEMORY (8).
            return TryApply(window);
        }
        finally
        {
            window.Close();
        }
    }

    // EnsureHandle creates an invisible HWND, so failure is handled before any UI pixels appear.
    internal static bool TryApply(Window window)
    {
        window.Dispatcher.VerifyAccess();
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        bool succeeded = TryApply(handle);
        RecordResult(window, succeeded);
        return succeeded;
    }

    internal static bool TryApply(IntPtr handle) =>
        handle != IntPtr.Zero && SetWindowDisplayAffinity(handle, 0x11);

    // Opaque backing also covers antialiasing, translucent theme brushes and content animations.
    internal static Brush OpaqueBackground()
    {
        Color fallback = Color.FromRgb(0x15, 0x1E, 0x2B);
        Brush token = ThemeBrushes.Follow("Surface.Floating", fallback);
        var brush = new SolidColorBrush(fallback);
        if (token is SolidColorBrush solid)
        {
            // Keep live theme changes, but never copy the floating token's alpha/opacity.
            BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty,
                new Binding(nameof(SolidColorBrush.Color)) { Source = solid, Converter = OpaqueColorConverter.Instance });
        }
        return brush;
    }

    private sealed class OpaqueColorConverter : IValueConverter
    {
        internal static OpaqueColorConverter Instance { get; } = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is Color color ? Color.FromRgb(color.R, color.G, color.B) : DependencyProperty.UnsetValue;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    internal static ContextMenu CreateContextMenu() => SupportsLayeredWindows
        ? new ContextMenu()
        : new OpaqueCaptureContextMenu();

    internal static void ExcludeMenuPopup(ContextMenu menu)
    {
        if (PresentationSource.FromVisual(menu) is HwndSource popup && TryApply(popup.Handle)) return;
        Logger.LogWarning("Could not exclude capture control popup; closing it");
        menu.IsOpen = false;
    }

    internal static void DisableTransitions(Window window)
    {
        int disabled = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(window).EnsureHandle(), 3, ref disabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}
