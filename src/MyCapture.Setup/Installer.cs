using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MyCapture.Setup;

public sealed class SetupLog
{
    public string? Path { get; private set; }
    public bool Quiet { get; set; }
    public SetupLog(string requested, string directory, bool quiet, bool uninstall, bool append = false)
    {
        Quiet = quiet;
        string prefix = uninstall ? "MyCapture-Uninstall-" : "MyCapture-Install-";
        string candidate = requested;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            string basis;
            try { basis = System.IO.Path.GetTempPath(); } catch { basis = directory; }
            candidate = System.IO.Path.Combine(basis, prefix + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Process.GetCurrentProcess().Id + ".log");
        }
        try { Initialize(candidate, append); }
        catch
        {
            if (!uninstall) try { Initialize(System.IO.Path.Combine(directory, prefix + Process.GetCurrentProcess().Id + ".log"), false); } catch { }
        }
    }
    private void Initialize(string path, bool append)
    {
        path = System.IO.Path.GetFullPath(path); PathSafety.NotLink(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        if (!append) File.WriteAllText(path, "", new UTF8Encoding(false));
        Path = path;
    }
    public void Write(string level, string message)
    {
        string line = DateTime.Now.ToString("O") + " [" + level.ToUpperInvariant() + "] " + message;
        if (!Quiet) Console.WriteLine(line);
        if (Path != null) try { File.AppendAllText(Path, line + Environment.NewLine, new UTF8Encoding(false)); } catch { }
    }
}

public sealed class OperationMutex : IDisposable
{
    public const string Name = @"Local\MyCapture.InstallOrUninstall";
    private readonly Mutex mutex = new Mutex(false, Name);
    private bool held;
    public OperationMutex(int timeout = 0, int failureCode = 18)
    {
        try { held = mutex.WaitOne(timeout, false); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) { mutex.Dispose(); throw new SetupException(failureCode, "Another MyCapture install or uninstall operation is already running."); }
    }
    public void Dispose() { if (held) { held = false; mutex.ReleaseMutex(); mutex.Dispose(); } }
}

public sealed class Installer
{
    private readonly Options options;
    private readonly string directory;
    private readonly SetupLog log;
    private string? stage, backup, installRoot, receipt;
    private UpdaterSession? update;
    private bool movedOld, committed;
    private bool migrateLegacyShell;
    private int warnings;
    public Installer(Options options, string directory, SetupLog log) { this.options = options; this.directory = directory; this.log = log; }
    private void Info(string message) => log.Write("INFO", message);
    private void Warning(string message) { warnings++; log.Write("WARN", message); }
    public int Run()
    {
        int code = 0;
        try
        {
            using (new OperationMutex()) Install();
        }
        catch (Exception e)
        {
            code = ExitCodes.FromException(e);
            string message = "Installation failed (exit " + code + "): " + e.Message;
            log.Write("ERROR", message);
            if (backup != null && Directory.Exists(backup)) log.Write("ERROR", "Rollback backup retained at " + backup);
            if (!options.Quiet) WindowsHost.Popup(false, message + "\r\nLog: " + log.Path);
        }
        finally
        {
            if (stage != null && Directory.Exists(stage))
                try { FileRemoval.DeleteDirectoryWithRetry(stage); } catch { log.Write("WARN", "Could not remove staging directory: " + stage); }
        }
        if (update != null && receipt != null)
        {
            try { new InstallReceipt { Token = update.Token, Version = update.Version, ExitCode = code, InstallRoot = installRoot, LogPath = log.Path }.Write(receipt); }
            catch (Exception e) { log.Write("ERROR", "Could not write updater receipt: " + e.Message); }
        }
        return code;
    }
    private void Install()
    {
        Info("MyCapture installer starting. PID=" + Process.GetCurrentProcess().Id + "; executable=" + Program.Executable);
        if (!File.Exists(options.Payload) || !File.Exists(options.Manifest)) throw new SetupException(14, "Payload ZIP or installer manifest is missing.");
        var manifest = PackageManifest.Read(options.Manifest);
        ValidateUpdate(manifest);
        WindowsHost.RequireSupported(manifest);
        manifest.VerifyBootstrap(directory);
        if (!manifest.Payload.Matches(options.Payload)) throw new SetupException(14, "Payload ZIP SHA-256 or size does not match the installer manifest.");
        Info("Payload cryptographic verification passed: " + manifest.Payload.Sha256 + ".");
        installRoot = PathSafety.InstallRoot(options.InstallRoot, manifest);
        string parent = Path.GetDirectoryName(installRoot)!;
        PathSafety.ParentWritable(parent);
        try { PathSafety.FreeSpace(new DriveInfo(Path.GetPathRoot(parent)).AvailableFreeSpace, manifest.TotalExpandedBytes, options.MinimumFreeBytes); }
        catch (SetupException) { throw; }
        catch (Exception e) { throw new SetupException(13, "Unable to query free space: " + e.Message); }
        string transaction = Guid.NewGuid().ToString("N");
        stage = Path.Combine(parent, ".MyCapture.stage." + transaction);
        backup = Path.Combine(parent, ".MyCapture.backup." + transaction);
        if (Directory.Exists(stage) || Directory.Exists(backup) || File.Exists(stage) || File.Exists(backup)) throw new SetupException(12, "Transaction staging path already exists.");
        Payload.Extract(options.Payload, manifest, stage);
        Payload.Application(stage, manifest);
        AddInstalledFiles(manifest, stage);
        Info("Payload extracted and verified in transaction staging: " + manifest.FileCount + " files.");
        if (options.VerifyOnly) Info("VerifyOnly completed; no installation state was changed.");
        else
        {
            FileRemoval.RemoveStaleWorkers(Info);
            AppProcesses.Stop(Path.Combine(installRoot, "MyCapture.exe"), update != null, false, Info);
            // install.ps1 preserves unmanifested files only for updater sessions. A direct
            // reinstall replaces the whole root and then deletes the previous tree.
            if (update != null) PathSafety.PreserveUserFiles(installRoot, stage);
            // Recheck ancestors and ownership immediately before the transactional moves.
            PathSafety.InstallRoot(installRoot, manifest);
            try
            {
                if (Directory.Exists(installRoot)) { Move(installRoot, backup); movedOld = true; Info("Existing installation moved to rollback backup: " + backup); }
                if (options.TestFault == "AfterBackup") throw new SetupException(17, "Injected test fault after backup.");
                Move(stage, installRoot); stage = null; committed = true;
                if (options.TestFault == "AfterCommit") throw new SetupException(17, "Injected test fault after commit.");
                Payload.VerifyInventory(installRoot, manifest); Payload.Application(installRoot, manifest);
                Info("Transactional payload commit verified at " + installRoot + ".");
            }
            catch (Exception original)
            {
                try { Rollback(); }
                catch (Exception e) { throw new SetupException(17, "Installation and rollback failed. Backup retained at " + backup + ". " + e.Message); }
                if (original is SetupException) throw;
                throw new SetupException(17, "Installation transaction failed: " + original.Message);
            }
            if (!options.NoShellIntegration) ShellIntegration.Install(installRoot, manifest, Warning);
            else if (migrateLegacyShell) ShellIntegration.RepairLegacyTargets(installRoot, manifest, Warning);
            else Info("Shell integration was intentionally skipped.");
            if (movedOld && Directory.Exists(backup))
            {
                try { FileRemoval.DeleteDirectoryWithRetry(backup); movedOld = false; }
                catch { Warning("Installation succeeded, but the old rollback directory could not be removed: " + backup); }
            }
        }
        string summary = "MyCapture " + manifest.Version + (options.VerifyOnly ? " package verification succeeded." : " installed successfully to " + installRoot + ".") +
            (warnings == 0 ? "" : " Warnings: " + warnings + ".") + " Log: " + log.Path;
        Info(summary);
        if (!options.Quiet) WindowsHost.Popup(true, summary);
        if (!options.Quiet && !options.VerifyOnly && !options.NoShellIntegration && update == null)
            try
            {
                using (Process.Start(LaunchInfo(installRoot))) { }
                Info("Launched MyCapture after successful interactive installation.");
            }
            catch (Exception e) { Warning("Installation succeeded, but MyCapture could not start. Open MyCapture from the Start Menu. " + e.Message); }
    }
    /// <summary>
    /// install.ps1 used Start-Process -WindowStyle Hidden, which is a shell launch with SW_HIDE.
    /// .NET Framework applies WindowStyle only to shell launches, so UseShellExecute must stay true.
    /// </summary>
    public static ProcessStartInfo LaunchInfo(string root) =>
        new ProcessStartInfo(Path.Combine(root, "MyCapture.exe")) { WorkingDirectory = root, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
    private void AddInstalledFiles(PackageManifest manifest, string destination)
    {
        var records = manifest.Files.ToList();
        foreach (var pair in new[] { new[] { "MyCaptureSetup.exe", "MyCaptureUninstall.exe" }, new[] { "MyCaptureSetup.exe.config", "MyCaptureUninstall.exe.config" }, new[] { "SETUP-HELP.txt", "SETUP-HELP.txt" } })
        {
            if (records.Any(f => string.Equals(f.Path, pair[1], StringComparison.OrdinalIgnoreCase))) throw new SetupException(14, "Payload uses a reserved installer file: " + pair[1]);
            string target = Path.Combine(destination, pair[1]); File.Copy(Path.Combine(directory, pair[0]), target, false);
            records.Add(FileRecord.Create(target, pair[1]));
        }
        if (records.Any(f => string.Equals(f.Path, "install-manifest.json", StringComparison.OrdinalIgnoreCase))) throw new SetupException(14, "Payload uses the ownership marker name.");
        manifest.Files = records.ToArray(); manifest.FileCount = records.Count; manifest.TotalExpandedBytes = records.Sum(f => f.Bytes);
        manifest.ShellIntegration = !options.NoShellIntegration;
        manifest.TestRegistryKey = options.TestRegistryKey; manifest.TestStartMenu = options.TestStartMenu;
        manifest.InstallationId = Guid.NewGuid().ToString("N");
        // An update that skips registration must retain the previous uninstaller's exact
        // shell ownership, including isolated test targets. It must not orphan those entries.
        string oldMarker = Path.Combine(installRoot!, "install-manifest.json");
        if (options.NoShellIntegration && File.Exists(oldMarker))
        {
            var old = PathSafety.PreviousInventory(installRoot!);
            if (old?.ShellIntegration == true)
            {
                Options.ValidateTestTargets(old.TestRegistryKey, old.TestStartMenu);
                manifest.ShellIntegration = true; manifest.TestRegistryKey = old.TestRegistryKey; manifest.TestStartMenu = old.TestStartMenu;
            }
            if (old != null && old.BootstrapFiles.Any(f => f.Path == "uninstall.cmd"))
            {
                // NoShellIntegration creates no entries. Repair only proven old pointers that
                // would otherwise refer to a removed script after an in-app custom-root update.
                migrateLegacyShell = ShellIntegration.LegacyTargetsOwned(installRoot!, manifest, Warning);
                if (migrateLegacyShell) manifest.ShellIntegration = true;
            }
        }
        manifest.ValidateInventory(true);
        Json.Write(Path.Combine(destination, "install-manifest.json"), manifest);
    }
    private void ValidateUpdate(PackageManifest manifest)
    {
        string? sessionPath = Environment.GetEnvironmentVariable("MYCAPTURE_UPDATE_SESSION");
        if (string.IsNullOrWhiteSpace(sessionPath)) return;
        sessionPath = Path.GetFullPath(sessionPath);
        string sessionDirectory = Path.GetDirectoryName(sessionPath)!;
        string expected = PathSafety.DefaultInstallRoot;
        string updateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"MyCapture\Updates");
        if (!string.IsNullOrWhiteSpace(options.InstallRoot))
        {
            if (!options.NoShellIntegration) throw new SetupException(12, "Custom updater roots require explicit NoShellIntegration.");
            expected = PathSafety.InstallRoot(options.InstallRoot, manifest);
            updateRoot = Path.Combine(Path.GetDirectoryName(expected)!, ".MyCapture.updates");
        }
        if (!PathSafety.Equal(Path.GetDirectoryName(sessionDirectory), updateRoot) ||
            !Regex.IsMatch(Path.GetFileName(sessionDirectory), @"\Aupdate-\d+\.\d+\.\d+-[a-f0-9]{32}\z") || Path.GetFileName(sessionPath) != "update-session.json")
            throw new SetupException(12, "Invalid updater session path.");
        PathSafety.NoLinks(sessionPath);
        var candidate = Json.Read<UpdaterSession>(sessionPath);
        if (candidate.Token == null || !Regex.IsMatch(candidate.Token, @"\A[a-f0-9]{32}\z") || candidate.Version != manifest.Version)
            throw new SetupException(14, "Invalid updater session identity.");
        receipt = Path.Combine(sessionDirectory, "install-result.json");
        if (File.Exists(receipt) || Directory.Exists(receipt)) throw new SetupException(12, "Updater receipt already exists.");
        update = candidate; options.Quiet = true; log.Quiet = true;
        if (candidate.TargetMode != null)
        {
            if (candidate.TargetMode == "OwnedInstall")
            {
                string root = PathSafety.InstallRoot(candidate.InstallRoot, manifest);
                if (!PathSafety.Equal(root, candidate.SourceRoot)) throw new SetupException(12, "Update target does not match its owned source.");
                PathSafety.OwnedUpdateSource(root); expected = root;
                if (!PathSafety.Equal(root, PathSafety.DefaultInstallRoot)) options.NoShellIntegration = true;
            }
            else if (candidate.TargetMode != "InstallToDefault") throw new SetupException(12, "Unknown updater target mode.");
        }
        PathSafety.UpdateBoundary(expected);
        if (!string.Equals(candidate.InstallRoot, expected, StringComparison.OrdinalIgnoreCase)) throw new SetupException(14, "Updater session does not match this package.");
        options.InstallRoot = candidate.InstallRoot;
        if (candidate.ParentId <= 0) throw new SetupException(14, "Invalid updater parent ID.");
        try
        {
            using (Process.GetProcessById(candidate.ParentId)) throw new SetupException(15, "Updater parent is still running.");
        }
        catch (ArgumentException) { }
    }
    private static void Move(string source, string destination)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { PathSafety.NotLink(source); PathSafety.NotLink(destination); Directory.Move(source, destination); return; }
            catch when (attempt < 6) { Thread.Sleep(150 * attempt); }
        }
    }
    private void Rollback()
    {
        log.Write("WARN", "Rolling back the installation transaction.");
        if (committed && Directory.Exists(installRoot)) { FileRemoval.DeleteDirectoryWithRetry(installRoot!); committed = false; }
        if (movedOld && Directory.Exists(backup)) { Move(backup!, installRoot!); movedOld = false; }
    }
}
