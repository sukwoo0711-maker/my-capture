namespace MyCapture.App.Recording;

/// <summary>Explicit quality presets; original-size clips are never enlarged.</summary>
internal sealed record GifExportQuality(string Label, int LongEdge, int FramesPerSecond)
{
    // Five fps remains available for existing small-file presets. Choices live only in the
    // export dialog, just like size and speed; they do not change recording settings.
    internal static IReadOnlyList<int> FrameRates { get; } = [5, 10, 15, 20, 25, 30, 50];
    internal static GifExportQuality Standard { get; } = new(UiText.Get("Text_A492D3026996"), 960, 10);
    internal static GifExportQuality Compact { get; } = new(UiText.Get("Text_21281475C002"), 640, 10);
    internal static GifExportQuality Smallest { get; } = new(UiText.Get("Text_19FE9B81012C"), 480, 5);

    internal void Validate()
    {
        if (LongEdge is < 1 or > 960 || !FrameRates.Contains(FramesPerSecond))
        {
            throw new ArgumentOutOfRangeException(nameof(GifExportQuality));
        }
    }

    internal static double EffectiveFrameRate(int requested, double sourceFramesPerSecond, double speed = 1) =>
        Math.Min(requested, Math.Min(sourceFramesPerSecond > 0 ? sourceFramesPerSecond : 10, 50 / speed));
}
