namespace MyCapture.Core.Recording;

/// <summary>Constrains one text endpoint without moving the other or crossing clip bounds.</summary>
public static class TextLayerTiming
{
    public static (double StartMs, double EndMs) Resize(
        double startMs, double endMs, double durationMs, bool startHandle, double targetMs)
    {
        if (!double.IsFinite(durationMs) || durationMs <= 0
            || !double.IsFinite(startMs) || !double.IsFinite(endMs) || !double.IsFinite(targetMs))
        {
            throw new ArgumentOutOfRangeException(nameof(targetMs));
        }

        double minimum = Math.Min(10, durationMs);
        double end = Math.Clamp(endMs, minimum, durationMs);
        double start = Math.Clamp(startMs, 0, end - minimum);
        return startHandle
            ? (Math.Clamp(targetMs, 0, end - minimum), end)
            : (start, Math.Clamp(targetMs, start + minimum, durationMs));
    }
}
