using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace MyCapture.Setup;

/// <summary>Recoverable recycling with the retry schedules of the PowerShell installer at 4769935.</summary>
public static class FileRemoval
{
    public const string WorkerPrefix = ".MyCapture-uninstall-cleanup-";
    public static readonly string[] WorkerFiles = { "MyCaptureUninstall.exe", "MyCaptureUninstall.exe.config", "SETUP-HELP.txt", "cleanup.json" };
    public static bool IsWorkerName(string name) => Regex.IsMatch(name, @"\A\.MyCapture-uninstall-cleanup-[a-f0-9]{32}\z");
    // install.ps1 Remove-DirectoryWithRetry: six attempts, 180 ms x attempt after each failure.
    public static int InstallRetryDelay(int attempt) => 180 * attempt;
    // uninstall-cleanup.ps1 Remove-Target: thirty attempts, min(1000, 150 + 75 x attempt) ms.
    public static int UninstallRetryDelay(int attempt) => Math.Min(1000, 150 + attempt * 75);

    /// <summary>Recycles an owned directory; read-only entries are cleared and links are never traversed.</summary>
    public static void DeleteTree(string path)
    {
        var root = new DirectoryInfo(path);
        if (!root.Exists) return;
        Prepare(root);
        global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteDirectory(root.FullName, recursive: true);
    }
    public static void DeleteDirectoryWithRetry(string path, Action<int>? sleep = null)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            if (!Directory.Exists(path)) return;
            try { DeleteTree(path); return; }
            catch (Exception e) { last = e; (sleep ?? Thread.Sleep)(InstallRetryDelay(attempt)); }
        }
        throw last!;
    }
    /// <summary>Returns false when the directory remains after every attempt; each failure is reported.</summary>
    public static bool RemoveTarget(string root, Action<string> warning, Action<int>? sleep = null)
    {
        for (int attempt = 1; attempt <= 30; attempt++)
        {
            if (!Directory.Exists(root)) return true;
            try { DeleteTree(root); }
            catch (Exception e) { warning("Removal attempt " + attempt + " failed: " + e.Message); }
            if (!Directory.Exists(root)) return true;
            (sleep ?? Thread.Sleep)(UninstallRetryDelay(attempt));
        }
        return !Directory.Exists(root);
    }
    /// <summary>
    /// A running cleanup worker cannot delete its own image, so its private TEMP folder keeps only
    /// MyCaptureUninstall.exe. Later setup and uninstall runs delete such folders after the worker exits.
    /// Only exact worker names directly under TEMP that contain nothing but worker files qualify.
    /// The executable is deleted first: a running worker image refuses deletion and the folder is kept intact.
    /// </summary>
    public static void RemoveStaleWorkers(Action<string> info)
    {
        string[] candidates;
        try
        {
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
            candidates = Directory.GetDirectories(temp, WorkerPrefix + "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception e) { info("Could not inspect previous cleanup worker folders: " + e.Message); return; }
        foreach (string directory in candidates)
        {
            if (!IsWorkerName(Path.GetFileName(directory))) continue;
            try
            {
                PathSafety.NotLink(directory);
                string[] entries = Directory.GetFileSystemEntries(directory);
                if (entries.Any(entry => !File.Exists(entry) || !WorkerFiles.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase)))
                {
                    info("Kept a cleanup worker folder with unexpected contents: " + directory);
                    continue;
                }
                foreach (string entry in entries.OrderBy(entry => string.Equals(Path.GetFileName(entry), WorkerFiles[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                {
                    PathSafety.NotLink(entry);
                    File.SetAttributes(entry, FileAttributes.Normal);
                    global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(entry);
                }
                global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteDirectory(directory, false);
                info("Removed a previous cleanup worker folder: " + directory);
            }
            catch (Exception e) { info("Kept a cleanup worker folder that is in use or inaccessible: " + directory + " (" + e.Message + ")"); }
        }
    }
    private static void Prepare(DirectoryInfo directory)
    {
        // A link (including the root) is never traversed or changed; recycling moves only the link.
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return;
        if ((directory.Attributes & FileAttributes.ReadOnly) != 0) directory.Attributes &= ~FileAttributes.ReadOnly;
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo child)
            {
                // .NET Framework's recursive delete reports a nested junction as access denied
                // (it tries to unmount it as a volume), so remove nested directory links first.
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteDirectory(child.FullName);
                else Prepare(child);
            }
            else if ((entry.Attributes & FileAttributes.ReadOnly) != 0) entry.Attributes &= ~FileAttributes.ReadOnly;
        }
    }
}
