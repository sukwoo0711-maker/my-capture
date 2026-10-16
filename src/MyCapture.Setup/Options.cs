using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace MyCapture.Setup;

public sealed class Options
{
    public bool Quiet { get; set; }
    public bool VerifyOnly { get; set; }
    public bool NoShellIntegration { get; set; }
    public bool Uninstall { get; set; }
    public string InstallRoot { get; set; } = "";
    public string Payload { get; set; } = "";
    public string Manifest { get; set; } = "";
    public string Log { get; set; } = "";
    public string TestFault { get; set; } = "";
    public long MinimumFreeBytes { get; set; }
    public string? TestRegistryKey { get; set; }
    public string? TestStartMenu { get; set; }
    internal string? CleanupRequest { get; set; }
    public static Options Parse(string[] args, string directory)
    {
        var result = new Options { Payload = Path.Combine(directory, "payload.zip"), Manifest = Path.Combine(directory, "installer-manifest.json") };
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i].ToLowerInvariant();
            if (!seen.Add(option)) throw new SetupException(21, "Duplicate option: " + option);
            switch (option)
            {
                case "/quiet": case "/q": result.Quiet = true; break;
                case "--verify-only": result.VerifyOnly = true; break;
                case "--no-shell-integration": result.NoShellIntegration = true; break;
                case "/uninstall": result.Uninstall = true; break;
                default:
                    if (i + 1 == args.Length) throw new SetupException(21, "Unknown option or missing value: " + option);
                    string value = args[++i];
                    switch (option)
                    {
                        case "--install-root": result.InstallRoot = value; break;
                        case "--payload": result.Payload = Path.GetFullPath(value); break;
                        case "--manifest": result.Manifest = Path.GetFullPath(value); break;
                        case "--log": result.Log = Path.GetFullPath(value); break;
                        case "--minimum-free-bytes":
                            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long minimum))
                                throw new SetupException(21, "MinimumFreeBytes must be a nonnegative Int64.");
                            result.MinimumFreeBytes = minimum; break;
                        case "--test-fault":
                            if (value != "AfterBackup" && value != "AfterCommit") throw new SetupException(21, "Unknown test fault: " + value);
                            result.TestFault = value; break;
                        case "--test-uninstall-key": result.TestRegistryKey = value; break;
                        case "--test-start-menu": result.TestStartMenu = value; break;
                        case "--cleanup-request": result.CleanupRequest = Path.GetFullPath(value); result.Uninstall = true; result.Quiet = true; break;
                        default: throw new SetupException(21, "Unknown option: " + option);
                    }
                    break;
            }
        }
        ValidateTestTargets(result.TestRegistryKey, result.TestStartMenu);
        if (result.Uninstall && (result.InstallRoot.Length != 0 || result.VerifyOnly || result.TestFault.Length != 0))
            throw new SetupException(21, "Uninstall uses its own executable directory; install options are not accepted.");
        return result;
    }
    public static void ValidateTestTargets(string? key, string? startMenu)
    {
        if (key == null && startMenu == null) return;
        if (key == null || startMenu == null || !Regex.IsMatch(key, @"\ASoftware\\MyCapture\.SetupTests\\[a-f0-9]{32}\z"))
            throw new SetupException(12, "Test targets require a key strictly under HKCU\\Software\\MyCapture.SetupTests\\<guid> and a temporary Start Menu directory.");
        string full = Path.GetFullPath(startMenu).TrimEnd('\\');
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
        if (!full.StartsWith(temp + "\\", StringComparison.OrdinalIgnoreCase) || !Path.IsPathRooted(startMenu) ||
            !string.Equals(startMenu.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(@"\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0)
            throw new SetupException(12, "Test Start Menu directory must be a canonical local directory strictly under TEMP.");
        PathSafety.NoLinks(full);
    }
}
