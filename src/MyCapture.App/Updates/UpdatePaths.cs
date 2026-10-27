using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using MyCapture.Core.Platform;

namespace MyCapture.App.Updates;

/// <summary>Canonical paths for locally named, single-session update files.</summary>
internal static class UpdatePaths
{
    private static readonly Regex SessionPattern = new(
        @"\Aupdate-(?<version>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))-(?<token>[a-f0-9]{32})\z",
        RegexOptions.CultureInvariant);

    internal static bool TryParseSessionName(string name, out UpdateVersion version, out string token)
    {
        version = default;
        token = string.Empty;
        Match match = SessionPattern.Match(name);
        if (!match.Success || !UpdateVersion.TryParse(match.Groups["version"].Value, out UpdateVersion? parsed))
            return false;
        version = parsed.Value;
        token = match.Groups["token"].Value;
        return true;
    }

    internal static string ChecksumName => AppArchitecture.IsX86 ? "SHA256SUMS-win-x86.txt" : "SHA256SUMS.txt";
    internal static string SessionName(UpdateVersion version)
    {
        _ = InstallerName(version); // Apply the same stable numeric version policy.
        return string.Create(CultureInfo.InvariantCulture,
            $"update-{version.Major}.{version.Minor}.{version.Patch}-{Guid.NewGuid():N}");
    }

    internal static string InstallerName(UpdateVersion version, string? architecture = null)
    {
        architecture ??= AppArchitecture.Name;
        if (architecture is not ("x86" or "x64"))
            throw new IOException("Unsupported update architecture.");
        if (version.IsPrerelease || version.Major < 0 || version.Minor < 0 || version.Patch < 0)
            throw new IOException("Only stable numeric update versions can be staged.");
        return string.Create(CultureInfo.InvariantCulture,
            $"MyCapture-{version.Major}.{version.Minor}.{version.Patch}-win-{architecture}-setup.exe");
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
