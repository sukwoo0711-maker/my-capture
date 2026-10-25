using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MyCapture.Setup;

public static class Uninstaller
{
    [DataContract]
    private sealed class CleanupRequest
    {
        [DataMember(IsRequired = true)] public string Root { get; set; } = "";
        [DataMember(IsRequired = true)] public string ManifestHash { get; set; } = "";
        [DataMember(IsRequired = true)] public int ParentId { get; set; }
        [DataMember(IsRequired = true)] public long ParentStarted { get; set; }
        [DataMember(IsRequired = true)] public string Log { get; set; } = "";
    }
    public static PackageManifest Verify(string root)
    {
        try
        {
            // uninstall.ps1 Assert-VerifiedInstallation: drive root, the root itself as a link, the
            // manifest's presence, validity and product, and the installed uninstaller's integrity.
            // The updater boundary and ancestor links are deliberately not checked here.
            string full = Path.GetFullPath(root).TrimEnd('\\');
            if (string.Equals(full, Path.GetPathRoot(full)!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Refusing to uninstall from a drive root.");
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Installation directory is missing: " + full);
            PathSafety.NotLink(full);
            string marker = Path.Combine(full, "install-manifest.json");
            if (!File.Exists(marker)) throw new IOException("Installed manifest is missing; recursive deletion was refused.");
            var manifest = PackageManifest.Read(marker, true);
            Options.ValidateTestTargets(manifest.TestRegistryKey, manifest.TestStartMenu);
            foreach (string name in new[] { "MyCaptureUninstall.exe", "MyCaptureUninstall.exe.config", "SETUP-HELP.txt" })
            {
                var entries = manifest.Files.Where(f => f.Path == name).ToArray();
                if (entries.Length != 1 || !entries[0].Matches(Path.Combine(full, name))) throw new IOException("Uninstaller integrity check failed: " + name);
            }
            return manifest;
        }
        catch (Exception e) { throw new SetupException(30, "Invalid installation; removal was refused: " + e.Message); }
    }
    public static int Run(Options options, string directory, SetupLog log, ISetupProgress? progress = null)
    {
        if (options.CleanupRequest != null) return Cleanup(options.CleanupRequest, directory, log);
        progress = progress ?? NullSetupProgress.Instance;
        if (options.UninstallUiRequest != null) return Cleanup(options.UninstallUiRequest, directory, log, progress, !options.Quiet);
        try
        {
            using (new OperationMutex(0, 31))
            {
                progress.Stage(SetupStage.VerifyingInstallation);
                string root = Path.GetFullPath(directory).TrimEnd('\\');
                var manifest = Verify(root);
                progress.Version(manifest.Version);
                if (!PathSafety.Equal(Program.Executable, Path.Combine(root, "MyCaptureUninstall.exe"))) throw new SetupException(30, "Run the installed MyCaptureUninstall.exe /uninstall.");
                if (options.TestRegistryKey != null && (options.TestRegistryKey != manifest.TestRegistryKey || !PathSafety.Equal(options.TestStartMenu, manifest.TestStartMenu)))
                    throw new SetupException(30, "Uninstall test targets do not match the installed ownership manifest.");
                progress.Stage(SetupStage.StoppingApplication);
                AppProcesses.Stop(Path.Combine(root, "MyCapture.exe"), false, true, message => log.Write("INFO", message));
                FileRemoval.RemoveStaleWorkers(message => log.Write("INFO", message));
                progress.Stage(SetupStage.PreparingRemoval);
                string workerRoot = Path.Combine(Path.GetTempPath(), FileRemoval.WorkerPrefix + Guid.NewGuid().ToString("N"));
                PathSafety.NotLink(workerRoot);
                var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                Directory.CreateDirectory(workerRoot, security);
                try
                {
                    string copy = Path.Combine(workerRoot, "MyCaptureUninstall.exe");
                    foreach (string name in new[] { "MyCaptureUninstall.exe", "MyCaptureUninstall.exe.config", "SETUP-HELP.txt" })
                    {
                        string source = Path.Combine(root, name), destination = Path.Combine(workerRoot, name);
                        File.Copy(source, destination, false);
                        if (!manifest.Files.Single(f => f.Path == name).Matches(destination)) throw new SetupException(30, "Temporary cleanup worker failed its integrity check.");
                    }
                    string requestPath = Path.Combine(workerRoot, "cleanup.json");
                    using (var parent = Process.GetCurrentProcess())
                    {
                        var request = new CleanupRequest { Root = root, ParentId = parent.Id, ParentStarted = parent.StartTime.ToUniversalTime().Ticks,
                            ManifestHash = FileRecord.Hash(Path.Combine(root, "install-manifest.json")), Log = log.Path ?? "" };
                        using (var stream = File.Open(requestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        { byte[] bytes = Json.Bytes(request); stream.Write(bytes, 0, bytes.Length); }
                    }
                    string arguments = options.Quiet ? "--cleanup-request " + Program.Quote(requestPath) :
                        "--uninstall-ui " + Program.Quote(requestPath) + (log.Path == null ? "" : " --log " + Program.Quote(log.Path));
                    using (var worker = Process.Start(new ProcessStartInfo(copy, arguments)
                        { WorkingDirectory = workerRoot, UseShellExecute = false, CreateNoWindow = true,
                            WindowStyle = options.Quiet ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal }))
                        log.Write("INFO", "Deferred cleanup worker started (PID " + worker.Id + ").");
                }
                catch (Exception e)
                {
                    try { FileRemoval.DeleteTree(workerRoot); } catch { }
                    if (e is SetupException) throw;
                    throw new SetupException(34, "Unable to launch deferred cleanup: " + e.Message);
                }
            }
            string message = "MyCapture removal is scheduled. Captures and settings are preserved. Log: " + log.Path;
            log.Write("INFO", message);
            return 0;
        }
        catch (Exception e)
        {
            int code = ExitCodes.FromException(e, true);
            string message = "Uninstall failed (exit " + code + "): " + e.Message;
            log.Write("ERROR", message);
            return code;
        }
    }
    private static int Cleanup(string requestPath, string directory, SetupLog initialLog, ISetupProgress? progress = null, bool interactive = false)
    {
        SetupLog log = initialLog;
        progress = progress ?? NullSetupProgress.Instance;
        bool privateWorker = false;
        try
        {
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
            directory = Path.GetFullPath(directory).TrimEnd('\\');
            if (!PathSafety.Equal(Path.GetDirectoryName(directory), temp) ||
                !FileRemoval.IsWorkerName(Path.GetFileName(directory)) ||
                !PathSafety.Equal(requestPath, Path.Combine(directory, "cleanup.json"))) throw new SetupException(30, "Invalid private cleanup request.");
            // Like the temporary uninstall-cleanup.ps1 copy, the worker always removes its own files.
            privateWorker = PathSafety.Equal(Program.Executable, Path.Combine(directory, "MyCaptureUninstall.exe"));
            PathSafety.NotLink(directory); PathSafety.NotLink(requestPath);
            progress.Stage(SetupStage.VerifyingInstallation);
            var request = Json.Read<CleanupRequest>(requestPath);
            var manifest = Verify(request.Root);
            progress.Version(manifest.Version);
            if (FileRecord.Hash(Path.Combine(request.Root, "install-manifest.json")) != request.ManifestHash ||
                FileRecord.Hash(Program.Executable) != FileRecord.Hash(Path.Combine(request.Root, "MyCaptureUninstall.exe")))
                throw new SetupException(30, "Cleanup ownership changed.");
            log = new SetupLog(request.Log, directory, true, true, true);
            progress.Stage(SetupStage.PreparingRemoval);
            try
            {
                using (var parent = Process.GetProcessById(request.ParentId))
                    if (parent.StartTime.ToUniversalTime().Ticks == request.ParentStarted && !parent.WaitForExit(300000)) throw new IOException("Uninstaller parent did not exit.");
            }
            catch (ArgumentException) { }
            using (new OperationMutex(300000, 31))
            {
                Verify(request.Root);
                if (FileRecord.Hash(Path.Combine(request.Root, "install-manifest.json")) != request.ManifestHash) throw new SetupException(30, "Cleanup target changed while waiting.");
                // Never traverse a link added under the installation after it was installed.
                AssertTreeNoLinks(request.Root);
                progress.Stage(SetupStage.RemovingFiles);
                if (!FileRemoval.RemoveTarget(request.Root, message => log.Write("WARN", message)))
                    throw new IOException("Installation directory remained after all removal retries: " + request.Root);
                // Shell and startup records are removed only after the payload directory is gone. If
                // deletion fails, Apps & Features remains available for a retry.
                progress.Stage(SetupStage.RemovingRegistration);
                ShellIntegration.RemoveOwnedStartupValue(request.Root, manifest, message => log.Write("INFO", message), message => log.Write("WARN", message));
                ShellIntegration.Remove(request.Root, manifest, message => log.Write("WARN", message), message => log.Write("INFO", message));
                log.Write("INFO", "MyCapture removal completed. Captures and settings under APPDATA were not accessed.");
            }
            return 0;
        }
        catch (Exception e)
        {
            log.Write("ERROR", "Deferred uninstall cleanup failed: " + e.Message);
            return interactive ? ExitCodes.FromException(e, true) : 1;
        }
        finally
        {
            if (privateWorker) RemoveOwnWorkerFiles(directory, log);
        }
    }
    private static void RemoveOwnWorkerFiles(string directory, SetupLog log)
    {
        // A running executable cannot delete its own image. Delete the other worker files now;
        // the next setup or uninstall run deletes the remaining folder after this process exits.
        foreach (string name in FileRemoval.WorkerFiles.Skip(1))
        {
            string path = Path.Combine(directory, name);
            try { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(path); } }
            catch (Exception e) { log.Write("WARN", "Could not delete cleanup worker file " + path + ": " + e.Message); }
        }
        log.Write("INFO", "Cleanup worker folder retained until the next MyCapture setup or uninstall run: " + directory);
    }
    private static void AssertTreeNoLinks(string root)
    {
        var pending = new System.Collections.Generic.Stack<string>(); pending.Push(root);
        while (pending.Count != 0)
            foreach (string item in Directory.EnumerateFileSystemEntries(pending.Pop()))
            { PathSafety.NotLink(item); if (Directory.Exists(item)) pending.Push(item); }
    }
}
