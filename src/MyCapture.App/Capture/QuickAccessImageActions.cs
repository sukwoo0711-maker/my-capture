using System.Windows.Media.Imaging;
using MyCapture.App.Gallery;

namespace MyCapture.App.Capture;

/// <summary>All receipt outputs resolve the current edited image through one source.</summary>
internal sealed class QuickAccessImageActions(
    Func<Task<BitmapSource>> render,
    Func<BitmapSource, Task<bool>> copy,
    Action<BitmapSource> pin,
    Func<BitmapSource, Task<bool>> save,
    string? sourceUrl = null, GalleryDragExportService? exportService = null)
{
    internal async Task<bool> CopyAsync() => await copy(await render());
    internal async Task PinAsync() => pin(await render());
    internal async Task<bool> SaveAsync() => await save(await render());
    internal async Task<string> ExportAsync()
    {
        BitmapSource image = await render();
        return await Task.Run(() => (exportService ?? new GalleryDragExportService()).PrepareExport(image, sourceUrl));
    }
}
