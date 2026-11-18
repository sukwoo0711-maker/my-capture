using System.Runtime.InteropServices;
using MyCapture.Core.Platform;

namespace MyCapture.App.Themes;

/// <summary>How DWM shapes a window's corners on Windows 11 (DWM_WINDOW_CORNER_PREFERENCE).</summary>
internal enum DwmCornerPreference
{
    Default = 0,
    DoNotRound = 1,
    Round = 2,
    RoundSmall = 3,
}

/// <summary>
/// The DWM window attributes MyCapture sets, behind the Windows build gates that decide whether
/// they exist. One P/Invoke for every caller; failures in remote or non-DWM sessions are reported
/// to the caller rather than thrown.
/// </summary>
internal static class DwmWindowAttributes
{
    internal const int UseImmersiveDarkMode = 20;
    internal const int LegacyUseImmersiveDarkMode = 19;
    internal const int WindowCornerPreference = 33;
    internal const int BorderColor = 34;
    internal const int CaptionColor = 35;
    internal const int TextColor = 36;
    internal const int SystemBackdropType = 38;
    internal const int BackdropNone = 1;

    /// <summary>DWMWA_COLOR_DEFAULT: the system chooses the colour.</summary>
    internal const int ColorDefault = unchecked((int)0xFFFFFFFF);

    /// <summary>The running Windows build, for the <see cref="WindowsFeatureSupport"/> gates.</summary>
    internal static int Build => Environment.OSVersion.Version.Build;

    /// <summary>True on Windows 11, where DWM rounds a window's corners on request.</summary>
    internal static bool SupportsCornerPreference => OperatingSystem.IsWindows() && WindowsFeatureSupport.HasRoundedWindowCorners(Build);

    /// <summary>True on Windows 11, where DWM paints a caller-chosen border colour.</summary>
    internal static bool SupportsBorderColor => OperatingSystem.IsWindows() && WindowsFeatureSupport.HasDwmCaptionColors(Build);

    /// <summary>
    /// The corner radius DWM gives a window for <paramref name="corners"/>, DIPs: 8 for a round
    /// window and 4 for a small round one, as Windows 11 draws them at every scale; 0 otherwise.
    /// </summary>
    internal static double CornerRadius(DwmCornerPreference corners) => corners switch
    {
        DwmCornerPreference.Round => 8,
        DwmCornerPreference.RoundSmall => 4,
        _ => 0,
    };

    /// <summary>COLORREF (0x00BBGGRR) for an opaque colour.</summary>
    internal static int ToColorRef(byte red, byte green, byte blue) => red | (green << 8) | (blue << 16);

    /// <summary>Sets one integer attribute; true when DWM accepted it.</summary>
    internal static bool Set(IntPtr handle, int attribute, int value)
    {
        if (handle == IntPtr.Zero) return false;
        try { return DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)) == 0; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>
    /// Rounds <paramref name="handle"/>'s corners and paints its border in <paramref name="border"/>
    /// (a COLORREF, or null for the system colour). True only when the build supports corner
    /// preferences and DWM accepted the request, so the caller can draw its own border otherwise.
    /// </summary>
    internal static bool TryRound(IntPtr handle, DwmCornerPreference corners, int? border)
    {
        if (!SupportsCornerPreference || !Set(handle, WindowCornerPreference, (int)corners)) return false;
        if (SupportsBorderColor) _ = Set(handle, BorderColor, border ?? ColorDefault);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Margins
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
