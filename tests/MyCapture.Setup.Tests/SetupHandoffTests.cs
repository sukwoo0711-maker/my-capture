using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Xunit;

namespace MyCapture.Setup.Tests;

public sealed class SetupHandoffTests
{
    [Fact] public void InteractiveUninstallFinishesRealCleanupBeforeTheResultWindowCloses()
    {
        using (var fixture = new Fixture())
        {
            // Prepare a verified uninstall fixture independently of the installer's running-app guard.
            Directory.CreateDirectory(fixture.Temp);
            Payload.Extract(Path.Combine(fixture.Package, "payload.zip"), fixture.Manifest, fixture.Install);
            var records = fixture.Manifest.Files.ToList();
            foreach (var pair in new[] { new[] { "MyCaptureSetup.exe", "MyCaptureUninstall.exe" },
                new[] { "MyCaptureSetup.exe.config", "MyCaptureUninstall.exe.config" }, new[] { "SETUP-HELP.txt", "SETUP-HELP.txt" } })
            {
                string target = Path.Combine(fixture.Install, pair[1]);
                File.Copy(Path.Combine(fixture.Package, pair[0]), target);
                records.Add(FileRecord.Create(target, pair[1]));
            }
            var manifest = fixture.Manifest;
            manifest.Files = records.ToArray(); manifest.FileCount = records.Count; manifest.TotalExpandedBytes = records.Sum(file => file.Bytes);
            manifest.ShellIntegration = false; manifest.TestRegistryKey = fixture.Key; manifest.TestStartMenu = fixture.Menu;
            manifest.InstallationId = Guid.NewGuid().ToString("N"); manifest.ValidateInventory(true);
            Json.Write(Path.Combine(fixture.Install, "install-manifest.json"), manifest);
            string data = Path.Combine(fixture.Root, "data"); Directory.CreateDirectory(data);
            string sentinel = Path.Combine(data, "capture.txt"); File.WriteAllText(sentinel, "preserved capture");
            string log = Path.Combine(fixture.Root, "interactive-uninstall.log");
            var start = new ProcessStartInfo(Path.Combine(fixture.Install, "MyCaptureUninstall.exe"), "/uninstall --log " + Program.Quote(log))
            { UseShellExecute = false, CreateNoWindow = true };
            start.EnvironmentVariables["TEMP"] = fixture.Temp; start.EnvironmentVariables["TMP"] = fixture.Temp;
            start.EnvironmentVariables.Remove("MYCAPTURE_UPDATE_SESSION");
            using (var parent = Process.Start(start))
            {
                Assert.True(parent.WaitForExit(30000), "The original uninstaller must exit after handing off its window.");
                Assert.Equal(0, parent.ExitCode);
            }
            string text = Fixture.ReadShared(log);
            var match = Regex.Match(text, @"Deferred cleanup worker started \(PID (\d+)\)");
            Assert.True(match.Success, text);
            using (var worker = Process.GetProcessById(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)))
            {
                Assert.NotEqual(IntPtr.Zero, worker.Handle);
                string workerPath = worker.MainModule.FileName;
                string workerDirectory = Path.GetDirectoryName(workerPath)!;
                Assert.True(PathSafety.Equal(Path.GetDirectoryName(workerDirectory), fixture.Temp));
                Assert.True(FileRemoval.IsWorkerName(Path.GetFileName(workerDirectory)));
                Assert.Equal("MyCaptureUninstall.exe", Path.GetFileName(workerPath));
                try
                {
                    DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                    while (!text.Contains("Cleanup worker folder retained") && DateTime.UtcNow < deadline)
                    { Thread.Sleep(50); text = Fixture.ReadShared(log); }
                    Assert.Contains("MyCapture removal completed. Captures and settings under APPDATA were not accessed.", text);
                    Assert.DoesNotContain("Deferred uninstall cleanup failed", text);
                    Assert.False(Directory.Exists(fixture.Install));
                    Assert.Equal("preserved capture", File.ReadAllText(sentinel));
                    Assert.False(worker.HasExited, "The completion page must wait for the user after cleanup.");
                    deadline = DateTime.UtcNow.AddSeconds(5);
                    while (!worker.HasExited && DateTime.UtcNow < deadline)
                    { worker.CloseMainWindow(); worker.WaitForExit(100); }
                    Assert.True(worker.HasExited, "The completed result window must close.");
                    Assert.Equal(0, worker.ExitCode);
                }
                finally
                {
                    if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(5000); }
                }
            }
        }
    }
}
