using System.IO;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;

namespace MyCapture.App.Editing;

/// <summary>
/// Shared destination rules for editor, library, quick-access and pin exports. Quick save always
/// uses the fixed folder (the explicit override, else the default capture folder); the last
/// Save As folder only chooses where the next Save As dialog starts.
/// </summary>
internal sealed class ImageExportLocation
{
    /// <summary>How long opening a remembered folder may block the caller before the default is used.</summary>
    internal static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromMilliseconds(300);

    private readonly Func<AppSettings> _settings;
    private readonly Func<AppPaths> _paths;
    private readonly ILogger _log;
    private readonly Action? _persistSettings;
    private readonly Func<string, bool> _probeDirectory;
    private readonly TimeSpan _probeTimeout;
    private readonly object _probeGate = new();
    private (string Directory, Task<bool> Probe)? _inFlightProbe;

    internal ImageExportLocation(
        Func<AppSettings> settings,
        Func<AppPaths> paths,
        ILogger log,
        Action? persistSettings = null,
        Func<string, bool>? probeDirectory = null,
        TimeSpan? probeTimeout = null)
    {
        _settings = settings;
        _paths = paths;
        _log = log;
        _persistSettings = persistSettings;
        _probeDirectory = probeDirectory ?? CanOpenDirectory;
        _probeTimeout = probeTimeout ?? DefaultProbeTimeout;
    }

    /// <summary>Starting folder of Save As dialogs: the last Save As folder when usable, else the quick save folder.</summary>
    internal string SaveAsDirectory => ExistingRememberedDirectory(_settings().Export) ?? QuickSaveDirectory;

    /// <summary>Fixed quick save folder. The Save As history never changes it.</summary>
    internal string QuickSaveDirectory
    {
        get
        {
            ExportSettings export = _settings().Export;
            return !string.IsNullOrWhiteSpace(export.QuickSaveDirectoryOverride)
                ? export.QuickSaveDirectoryOverride
                : _paths().QuickSaveRoot;
        }
    }

    internal void RememberSavedPath(string path, bool persistEvenIfUnchanged = false)
    {
        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(directory)) return;
            AppSettings current = _settings();
            if (!persistEvenIfUnchanged
                && string.Equals(current.Export.LastSaveDirectory, directory, StringComparison.OrdinalIgnoreCase)) return;
            string previous = current.Export.LastSaveDirectory;
            current.Export.LastSaveDirectory = directory;
            try { _persistSettings?.Invoke(); }
            catch
            {
                current.Export.LastSaveDirectory = previous;
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException
                                   or InvalidOperationException or System.Text.Json.JsonException
                                   or System.Security.SecurityException
                                   or System.Security.Cryptography.CryptographicException)
        {
            // The image is already durable; a preference failure must not fail its export.
            _log.LogWarning(ex, "Image saved, but its destination folder could not be remembered");
        }
    }

    private string? ExistingRememberedDirectory(ExportSettings export)
    {
        string directory = export.LastSaveDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) return null;

        // A disconnected share can block a directory open for many seconds, and this runs on the
        // UI thread. Probe on the thread pool and give up quickly: the caller falls back to the
        // default folder while the remembered path stays stored for when the share returns.
        Task<bool> probe = StartOrReuseProbe(directory);
        try
        {
            if (!probe.Wait(_probeTimeout))
            {
                _log.LogInformation("The remembered export folder did not answer within {Timeout} ms; using the default folder",
                    (int)_probeTimeout.TotalMilliseconds);
                return null;
            }
            return probe.Result ? directory : null;
        }
        catch (AggregateException ex)
        {
            _log.LogWarning(ex.InnerException ?? ex, "The remembered export folder could not be checked; using the default folder");
            return null;
        }
    }

    private Task<bool> StartOrReuseProbe(string directory)
    {
        lock (_probeGate)
        {
            // While an earlier probe of the same folder is still stuck, wait on it instead of
            // parking another thread-pool thread behind the same unreachable share.
            if (_inFlightProbe is { } running && !running.Probe.IsCompleted
                && string.Equals(running.Directory, directory, StringComparison.OrdinalIgnoreCase))
                return running.Probe;
            Task<bool> started = Task.Run(() => _probeDirectory(directory));
            _inFlightProbe = (directory, started);
            return started;
        }
    }

    private static bool CanOpenDirectory(string directory)
    {
        try
        {
            // Exists only checks metadata and can succeed when the picker cannot open the
            // folder. Probe one entry, including an empty folder, without scanning or writing.
            using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            _ = entries.MoveNext();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // The caller keeps the remembered path so a reconnected drive becomes available again.
            return false;
        }
    }
}
