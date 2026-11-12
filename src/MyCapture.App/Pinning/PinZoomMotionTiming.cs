using System.Diagnostics;
using MyCapture.Core.Pin;

namespace MyCapture.App.Pinning;

/// <summary>
/// The timing both pin hosts give a wheel or key zoom (<see cref="PinZoomMotion"/>): a sixth of a second
/// from the zoom shown to the new one, with the first step drawn in the input's own turn.
/// </summary>
internal static class PinZoomMotionTiming
{
    /// <summary>
    /// The Normal motion token (167 ms). A placement of a 1600 x 1000 pin takes about two 120 Hz frames, so a
    /// notch drew 3-4 sizes at 83 ms, 4-6 at 125 ms and 5-8 at 167 ms; at 167 ms a WPF pin's one-frame picture
    /// lag per step was also smallest (median 8-9 px against 16-37 px at 83 ms). Measured 2026-11-13.
    /// </summary>
    internal const double DefaultDurationMs = 167;

    /// <summary>Half a 60 Hz frame: the first frame moves a small part of the step, not a jump.</summary>
    internal const double LeadMs = 8;

    /// <summary>The duration new zoom motions take; the resident probe compares candidates with it.</summary>
    internal static double DurationMs { get; set; } = DefaultDurationMs;

    internal static PinZoomMotion Create() => new(DurationMs, LeadMs);

    /// <summary>The motion clock in milliseconds.</summary>
    internal static double Now() => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
}
