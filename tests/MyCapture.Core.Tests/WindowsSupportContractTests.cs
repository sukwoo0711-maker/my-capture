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
/// Windows 10 22H2 baseline; Windows 11 is required only for ARM64 x64 emulation.
/// </summary>
public sealed class WindowsSupportContractTests
{
    /// <summary>
    /// Matches minimum-OS claims without rejecting optional Windows 11 features or SDK monikers.
    /// </summary>
    private static readonly Regex Windows11MinimumClaim = new(
        @"\bMyCapture\s+(?:requires?|needs?)\s+(?:at\s+least\s+)?Windows\s+11\b"
        + @"|\bMyCapture\s+(?:runs?\s+only\s+on|can\s+(?:run|install)\s+only\s+on)\s+Windows\s+11\b"
        + @"|\bMyCapture\s+(?:ships\s+for|runs?\s+on)\s+Windows\s+11\s+(?:only|or\s+(?:later|newer))\b"
        + @"|MyCapture(?:는|은)?\s+Windows\s+11\s+이상에서만\s+(?:실행|설치)"
        + @"|Windows\s+11\s+is\s+(?:the\s+)?(?:supported\s+baseline|minimum(?:\s+OS)?|support\s+floor)"
        + @"|Windows\s+11\s+is\s+required\s+to\s+(?:run|install|launch)\s+MyCapture\b"
        + @"|(?:minimum\s+(?:OS|operating\s+system)|system\s+requirements)\s*[:=]\s*Windows\s+11\b"
        + @"|^\W*Windows\s+11\s+(?:or\s+(?:later|newer)|이상|전용|only)\s*(?:[,.:;""'<|]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsWindows11MinimumClaim(string text) =>
        Regex.Split(Regex.Replace(text, @"<[^>]+>", " "), @"[.!?;。\r\n]+").Any(claim =>
            Windows11MinimumClaim.IsMatch(claim)
            && !Regex.IsMatch(claim, @"ARM64.*(?:emulation|에뮬레이션)|(?:emulation|에뮬레이션).*ARM64", RegexOptions.IgnoreCase));

    [Theory]
    [InlineData("MyCapture requires Windows 11 or later.", true)]
    [InlineData("MyCapture requires Windows 11. ARM64 uses x64 emulation.", true)]
    [InlineData("MyCapture requires Windows 11.ARM64 uses x64 emulation.", true)]
    [InlineData("<value>MyCapture requires Windows 11.</value><value>ARM64 uses x64 emulation.</value>", true)]
    [InlineData("ARM64 uses x64 emulation. MyCapture requires Windows 11.", true)]
    [InlineData("ARM64 requires Windows 11 for x64 emulation; MyCapture requires Windows 11.", true)]
    [InlineData("This optional Windows 11 feature is required only for borderless capture.", false)]
    [InlineData("Windows 11 is required only for borderless capture.", false)]
    [InlineData("This optional feature works on Windows 11 only.", false)]
    [InlineData("MyCapture runs on Windows 11 and Windows 10.", false)]
    [InlineData("Minimum OS: Windows 11.", true)]
    [InlineData("MyCapture runs only on Windows 11.", true)]
    [InlineData("MyCapture uses an optional Windows 11 feature required only for borderless capture.", false)]
    [InlineData("Windows 11 is required to install MyCapture.", true)]
    [InlineData("Windows 11 is the supported baseline.", true)]
    [InlineData("MyCapture는 Windows 11 이상에서만 실행됩니다.", true)]
    [InlineData("Windows 11 이상, x64", true)]
    [InlineData("Windows 11 전용", true)]
    [InlineData("MyCapture ships for Windows 11 only.", true)]
    [InlineData("Windows 10 version 22H2 or later", false)]
    [InlineData("ARM64 requires Windows 11 for x64 emulation.", false)]
    [InlineData("Windows 11 rounded corners", false)]
    [InlineData("Windows 11 22H2 updates enabled Print Screen", false)]
    [InlineData("net10.0-windows10.0.22000.0", false)]
    [InlineData("10.0.17763.0", false)]
    [InlineData("B25409A18643DB2081CE2B1809CD5DC87997C85498AB1FF5DFBB81BD060C0931", false)]
    [InlineData("AA17763BB", false)]
    [InlineData("AA19045BB", false)]
    public void SupportClaimMatcherDistinguishesVersionTokensFromSdkMonikersAndHashes(string text, bool expected)
    {
        Assert.Equal(expected, IsWindows11MinimumClaim(text));
    }

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
    public void DirectoryBuildProps_DeclaresTheSharedWindows10ApiContract()
    {
        string props = ReadRepositoryFile("Directory.Build.props");
        Assert.Contains(
            $"<SupportedOSPlatformVersion>{WindowsSupportPolicy.SupportedOSPlatformVersion}</SupportedOSPlatformVersion>",
            props,
            StringComparison.Ordinal);
        Assert.Contains("<TargetFramework>net10.0-windows10.0.22000.0</TargetFramework>", props, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageScript_StampsTheWindows10CompatibilityContractAndArm64Exception()
    {
        string package = ReadRepositoryFile("build/package.ps1");

        // Both the installer manifest and the release manifest carry the floor.
        int occurrences = Regex.Matches(package, $@"MinimumWindowsBuild\s*=\s*{WindowsSupportPolicy.MinimumBuild}\b").Count;
        Assert.True(occurrences >= 2, $"Expected at least 2 MinimumWindowsBuild={WindowsSupportPolicy.MinimumBuild} stamps, found {occurrences}.");

        Assert.Contains($"MinimumWindowsRelease = '{WindowsSupportPolicy.MinimumReleaseName}'", package, StringComparison.Ordinal);
        Assert.Matches($@"MinimumArm64EmulationBuild\s*=\s*{WindowsSupportPolicy.MinimumArm64EmulationBuild}\b", package);
        Assert.Contains("Arm64Support = 'Windows 11 ARM64 via x64 emulation'", package, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerPreflight_UsesManifestFloorAndKeepsTheArm64Exception()
    {
        string install = ReadRepositoryFile("build/installer/install.ps1");
        Assert.Contains(WindowsSupportPolicy.MinimumReleaseName, install, StringComparison.Ordinal);
        Assert.Contains("$minimumBuild = [int]$Manifest.MinimumWindowsBuild", install, StringComparison.Ordinal);
        Assert.Contains("$build -lt $minimumBuild", install, StringComparison.Ordinal);
        Assert.Contains($"$architectureText -match 'ARM64' -and $build -lt {WindowsSupportPolicy.MinimumArm64EmulationBuild}", install, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorAndStorePackaging_UseTheHostFloorInsteadOfTheProjectionTarget()
    {
        string doctor = ReadRepositoryFile("build/doctor.ps1");
        Assert.Contains($"Version.Build -lt {WindowsSupportPolicy.MinimumBuild}", doctor, StringComparison.Ordinal);
        string store = ReadRepositoryFile("build/package-store.ps1");
        Assert.Contains("$minimumVersion = [Version]$manifestTemplate.Package.Dependencies.TargetDeviceFamily.MinVersion", store, StringComparison.Ordinal);
        Assert.Contains("[Version]$MaxVersionTested -lt $minimumVersion", store, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ko-KR", "MyCapture는 Windows 10 버전 22H2 이상에서만 실행됩니다.")]
    [InlineData("en-US", "MyCapture requires Windows 10 version 22H2 or later.")]
    public void HostRequirementCopy_UsesTheSupportedFloorInBothLanguages(string language, string expected)
    {
        using IDisposable scope = MyCapture.Core.Localization.UiText.UseLanguage(language);
        Assert.Equal(expected, MyCapture.Core.Localization.UiText.Get("Text_E04C819FB554"));
    }

    [Fact]
    public void NoShippedSourceOrBuildFileRequiresWindows11ExceptArm64Emulation()
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
            string[] lines = File.ReadAllLines(file);
            for (int line = 0; line < lines.Length; line++)
            {
                if (IsWindows11MinimumClaim(lines[line]))
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{line + 1}: {lines[line].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Windows 11 minimum claims must be limited to ARM64 x64 emulation:\n"
            + string.Join(Environment.NewLine, offenders));
    }

    private static bool IsTextArtefact(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".cs" or ".ps1" or ".props" or ".csproj" or ".manifest" or ".xaml" or ".json" or ".txt" or ".sed" or ".resx" or ".xml";
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
