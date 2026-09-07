namespace MyCapture.App.Recording;

/// <summary>Explicit quality presets; original-size clips are never enlarged.</summary>
internal sealed record GifExportQuality(string Label, int LongEdge, int FramesPerSecond)
{
    internal static GifExportQuality Standard { get; } = new("표준 · 960px / 10fps", 960, 10);
    internal static GifExportQuality Compact { get; } = new("작게 · 640px / 10fps", 640, 10);
    internal static GifExportQuality Smallest { get; } = new("최소 용량 · 480px / 5fps", 480, 5);

    internal void Validate()
    {
        if (LongEdge is < 1 or > 960 || FramesPerSecond is not (5 or 10))
        {
            throw new ArgumentOutOfRangeException(nameof(GifExportQuality));
        }
    }
}
