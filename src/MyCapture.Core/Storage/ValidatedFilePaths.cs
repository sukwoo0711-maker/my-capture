namespace MyCapture.Core.Storage;

/// <summary>Validates explicitly chosen roots and confines their files before filesystem access.</summary>
public static class ValidatedFilePaths
{
    public static string DirectoryRoot(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!HasUnambiguousSegments(directory)) throw new IOException("Ambiguous directory name.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!IsPlainDirectory(root))
            throw new IOException("The selected directory must have an unambiguous path without reparse points.");
        return root;
    }

    public static string CreateDirectory(string directory)
    {
        string root = DirectoryRoot(directory);
        System.IO.Directory.CreateDirectory(root);
        return DirectoryRoot(root);
    }

    /// <summary>Creates a GUID-isolated fixture directory below the OS temporary directory.</summary>
    public static string CreateTemporaryDirectory() => CreateTemporaryDirectory(Path.GetTempPath());

    internal static string CreateTemporaryDirectory(string temporaryRoot)
    {
        // The OS-selected temp root may itself be redirected. Resolve that trusted
        // location before applying the strict fixture policy to our own descendants.
        string resolved = Path.GetPathRoot(Path.GetFullPath(temporaryRoot))!;
        foreach (string segment in Path.GetFullPath(temporaryRoot)[resolved.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(resolved, segment));
            resolved = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
        }
        string root = CreateDirectory(Path.Combine(resolved, "MyCapture", "Diagnostics"));
        string path = Path.GetFullPath(Path.Combine(root, Guid.NewGuid().ToString("N")));
        if (!IsWithinRoot(root, path) || !IsPlainDirectory(path))
            throw new IOException("The temporary directory escaped its approved root.");
        System.IO.Directory.CreateDirectory(path);
        return DirectoryRoot(path);
    }

    /// <summary>Resolves relative names against an approved root; rejects escapes and linked/special files.</summary>
    public static string FileWithinRoot(string directory, string path, params string[] extensions)
    {
        string root = DirectoryRoot(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!HasUnambiguousSegments(path)) throw new IOException("Ambiguous file name.");
        if (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path))
            throw new IOException("Drive-relative paths are not permitted.");
        string fullPath = Path.GetFullPath(path, root);
        if (!IsWithinRoot(root, fullPath) || !IsRegularFile(fullPath, mustExist: false, extensions))
            throw new IOException("The file must remain within its approved directory and use an allowed file type.");
        return fullPath;
    }

    /// <summary>Confines settings lexically while allowing redirected user profiles and linked settings.</summary>
    public static string SettingsFileWithinRoot(string directory, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!HasUnambiguousSegments(directory) || !HasUnambiguousSegments(path))
            throw new IOException("Ambiguous settings path.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path))
            throw new IOException("Drive-relative paths are not permitted.");
        string fullPath = Path.GetFullPath(path, root);
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Settings must remain within the data root and use the JSON extension.");
        return fullPath;
    }

    /// <summary>The file chooser grants access to this exact file, rather than to arbitrary sibling paths.</summary>
    public static string SelectedFile(string selectedPath, params string[] extensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        if (!HasUnambiguousSegments(selectedPath)) throw new IOException("Ambiguous selected file name.");
        if (!Path.IsPathFullyQualified(selectedPath))
            throw new IOException("A selected file must have an absolute path.");
        string fullPath = Path.GetFullPath(selectedPath);
        if (!IsUnambiguousPath(fullPath) || !extensions.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase)
            || !File.Exists(fullPath)
            || !IsSelectedFileType(File.GetAttributes(fullPath)))
            throw new IOException("The selected file must be an existing regular file with an allowed extension.");
        return fullPath;
    }

    internal static bool IsSelectedFileType(FileAttributes attributes) =>
        (attributes & (FileAttributes.Directory | FileAttributes.Device)) == 0;

    private static bool IsWithinRoot(string root, string path)
    {
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        // Ordinal also protects case-sensitive directories from a differently cased sibling.
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static bool IsRegularFile(string path, bool mustExist, string[] extensions)
    {
        if (!IsUnambiguousPath(path) || !HasNoReparsePoints(path)) return false;
        if (extensions.Length > 0 && !extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            return false;
        try { return (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) == 0; }
        catch (FileNotFoundException) { return !mustExist; }
        catch (DirectoryNotFoundException) { return !mustExist; }
    }

    private static bool IsPlainDirectory(string path)
    {
        if (!IsUnambiguousPath(path) || !HasNoReparsePoints(path)) return false;
        try { return (File.GetAttributes(path) & FileAttributes.Directory) != 0; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }

    private static bool HasNoReparsePoints(string path)
    {
        if (!IsUnambiguousPath(path)) return false;
        bool leaf = true;
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
                    (!leaf && (attributes & FileAttributes.Directory) == 0)) return false;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            leaf = false;
        }
        return true;
    }

    private static bool IsUnambiguousPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
        return HasUnambiguousSegments(path) && string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal);
    }

    private static bool HasUnambiguousSegments(string path)
    {
        int rootLength = Path.GetPathRoot(path)?.Length ?? 0;
        foreach (string segment in path[rootLength..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            // Traversal is normalized before the approved-root comparison; do not silently
            // accept Win32 trimming of any other segment before that comparison.
            if (segment is "." or "..") continue;
            if (segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            string stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')) return false;
        }
        return true;
    }
}
