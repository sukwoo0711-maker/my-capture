using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Store;
using MyCapture.App.Updates;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class AppUpdateCoordinatorTests
{
    private static readonly UpdateVersion Current = UpdateVersion.FromVersion(new Version(4, 5, 2));

    [Fact]
    public async Task AutomaticFailureLogsWithoutChangingNoticeOrStatusAndRecordsAttempt()
    {
        var state = new UpdateSettings();
        var network = new ReleaseService();
        var log = new RecordingLog();
        int saves = 0, changes = 0;
        using var updates = Create(UpdateChannel.GitHubInstall, state, network, log, save: () => saves++);
        updates.Changed += () => changes++;
        await updates.CheckAsync(automatic: true);
        Assert.True(updates.HasAvailableUpdate);
        Assert.Equal("4.6.0", updates.AvailableVersion);
        network.Check = () => Task.FromResult(UpdateCheckResult.Failed(UpdateErrorKind.CheckFailed, "offline"));
        int before = changes;
        await updates.CheckAsync(automatic: true);
        Assert.Equal(before, changes);
        Assert.Equal(StoreUpdatePhase.Available, updates.Snapshot.Phase);
        Assert.Null(updates.StatusText);
        Assert.NotNull(state.LastCheckUtc);
        Assert.Equal(2, saves);
        Assert.Single(log.Errors);
        Assert.False(updates.IsBusy);
    }

    [Fact]
    public async Task DismissalIsForTheOfferedGitHubVersionAndANewerVersionCanAppear()
    {
        var state = new UpdateSettings();
        var network = new ReleaseService();
        using var updates = Create(UpdateChannel.Portable, state, network);
        await updates.CheckAsync();
        updates.DismissAvailable();
        Assert.Equal(updates.AvailableKey, state.DismissedVersion);
        network.Check = () => Task.FromResult(UpdateCheckResult.Available(Current, Package("4.7.0")));
        await updates.CheckAsync(automatic: true);
        Assert.Equal("4.7.0", updates.AvailableKey);
        Assert.NotEqual(state.DismissedVersion, updates.AvailableKey);
    }

    [Fact]
    public void PortableOpensValidatedReleaseWithoutDownloadOrProtection() => StaTestHost.Run(() =>
    {
        var network = new ReleaseService();
        Uri? opened = null;
        using var updates = Create(UpdateChannel.Portable, new(), network, open: uri => opened = uri,
            prepare: () => throw new InvalidOperationException("Portable must not prepare installation."));
        updates.CheckAsync().GetAwaiter().GetResult();
        updates.InstallAsync(new Window()).GetAwaiter().GetResult();
        Assert.Equal(Package("4.6.0").ReleaseUrl, opened);
        Assert.Equal(0, network.Downloads);
        Assert.False(updates.ShowInstallationNotice);
    });

    [Theory]
    [InlineData("https://example.com/sukwoo0711-maker/my-capture/releases/tag/v4.6.0")]
    [InlineData("https://github.com/other/project/releases/tag/v4.6.0")]
    [InlineData("http://github.com/sukwoo0711-maker/my-capture/releases/tag/v4.6.0")]
    [InlineData("https://github.com/sukwoo0711-maker/my-capture/releases/tag/v4.6.0?redirect=evil")]
    public void PortableRejectsUntrustedReleasePage(string url) => StaTestHost.Run(() =>
    {
        var network = new ReleaseService { Check = () => Task.FromResult(UpdateCheckResult.Available(Current, Package("4.6.0", url))) };
        bool opened = false;
        using var updates = Create(UpdateChannel.Portable, new(), network, open: _ => opened = true);
        updates.CheckAsync().GetAwaiter().GetResult();
        updates.InstallAsync(new Window()).GetAwaiter().GetResult();
        Assert.False(opened);
        Assert.Equal(StoreUpdatePhase.Failed, updates.Snapshot.Phase);
    });

    [Theory]
    [InlineData((int)StoreUpdatePhase.Canceled)]
    [InlineData((int)StoreUpdatePhase.Failed)]
    [InlineData((int)StoreUpdatePhase.RestartRequired)]
    public void StoreProtectionRestoresOwnerAndClosedTerminalNoticeStaysClosed(int phase) => StaTestHost.Run(() =>
    {
        var result = (StoreUpdatePhase)phase;
        var owner = new Window();
        var store = new StoreService { Result = result };
        int releases = 0;
        using var updates = Create(UpdateChannel.Store, new(), new(), store: _ => store,
            prepare: () => { owner.IsEnabled = false; return Task.FromResult(true); },
            release: () => { releases++; owner.IsEnabled = true; });
        updates.CheckAsync(owner).GetAwaiter().GetResult();
        updates.InstallAsync(owner).GetAwaiter().GetResult();
        Assert.True(owner.IsEnabled);
        Assert.Equal(1, releases);
        Assert.Equal(result, updates.Snapshot.Phase);
        Assert.False(updates.IsBusy);
        if (result == StoreUpdatePhase.Failed)
        {
            Assert.True(updates.ShowInstallationNotice);
            string? failure = updates.StatusText;
            updates.CheckAsync(automatic: true).GetAwaiter().GetResult();
            Assert.Equal(StoreUpdatePhase.Failed, updates.Snapshot.Phase);
            Assert.Equal(failure, updates.StatusText);
        }
        updates.DismissAvailable();
        Assert.False(updates.ShowInstallationNotice);
    });

    [Fact]
    public void StoreProgressFromBackgroundIsMarshaledAndLateProgressCannotReplaceTerminalState() => StaTestHost.Run(() =>
    {
        var owner = new Window();
        var store = new StoreService();
        using var updates = Create(UpdateChannel.Store, new(), new(), store: _ => store);
        int uiThread = Environment.CurrentManagedThreadId;
        updates.Changed += () => Assert.Equal(uiThread, Environment.CurrentManagedThreadId);
        updates.CheckAsync(owner).GetAwaiter().GetResult();
        updates.InstallAsync(owner).GetAwaiter().GetResult();
        Task.Run(() => store.Progress!.Report(new(StoreUpdatePhase.Downloading, .75))).GetAwaiter().GetResult();
        var frame = new DispatcherFrame();
        owner.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Assert.Equal(StoreUpdatePhase.Canceled, updates.Snapshot.Phase);
        Assert.False(updates.ShowInstallationNotice);
    });

    [Fact]
    public void DisposalDuringStoreRecheckNeverEntersProtectionOrInstallation() => StaTestHost.Run(() =>
    {
        var pending = new TaskCompletionSource<bool>();
        var store = new StoreService();
        bool prepared = false;
        using var updates = Create(UpdateChannel.Store, new(), new(), store: _ => store,
            prepare: () => { prepared = true; return Task.FromResult(true); });
        updates.CheckAsync().GetAwaiter().GetResult();
        store.Check = () => pending.Task;
        Task installation = updates.InstallAsync(new Window());
        updates.Dispose();
        pending.SetResult(true);
        installation.GetAwaiter().GetResult();
        Assert.False(prepared);
        Assert.Equal(0, store.Installations);
    });

    [Fact]
    public async Task DevelopmentChannelNeverCallsEitherService()
    {
        var network = new ReleaseService { Check = () => throw new InvalidOperationException("Unexpected network") };
        using var updates = Create(UpdateChannel.None, new(), network,
            store: _ => throw new InvalidOperationException("Unexpected Store"));
        await updates.CheckAsync();
        Assert.False(updates.HasAvailableUpdate);
        Assert.Equal(StoreUpdatePhase.Idle, updates.Snapshot.Phase);
    }

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void CardPriorityAndTrayAvailabilityUseOneAction(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        var notice = new EngagementNotice();
        notice.ShowInvitation();
        notice.ShowUpdateAvailable("4.6.0");
        Assert.Equal(UiText.Format("Update_AvailableVersion", "4.6.0"), notice.Message);
        notice.ShowInvitation();
        Assert.Equal(EngagementNoticeKind.UpdateAvailable, notice.Kind);
        notice.ShowUpdateCompleted("4.5.2");
        notice.ShowUpdateAvailable("4.6.0");
        Assert.Equal(EngagementNoticeKind.UpdateCompleted, notice.Kind);
        notice.Dismiss();
        Assert.Equal(Visibility.Collapsed, notice.Visibility);
        int installs = 0;
        Action noop = () => { };
        var commands = new TrayMenuCommands(noop, noop, noop, noop, noop, noop, noop, noop,
            noop, noop, noop, noop, noop, InstallUpdate: () => installs++);
        string label = UiText.Format("Update_TrayInstallVersion", "4.6.0");
        Assert.DoesNotContain(TrayMenuLayout.Build(commands, new(), new TrayMenuState()), item => item.Label == label);
        var menu = TrayMenuLayout.Build(commands, new(), new TrayMenuState(UpdateAvailable: true, UpdateVersion: "4.6.0"));
        var action = Assert.Single(menu, item => item.Label == label);
        action.Action!();
        Assert.Equal(1, installs);
        var busy = TrayMenuLayout.Build(commands, new(), new TrayMenuState(UpdateAvailable: true, UpdateVersion: "4.6.0", UpdateBusy: true));
        Assert.False(Assert.Single(busy, item => item.Label == label).IsEnabled);
    });

    private static AppUpdateCoordinator Create(UpdateChannel channel, UpdateSettings state, ReleaseService network,
        RecordingLog? log = null, Action? save = null, Func<Task<bool>>? prepare = null, Action? release = null,
        Func<Window?, IStorePackageUpdateService>? store = null, Action<Uri>? open = null) =>
        new(channel, Current, () => state, save ?? (() => { }), prepare ?? (() => Task.FromResult(true)),
            release ?? (() => { }), () => throw new InvalidOperationException("Unexpected exit"), log ?? new(),
            AppContext.BaseDirectory, Path.GetTempPath(), github: network, store: store,
            open: open ?? (_ => throw new InvalidOperationException("Unexpected browser")));

    private static UpdatePackageInfo Package(string version, string? url = null)
    {
        UpdateVersion.TryParse(version, out UpdateVersion? parsed);
        string prefix = "https://github.com/sukwoo0711-maker/my-capture/releases/";
        return new(parsed!.Value, "release", "", new Uri(url ?? prefix + "tag/v" + version), DateTimeOffset.UtcNow,
            $"MyCapture-{version}-win-x64-setup.exe", new Uri(prefix + $"download/v{version}/MyCapture-{version}-win-x64-setup.exe"),
            1, new Uri(prefix + $"download/v{version}/SHA256SUMS.txt"), "v" + version);
    }

    private sealed class ReleaseService : IUpdateService
    {
        internal Func<Task<UpdateCheckResult>> Check = () => Task.FromResult(UpdateCheckResult.Available(Current, Package("4.6.0")));
        internal int Downloads;
        public Task<UpdateCheckResult> CheckForUpdateAsync(UpdateVersion version, CancellationToken token = default) => Check();
        public Task<UpdateCheckResult> CheckForUpdateAsync(Version version, CancellationToken token = default) => Check();
        public Task<StagedUpdateResult> DownloadAndStageAsync(UpdatePackageInfo package, string root, IProgress<UpdateProgress>? progress = null, CancellationToken token = default)
        { Downloads++; return Task.FromResult(StagedUpdateResult.Failed(UpdateErrorKind.CheckFailed, "Download not expected")); }
        public Task<StagedUpdateResult> CheckAndStageAsync(UpdateVersion version, string root, IProgress<UpdateProgress>? progress = null, CancellationToken token = default) => throw new NotSupportedException();
        public Task<StagedUpdateResult> CheckAndStageAsync(Version version, string root, IProgress<UpdateProgress>? progress = null, CancellationToken token = default) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class StoreService : IStorePackageUpdateService
    {
        internal Func<Task<bool>> Check = () => Task.FromResult(true);
        internal StoreUpdatePhase Result = StoreUpdatePhase.Canceled;
        internal int Installations;
        internal IProgress<StoreUpdateSnapshot>? Progress;
        public Task<bool> CheckAsync() => Check();
        public Task<StoreUpdatePhase> InstallAsync(IProgress<StoreUpdateSnapshot> progress)
        { Installations++; Progress = progress; progress.Report(new(StoreUpdatePhase.Downloading, .5)); return Task.FromResult(Result); }
    }

    private sealed class RecordingLog : ILogger
    {
        internal readonly List<Exception> Errors = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (exception is not null) Errors.Add(exception); }
    }
}
