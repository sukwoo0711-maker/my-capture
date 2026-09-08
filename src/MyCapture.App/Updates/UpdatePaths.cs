using System.Globalization;
using System.IO;

namespace MyCapture.App.Updates;

/// <summary>Canonical paths for locally named, single-session update files.</summary>
internal static class UpdatePaths
{
    internal static string InstallerName(UpdateVersion version)
    {
        if (version.IsPrerelease || version.Major < 0 || version.Minor < 0 || version.Patch < 0)
            throw new IOException("Only stable numeric update versions can be staged.");
        return string.Create(CultureInfo.InvariantCulture,
            $"MyCapture-{version.Major}.{version.Minor}.{version.Patch}-win-x64-setup.exe");
    }

    internal static string CanonicalRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IOException("Update staging requires an absolute local directory.");
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(canonical, Path.GetPathRoot(canonical), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Update staging cannot use a drive root.");
        AssertExistingAncestors(canonical);
        return canonical;
    }

    internal static string Child(string parent, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new IOException("Update file names must be a single local path component.");
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        string child = Path.GetFullPath(Path.Combine(prefix, name));
        if (!child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Update path escaped its owning directory.");
        AssertExistingAncestors(child);
        return child;
    }

    // Inspect the nearest existing ancestor before creation, including dangling links.
    internal static void AssertExistingAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Update paths cannot contain symbolic links or junctions.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
