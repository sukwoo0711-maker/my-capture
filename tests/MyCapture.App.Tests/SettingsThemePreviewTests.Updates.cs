using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Store;
using MyCapture.App.Updates;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task UpdateTab_SharedChannelsReflectAvailabilityAndSaveAutomaticPreference() =>
        RunIsolated(nameof(UpdateTab_SharedChannelsReflectAvailabilityAndSaveAutomaticPreference), VerifySharedUpdateChannels);

    private static void VerifySharedUpdateChannels()
    {
        Application application = CreateThemedApplication();
        try
        {
            foreach (string locale in new[] { "ko-KR", "en-US" })
            foreach (UpdateChannel channel in Enum.GetValues<UpdateChannel>())
            {
                using var language = UiText.UseLanguage(locale);
                var settings = new AppSettings();
                var github = new SettingsUpdateService();
                var store = new ObservableStoreUpdates { Available = true };
                var opened = new List<Uri>();
                var checkedAt = new DateTimeOffset(2026, 10, 22, 14, 2, 0, TimeSpan.Zero);
                using var coordinator = new AppUpdateCoordinator(channel, new UpdateVersion(4, 1, 0),
                    () => settings.Updates, () => { }, () => Task.FromResult(false), () => { },
                    () => Assert.Fail("The settings fixture cannot exit the application."), NullLogger.Instance,
                    AppContext.BaseDirectory, Path.Combine(Path.GetTempPath(), "mc-settings-update-unused"),
                    github: github, store: _ => store, open: opened.Add, clock: () => checkedAt);
                SettingsWindow window = CreateOffscreenSettingsWindow(settings);
                try
                {
                    window.ConfigureUpdates(coordinator);
                    window.Show();
                    ((TabControl)window.FindName("CategoryTabs")).SelectedItem = window.FindName("UpdateTab");
                    Drain(window);
                    var action = (Button)window.FindName("UpdateAction");
                    var automatic = (CheckBox)window.FindName("AutoCheckUpdatesCheckBox");
                    var state = (TextBlock)window.FindName("UpdateObservedStatus");
                    var listing = (Button)window.FindName("StorePageAction");
                    Assert.Equal(channel == UpdateChannel.None ? Visibility.Collapsed : Visibility.Visible, automatic.Visibility);
                    Assert.Equal(channel is UpdateChannel.GitHubInstall or UpdateChannel.Portable
                        ? Visibility.Visible : Visibility.Collapsed, listing.Visibility);
                    if (channel == UpdateChannel.None)
                    {
                        Assert.True(window.RunUpdateActionAsync().IsCompletedSuccessfully);
                        Assert.Equal(0, store.Checks);
                        Assert.Equal(0, github.Checks);
                        Assert.Equal(UiText.Get("StoreUpdate_OpenStore"), action.Content);
                        continue;
                    }

                    Assert.Equal(UiText.Get("StoreUpdate_Check"), action.Content);
                    Assert.True(automatic.IsChecked);
                    automatic.IsChecked = false;
                    Drain(window);
                    Assert.False(DraftOf(window).AutoCheckUpdates);
                    Assert.False(DraftOf(window).ToAppSettings().Updates.AutoCheck);
                    PumpUntil(window, window.RunUpdateActionAsync());
                    Assert.Equal(channel == UpdateChannel.Store ? 1 : 0, store.Checks);
                    Assert.Equal(channel == UpdateChannel.Store ? 0 : 1, github.Checks);
                    Assert.Equal(UiText.Get("StoreUpdate_Install"), action.Content);
                    Assert.Equal(action.Content, AutomationProperties.GetName(action));
                    Assert.Equal(UiText.Get(channel switch
                    {
                        UpdateChannel.Store => "StoreUpdate_InstallHelp",
                        UpdateChannel.Portable => "Update_OpenReleaseHelp",
                        _ => "Update_InstallHelp",
                    }), AutomationProperties.GetHelpText(action));
                    Assert.Equal(SettingsWindow.UpdateCheckSummary(StoreUpdatePhase.Available, checkedAt), state.Text);
                    Assert.True(action.IsEnabled);
                    if (channel == UpdateChannel.Portable)
                    {
                        PumpUntil(window, window.RunUpdateActionAsync());
                        Assert.Equal(SettingsUpdateService.Package.ReleaseUrl, Assert.Single(opened));
                        Assert.Equal(0, github.Downloads);
                    }
                    if (channel is UpdateChannel.GitHubInstall or UpdateChannel.Portable)
                        Assert.Equal(UiText.Get("Update_UnpackagedDescription"), ((TextBlock)window.FindName("UpdateDescription")).Text);
                    automatic.BringIntoView(); Drain(window); AssertFits(window, automatic);
                    SaveStoreUpdateEvidence(window, $"update-tab-shared-{channel}-{locale}-glass-940x700.png");
                }
                finally { window.CloseForExit(); }
                Assert.Null(window.UpdateCoordinator);
            }
        }
        finally { application.Shutdown(); }
    }

    private sealed class SettingsUpdateService : IUpdateService
    {
        internal int Checks, Downloads;
        internal static readonly UpdatePackageInfo Package = new(new UpdateVersion(4, 2, 0), "4.2.0", "",
            new Uri("https://github.com/sukwoo0711-maker/my-capture/releases/tag/v4.2.0"), DateTimeOffset.UnixEpoch,
            "MyCapture-4.2.0-win-x64-setup.exe",
            new Uri("https://github.com/sukwoo0711-maker/my-capture/releases/download/v4.2.0/MyCapture-4.2.0-win-x64-setup.exe"), 100,
            new Uri("https://github.com/sukwoo0711-maker/my-capture/releases/download/v4.2.0/SHA256SUMS.txt"));

        public Task<UpdateCheckResult> CheckForUpdateAsync(UpdateVersion currentVersion, CancellationToken cancellationToken = default)
        {
            Checks++;
            return Task.FromResult(UpdateCheckResult.Available(currentVersion, Package));
        }
        public Task<UpdateCheckResult> CheckForUpdateAsync(Version currentVersion, CancellationToken cancellationToken = default) =>
            CheckForUpdateAsync(UpdateVersion.FromVersion(currentVersion), cancellationToken);
        public Task<StagedUpdateResult> DownloadAndStageAsync(UpdatePackageInfo package, string stagingRoot,
            IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Downloads++;
            throw new InvalidOperationException("This fixture never downloads or launches an installer.");
        }
        public Task<StagedUpdateResult> CheckAndStageAsync(UpdateVersion currentVersion, string stagingRoot,
            IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StagedUpdateResult> CheckAndStageAsync(Version currentVersion, string stagingRoot,
            IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
