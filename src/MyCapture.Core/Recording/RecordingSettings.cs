namespace MyCapture.Core.Recording;

/// <summary>
/// Target capture frame rate for a recording.
/// </summary>
/// <remarks>
/// Kept to a small set of sensible values rather than an open integer. 15 is the
/// default because it is smooth enough for UI walkthroughs while costing the
/// software H.264 encoder — the path taken on machines without a hardware MFT —
/// roughly half the work of 30, which is what keeps a weak PC responsive.
/// </remarks>
public enum RecordingFrameRate
{
    Fps10 = 10,
    Fps15 = 15,
    Fps24 = 24,
    Fps30 = 30,
}

/// <summary>
/// Everything the user can configure about region recording, persisted as part of
/// <c>settings.json</c>. Additive to the existing settings graph: an older file
/// simply leaves these at their defaults, so no migration is needed.
/// </summary>
public sealed class RecordingSettings
{
    /// <summary>Capture frame rate. 15 fps by default (see <see cref="RecordingFrameRate"/>).</summary>
    public RecordingFrameRate FrameRate { get; set; } = RecordingFrameRate.Fps15;

    /// <summary>
    /// Whether to count down before the first frame is captured.
    /// </summary>
    /// <remarks>
    /// This is the "delay를 주고 시작할지" switch. Off by default so a single
    /// hotkey press starts recording immediately; on, it reuses the capture
    /// countdown window so the countdown is never in frame.
    /// </remarks>
    public bool UseStartDelay { get; set; }

    /// <summary>Countdown length in seconds when <see cref="UseStartDelay"/> is on.</summary>
    public int StartDelaySeconds { get; set; } = 3;

    /// <summary>Composite the mouse cursor into the recorded frames.</summary>
    public bool IncludeCursor { get; set; } = true;

    /// <summary>
    /// Target bitrate in bits per second. 0 means derive from frame size and rate.
    /// </summary>
    /// <remarks>
    /// A derived bitrate keeps small-region clips small and large-region clips
    /// legible without asking the user to reason about bitrate. An explicit value
    /// is honoured when set.
    /// </remarks>
    public int BitrateBitsPerSecond { get; set; }

    /// <summary>
    /// Seconds moved by a plain arrow key when frame-step mode is off, expressed as
    /// the fallback used for long clips.
    /// </summary>
    /// <remarks>
    /// Short clips scale this down (see <c>FrameStepCalculator</c>) so the arrow key
    /// always feels like a proportional nudge rather than jumping past the whole clip.
    /// </remarks>
    public double CoarseStepSeconds { get; set; } = 5.0;

    public int TargetFps => (int)FrameRate;
}
