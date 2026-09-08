using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyCapture.App.Updates;

/// <summary>Prepares a per-session handoff. No installer runs until the parent has exited.</summary>
internal sealed class UpdateInstaller
{
    internal static bool SupportsCurrentInstallation => SupportsInstallation(AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MyCapture"));

    internal static bool SupportsInstallation(string currentDirectory, string defaultDirectory)
    {
        if (!string.Equals(Path.GetFullPath(currentDirectory).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(defaultDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            string marker = Path.Combine(currentDirectory, "install-manifest.json");
            AssertNoReparsePoints(marker);
            using var manifest = JsonDocument.Parse(File.ReadAllText(marker));
            return manifest.RootElement.GetProperty("Product").GetString() == "MyCapture" &&
                UpdateVersion.TryParse(manifest.RootElement.GetProperty("Version").GetString(), out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }
    private readonly Action<ProcessStartInfo> _start;
    internal UpdateInstaller(Action<ProcessStartInfo>? start = null) =>
        _start = start ?? (info => { using var process = Process.Start(info) ?? throw new IOException("Updater helper did not start."); });

    internal async Task<bool> InstallAsync(VerifiedUpdatePackage package, Func<bool> canExit,
        Action exit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        string session = package.StagingDirectory;
        string expectedName = $"MyCapture-{package.Version}-win-x64-setup.exe";
        if (!string.Equals(Path.Combine(session, expectedName), package.InstallerPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Installer is outside its update session.");
        AssertNoReparsePoints(package.InstallerPath);
        // Keep the verified file read-locked through process creation and the exit decision.
        await using var stream = new FileStream(package.InstallerPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != package.FileSizeBytes ||
            !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)),
                package.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Installer integrity changed after download.");

        string scriptPath = Path.Combine(session, "update-helper.ps1");
        string configPath = Path.Combine(session, "update-session.json");
        using Stream resource = typeof(UpdateInstaller).Assembly.GetManifestResourceStream("MyCapture.UpdateHelper.ps1")
            ?? throw new IOException("Updater helper resource is missing.");
        await using (var script = new FileStream(scriptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await resource.CopyToAsync(script, cancellationToken);
        using var parent = Process.GetCurrentProcess();
        var config = new
        {
            ParentId = parent.Id,
            ParentStartedUtc = parent.StartTime.ToUniversalTime().Ticks,
            Installer = package.InstallerPath,
            Sha256 = package.ExpectedSha256,
            Bytes = package.FileSizeBytes,
            Version = package.Version.ToNormalizedString(),
            Token = Guid.NewGuid().ToString("N"),
            InstallRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MyCapture"),
            FailureMessage = UpdateStrings.HelperFailed,
        };
        await using (var file = new FileStream(configPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(file, config, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // This callback and exit run on the UI dispatcher, with no intervening await.
        if (!canExit()) return false;
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-Session", configPath })
            info.ArgumentList.Add(argument);
        _start(info);
        exit();
        return true;
    }

    internal static void AssertNoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update paths cannot contain symbolic links or junctions.");
    }
}
