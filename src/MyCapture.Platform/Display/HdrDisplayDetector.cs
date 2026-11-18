using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Display;

/// <summary>Colour state of one desktop source, as the display configuration reports it.</summary>
/// <param name="DeviceName">GDI device name, for example <c>\\.\DISPLAY1</c>; the same name a monitor and a DXGI output carry.</param>
/// <param name="AdvancedColorSupported">The display supports HDR or wide colour gamut.</param>
/// <param name="AdvancedColorEnabled">HDR or wide colour gamut composition is on.</param>
/// <param name="WideColorEnforced">Windows manages a wide colour gamut for the display.</param>
/// <param name="AdvancedColorForceDisabled">A system or OEM policy keeps advanced colour off.</param>
/// <param name="ActiveColorMode">
/// 0 SDR, 1 wide colour gamut, 2 HDR on Windows 11 24H2 and later; <see langword="null"/> where
/// the system does not report it.
/// </param>
/// <param name="ColorEncoding">DISPLAYCONFIG_COLOR_ENCODING of the signal.</param>
/// <param name="BitsPerColorChannel">Bits per channel of the signal.</param>
/// <param name="SdrWhiteLevel">SDR white as a multiple of 80 nits times 1000, or <see langword="null"/> when unavailable.</param>
public sealed record HdrDisplayState(
    string DeviceName,
    bool AdvancedColorSupported,
    bool AdvancedColorEnabled,
    bool WideColorEnforced,
    bool AdvancedColorForceDisabled,
    int? ActiveColorMode,
    int ColorEncoding,
    int BitsPerColorChannel,
    int? SdrWhiteLevel)
{
    private const int ActiveColorModeHdr = 2;

    /// <summary>
    /// The desktop of this display is composed for HDR. Where the active mode is not reported,
    /// "advanced colour enabled" also covers a wide-gamut SDR display; the capture path then
    /// confirms HDR from the DXGI output's colour space before it reads.
    /// </summary>
    public bool IsHdrActive => ActiveColorMode is { } mode ? mode == ActiveColorModeHdr : AdvancedColorEnabled;

    /// <summary>SDR white in nits; 80 when the system reports none.</summary>
    public double SdrWhiteNits => SdrWhiteLevel is > 0 ? SdrWhiteLevel.Value / 1000.0 * 80.0 : 80.0;
}

/// <summary>
/// Reads which displays are in HDR mode, and their SDR white level, from the display configuration.
/// </summary>
/// <remarks>
/// <para>
/// Not cached. One pass over two displays measured 0.012 ms (median of 3,000; the first call,
/// which also compiles the interop, 2.2 ms), so every still capture asks again, as
/// <see cref="MonitorEnumerator"/> does. A cached answer would need an invalidation signal that
/// does not exist for the SDR brightness slider, and a stale "HDR" with a stale white level
/// darkens a capture.
/// </para>
/// <para>
/// Only user32 is called: no D3D device and no module this process has not already loaded.
/// A failure of any query reads as "no HDR display", never as an exception.
/// </para>
/// </remarks>
public static unsafe class HdrDisplayDetector
{
    private const int MaximumPaths = 64;
    private const int MaximumModes = 128;
    private const uint AdvancedColorSupportedFlag = 0x1;
    private const uint AdvancedColorEnabledFlag = 0x2;
    private const uint WideColorEnforcedFlag = 0x4;
    private const uint AdvancedColorForceDisabledFlag = 0x8;

    /// <summary>One entry per active desktop source; empty when the configuration cannot be read.</summary>
    public static IReadOnlyList<HdrDisplayState> Detect()
    {
        try
        {
            // The path count can change between sizing and filling the buffers.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (TryQuery(out IReadOnlyList<HdrDisplayState> displays))
                {
                    return displays;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A capture never depends on this answer.
        }

        return [];
    }

    /// <summary>Returns false only when the buffers were too small for a topology that just changed.</summary>
    private static bool TryQuery(out IReadOnlyList<HdrDisplayState> displays)
    {
        displays = [];
        uint pathCount, modeCount;
        if (NativeMethods.GetDisplayConfigBufferSizes(NativeMethods.QDC_ONLY_ACTIVE_PATHS, &pathCount, &modeCount) != 0
            || pathCount == 0 || pathCount > MaximumPaths || modeCount > MaximumModes)
        {
            return true;
        }

        NativeMethods.DISPLAYCONFIG_PATH_INFO* paths = stackalloc NativeMethods.DISPLAYCONFIG_PATH_INFO[(int)pathCount];
        byte* modes = stackalloc byte[Math.Max(1, (int)modeCount) * NativeMethods.DisplayConfigModeInfoSize];
        int error = NativeMethods.QueryDisplayConfig(
            NativeMethods.QDC_ONLY_ACTIVE_PATHS, &pathCount, paths, &modeCount, modes, null);
        if (error == NativeMethods.ERROR_INSUFFICIENT_BUFFER)
        {
            return false;
        }

        if (error != 0)
        {
            return true;
        }

        var found = new List<HdrDisplayState>((int)pathCount);
        for (int index = 0; index < pathCount; index++)
        {
            HdrDisplayState? state = Describe(in paths[index]);
            if (state is null)
            {
                continue;
            }

            // Duplicated displays share one source; the source counts as HDR when any of them is.
            int existing = found.FindIndex(other => string.Equals(other.DeviceName, state.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (existing < 0)
            {
                found.Add(state);
            }
            else if (state.IsHdrActive && !found[existing].IsHdrActive)
            {
                found[existing] = state;
            }
        }

        displays = found;
        return true;
    }

    private static HdrDisplayState? Describe(in NativeMethods.DISPLAYCONFIG_PATH_INFO path)
    {
        var name = new NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            Header = Header(NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                sizeof(NativeMethods.DISPLAYCONFIG_SOURCE_DEVICE_NAME), path.SourceAdapterId, path.SourceId),
        };
        if (NativeMethods.DisplayConfigGetDeviceInfo(&name) != 0)
        {
            return null;
        }

        var characters = new ReadOnlySpan<char>(name.ViewGdiDeviceName, 32);
        int terminator = characters.IndexOf('\0');
        string deviceName = new(terminator >= 0 ? characters[..terminator] : characters);
        if (deviceName.Length == 0)
        {
            return null;
        }

        var color = new NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            Header = Header(NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                sizeof(NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO), path.TargetAdapterId, path.TargetId),
        };
        if (NativeMethods.DisplayConfigGetDeviceInfo(&color) != 0)
        {
            // Without the colour state the display is treated as SDR.
            return new HdrDisplayState(deviceName, false, false, false, false, null, 0, 0, null);
        }

        var active = new NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
        {
            Header = Header(NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2,
                sizeof(NativeMethods.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2), path.TargetAdapterId, path.TargetId),
        };
        int? activeMode = NativeMethods.DisplayConfigGetDeviceInfo(&active) == 0 ? (int)active.ActiveColorMode : null;

        var white = new NativeMethods.DISPLAYCONFIG_SDR_WHITE_LEVEL
        {
            Header = Header(NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL,
                sizeof(NativeMethods.DISPLAYCONFIG_SDR_WHITE_LEVEL), path.TargetAdapterId, path.TargetId),
        };
        int? whiteLevel = NativeMethods.DisplayConfigGetDeviceInfo(&white) == 0 && white.SDRWhiteLevel is > 0 and <= int.MaxValue
            ? (int)white.SDRWhiteLevel
            : null;

        return new HdrDisplayState(
            deviceName,
            (color.Flags & AdvancedColorSupportedFlag) != 0,
            (color.Flags & AdvancedColorEnabledFlag) != 0,
            (color.Flags & WideColorEnforcedFlag) != 0,
            (color.Flags & AdvancedColorForceDisabledFlag) != 0,
            activeMode,
            (int)color.ColorEncoding,
            (int)color.BitsPerColorChannel,
            whiteLevel);
    }

    private static NativeMethods.DISPLAYCONFIG_DEVICE_INFO_HEADER Header(
        uint type, int size, NativeMethods.LUID adapter, uint id) =>
        new() { Type = type, Size = (uint)size, AdapterId = adapter, Id = id };
}
