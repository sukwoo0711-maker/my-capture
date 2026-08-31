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

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> contents) =>
        WriteAllBytesCore(path, contents, keepRecoveryBackup: true);

    /// <summary>
    /// Atomically replaces a user-facing export without leaving a sibling <c>.bak</c> file.
    /// </summary>
    /// <remarks>
    /// Settings and indexes keep a recovery backup because the app consumes it after a torn
    /// write. An explicitly exported image is different: the user already approved replacing
    /// the destination and an unexplained backup beside every PNG pollutes their Pictures
    /// folder. <see cref="File.Replace(string, string, string?)"/> remains atomic when the
    /// backup argument is <see langword="null"/>.
    /// </remarks>
    public static void WriteExportBytes(string path, ReadOnlySpan<byte> contents) =>
        WriteAllBytesCore(path, contents, keepRecoveryBackup: false);

    /// <summary>
    /// Atomically creates a new user-facing export and returns <see langword="false"/> when
    /// another writer already claimed the destination. Existing files are never replaced.
    /// </summary>
    public static bool TryWriteNewExportBytes(string path, ReadOnlySpan<byte> contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{path}.{Guid.NewGuid():N}{TempSuffix}";
        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(tempPath, path);
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                // A concurrent quick save won the atomic create. The caller can choose the
                // next suffix without overwriting or re-encoding user data.
                return false;
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static void WriteAllBytesCore(
        string path,
        ReadOnlySpan<byte> contents,
        bool keepRecoveryBackup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // A unique sibling temp keeps independent saves to the same destination from sharing
        // a writable file. The final replace/move is still atomic and stays on the same volume.
        string tempPath = $"{path}.{Guid.NewGuid():N}{TempSuffix}";
        string backupPath = path + BackupSuffix;

        try
        {
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
                File.Replace(
                    tempPath,
                    path,
                    keepRecoveryBackup ? backupPath : null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }

        }
        finally
        {
            // A denied destination or failed volume swap must not leave an opaque .tmp beside
            // the user's chosen file. Settings recovery still uses the separate .bak path.
            TryDelete(tempPath);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        TryDelete(fullPath + TempSuffix); // legacy fixed-name temp

        string? directory = Path.GetDirectoryName(fullPath);
        string fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (string candidate in Directory.EnumerateFiles(
                         directory,
                         fileName + ".*" + TempSuffix,
                         SearchOption.TopDirectoryOnly))
            {
                string candidateName = Path.GetFileName(candidate);
                int prefixLength = fileName.Length + 1;
                int tokenLength = candidateName.Length - prefixLength - TempSuffix.Length;
                if (tokenLength == 32
                    && Guid.TryParseExact(candidateName.Substring(prefixLength, tokenLength), "N", out _))
                {
                    TryDelete(candidate);
                }
            }
        }
        catch (IOException)
        {
            // A locked leftover is harmless and can be retried next startup.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
