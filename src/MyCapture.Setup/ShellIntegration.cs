using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace MyCapture.Setup;

public static class ShellIntegration
{
    public const string DefaultKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MyCapture";
    public static string DefaultMenu => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MyCapture");
    public static bool LegacyTargetsOwned(string root, PackageManifest manifest, Action<string> warning)
    {
        Options.ValidateTestTargets(manifest.TestRegistryKey, manifest.TestStartMenu);
        bool owned = false;
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(manifest.TestRegistryKey ?? DefaultKey))
                owned = LegacyKeyOwned(key, root);
        }
        catch (Exception e) { warning("Could not inspect legacy uninstall registration: " + e.Message); }
        try
        {
            string menu = manifest.TestStartMenu ?? DefaultMenu; PathSafety.NotLink(menu);
            foreach (string name in new[] { "MyCapture.lnk", "Uninstall MyCapture.lnk" })
            {
                string link = Path.Combine(menu, name); PathSafety.NotLink(link);
                if (File.Exists(link) && PathSafety.Equal(ShellLink.Target(link), Path.Combine(root, name == "MyCapture.lnk" ? "MyCapture.exe" : "uninstall.cmd"))) owned = true;
            }
        }
        catch (Exception e) { warning("Could not inspect legacy shortcuts: " + e.Message); }
        return owned;
    }
    public static void RepairLegacyTargets(string root, PackageManifest manifest, Action<string> warning)
    {
        Options.ValidateTestTargets(manifest.TestRegistryKey, manifest.TestStartMenu);
        string uninstall = Path.Combine(root, "MyCaptureUninstall.exe");
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(manifest.TestRegistryKey ?? DefaultKey, true))
                if (LegacyKeyOwned(key, root))
                {
                    key!.SetValue("UninstallString", Program.Quote(uninstall) + " /uninstall");
                    key.SetValue("QuietUninstallString", Program.Quote(uninstall) + " /uninstall /quiet");
                    key.SetValue("DisplayVersion", manifest.Version);
                }
        }
        catch (Exception e) { warning("Could not migrate legacy uninstall registration: " + e.Message); }
        try
        {
            string link = Path.Combine(manifest.TestStartMenu ?? DefaultMenu, "Uninstall MyCapture.lnk"); PathSafety.NotLink(link);
            if (File.Exists(link) && PathSafety.Equal(ShellLink.Target(link), Path.Combine(root, "uninstall.cmd")))
                ShellLink.Write(link, uninstall, "/uninstall", root, Path.Combine(root, "MyCapture.exe"), "Uninstall MyCapture");
        }
        catch (Exception e) { warning("Could not migrate legacy uninstall shortcut: " + e.Message); }
    }
    private static bool LegacyKeyOwned(RegistryKey? key, string root)
    {
        if (key == null || !PathSafety.Equal(Convert.ToString(key.GetValue("InstallLocation")), root)) return false;
        string command = Convert.ToString(key.GetValue("UninstallString"))?.Trim() ?? "";
        string executable;
        if (command.StartsWith("\"", StringComparison.Ordinal))
        { int end = command.IndexOf('"', 1); if (end < 0) return false; executable = command.Substring(1, end - 1); }
        else { int end = command.IndexOf(' '); executable = end < 0 ? command : command.Substring(0, end); }
        return PathSafety.Equal(executable, Path.Combine(root, "uninstall.cmd"));
    }
    public static void Install(string root, PackageManifest manifest, Action<string> warning)
    {
        Options.ValidateTestTargets(manifest.TestRegistryKey, manifest.TestStartMenu);
        string exe = Path.Combine(root, "MyCapture.exe"), uninstall = Path.Combine(root, "MyCaptureUninstall.exe");
        string menu = manifest.TestStartMenu ?? DefaultMenu;
        try
        {
            PathSafety.NotLink(menu); Directory.CreateDirectory(menu);
            ShellLink.Write(Path.Combine(menu, "MyCapture.lnk"), exe, "", root, exe, "MyCapture screen capture and annotation tool");
            ShellLink.Write(Path.Combine(menu, "Uninstall MyCapture.lnk"), uninstall, "/uninstall", root, exe, "Uninstall MyCapture");
        }
        catch (Exception e) { warning("Core files were installed, but Start Menu shortcuts could not be created: " + e.Message); }
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(manifest.TestRegistryKey ?? DefaultKey))
            {
                key.SetValue("DisplayName", "MyCapture"); key.SetValue("DisplayVersion", manifest.Version);
                key.SetValue("Publisher", "MyCapture"); key.SetValue("DisplayIcon", exe); key.SetValue("InstallLocation", root);
                key.SetValue("UninstallString", Program.Quote(uninstall) + " /uninstall");
                key.SetValue("QuietUninstallString", Program.Quote(uninstall) + " /uninstall /quiet");
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
                key.SetValue("EstimatedSize", (int)Math.Ceiling(manifest.TotalExpandedBytes / 1024.0), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }
        catch (Exception e) { warning("Core files were installed, but the uninstall registry entry could not be written: " + e.Message); }
    }
    /// <summary>
    /// uninstall-cleanup.ps1 always attempted this, whatever the installation recorded about shell
    /// integration; only entries proven to point at this root are deleted. Installs with recorded
    /// test targets use only those isolated targets.
    /// </summary>
    public static void Remove(string root, PackageManifest manifest, Action<string> warning, Action<string>? info = null)
    {
        Options.ValidateTestTargets(manifest.TestRegistryKey, manifest.TestStartMenu);
        info ??= _ => { };
        string menu = Path.GetFullPath(manifest.TestStartMenu ?? DefaultMenu).TrimEnd('\\');
        string menuBoundary = Path.GetFullPath(manifest.TestStartMenu is null
            ? Environment.GetFolderPath(Environment.SpecialFolder.Programs) : Path.GetTempPath()).TrimEnd('\\') + "\\";
        try
        {
            if (!menu.StartsWith(menuBoundary, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Start Menu cleanup escaped its approved parent.");
            PathSafety.NotLink(menu);
            foreach (string name in new[] { "MyCapture.lnk", "Uninstall MyCapture.lnk" })
            {
                string link = Path.Combine(menu, name);
                if (!File.Exists(link)) continue;
                PathSafety.NotLink(link);
                string target = ShellLink.Target(link);
                string expected = Path.Combine(root, name == "MyCapture.lnk" ? "MyCapture.exe" : "MyCaptureUninstall.exe");
                if (PathSafety.Equal(target, expected) || (name == "Uninstall MyCapture.lnk" && PathSafety.Equal(target, Path.Combine(root, "uninstall.cmd"))))
                {
                    global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(link);
                    info("Removed owned shortcut: " + name);
                }
                else info("Preserved modified shortcut: " + name);
            }
            // The folder itself is removed only when nothing else remains in it.
            if (Directory.Exists(menu) && !Directory.EnumerateFileSystemEntries(menu).Any()) global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteDirectory(menu, false);
        }
        catch (Exception e) { warning("Could not clean Start Menu shortcuts: " + e.Message); }
        try
        {
            string path = manifest.TestRegistryKey ?? DefaultKey;
            bool exists, owned;
            using (var key = Registry.CurrentUser.OpenSubKey(path))
            { exists = key != null; owned = PathSafety.Equal(Convert.ToString(key?.GetValue("InstallLocation")), root); }
            if (owned)
            {
                Registry.CurrentUser.DeleteSubKeyTree(path, false);
                info("Removed the owned uninstall registration.");
            }
            else if (exists) info("Preserved an uninstall registration that does not belong to this installation.");
        }
        catch (Exception e) { warning("Could not clean uninstall registration: " + e.Message); }
    }
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>Isolated installs use a subkey of their test registration key in place of HKCU Run.</summary>
    public static string StartupKey(PackageManifest manifest) => manifest.TestRegistryKey == null ? RunKey : manifest.TestRegistryKey + @"\Run";
    /// <summary>uninstall-cleanup.ps1 Remove-OwnedStartupValue. The app, not setup, writes this value.</summary>
    public static void RemoveOwnedStartupValue(string root, PackageManifest manifest, Action<string> info, Action<string> warning)
    {
        try
        {
            Options.ValidateTestTargets(manifest.TestRegistryKey, manifest.TestStartMenu);
            string path = StartupKey(manifest);
            string command;
            using (var key = Registry.CurrentUser.OpenSubKey(path)) command = RegistryText(key?.GetValue("MyCapture"));
            string? executable = CommandExecutable(command);
            if (!string.IsNullOrEmpty(executable) && PathSafety.Equal(executable, Path.Combine(root, "MyCapture.exe")))
            {
                using (var key = Registry.CurrentUser.OpenSubKey(path, true)) key!.DeleteValue("MyCapture", true);
                info("Removed the owned MyCapture startup value.");
            }
            else if (command.Length != 0) info("Preserved a MyCapture startup value that does not point to this installation.");
        }
        catch (Exception e) { warning("Could not evaluate/remove startup registration: " + e.Message); }
    }
    /// <summary>The executable of a Run command line, parsed exactly as the PowerShell cleanup did.</summary>
    public static string? CommandExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string value = command!.Trim();
        if (value.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = value.IndexOf('"', 1);
            if (end > 1) return value.Substring(1, end - 1);
        }
        int space = value.IndexOf(' ');
        if (space > 0) return value.Substring(0, space);
        return value.Trim('"');
    }
    // Windows PowerShell [string] conversion of a registry value: arrays join with a space.
    private static string RegistryText(object? value) => value switch
    {
        null => "",
        string text => text,
        string[] lines => string.Join(" ", lines),
        byte[] bytes => string.Join(" ", bytes),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
    };
}
