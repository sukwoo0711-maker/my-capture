namespace MyCapture.App.Recording;

/// <summary>Explicit quality presets; original-size clips are never enlarged.</summary>
internal sealed record GifExportQuality(string Label, int LongEdge, int FramesPerSecond)
{
    internal static GifExportQuality Standard { get; } = new(UiText.Get("Text_A492D3026996"), 960, 10);
    internal static GifExportQuality Compact { get; } = new(UiText.Get("Text_21281475C002"), 640, 10);
    internal static GifExportQuality Smallest { get; } = new(UiText.Get("Text_19FE9B81012C"), 480, 5);

    internal void Validate()
    {
        if (LongEdge is < 1 or > 960 || FramesPerSecond is not (5 or 10))
        {
            throw new ArgumentOutOfRangeException(nameof(GifExportQuality));
        }
    }
}
