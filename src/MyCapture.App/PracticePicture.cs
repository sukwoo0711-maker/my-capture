using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace MyCapture.App;

internal static class PracticePicture
{
    internal static BitmapSource Load()
    {
        string path = Path.Combine(Path.GetDirectoryName(typeof(PracticePicture).Assembly.Location)!, "Assets", "practice-picture.png");
        using var stream = File.OpenRead(path);
        // BitmapImage detaches its decoder on load; a BitmapFrame can retain dispatcher-bound metadata
        // even when frozen, which prevents the pin history's background PNG encoder from using it.
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
    internal static uint Copy()
    {
        var data = new DataObject();
        data.SetImage(Load());
        ClipboardWriter.SetDataObject(data, true);
        return ClipboardWriter.GetVersion();
    }
}
