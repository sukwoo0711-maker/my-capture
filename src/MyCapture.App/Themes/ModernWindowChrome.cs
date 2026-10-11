using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MyCapture.Core.Platform;
using MyCapture.Core.Themes;

namespace MyCapture.App.Themes;

/// <summary>Native caption colours for one window, resolved without an HWND.</summary>
/// <param name="DarkMode">Value for DWMWA_USE_IMMERSIVE_DARK_MODE (caption buttons and glyphs).</param>
/// <param name="CaptionColor">COLORREF for DWMWA_CAPTION_COLOR, or <see langword="null"/> to keep the system colour.</param>
/// <param name="TextColor">COLORREF for DWMWA_TEXT_COLOR, or <see langword="null"/> to keep the system colour.</param>
/// <param name="BorderColor">COLORREF for DWMWA_BORDER_COLOR, or <see langword="null"/> to keep the system colour.</param>
internal readonly record struct WindowChromePlan(bool DarkMode, int? CaptionColor, int? TextColor, int? BorderColor);

/// <summary>
/// Opt-in attached behavior for native captions and Windows 11 rounded corners. The caption is
/// painted with the colour the client area actually shows, so the system title bar and the
/// window body read as one surface while Windows keeps dragging, snapping, caption buttons
/// and accessibility. Unsupported DWM attributes (before Windows 11 build 22000, remote or
/// non-DWM sessions) fail silently and leave normal chrome.
/// </summary>
public static class ModernWindowChrome
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaLegacyUseImmersiveDarkMode = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtNone = 1;
    private const int DwmWcpRound = 2;
    private const int DwmColorDefault = unchecked((int)0xFFFFFFFF);
    private const int WmSysColorChange = 0x0015;

    // WPF clears every HWND to opaque black before drawing the window background (the
    // HwndSource composition target defaults to black), so a translucent glass Surface.Base
    // is seen over black. The caption uses that composited colour to match the body exactly.
    private static readonly ThemeColor ClientClearColor = ThemeColor.Rgb(0, 0, 0);

    private static readonly List<WeakReference<Window>> Tracked = [];
    private static readonly ConditionalWeakTable<HwndSource, object> Hooked = new();

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(ModernWindowChrome),
        new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty WorkspaceRoleProperty = DependencyProperty.RegisterAttached(
        "WorkspaceRole",
        typeof(WorkspaceRole),
        typeof(ModernWindowChrome),
        new PropertyMetadata(WorkspaceRole.VideoEditor, OnWorkspaceRoleChanged));

    public static void SetEnabled(DependencyObject element, bool value) =>
        element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) =>
        (bool)element.GetValue(EnabledProperty);

    internal static void SetWorkspaceRole(DependencyObject element, WorkspaceRole value) =>
        element.SetValue(WorkspaceRoleProperty, value);

    internal static WorkspaceRole GetWorkspaceRole(DependencyObject element) =>
        (WorkspaceRole)element.GetValue(WorkspaceRoleProperty);

    internal static void RefreshAll()
    {
        for (int i = Tracked.Count - 1; i >= 0; i--)
        {
            if (Tracked[i].TryGetTarget(out Window? window) && GetEnabled(window))
            {
                Apply(window);
            }
            else
            {
                Tracked.RemoveAt(i);
            }
        }
    }

    internal static void Refresh(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (GetEnabled(window))
        {
            Apply(window);
        }
    }

    internal static bool UseDarkCaption(AppTheme theme, WorkspaceRole role) => theme switch
    {
        AppTheme.Daylight or AppTheme.GlassLight => false,
        AppTheme.Workspace => role != WorkspaceRole.Gallery,
        _ => true,
    };

    internal static int ToColorRef(ThemeColor color) => color.R | (color.G << 8) | (color.B << 16);

    /// <summary>
    /// Pure decision for a window's native caption: dark caption buttons for dark palettes, and
    /// caption, caption text and border colours equal to what the client area paints. System
    /// High Contrast keeps the user's caption palette.
    /// </summary>
    internal static WindowChromePlan ResolveChrome(AppTheme theme, WorkspaceRole role, bool systemHighContrast)
    {
        bool dark = UseDarkCaption(theme, role);
        if (systemHighContrast) return new WindowChromePlan(dark, null, null, null);
        IReadOnlyDictionary<string, ThemeColor> colors = ClientColors(theme, role);
        ThemeColor caption = ThemeContrast.Composite(colors["Surface.Base"], ClientClearColor);
        ThemeColor text = ThemeContrast.Composite(colors["Text.Primary"], caption);
        ThemeColor border = ThemeContrast.Composite(colors["Border.Subtle"], caption);
        return new WindowChromePlan(dark, ToColorRef(caption), ToColorRef(text), ToColorRef(border));
    }

    /// <summary>The palette a window's own resources resolve to, including Workspace's per-role overrides.</summary>
    internal static IReadOnlyDictionary<string, ThemeColor> ClientColors(AppTheme theme, WorkspaceRole role)
    {
        if (theme != AppTheme.Workspace || role != WorkspaceRole.Gallery) return ThemeCatalog.ColorsFor(theme);
        // The library keeps Workspace's light role palette: Daylight plus WorkspaceTheme's inline overrides.
        var colors = new Dictionary<string, ThemeColor>(ThemeCatalog.ColorsFor(AppTheme.Daylight), StringComparer.Ordinal);
        foreach ((string key, string value) in WorkspaceTheme.WorkspaceOverrides(role))
            colors[key] = ParseRgb(value);
        return colors;
    }

    private static ThemeColor ParseRgb(string hex)
    {
        int value = int.Parse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);
        return ThemeColor.Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    private static void OnEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not Window window || e.NewValue is not true)
        {
            return;
        }

        Track(window);
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Apply(window);
        }
        else
        {
            window.SourceInitialized += OnWindowSourceInitialized;
        }
    }

    private static void OnWorkspaceRoleChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is Window window && GetEnabled(window)
            && new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Apply(window);
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

    private static void Track(Window window)
    {
        foreach (WeakReference<Window> existing in Tracked)
        {
            if (existing.TryGetTarget(out Window? tracked) && ReferenceEquals(tracked, window))
            {
                return;
            }
        }

        Tracked.Add(new WeakReference<Window>(window));
        window.Closed += OnWindowClosed;
    }

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        window.Closed -= OnWindowClosed;
        for (int i = Tracked.Count - 1; i >= 0; i--)
        {
            if (!Tracked[i].TryGetTarget(out Window? tracked) || ReferenceEquals(tracked, window))
            {
                Tracked.RemoveAt(i);
            }
        }
    }

    private static void Apply(Window window)
    {
        WindowIdentity.Attach(window);
        AppTheme theme = ThemeService.Current;
        WorkspaceRole role = GetWorkspaceRole(window);
        WindowChromePlan plan = ResolveChrome(theme, role, ThemeService.IsSystemHighContrast);

        // Every palette paints its base surface, glass included. A transparent window would
        // show WPF's black clear colour, which no longer matched the Mica caption above it.
        if (window.TryFindResource("Surface.Base") is Brush surface)
        {
            window.Background = surface;
        }

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

        // Switching between two contrast themes changes the system colours without toggling
        // SystemParameters.HighContrast; the broadcast reaches every top-level window.
        if (HwndSource.FromHwnd(handle) is { } source && !Hooked.TryGetValue(source, out _))
        {
            Hooked.Add(source, new object());
            source.AddHook(OnWindowMessage);
        }

        int dark = plan.DarkMode ? 1 : 0;
        // Attribute 20 is the documented DWMWA_USE_IMMERSIVE_DARK_MODE value; 19 is the
        // pre-release value some DWM builds still answer, kept as a silent fallback.
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, DwmwaLegacyUseImmersiveDarkMode, ref dark, sizeof(int));
        }

        // The client is opaque, so no system backdrop or extended frame can show through it.
        // Build 22621 is the first to accept the backdrop attribute; earlier builds ignore it.
        int backdrop = DwmsbtNone;
        _ = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        Margins margins = default;
        _ = DwmExtendFrameIntoClientArea(handle, ref margins);

        // Caption colours exist from build 22000. High Contrast hands the caption back to the
        // user's system palette instead of keeping a colour from an earlier theme.
        int border = plan.BorderColor ?? DwmColorDefault;
        int caption = plan.CaptionColor ?? DwmColorDefault;
        int text = plan.TextColor ?? DwmColorDefault;
        _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));

        int rounded = DwmWcpRound;
        _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref rounded, sizeof(int));
    }

    private static IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSysColorChange)
        {
            ThemeService.QueueSystemRefresh();
        }

        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
