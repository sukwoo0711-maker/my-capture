namespace MyCapture.Core.Capture;

/// <summary>Whether a still capture reads HDR displays in scRGB and converts them to SDR.</summary>
public enum HdrCaptureMode
{
    /// <summary>Displays that are in HDR mode are read in scRGB; every other display keeps the GDI copy.</summary>
    Auto = 0,

    /// <summary>Every display keeps the GDI copy, as before the HDR correction existed.</summary>
    Off = 1,

    /// <summary>
    /// Every display is read in scRGB, HDR or not. An SDR desktop then converts back to the pixels
    /// the GDI copy holds, which is how the read and the conversion are checked on a PC without HDR.
    /// </summary>
    ForceFloatRead = 2,
}

/// <summary>The diagnostic switch for <see cref="HdrCaptureMode"/>. There is no user setting.</summary>
public static class HdrCaptureModeSelection
{
    /// <summary>
    /// Process environment variable: <c>off</c> keeps every capture on the GDI copy, <c>force</c>
    /// reads every display in scRGB. Diagnostic only; the app never writes it.
    /// </summary>
    public const string EnvironmentVariable = "MYCAPTURE_HDR_CAPTURE";

    public const string Off = "off";
    public const string Force = "force";

    /// <summary>
    /// <c>off</c> and <c>force</c> (case-insensitive, trimmed) select their modes. Anything else,
    /// including a typo, leaves <see cref="HdrCaptureMode.Auto"/>.
    /// </summary>
    public static HdrCaptureMode Parse(string? value)
    {
        string? trimmed = value?.Trim();
        if (string.Equals(trimmed, Off, StringComparison.OrdinalIgnoreCase))
        {
            return HdrCaptureMode.Off;
        }

        return string.Equals(trimmed, Force, StringComparison.OrdinalIgnoreCase)
            ? HdrCaptureMode.ForceFloatRead
            : HdrCaptureMode.Auto;
    }

    /// <summary><see cref="Parse"/> of <see cref="EnvironmentVariable"/> in the current process.</summary>
    public static HdrCaptureMode FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));
}
