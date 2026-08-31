using System;
using System.IO;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Keeps the 1.0.0 GA version and its Windows binary version aligned across MSBuild, packaging
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
    public void BuildDefaults_AreTheStableOnePointZeroBinaryVersion()
    {
        string props = Read("Directory.Build.props");
        string manifest = Read("src/MyCapture.App/app.manifest");

        Assert.Contains("<Version>1.0.0</Version>", props, StringComparison.Ordinal);
        Assert.Contains("<FileVersion>1.0.0.0</FileVersion>", props, StringComparison.Ordinal);
        Assert.Contains("<AssemblyVersion>1.0.0.0</AssemblyVersion>", props, StringComparison.Ordinal);
        Assert.Contains("version=\"1.0.0.0\"", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageAndInstaller_DefaultToTheGaVersion_AndStillAcceptAPrerelease()
    {
        string package = Read("build/package.ps1");
        string installer = Read("build/installer/install.ps1");
        string hostile = Read("build/tests/installer-hostile-tests.ps1");

        // The shipping default is the GA version. A release candidate must never be the default
        // again, otherwise an operator who forgets -Version publishes a prerelease by accident.
        Assert.Contains("[string]$Version = '1.0.0'", package, StringComparison.Ordinal);
        Assert.Contains("[string]$Version = '1.0.0',", hostile, StringComparison.Ordinal);
        Assert.DoesNotContain("$Version = '1.0.0-rc.1'", package, StringComparison.Ordinal);
        Assert.DoesNotContain("$Version = '1.0.0-rc.1'", hostile, StringComparison.Ordinal);

        // A prerelease must still parse, so a future RC needs no script edit. The Windows binary
        // version stays numeric because Win32 version resources cannot carry a prerelease label.
        Assert.Contains("$semVerPattern", package, StringComparison.Ordinal);
        Assert.Contains("$baseVersion", package, StringComparison.Ordinal);
        Assert.Contains("$binaryVersion = \"$baseVersion.0\"", package, StringComparison.Ordinal);
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
}
