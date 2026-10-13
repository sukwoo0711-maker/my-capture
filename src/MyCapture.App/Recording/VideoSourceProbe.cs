using Microsoft.Extensions.Logging;
using MyCapture.Core.Diagnostics;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Recording;

/// <summary>
/// Reads, off the UI thread, what a WPF player cannot tell about a clip: its exact container
/// duration (WPF's <c>NaturalDuration</c> is truncated to whole seconds, see
/// <c>VideoDurationResolver</c>) and whether it is studio range (see <see cref="StudioRangeVideoEffect"/>).
/// </summary>
internal static class VideoSourceProbe
{
    /// <summary>
    /// Completes with the clip's duration and range. When the file cannot be read the duration
    /// is 0 and the range is studio, the format every recording uses.
    /// </summary>
    internal static Task<VideoSourceInfo> ReadAsync(string path, ILogger? log = null) =>
        Task.Run(() =>
        {
            try
            {
                return MediaFoundationVideoFrameReader.ReadSourceInfo(path);
            }
            catch (Exception ex)
            {
                // The caller falls back to the player/recorded duration; never fault a UI open.
                log?.LogWarning(ex, "Could not read the container information of {Path}", LogText.SingleLine(path));
                return new VideoSourceInfo(0, StudioRange: true);
            }
        });
}
