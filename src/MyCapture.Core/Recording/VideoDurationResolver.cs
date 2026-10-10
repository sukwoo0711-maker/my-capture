namespace MyCapture.Core.Recording;

/// <summary>
/// Chooses the editable length of a clip from the values the app can observe.
/// </summary>
/// <remarks>
/// <para>
/// The container duration (Media Foundation <c>MF_PD_DURATION</c>, read from the MP4
/// <c>mvhd</c> box) is authoritative: trim, timed text and every export decode against it.
/// MyCapture's encoder writes it to within one timescale unit (32 frames at 15 fps read as
/// 2133.267 ms).
/// </para>
/// <para>
/// WPF's MediaElement and MediaPlayer obtain <c>NaturalDuration</c> from Windows Media
/// Player's <c>IWMPMedia::get_duration</c>, which reports the same files in whole seconds
/// rounded down: 2133 ms opens as 2000 ms and a sub-second clip reports no duration. A player
/// value is therefore only a lower bound with one second of resolution.
/// </para>
/// </remarks>
public static class VideoDurationResolver
{
    /// <summary>Resolution of the WPF/Windows Media Player duration.</summary>
    public const double PlayerResolutionMs = 1000.0;

    /// <summary>
    /// Returns the container duration when known. Otherwise the recorded duration is used when
    /// it agrees with the whole-second player value, else the player value itself.
    /// </summary>
    /// <param name="containerDurationMs">Container duration, or 0 when unavailable.</param>
    /// <param name="playerDurationMs">WPF <c>NaturalDuration</c>, or <c>null</c> when it has none.</param>
    /// <param name="recordedDurationMs">Duration stored with the recording.</param>
    public static double Resolve(double containerDurationMs, double? playerDurationMs, double recordedDurationMs)
    {
        if (IsPositive(containerDurationMs))
        {
            return containerDurationMs;
        }

        bool hasRecorded = IsPositive(recordedDurationMs);
        if (playerDurationMs is double player && IsPositive(player))
        {
            // The real length lies in [player, player + 1 s). A recorded value outside that
            // window contradicts the file being played.
            return hasRecorded
                && recordedDurationMs >= player
                && recordedDurationMs < player + PlayerResolutionMs
                    ? recordedDurationMs
                    : player;
        }

        return hasRecorded ? recordedDurationMs : 1;
    }

    private static bool IsPositive(double value) => double.IsFinite(value) && value > 0;
}
