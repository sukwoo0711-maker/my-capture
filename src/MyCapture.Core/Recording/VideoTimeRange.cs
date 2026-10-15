using System.Text.Json.Serialization;

namespace MyCapture.Core.Recording;

/// <summary>A half-open interval on the immutable source recording, in milliseconds.</summary>
public readonly record struct VideoTimeRange(double StartMs, double EndMs)
{
    [JsonIgnore]
    public double DurationMs => Math.Max(0, EndMs - StartMs);
}
