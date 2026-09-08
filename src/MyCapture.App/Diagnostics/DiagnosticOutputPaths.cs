using System.IO;

namespace MyCapture.App.Diagnostics;

/// <summary>Explicit CLI output roots with confined, locally named diagnostic artifacts.</summary>
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

    private static string CanonicalRoot(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        // Relative CLI paths remain relative to the caller's working directory, resolved once.
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (root.StartsWith(@"\\", StringComparison.Ordinal) ||
            string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
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
