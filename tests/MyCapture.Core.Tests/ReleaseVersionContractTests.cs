using System;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Keeps the 1.5.0 GA version and its Windows binary version aligned across MSBuild, packaging
/// and installer validation.
/// </summary>
public sealed class ReleaseVersionContractTests
{
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(dir.FullName, "src"))
                && Directory.Exists(Path.Combine(dir.FullName, "build")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Repository root was not found above '{AppContext.BaseDirectory}'.");
    }

    private static string Read(string relativePath) => File.ReadAllText(
        Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void BuildDefaults_AreTheStableOnePointFiveBinaryVersion()
    {
        XDocument props = XDocument.Parse(Read("Directory.Build.props"));
        XDocument manifest = XDocument.Parse(Read("src/MyCapture.App/app.manifest"));

        Assert.Equal("1.5.0", Assert.Single(props.Descendants("Version")).Value);
        Assert.Equal("1.5.0.0", Assert.Single(props.Descendants("FileVersion")).Value);
        Assert.Equal("1.5.0.0", Assert.Single(props.Descendants("AssemblyVersion")).Value);

        XElement identity = Assert.Single(
            manifest.Descendants(),
            element => element.Name.LocalName == "assemblyIdentity");
        Assert.Equal("1.5.0.0", identity.Attribute("version")?.Value);
    }

    [Fact]
    public void PackageAndInstaller_DefaultToTheGaVersion_AndStillAcceptAPrerelease()
    {
        string package = Read("build/package.ps1");
        string installer = Read("build/installer/install.ps1");
        string hostile = Read("build/tests/installer-hostile-tests.ps1");

        // The shipping default is the GA version. A release candidate must never be the default
        // again, otherwise an operator who forgets -Version publishes a prerelease by accident.
        Assert.Contains("[string]$Version = '1.5.0'", package, StringComparison.Ordinal);
        Assert.Contains("[string]$Version = '1.5.0',", hostile, StringComparison.Ordinal);
        Assert.DoesNotContain("$Version = '1.5.0-rc.1'", package, StringComparison.Ordinal);
        Assert.DoesNotContain("$Version = '1.5.0-rc.1'", hostile, StringComparison.Ordinal);

        // A prerelease must still parse, so a future RC needs no script edit. The Windows binary
        // version stays numeric because Win32 version resources cannot carry a prerelease label.
        Assert.Contains("$semVerPattern", package, StringComparison.Ordinal);
        Assert.Contains("$baseVersion", package, StringComparison.Ordinal);
        Assert.Contains("$binaryVersion = \"$baseVersion.0\"", package, StringComparison.Ordinal);
        Assert.Contains("does not match the release version declared in Directory.Build.props", package, StringComparison.Ordinal);
        Assert.Contains("SemVer core", package, StringComparison.Ordinal);
        Assert.DoesNotContain("numeric SemVer", package, StringComparison.Ordinal);

        Assert.Contains("$semVerPattern", installer, StringComparison.Ordinal);
        Assert.Contains("$versionMatch", hostile, StringComparison.Ordinal);
        Assert.Contains("$baseVersion", hostile, StringComparison.Ordinal);
        Assert.Contains("$binaryVersion", hostile, StringComparison.Ordinal);
        Assert.Contains("Assert-Equal $baseVersion $sourceVersion", hostile, StringComparison.Ordinal);
        Assert.Contains("Assert-Equal $binaryVersion $sourceFileVersion", hostile, StringComparison.Ordinal);
        Assert.Contains("Assert-Equal $Version (($dllVersionInfo.ProductVersion -split '\\+')[0])", hostile, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseManifest_RecordsTheExactGitSourceCommit()
    {
        string package = Read("build/package.ps1");

        Assert.Contains("rev-parse --verify 'HEAD^{commit}'", package, StringComparison.Ordinal);
        Assert.Contains("-p:SourceRevisionId=$sourceCommit", package, StringComparison.Ordinal);
        Assert.Contains("SchemaVersion = 3", package, StringComparison.Ordinal);
        Assert.Contains("SourceCommit = $sourceCommit", package, StringComparison.Ordinal);
        Assert.Contains("SourceTreeClean = $sourceTreeClean", package, StringComparison.Ordinal);
        Assert.Contains("releaseRoundTrip.SourceCommit", package, StringComparison.Ordinal);
        Assert.Contains("New-FileRecord $releaseManifestOutput", package, StringComparison.Ordinal);
        Assert.Contains("Refusing to package a dirty Git worktree", package, StringComparison.Ordinal);
        Assert.Contains("Release source changed during packaging", package, StringComparison.Ordinal);
    }

    [Fact]
    public void Package_IncludesProjectAndDotNetLicenseNotices()
    {
        string package = Read("build/package.ps1");
        string notices = Read("THIRD-PARTY-NOTICES.md");

        Assert.Contains("$projectLicense = Join-Path $repo 'LICENSE'", package, StringComparison.Ordinal);
        Assert.Contains("$thirdPartyIndex = Join-Path $repo 'THIRD-PARTY-NOTICES.md'", package, StringComparison.Ordinal);
        Assert.Contains("DOTNET-LICENSE.txt", package, StringComparison.Ordinal);
        Assert.Contains("DOTNET-THIRD-PARTY-NOTICES.txt", package, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Extensions.DependencyInjection 10.0.11", notices, StringComparison.Ordinal);
        Assert.Contains("xUnit.net 2.9.3", notices, StringComparison.Ordinal);
        Assert.Contains("does not bundle FFmpeg", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void BrandIcons_ShipEveryWindowsAndTrayResolution()
    {
        int[] expectedSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        string assets = Path.Combine(RepositoryRoot(), "src", "MyCapture.App", "Assets");

        foreach (string fileName in new[]
                 {
                     "app.ico",
                     "tray-idle.ico",
                     "tray-capturing.ico",
                     "tray-busy.ico",
                     "tray-error.ico",
                 })
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(assets, fileName));
            Assert.True(bytes.Length > 6, $"{fileName} is not a complete ICO file.");
            Assert.Equal((ushort)0, BitConverter.ToUInt16(bytes, 0));
            Assert.Equal((ushort)1, BitConverter.ToUInt16(bytes, 2));
            int count = BitConverter.ToUInt16(bytes, 4);
            Assert.Equal(expectedSizes.Length, count);

            int[] actualSizes = Enumerable.Range(0, count)
                .Select(index => bytes[6 + (index * 16)] is 0
                    ? 256
                    : bytes[6 + (index * 16)])
                .ToArray();
            Assert.Equal(expectedSizes, actualSizes);

            for (int index = 0; index < count; index++)
            {
                int entry = 6 + (index * 16);
                uint length = BitConverter.ToUInt32(bytes, entry + 8);
                uint offset = BitConverter.ToUInt32(bytes, entry + 12);
                Assert.True(length > 0, $"{fileName} has an empty {actualSizes[index]} px entry.");
                Assert.True(offset + length <= bytes.Length,
                    $"{fileName} has an out-of-range {actualSizes[index]} px entry.");
            }
        }

        string package = Read("build/package.ps1");
        string installer = Read("build/installer/install.ps1");
        Assert.Contains("'tray-error.ico'", package, StringComparison.Ordinal);
        Assert.Contains("'tray-error.ico'", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagedSelfTestRunner_RequiresExitZeroAndAnExactPassFromAllSevenTests()
    {
        string runner = Read("build/run-packaged-self-tests.ps1");

        string[] switches =
        [
            "--selftest-capture",
            "--selftest-shell",
            "--selftest-advanced",
            "--selftest-settings",
            "--selftest-ocr",
            "--selftest-recording",
            "--selftest-video-editor",
        ];

        foreach (string commandLineSwitch in switches)
        {
            Assert.Contains(commandLineSwitch, runner, StringComparison.Ordinal);
        }

        Assert.Contains("$exitCode -ne 0", runner, StringComparison.Ordinal);
        Assert.Contains("[string]::Equals($resultLines[0], 'RESULT: PASS', [StringComparison]::Ordinal)", runner, StringComparison.Ordinal);
        Assert.Contains("$resultLines.Count -eq 1", runner, StringComparison.Ordinal);
        Assert.Contains("$passCount -ne $tests.Count", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionExtensions_AreAlignedWithTheNetTenServicingLine()
    {
        string packages = Read("Directory.Packages.props");

        foreach (string package in new[]
                 {
                     "Microsoft.Extensions.DependencyInjection",
                     "Microsoft.Extensions.Logging",
                     "Microsoft.Extensions.Logging.Debug",
                 })
        {
            Assert.Contains($"Include=\"{package}\" Version=\"10.0.11\"", packages, StringComparison.Ordinal);
        }

        Assert.Contains("Include=\"xunit\" Version=\"2.9.3\"", packages, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuousIntegration_UsesOnlyImmutableActionsAndLeastTokenPermissions()
    {
        string workflow = Read(".github/workflows/ci.yml");
        string dependabot = Read(".github/dependabot.yml");

        string[] actions = workflow
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.TrimStart().StartsWith("uses:", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, actions.Length);
        foreach (string action in actions)
        {
            int at = action.IndexOf('@');
            Assert.True(at >= 0, $"Action is not pinned: {action}");
            string sha = action[(at + 1)..].Split([' ', '#'], StringSplitOptions.RemoveEmptyEntries)[0];
            Assert.Equal(40, sha.Length);
            Assert.All(sha, character => Assert.True(Uri.IsHexDigit(character), $"Invalid action SHA: {sha}"));
        }

        Assert.DoesNotContain("uses: actions/checkout@v", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("uses: actions/setup-dotnet@v", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request_target", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain(": write", workflow, StringComparison.Ordinal);
        Assert.Contains("permissions:\n  contents: read", workflow.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("timeout-minutes: 25", workflow, StringComparison.Ordinal);
        Assert.Contains("persist-credentials: false", workflow, StringComparison.Ordinal);

        Assert.Contains("package-ecosystem: nuget", dependabot, StringComparison.Ordinal);
        Assert.Contains("package-ecosystem: github-actions", dependabot, StringComparison.Ordinal);
        Assert.Contains("interval: weekly", dependabot, StringComparison.Ordinal);
        Assert.Contains("version-update:semver-patch", dependabot, StringComparison.Ordinal);
    }
}
