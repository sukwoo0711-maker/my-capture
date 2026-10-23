using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace MyCapture.Setup.Tests;

public sealed class UpdateRestartTests
{
    private const string Diagnostic = "--selftest-ux-review";

    [Fact]
    public void NormalRestartUsesOnlyTheInstalledExecutableAndRemovesTheSession()
    {
        string? previous = Environment.GetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION");
        try
        {
            Environment.SetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION", @"C:\Updates\update-session.json");
            var start = UpdateRestart.CreateStartInfo(@"C:\Installed App", @"C:\Updates", null);
            Assert.Equal(@"C:\Installed App\MyCapture.exe", start.FileName);
            Assert.Equal(@"C:\Installed App", start.WorkingDirectory);
            Assert.False(start.UseShellExecute); Assert.Equal("", start.Arguments);
            Assert.False(start.EnvironmentVariables.ContainsKey("MYCAPTURE_UPDATE_SESSION"));
            Assert.False(start.RedirectStandardOutput); Assert.False(start.RedirectStandardError);
        }
        finally { Environment.SetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION", previous); }
    }

    [Fact]
    public void DiagnosticSeamChangesArgumentsAndRedirectionWithoutChangingTheExecutable()
    {
        var start = UpdateRestart.CreateStartInfo(@"C:\Installed App", @"C:\Updates", Diagnostic);
        Assert.Equal(@"C:\Installed App\MyCapture.exe", start.FileName);
        Assert.Equal(@"C:\Installed App", start.WorkingDirectory);
        Assert.False(start.UseShellExecute); Assert.Equal(Diagnostic, start.Arguments);
        Assert.True(start.RedirectStandardOutput); Assert.True(start.RedirectStandardError);
        Assert.False(start.EnvironmentVariables.ContainsKey("MYCAPTURE_UPDATE_SESSION"));
    }

    [Theory]
    [InlineData("")][InlineData(" ")][InlineData("--SELFTEST-UX-REVIEW")]
    [InlineData("--selftest-ux-review --other")][InlineData("--background")]
    [InlineData("C:\\other.exe")][InlineData("--selftest-ux-review\n")]
    public void RestartSeamRejectsEveryUnallowlistedValue(string argument) =>
        SetupContractsTests.Code(21, () => UpdateRestart.CreateStartInfo(@"C:\Installed App", @"C:\Updates", argument));

    [Theory]
    [InlineData(0, false, true, true, true)]
    [InlineData(0, false, false, true, true)]
    [InlineData(0, false, true, false, false)]
    [InlineData(17, false, true, true, false)]
    [InlineData(17, false, false, true, true)]
    [InlineData(17, false, false, false, false)]
    [InlineData(0, true, true, true, false)]
    [InlineData(0, true, false, true, false)]
    [InlineData(17, true, false, true, false)]
    public void CompletionWritesTheReceiptBeforeApplyingTheRestartPolicy(int code, bool verifyOnly, bool showProgress, bool requested, bool expected)
    {
        using (var f = new Fixture())
        {
            Directory.CreateDirectory(f.Install);
            File.Copy(Path.Combine(f.Package, "MyCaptureSetup.exe"), Path.Combine(f.Install, "MyCapture.exe"));
            string receipt = Path.Combine(f.Root, "install-result.json");
            var log = new SetupLog(Path.Combine(f.Root, "completion.log"), f.Root, true, false);
            var session = new UpdaterSession { Token = "0123456789abcdef0123456789abcdef", Version = f.Manifest.Version,
                InstallRoot = f.Install, RestartAfterInstall = requested, ShowProgress = showProgress };
            int started = 0;
            var restart = new UpdateRestart(_ => { }, _ => { }, info =>
            {
                started++;
                Assert.True(File.Exists(receipt), "The receipt must be persisted before any restart.");
                var saved = Json.Read<InstallReceipt>(receipt);
                Assert.Equal(code, saved.ExitCode); Assert.Equal(session.Token, saved.Token);
                Assert.Equal(f.Install, saved.InstallRoot); Assert.Equal(log.Path, saved.LogPath);
                Assert.Equal(Path.Combine(f.Install, "MyCapture.exe"), info.FileName);
                Assert.Equal("", info.Arguments); Assert.False(info.UseShellExecute);
                return null;
            });
            UpdateCompletion.Finish(session, receipt, f.Install, log, code, verifyOnly, true, null, restart);
            Assert.Equal(expected ? 1 : 0, started);
        }
    }

    [Theory][InlineData("receipt")][InlineData("unvalidated")][InlineData("missing-executable")]
    public void UnsafeOrIncompleteCompletionCannotStartTheApp(string condition)
    {
        using (var f = new Fixture())
        {
            Directory.CreateDirectory(f.Install);
            if (condition != "missing-executable") File.Copy(Path.Combine(f.Package, "MyCaptureSetup.exe"), Path.Combine(f.Install, "MyCapture.exe"));
            string receipt = Path.Combine(f.Root, "install-result.json");
            if (condition == "receipt") File.WriteAllText(receipt, "existing receipt");
            var log = new SetupLog(Path.Combine(f.Root, "completion.log"), f.Root, true, false);
            int started = 0;
            var restart = new UpdateRestart(_ => { }, _ => { }, _ => { started++; return null; });
            UpdateCompletion.Finish(new UpdaterSession { Token = new string('a', 32), Version = f.Manifest.Version,
                InstallRoot = f.Install, RestartAfterInstall = true }, receipt, f.Install, log,
                0, false, condition != "unvalidated", null, restart);
            Assert.Equal(0, started);
            if (condition == "receipt") Assert.Equal("existing receipt", File.ReadAllText(receipt));
        }
    }
}
