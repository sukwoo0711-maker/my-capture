using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Prevents an installer or portable archive from claiming to be offline while shipping a
/// framework-dependent apphost. The release must carry CoreCLR and WPF beside MyCapture.exe.
/// </summary>
public sealed class OfflineDeploymentContractTests
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

    private static string RepositoryPath(string relativePath) =>
        Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void WinX64PublishProfile_IsExplicitlySelfContainedAndUntrimmed()
    {
        string path = RepositoryPath(
            "src/MyCapture.App/Properties/PublishProfiles/win-x64-self-contained.pubxml");
        Assert.True(File.Exists(path), $"Required publish profile is missing: {path}");

        XDocument document = XDocument.Load(path);
        string Property(string name) => document
            .Descendants()
            .Single(element => element.Name.LocalName == name)
            .Value;

        Assert.Equal("win-x64", Property("RuntimeIdentifier"));
        Assert.Equal("true", Property("SelfContained"));
        Assert.Equal("true", Property("PublishSelfContained"));
        Assert.Equal("true", Property("UseAppHost"));
        Assert.Equal("false", Property("PublishSingleFile"));
        Assert.Equal("false", Property("PublishTrimmed"));
        Assert.Equal("true", Property("PublishReadyToRun"));
    }

    [Fact]
    public void PackageScript_UsesTheProfileAndVerifiesTheEmbeddedRuntime()
    {
        string package = File.ReadAllText(RepositoryPath("build/package.ps1"));

        Assert.Contains("-p:PublishProfile=win-x64-self-contained", package, StringComparison.Ordinal);
        Assert.Contains("Assert-SelfContainedPublish $publish", package, StringComparison.Ordinal);
        Assert.Contains("coreclr.dll", package, StringComparison.Ordinal);
        Assert.Contains("hostfxr.dll", package, StringComparison.Ordinal);
        Assert.Contains("hostpolicy.dll", package, StringComparison.Ordinal);
        Assert.Contains("includedFrameworks", package, StringComparison.Ordinal);
        Assert.Contains("RequiresPreinstalledDotNet = $false", package, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildScripts_AreReadableByWindowsPowerShell51OnEveryCodePage()
    {
        string buildDirectory = RepositoryPath("build");
        string[] unsafeScripts = Directory
            .EnumerateFiles(buildDirectory, "*.ps1", SearchOption.AllDirectories)
            .Where(path =>
            {
                byte[] bytes = File.ReadAllBytes(path);
                bool hasUtf8Bom = bytes.Length >= 3
                    && bytes[0] == 0xEF
                    && bytes[1] == 0xBB
                    && bytes[2] == 0xBF;
                return !hasUtf8Bom && bytes.Any(value => value > 0x7F);
            })
            .Select(path => Path.GetRelativePath(RepositoryRoot(), path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unsafeScripts.Length == 0,
            "Windows PowerShell 5.1 decodes UTF-8 without a BOM through the host code page. "
            + "Keep build scripts ASCII-only or add a UTF-8 BOM: "
            + string.Join(", ", unsafeScripts));
    }
}
