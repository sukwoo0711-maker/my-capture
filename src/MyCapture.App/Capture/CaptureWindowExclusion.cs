using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    private static readonly Func<bool> s_probeWindow = ProbeLayeredWindow;
    private static Func<bool> s_layeredWindowProbe = s_probeWindow;
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

    /// <summary>
    /// Whether a layered surface may be made: <see cref="SupportsLayeredWindows"/>, or true while
    /// nothing has asked it. The surface then answers it with its own window
    /// (<see cref="TryApplyLayered"/>), which shows only once exclusion has succeeded, so the
    /// first capture makes no probe window of its own (about 35 ms).
    /// </summary>
    internal static bool MayTryLayeredWindow =>
        (CanAnswerWithFirstWindow && !s_supportsLayeredWindows.IsValueCreated) || SupportsLayeredWindows;

    // A probe a test put in place stays the only answer.
    private static bool CanAnswerWithFirstWindow => ReferenceEquals(s_layeredWindowProbe, s_probeWindow);

    internal static int ProbeWindowsForTest { get; private set; }

    /// <summary>
    /// <see cref="TryApply(Window)"/> for a layered window; the first one, made before anything
    /// asked <see cref="SupportsLayeredWindows"/>, answers it.
    /// </summary>
    internal static bool TryApplyLayered(Window window)
    {
        bool succeeded = TryApply(window);
        if (CanAnswerWithFirstWindow && !s_supportsLayeredWindows.IsValueCreated)
        {
            s_supportsLayeredWindows = CreateCapabilityCache(() => succeeded);
            _ = s_supportsLayeredWindows.Value;
        }
        return succeeded;
    }

    private static Lazy<bool> CreateCapabilityCache() => CreateCapabilityCache(() => s_layeredWindowProbe());

    private static Lazy<bool> CreateCapabilityCache(Func<bool> answer) => new(() =>
    {
        bool supported = answer();
        Logger.LogInformation("Capture exclusion mode: {Mode}",
            supported ? "layered windows" : "opaque windows with recording control region");
        return supported;
    });

    private static bool ProbeLayeredWindow()
    {
        ProbeWindowsForTest++;
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

    // Binding.Source holds a DependencyObject only weakly and ThemeBrushes keeps only a weak
    // reference to its follower, so each backing owns its follower here for exactly its own
    // lifetime. Otherwise a collection drops the follower: theme changes stop reaching the
    // backing and the next UpdateTarget makes it transparent (#220).
    private static readonly ConditionalWeakTable<SolidColorBrush, SolidColorBrush> OpaqueBackingSources = new();

    // Opaque backing also covers antialiasing, translucent theme brushes and content animations.
    internal static Brush OpaqueBackground()
    {
        Color fallback = Color.FromRgb(0x15, 0x1E, 0x2B);
        Brush token = ThemeBrushes.Follow("Surface.Floating", fallback);
        var brush = new SolidColorBrush(fallback);
        if (token is SolidColorBrush solid)
        {
            OpaqueBackingSources.Add(brush, solid);
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
        var popup = PresentationSource.FromVisual(menu) as HwndSource;
        if (popup is not null && TryApply(popup.Handle)) return;
        Logger.LogWarning("Could not exclude capture control popup; closing it");
        menu.IsOpen = false;
        HideClosedPopup(popup);
    }

    internal static void ExcludeSubmenuPopup(MenuItem item, Popup popup)
    {
        var source = popup.Child is Visual visual ? PresentationSource.FromVisual(visual) as HwndSource : null;
        if (source is not null && TryApply(source.Handle)) return;
        Logger.LogWarning("Could not exclude capture control submenu; closing it");
        item.IsSubmenuOpen = false;
        HideClosedPopup(source);
    }

    // WPF keeps a closed animated popup's window on screen for its closing animation and renders
    // it meanwhile (about 100 to 200 ms), so capture would still see it: hide the window now.
    private static void HideClosedPopup(HwndSource? popup)
    {
        if (popup is not null && !popup.IsDisposed) _ = ShowWindow(popup.Handle, SwHide);
    }

    private sealed class ToolTipGuard(Func<bool> closeUnexcluded)
    {
        internal Func<bool> CloseUnexcluded { get; } = closeUnexcluded;
        internal ToolTip? Unexcluded;
    }

    private static readonly ConditionalWeakTable<Window, ToolTipGuard> ToolTipGuards = new();
    private static int s_toolTipHandler;

    /// <summary>
    /// Keeps the tooltips of <paramref name="window"/> out of capture too. WPF opens each tooltip
    /// in a popup window of its own, which the window's display affinity does not reach, and
    /// ToolTip.Opened does not route to the window. A tooltip that cannot be excluded (WPF's
    /// per-pixel-alpha popup on Windows 10) is hidden before it renders while
    /// <paramref name="closeUnexcluded"/> answers true.
    /// </summary>
    internal static void ExcludeToolTips(Window window, Func<bool> closeUnexcluded)
    {
        if (Interlocked.Exchange(ref s_toolTipHandler, 1) == 0)
        {
            EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent,
                new RoutedEventHandler(OnToolTipOpened), handledEventsToo: true);
        }
        ToolTipGuards.AddOrUpdate(window, new ToolTipGuard(closeUnexcluded));
    }

    /// <summary>Hides a tooltip of <paramref name="window"/> that opened unexcluded, before capture starts.</summary>
    internal static void CloseUnexcludedToolTip(Window window)
    {
        if (!ToolTipGuards.TryGetValue(window, out ToolTipGuard? guard) || guard.Unexcluded is not { } tip) return;
        guard.Unexcluded = null;
        CloseNow(tip);
    }

    private static void CloseNow(ToolTip tip)
    {
        var popup = PresentationSource.FromVisual(tip) as HwndSource;
        tip.IsOpen = false;
        HideClosedPopup(popup);
    }

    private static void OnToolTipOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ToolTip tip || PresentationSource.FromVisual(tip) is not HwndSource popup
            || OwnerWindow(tip.PlacementTarget) is not { } owner
            || !ToolTipGuards.TryGetValue(owner, out ToolTipGuard? guard))
        {
            return;
        }
        // Opened follows the popup's ShowWindow before WPF renders it, so no frame has shown it yet.
        bool layered = tip.Parent is Popup { AllowsTransparency: true };
        if ((!layered || SupportsLayeredWindows) && TryApply(popup.Handle)) return;
        if (!guard.CloseUnexcluded())
        {
            guard.Unexcluded = tip;
            return;
        }
        Logger.LogDebug("Could not exclude a capture control tooltip; hiding it");
        CloseNow(tip);
    }

    // WPF gives a popup HWND an owner only while its target's window is in the foreground, which
    // the palette rarely is while recording, so follow placement targets instead: a menu item's
    // tooltip targets the item, whose menu popup targets the palette's button.
    private static Window? OwnerWindow(DependencyObject? element)
    {
        for (int step = 0; element is not null && step < 256; step++)
        {
            if (element is Window window) return window;
            element = element is Popup popup
                ? popup.PlacementTarget
                : LogicalTreeHelper.GetParent(element) ?? (element is Visual ? VisualTreeHelper.GetParent(element) : null);
        }
        return null;
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

    private const int SwHide = 0;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}
