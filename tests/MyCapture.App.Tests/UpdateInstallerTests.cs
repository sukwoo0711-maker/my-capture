using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
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
            () => Assert.Fail("Must not exit"), CancellationToken.None, fixture.Target));
    }

    [Fact]
    public async Task BusyAtFinalGateNeverStartsOrExits()
    {
        using var fixture = new PackageFixture();
        var installer = new UpdateInstaller(_ => Assert.Fail("Must not start"));
        Assert.False(await installer.InstallAsync(fixture.Package, () => false,
            () => Assert.Fail("Must not exit"), CancellationToken.None, fixture.Target));
    }

    [Fact]
    public async Task LaunchFailureKeepsApplicationRunning()
    {
        using var fixture = new PackageFixture();
        var installer = new UpdateInstaller(_ => throw new IOException("Fake launch failure"));
        await Assert.ThrowsAsync<IOException>(() => installer.InstallAsync(fixture.Package, () => true,
            () => Assert.Fail("Must not exit"), CancellationToken.None, fixture.Target));
    }

    [Fact]
    public async Task HandoffLaunchesInstallerDirectlyAndExitsOnlyAfterItStarts()
    {
        using var fixture = new PackageFixture();
        bool started = false, exited = false;
        var installer = new UpdateInstaller(info =>
        {
            Assert.False(info.UseShellExecute);
            Assert.Empty(info.Arguments);
            Assert.Empty(info.ArgumentList);
            Assert.Equal(fixture.Package.InstallerPath, info.FileName);
            Assert.Equal(fixture.Directory, info.WorkingDirectory);
            Assert.Equal(Path.Combine(fixture.Directory, "update-session.json"), info.Environment["MYCAPTURE_UPDATE_SESSION"]);
            // The integrity-checked installer stays read-locked through process creation.
            Assert.Throws<IOException>(() => File.Open(fixture.Package.InstallerPath,
                FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
            Assert.False(exited);
            started = true;
        });
        Assert.True(await installer.InstallAsync(fixture.Package, () => true, () =>
        {
            Assert.True(started);
            exited = true;
        }, CancellationToken.None, fixture.Target));
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
            () => true, () => Assert.Fail("Must not exit"), cancellation.Token, fixture.Target));
    }

    [Fact]
    public async Task SessionHasExactInstallerContractAndUtf8WithoutBom()
    {
        using var fixture = new PackageFixture();
        using var parent = Process.GetCurrentProcess();
        var installer = new UpdateInstaller(info =>
        {
            byte[] bytes = File.ReadAllBytes(info.Environment["MYCAPTURE_UPDATE_SESSION"]!);
            Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement session = document.RootElement;
            Assert.Equal(new[] { "InstallRoot", "ParentId", "ParentStartedUtc", "RestartAfterInstall", "ShowProgress",
                "SourceRoot", "TargetMode", "Token", "Version", "WaitForParent" },
                session.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal(Path.GetFileName(fixture.Directory)[^32..], session.GetProperty("Token").GetString());
            Assert.Equal("1.8.0", session.GetProperty("Version").GetString());
            Assert.Equal(fixture.Target.InstallRoot, session.GetProperty("InstallRoot").GetString());
            Assert.Equal(fixture.Target.InstallRoot, session.GetProperty("SourceRoot").GetString());
            Assert.Equal("OwnedInstall", session.GetProperty("TargetMode").GetString());
            Assert.Equal(parent.Id, session.GetProperty("ParentId").GetInt32());
            Assert.Equal(parent.StartTime.ToUniversalTime().Ticks, session.GetProperty("ParentStartedUtc").GetInt64());
            Assert.True(session.GetProperty("WaitForParent").GetBoolean());
            Assert.True(session.GetProperty("RestartAfterInstall").GetBoolean());
            Assert.True(session.GetProperty("ShowProgress").GetBoolean());
        });
        Assert.True(await installer.InstallAsync(fixture.Package, () => true, () => { }, CancellationToken.None, fixture.Target));
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "update-helper.ps1")));
    }

    [Fact]
    public async Task ExistingSessionFileIsNeverOverwritten()
    {
        using var fixture = new PackageFixture();
        string path = Path.Combine(fixture.Directory, "update-session.json");
        await File.WriteAllTextAsync(path, "keep existing session");
        var installer = new UpdateInstaller(_ => Assert.Fail("Must not start"));
        await Assert.ThrowsAsync<IOException>(() => installer.InstallAsync(fixture.Package, () => true,
            () => Assert.Fail("Must not exit"), CancellationToken.None, fixture.Target));
        Assert.Equal("keep existing session", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task PortableCannotLaunchInstaller()
    {
        using var fixture = new PackageFixture();
        var target = new UpdateTarget(fixture.Target.SourceRoot, UpdateTarget.DefaultRoot, true);
        var installer = new UpdateInstaller(_ => Assert.Fail("Must not start"));
        await Assert.ThrowsAsync<IOException>(() => installer.InstallAsync(fixture.Package, () => true,
            () => Assert.Fail("Must not exit"), CancellationToken.None, target));
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "update-session.json")));
    }

    [Theory]
    [InlineData("update-1.9.0-0123456789abcdef0123456789abcdef")]
    [InlineData("update-1.8.0-0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("update-1.8.0-invalid")]
    public async Task MalformedOrMismatchedSessionIsRejected(string sessionName)
    {
        using var fixture = new PackageFixture(sessionName);
        var installer = new UpdateInstaller(_ => Assert.Fail("Must not start"));
        await Assert.ThrowsAsync<IOException>(() => installer.InstallAsync(fixture.Package, () => true,
            () => Assert.Fail("Must not exit"), CancellationToken.None, fixture.Target));
    }

    internal sealed class PackageFixture : IDisposable
    {
        private readonly string _root = OwnedTestDirectory.Create("mycapture updater ' & tests ");
        internal string Directory { get; }
        internal VerifiedUpdatePackage Package { get; }
        internal UpdateTarget Target { get; }
        internal PackageFixture(string? sessionName = null)
        {
            string root = _root;
            Directory = Path.Combine(root, sessionName ?? UpdatePaths.SessionName(new UpdateVersion(1, 8, 0)));
            System.IO.Directory.CreateDirectory(Directory);
            string installed = Path.Combine(root, "owned install");
            System.IO.Directory.CreateDirectory(installed);
            WriteOwnedInstallation(installed);
            Target = UpdateTarget.Resolve(installed, UpdateTarget.DefaultRoot);
            byte[] data = [1, 2, 3, 4];
            string installer = Path.Combine(Directory, "MyCapture-1.8.0-win-x64-setup.exe");
            File.WriteAllBytes(installer, data);
            string hash = Convert.ToHexString(SHA256.HashData(data));
            Package = new VerifiedUpdatePackage(new UpdateVersion(1, 8, 0), installer, hash, hash, data.Length,
                Directory, "Release", "", new Uri("https://github.com/sukwoo0711-maker/my-capture/releases/tag/v1.8.0"), DateTimeOffset.UtcNow);
        }
        internal static void WriteOwnedInstallation(string root)
        {
            var files = new[] { "MyCapture.exe", "MyCapture.dll" }.Select(name =>
            {
                byte[] bytes = [1, 2, 3];
                File.WriteAllBytes(Path.Combine(root, name), bytes);
                return new { Path = name, Bytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
            }).ToArray();
            File.WriteAllText(Path.Combine(root, "install-manifest.json"),
                JsonSerializer.Serialize(new { Product = "MyCapture", Version = "1.7.0", Files = files }));
        }

        public void Dispose() { Package.Cleanup(); OwnedTestDirectory.Delete(_root); }
    }
}
