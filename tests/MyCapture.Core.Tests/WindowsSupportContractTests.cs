using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MyCapture.Core.Platform;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Keeps the shipped artefacts honest about the supported host. The runtime policy, the MSBuild
/// platform floor, the installer preflight and the release manifest must all state the same
/// Windows 11 baseline; a stray Windows 10 claim anywhere in src/ or build/ is a contract break.
/// </summary>
public sealed class WindowsSupportContractTests
{
    /// <summary>
    /// Matches Windows 10 *support claims*, not the Windows SDK target moniker. The TFM
    /// "net10.0-windows10.0.22000.0" only selects the WinRT projection surface and is
    /// deliberately not treated as a support statement.
    /// </summary>
    private static readonly Regex Windows10Claim = new(
        @"Windows\s+10\b|(?<![.\d])1809(?![.\d])|(?<![.\d])17763(?![.\d])|(?<![.\d])19045(?![.\d])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            bool hasProps = File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"));
            bool hasSrc = Directory.Exists(Path.Combine(dir.FullName, "src"));
            bool hasBuild = Directory.Exists(Path.Combine(dir.FullName, "build"));
            if (hasProps && hasSrc && hasBuild)
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Repository root was not found above '{AppContext.BaseDirectory}'.");
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        string full = Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Expected repository file '{relativePath}' at '{full}'.");
        return File.ReadAllText(full);
    }

    [Fact]
    public void DirectoryBuildProps_DeclaresTheWindows11PlatformFloor()
    {
        string props = ReadRepositoryFile("Directory.Build.props");
        Assert.Contains(
            $"<SupportedOSPlatformVersion>{WindowsSupportPolicy.SupportedOSPlatformVersion}</SupportedOSPlatformVersion>",
            props,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PackageScript_StampsTheWindows11CompatibilityContract()
    {
        string package = ReadRepositoryFile("build/package.ps1");

        // Both the installer manifest and the release manifest carry the floor.
        int occurrences = Regex.Matches(package, $@"MinimumWindowsBuild\s*=\s*{WindowsSupportPolicy.MinimumBuild}\b").Count;
        Assert.True(occurrences >= 2, $"Expected at least 2 MinimumWindowsBuild={WindowsSupportPolicy.MinimumBuild} stamps, found {occurrences}.");

        Assert.Contains($"MinimumWindowsRelease = '{WindowsSupportPolicy.MinimumReleaseName}'", package, StringComparison.Ordinal);
        Assert.Contains("Windows 11", package, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerPreflight_RejectsHostsBelowTheWindows11Floor()
    {
        string install = ReadRepositoryFile("build/installer/install.ps1");
        Assert.Contains("Windows 11", install, StringComparison.Ordinal);
        Assert.Contains("MinimumWindowsBuild", install, StringComparison.Ordinal);
    }

    [Fact]
    public void NoShippedSourceOrBuildFileClaimsWindows10Support()
    {
        string root = RepositoryRoot();
        var offenders = new List<string>();

        IEnumerable<string> files = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "build"), "*.*", SearchOption.AllDirectories))
            .Append(Path.Combine(root, "Directory.Build.props"))
            .Where(p => IsTextArtefact(p) && !IsBuildOutput(p, root));

        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            Match match = Windows10Claim.Match(text);
            if (match.Success)
            {
                int line = text.Take(match.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetRelativePath(root, file)}:{line}: {match.Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Windows 10 support claims must be gone from shipped source and build scripts:\n"
            + string.Join(Environment.NewLine, offenders));
    }

    private static bool IsTextArtefact(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".cs" or ".ps1" or ".props" or ".csproj" or ".manifest" or ".xaml" or ".json" or ".txt" or ".sed";
    }

    private static bool IsBuildOutput(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path);
        string[] parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p =>
            p.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || p.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || p.Equals("artifacts", StringComparison.OrdinalIgnoreCase)
            || p.Equals("dist", StringComparison.OrdinalIgnoreCase));
    }
}
