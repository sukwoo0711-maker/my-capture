using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using MyCapture.App.Store;
using MyCapture.Core.Settings;

namespace MyCapture.App.Updates;

/// <summary>One update operation shared by Settings, the tray, and both notice hosts.</summary>
internal sealed class AppUpdateCoordinator : IDisposable
{
    private readonly IUpdateService _github;
    private readonly Func<Window?, IStorePackageUpdateService> _store;
    private readonly Func<UpdateSettings> _settings;
    private readonly Action _save;
    private readonly Func<Task<bool>> _prepare;
    private readonly Action _release;
    private readonly Action _exit;
    private readonly ILogger _log;
    private readonly UpdateVersion _current;
    private readonly string _sourceRoot, _stagingRoot;
    private readonly UpdateInstaller _installer;
    private readonly Action<Uri> _open;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Task _startupCleanup;
    private readonly CancellationTokenSource _lifetime = new();
    private UpdatePackageInfo? _package;
    private bool _disposed;
    private long _operation;
    private bool _installationProgressActive;

    internal AppUpdateCoordinator(UpdateChannel channel, UpdateVersion current, Func<UpdateSettings> settings,
        Action save, Func<Task<bool>> prepare, Action release, Action exit, ILogger log,
        string sourceRoot, string stagingRoot, IUpdateService? github = null,
        Func<Window?, IStorePackageUpdateService>? store = null, UpdateInstaller? installer = null,
        Action<Uri>? open = null, Func<DateTimeOffset>? clock = null, Task? startupCleanup = null)
    {
        Channel = channel; _current = current; _settings = settings; _save = save;
        _prepare = prepare; _release = release; _exit = exit; _log = log;
        _sourceRoot = sourceRoot; _stagingRoot = stagingRoot;
        _github = github ?? new GitHubUpdateService();
        _store = store ?? (owner => new StorePackageUpdateService(owner));
        _installer = installer ?? new UpdateInstaller();
        _open = open ?? (uri => { using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); });
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _startupCleanup = startupCleanup ?? Task.CompletedTask;
        LastCheckUtc = settings().LastCheckUtc;
    }

    internal UpdateChannel Channel { get; }
    internal bool IsBusy { get; private set; }
    internal bool ShowInstallationNotice { get; private set; }
    internal bool HasAvailableUpdate => AvailableKey is not null;
    internal string? AvailableVersion { get; private set; }
    internal string? AvailableKey { get; private set; }
    internal StoreUpdateSnapshot Snapshot { get; private set; } = new(StoreUpdatePhase.Idle);
    internal string? StatusText { get; private set; }
    internal DateTimeOffset? LastCheckUtc { get; private set; }
    internal event Action? Changed;

    internal async Task CheckAsync(Window? owner = null, bool automatic = false)
    {
        if (_disposed || IsBusy || Channel == UpdateChannel.None) return;
        IsBusy = true;
        StoreUpdateSnapshot previous = Snapshot;
        string? previousText = StatusText;
        string? previousKey = AvailableKey;
        if (!automatic) Publish(new(StoreUpdatePhase.Checking));
        _settings().LastCheckUtc = _clock().ToUniversalTime();
        try
        {
            _save();
            if (Channel == UpdateChannel.Store)
            {
                var service = _store(owner);
                bool available = await service.CheckAsync();
                if (_disposed) return;
                // StorePackageUpdate exposes the installed Package, not the offered version.
                // Keep its display generic and scope dismissal to this installed version.
                AvailableKey = available ? "store:" + _current.ToNormalizedString() : null;
                AvailableVersion = null;
            }
            else
            {
                var result = await _github.CheckForUpdateAsync(_current, _lifetime.Token);
                if (_disposed) return;
                if (result.ErrorKind is not (UpdateErrorKind.None or UpdateErrorKind.AlreadyUpToDate))
                    throw new IOException(result.ErrorMessage ?? "Update check failed.");
                _package = result.IsUpdateAvailable ? result.PackageInfo : null;
                AvailableVersion = _package?.Version.ToNormalizedString();
                AvailableKey = AvailableVersion;
            }
            LastCheckUtc = _settings().LastCheckUtc;
            if (automatic && previousKey == AvailableKey && ShowInstallationNotice
                && previous.Phase == StoreUpdatePhase.Failed)
            {
                Snapshot = previous; StatusText = previousText;
                return;
            }
            if (!automatic || previousKey != AvailableKey) ShowInstallationNotice = false;
            Snapshot = new(HasAvailableUpdate ? StoreUpdatePhase.Available : StoreUpdatePhase.Current);
            StatusText = null;
        }
        catch (Exception error)
        {
            _log.LogWarning(error, "Update availability check failed");
            if (automatic)
            {
                Snapshot = previous; StatusText = previousText;
                return;
            }
            Snapshot = new(StoreUpdatePhase.Failed); StatusText = null;
        }
        finally { IsBusy = false; }
        if (!_disposed) Changed?.Invoke();
    }

    internal async Task InstallAsync(Window owner)
    {
        if (_disposed || IsBusy || !HasAvailableUpdate) return;
        IsBusy = true;
        ShowInstallationNotice = Channel != UpdateChannel.Portable;
        long operation = ++_operation;
        bool held = false;
        bool handedOff = false;
        try
        {
            if (Channel == UpdateChannel.Portable)
            {
                if (_package is null || !GitHubUrlValidator.IsValidReleaseUri(_package.ReleaseUrl,
                        UpdateServiceOptions.DefaultOwner, UpdateServiceOptions.DefaultRepo, _package.ReleaseTag))
                    throw new IOException("The release page URL is not valid.");
                _open(_package.ReleaseUrl);
                return;
            }
            if (Channel == UpdateChannel.Store)
            {
                var service = _store(owner);
                Publish(new(StoreUpdatePhase.Checking));
                bool available = await service.CheckAsync();
                if (_disposed) return;
                if (!available)
                {
                    AvailableKey = null; AvailableVersion = null;
                    ShowInstallationNotice = false;
                    Publish(new(StoreUpdatePhase.Current));
                    return;
                }
                _installationProgressActive = true;
                StoreUpdatePhase phase = await StoreUpdateInstallation.InstallAsync(owner, service,
                    _prepare, _release, new InlineProgress<StoreUpdateSnapshot>(value =>
                    {
                        void Apply()
                        {
                            if (_disposed || !_installationProgressActive || operation != _operation) return;
                            Publish(value, value.Phase == StoreUpdatePhase.Downloading && value.Progress is { } progress
                                ? UiText.Format("Update_DownloadingPercent", Math.Round(progress * 100)) : null);
                        }
                        if (owner.Dispatcher.CheckAccess()) Apply(); else owner.Dispatcher.BeginInvoke((Action)Apply);
                    }), () => !_disposed);
                _installationProgressActive = false;
                if (phase == StoreUpdatePhase.Failed) throw new IOException("Store installation failed.");
                if (phase is StoreUpdatePhase.Canceled or StoreUpdatePhase.Blocked) ShowInstallationNotice = false;
                if (phase == StoreUpdatePhase.RestartRequired) { AvailableKey = null; AvailableVersion = null; }
                Publish(new(phase));
                return;
            }
            if (Channel != UpdateChannel.GitHubInstall || _package is null) return;
            await _startupCleanup;
            if (_disposed) return;
            Publish(new(StoreUpdatePhase.Downloading), UiText.Get("Update_Verifying"));
            _installationProgressActive = true;
            var result = await _github.DownloadAndStageAsync(_package, _stagingRoot,
                new InlineProgress<UpdateProgress>(progress =>
                {
                    // The downloader reports from background continuations; marshal UI state.
                    void Apply()
                    {
                        if (_disposed || !_installationProgressActive || operation != _operation) return;
                        string text = progress.Phase == UpdatePhase.DownloadingInstaller && progress.Percent is { } percent
                            ? UiText.Format("Update_DownloadingPercent", Math.Round(percent))
                            : UiText.Get(progress.Phase == UpdatePhase.Ready ? "Update_Preparing" : "Update_Verifying");
                        Publish(new(progress.Phase == UpdatePhase.DownloadingInstaller ? StoreUpdatePhase.Downloading
                            : StoreUpdatePhase.Installing, progress.Percent / 100), text);
                    }
                    if (owner.Dispatcher.CheckAccess()) Apply(); else owner.Dispatcher.BeginInvoke((Action)Apply);
                }), _lifetime.Token);
            _installationProgressActive = false;
            if (_disposed) return;
            if (!result.Succeeded || result.VerifiedPackage is not { } verified)
                throw new IOException(result.ErrorMessage ?? "Update download failed.");
            Publish(new(StoreUpdatePhase.Installing), UiText.Get("Update_Preparing"));
            if (!await _prepare())
            {
                ShowInstallationNotice = false;
                Publish(new(StoreUpdatePhase.Blocked)); return;
            }
            held = true;
            handedOff = await _installer.InstallAsync(verified, () => !_disposed && !_lifetime.IsCancellationRequested,
                _exit, _lifetime.Token, UpdateTarget.Resolve(_sourceRoot, UpdateTarget.DefaultRoot));
        }
        catch (Exception error)
        {
            _log.LogError(error, "Update installation failed");
            Publish(new(StoreUpdatePhase.Failed), UiText.Get("Update_InstallFailed"));
        }
        finally
        {
            _installationProgressActive = false;
            if (held && !handedOff) _release();
            IsBusy = false;
            if (!_disposed) Changed?.Invoke();
        }
    }

    internal void DismissAvailable()
    {
        ShowInstallationNotice = false;
        if (AvailableKey is { } key)
        {
            _settings().DismissedVersion = key;
            _save();
        }
        Changed?.Invoke();
    }

    private void Publish(StoreUpdateSnapshot snapshot, string? text = null)
    {
        Snapshot = snapshot; StatusText = text;
        if (!_disposed) Changed?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        _github.Dispose();
    }
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
