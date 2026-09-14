using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MyCapture.Core.Platform;
using MyCapture.Core.Themes;

namespace MyCapture.App.Themes;

/// <summary>
/// Opt-in attached behavior for native captions, Windows 11 rounded corners, and Mica
/// backdrops on glass themes. Unsupported DWM attributes fail silently so remote or
/// non-DWM sessions retain normal WPF chrome.
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
    private const int DwmsbtMainWindow = 2; // Mica
    private const int DwmWcpRound = 2;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    private static readonly List<WeakReference<Window>> Tracked = [];

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
        bool darkCaption = UseDarkCaption(theme, role);
        bool mica = AppThemeNames.UsesBackdrop(theme);

        if (mica)
        {
            window.Background = Brushes.Transparent;
        }
        else if (window.TryFindResource("Surface.Base") is Brush surface)
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

        int dark = darkCaption ? 1 : 0;
        // Attribute 20 is the documented DWMWA_USE_IMMERSIVE_DARK_MODE value; 19 is the
        // pre-release value some DWM builds still answer, kept as a silent fallback.
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(handle, DwmwaLegacyUseImmersiveDarkMode, ref dark, sizeof(int));
        }

        int backdrop = mica ? DwmsbtMainWindow : DwmsbtNone;
        _ = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));

        Margins margins = mica
            ? new Margins { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 }
            : default;
        _ = DwmExtendFrameIntoClientArea(handle, ref margins);

        // Keep native chrome continuous with the WPF surface. High Contrast owns its
        // caption palette, so never replace the user's accessibility colours there.
        if (!SystemParameters.HighContrast)
        {
            if (mica)
            {
                int none = DwmColorNone;
                _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref none, sizeof(int));
                _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref none, sizeof(int));
            }
            else
            {
                AppTheme captionTheme = theme == AppTheme.Workspace
                    ? role == WorkspaceRole.Gallery ? AppTheme.Daylight : AppTheme.Midnight
                    : theme;
                IReadOnlyDictionary<string, ThemeColor> colors = ThemeCatalog.ColorsFor(captionTheme);
                int border = ToColorRef(colors["Border.Subtle"]);
                int caption = ToColorRef(colors["Surface.Base"]);
                int text = ToColorRef(colors["Text.Primary"]);
                _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
                _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
                _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));
            }
        }

        int rounded = DwmWcpRound;
        _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref rounded, sizeof(int));
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
