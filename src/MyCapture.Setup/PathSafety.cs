using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MyCapture.Setup;

public static class PathSafety
{
    public static string DefaultInstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\MyCapture");
    public static string PackagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith("/", StringComparison.Ordinal) || path.Contains(':'))
            throw new SetupException(14, "Payload contains an empty, absolute, alternate-stream or backslash path: " + path);
        foreach (string segment in path.Split('/'))
            if (string.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) ||
                segment.EndsWith(" ", StringComparison.Ordinal) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Regex.IsMatch(segment, @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)", RegexOptions.IgnoreCase))
                throw new SetupException(14, "Payload contains an unsafe path segment: " + path);
        return path;
    }
    public static bool Equal(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    /// <summary>
    /// Every component, including all ancestors. install.ps1 applied this walk only to updater
    /// sessions (Assert-UpdateRootBoundary and the session path); direct setup must not use it,
    /// so a profile moved behind a junction still installs.
    /// </summary>
    public static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            NotLink(current);
    }
    /// <summary>The path itself only, like install.ps1 Get-SafeInstallRoot; ancestors are not inspected.</summary>
    public static void NotLink(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new SetupException(12, "Path contains a junction or symbolic link: " + full);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    /// <summary>Components from root (inclusive) down to path; nothing above root is inspected.</summary>
    public static void NoLinksBelow(string root, string path)
    {
        string top = Path.GetFullPath(root).TrimEnd('\\');
        string full = Path.GetFullPath(path).TrimEnd('\\');
        if (!Equal(full, top) && !full.StartsWith(top + "\\", StringComparison.OrdinalIgnoreCase))
            throw new SetupException(12, "Path escapes its root: " + full);
        for (string? current = full; current != null && current.Length >= top.Length; current = Path.GetDirectoryName(current))
            NotLink(current);
    }
    public static string InstallRoot(string requested, PackageManifest manifest)
    {
        string root = string.IsNullOrWhiteSpace(requested) ? DefaultInstallRoot : requested;
        string full = LocalRoot(root);
        int longest = Math.Max("MyCaptureUninstall.exe.config".Length, manifest.Files.Max(f => f.Path.Length));
        if (full.Length + 1 + longest > 240) throw new SetupException(12, "InstallRoot is too deep; choose a shorter local path.");
        NotLink(full);
        if (File.Exists(full)) throw new SetupException(12, "InstallRoot is a file.");
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() && !IsOwned(full))
            throw new SetupException(20, "InstallRoot contains files not proven to belong to MyCapture: " + full);
        return full;
    }
    public static string LocalRoot(string root)
    {
        if (root.StartsWith(@"\\?\", StringComparison.Ordinal) || root.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new SetupException(12, "Extended/device install paths are not accepted.");
        string full;
        try { full = Path.GetFullPath(root).TrimEnd('\\'); }
        catch (Exception e) { throw new SetupException(12, "Invalid install path: " + e.Message); }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new SetupException(12, "UNC/network install roots are not supported.");
        if (Equal(full, Path.GetPathRoot(full))) throw new SetupException(12, "A drive root cannot be used as InstallRoot.");
        if (full.IndexOf(':', 2) >= 0) throw new SetupException(12, "Alternate-stream install paths are not accepted.");
        return full;
    }
    public static bool IsOwned(string root)
    {
        // install.ps1 Get-SafeInstallRoot read the marker and DLL without link checks.
        string marker = Path.Combine(root, "install-manifest.json");
        try { if (File.Exists(marker) && Json.Read<OwnershipMarker>(marker).Product == "MyCapture") return true; }
        catch { }
        string dll = Path.Combine(root, "MyCapture.dll");
        try { return File.Exists(dll) && FileVersionInfo.GetVersionInfo(dll).ProductName == "MyCapture"; }
        catch { return false; }
    }
    /// <summary>Updater boundary folders as (path, system). Tests substitute TEMP folders here.</summary>
    internal static Func<IEnumerable<KeyValuePair<string, bool>>> BoundaryFolders { get; set; } = WindowsBoundaryFolders;
    private static IEnumerable<KeyValuePair<string, bool>> WindowsBoundaryFolders()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments })
            yield return new KeyValuePair<string, bool>(Environment.GetFolderPath(folder),
                folder == Environment.SpecialFolder.Windows || folder == Environment.SpecialFolder.ProgramFiles || folder == Environment.SpecialFolder.ProgramFilesX86);
    }
    /// <summary>install.ps1 Assert-UpdateRootBoundary. Applies to updater sessions only.</summary>
    public static void UpdateBoundary(string root)
    {
        string full = LocalRoot(root);
        foreach (var folder in BoundaryFolders())
        {
            string protectedPath = folder.Key.TrimEnd('\\');
            if (protectedPath.Length == 0) continue;
            bool system = folder.Value;
            if (Equal(full, protectedPath) || protectedPath.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase) ||
                (system && full.StartsWith(protectedPath + "\\", StringComparison.OrdinalIgnoreCase)))
                throw new SetupException(12, "Per-user updater cannot target a system or user-data root.");
        }
        NoLinks(full);
    }
    public static void OwnedUpdateSource(string root)
    {
        UpdateBoundary(root);
        string marker = Path.Combine(root, "install-manifest.json");
        NoLinks(marker);
        if (!File.Exists(marker)) throw new SetupException(20, "An in-place update requires an owned installation manifest.");
        var old = PackageManifest.Read(marker, true);
        foreach (string name in new[] { "MyCapture.exe", "MyCapture.dll" })
        {
            string path = Path.Combine(root, name);
            NoLinks(path);
            var entries = old.Files.Where(f => f.Path == name).ToArray();
            if (entries.Length != 1 || !entries[0].Matches(path)) throw new SetupException(14, "Installed application does not match its ownership manifest.");
        }
    }
    public static void PreserveUserFiles(string oldRoot, string stage)
    {
        if (!Directory.Exists(oldRoot)) return;
        NoLinks(oldRoot); NoLinks(stage);
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // install.ps1 Preserve-UserInstallFiles: an existing marker must be a valid inventory
        // (exit 14 otherwise); without a marker only the installer's own names are owned.
        string marker = Path.Combine(oldRoot, "install-manifest.json"); NoLinks(marker);
        if (File.Exists(marker)) foreach (var file in PackageManifest.Read(marker, true).Files) owned.Add(file.Path.Replace('/', '\\'));
        foreach (string name in new[] { "install-manifest.json", "uninstall.ps1", "uninstall.cmd", "uninstall-cleanup.ps1" }) owned.Add(name);
        var pending = new Stack<string>(); pending.Push(oldRoot);
        while (pending.Count != 0)
            foreach (string item in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                NoLinks(item);
                string relative = item.Substring(oldRoot.TrimEnd('\\').Length + 1);
                string destination = Path.Combine(stage, relative);
                if (Directory.Exists(item))
                {
                    if (File.Exists(destination)) throw new SetupException(20, "A user directory conflicts with the new package: " + relative);
                    Directory.CreateDirectory(destination); pending.Push(item);
                }
                else if (!owned.Contains(relative))
                {
                    if (File.Exists(destination) || Directory.Exists(destination)) throw new SetupException(20, "A user file conflicts with the new package: " + relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(item, destination, false);
                }
            }
    }
    public static PackageManifest? PreviousInventory(string root)
    {
        string marker = Path.Combine(root, "install-manifest.json"); NotLink(marker);
        if (!File.Exists(marker)) return null;
        try { return PackageManifest.Read(marker, true); }
        catch (SetupException) when (IsOwned(root))
        {
            // Direct reinstall historically accepts a Product marker or legacy DLL. Such a root
            // has no previous shell ownership to carry forward.
            return null;
        }
    }
    public static void ParentWritable(string parent)
    {
        try
        {
            Directory.CreateDirectory(parent);
            string probe = Path.Combine(parent, ".mycapture-write-probe-" + Guid.NewGuid().ToString("N"));
            using (var stream = File.Open(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.WriteByte(1);
            File.Delete(probe);
        }
        catch (Exception e) { throw new SetupException(12, "Install location is not writable: " + parent + " (" + e.Message + ")"); }
    }
    public static void FreeSpace(long available, long expanded, long requested)
    {
        long required = Math.Max(checked(expanded + 67108864L), requested);
        if (available < required) throw new SetupException(13, "Insufficient free space: need " + required + ", available " + available + " bytes.");
    }
    [System.Runtime.Serialization.DataContract]
    private sealed class OwnershipMarker
    {
        [System.Runtime.Serialization.DataMember] public string? Product { get; set; }
    }
}
