using System.IO;
using System.Windows;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Editing;

internal sealed partial class AnnotationEditorControl
{
    /// <summary>Distance between the centres of images inserted together, so they do not hide one another.</summary>
    internal const double InsertCascadeStep = 24;

    /// <summary>How many cascade steps fit before the offsets start over.</summary>
    private const int InsertCascadeLength = 10;

    /// <summary>
    /// Replaces where the library's images come from. By default the app-wide source set in
    /// <see cref="AnnotationEditorPreferences.LibraryImages"/> is read, excluding the capture
    /// being edited. Without either, the file dialog is used directly.
    /// </summary>
    internal Func<IReadOnlyList<LibraryImageEntry>?>? LibraryImagesOverride { get; set; }

    /// <summary>Shows the picker and returns its choice, or <see langword="null"/> when cancelled. Tests substitute the modal window.</summary>
    internal Func<IReadOnlyList<LibraryImageEntry>, LibraryImagePick?>? LibraryImagePickerOverride { get; set; }

    private IReadOnlyList<LibraryImageEntry>? ReadLibraryImages() => LibraryImagesOverride is { } read
        ? read()
        : AnnotationEditorPreferences.LibraryImages?.Invoke(SourceFilePath);

    private LibraryImagePick? ShowLibraryImagePicker(IReadOnlyList<LibraryImageEntry> entries)
    {
        if (LibraryImagePickerOverride is { } show) return show(entries);

        // Built when needed and dropped on close, so the thumbnails it decoded are released.
        var dialog = new LibraryImagePickerDialog(entries);
        if (Window.GetWindow(this) is { IsVisible: true } owner) dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    /// <summary>
    /// Inserts the picked library images, one undo step each, around <paramref name="image"/>:
    /// each later one sits a little down and to the right of the one before. Each uses the
    /// capture's current picture (its edited result, else its original), read once and kept in
    /// memory, so deleting the capture afterwards leaves the inserted picture in place.
    /// </summary>
    internal Task InsertLibraryImagesAsync(IReadOnlyList<LibraryImageEntry> entries, PointD image) =>
        WhilePendingImageAsync(async () =>
        {
            for (int index = 0; index < entries.Count; index++)
            {
                if (_completed || _imageInsertCts is { IsCancellationRequested: true }) return;
                LibraryImageEntry entry = entries[index];
                string? path;
                try
                {
                    path = await Task.Run(entry.ResolveImagePath);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                {
                    path = null;
                }

                if (path is null)
                {
                    ReportUnreadableImage();
                    continue;
                }

                double step = InsertCascadeStep * (index % InsertCascadeLength);
                await InsertPendingImageAsync(path, new PointD(image.X + step, image.Y + step));
            }
        });
}
