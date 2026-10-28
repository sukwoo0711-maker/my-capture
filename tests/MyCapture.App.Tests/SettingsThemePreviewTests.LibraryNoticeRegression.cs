using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.App.Store;
using MyCapture.App.Themes;
using MyCapture.App.Updates;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryOverhaul_PendingNoticeSurvivesHiddenStatusExpiryAndPausesWhileHidden() =>
        RunIsolated(nameof(LibraryOverhaul_PendingNoticeSurvivesHiddenStatusExpiryAndPausesWhileHidden), () =>
        {
            Application application = CreateLibraryMotionApplication();
            using var scope = UiText.UseLanguage("ko-KR");
            LibraryUxFixture fixture = CreateLibraryUxFixture("mc-notice-hidden-");
            GalleryWindow window = fixture.Window;
            try
            {
                window.Show(); SettleGalleryLayout(window);
                var notice = new LibraryEngagementNotice(window);
                int consumed = 0;
                notice.ClosedByUser = kind =>
                {
                    Assert.Equal(EngagementNoticeKind.UpdateCompleted, kind);
                    consumed++;
                };
                window.ShowEngagementStatusForTest("saved", "Undo", () => Task.CompletedTask);
                Assert.False(notice.ShowUpdateCompleted("4.9.2"));
                window.Hide();
                window.ExpireActionStatus();
                window.AdvanceNoticeForTest(TimeSpan.FromMinutes(1));
                Assert.Equal(0, consumed);
                Assert.False(window.IsNoticeVisible);
                Assert.Equal(GalleryWindow.UpdateDoneLifetime, window.NoticeRemainingForTest);

                window.Show(); SettleGalleryLayout(window);
                Assert.True(window.IsNoticeVisible);
                Assert.Equal(1, consumed);
                Assert.Equal(UiText.Format("StoreUpdate_CompletedVersion", "4.9.2"), window.NoticeTextForTest);
                window.Hide();
                TimeSpan? remaining = window.NoticeRemainingForTest;
                window.AdvanceNoticeForTest(TimeSpan.FromMinutes(1));
                Assert.Equal(remaining, window.NoticeRemainingForTest);
                window.Show(); SettleGalleryLayout(window);
                Assert.Equal(1, consumed);
                window.AdvanceNoticeForTest(GalleryWindow.UpdateDoneLifetime);
                Assert.False(notice.IsShown);

                // The window being visible is insufficient while the notice's ancestor is hidden.
                var bar = (FrameworkElement)window.FindName("LibraryStatusBar");
                bar.Visibility = Visibility.Collapsed;
                Assert.False(notice.ShowUpdateCompleted("4.9.3"));
                window.AdvanceNoticeForTest(TimeSpan.FromMinutes(1));
                Assert.Equal(1, consumed);
                bar.Visibility = Visibility.Visible;
                SettleGalleryLayout(window);
                Assert.Equal(2, consumed);
                Assert.True(window.IsNoticeVisible);
                window.CloseForExit();
                Assert.Null(window.NoticeRemainingForTest);
            }
            finally { window.CloseForExit(); application.Shutdown(); }
        });

    [Fact]
    public Task LibraryOverhaul_RatingToUpdateCancelsOldCallbacksAndHideClearsBothAreas() =>
        RunIsolated(nameof(LibraryOverhaul_RatingToUpdateCancelsOldCallbacksAndHideClearsBothAreas), () =>
        {
            Application application = CreateLibraryMotionApplication();
            using var scope = UiText.UseLanguage("en-US");
            LibraryUxFixture fixture = CreateLibraryUxFixture("mc-notice-transition-");
            GalleryWindow window = fixture.Window;
            try
            {
                window.Show(); SettleGalleryLayout(window);
                var notice = new LibraryEngagementNotice(window);
                var update = (Button)window.FindName("UpdateAvailableButton");
                int reviews = 0;
                notice.ReviewChosen = () => reviews++;
                foreach (bool pending in new[] { false, true })
                {
                    if (pending) window.ShowEngagementStatusForTest("saved", "Undo", () => Task.CompletedTask);
                    notice.ShowInvitation();
                    notice.ShowUpdateAvailable("4.9.3");
                    window.AdvanceNoticeForTest(GalleryWindow.InvitationLifetime);
                    window.InvokeNoticeActionForTest();
                    window.ExpireActionStatus();
                    Assert.Equal(0, reviews);
                    Assert.Null(window.NoticeRemainingForTest);
                    Assert.Equal(EngagementNoticeKind.UpdateAvailable, notice.Kind);
                    Assert.True(update.IsVisible);
                    Assert.False(window.IsNoticeVisible);
                    notice.Hide();
                    Assert.Equal(EngagementNoticeKind.None, notice.Kind);
                    Assert.False(update.IsVisible);
                    Assert.False(window.IsNoticeVisible);
                }

                // An action may synchronously publish the next state before its click handler returns.
                notice.ReviewChosen = () => { reviews++; notice.ShowUpdateAvailable("4.9.4"); };
                notice.ShowInvitation();
                window.InvokeNoticeActionForTest();
                Assert.Equal(1, reviews);
                Assert.Equal(EngagementNoticeKind.UpdateAvailable, notice.Kind);
                notice.Hide();
                Assert.False(update.IsVisible);
                Assert.False(window.IsNoticeVisible);
            }
            finally { window.CloseForExit(); application.Shutdown(); }
        });

    [Fact]
    public Task LibraryOverhaul_FailedInstallationOffersRetryAndDismissesTheOfferedUpdate() =>
        RunIsolated(nameof(LibraryOverhaul_FailedInstallationOffersRetryAndDismissesTheOfferedUpdate), () =>
        {
            Application application = CreateLibraryMotionApplication();
            foreach (string language in new[] { "ko-KR", "en-US" })
            {
                using var scope = UiText.UseLanguage(language);
                LibraryUxFixture fixture = CreateLibraryUxFixture("mc-notice-failure-");
                GalleryWindow window = fixture.Window;
                var state = new UpdateSettings();
                var service = new FailedLibraryStoreUpdate();
                using var updates = new AppUpdateCoordinator(UpdateChannel.Store,
                    UpdateVersion.FromVersion(new Version(4, 9, 0)), () => state, () => { },
                    () => Task.FromResult(true), () => { }, () => throw new InvalidOperationException("Unexpected exit"),
                    NullLogger.Instance, AppContext.BaseDirectory, AppContext.BaseDirectory, store: _ => service);
                try
                {
                    window.Show(); SettleGalleryLayout(window);
                    var notice = new LibraryEngagementNotice(window);
                    var update = (Button)window.FindName("UpdateAvailableButton");
                    var dismiss = (Button)window.FindName("UpdateDismissButton");
                    Task? retry = null;
                    notice.UpdateChosen = () => retry = updates.InstallAsync(window);
                    notice.ClosedByUser = kind =>
                    {
                        Assert.Equal(EngagementNoticeKind.UpdateAvailable, kind);
                        updates.DismissAvailable();
                    };
                    updates.Changed += () =>
                    {
                        if (!updates.ShowInstallationNotice) return;
                        notice.ShowUpdateAvailable(updates.AvailableVersion, updates.IsBusy,
                            updates.StatusText ?? updates.Snapshot.Phase.ToString(),
                            failed: updates.Snapshot.Phase == StoreUpdatePhase.Failed);
                        if (updates.IsBusy)
                        {
                            Assert.False(update.IsEnabled);
                            Assert.False(dismiss.IsVisible);
                        }
                    };
                    updates.CheckAsync(window).GetAwaiter().GetResult();
                    updates.InstallAsync(window).GetAwaiter().GetResult();
                    SettleGalleryLayout(window);
                    Assert.Equal(StoreUpdatePhase.Failed, updates.Snapshot.Phase);
                    Assert.False(updates.IsBusy);
                    Assert.True(update.IsEnabled && dismiss.IsVisible);
                    Assert.Equal(UiText.Get("Update_RetryAction"), ((TextBlock)update.Content).Text);
                    Assert.Equal(UiText.Get("Update_InstallFailed"), update.ToolTip);
                    Assert.Equal(UiText.Get("Dialog.Close"), dismiss.Content);
                    update.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.NotNull(retry);
                    retry.GetAwaiter().GetResult();
                    Assert.Equal(2, service.Installations);
                    Assert.True(update.IsEnabled && dismiss.IsVisible);
                    dismiss.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal(updates.AvailableKey, state.DismissedVersion);
                    Assert.False(updates.ShowInstallationNotice);
                    Assert.False(update.IsVisible || dismiss.IsVisible);
                    Assert.Equal(EngagementNoticeKind.None, notice.Kind);
                }
                finally { window.CloseForExit(); }
            }
            application.Shutdown();
        });

    private sealed class FailedLibraryStoreUpdate : IStorePackageUpdateService
    {
        internal int Installations { get; private set; }
        public Task<bool> CheckAsync() => Task.FromResult(true);
        public Task<StoreUpdatePhase> InstallAsync(IProgress<StoreUpdateSnapshot> progress)
        {
            Installations++;
            progress.Report(new(StoreUpdatePhase.Downloading, .5));
            return Task.FromResult(StoreUpdatePhase.Failed);
        }
    }
}
