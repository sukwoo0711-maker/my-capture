using System.IO;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;

namespace MyCapture.App.Editing;

/// <summary>Shared destination history for editor, library, quick-access and pin exports.</summary>
internal sealed class ImageExportLocation(
    Func<AppSettings> settings,
    Func<AppPaths> paths,
    ILogger log,
    Action? persistSettings = null)
{
    internal string SaveAsDirectory
    {
        get
        {
            ExportSettings export = settings().Export;
            return ExistingRememberedDirectory(export)
                ?? (!string.IsNullOrWhiteSpace(export.QuickSaveDirectoryOverride)
                    ? export.QuickSaveDirectoryOverride
                    : paths().QuickSaveRoot);
        }
    }

    internal string QuickSaveDirectory
    {
        get
        {
            ExportSettings export = settings().Export;
            return !string.IsNullOrWhiteSpace(export.QuickSaveDirectoryOverride)
                ? export.QuickSaveDirectoryOverride
                : ExistingRememberedDirectory(export) ?? paths().QuickSaveRoot;
        }
    }

    internal void RememberSavedPath(string path, bool persistEvenIfUnchanged = false)
    {
        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(directory)) return;
            AppSettings current = settings();
            if (!persistEvenIfUnchanged
                && string.Equals(current.Export.LastSaveDirectory, directory, StringComparison.OrdinalIgnoreCase)) return;
            string previous = current.Export.LastSaveDirectory;
            current.Export.LastSaveDirectory = directory;
            try { persistSettings?.Invoke(); }
            catch
            {
                current.Export.LastSaveDirectory = previous;
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException
                                   or InvalidOperationException or System.Text.Json.JsonException
                                   or System.Security.SecurityException
                                   or System.Security.Cryptography.CryptographicException)
        {
            // The image is already durable; a preference failure must not fail its export.
            log.LogWarning(ex, "Image saved, but its destination folder could not be remembered");
        }
    }

    private static string? ExistingRememberedDirectory(ExportSettings export)
    {
        string directory = export.LastSaveDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) return null;
        try
        {
            // Exists only checks metadata and can succeed when the picker cannot open the
            // folder. Probe one entry, including an empty folder, without scanning or writing.
            using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            _ = entries.MoveNext();
            return directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Keep the remembered path so a reconnected drive becomes available again.
            return null;
        }
    }
}
