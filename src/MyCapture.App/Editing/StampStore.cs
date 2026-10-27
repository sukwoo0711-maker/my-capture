using System.IO;
using System.Windows.Media.Imaging;
using MyCapture.Core.Storage;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Editing;

/// <summary>
/// The one registered stamp picture, kept as a PNG file under the app's data folder.
/// </summary>
/// <remarks>
/// <para>
/// Registering copies the pixels, never the path: once the stamp is saved the original file can
/// be moved or deleted. The folder comes from <see cref="AnnotationEditorPreferences.StampFolder"/>
/// (a subfolder of the data root, so <c>MYCAPTURE_DATA_ROOT</c> isolation applies) and the file
/// name is fixed, so nothing a user types or a settings file holds can name another location.
/// </para>
/// <para>
/// Nothing here runs when the editor opens. The file is looked up only when the stamp command is
/// used or its menu opens.
/// </para>
/// </remarks>
internal static class StampStore
{
    /// <summary>The subfolder of the data root that holds the stamp.</summary>
    internal const string FolderName = "stamp";

    /// <summary>The stored picture's file name inside the folder.</summary>
    internal const string FileName = "stamp.png";

    /// <summary>The longest side kept, in pixels. A larger logo is scaled down once, when registered.</summary>
    internal const int MaxLongEdge = 1024;

    /// <summary>The stamp folder, or <see langword="null"/> where the host provides none.</summary>
    internal static string? Folder => AnnotationEditorPreferences.StampFolder?.Invoke();

    internal static string PathIn(string folder) => Path.Combine(folder, FileName);

    /// <summary>The stored picture's path, or <see langword="null"/> when no stamp is registered.</summary>
    internal static string? RegisteredPath(string folder)
    {
        string path = PathIn(folder);
        try
        {
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stores <paramref name="picture"/> as the stamp, scaled so its longest side is at most
    /// <see cref="MaxLongEdge"/> (a smaller picture is kept as it is), replacing any earlier stamp.
    /// Safe on any thread when <paramref name="picture"/> is frozen.
    /// </summary>
    /// <returns>The stored size in pixels.</returns>
    internal static (int Width, int Height) Save(BitmapSource picture, string folder)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        BitmapSource stored = ImageCodec.CreateThumbnail(picture, MaxLongEdge);
        if (!stored.IsFrozen && stored.CanFreeze) stored.Freeze();
        Directory.CreateDirectory(folder);
        ImageCodec.SavePngExport(stored, PathIn(folder));
        return (stored.PixelWidth, stored.PixelHeight);
    }

    /// <summary>Recycles the stored picture. Returns true only after a confirmed recoverable move.</summary>
    internal static bool Delete(string folder)
    {
        string? path = RegisteredPath(folder);
        if (path is null) return false;
        try
        {
            RecoverableFileRemoval.DeleteFile(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }
}
