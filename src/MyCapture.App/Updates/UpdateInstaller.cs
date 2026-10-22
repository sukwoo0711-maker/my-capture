using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace MyCapture.App.Updates;

/// <summary>Prepares a per-session handoff. No installer runs until the parent has exited.</summary>
internal sealed class UpdateInstaller
{
    private readonly Action<ProcessStartInfo> _start;
    internal UpdateInstaller(Action<ProcessStartInfo>? start = null) =>
        _start = start ?? (info => { using var process = Process.Start(info) ?? throw new IOException("Update installer did not start."); });

    internal async Task<bool> InstallAsync(VerifiedUpdatePackage package, Func<bool> canExit,
        Action exit, CancellationToken cancellationToken, UpdateTarget? target = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        target ??= UpdateTarget.Resolve(AppContext.BaseDirectory, UpdateTarget.DefaultRoot);
        if (target.IsPortableMigration)
            throw new IOException("Only an owned installation can hand off an in-app update.");
        await target.ValidateAsync(cancellationToken);
        string session = UpdatePaths.CanonicalRoot(package.StagingDirectory);
        if (!UpdatePaths.TryParseSessionName(Path.GetFileName(session), out UpdateVersion version, out string token) ||
            version != package.Version)
            throw new IOException("The update session name does not match its package version.");
        string expectedName = UpdatePaths.InstallerName(package.Version);
        if (!string.Equals(UpdatePaths.Child(session, expectedName), package.InstallerPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Installer is outside its update session.");
        AssertNoReparsePoints(package.InstallerPath);
        // Keep the verified file read-locked through process creation and the exit decision.
        await using var stream = new FileStream(package.InstallerPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != package.FileSizeBytes ||
            !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)),
                package.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Installer integrity changed after download.");

        string configPath = UpdatePaths.Child(session, "update-session.json");
        using var parent = Process.GetCurrentProcess();
        var config = new
        {
            Token = token,
            Version = package.Version.ToNormalizedString(),
            InstallRoot = UpdatePaths.CanonicalRoot(target.InstallRoot),
            SourceRoot = UpdatePaths.CanonicalRoot(target.SourceRoot),
            TargetMode = "OwnedInstall",
            ParentId = parent.Id,
            ParentStartedUtc = parent.StartTime.ToUniversalTime().Ticks,
            WaitForParent = true,
            RestartAfterInstall = true,
            ShowProgress = true,
        };
        await using (var file = new FileStream(configPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(file, config, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // This callback and exit run on the UI dispatcher, with no intervening await.
        if (!canExit()) return false;
        var info = new ProcessStartInfo(package.InstallerPath)
        {
            UseShellExecute = false,
            WorkingDirectory = session,
        };
        info.Environment["MYCAPTURE_UPDATE_SESSION"] = configPath;
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
