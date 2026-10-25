using System.IO;

namespace MyCapture.App.Updates;

/// <summary>
/// Removes finished in-app update sessions. They are app-owned temporary files (a downloaded
/// installer, its checksum list, the handoff and the receipt), so they are deleted outright like
/// every other transient the app creates; captures never live here.
/// </summary>
internal static class UpdateSessionCleanup
{
    internal static int Cleanup(string updatesRoot)
    {
        int removed = 0;
        foreach (string session in FindStaleSessions(updatesRoot))
        {
            try
            {
                // Revalidate immediately before deleting exact known files. Never recurse into
                // a session directory; unknown content stays in place.
                if (!TryGetFiles(session, out string[] files)) continue;
                foreach (string file in files) global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(file);
                UpdateInstaller.AssertNoReparsePoints(session);
                if (Directory.EnumerateFileSystemEntries(session).Any()) continue;
                global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteDirectory(session, recursive: false);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    internal static IReadOnlyList<string> FindStaleSessions(string updatesRoot)
    {
        var sessions = new List<string>();
        try
        {
            updatesRoot = UpdatePaths.CanonicalRoot(updatesRoot);
            if (!Directory.Exists(updatesRoot)) return sessions;
            foreach (string session in Directory.EnumerateDirectories(updatesRoot))
                if (TryGetFiles(session, out _)) sessions.Add(session);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return sessions;
    }

    private static bool TryGetFiles(string session, out string[] files)
    {
        files = [];
        try
        {
            if (!UpdatePaths.TryParseSessionName(Path.GetFileName(session), out UpdateVersion version, out _)) return false;
            UpdateInstaller.AssertNoReparsePoints(session);
            string installer = UpdatePaths.InstallerName(version);
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { installer, "SHA256SUMS.txt", "update-session.json", "install-result.json", "update.log" };
            string[] entries = Directory.GetFileSystemEntries(session);
            foreach (string entry in entries)
                if (!known.Contains(Path.GetFileName(entry)) ||
                    (File.GetAttributes(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    return false;
            string installerPath = UpdatePaths.Child(session, installer);
            if (File.Exists(installerPath))
            {
                // A running installer (the update in progress) keeps its own session.
                using var unlocked = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            files = entries;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
