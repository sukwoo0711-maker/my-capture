using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Xunit;

namespace MyCapture.Setup.Tests;

public sealed class Fixture : IDisposable
{
    public static string Repository
    {
        get
        {
            for (var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "MyCapture.slnx"))) return dir.FullName;
            string sourceRoot = RepositoryFromSource();
            if (File.Exists(Path.Combine(sourceRoot, "MyCapture.slnx"))) return sourceRoot;
            throw new InvalidOperationException("Repository not found.");
        }
    }
    private static string RepositoryFromSource([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../.."));
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "MyCapture-SetupTests-" + Guid.NewGuid().ToString("N"));
    public string Package => Path.Combine(Root, "package");
    // Settable so a test can install below a junction; the default has Unicode and shell metacharacters.
    public string Install { get; set; }
    public string Key { get; } = @"Software\MyCapture.SetupTests\" + Guid.NewGuid().ToString("N");
    // Under Temp as well: the uninstaller runs with TEMP=Temp and test targets must stay under TEMP.
    public string Menu => Path.Combine(Temp, "Start Menu");
    public PackageManifest Manifest { get; private set; } = new PackageManifest();
    private readonly string? previousData = Environment.GetEnvironmentVariable("MYCAPTURE_DATA_ROOT");
    private readonly bool testKeyParentExisted = TestKeyParentExists();
    public Fixture()
    {
        Install = Path.Combine(Root, "installed 한글 & (test)");
        Directory.CreateDirectory(Root); Directory.CreateDirectory(Package);
        Environment.SetEnvironmentVariable("MYCAPTURE_DATA_ROOT", Path.Combine(Root, "data"));
        // Use this test run's actual setup build, including prerelease/isolated outputs.
        string source = AppDomain.CurrentDomain.BaseDirectory;
        foreach (string name in PackageManifest.BootstrapNames)
            File.Copy(name == "SETUP-HELP.txt" ? Path.Combine(Repository, "build", "installer", name) : Path.Combine(source, name), Path.Combine(Package, name));
        string publish = Path.Combine(Root, "publish"); Directory.CreateDirectory(Path.Combine(publish, "Assets"));
        File.Copy(Path.Combine(source, "MyCaptureSetup.exe"), Path.Combine(publish, "MyCapture.exe"));
        File.Copy(Path.Combine(source, "MyCaptureSetup.exe"), Path.Combine(publish, "MyCapture.dll"));
        foreach (string name in new[] { "tray-idle.ico", "tray-capturing.ico", "tray-busy.ico", "tray-error.ico" }) File.WriteAllBytes(Path.Combine(publish, "Assets", name), new byte[] { 0, 1, 2 });
        var files = Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories).Select(p => FileRecord.Create(p, p.Substring(publish.Length + 1).Replace('\\', '/'))).ToArray();
        using (var zip = ZipFile.Open(Path.Combine(Package, "payload.zip"), ZipArchiveMode.Create))
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Path);
                using (var input = File.OpenRead(Path.Combine(publish, file.Path.Replace('/', '\\'))))
                using (var output = entry.Open()) input.CopyTo(output);
            }
        Manifest = new PackageManifest
        {
            Version = FileVersionInfo.GetVersionInfo(Path.Combine(publish, "MyCapture.dll")).ProductVersion!.Split('+')[0],
            FileCount = files.Length, Files = files, TotalExpandedBytes = files.Sum(f => f.Bytes),
            Payload = FileRecord.Create(Path.Combine(Package, "payload.zip"), "payload.zip"),
            BootstrapFiles = PackageManifest.BootstrapNames.Select(n => FileRecord.Create(Path.Combine(Package, n), n)).ToArray()
        };
        SaveManifest();
    }
    public void SaveManifest() => Json.Write(Path.Combine(Package, "installer-manifest.json"), Manifest);
    public int Run(params string[] extra)
    {
        var args = new[] { "/quiet", "--install-root", Install, "--no-shell-integration", "--log", Path.Combine(Root, "install-" + Guid.NewGuid().ToString("N") + ".log") }.Concat(extra).ToArray();
        var options = Options.Parse(args, Package);
        return Sta(() => new Installer(options, Package, new SetupLog(options.Log, Package, true, false)).Run());
    }
    public int RunShell()
    {
        var options = Options.Parse(new[] { "/quiet", "--install-root", Install, "--test-uninstall-key", Key, "--test-start-menu", Menu, "--log", Path.Combine(Root, "install-shell.log") }, Package);
        return Sta(() => new Installer(options, Package, new SetupLog(options.Log, Package, true, false)).Run());
    }
    public static T Sta<T>(Func<T> action)
    {
        T result = default!; Exception? error = null;
        var thread = new Thread(() => { try { result = action(); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }
    public string Session(int parent = int.MaxValue)
    {
        // UpdatePaths and the installer require a numeric directory core; the JSON identity
        // still carries the package's complete prerelease version.
        string session = Path.Combine(Path.GetDirectoryName(Install)!, ".MyCapture.updates", "update-" + Manifest.Version.Split('-')[0] + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        Json.Write(Path.Combine(session, "update-session.json"), new UpdaterSession
        { Token = Guid.NewGuid().ToString("N"), Version = Manifest.Version, ParentId = parent, InstallRoot = Install, SourceRoot = Install, TargetMode = "OwnedInstall" });
        return Path.Combine(session, "update-session.json");
    }
    public int RunIsolatedWithoutShell()
    {
        var options = Options.Parse(new[] { "/quiet", "--install-root", Install, "--no-shell-integration", "--test-uninstall-key", Key, "--test-start-menu", Menu, "--log", Path.Combine(Root, "install-isolated.log") }, Package);
        return Sta(() => new Installer(options, Package, new SetupLog(options.Log, Package, true, false)).Run());
    }
    /// <summary>A TEMP folder inside the fixture, so deferred cleanup workers never use the real TEMP.</summary>
    public string Temp => Path.Combine(Root, "temp");
    /// <summary>Runs the installed uninstaller and waits for its deferred worker to finish and exit.</summary>
    public string Uninstall(string name = "uninstall")
    {
        string log = Path.Combine(Root, name + ".log");
        Directory.CreateDirectory(Temp);
        var start = new ProcessStartInfo(Path.Combine(Install, "MyCaptureUninstall.exe"), "/uninstall /quiet --log " + Program.Quote(log)) { UseShellExecute = false, CreateNoWindow = true };
        start.EnvironmentVariables["TEMP"] = Temp; start.EnvironmentVariables["TMP"] = Temp;
        using (var process = Process.Start(start)) { Assert.True(process.WaitForExit(30000)); Assert.True(process.ExitCode == 0, "exit " + process.ExitCode + ": " + ReadShared(log)); }
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!ReadShared(log).Contains("Cleanup worker folder retained") && DateTime.UtcNow < deadline) Thread.Sleep(100);
        string text = ReadShared(log);
        Assert.Contains("Cleanup worker folder retained", text);
        var started = System.Text.RegularExpressions.Regex.Match(text, @"Deferred cleanup worker started \(PID (\d+)\)");
        Assert.True(started.Success);
        try
        {
            using (var worker = Process.GetProcessById(int.Parse(started.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)))
                if (string.Equals(worker.ProcessName, "MyCaptureUninstall", StringComparison.OrdinalIgnoreCase)) Assert.True(worker.WaitForExit(30000));
        }
        catch (ArgumentException) { }
        return text;
    }
    // Share write access so polling never blocks the worker's appended log lines.
    public static string ReadShared(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
    }
    public static T WithTemp<T>(string temp, Func<T> action)
    {
        string? oldTemp = Environment.GetEnvironmentVariable("TEMP"), oldTmp = Environment.GetEnvironmentVariable("TMP");
        try { Environment.SetEnvironmentVariable("TEMP", temp); Environment.SetEnvironmentVariable("TMP", temp); return action(); }
        finally { Environment.SetEnvironmentVariable("TEMP", oldTemp); Environment.SetEnvironmentVariable("TMP", oldTmp); }
    }
    public static string? RealStartupValue()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(ShellIntegration.RunKey)) return Convert.ToString(key?.GetValue("MyCapture"));
    }
    public static bool TestKeyParentExists(string parent = @"Software\MyCapture.SetupTests")
    {
        using (var key = Registry.CurrentUser.OpenSubKey(parent)) return key != null;
    }
    public static void DeleteEmptyTestKeyParent(bool existedBefore, string parent = @"Software\MyCapture.SetupTests")
    {
        // An empty key that predates this fixture is not owned by its cleanup.
        if (existedBefore) return;
        using (var key = Registry.CurrentUser.OpenSubKey(parent))
            if (key == null || key.SubKeyCount != 0 || key.ValueCount != 0) return;
        try { Registry.CurrentUser.DeleteSubKey(parent, false); } catch (InvalidOperationException) { }
    }
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MYCAPTURE_DATA_ROOT", previousData);
        Registry.CurrentUser.DeleteSubKeyTree(Key, false);
        DeleteEmptyTestKeyParent(testKeyParentExisted);
        // Permanent deletion of this fixture's own TEMP root. A failure fails the test.
        try { FileRemoval.DeleteDirectoryWithRetry(Root); }
        catch (Exception e) { throw new IOException("Cleanup failed; test fixture retained at " + Root, e); }
    }
}

public sealed class InstallerIntegrationTests
{
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void TestRegistryCleanupPreservesExistingParentsAndNonemptyKeys(bool existedBefore, bool hasChild, bool hasValue, bool retained)
    {
        using (var f = new Fixture())
        {
            string parent = f.Key + @"\cleanup-parent";
            if (existedBefore) using (Registry.CurrentUser.CreateSubKey(parent)) { }
            bool parentExisted = Fixture.TestKeyParentExists(parent);
            Assert.Equal(existedBefore, parentExisted);
            using (var key = Registry.CurrentUser.CreateSubKey(parent))
            {
                if (hasChild) using (key.CreateSubKey("child")) { }
                if (hasValue) key.SetValue("sentinel", "keep");
            }
            Fixture.DeleteEmptyTestKeyParent(parentExisted, parent);
            Assert.Equal(retained, Fixture.TestKeyParentExists(parent));
        }
    }

    [Theory]
    [InlineData("missing-exe")][InlineData("missing-dll")][InlineData("not-pe")][InlineData("x86")][InlineData("version")]
    [InlineData("tray-idle.ico")][InlineData("tray-capturing.ico")][InlineData("tray-busy.ico")][InlineData("tray-error.ico")]
    public void StagedApplicationRequiresX64VersionAndEveryTrayAsset(string fault)
    {
        using (var f = new Fixture())
        {
            string stage = Path.Combine(f.Root, "staged"); Directory.CreateDirectory(Path.Combine(stage, "Assets"));
            foreach (string name in new[] { "MyCapture.exe", "MyCapture.dll" })
                if (!(fault == "missing-exe" && name.EndsWith(".exe", StringComparison.Ordinal)) && !(fault == "missing-dll" && name.EndsWith(".dll", StringComparison.Ordinal)))
                    File.Copy(Path.Combine(f.Root, "publish", name), Path.Combine(stage, name));
            foreach (string name in new[] { "tray-idle.ico", "tray-capturing.ico", "tray-busy.ico", "tray-error.ico" })
                if (name != fault) File.WriteAllBytes(Path.Combine(stage, "Assets", name), new byte[] { 0, 1 });
            if (fault == "version") f.Manifest.Version = "99.0.0";
            if (fault == "not-pe") File.WriteAllText(Path.Combine(stage, "MyCapture.exe"), "not a PE");
            if (fault == "x86")
                using (var file = File.Open(Path.Combine(stage, "MyCapture.exe"), FileMode.Open, FileAccess.ReadWrite))
                using (var reader = new BinaryReader(file))
                { file.Position = 0x3c; int offset = reader.ReadInt32(); file.Position = offset + 4; file.WriteByte(0x4c); file.WriteByte(0x01); }
            SetupContractsTests.Code(14, () => Payload.Application(stage, f.Manifest));
        }
    }
    [Fact] public void BrokenZipMapsToStagingAndChangedInstalledInventoryMapsToCommit()
    {
        using (var f = new Fixture())
        {
            string zip = Path.Combine(f.Root, "not-zip"); File.WriteAllText(zip, "not zip");
            SetupContractsTests.Code(16, () => Payload.Extract(zip, f.Manifest, Path.Combine(f.Root, "stage")));
            string publish = Path.Combine(f.Root, "publish"); Payload.VerifyInventory(publish, f.Manifest);
            File.AppendAllText(Path.Combine(publish, "MyCapture.dll"), "changed");
            SetupContractsTests.Code(17, () => Payload.VerifyInventory(publish, f.Manifest));
        }
    }
    [Fact] public void JunctionsAtRootAncestorsAndUserFilesAreRejectedWithoutTraversal()
    {
        using (var f = new Fixture())
        {
            string outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "untouched");
            string link = Path.Combine(f.Root, "junction"); CreateJunction(link, outside);
            SetupContractsTests.Code(12, () => PathSafety.InstallRoot(link, f.Manifest));
            // A link ancestor is refused only for updater sessions (install.ps1 Assert-UpdateRootBoundary);
            // direct setup inspects the root itself (DirectInstallAcceptsJunctionAncestorsButUpdateSessionsRefuseThem).
            SetupContractsTests.Code(12, () => PathSafety.UpdateBoundary(Path.Combine(link, "new")));
            SetupContractsTests.Code(12, () => PathSafety.UpdateBoundary(link));
            SetupContractsTests.Code(12, () => Options.ValidateTestTargets(f.Key, Path.Combine(link, "menu")));
            SetupContractsTests.Code(30, () => Uninstaller.Verify(link));
            string old = Path.Combine(f.Root, "old"), stage = Path.Combine(f.Root, "stage"); Directory.CreateDirectory(old); Directory.CreateDirectory(stage);
            Json.Write(Path.Combine(old, "install-manifest.json"), f.Manifest); CreateJunction(Path.Combine(old, "user-link"), outside);
            SetupContractsTests.Code(12, () => PathSafety.PreserveUserFiles(old, stage));
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        }
    }
    [Fact] public void ShellCleanupPreservesModifiedShortcutsAndForeignRegistration()
    {
        using (var f = new Fixture())
        {
            Assert.Equal(0, f.RunShell());
            var manifest = PackageManifest.Read(Path.Combine(f.Install, "install-manifest.json"), true);
            string target = Path.Combine(f.Root, "another-install", "MyCapture.exe");
            Fixture.Sta(() => { ShellLink.Write(Path.Combine(f.Menu, "MyCapture.lnk"), target, "", f.Root, target, "Modified by user"); return 0; });
            File.WriteAllText(Path.Combine(f.Menu, "user.txt"), "menu content");
            using (var key = Registry.CurrentUser.OpenSubKey(f.Key, true)) key!.SetValue("InstallLocation", f.Root);
            var info = new System.Collections.Generic.List<string>(); var warnings = new System.Collections.Generic.List<string>();
            Fixture.Sta(() => { ShellIntegration.Remove(f.Install, manifest, warnings.Add, info.Add); return 0; });
            Assert.Empty(warnings);
            Assert.Equal(new[] { "Preserved modified shortcut: MyCapture.lnk", "Removed owned shortcut: Uninstall MyCapture.lnk",
                "Preserved an uninstall registration that does not belong to this installation." }, info);
            Assert.True(File.Exists(Path.Combine(f.Menu, "MyCapture.lnk"))); Assert.False(File.Exists(Path.Combine(f.Menu, "Uninstall MyCapture.lnk")));
            Assert.Equal("menu content", File.ReadAllText(Path.Combine(f.Menu, "user.txt")));
            using (var key = Registry.CurrentUser.OpenSubKey(f.Key)) Assert.Equal(f.Root, key!.GetValue("InstallLocation"));
        }
    }
    [Fact] public void RuntimeFailureWritesLogAndCopiesHelpWithInteractiveAndQuietGuidance()
    {
        using (var f = new Fixture())
        {
            string temp = Path.Combine(f.Root, "runtime-temp"); Directory.CreateDirectory(temp);
            string? oldTemp = Environment.GetEnvironmentVariable("TEMP"), oldTmp = Environment.GetEnvironmentVariable("TMP");
            try
            {
                Environment.SetEnvironmentVariable("TEMP", temp); Environment.SetEnvironmentVariable("TMP", temp);
                int calls = 0; string message = "";
                int code = Program.Run(Array.Empty<string>(), () => 528039, () => throw new Exception("Probe order"), f.Package,
                    (success, text, uninstall) => { Assert.False(success); calls++; message = text; });
                Assert.Equal(19, code); Assert.Equal(1, calls); Assert.Contains("portable.zip", message); Assert.Contains("Microsoft Store", message);
                Assert.Equal(File.ReadAllBytes(Path.Combine(f.Package, "SETUP-HELP.txt")), File.ReadAllBytes(Path.Combine(temp, "MyCapture-Setup-Help.txt")));
                string log = File.ReadAllText(Assert.Single(Directory.GetFiles(temp, "MyCapture-Install-*.log"))); Assert.Contains("(exit 19)", log);
                Assert.Matches(@"\d{4}-\d{2}-\d{2}T.+ \[ERROR\]", log);
                Assert.Equal(19, Program.Run(new[] { "/quiet" }, () => 0, () => { }, f.Package, (_, __, ___) => calls++)); Assert.Equal(1, calls);
                Assert.False(Directory.Exists(f.Install));
            }
            finally { Environment.SetEnvironmentVariable("TEMP", oldTemp); Environment.SetEnvironmentVariable("TMP", oldTmp); }
        }
    }
    [Fact] public void ManifestJsonRejectsMissingDuplicateAndMalformedFields()
    {
        using (var f = new Fixture())
        {
            string path = Path.Combine(f.Root, "invalid.json");
            foreach (string text in new[] { "{}", "not json", "{\"Product\":\"MyCapture\",\"Product\":\"Other\"}" })
            { File.WriteAllText(path, text); SetupContractsTests.Code(14, () => PackageManifest.Read(path)); }
        }
    }
    [Theory][InlineData("path")][InlineData("versionless-path")][InlineData("prerelease-path")][InlineData("token")][InlineData("version")][InlineData("receipt")][InlineData("source")][InlineData("target-mode")][InlineData("parent")][InlineData("owned-hash")]
    public void UpdateSessionRefusesForgedOrStaleBoundary(string fault)
    {
        using (var f = new Fixture())
        {
            Assert.Equal(0, f.Run()); string session = f.Session(); var metadata = Json.Read<UpdaterSession>(session); int expected = 14;
            switch (fault)
            {
                case "path": session = Path.Combine(f.Root, "update-session.json"); expected = 12; break;
                case "versionless-path":
                case "prerelease-path":
                    string name = fault == "versionless-path" ? "update-" : "update-4.2.1-win10.8-";
                    string invalidDirectory = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(session))!, name + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(invalidDirectory);
                    session = Path.Combine(invalidDirectory, "update-session.json"); expected = 12; break;
                case "token": metadata.Token = "bad"; break; case "version": metadata.Version = "0.0.0"; break;
                case "receipt": File.WriteAllText(Path.Combine(Path.GetDirectoryName(session)!, "install-result.json"), "existing"); expected = 12; break;
                case "source": metadata.SourceRoot = f.Install + "-other"; expected = 12; break;
                case "target-mode": metadata.TargetMode = "Unknown"; expected = 12; break;
                case "parent": metadata.ParentId = Process.GetCurrentProcess().Id; expected = 15; break;
                case "owned-hash": File.AppendAllText(Path.Combine(f.Install, "MyCapture.dll"), "changed"); break;
            }
            Json.Write(session, metadata); string? old = Environment.GetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION");
            try { Environment.SetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION", session); Assert.Equal(expected, f.Run()); }
            finally { Environment.SetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION", old); }
            Assert.True(Directory.Exists(f.Install));
        }
    }
    [Fact] public void HashVerificationDetectsTamperAndBootstrapMismatch()
    {
        using (var f = new Fixture())
        {
            var record = f.Manifest.Payload; Assert.True(record.Matches(Path.Combine(f.Package, "payload.zip")));
            File.AppendAllText(Path.Combine(f.Package, "payload.zip"), "tamper"); Assert.False(record.Matches(Path.Combine(f.Package, "payload.zip"))); Assert.Equal(14, f.Run("--verify-only"));
            File.AppendAllText(Path.Combine(f.Package, "SETUP-HELP.txt"), "tamper"); SetupContractsTests.Code(14, () => f.Manifest.VerifyBootstrap(f.Package));
        }
    }
    [Theory][InlineData("traversal")][InlineData("absolute")][InlineData("ads")][InlineData("duplicate")][InlineData("count")][InlineData("size")][InlineData("hash")][InlineData("directory-traversal")]
    public void ZipRulesRejectHostileEntriesBeforeCommit(string fault)
    {
        using (var f = new Fixture())
        {
            string zip = Path.Combine(f.Root, "bad.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                string name = fault == "traversal" ? "../outside" : fault == "absolute" ? "/absolute" : fault == "ads" ? "a:b" : fault == "directory-traversal" ? "../outside/" : "a";
                var entry = archive.CreateEntry(name);
                using (var output = entry.Open()) output.WriteByte(1);
                if (fault == "duplicate") { archive.CreateEntry("A"); }
                if (fault == "count") { archive.CreateEntry("b"); }
            }
            var manifest = SetupContractsTests.Manifest(); if (fault == "size") manifest.Files[0].Bytes = 2;
            SetupContractsTests.Code(14, () => Payload.Extract(zip, manifest, Path.Combine(f.Root, "stage")));
            Assert.False(File.Exists(Path.Combine(f.Root, "outside"))); Assert.False(Directory.Exists(f.Install));
        }
    }
    [Fact] public void OwnershipUsesProductMarkerAndLegacyProductName()
    {
        using (var f = new Fixture())
        {
            Directory.CreateDirectory(f.Install); string sentinel = Path.Combine(f.Install, "keep.txt"); File.WriteAllText(sentinel, "owned by user");
            SetupContractsTests.Code(20, () => PathSafety.InstallRoot(f.Install, f.Manifest)); Assert.Equal("owned by user", File.ReadAllText(sentinel));
            File.Copy(Path.Combine(f.Package, "MyCaptureSetup.exe"), Path.Combine(f.Install, "MyCapture.dll"));
            Assert.Equal(f.Install, PathSafety.InstallRoot(f.Install, f.Manifest));
            Json.Write(Path.Combine(f.Install, "install-manifest.json"), f.Manifest); Assert.True(PathSafety.IsOwned(f.Install));
        }
    }
    [Fact] public void ProductOnlyLegacyMarkerOwnsDirectReinstallButIsNotAnUpdateInventory()
    {
        using (var f = new Fixture())
        {
            Directory.CreateDirectory(f.Install);
            File.WriteAllText(Path.Combine(f.Install, "MyCapture.exe"), "old app");
            File.WriteAllText(Path.Combine(f.Install, "notes.txt"), "user notes");
            string stage = Path.Combine(f.Root, "replacement"); Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "MyCapture.exe"), "verified replacement");
            // install.ps1 Preserve-UserInstallFiles: without a marker only installer names are owned.
            SetupContractsTests.Code(20, () => PathSafety.PreserveUserFiles(f.Install, stage));
            File.Delete(Path.Combine(stage, "notes.txt"));
            File.WriteAllText(Path.Combine(f.Install, "install-manifest.json"), "{\"Product\":\"MyCapture\"}");
            Assert.Equal(f.Install, PathSafety.InstallRoot(f.Install, f.Manifest));
            // An existing marker must be a valid inventory (Read-And-ValidateManifest, exit 14).
            SetupContractsTests.Code(14, () => PathSafety.PreserveUserFiles(f.Install, stage));
            Assert.Equal("verified replacement", File.ReadAllText(Path.Combine(stage, "MyCapture.exe")));
            Assert.False(File.Exists(Path.Combine(stage, "notes.txt")));
            SetupContractsTests.Code(14, () => PathSafety.OwnedUpdateSource(f.Install));
            // The direct reinstall accepts the Product marker and replaces the whole legacy root.
            Assert.Equal(0, f.Run());
            Assert.False(File.Exists(Path.Combine(f.Install, "notes.txt")));
            Payload.VerifyInventory(f.Install, PackageManifest.Read(Path.Combine(f.Install, "install-manifest.json"), true));
        }
    }
    [Fact] public void VerifyOnlyDiskAndTransactionalLifecycle()
    {
        using (var f = new Fixture())
        {
            Assert.Equal(13, f.Run("--verify-only", "--minimum-free-bytes", long.MaxValue.ToString()));
            Assert.Equal(0, f.Run("--verify-only")); Assert.False(Directory.Exists(f.Install));
            Assert.Empty(Directory.GetDirectories(f.Root, ".MyCapture.stage.*"));
            Assert.Equal(0, f.Run());
            Assert.Empty(Directory.GetFiles(f.Root, ".mycapture-write-probe-*"));
            string user = Path.Combine(f.Install, "notes.txt"); File.WriteAllText(user, "user content");
            // install.ps1: a direct reinstall replaces the whole root and deletes the previous tree.
            // Only updater sessions carry extra files forward (UpdateReceiptTracksSuccessAndRollback).
            Assert.Equal(0, f.Run()); Assert.False(File.Exists(user));
            Assert.Empty(Directory.GetDirectories(f.Root, ".MyCapture.backup.*"));
            var installed = PackageManifest.Read(Path.Combine(f.Install, "install-manifest.json"));
            Assert.Contains(installed.Files, r => r.Path == "MyCaptureUninstall.exe"); Payload.VerifyInventory(f.Install, installed);
            File.WriteAllText(user, "user content");
            foreach (string fault in new[] { "AfterBackup", "AfterCommit" })
            {
                string previous = FileRecord.Hash(Path.Combine(f.Install, "install-manifest.json"));
                Assert.Equal(17, f.Run("--test-fault", fault)); Assert.Equal("user content", File.ReadAllText(user));
                Assert.Equal(previous, FileRecord.Hash(Path.Combine(f.Install, "install-manifest.json")));
                Assert.Empty(Directory.GetDirectories(f.Root, ".MyCapture.stage.*")); Assert.Empty(Directory.GetDirectories(f.Root, ".MyCapture.backup.*"));
            }
        }
    }
    [Fact] public void PreservationConflictsAndLinkedUserFilesRefuseReplacement()
    {
        using (var f = new Fixture())
        {
            Assert.Equal(0, f.Run());
            string stage = Path.Combine(f.Root, "stage"); Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "MyCapture.exe"), "new binary"); File.WriteAllText(Path.Combine(f.Install, "notes.txt"), "notes");
            PathSafety.PreserveUserFiles(f.Install, stage); Assert.Equal("notes", File.ReadAllText(Path.Combine(stage, "notes.txt")));
            Assert.Equal("new binary", File.ReadAllText(Path.Combine(stage, "MyCapture.exe")));
            SetupContractsTests.Code(20, () => PathSafety.PreserveUserFiles(f.Install, stage));
            Assert.Equal("notes", File.ReadAllText(Path.Combine(f.Install, "notes.txt")));
        }
    }
    [Fact] public void MutexReturns18AndUninstall31AcrossProcessBoundary()
    {
        using (var f = new Fixture())
        using (new OperationMutex())
        {
            var start = new ProcessStartInfo(Path.Combine(f.Package, "MyCaptureSetup.exe"), "/quiet --install-root " + Program.Quote(f.Install) + " --no-shell-integration --verify-only") { UseShellExecute = false, CreateNoWindow = true };
            using (var process = Process.Start(start)) { Assert.True(process.WaitForExit(30000)); Assert.Equal(18, process.ExitCode); }
            start.Arguments = "/quiet /uninstall";
            using (var process = Process.Start(start)) { Assert.True(process.WaitForExit(30000)); Assert.Equal(31, process.ExitCode); }
        }
    }
    [Fact] public void ReceiptIsUtf8WithoutBomAtomicAndContainsExactlyTheLegacyFields()
    {
        using (var f = new Fixture())
        {
            string path = Path.Combine(f.Root, "install-result.json");
            var receipt = new InstallReceipt { Token = new string('a', 32), Version = "4.2.1", ExitCode = 17, InstallRoot = f.Install, LogPath = Path.Combine(f.Root, "log") };
            receipt.Write(path); var bytes = File.ReadAllBytes(path); Assert.NotEqual((byte)0xef, bytes[0]);
            string json = Encoding.UTF8.GetString(bytes);
            foreach (string name in new[] { "Token", "Version", "ExitCode", "InstallRoot", "LogPath" }) Assert.Contains("\"" + name + "\":", json);
            Assert.Equal(5, json.Count(c => c == ':') - 2); // two drive-letter colons in absolute paths
            Assert.Equal(f.Install, Json.Read<InstallReceipt>(path).InstallRoot); Assert.False(File.Exists(path + ".tmp"));
            Assert.Throws<IOException>(() => receipt.Write(path)); Assert.Equal(17, Json.Read<InstallReceipt>(path).ExitCode);
        }
    }
    [Theory][InlineData("")][InlineData("AfterBackup")][InlineData("AfterCommit")]
    public void UpdateReceiptTracksSuccessAndRollback(string fault)
    {
        using (var f = new Fixture())
        {
            Assert.Equal(0, f.Run()); string session = f.Session();
            Assert.Matches(@"\Aupdate-\d+\.\d+\.\d+-[a-f0-9]{32}\z", Path.GetFileName(Path.GetDirectoryName(session)));
            Assert.Equal(f.Manifest.Version, Json.Read<UpdaterSession>(session).Version);
            string? previous = Environment.GetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION");
            string user = Path.Combine(f.Install, "user folder", "notes.txt"); Directory.CreateDirectory(Path.GetDirectoryName(user)!); File.WriteAllText(user, "user content");
            try
            {
                Environment.SetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION", session);
                int code = fault.Length == 0 ? f.Run() : f.Run("--test-fault", fault); Assert.Equal(fault.Length == 0 ? 0 : 17, code);
                // Updater sessions preserve unmanifested files; a rollback restores the previous tree.
                Assert.Equal("user content", File.ReadAllText(user));
                Assert.Empty(Directory.GetDirectories(f.Root, ".MyCapture.stage.*")); Assert.Empty(Directory.GetDirectories(f.Root, ".MyCapture.backup.*"));
                var receipt = Json.Read<InstallReceipt>(Path.Combine(Path.GetDirectoryName(session)!, "install-result.json"));
                Assert.Equal(code, receipt.ExitCode); Assert.Equal(f.Install, receipt.InstallRoot); Assert.Equal(f.Manifest.Version, receipt.Version);
                Assert.Equal(Json.Read<UpdaterSession>(session).Token, receipt.Token); Assert.True(File.Exists(receipt.LogPath));
            }
            finally { Environment.SetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION", previous); }
        }
    }
    [Theory][InlineData("owned")][InlineData("foreign")][InlineData("absent")]
    public void LegacyShellRepairChangesOnlyExistingOwnedTargets(string kind)
    {
        using (var f = new Fixture())
        {
            var manifest = f.Manifest; manifest.TestRegistryKey = f.Key; manifest.TestStartMenu = f.Menu;
            string oldRoot = kind == "foreign" ? Path.Combine(f.Root, "another-install") : f.Install;
            string oldUninstaller = Path.Combine(oldRoot, "uninstall.cmd");
            string link = Path.Combine(f.Menu, "Uninstall MyCapture.lnk");
            if (kind != "absent")
            {
                Directory.CreateDirectory(f.Menu);
                using (var key = Registry.CurrentUser.CreateSubKey(f.Key))
                { key.SetValue("InstallLocation", oldRoot); key.SetValue("UninstallString", Program.Quote(oldUninstaller)); }
                Fixture.Sta(() => { ShellLink.Write(link, oldUninstaller, "", oldRoot, oldUninstaller, "Uninstall MyCapture"); return 0; });
            }
            var warnings = new System.Collections.Generic.List<string>();
            Assert.Equal(kind == "owned", Fixture.Sta(() => ShellIntegration.LegacyTargetsOwned(f.Install, manifest, warnings.Add)));
            Fixture.Sta(() => { ShellIntegration.RepairLegacyTargets(f.Install, manifest, warnings.Add); return 0; });
            Assert.Empty(warnings);
            using (var key = Registry.CurrentUser.OpenSubKey(f.Key))
            {
                if (kind == "absent") { Assert.Null(key); Assert.False(Directory.Exists(f.Menu)); }
                else
                {
                    string expected = kind == "owned" ? Path.Combine(f.Install, "MyCaptureUninstall.exe") : oldUninstaller;
                    Assert.Equal(Program.Quote(expected) + (kind == "owned" ? " /uninstall" : ""), key!.GetValue("UninstallString"));
                    Assert.True(PathSafety.Equal(expected, Fixture.Sta(() => ShellLink.Target(link))));
                    Assert.Equal(kind == "owned" ? "/uninstall" : "", Fixture.Sta(() => ShellLink.Arguments(link)));
                }
            }
        }
    }
    [Fact] public void RealShellRegistrationAndDeferredUninstallAreIsolatedAndPreserveExternalData()
    {
        using (var f = new Fixture())
        {
            string external = Path.Combine(f.Root, "user-data.bin"); File.WriteAllBytes(external, new byte[] { 0, 1, 255 });
            Assert.Equal(0, f.RunShell());
            using (var key = Registry.CurrentUser.OpenSubKey(f.Key))
            {
                Assert.NotNull(key); Assert.Equal("MyCapture", key!.GetValue("DisplayName")); Assert.Equal(f.Install, key.GetValue("InstallLocation"));
                Assert.Equal(Program.Quote(Path.Combine(f.Install, "MyCaptureUninstall.exe")) + " /uninstall", key.GetValue("UninstallString"));
                Assert.Equal(Program.Quote(Path.Combine(f.Install, "MyCaptureUninstall.exe")) + " /uninstall /quiet", key.GetValue("QuietUninstallString"));
                Assert.Equal(1, key.GetValue("NoModify")); Assert.Equal(1, key.GetValue("NoRepair"));
            }
            Assert.True(PathSafety.Equal(Path.Combine(f.Install, "MyCapture.exe"), Fixture.Sta(() => ShellLink.Target(Path.Combine(f.Menu, "MyCapture.lnk")))));
            Assert.True(PathSafety.Equal(Path.Combine(f.Install, "MyCaptureUninstall.exe"), Fixture.Sta(() => ShellLink.Target(Path.Combine(f.Menu, "Uninstall MyCapture.lnk")))));
            Assert.Equal("/uninstall", Fixture.Sta(() => ShellLink.Arguments(Path.Combine(f.Menu, "Uninstall MyCapture.lnk"))));
            string previousIdentity = PackageManifest.Read(Path.Combine(f.Install, "install-manifest.json"), true).InstallationId!;
            Assert.Equal(0, f.Run()); // NoShellIntegration must retain the exact previous ownership targets.
            var installed = PackageManifest.Read(Path.Combine(f.Install, "install-manifest.json"), true);
            Assert.NotEqual(previousIdentity, installed.InstallationId);
            Assert.Equal(f.Key, installed.TestRegistryKey); Assert.Equal(f.Menu, installed.TestStartMenu); Assert.True(installed.ShellIntegration);
            string? realStartup = Fixture.RealStartupValue();
            using (var key = Registry.CurrentUser.CreateSubKey(ShellIntegration.StartupKey(installed)))
                key.SetValue("MyCapture", Program.Quote(Path.Combine(f.Install, "MyCapture.exe")) + " --background");
            string log = f.Uninstall();
            Assert.Contains("MyCapture removal completed", log);
            Assert.False(Directory.Exists(f.Install)); Assert.False(Directory.Exists(f.Menu));
            using (var key = Registry.CurrentUser.OpenSubKey(f.Key)) Assert.Null(key);
            Assert.Equal(new byte[] { 0, 1, 255 }, File.ReadAllBytes(external));
            foreach (string line in new[] { "Removed the owned MyCapture startup value.", "Removed owned shortcut: MyCapture.lnk",
                "Removed owned shortcut: Uninstall MyCapture.lnk", "Removed the owned uninstall registration." }) Assert.Contains(line, log);
            Assert.DoesNotContain("Could not", log); Assert.DoesNotContain("[ERROR]", log);
            Assert.Equal(realStartup, Fixture.RealStartupValue());
            // The exited worker keeps only its own image; the next setup run deletes the folder.
            string worker = Assert.Single(Directory.GetDirectories(f.Temp, FileRemoval.WorkerPrefix + "*"));
            Assert.Equal(new[] { "MyCaptureUninstall.exe" }, Directory.GetFileSystemEntries(worker).Select(Path.GetFileName).ToArray());
            Assert.Contains("Cleanup worker folder retained until the next MyCapture setup or uninstall run: " + worker, log);
            Assert.Equal(0, Fixture.WithTemp(f.Temp, () => f.Run()));
            Assert.False(Directory.Exists(worker));
        }
    }
    [Theory][InlineData("owned")][InlineData("foreign")][InlineData("absent")]
    public void UninstallRemovesOnlyTheStartupValueOwnedByThisInstallation(string kind)
    {
        using (var f = new Fixture())
        {
            Assert.Equal(0, f.RunIsolatedWithoutShell());
            var installed = PackageManifest.Read(Path.Combine(f.Install, "install-manifest.json"), true);
            Assert.False(installed.ShellIntegration); Assert.Equal(f.Key, installed.TestRegistryKey);
            // Test installs use <test key>\Run in place of HKCU\...\CurrentVersion\Run.
            string run = f.Key + @"\Run"; Assert.Equal(run, ShellIntegration.StartupKey(installed));
            Assert.Equal(ShellIntegration.RunKey, ShellIntegration.StartupKey(new PackageManifest()));
            string owned = Program.Quote(Path.Combine(f.Install, "MyCapture.exe")) + " --background";
            string foreign = Program.Quote(Path.Combine(f.Root, "portable", "MyCapture.exe")) + " --background";
            string? value = kind == "owned" ? owned : kind == "foreign" ? foreign : null;
            using (var key = Registry.CurrentUser.CreateSubKey(run))
            { key.SetValue("Other", "kept"); if (value != null) key.SetValue("MyCapture", value); }
            string? realStartup = Fixture.RealStartupValue();
            string log = f.Uninstall();
            Assert.Contains("MyCapture removal completed", log); Assert.False(Directory.Exists(f.Install));
            using (var key = Registry.CurrentUser.OpenSubKey(run))
            {
                Assert.NotNull(key); Assert.Equal("kept", key!.GetValue("Other"));
                Assert.Equal(kind == "foreign" ? foreign : null, key.GetValue("MyCapture"));
            }
            Assert.Equal(kind == "owned", log.Contains("Removed the owned MyCapture startup value."));
            Assert.Equal(kind == "foreign", log.Contains("Preserved a MyCapture startup value that does not point to this installation."));
            Assert.DoesNotContain("Could not", log); Assert.DoesNotContain("[ERROR]", log);
            Assert.Equal(realStartup, Fixture.RealStartupValue());
        }
    }
    [Fact] public void LockedTreesFollowTheLegacyRetrySchedulesAndAreDeletedPermanently()
    {
        using (var f = new Fixture())
        {
            string tree = Path.Combine(f.Root, "tree"); Directory.CreateDirectory(Path.Combine(tree, "sub"));
            string locked = Path.Combine(tree, "sub", "locked.bin"); File.WriteAllBytes(locked, new byte[] { 1 });
            string readOnly = Path.Combine(tree, "read-only.txt"); File.WriteAllText(readOnly, "x");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly | FileAttributes.Hidden);
            var sleeps = new System.Collections.Generic.List<int>(); var warnings = new System.Collections.Generic.List<string>();
            using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => FileRemoval.DeleteDirectoryWithRetry(tree, sleeps.Add));
                Assert.Equal(Enumerable.Range(1, 6).Select(FileRemoval.InstallRetryDelay), sleeps);
                sleeps.Clear();
                Assert.False(FileRemoval.RemoveTarget(tree, warnings.Add, sleeps.Add));
                Assert.Equal(Enumerable.Range(1, 30).Select(FileRemoval.UninstallRetryDelay), sleeps);
                Assert.Equal(30, warnings.Count); Assert.StartsWith("Removal attempt 1 failed: ", warnings[0]);
                Assert.True(File.Exists(locked));
            }
            sleeps.Clear(); warnings.Clear();
            var handle = File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.True(FileRemoval.RemoveTarget(tree, warnings.Add, delay => { sleeps.Add(delay); handle.Dispose(); }));
            Assert.Equal(new[] { FileRemoval.UninstallRetryDelay(1) }, sleeps); Assert.Single(warnings);
            Assert.False(Directory.Exists(tree));
            // Links inside a deleted tree are removed without touching their targets.
            string outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "kept");
            string linked = Path.Combine(f.Root, "linked-tree"); Directory.CreateDirectory(linked); CreateJunction(Path.Combine(linked, "link"), outside);
            FileRemoval.DeleteTree(linked);
            Assert.False(Directory.Exists(linked)); Assert.Equal("kept", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        }
    }
    [Fact] public void StaleCleanupWorkersAreDeletedOnlyWhenExactlyOwnedAndNotInUse()
    {
        using (var f = new Fixture())
        {
            Directory.CreateDirectory(f.Temp);
            string Worker(string name, params string[] files)
            {
                string directory = Path.Combine(f.Temp, name); Directory.CreateDirectory(directory);
                foreach (string file in files) File.WriteAllText(Path.Combine(directory, file), file);
                return directory;
            }
            string Id() => Guid.NewGuid().ToString("N");
            string stale = Worker(FileRemoval.WorkerPrefix + Id(), "MyCaptureUninstall.exe");
            string complete = Worker(FileRemoval.WorkerPrefix + Id(), FileRemoval.WorkerFiles);
            string inUse = Worker(FileRemoval.WorkerPrefix + Id(), "MyCaptureUninstall.exe", "cleanup.json");
            string extra = Worker(FileRemoval.WorkerPrefix + Id(), "MyCaptureUninstall.exe", "notes.txt");
            string nested = Worker(FileRemoval.WorkerPrefix + Id()); Directory.CreateDirectory(Path.Combine(nested, "child"));
            string wrongName = Worker(FileRemoval.WorkerPrefix + "not-a-guid", "MyCaptureUninstall.exe");
            string upperCase = Worker(FileRemoval.WorkerPrefix + Id().ToUpperInvariant(), "MyCaptureUninstall.exe");
            File.SetAttributes(Path.Combine(stale, "MyCaptureUninstall.exe"), FileAttributes.ReadOnly);
            var messages = new System.Collections.Generic.List<string>();
            using (File.Open(Path.Combine(inUse, "MyCaptureUninstall.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
                Fixture.WithTemp(f.Temp, () => { FileRemoval.RemoveStaleWorkers(messages.Add); return 0; });
            Assert.False(Directory.Exists(stale)); Assert.False(Directory.Exists(complete));
            Assert.Contains("Removed a previous cleanup worker folder: " + stale, messages);
            Assert.Contains("Removed a previous cleanup worker folder: " + complete, messages);
            // The executable is deleted first, so an in-use worker keeps every file.
            Assert.True(File.Exists(Path.Combine(inUse, "MyCaptureUninstall.exe")) && File.Exists(Path.Combine(inUse, "cleanup.json")));
            Assert.Contains(messages, m => m.StartsWith("Kept a cleanup worker folder that is in use or inaccessible: " + inUse, StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(extra, "notes.txt")) && File.Exists(Path.Combine(extra, "MyCaptureUninstall.exe")));
            Assert.Contains("Kept a cleanup worker folder with unexpected contents: " + extra, messages);
            Assert.True(Directory.Exists(Path.Combine(nested, "child")));
            Assert.True(File.Exists(Path.Combine(wrongName, "MyCaptureUninstall.exe")) && File.Exists(Path.Combine(upperCase, "MyCaptureUninstall.exe")));
            Assert.Equal(5, messages.Count);
        }
    }
    [Fact] public void DirectInstallAcceptsJunctionAncestorsButUpdateSessionsRefuseThem()
    {
        using (var f = new Fixture())
        {
            // A profile moved behind a junction: only the ancestors are links, not the root itself.
            string profile = Path.Combine(f.Root, "moved profile"); Directory.CreateDirectory(profile);
            string link = Path.Combine(f.Root, "profile link"); CreateJunction(link, profile);
            f.Install = Path.Combine(link, "Programs", "MyCapture");
            // install.ps1 Get-SafeInstallRoot inspects the root itself; a root that is a link is still refused.
            Assert.Equal(f.Install, PathSafety.InstallRoot(f.Install, f.Manifest));
            SetupContractsTests.Code(12, () => PathSafety.InstallRoot(link, f.Manifest));
            Assert.Equal(0, f.RunIsolatedWithoutShell());
            Assert.Equal(0, f.RunIsolatedWithoutShell()); // Direct reinstall: backup move and deletion below the junction.
            Payload.VerifyInventory(f.Install, PackageManifest.Read(Path.Combine(f.Install, "install-manifest.json"), true));
            Assert.True(File.Exists(Path.Combine(profile, "Programs", "MyCapture", "MyCapture.exe")));
            Assert.Empty(Directory.GetDirectories(Path.Combine(profile, "Programs"), ".MyCapture.*"));
            Assert.Empty(Directory.GetFiles(Path.Combine(profile, "Programs"), ".mycapture-write-probe-*"));
            // Updater sessions keep install.ps1 Assert-UpdateRootBoundary and the session-path walk.
            SetupContractsTests.Code(12, () => PathSafety.UpdateBoundary(f.Install));
            string previous = FileRecord.Hash(Path.Combine(f.Install, "install-manifest.json"));
            string session = f.Session(); string? old = Environment.GetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION");
            try { Environment.SetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION", session); Assert.Equal(12, f.Run()); }
            finally { Environment.SetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION", old); }
            Assert.Equal(previous, FileRecord.Hash(Path.Combine(f.Install, "install-manifest.json")));
            // Uninstall checks the root itself only and removes it through the junctioned ancestor.
            string log = f.Uninstall();
            Assert.Contains("MyCapture removal completed", log); Assert.DoesNotContain("Could not", log);
            Assert.False(Directory.Exists(f.Install)); Assert.True(Directory.Exists(Path.Combine(profile, "Programs")));
        }
    }
    [Fact] public void UninstallVerifiesLikeTheLegacyScriptWithoutTheUpdaterBoundary()
    {
        using (var f = new Fixture())
        {
            Assert.Equal(0, f.RunIsolatedWithoutShell());
            var defaults = PathSafety.BoundaryFolders;
            try
            {
                // The fixture root plays the part of Program Files; real system folders are never used.
                PathSafety.BoundaryFolders = () => new[] { new System.Collections.Generic.KeyValuePair<string, bool>(f.Root, true) };
                SetupContractsTests.Code(12, () => PathSafety.UpdateBoundary(f.Install));
                Assert.Equal(f.Manifest.Version, Uninstaller.Verify(f.Install).Version);
                // Updater sessions still apply the boundary to the same root.
                string session = f.Session(); string? old = Environment.GetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION");
                try { Environment.SetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION", session); Assert.Equal(12, f.Run()); }
                finally { Environment.SetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION", old); }
                Assert.Equal(12, Json.Read<InstallReceipt>(Path.Combine(Path.GetDirectoryName(session)!, "install-result.json")).ExitCode);
            }
            finally { PathSafety.BoundaryFolders = defaults; }
            // Assert-VerifiedInstallation checks that remain: drive root and a missing manifest.
            SetupContractsTests.Code(30, () => Uninstaller.Verify(Path.GetPathRoot(f.Root)!));
            string unowned = Path.Combine(f.Root, "no-marker"); Directory.CreateDirectory(unowned);
            File.Copy(Path.Combine(f.Install, "MyCaptureUninstall.exe"), Path.Combine(unowned, "MyCaptureUninstall.exe"));
            SetupContractsTests.Code(30, () => Uninstaller.Verify(unowned));
            string log = f.Uninstall();
            Assert.Contains("MyCapture removal completed", log); Assert.False(Directory.Exists(f.Install));
        }
    }
    [Theory][InlineData("owned")][InlineData("foreign")]
    public void UninstallCleansOwnedIsolatedShellEntriesEvenWithoutShellIntegration(string kind)
    {
        using (var f = new Fixture())
        {
            Assert.Equal(0, f.RunIsolatedWithoutShell());
            var installed = PackageManifest.Read(Path.Combine(f.Install, "install-manifest.json"), true);
            Assert.False(installed.ShellIntegration); Assert.Equal(f.Key, installed.TestRegistryKey); Assert.Equal(f.Menu, installed.TestStartMenu);
            bool realMenu = Directory.Exists(ShellIntegration.DefaultMenu), realKey;
            using (var key = Registry.CurrentUser.OpenSubKey(ShellIntegration.DefaultKey)) realKey = key != null;
            string other = Path.Combine(f.Root, "another-install");
            string app = kind == "owned" ? Path.Combine(f.Install, "MyCapture.exe") : Path.Combine(other, "MyCapture.exe");
            string uninstaller = Path.Combine(f.Install, "MyCaptureUninstall.exe");
            Directory.CreateDirectory(f.Menu);
            Fixture.Sta(() =>
            {
                ShellLink.Write(Path.Combine(f.Menu, "MyCapture.lnk"), app, "", f.Install, app, "MyCapture");
                ShellLink.Write(Path.Combine(f.Menu, "Uninstall MyCapture.lnk"), uninstaller, "/uninstall", f.Install, app, "Uninstall MyCapture");
                return 0;
            });
            using (var key = Registry.CurrentUser.CreateSubKey(f.Key))
            { key.SetValue("DisplayName", "MyCapture"); key.SetValue("InstallLocation", kind == "owned" ? f.Install : other); }
            string log = f.Uninstall();
            Assert.Contains("MyCapture removal completed", log); Assert.False(Directory.Exists(f.Install));
            Assert.Contains("Removed owned shortcut: Uninstall MyCapture.lnk", log);
            Assert.False(File.Exists(Path.Combine(f.Menu, "Uninstall MyCapture.lnk")));
            if (kind == "owned")
            {
                Assert.Contains("Removed owned shortcut: MyCapture.lnk", log); Assert.Contains("Removed the owned uninstall registration.", log);
                Assert.False(Directory.Exists(f.Menu));
                using (var key = Registry.CurrentUser.OpenSubKey(f.Key)) Assert.Null(key);
            }
            else
            {
                Assert.Contains("Preserved modified shortcut: MyCapture.lnk", log);
                Assert.Contains("Preserved an uninstall registration that does not belong to this installation.", log);
                Assert.True(PathSafety.Equal(app, Fixture.Sta(() => ShellLink.Target(Path.Combine(f.Menu, "MyCapture.lnk")))));
                using (var key = Registry.CurrentUser.OpenSubKey(f.Key)) Assert.Equal(other, key!.GetValue("InstallLocation"));
            }
            Assert.DoesNotContain("Could not", log); Assert.DoesNotContain("[ERROR]", log);
            // Recorded test targets keep the real Start Menu folder and HKCU registration untouched.
            Assert.Equal(realMenu, Directory.Exists(ShellIntegration.DefaultMenu));
            using (var key = Registry.CurrentUser.OpenSubKey(ShellIntegration.DefaultKey)) Assert.Equal(realKey, key != null);
        }
    }
    private static void CreateJunction(string link, string destination)
    {
        Directory.CreateDirectory(link);
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + destination), print = Encoding.Unicode.GetBytes(destination);
        byte[] buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        Array.Copy(BitConverter.GetBytes(0xA0000003u), 0, buffer, 0, 4);
        Array.Copy(BitConverter.GetBytes((ushort)(buffer.Length - 8)), 0, buffer, 4, 2);
        Array.Copy(BitConverter.GetBytes((ushort)substitute.Length), 0, buffer, 10, 2);
        Array.Copy(BitConverter.GetBytes((ushort)(substitute.Length + 2)), 0, buffer, 12, 2);
        Array.Copy(BitConverter.GetBytes((ushort)print.Length), 0, buffer, 14, 2);
        Array.Copy(substitute, 0, buffer, 16, substitute.Length); Array.Copy(print, 0, buffer, 18 + substitute.Length, print.Length);
        using (var handle = CreateFile(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
        {
            Assert.False(handle.IsInvalid);
            Assert.True(DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero), new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle file, uint control, byte[] input, int inputLength, IntPtr output, int outputLength, out int bytes, IntPtr overlapped);
}
