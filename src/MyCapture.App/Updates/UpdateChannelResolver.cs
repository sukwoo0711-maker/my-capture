using System.IO;
using System.Text.Json;
using MyCapture.Core.Settings;

namespace MyCapture.App.Updates;

internal static class UpdateChannelResolver
{
    internal static UpdateChannel Resolve(bool isPackaged, string sourceRoot)
    {
        if (isPackaged) return UpdateChannel.Store;
        try
        {
            sourceRoot = UpdatePaths.CanonicalRoot(sourceRoot);
            string[] parts = sourceRoot.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (int index = 0; index + 1 < parts.Length; index++)
                if (parts[index].Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                    (parts[index + 1].Equals("Debug", StringComparison.OrdinalIgnoreCase) ||
                     parts[index + 1].Equals("Release", StringComparison.OrdinalIgnoreCase)))
                    return UpdateChannel.None;

            if (File.Exists(UpdatePaths.Child(sourceRoot, "install-manifest.json")))
                return UpdateTarget.Resolve(sourceRoot, UpdateTarget.DefaultRoot).IsPortableMigration
                    ? UpdateChannel.None : UpdateChannel.GitHubInstall;

            // package.ps1 publishes a self-contained win-x64 runtime; ordinary build output
            // (even when copied elsewhere) lacks these binaries and included-framework metadata.
            foreach (string name in new[] { "MyCapture.exe", "MyCapture.dll", "coreclr.dll", "hostfxr.dll",
                "hostpolicy.dll", "System.Private.CoreLib.dll", "PresentationCore.dll", "PresentationFramework.dll" })
                if (new FileInfo(UpdatePaths.Child(sourceRoot, name)).Length == 0) return UpdateChannel.None;
            using JsonDocument runtime = ReadMetadata(sourceRoot, "MyCapture.runtimeconfig.json");
            JsonElement options = runtime.RootElement.GetProperty("runtimeOptions");
            if (options.TryGetProperty("framework", out _) || options.TryGetProperty("frameworks", out _))
                return UpdateChannel.None;
            string?[] frameworks = options.GetProperty("includedFrameworks").EnumerateArray()
                .Select(item => item.GetProperty("name").GetString()).ToArray();
            if (!frameworks.Contains("Microsoft.NETCore.App") || !frameworks.Contains("Microsoft.WindowsDesktop.App"))
                return UpdateChannel.None;
            using JsonDocument deps = ReadMetadata(sourceRoot, "MyCapture.deps.json");
            return deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()
                ?.EndsWith("/win-x64", StringComparison.Ordinal) == true ? UpdateChannel.Portable : UpdateChannel.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            KeyNotFoundException or InvalidOperationException or ArgumentException or NotSupportedException)
        { return UpdateChannel.None; }
    }

    private static JsonDocument ReadMetadata(string root, string name)
    {
        string path = UpdatePaths.Child(root, name);
        if (new FileInfo(path).Length > 10 * 1024 * 1024)
            throw new IOException("Published metadata is larger than expected.");
        using var file = File.OpenRead(path);
        return JsonDocument.Parse(file);
    }
}
