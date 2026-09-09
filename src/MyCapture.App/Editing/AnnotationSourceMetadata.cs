using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;

namespace MyCapture.App.Editing;

/// <summary>Capture provenance without retaining the full desktop pixel buffer.</summary>
internal sealed record AnnotationSourceMetadata(RectD ScreenBounds, MonitorInfo? Monitor, double ElapsedMilliseconds)
{
    internal double DpiScale => Monitor?.ScaleFactor ?? 1.0;

    internal static AnnotationSourceMetadata FromFrame(FrozenFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new(frame.ScreenBounds, frame.Monitor, frame.ElapsedMilliseconds);
    }
}
