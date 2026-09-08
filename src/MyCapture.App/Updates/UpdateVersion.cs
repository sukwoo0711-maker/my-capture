using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace MyCapture.App.Updates;

/// <summary>
/// Immutable SemVer core version (Major.Minor.Patch) with optional prerelease identifier,
/// used for release comparisons and asset name formatting.
/// </summary>
public readonly partial record struct UpdateVersion : IComparable<UpdateVersion>, IComparable
{
    private static readonly Regex VersionRegex = new(
        @"^[vV]?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<prerelease>[0-9A-Za-z.-]+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? Prerelease { get; }

    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    public UpdateVersion(int major, int minor, int patch, string? prerelease = null)
    {
        if (major < 0 || minor < 0 || patch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(major), "Version numbers must be non-negative.");
        }

        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = string.IsNullOrWhiteSpace(prerelease) ? null : prerelease.Trim();
    }

    public static bool TryParse(string? text, [NotNullWhen(true)] out UpdateVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Match match = VersionRegex.Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["major"].Value, out int major) ||
            !int.TryParse(match.Groups["minor"].Value, out int minor) ||
            !int.TryParse(match.Groups["patch"].Value, out int patch))
        {
            return false;
        }

        string? prerelease = match.Groups["prerelease"].Success
            ? match.Groups["prerelease"].Value
            : null;

        version = new UpdateVersion(major, minor, patch, prerelease);
        return true;
    }

    public static UpdateVersion Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!TryParse(text, out UpdateVersion? version))
        {
            throw new FormatException($"String '{text}' was not recognized as a valid SemVer release version.");
        }

        return version.Value;
    }

    public static UpdateVersion FromVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        int patch = version.Build >= 0 ? version.Build : 0;
        return new UpdateVersion(version.Major, version.Minor, patch);
    }

    public string ToNormalizedString() =>
        IsPrerelease
            ? $"{Major}.{Minor}.{Patch}-{Prerelease}"
            : $"{Major}.{Minor}.{Patch}";

    public override string ToString() => ToNormalizedString();

    public int CompareTo(UpdateVersion other)
    {
        int majorCompare = Major.CompareTo(other.Major);
        if (majorCompare != 0)
        {
            return majorCompare;
        }

        int minorCompare = Minor.CompareTo(other.Minor);
        if (minorCompare != 0)
        {
            return minorCompare;
        }

        int patchCompare = Patch.CompareTo(other.Patch);
        if (patchCompare != 0)
        {
            return patchCompare;
        }

        // Standard SemVer: normal release has higher precedence than prerelease
        if (IsPrerelease && !other.IsPrerelease)
        {
            return -1;
        }

        if (!IsPrerelease && other.IsPrerelease)
        {
            return 1;
        }

        if (IsPrerelease && other.IsPrerelease)
        {
            return string.Compare(Prerelease, other.Prerelease, StringComparison.Ordinal);
        }

        return 0;
    }

    public int CompareTo(object? obj)
    {
        if (obj is null)
        {
            return 1;
        }

        if (obj is UpdateVersion other)
        {
            return CompareTo(other);
        }

        throw new ArgumentException($"Object must be of type {nameof(UpdateVersion)}.", nameof(obj));
    }

    public static bool operator <(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) < 0;
    public static bool operator <=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) > 0;
    public static bool operator >=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) >= 0;
}
