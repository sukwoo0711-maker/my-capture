using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace MyCapture.Setup.Tests;

public sealed class UpdateSessionTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void SessionReadsReleasedAndInAppContracts(bool modern)
    {
        using (var f = new Fixture())
        {
            string path = Path.Combine(f.Root, "session.json");
            string extras = modern ? ",\"ParentStartedUtc\":638968608000000000,\"WaitForParent\":true,\"RestartAfterInstall\":true,\"ShowProgress\":true" : "";
            File.WriteAllText(path, "{\"Token\":\"0123456789abcdef0123456789abcdef\",\"Version\":\"4.5.2\",\"InstallRoot\":\"C:\\\\Owned\",\"SourceRoot\":\"C:\\\\Owned\",\"TargetMode\":\"OwnedInstall\",\"ParentId\":123" + extras + "}", new UTF8Encoding(false));
            var value = Json.Read<UpdaterSession>(path);
            Assert.Equal("0123456789abcdef0123456789abcdef", value.Token);
            Assert.Equal("4.5.2", value.Version); Assert.Equal(@"C:\Owned", value.InstallRoot);
            Assert.Equal(value.InstallRoot, value.SourceRoot); Assert.Equal("OwnedInstall", value.TargetMode);
            Assert.Equal(123, value.ParentId);
            Assert.Equal(modern ? 638968608000000000L : 0, value.ParentStartedUtc);
            Assert.Equal(modern, value.WaitForParent); Assert.Equal(modern, value.RestartAfterInstall); Assert.Equal(modern, value.ShowProgress);
        }
    }

    [Fact]
    public void MatchingLiveParentIsWaitedUntilItActuallyExits()
    {
        using (var child = StartShortChild())
        {
            var session = SessionFor(child);
            Assert.False(child.HasExited);
            var log = new List<string>();
            UpdateParent.Wait(session, log.Add, 10000);
            Assert.True(child.HasExited); Assert.Equal(0, child.ExitCode);
            Assert.NotEmpty(log);
        }
    }

    [Fact]
    public void ReusedParentIdDoesNotWaitForTheUnrelatedLiveProcess()
    {
        using (var child = StartShortChild())
        {
            try
            {
                var session = SessionFor(child); session.ParentStartedUtc--;
                UpdateParent.Wait(session, _ => { }, 20);
                Assert.False(child.HasExited);
            }
            finally { Assert.True(child.WaitForExit(10000)); }
        }
    }

    [Fact]
    public void MatchingParentTimeoutReturns15WithoutTerminatingIt()
    {
        using (var child = StartShortChild())
        {
            try
            {
                var session = SessionFor(child); var log = new List<string>();
                var elapsed = Stopwatch.StartNew();
                var error = Assert.Throws<SetupException>(() => UpdateParent.Wait(session, log.Add, 30));
                Assert.Equal(15, error.Code); Assert.Contains("30", error.Message); Assert.Contains("parent", error.Message.ToLowerInvariant());
                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2)); Assert.False(child.HasExited);
            }
            finally { Assert.True(child.WaitForExit(10000)); }
        }
    }

    [Theory][InlineData(0L)][InlineData(-1L)]
    public void WaitRequiresTheRecordedStartTime(long ticks)
    {
        SetupContractsTests.Code(14, () => UpdateParent.Wait(new UpdaterSession
        { ParentId = int.MaxValue, ParentStartedUtc = ticks, WaitForParent = true }, _ => { }, 20));
    }

    [Fact]
    public void ReleasedSessionStillRefusesALiveParentWithoutWaiting()
    {
        using (var current = Process.GetCurrentProcess())
            SetupContractsTests.Code(15, () => UpdateParent.Wait(new UpdaterSession { ParentId = current.Id }, _ => { }, 20));
        UpdateParent.Wait(new UpdaterSession { ParentId = int.MaxValue }, _ => { }, 20);
    }

    [Theory][InlineData("--manifest")][InlineData("--payload")]
    public void ProgramRetainsMissingPackageExit14ForUpdateSessions(string missing)
    {
        using (var f = new Fixture())
        {
            string? previous = Environment.GetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION");
            try
            {
                Environment.SetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION", Path.Combine(f.Root, "unvalidated-session.json"));
                Assert.Equal(14, Program.Run(new[] { "/quiet", missing, Path.Combine(f.Root, "missing-file"),
                    "--install-root", f.Install, "--no-shell-integration", "--log", Path.Combine(f.Root, "missing-package.log") },
                    directoryOverride: f.Package));
            }
            finally { Environment.SetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION", previous); }
        }
    }

    [Fact]
    public void ValidatedModernSessionCanWaitAndVerifyWithoutInstallingOrRestarting()
    {
        using (var f = new Fixture())
        using (var child = StartShortChild())
        {
            SeedOwnedInstall(f);
            string path = f.Session(child.Id);
            var session = Json.Read<UpdaterSession>(path);
            session.Token = Path.GetFileName(Path.GetDirectoryName(path)!).Substring(Path.GetFileName(Path.GetDirectoryName(path)!).Length - 32);
            session.ParentStartedUtc = child.StartTime.ToUniversalTime().Ticks;
            session.WaitForParent = session.RestartAfterInstall = session.ShowProgress = true;
            Json.Write(path, session);
            string marker = Path.Combine(f.Install, "install-manifest.json"); string hash = FileRecord.Hash(marker);
            string sentinel = Path.Combine(f.Install, "personal.txt"); File.WriteAllText(sentinel, "keep this file");
            string? previous = Environment.GetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION");
            try
            {
                Environment.SetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION", path);
                var options = Options.Parse(new[] { "--install-root", f.Install, "--no-shell-integration", "--verify-only" }, f.Package);
                var log = new SetupLog(Path.Combine(f.Root, "verify-update.log"), f.Root, true, false);
                var installer = new Installer(options, f.Package, log);
                Assert.NotNull(installer.PrepareUpdate());
                Assert.False(SetupInteraction.ShowUpdateProgress(options, session));
                Assert.Equal(0, installer.Run());
                Assert.True(child.HasExited); Assert.Equal(hash, FileRecord.Hash(marker));
                Assert.Equal("keep this file", File.ReadAllText(sentinel));
                Assert.False(installer.CanOpenUpdatedApplication);
                string text = File.ReadAllText(log.Path!);
                Assert.Contains("VerifyOnly completed", text); Assert.DoesNotContain("Restarted MyCapture", text);
            }
            finally { Environment.SetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION", previous); }
        }
    }

    [Theory][InlineData("ticks", 14)][InlineData("token", 14)][InlineData("source", 12)][InlineData("seam", 21)]
    public void InvalidModernSessionRemainsUntrustedAndCannotOfferRecovery(string fault, int code)
    {
        using (var f = new Fixture())
        {
            SeedOwnedInstall(f);
            string path = f.Session();
            var session = Json.Read<UpdaterSession>(path);
            session.Token = Path.GetFileName(Path.GetDirectoryName(path)!).Substring(Path.GetFileName(Path.GetDirectoryName(path)!).Length - 32);
            session.ParentStartedUtc = 1; session.WaitForParent = session.RestartAfterInstall = session.ShowProgress = true;
            if (fault == "ticks") session.ParentStartedUtc = 0;
            if (fault == "token") session.Token = new string('a', 32);
            if (fault == "source") session.SourceRoot = Path.Combine(f.Root, "unowned");
            Json.Write(path, session);
            string? previous = Environment.GetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION");
            string? oldSeam = Environment.GetEnvironmentVariable(UpdateRestart.TestArgumentsVariable);
            try
            {
                Environment.SetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION", path);
                Environment.SetEnvironmentVariable(UpdateRestart.TestArgumentsVariable, fault == "seam" ? "--background" : null);
                var options = Options.Parse(new[] { "--install-root", f.Install, "--no-shell-integration", "--verify-only" }, f.Package);
                var log = new SetupLog(Path.Combine(f.Root, "invalid-update.log"), f.Root, true, false);
                var installer = new Installer(options, f.Package, log);
                Assert.Null(installer.PrepareUpdate()); Assert.False(installer.CanOpenUpdatedApplication);
                Assert.Equal(code, installer.Run());
                Assert.DoesNotContain("Restarted MyCapture", File.ReadAllText(log.Path!));
            }
            finally
            {
                Environment.SetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION", previous);
                Environment.SetEnvironmentVariable(UpdateRestart.TestArgumentsVariable, oldSeam);
            }
        }
    }

    private static void SeedOwnedInstall(Fixture f)
    {
        Payload.Extract(Path.Combine(f.Package, "payload.zip"), f.Manifest, f.Install);
        var manifest = Json.Read<PackageManifest>(Path.Combine(f.Package, "installer-manifest.json"));
        var files = manifest.Files.ToList();
        foreach (var pair in new[] { new[] { "MyCaptureSetup.exe", "MyCaptureUninstall.exe" },
            new[] { "MyCaptureSetup.exe.config", "MyCaptureUninstall.exe.config" }, new[] { "SETUP-HELP.txt", "SETUP-HELP.txt" } })
        {
            string destination = Path.Combine(f.Install, pair[1]); File.Copy(Path.Combine(f.Package, pair[0]), destination);
            files.Add(FileRecord.Create(destination, pair[1]));
        }
        manifest.Files = files.ToArray(); manifest.FileCount = files.Count; manifest.TotalExpandedBytes = files.Sum(file => file.Bytes);
        manifest.ShellIntegration = false; manifest.InstallationId = Guid.NewGuid().ToString("N");
        Json.Write(Path.Combine(f.Install, "install-manifest.json"), manifest);
    }

    private static UpdaterSession SessionFor(Process child) => new UpdaterSession
    { ParentId = child.Id, ParentStartedUtc = child.StartTime.ToUniversalTime().Ticks, WaitForParent = true };

    private static Process StartShortChild()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 3 127.0.0.1")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        return Process.Start(start)!;
    }
}
