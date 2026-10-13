namespace MyCapture.Core.Recording;

/// <summary>
/// Pure arithmetic for moving a playhead through a recorded clip, both frame-by-frame
/// and by the "coarse" step a normal video editor uses for a plain arrow key.
/// </summary>
/// <remarks>
/// <para>
/// Kept free of any UI type so the exact behaviour the user asked for — frame-step
/// mode versus a normal editor's arrow-key jump — is unit-tested in isolation.
/// </para>
/// <para>
/// Positions are milliseconds from the start of the clip. The playhead is always
/// clamped to <c>[0, duration]</c>: a video editor that lets the playhead run off the
/// end of the clip feels broken.
/// </para>
/// </remarks>
public static class FrameStepCalculator
{
    /// <summary>Duration of a single frame in milliseconds for <paramref name="fps"/>.</summary>
    public static double FrameDurationMs(int fps) => fps <= 0 ? 0 : 1000.0 / fps;

    /// <summary>
    /// Snaps <paramref name="positionMs"/> to the nearest frame boundary.
    /// </summary>
    /// <remarks>
    /// Frame stepping must start from a frame boundary or the first arrow press moves
    /// a fractional amount and every subsequent frame is off by that remainder.
    /// </remarks>
    public static double SnapToFrame(double positionMs, int fps, double durationMs)
    {
        if (fps <= 0)
        {
            return Clamp(positionMs, durationMs);
        }

        double frame = FrameDurationMs(fps);
        double snapped = Math.Round(positionMs / frame) * frame;
        return Clamp(snapped, durationMs);
    }

    /// <summary>
    /// Moves <paramref name="frames"/> whole frames (negative moves back) from the frame shown at
    /// <paramref name="positionMs"/> and returns the start of that frame, between the first frame
    /// and the last one.
    /// </summary>
    /// <remarks>
    /// The step counts frames, not milliseconds. Rounding the position to the nearest boundary
    /// first made a step from the second half of a frame, or from the clip end, land in the frame
    /// already shown; and a step past the last frame used to park at the clip end, which still
    /// shows the last frame. Either way the press changed nothing on screen.
    /// </remarks>
    public static double StepByFrames(double positionMs, int frames, int fps, double durationMs)
    {
        if (fps <= 0)
        {
            return Clamp(positionMs, durationMs);
        }

        int last = LastFrameIndex(fps, durationMs);
        int current = Math.Min(FrameIndexAt(positionMs, fps, durationMs), last);
        long target = Math.Clamp((long)current + frames, 0, last);
        return Clamp(target * FrameDurationMs(fps), durationMs);
    }

    /// <summary>
    /// Zero-based index of the last frame that starts before <paramref name="durationMs"/>; a clip
    /// whose container length is a hair short of the frame grid still ends with a whole frame.
    /// </summary>
    public static int LastFrameIndex(int fps, double durationMs)
    {
        if (fps <= 0 || !(durationMs > 0))
        {
            return 0;
        }

        double frames = Math.Ceiling((durationMs / FrameDurationMs(fps)) - 1e-6);
        return frames >= int.MaxValue ? int.MaxValue - 1 : Math.Max(0, (int)frames - 1);
    }

    /// <summary>
    /// The coarse step in milliseconds for a plain arrow key when frame-step mode is
    /// off — the "일반 편집 앱이 이동하는 평균 수준의 구간".
    /// </summary>
    /// <remarks>
    /// A fixed 5-second jump is right for a two-minute clip but skips past a
    /// three-second clip entirely. So the step is <c>min(fallback, duration/20)</c>,
    /// then clamped to a usable 250ms–1000ms band for short clips. The result: on a
    /// long clip you get the familiar multi-second jump; on a short clip you get a
    /// proportional nudge that still lands several times across the timeline.
    /// </remarks>
    public static double CoarseStepMs(double durationMs, double fallbackSeconds)
    {
        double fallbackMs = Math.Max(0, fallbackSeconds) * 1000.0;
        if (durationMs <= 0)
        {
            return fallbackMs;
        }

        double proportional = durationMs / 20.0;
        double step = Math.Min(fallbackMs, proportional);

        // For long clips the fallback wins and we return it unclamped; the band only
        // rescues very short clips from a sub-frame or whole-clip step.
        if (proportional < fallbackMs)
        {
            step = Math.Clamp(proportional, 250.0, 1000.0);
        }

        return Math.Min(step, durationMs);
    }

    /// <summary>Moves the playhead by one coarse step (negative moves back).</summary>
    public static double StepCoarse(double positionMs, int direction, double durationMs, double fallbackSeconds)
    {
        double step = CoarseStepMs(durationMs, fallbackSeconds);
        return Clamp(positionMs + (Math.Sign(direction) * step), durationMs);
    }

    /// <summary>Zero-based frame index containing <paramref name="positionMs"/>.</summary>
    public static int FrameIndexAt(double positionMs, int fps, double durationMs)
    {
        if (fps <= 0)
        {
            return 0;
        }

        double clamped = Clamp(positionMs, durationMs);
        return (int)Math.Floor(clamped / FrameDurationMs(fps) + 1e-6);
    }

    private static double Clamp(double value, double durationMs) =>
        Math.Clamp(value, 0.0, Math.Max(0.0, durationMs));
}
