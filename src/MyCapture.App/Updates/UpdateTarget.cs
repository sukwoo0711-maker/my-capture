using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MyCapture.App.Updates;

internal sealed record UpdateTarget(string SourceRoot, string InstallRoot, bool IsPortableMigration)
{
    internal static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MyCapture");

    internal static UpdateTarget Resolve(string sourceRoot, string defaultRoot)
    {
        sourceRoot = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar);
        defaultRoot = Path.GetFullPath(defaultRoot).TrimEnd(Path.DirectorySeparatorChar);
        bool owned = ReadOwnedInventory(sourceRoot) is not null;
        var target = new UpdateTarget(sourceRoot, owned ? sourceRoot : defaultRoot, !owned);
        AssertSafeRoot(target.InstallRoot);
        return target;
    }

    internal async Task ValidateAsync(CancellationToken cancellationToken)
    {
        AssertSafeRoot(InstallRoot);
        if (IsPortableMigration)
        {
            if (!string.Equals(InstallRoot, DefaultRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Portable updates can only install to the fixed per-user destination.");
            return;
        }
        if (!string.Equals(SourceRoot, InstallRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("An installed update must target its source directory.");
        var inventory = ReadOwnedInventory(SourceRoot) ?? throw new IOException("Installed ownership changed.");
        foreach (var entry in inventory)
        {
            string path = Path.Combine(SourceRoot, entry.Name);
            UpdateInstaller.AssertNoReparsePoints(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != entry.Bytes || !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)),
                entry.Hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Installed application no longer matches its ownership manifest.");
        }
    }

    private static List<(string Name, long Bytes, string Hash)>? ReadOwnedInventory(string root)
    {
        try
        {
            string marker = Path.Combine(root, "install-manifest.json");
            UpdateInstaller.AssertNoReparsePoints(marker);
            if (new FileInfo(marker).Length > 10 * 1024 * 1024) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(marker));
            var manifest = document.RootElement;
            if (manifest.GetProperty("Product").GetString() != "MyCapture" ||
                !UpdateVersion.TryParse(manifest.GetProperty("Version").GetString(), out _)) return null;
            var result = new List<(string, long, string)>();
            foreach (string name in new[] { "MyCapture.exe", "MyCapture.dll" })
            {
                var entries = manifest.GetProperty("Files").EnumerateArray().Where(e => e.GetProperty("Path").GetString() == name).ToArray();
                if (entries.Length != 1) return null;
                string? hash = entries[0].GetProperty("Sha256").GetString();
                long bytes = entries[0].GetProperty("Bytes").GetInt64();
                if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit) || bytes <= 0) return null;
                result.Add((name, bytes, hash));
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return null; }
    }

    internal static void AssertSafeRoot(string root)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(root, Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            root.StartsWith(@"\\?\", StringComparison.Ordinal) || new Uri(root).IsUnc)
            throw new IOException("An update cannot target a drive, device, or network root.");
        foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments })
        {
            string protectedPath = Environment.GetFolderPath(folder);
            if (folder is Environment.SpecialFolder.Windows or Environment.SpecialFolder.ProgramFiles or Environment.SpecialFolder.ProgramFilesX86 &&
                !string.IsNullOrEmpty(protectedPath) && root.StartsWith(protectedPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A per-user update cannot replace files in a system directory.");
            if (!string.IsNullOrEmpty(protectedPath) && (string.Equals(root, protectedPath, StringComparison.OrdinalIgnoreCase) ||
                protectedPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("An update cannot claim a system or user-data root.");
        }
        for (string? existing = root; existing is not null; existing = Path.GetDirectoryName(existing))
            if (Directory.Exists(existing)) { UpdateInstaller.AssertNoReparsePoints(existing); break; }
    }
}
