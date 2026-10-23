using System.Windows.Media.Imaging;
using MyCapture.Core.Capture;

namespace MyCapture.App.Gallery;

internal sealed class GalleryFloatRequestedEventArgs(BitmapSource image, CaptureProvenance provenance) : EventArgs
{
    internal BitmapSource Image { get; } = image;
    internal CaptureProvenance Provenance { get; } = provenance;
}
