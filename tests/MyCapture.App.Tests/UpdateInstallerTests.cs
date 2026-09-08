using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using MyCapture.App.Updates;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class UpdateInstallerTests
{
    [Fact]
    public async Task TamperedDownloadNeverStartsOrExits()
    {
        using var fixture = new PackageFixture();
        await File.WriteAllTextAsync(fixture.Package.InstallerPath, "changed");
        var installer = new UpdateInstaller(_ => Assert.Fail("Must not start"));
        await Assert.ThrowsAsync<IOException>(() => installer.InstallAsync(fixture.Package, () => true,
            () => Assert.Fail("Must not exit"), CancellationToken.None));
    }

    [Fact]
    public async Task BusyAtFinalGateNeverStartsOrExits()
    {
        using var fixture = new PackageFixture();
        var installer = new UpdateInstaller(_ => Assert.Fail("Must not start"));
        Assert.False(await installer.InstallAsync(fixture.Package, () => false,
            () => Assert.Fail("Must not exit"), CancellationToken.None));
    }

    [Fact]
    public async Task LaunchFailureKeepsApplicationRunning()
    {
        using var fixture = new PackageFixture();
        var installer = new UpdateInstaller(_ => throw new IOException("Fake launch failure"));
        await Assert.ThrowsAsync<IOException>(() => installer.InstallAsync(fixture.Package, () => true,
            () => Assert.Fail("Must not exit"), CancellationToken.None));
    }

    [Fact]
    public async Task HandoffUsesLiteralArgumentsAndExitsOnlyAfterHelperStarts()
    {
        using var fixture = new PackageFixture();
        bool started = false, exited = false;
        var installer = new UpdateInstaller(info =>
        {
            Assert.False(info.UseShellExecute);
            Assert.True(info.CreateNoWindow);
            Assert.Equal(ProcessWindowStyle.Hidden, info.WindowStyle);
            Assert.Empty(info.Arguments);
            Assert.Equal(Path.Combine(fixture.Directory, "update-helper.ps1"), info.ArgumentList[6]);
            Assert.Equal(Path.Combine(fixture.Directory, "update-session.json"), info.ArgumentList[8]);
            Assert.False(exited);
            started = true;
        });
        Assert.True(await installer.InstallAsync(fixture.Package, () => true, () =>
        {
            Assert.True(started);
            exited = true;
        }, CancellationToken.None));
        Assert.True(exited);
    }

    [Fact]
    public async Task CancelledHandoffNeverStartsOrExits()
    {
        using var fixture = new PackageFixture();
        var installer = new UpdateInstaller(_ => Assert.Fail("Must not start"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(fixture.Package,
            () => true, () => Assert.Fail("Must not exit"), cancellation.Token));
    }

    internal sealed class PackageFixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "mycapture updater ' & tests " + Guid.NewGuid().ToString("N"));
        internal VerifiedUpdatePackage Package { get; }
        internal PackageFixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            byte[] data = [1, 2, 3, 4];
            string installer = Path.Combine(Directory, "MyCapture-1.8.0-win-x64-setup.exe");
            File.WriteAllBytes(installer, data);
            string hash = Convert.ToHexString(SHA256.HashData(data));
            Package = new VerifiedUpdatePackage(new UpdateVersion(1, 8, 0), installer, hash, hash, data.Length,
                Directory, "Release", "", new Uri("https://github.com/sukwoo0711-maker/my-capture/releases/tag/v1.8.0"), DateTimeOffset.UtcNow);
        }
        public void Dispose() { Package.Cleanup(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, false); }
    }
}
