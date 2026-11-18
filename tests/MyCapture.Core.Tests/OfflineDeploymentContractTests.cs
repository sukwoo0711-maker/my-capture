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

    [Theory]
    [InlineData("x64")]
    [InlineData("x86")]
    [InlineData("arm64")]
    public void PublishProfile_IsExplicitlySelfContainedAndUntrimmed(string architecture)
    {
        string path = RepositoryPath(
            $"src/MyCapture.App/Properties/PublishProfiles/win-{architecture}-self-contained.pubxml");
        Assert.True(File.Exists(path), $"Required publish profile is missing: {path}");

        XDocument document = XDocument.Load(path);
        string Property(string name) => document
            .Descendants()
            .Single(element => element.Name.LocalName == name)
            .Value;

        Assert.Equal("win-" + architecture, Property("RuntimeIdentifier"));
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

        Assert.Contains("-p:PublishProfile=$runtime-self-contained", package, StringComparison.Ordinal);
        Assert.Contains("Assert-SelfContainedPublish $publish", package, StringComparison.Ordinal);
        Assert.Contains("coreclr.dll", package, StringComparison.Ordinal);
        Assert.Contains("hostfxr.dll", package, StringComparison.Ordinal);
        Assert.Contains("hostpolicy.dll", package, StringComparison.Ordinal);
        Assert.Contains("includedFrameworks", package, StringComparison.Ordinal);
        Assert.Contains("RequiresPreinstalledDotNet = $false", package, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageScript_ReadsTheArchitectureOfEveryPublishedImage()
    {
        // An arm64 package is cross-built on an x64 PC that cannot start it, so the package script
        // judges the files themselves before it builds the ZIP and the installer.
        string package = File.ReadAllText(RepositoryPath("build/package.ps1"));
        string gate = File.ReadAllText(RepositoryPath("build/publish-architecture.ps1"));

        Assert.Contains("[ValidateSet('x64', 'x86', 'arm64')][string]$Architecture = 'x64'", package, StringComparison.Ordinal);
        Assert.Contains(". (Join-Path $PSScriptRoot 'publish-architecture.ps1')", package, StringComparison.Ordinal);
        const string check = "Assert-PublishArchitecture -PublishRoot $publish -Architecture $Architecture";
        Assert.Contains(check, package, StringComparison.Ordinal);
        Assert.True(package.IndexOf(check, StringComparison.Ordinal)
            < package.IndexOf("New-InventoryZip $publish $portable $inventory", StringComparison.Ordinal));
        Assert.Contains("'x64' = 0x8664; 'x86' = 0x014C; 'arm64' = 0xAA64", gate, StringComparison.Ordinal);
        Assert.Contains("function Assert-PublishArchitecture", gate, StringComparison.Ordinal);
        Assert.Contains("MyCapture.exe is not a native win-$Architecture application host", gate, StringComparison.Ordinal);
    }

    [Fact]
    public void Arm64Package_CarriesTheX64Setup()
    {
        // Setup runs on the in-box .NET Framework 4.8; a native ARM64 process needs 4.8.1.
        string package = File.ReadAllText(RepositoryPath("build/package.ps1"));
        Assert.Contains("$setupMachine = if ($Architecture -eq 'x86') { 0x014C } else { 0x8664 }", package, StringComparison.Ordinal);
        Assert.Contains("$setupKind.Machine -ne $setupMachine", package, StringComparison.Ordinal);
        // The self-extractor follows the packaging PC; an ARM64 one must never reach an x64 or x86 package.
        Assert.Contains(
            "$extractorMachines = if ($Architecture -eq 'x86') { @(0x014C) } elseif ($Architecture -eq 'x64') { @(0x8664) } else { @(0x8664, 0xAA64) }",
            package, StringComparison.Ordinal);
        Assert.Contains("$extractorMachines -notcontains $extractorKind.Machine", package, StringComparison.Ordinal);

        foreach (string project in new[] { "src/MyCapture.Setup/MyCapture.Setup.csproj", "tests/MyCapture.Setup.Tests/MyCapture.Setup.Tests.csproj" })
        {
            XDocument document = XDocument.Load(RepositoryPath(project));
            XElement group = Assert.Single(document.Root!.Elements("PropertyGroup"),
                element => element.Attribute("Condition")?.Value == "'$(Platform)' == 'arm64'");
            Assert.Equal("x64", Assert.Single(group.Elements("PlatformTarget")).Value);
            Assert.Equal("net48", document.Descendants("TargetFramework").Single().Value);
        }

        XDocument props = XDocument.Load(RepositoryPath("Directory.Build.props"));
        Assert.Equal("x64;x86;arm64", props.Descendants("Platforms").Single().Value);
        Assert.Contains(props.Descendants("PlatformTarget"), element => element.Value == "arm64"
            && element.Attribute("Condition")?.Value.Contains("'$(RuntimeIdentifier)' == 'win-arm64'", StringComparison.Ordinal) == true);
        // x64 remains the default on every build host, an ARM64 PC included.
        Assert.Equal("x64", props.Descendants("PlatformTarget").Last().Value);
        XDocument app = XDocument.Load(RepositoryPath("src/MyCapture.App/MyCapture.App.csproj"));
        Assert.Equal("win-x64;win-x86;win-arm64", app.Descendants("RuntimeIdentifiers").Single().Value);
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
