using System.IO;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// Explicit CLI roots with confined, locally named diagnostic artifacts. <see cref="Create"/> makes a
/// new output directory; <see cref="ExistingDirectory"/> and <see cref="ExistingFile"/> only validate
/// inputs that must already exist and never create anything.
/// </summary>
internal static class DiagnosticOutputPaths
{
    internal static string Create(string directory)
    {
        string root = CanonicalRoot(directory);
        AssertNoLinks(root);
        Directory.CreateDirectory(root);
        AssertNoLinks(root);
        return root;
    }

    /// <summary>An existing input directory with the same local, no-link contract as an output root.</summary>
    internal static string ExistingDirectory(string directory)
    {
        string root = CanonicalRoot(directory);
        AssertNoLinks(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("Diagnostic input directory does not exist.");
        return root;
    }

    /// <summary>An existing regular input file named directly below <paramref name="directory"/>.</summary>
    internal static string ExistingFile(string directory, string name)
    {
        string path = Child(directory, name);
        // Missing files throw FileNotFoundException here; directories and devices are not inputs.
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Diagnostic input must be an existing regular file.");
        return path;
    }

    internal static string Child(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\') ||
            name.EndsWith('.') || name.EndsWith(' '))
            throw new IOException("Diagnostic artifact names must be a single unambiguous filename.");
        string prefix = CanonicalRoot(directory) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(prefix, name));
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Diagnostic artifact escaped its output directory.");
        AssertNoLinks(path);
        return path;
    }

    /// <summary>A mapped drive letter is still a network location, whatever its path looks like.</summary>
    internal static bool IsLocalDriveType(DriveType type) => type != DriveType.Network;

    private static string CanonicalRoot(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        // Relative CLI paths remain relative to the caller's working directory, resolved once.
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (root.StartsWith(@"\\", StringComparison.Ordinal) ||
            string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase) ||
            !IsLocalDriveType(new DriveInfo(root).DriveType))
            throw new IOException("Diagnostic output requires a local directory below a drive root.");
        return root;
    }

    private static void AssertNoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Diagnostic output cannot contain symbolic links or junctions.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
