using System.IO;
using System.Windows.Media.Imaging;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Capture;

/// <summary>All receipt outputs resolve the current edited image through one source.</summary>
internal sealed class QuickAccessImageActions(
    Func<Task<BitmapSource>> render,
    Func<BitmapSource, Task<bool>> copy,
    Action<BitmapSource> pin,
    Func<BitmapSource, Task<bool>> save,
    string? sourceUrl = null)
{
    internal async Task<bool> CopyAsync() => await copy(await render());
    internal async Task PinAsync() => pin(await render());
    internal async Task<bool> SaveAsync() => await save(await render());
    internal async Task<string> ExportAsync()
    {
        BitmapSource image = await render();
        return await Task.Run(() =>
        {
            string directory = Directory.CreateTempSubdirectory("MyCapture-QuickAccess-").FullName;
            string path = Path.Combine(directory, "MyCapture.png");
            byte[] png = ImageCodec.EncodePng(image, sourceUrl);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(png);
            return path;
        });
    }
}
