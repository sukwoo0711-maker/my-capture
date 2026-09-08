using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using MyCapture.App.Updates;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class UpdateTargetTests
{
    [Fact]
    public async Task OwnedCustomInstallationTargetsItsExactDirectoryAndDetectsTampering()
    {
        using var fixture = new OwnedFixture();
        var target = UpdateTarget.Resolve(fixture.Root, UpdateTarget.DefaultRoot);
        Assert.False(target.IsPortableMigration);
        Assert.Equal(fixture.Root, target.InstallRoot);
        await target.ValidateAsync(CancellationToken.None);
        File.AppendAllText(Path.Combine(fixture.Root, "MyCapture.dll"), "tamper");
        await Assert.ThrowsAsync<IOException>(() => target.ValidateAsync(CancellationToken.None));
    }

    [Fact]
    public void PortableSourceSelectsDefaultDestinationAndRetainsItsOwnFiles()
    {
        using var fixture = new OwnedFixture();
        File.Delete(Path.Combine(fixture.Root, "install-manifest.json"));
        var target = UpdateTarget.Resolve(fixture.Root, UpdateTarget.DefaultRoot);
        Assert.True(target.IsPortableMigration);
        Assert.Equal(UpdateTarget.DefaultRoot, target.InstallRoot);
        Assert.Equal(fixture.Root, target.SourceRoot);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "MyCapture.exe")));
        Assert.Contains(UpdateTarget.DefaultRoot, UpdateStrings.TargetDescription(target), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PortableModeCannotSelectAnArbitraryDestination()
    {
        using var fixture = new OwnedFixture();
        var target = new UpdateTarget(fixture.Root, fixture.Root, true);
        await Assert.ThrowsAsync<IOException>(() => target.ValidateAsync(CancellationToken.None));
    }

    [Fact]
    public void SystemUserAndDriveRootsAreRejected()
    {
        foreach (string root in new[] { Path.GetPathRoot(Path.GetTempPath())!,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "MyCapture") })
            Assert.Throws<IOException>(() => UpdateTarget.AssertSafeRoot(root));
    }

    [Fact]
    public async Task HandoffSerializesExactOwnedSourceAndDestination()
    {
        using var package = new UpdateInstallerTests.PackageFixture();
        using var source = new OwnedFixture();
        var target = UpdateTarget.Resolve(source.Root, UpdateTarget.DefaultRoot);
        var installer = new UpdateInstaller(info =>
        {
            using var json = JsonDocument.Parse(File.ReadAllText(info.ArgumentList[8]));
            Assert.Equal(source.Root, json.RootElement.GetProperty("SourceRoot").GetString());
            Assert.Equal(source.Root, json.RootElement.GetProperty("InstallRoot").GetString());
            Assert.Equal("OwnedInstall", json.RootElement.GetProperty("TargetMode").GetString());
        });
        Assert.True(await installer.InstallAsync(package.Package, () => true, () => { }, CancellationToken.None, target));
    }

    private sealed class OwnedFixture : IDisposable
    {
        internal string Root { get; } = OwnedTestDirectory.Create("mycapture-owned-");
        internal OwnedFixture()
        {
            var files = new[] { "MyCapture.exe", "MyCapture.dll" }.Select(name =>
            {
                byte[] bytes = [1, 2, 3];
                File.WriteAllBytes(Path.Combine(Root, name), bytes);
                return new { Path = name, Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
            }).ToArray();
            File.WriteAllText(Path.Combine(Root, "install-manifest.json"), JsonSerializer.Serialize(new { Product = "MyCapture", Version = "1.8.0", Files = files }));
        }
        public void Dispose()
        {
            foreach (string file in new[] { "MyCapture.exe", "MyCapture.dll", "install-manifest.json" }) File.Delete(Path.Combine(Root, file));
            OwnedTestDirectory.Delete(Root);
        }
    }
}
