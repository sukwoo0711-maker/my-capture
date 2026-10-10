using Microsoft.Extensions.Logging;
using MyCapture.Core.Diagnostics;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// Reads a clip's exact container duration off the UI thread, for players whose WPF
/// <c>NaturalDuration</c> is truncated to whole seconds (see <c>VideoDurationResolver</c>).
/// </summary>
internal static class VideoDurationProbe
{
    /// <summary>Completes with the duration in milliseconds, or 0 when it cannot be read.</summary>
    internal static Task<double> ReadContainerDurationAsync(string path, ILogger? log = null) =>
        Task.Run(() =>
        {
            try
            {
                return MediaFoundationVideoFrameReader.ReadContainerDurationMs(path);
            }
            catch (Exception ex)
            {
                // The caller falls back to the player/recorded duration; never fault a UI open.
                log?.LogWarning(ex, "Could not read the container duration of {Path}", LogText.SingleLine(path));
                return 0d;
            }
        });
}
