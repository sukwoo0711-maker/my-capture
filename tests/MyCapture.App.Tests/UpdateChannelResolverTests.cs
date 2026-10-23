using System.IO;
using System.Text.Json;
using MyCapture.App.Updates;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class UpdateChannelResolverTests
{
    [Fact]
    public void PackagedBuildAlwaysUsesStore() =>
        Assert.Equal(UpdateChannel.Store, UpdateChannelResolver.Resolve(true, "unused"));

    [Fact]
    public void OwnedInstallUsesGitHubInAppInstaller()
    {
        using var fixture = new UpdateInstallerTests.PackageFixture();
        Assert.Equal(UpdateChannel.GitHubInstall, UpdateChannelResolver.Resolve(false, fixture.Target.SourceRoot));
    }

    [Fact]
    public void PublishedSelfContainedCopyUsesPortableReleasePage()
    {
        string root = CreatePublishedPortable();
        Assert.Equal(UpdateChannel.Portable, UpdateChannelResolver.Resolve(false, root));
    }

    [Fact]
    public void CopiedOrdinaryBuildDoesNotEnableNetworkChecks()
    {
        string root = Directory.CreateTempSubdirectory("mycapture-update-dev-").FullName;
        File.WriteAllBytes(Path.Combine(root, "MyCapture.exe"), [1]);
        File.WriteAllBytes(Path.Combine(root, "MyCapture.dll"), [1]);
        File.WriteAllText(Path.Combine(root, "MyCapture.runtimeconfig.json"),
            """{"runtimeOptions":{"frameworks":[{"name":"Microsoft.NETCore.App"}]}}""");
        Assert.Equal(UpdateChannel.None, UpdateChannelResolver.Resolve(false, root));
    }

    [Theory]
    [InlineData("Debug")]
    [InlineData("Release")]
    [InlineData("debug")]
    public void BuildOutputIsDevelopmentEvenWithPublishedFiles(string configuration)
    {
        string root = Directory.CreateTempSubdirectory("mycapture-update-build-").FullName;
        string build = Path.Combine(root, "bin", configuration, "net10.0-windows", "win-x64");
        Directory.CreateDirectory(build);
        CreatePublishedPortable(build);
        Assert.Equal(UpdateChannel.None, UpdateChannelResolver.Resolve(false, build));
    }

    [Fact]
    public void InvalidOwnershipDoesNotBecomePortableInstall()
    {
        string root = CreatePublishedPortable();
        File.WriteAllText(Path.Combine(root, "install-manifest.json"), "{}");
        Assert.Equal(UpdateChannel.None, UpdateChannelResolver.Resolve(false, root));
    }

    [Fact]
    public void FrameworkDependentOrMalformedRuntimeIsDevelopment()
    {
        string root = CreatePublishedPortable();
        string config = Path.Combine(root, "MyCapture.runtimeconfig.json");
        File.WriteAllText(config, """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App"}}}""");
        Assert.Equal(UpdateChannel.None, UpdateChannelResolver.Resolve(false, root));
        File.WriteAllText(config, "not json");
        Assert.Equal(UpdateChannel.None, UpdateChannelResolver.Resolve(false, root));
    }

    private static string CreatePublishedPortable(string? root = null)
    {
        root ??= Directory.CreateTempSubdirectory("mycapture-update-portable-").FullName;
        foreach (string name in new[] { "MyCapture.exe", "MyCapture.dll", "coreclr.dll", "hostfxr.dll",
            "hostpolicy.dll", "System.Private.CoreLib.dll", "PresentationCore.dll", "PresentationFramework.dll" })
            File.WriteAllBytes(Path.Combine(root, name), [1]);
        File.WriteAllText(Path.Combine(root, "MyCapture.runtimeconfig.json"), JsonSerializer.Serialize(new
        {
            runtimeOptions = new { includedFrameworks = new[] {
                new { name = "Microsoft.NETCore.App", version = "10.0.0" },
                new { name = "Microsoft.WindowsDesktop.App", version = "10.0.0" } } },
        }));
        File.WriteAllText(Path.Combine(root, "MyCapture.deps.json"),
            """{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/win-x64"}}""");
        return root;
    }
}
