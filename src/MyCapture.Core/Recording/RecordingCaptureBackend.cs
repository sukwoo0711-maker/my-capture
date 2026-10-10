using System.Text.Json;
using System.Text.Json.Serialization;
using MyCapture.Core.Primitives;

namespace MyCapture.Core.Recording;

/// <summary>
/// Frame source a region recording grabs its pixels from.
/// </summary>
/// <remarks>
/// This is the backend a recording actually asks for, after the user's
/// <see cref="RecordingCaptureMode"/> and the diagnostic environment switch are resolved
/// (<see cref="RecordingCaptureBackendSelection.Resolve"/>). The grabber may still fall back
/// from Windows.Graphics.Capture to GDI for a given region or failure.
/// </remarks>
public enum RecordingCaptureBackend
{
    /// <summary>GDI <c>BitBlt + CAPTUREBLT</c> from the desktop DC. Always available.</summary>
    Gdi = 0,

    /// <summary>
    /// Windows.Graphics.Capture monitor session whose GPU frames are cropped to the region
    /// and read back through a staging texture.
    /// </summary>
    WindowsGraphicsCapture = 1,
}

/// <summary>
/// The user's "recording capture method" setting, persisted in <c>settings.json</c> as
/// <c>recording.captureMode</c>.
/// </summary>
/// <remarks>
/// A missing, unknown or out-of-range value reads as <see cref="Auto"/>, so an older settings
/// file (or one written by a newer version with another value) keeps working. The property
/// carries <see cref="RecordingCaptureModeJsonConverter"/> itself, because a converter in the
/// shared options' <c>Converters</c> list outranks one declared on the type.
/// </remarks>
public enum RecordingCaptureMode
{
    /// <summary>
    /// Windows.Graphics.Capture when the region lies inside one display and the OS supports it;
    /// GDI for regions that span displays, unsupported systems and any WGC failure.
    /// </summary>
    Auto = 0,

    /// <summary>Compatibility mode: every recording uses GDI.</summary>
    Gdi = 1,
}

/// <summary>Stable setting names for <see cref="RecordingCaptureMode"/>.</summary>
public static class RecordingCaptureModeNames
{
    public const string Auto = "auto";
    public const string Gdi = "gdi";

    /// <summary>Only <c>gdi</c> (case-insensitive, trimmed) selects compatibility mode.</summary>
    public static RecordingCaptureMode Parse(string? value) =>
        string.Equals(value?.Trim(), Gdi, StringComparison.OrdinalIgnoreCase)
            ? RecordingCaptureMode.Gdi
            : RecordingCaptureMode.Auto;

    public static string ToSetting(RecordingCaptureMode mode) =>
        mode == RecordingCaptureMode.Gdi ? Gdi : Auto;
}

/// <summary>
/// Reads <see cref="RecordingCaptureMode"/> leniently: a name or number that is not a known
/// mode becomes <see cref="RecordingCaptureMode.Auto"/> instead of failing the whole settings
/// file, which the strict string-enum converter would do.
/// </summary>
public sealed class RecordingCaptureModeJsonConverter : JsonConverter<RecordingCaptureMode>
{
    public override RecordingCaptureMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return RecordingCaptureModeNames.Parse(reader.GetString());
            case JsonTokenType.Number:
                return reader.TryGetInt32(out int value) && value == (int)RecordingCaptureMode.Gdi
                    ? RecordingCaptureMode.Gdi
                    : RecordingCaptureMode.Auto;
            default:
                reader.Skip();
                return RecordingCaptureMode.Auto;
        }
    }

    public override void Write(Utf8JsonWriter writer, RecordingCaptureMode value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(RecordingCaptureModeNames.ToSetting(value));
    }
}

/// <summary>
/// Pure selection rules for <see cref="RecordingCaptureBackend"/>.
/// </summary>
public static class RecordingCaptureBackendSelection
{
    /// <summary>
    /// Process environment variable (<c>gdi</c> or <c>wgc</c>) that overrides the user setting.
    /// Diagnostic only: it is never written by the app.
    /// </summary>
    public const string EnvironmentVariable = "MYCAPTURE_RECORDING_BACKEND";

    /// <summary>
    /// Maps the environment switch to a forced backend: <c>wgc</c> or <c>gdi</c>
    /// (case-insensitive, trimmed). Anything else, including a typo, forces nothing.
    /// </summary>
    public static RecordingCaptureBackend? ParseOverride(string? value)
    {
        string? trimmed = value?.Trim();
        if (string.Equals(trimmed, "wgc", StringComparison.OrdinalIgnoreCase))
        {
            return RecordingCaptureBackend.WindowsGraphicsCapture;
        }

        return string.Equals(trimmed, "gdi", StringComparison.OrdinalIgnoreCase)
            ? RecordingCaptureBackend.Gdi
            : null;
    }

    /// <summary>
    /// Backend a recording requests: the environment override when it names one, otherwise
    /// GDI for compatibility mode and Windows.Graphics.Capture for <see cref="RecordingCaptureMode.Auto"/>.
    /// </summary>
    public static RecordingCaptureBackend Resolve(RecordingCaptureMode mode, string? environmentValue) =>
        ParseOverride(environmentValue)
        ?? (mode == RecordingCaptureMode.Gdi ? RecordingCaptureBackend.Gdi : RecordingCaptureBackend.WindowsGraphicsCapture);

    /// <summary>Reads <see cref="EnvironmentVariable"/> from the current process.</summary>
    public static string? EnvironmentOverrideValue() => Environment.GetEnvironmentVariable(EnvironmentVariable);

    /// <summary>
    /// <see cref="Resolve"/> against the current process environment. The default mode is the
    /// product default, so diagnostics and tests without a setting behave like a fresh install.
    /// </summary>
    public static RecordingCaptureBackend FromEnvironment(RecordingCaptureMode mode = RecordingCaptureMode.Auto) =>
        Resolve(mode, EnvironmentOverrideValue());

    /// <summary>
    /// Index of the one monitor that fully contains <paramref name="pixelRegion"/>, or -1
    /// when the region is empty, crosses a monitor edge or lies outside every monitor.
    /// </summary>
    /// <remarks>
    /// A Windows.Graphics.Capture monitor item only sees its own display, so a region that
    /// spans displays (or mixed-DPI seams) must stay on the virtual-desktop GDI path.
    /// Both arguments are physical virtual-screen pixels.
    /// </remarks>
    public static int FindContainingMonitor(RectD pixelRegion, IReadOnlyList<RectD> monitorBounds)
    {
        ArgumentNullException.ThrowIfNull(monitorBounds);
        RectD region = pixelRegion.Normalized();
        if (region.Width <= 0 || region.Height <= 0)
        {
            return -1;
        }

        for (int index = 0; index < monitorBounds.Count; index++)
        {
            RectD monitor = monitorBounds[index].Normalized();
            if (region.Left >= monitor.Left && region.Top >= monitor.Top
                && region.Right <= monitor.Right && region.Bottom <= monitor.Bottom)
            {
                return index;
            }
        }

        return -1;
    }
}
