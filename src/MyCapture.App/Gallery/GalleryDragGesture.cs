namespace MyCapture.App.Gallery;

/// <summary>A release, Escape, capture loss or hidden window invalidates in-flight preparation.</summary>
internal sealed class GalleryDragGesture
{
    private int _generation;
    internal bool Armed { get; private set; }
    internal int Arm() { Armed = true; return ++_generation; }
    internal void Cancel() { Armed = false; _generation++; }
    internal bool CanStart(int generation, bool buttonPressed, bool visible) =>
        Armed && generation == _generation && buttonPressed && visible;
    internal int Generation => _generation;
}
