using System.Text;

namespace MyCapture.Core.Storage;

/// <summary>
/// Writes files so that a crash or power loss can never leave a half-written file
/// in place of a good one.
/// </summary>
/// <remarks>
/// <para>
/// The capture index and the settings file are both single points of failure: a
/// truncated index loses the user's entire 300-item history. Every write therefore
/// goes to a sibling temporary file which is flushed to disk and only then swapped
/// into place.
/// </para>
/// <para>
/// <see cref="File.Replace(string, string, string?)"/> is preferred over
/// <see cref="File.Move(string, string, bool)"/> because it performs the swap and
/// produces a backup of the previous contents in one operation, giving a recovery
/// source if the new contents turn out to be unparseable.
/// </para>
/// </remarks>
public static class AtomicFile
{
    private const string TempSuffix = ".tmp";
    public const string BackupSuffix = ".bak";

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = path + TempSuffix;
        string backupPath = path + BackupSuffix;

        using (var stream = new FileStream(
                   tempPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 64 * 1024,
                   FileOptions.WriteThrough))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            // ignoreMetadataErrors avoids failing the swap when the destination
            // lives on a volume that cannot carry all of the source's metadata,
            // which happens when the user relocates the data folder to a network
            // or removable drive.
            File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }

    public static void WriteAllText(string path, string contents) =>
        WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents));

    /// <summary>
    /// Reads <paramref name="path"/>, falling back to the backup produced by a
    /// previous atomic write when the primary file is missing or rejected by
    /// <paramref name="validate"/>.
    /// </summary>
    /// <param name="validate">
    /// Returns <see langword="true"/> when the text is usable. Supplying a real
    /// parse here (rather than a length check) is what makes the fallback
    /// meaningful: a file can exist, be non-empty, and still be corrupt.
    /// </param>
    /// <returns>
    /// The recovered text, or <see langword="null"/> when neither the primary nor
    /// the backup is usable.
    /// </returns>
    public static string? ReadAllTextWithRecovery(string path, Func<string, bool> validate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(validate);

        foreach (string candidate in new[] { path, path + BackupSuffix })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(candidate, Encoding.UTF8);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            bool ok;
            try
            {
                ok = validate(text);
            }
            catch
            {
                ok = false;
            }

            if (ok)
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>
    /// Removes any temporary file left behind by an interrupted write.
    /// </summary>
    public static void CleanUpTemp(string path)
    {
        string tempPath = path + TempSuffix;
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (IOException)
        {
            // A leftover temp file is harmless; the next write overwrites it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
