using MyCapture.Core.Primitives;

namespace MyCapture.Core.Recording;

/// <summary>
/// Frame source a region recording grabs its pixels from.
/// </summary>
/// <remarks>
/// Internal diagnostic switch only: it is never part of <c>settings.json</c> or any UI, so
/// a spike backend can be measured on real hardware without changing user-visible behaviour.
/// </remarks>
public enum RecordingCaptureBackend
{
    /// <summary>GDI <c>BitBlt + CAPTUREBLT</c> from the desktop DC. The default.</summary>
    Gdi = 0,

    /// <summary>
    /// Windows.Graphics.Capture monitor session whose GPU frames are cropped to the region
    /// and read back through a staging texture.
    /// </summary>
    WindowsGraphicsCapture = 1,
}

/// <summary>
/// Pure selection rules for <see cref="RecordingCaptureBackend"/>.
/// </summary>
public static class RecordingCaptureBackendSelection
{
    /// <summary>Process environment variable that opts a run into another backend.</summary>
    public const string EnvironmentVariable = "MYCAPTURE_RECORDING_BACKEND";

    /// <summary>
    /// Maps the switch value to a backend. Only <c>wgc</c> (case-insensitive, trimmed)
    /// selects Windows.Graphics.Capture; anything else, including a typo, keeps GDI.
    /// </summary>
    public static RecordingCaptureBackend Parse(string? value) =>
        string.Equals(value?.Trim(), "wgc", StringComparison.OrdinalIgnoreCase)
            ? RecordingCaptureBackend.WindowsGraphicsCapture
            : RecordingCaptureBackend.Gdi;

    /// <summary>Reads <see cref="EnvironmentVariable"/> from the current process.</summary>
    public static RecordingCaptureBackend FromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

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
