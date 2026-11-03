using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LegacyImport_PreservesTargetSettingsUntilApplyAndFailedApplyRetainsDraft() =>
        RunIsolated(nameof(LegacyImport_PreservesTargetSettingsUntilApplyAndFailedApplyRetainsDraft), VerifyLegacyDraft);

    [Fact]
    public Task LegacyImport_PreservesCurrentCoreShortcutAnswerAtImportAndApply() =>
        RunIsolated(nameof(LegacyImport_PreservesCurrentCoreShortcutAnswerAtImportAndApply), VerifyLegacyCoreShortcutAnswer);

    [Fact]
    public Task LegacyImport_RecoveryFailureNeverTrapsTheWindowAndReleasesProtectionOnClose() =>
        RunIsolated(nameof(LegacyImport_RecoveryFailureNeverTrapsTheWindowAndReleasesProtectionOnClose), VerifyLegacyRecovery);

    [Fact]
    public Task LegacyImport_RefreshFailureLeavesRecoveryRetryAvailableWithoutBlockingClose() =>
        RunIsolated(nameof(LegacyImport_RefreshFailureLeavesRecoveryRetryAvailableWithoutBlockingClose), VerifyLegacyRefreshFailure);

    [Fact]
    public Task LegacyImport_PublicationCannotBeCanceledAndALateCancelWithholdsTheSettingsDraft() =>
        RunIsolated(nameof(LegacyImport_PublicationCannotBeCanceledAndALateCancelWithholdsTheSettingsDraft), VerifyLegacyPublicationCancel);

    [Fact]
    public Task LegacyImport_AppExitCancelsACopyAndClosesTheDialog() =>
        RunIsolated(nameof(LegacyImport_AppExitCancelsACopyAndClosesTheDialog), VerifyLegacyExit);

    [Fact]
    public Task LegacyImport_StartupDecisionOffersRetryAndCancelAndNeverBlocksTheLaunch() =>
        RunIsolated(nameof(LegacyImport_StartupDecisionOffersRetryAndCancelAndNeverBlocksTheLaunch), VerifyLegacyStartupDecision);

    private static void VerifyLegacyRefreshFailure()
    {
        Application application = CreateThemedApplication();
        try
        {
            foreach (bool failDuringInitialCompletion in new[] { true, false })
            {
                int refreshes = 0, releases = 0;
                var service = new LegacyFixtureService();
                var operations = LegacyOperations(service, (_, _, _) => Task.FromResult(new LegacyMigrationResult(
                    failDuringInitialCompletion ? LegacyMigrationStatus.Completed : LegacyMigrationStatus.Failed,
                    ErrorCode: failDuringInitialCompletion ? null : "RecoveryRequired")), () => releases++);
                operations = operations with { Refresh = () => { if (++refreshes == 1) throw new IOException("fixture refresh failed"); } };
                var window = NewLegacyWindow(operations);
                window.Show(); Drain(window);
                CompleteLegacyTask(window.ImportAsync());
                if (!failDuringInitialCompletion) CompleteLegacyTask(window.RetryRecoveryAsync());
                Assert.True(window.IsRecoveryRequired); Assert.Null(window.Selection); Assert.Equal(0, releases);
                Button retry = LegacyField<Button>(window, "_copy");
                Assert.True(retry.IsVisible && retry.IsEnabled);
                Assert.True(LegacyField<Button>(window, "_cancel").IsEnabled);
                CompleteLegacyTask(window.RetryRecoveryAsync());
                Assert.False(window.IsRecoveryRequired); Assert.Equal(1, releases);
                window.Close(); Assert.False(window.IsVisible); Assert.Equal(1, releases);
            }
        }
        finally { application.Shutdown(); }
    }

    [Fact]
    public Task LegacyImport_StaleScanAndProgressRemainBoundToTheActiveOperation() =>
        RunIsolated(nameof(LegacyImport_StaleScanAndProgressRemainBoundToTheActiveOperation), VerifyLegacyScanAndProgress);

    [Fact]
    public Task LegacyImport_EntryAndDialogFitBothLanguagesAcrossThemes() =>
        RunIsolated(nameof(LegacyImport_EntryAndDialogFitBothLanguagesAcrossThemes), VerifyLegacyEvidence);

    [Fact]
    public Task LegacyImport_StartupRecoveryDefersTheSpinnerAndTurnsFailureIntoAChoice() =>
        RunIsolated(nameof(LegacyImport_StartupRecoveryDefersTheSpinnerAndTurnsFailureIntoAChoice), VerifyLegacyStartupRecovery);

    [Fact]
    public Task LegacyImport_QuietOrPreviouslyOfferedLaunchDoesNotScanTheLegacyProfile() =>
        RunIsolated(nameof(LegacyImport_QuietOrPreviouslyOfferedLaunchDoesNotScanTheLegacyProfile), VerifyLegacyQuietOffer);

    private static void VerifyLegacyQuietOffer()
    {
        var application = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            var paths = AppPaths.CreateForRoot(TestRecycleBin.CreateTempSubdirectory("mc-legacy-quiet-").FullName);
            paths.EnsureCreated();
            int scans = 0;
            var service = new LegacyFixtureService { Scan = (_, _, _) => { scans++; throw new InvalidOperationException("Must not scan a quiet profile"); } };
            using var services = new ServiceCollection().AddSingleton(paths).AddSingleton<ILegacyLibraryMigrationService>(service).BuildServiceProvider();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(App).GetField("_services", flags)!.SetValue(application, services);
            typeof(App).GetField("_settings", flags)!.SetValue(application, new AppSettings());
            MyCapture.Core.Platform.AppPackage.IsPackagedOverride = true;
            Task quiet = (Task)typeof(App).GetMethod("OfferLegacyImportAfterLaunchAsync", flags)!.Invoke(application, [true])!;
            CompleteLegacyTask(quiet);
            Assert.Equal(0, scans);
            string marker = Path.Combine(paths.DataRoot, "legacy-import-offer.json");
            Assert.False(File.Exists(marker));
            File.WriteAllText(marker, "{\"offered\":true}");
            Task offered = (Task)typeof(App).GetMethod("OfferLegacyImportAfterLaunchAsync", flags)!.Invoke(application, [false])!;
            CompleteLegacyTask(offered); Assert.Equal(0, scans);
        }
        finally { MyCapture.Core.Platform.AppPackage.IsPackagedOverride = null; application.Shutdown(); }
    }

    private static void VerifyLegacyStartupRecovery()
    {
        Application application = CreateThemedApplication();
        try { VerifyLegacyStartupRecoveryCore(); }
        finally { application.Shutdown(); }
    }

    private static void VerifyLegacyStartupRecoveryCore()
    {
        int delays = 0, windows = 0;
        LegacyRecoveryResult clean = new(LegacyRecoveryStatus.Clean);
        CompleteLegacyTask(LegacyMigrationRecoveryWindow.RunAsync(() => Task.FromResult(clean),
            () => throw new InvalidOperationException("Clean recovery never abandons"),
            () => { delays++; return Task.CompletedTask; }, _ => windows++));
        Assert.Equal(0, delays); Assert.Equal(0, windows);
        // A recovery that throws does not escape into a startup failure; it becomes the choice.
        LegacyMigrationRecoveryWindow? shown = null;
        CompleteLegacyTask(LegacyMigrationRecoveryWindow.RunAsync(
            () => Task.FromException<LegacyRecoveryResult>(new IOException("fixture recovery failure")),
            () => Task.FromResult(clean), () => Task.CompletedTask, window => shown = window));
        Assert.NotNull(shown);
        // A slow recovery shows its window only after the grace delay, while it is still running.
        var slow = new TaskCompletionSource<LegacyRecoveryResult>();
        CompleteLegacyTask(LegacyMigrationRecoveryWindow.RunAsync(() => slow.Task, () => Task.FromResult(clean),
            () => Task.CompletedTask, _ => { windows++; Assert.False(slow.Task.IsCompleted); slow.SetResult(clean); }));
        Assert.Equal(1, windows);
    }

    private static void VerifyLegacyStartupDecision()
    {
        Application application = CreateThemedApplication();
        try
        {
            foreach (bool closeFromTitleBar in new[] { false, true })
            {
                int recoveries = 0, abandons = 0;
                var window = new LegacyMigrationRecoveryWindow(
                    Task.FromResult(new LegacyRecoveryResult(LegacyRecoveryStatus.DecisionRequired, "InsufficientSpace")),
                    () => { recoveries++; return Task.FromResult(new LegacyRecoveryResult(LegacyRecoveryStatus.DecisionRequired, "InsufficientSpace")); },
                    () => { abandons++; return Task.FromResult(new LegacyRecoveryResult(LegacyRecoveryStatus.Clean, RolledBackCount: 1)); })
                { Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
                window.Show(); Drain(window);
                Assert.True(window.IsAskingForDecision);
                TextBlock message = Descendants(window).OfType<TextBlock>().Single(text => KoreanWordWrap.Strip(text.Text).Contains(UiText.Get("LegacyImport.StartupReasonSpace")));
                Assert.Contains(UiText.Get("LegacyImport.StartupChoice"), KoreanWordWrap.Strip(message.Text));
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(message));
                Button[] buttons = Descendants(window).OfType<Button>().ToArray();
                Assert.Contains(buttons, button => button.IsDefault && SurfaceShortcutHintTests.PrimaryCaption(button) == UiText.Get("LegacyImport.Retry"));
                Assert.Contains(buttons, button => button.IsCancel && SurfaceShortcutHintTests.PrimaryCaption(button) == UiText.Get("LegacyImport.Abandon"));
                CompleteLegacyTask(window.RetryAsync());
                Assert.Equal(1, recoveries); Assert.True(window.IsAskingForDecision); Assert.True(window.IsVisible);
                if (closeFromTitleBar) window.Close(); // Closing means Cancel import, never an open decision.
                else CompleteLegacyTask(window.AbandonAsync());
                DateTime end = DateTime.UtcNow.AddSeconds(5);
                while (window.IsVisible && DateTime.UtcNow < end) Drain(window);
                Assert.False(window.IsVisible); Assert.Equal(1, abandons);
                Assert.Equal(1, window.Outcome!.RolledBackCount);
            }
            // A retry that succeeds closes without abandoning anything.
            int abandoned = 0;
            var recovered = new LegacyMigrationRecoveryWindow(
                Task.FromResult(new LegacyRecoveryResult(LegacyRecoveryStatus.DecisionRequired, "IoFailure")),
                () => Task.FromResult(new LegacyRecoveryResult(LegacyRecoveryStatus.Clean, CompletedCount: 1)),
                () => { abandoned++; return Task.FromResult(new LegacyRecoveryResult(LegacyRecoveryStatus.Clean)); })
            { Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
            recovered.Show(); Drain(recovered);
            Assert.Contains(Descendants(recovered).OfType<TextBlock>(), text => KoreanWordWrap.Strip(text.Text).Contains(UiText.Get("LegacyImport.StartupReasonAccess")));
            CompleteLegacyTask(recovered.RetryAsync());
            Assert.False(recovered.IsVisible); Assert.Equal(0, abandoned);
        }
        finally { application.Shutdown(); }
    }

    private static void VerifyLegacyCoreShortcutAnswer()
    {
        Application application = CreateThemedApplication();
        try
        {
            foreach (bool asked in new[] { false, true })
            {
                var paths = AppPaths.CreateForRoot(ExportTestDirectory.Create());
                var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
                var current = new AppSettings { Hotkeys = { CoreShortcutsAsked = asked } };
                store.Save(current);
                byte[] before = File.ReadAllBytes(paths.SettingsFile);
                string importPath = Path.Combine(paths.DataRoot, "legacy-core-shortcuts.json");
                store.ExportTo(new AppSettings
                {
                    Hotkeys = { CoreShortcutsAsked = !asked, ForceCoreShortcuts = true },
                }, importPath);
                var applied = new List<AppSettings>();
                var window = new SettingsWindow(() => current, candidate =>
                {
                    applied.Add(candidate);
                    return new SettingsApplyResult(true, true, true, false, []);
                }, NullLogger.Instance, () => store, _ => { })
                { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
                try
                {
                    window.Show(); Drain(window);
                    window.ImportLegacySettingsDraft(importPath);
                    SettingsDraft draft = DraftOf(window);
                    Assert.True(draft.ForceCoreShortcuts);
                    Assert.Equal(asked, draft.ToAppSettings().Hotkeys.CoreShortcutsAsked);
                    Assert.Equal(asked, window.LegacyApplyCandidate(draft.ToAppSettings()).Hotkeys.CoreShortcutsAsked);

                    current.Hotkeys.CoreShortcutsAsked = !asked;
                    AppSettings candidate = window.LegacyApplyCandidate(draft.ToAppSettings());
                    Assert.Equal(!asked, candidate.Hotkeys.CoreShortcutsAsked);
                    Assert.True(candidate.Hotkeys.ForceCoreShortcuts);
                    Assert.Equal(asked, draft.ToAppSettings().Hotkeys.CoreShortcutsAsked);
                    Assert.Equal(before, File.ReadAllBytes(paths.SettingsFile));
                    // The import stays a draft until it is saved.
                    Assert.Empty(applied);
                }
                finally { window.CloseForExit(); }
                // Closing saves the pending draft, still with the current answer.
                Assert.All(applied, candidate => Assert.Equal(!asked, candidate.Hotkeys.CoreShortcutsAsked));
            }
        }
        finally { application.Shutdown(); }
    }

    private static void VerifyLegacyDraft()
    {
        Application application = CreateThemedApplication();
        string root = TestRecycleBin.CreateTempSubdirectory("mc-legacy-draft-").FullName;
        try
        {
            var paths = AppPaths.CreateForRoot(root);
            var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
            var current = new AppSettings
            {
                General = { Theme = AppThemeNames.Glass, IsFirstRun = false, LaunchAtLogin = true },
                Queue = { MaxItems = 777, MaxBytes = 987654321, CapturesDirectoryOverride = Path.Combine(root, "store-captures"),
                    FolderFilePrefix = "retained", UseCurrentFolderForNewCaptures = true, DefaultCaptureFolderId = Guid.NewGuid(),
                    ThumbnailLongEdge = 384, ImageRetentionHours = 720 },
                Export = { QuickSaveDirectoryOverride = Path.Combine(root, "store-exports") },
                GitHub = { Token = "fixture-store-credential", IssueUrl = "https://github.com/store-owner/store-repo/issues/7" },
                Capture = { StartGuideShownCount = 2 },
                Hotkeys = { CoreShortcutsAsked = true },
                StoreRating = { SuccessfulCaptureCount = 17 }, StoreUpdate = { CompletionNoticeVersion = "4.1.2.0" },
                Updates = { LastCheckUtc = DateTimeOffset.UnixEpoch.AddDays(2), DismissedVersion = "4.2.0" },
            };
            store.Save(current);
            byte[] before = File.ReadAllBytes(paths.SettingsFile);
            string oldSettings = Path.Combine(root, "legacy-settings.json");
            store.ExportTo(new AppSettings { General = { Theme = AppThemeNames.Daylight }, Queue = { MaxItems = 12 },
                Updates = { AutoCheck = false, LastCheckUtc = DateTimeOffset.UnixEpoch, DismissedVersion = "1.0.0" },
                GitHub = { Token = "fixture-legacy-credential", IssueUrl = "https://github.com/legacy-owner/legacy-repo/issues/3" } }, oldSettings);
            int applies = 0;
            var window = new SettingsWindow(() => current, _ =>
            {
                applies++;
                return new SettingsApplyResult(false, false, false, false, []);
            }, NullLogger.Instance, () => store, _ => { })
            { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); Drain(window);
                window.ImportLegacySettingsDraft(oldSettings);
                SettingsDraft draft = DraftOf(window);
                AppSettings candidate = window.LegacyApplyCandidate(draft.ToAppSettings());
                Assert.Equal(AppThemeNames.GlassLight, candidate.General.Theme);
                Assert.Equal(JsonSerializer.Serialize(current.Queue), JsonSerializer.Serialize(candidate.Queue));
                Assert.Equal(current.Export.QuickSaveDirectoryOverride, candidate.Export.QuickSaveDirectoryOverride);
                Assert.Equal(current.GitHub.Token, candidate.GitHub.Token);
                // The upload target stays paired with the kept token.
                Assert.Equal(current.GitHub.IssueUrl, draft.GitHubIssueUrl);
                Assert.Equal(current.GitHub.IssueUrl, candidate.GitHub.IssueUrl);
                Assert.Equal(current.General.IsFirstRun, candidate.General.IsFirstRun);
                Assert.True(draft.ToAppSettings().Hotkeys.CoreShortcutsAsked);
                Assert.True(candidate.Hotkeys.CoreShortcutsAsked);
                Assert.Equal(current.General.LaunchAtLogin, candidate.General.LaunchAtLogin);
                Assert.Equal(current.Capture.StartGuideShownCount, candidate.Capture.StartGuideShownCount);
                Assert.Equal(current.StoreRating.SuccessfulCaptureCount, candidate.StoreRating.SuccessfulCaptureCount);
                Assert.False(candidate.Updates.AutoCheck);
                Assert.Equal(current.Updates.LastCheckUtc, candidate.Updates.LastCheckUtc);
                Assert.Equal("4.2.0", candidate.Updates.DismissedVersion);
                // A check or dismissal after import stays live when the draft is applied later.
                current.Updates.LastCheckUtc = DateTimeOffset.UnixEpoch.AddDays(3);
                current.Updates.DismissedVersion = "4.3.0";
                candidate = window.LegacyApplyCandidate(draft.ToAppSettings());
                Assert.Equal(current.Updates.LastCheckUtc, candidate.Updates.LastCheckUtc);
                Assert.Equal("4.3.0", candidate.Updates.DismissedVersion);
                Assert.Equal(before, File.ReadAllBytes(paths.SettingsFile));
                Assert.Equal(0, applies);
                SettingsWindow.ApplyCommand.Execute(null, window);
                Assert.Equal(1, applies);
                Assert.Same(draft, window.DataContext);
                Assert.True(draft.HasUnappliedChanges);
                Assert.Equal(before, File.ReadAllBytes(paths.SettingsFile));
                Assert.Equal("4.1.2.0", current.StoreUpdate.CompletionNoticeVersion);
                draft.MaxItems = "888";
                draft.QuickSaveDirectoryOverride = Path.Combine(root, "chosen-exports");
                draft.GitHubToken = "fixture-user-edited-credential";
                draft.GitHubIssueUrl = "https://github.com/chosen-owner/chosen-repo/issues/9";
                draft.LaunchAtLogin = false;
                candidate = window.LegacyApplyCandidate(draft.ToAppSettings());
                Assert.Equal(888, candidate.Queue.MaxItems);
                Assert.Equal(current.Queue.MaxBytes, candidate.Queue.MaxBytes);
                Assert.Equal(draft.QuickSaveDirectoryOverride, candidate.Export.QuickSaveDirectoryOverride);
                Assert.Equal(draft.GitHubToken, candidate.GitHub.Token);
                Assert.Equal("https://github.com/chosen-owner/chosen-repo/issues/9", candidate.GitHub.IssueUrl);
                Assert.False(candidate.General.LaunchAtLogin);
                SettingsWindow.ApplyCommand.Execute(null, window);
                Assert.Equal(2, applies); Assert.Same(draft, window.DataContext);
                Assert.Equal(before, File.ReadAllBytes(paths.SettingsFile));
                var invalidHotkeys = new AppSettings();
                invalidHotkeys.Hotkeys.OpenLibrary = invalidHotkeys.Hotkeys.Capture;
                string invalidImport = Path.Combine(root, "legacy-duplicate-hotkeys.json");
                store.ExportTo(invalidHotkeys, invalidImport);
                window.ImportLegacySettingsDraft(invalidImport);
                Assert.True(draft.HasErrors);
                draft.OpenLibraryHotkey = "Ctrl+Shift+Z";
                Assert.False(draft.HasErrors);
                candidate = window.LegacyApplyCandidate(draft.ToAppSettings());
                Assert.Equal(current.Queue.MaxBytes, candidate.Queue.MaxBytes);
                Assert.Equal(JsonSerializer.Serialize(current.Queue), JsonSerializer.Serialize(candidate.Queue));
                Assert.Equal(current.GitHub.IssueUrl, candidate.GitHub.IssueUrl);
            }
            finally { window.CloseForExit(); }
        }
        finally { application.Shutdown(); }
    }

    private static LegacyDataImportWindow NewLegacyWindow(LegacyImportOperations operations)
    {
        var window = new LegacyDataImportWindow(operations, "C:\\Fixture\\Existing data")
        { Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        FluidMotion.SetWindowEntrance(window, false);
        return window;
    }

    private static T LegacyField<T>(LegacyDataImportWindow window, string field) =>
        (T)typeof(LegacyDataImportWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void CompleteLegacyTask(Task task)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < end)
        {
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
        Assert.True(task.IsCompleted, "Legacy import operation did not complete within the fixture bound.");
        task.GetAwaiter().GetResult();
    }

    private static LegacyImportOperations LegacyOperations(LegacyFixtureService service, Func<string, IProgress<LegacyMigrationProgress>, CancellationToken, Task<LegacyMigrationResult>> import,
        Action? release = null, Func<Task>? recover = null) => new(service, AppPaths.CreateForRoot(TestRecycleBin.CreateTempSubdirectory("mc-legacy-target-").FullName),
            _ => Task.FromResult(true), release ?? (() => { }), import, () => { }, recover ?? (() => Task.CompletedTask));

    private static void VerifyLegacyRecovery()
    {
        Application application = CreateThemedApplication();
        try
        {
            // Closing during a copy cancels it and closes as soon as it stops, even if it then
            // reports a recovery problem; closing returns the protected windows and exit.
            int releases = 0;
            var service = new LegacyFixtureService();
            var import = new TaskCompletionSource<LegacyMigrationResult>();
            CancellationToken observed = default;
            var window = NewLegacyWindow(LegacyOperations(service, (_, _, token) => { observed = token; return import.Task; }, () => releases++));
            window.Show(); Drain(window);
            Task copying = window.ImportAsync();
            window.Close();
            Assert.True(window.IsVisible); Assert.True(observed.IsCancellationRequested);
            Assert.Equal(UiText.Get("LegacyImport.Canceling"), KoreanWordWrap.Strip(LegacyField<TextBlock>(window, "_status").Text));
            Assert.Equal(0, releases);
            import.SetResult(new(LegacyMigrationStatus.Failed, ErrorCode: "RecoveryRequired"));
            CompleteLegacyTask(copying);
            Assert.False(window.IsVisible); Assert.Equal(1, releases);

            // Pending recovery offers a retry but never disables Close or Esc.
            int secondReleases = 0, recoveryAttempts = 0;
            var recovery = new TaskCompletionSource();
            var second = NewLegacyWindow(LegacyOperations(service,
                (_, _, _) => Task.FromResult(new LegacyMigrationResult(LegacyMigrationStatus.Failed, ErrorCode: "RecoveryRequired")),
                () => secondReleases++, () =>
                {
                    recoveryAttempts++;
                    return recoveryAttempts == 1 ? Task.FromException(new IOException("fixture recovery unavailable")) : recovery.Task;
                }));
            second.Show(); Drain(second);
            CompleteLegacyTask(second.ImportAsync());
            Assert.True(second.IsRecoveryRequired); Assert.Equal(0, secondReleases);
            Button close = LegacyField<Button>(second, "_cancel");
            Assert.True(close.IsEnabled && close.IsCancel);
            Assert.Equal(UiText.Get("LegacyImport.RecoveryRequired"), KoreanWordWrap.Strip(LegacyField<TextBlock>(second, "_status").Text));
            CompleteLegacyTask(second.RetryRecoveryAsync());
            Assert.True(second.IsRecoveryRequired); Assert.Equal(0, secondReleases); Assert.True(close.IsEnabled);
            Task retry = second.RetryRecoveryAsync();
            Assert.True(second.IsWorking);
            second.Close(); Assert.True(second.IsVisible); // Only the running recovery write defers closing.
            recovery.SetResult(); CompleteLegacyTask(retry);
            Assert.False(second.IsRecoveryRequired); Assert.Equal(1, secondReleases);
            Assert.Null(second.Selection); // Recovery alone does not invent an import completion.
            Assert.False(second.IsVisible);

            // Closing while recovery is still pending releases protection at once.
            int thirdReleases = 0;
            var third = NewLegacyWindow(LegacyOperations(service,
                (_, _, _) => Task.FromResult(new LegacyMigrationResult(LegacyMigrationStatus.Failed, ErrorCode: "RecoveryRequired")),
                () => thirdReleases++, () => Task.FromException(new IOException("still unavailable"))));
            third.Show(); Drain(third);
            CompleteLegacyTask(third.ImportAsync());
            Assert.True(third.IsRecoveryRequired);
            third.Close();
            Assert.False(third.IsVisible); Assert.Equal(1, thirdReleases);
        }
        finally { application.Shutdown(); }
    }

    private static void VerifyLegacyPublicationCancel()
    {
        Application application = CreateThemedApplication();
        try
        {
            using var language = UiText.UseLanguage("ko-KR");
            var service = new LegacyFixtureService();
            // Publication started: Cancel is disabled with honest text, and Close waits for it.
            var import = new TaskCompletionSource<LegacyMigrationResult>();
            IProgress<LegacyMigrationProgress>? observer = null;
            var window = NewLegacyWindow(LegacyOperations(service, (_, progress, _) => { observer = progress; return import.Task; }));
            window.Show(); Drain(window);
            LegacyField<CheckBox>(window, "_includeSettings").IsChecked = true;
            Task publishing = window.ImportAsync();
            observer!.Report(new(LegacyMigrationStage.Publishing, 2, 2, 10, 10)); Drain(window);
            Button cancel = LegacyField<Button>(window, "_cancel");
            Assert.False(cancel.IsEnabled);
            Assert.Equal(UiText.Get("LegacyImport.PublishingNoCancel"), KoreanWordWrap.Strip(LegacyField<TextBlock>(window, "_status").Text));
            window.Close(); Assert.True(window.IsVisible);
            import.SetResult(new(LegacyMigrationStatus.Completed, 2, 0, "C:\\Fixture\\legacy-settings.json"));
            CompleteLegacyTask(publishing);
            Assert.False(window.IsVisible);
            Assert.True(window.Selection!.IncludeSettings); Assert.False(window.Selection.CompletedAfterCancel);

            // Cancel requested during the copy, but the import committed first: no settings draft.
            var late = new TaskCompletionSource<LegacyMigrationResult>();
            var raced = NewLegacyWindow(LegacyOperations(service, (_, _, _) => late.Task));
            raced.Show(); Drain(raced);
            LegacyField<CheckBox>(raced, "_includeSettings").IsChecked = true;
            Task copying = raced.ImportAsync();
            LegacyField<Button>(raced, "_cancel").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            late.SetResult(new(LegacyMigrationStatus.Completed, 1, 0, "C:\\Fixture\\legacy-settings.json"));
            CompleteLegacyTask(copying);
            Assert.False(raced.IsVisible);
            Assert.False(raced.Selection!.IncludeSettings); Assert.True(raced.Selection.CompletedAfterCancel);
            Assert.StartsWith(UiText.Get("LegacyImport.CompletedBeforeCancel"), KoreanWordWrap.Strip(LegacyField<TextBlock>(raced, "_status").Text));
        }
        finally { application.Shutdown(); }
    }

    private static void VerifyLegacyExit()
    {
        Application application = CreateThemedApplication();
        try
        {
            int releases = 0;
            var window = NewLegacyWindow(LegacyOperations(new LegacyFixtureService(), (_, _, token) =>
            {
                // Like the service: cancellation during the copy returns Canceled after rolling back.
                var canceled = new TaskCompletionSource<LegacyMigrationResult>();
                token.Register(() => canceled.TrySetResult(new(LegacyMigrationStatus.Canceled)));
                return canceled.Task;
            }, () => releases++));
            window.Show(); Drain(window);
            Task copying = window.ImportAsync();
            Assert.True(window.IsWorking);
            CompleteLegacyTask(window.StopForExitAsync());
            Assert.True(copying.IsCompleted);
            Assert.False(window.IsVisible); Assert.Equal(1, releases); Assert.Null(window.Selection);
        }
        finally { application.Shutdown(); }
    }

    private static void VerifyLegacyScanAndProgress()
    {
        Application application = CreateThemedApplication();
        try
        {
            var firstScan = new TaskCompletionSource<LegacyMigrationScan>();
            var service = new LegacyFixtureService { Scan = (_, _, _) => firstScan.Task };
            var import = new TaskCompletionSource<LegacyMigrationResult>();
            IProgress<LegacyMigrationProgress>? observer = null;
            int releases = 0;
            var window = NewLegacyWindow(LegacyOperations(service, (_, progress, _) => { observer = progress; return import.Task; }, () => releases++));
            window.Show(); Drain(window);
            service.Scan = (_, _, _) => Task.FromResult(new LegacyMigrationScan(LegacyMigrationScanStatus.Ready, 3, 1, 2, 200, true,
                UnusableCount: 2, SettingsUnreadable: false));
            CompleteLegacyTask(window.ScanAsync());
            firstScan.SetResult(new(LegacyMigrationScanStatus.Failed, ErrorCode: "InvalidSource")); Drain(window);
            Assert.Equal(UiText.Get("LegacyImport.Ready"), KoreanWordWrap.Strip(LegacyField<TextBlock>(window, "_status").Text));
            Assert.Contains(UiText.Format("LegacyImport.Unusable", 2), KoreanWordWrap.Strip(LegacyField<TextBlock>(window, "_summary").Text));
            Button copy = LegacyField<Button>(window, "_copy"), cancel = LegacyField<Button>(window, "_cancel");
            Assert.True(copy.IsEnabled && copy.IsDefault); Assert.True(cancel.IsCancel);
            Assert.False(LegacyField<CheckBox>(window, "_includeSettings").IsChecked);
            ProgressBar bar = LegacyField<ProgressBar>(window, "_progress");
            Assert.Equal(UiText.Get("LegacyImport.ProgressName"), AutomationProperties.GetName(bar));
            FocusManager.SetFocusedElement(window, copy);
            Task copying = window.ImportAsync();
            observer!.Report(new(LegacyMigrationStage.Scanning, 2, 4, 0, 0)); Drain(window);
            Assert.False(bar.IsIndeterminate); Assert.Equal(50, bar.Value);
            Assert.Equal(UiText.Format("LegacyImport.ScanProgress", 2, 4), KoreanWordWrap.Strip(LegacyField<TextBlock>(window, "_status").Text));
            observer.Report(new(LegacyMigrationStage.Copying, 1, 4, 50, 200)); Drain(window);
            int announcements = window.AnnouncementCountForTest;
            observer.Report(new(LegacyMigrationStage.Copying, 2, 4, 100, 200)); Drain(window);
            Assert.Equal(announcements, window.AnnouncementCountForTest); // Ticks are not re-announced.
            Assert.False(bar.IsIndeterminate);
            Assert.Equal(50, bar.Value);
            Assert.True(cancel.IsEnabled);
            observer.Report(new(LegacyMigrationStage.Publishing, 4, 4, 200, 200)); Drain(window);
            Assert.True(bar.IsIndeterminate); Assert.False(cancel.IsEnabled);
            Assert.Equal(announcements + 1, window.AnnouncementCountForTest);
            import.SetResult(new(LegacyMigrationStatus.Completed, 4, 0, UnusableCount: 2)); CompleteLegacyTask(copying);
            string completed = LegacyField<TextBlock>(window, "_status").Text;
            Assert.Equal(UiText.Format("LegacyImport.CompletedWithUnusable", 4, 0, 2), KoreanWordWrap.Strip(completed));
            observer.Report(new(LegacyMigrationStage.Copying, 0, 4, 0, 200)); Drain(window);
            Assert.Equal(completed, LegacyField<TextBlock>(window, "_status").Text);
            Assert.Equal(1, releases); Assert.NotNull(window.Selection);
            // The finished action leaves; focus and Enter move to the remaining Close button.
            Assert.Equal(Visibility.Collapsed, copy.Visibility);
            Assert.True(cancel.IsEnabled && cancel.IsDefault);
            Assert.Same(cancel, FocusManager.GetFocusedElement(window));
            window.Close();
            string generic = LegacyDataImportWindow.ErrorText((string?)null);
            foreach (string code in new[] { "OverlappingRoots", "TargetChanged", "MissingIndex", "InsufficientSpace", "TargetUnreadable", "InvalidJournal" })
                Assert.NotEqual(generic, LegacyDataImportWindow.ErrorText(code));
            string space = LegacyDataImportWindow.ErrorText(new LegacyMigrationResult(LegacyMigrationStatus.Failed,
                ErrorCode: "InsufficientSpace", RequiredBytes: 3L * 1024 * 1024 * 1024, AvailableBytes: 1024L * 1024 * 1024));
            Assert.Equal(UiText.Format("LegacyImport.InsufficientSpaceAmount", LegacyDataImportWindow.FormatBytes(2L * 1024 * 1024 * 1024)), space);
            Assert.Contains("2", LegacyDataImportWindow.FormatBytes(2L * 1024 * 1024 * 1024));
        }
        finally { application.Shutdown(); }
    }

    private static void VerifyLegacyEvidence()
    {
        Application application = CreateThemedApplication();
        bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            MyCapture.Core.Platform.AppPackage.IsPackagedOverride = true;
            foreach (string locale in new[] { "ko-KR", "en-US" })
            foreach (string theme in new[] { AppThemeNames.Daylight, AppThemeNames.Midnight, AppThemeNames.HighContrast })
            {
                using var language = UiText.UseLanguage(locale);
                ThemeService.ApplyFromSettings(theme);
                var saved = new AppSettings { General = { Theme = theme } };
                SettingsWindow settings = CreateOffscreenSettingsWindow(saved);
                try
                {
                    settings.LegacyImportPackaged = true;
                    settings.Width = 780; settings.Height = 560;
                    settings.Show();
                    Assert.True(settings.NavigateToSection(SettingsSection.Storage));
                    Assert.IsType<Expander>(settings.FindName("BackupExpander")).IsExpanded = true;
                    Drain(settings);
                    Button entry = (Button)settings.FindName("LegacyImportAction");
                    // The import sits in storage's named backup group, below the fold at this size.
                    entry.BringIntoView(); Drain(settings);
                    Assert.True(entry.IsVisible); AssertFits(settings, entry);
                    SaveStoreUpdateEvidence(settings, $"legacy-information-{locale}-{theme}-780x560.png");
                    settings.LegacyImportPackaged = false; Assert.Equal(Visibility.Collapsed, entry.Visibility);
                }
                finally { settings.CloseForExit(); }
                var service = new LegacyFixtureService
                {
                    Scan = (_, _, _) => Task.FromResult(new LegacyMigrationScan(LegacyMigrationScanStatus.Ready, 12, 3, 4, 150000, false,
                        UnusableCount: 2, SettingsUnreadable: true)),
                };
                var dialog = NewLegacyWindow(LegacyOperations(service, (_, _, _) => Task.FromResult(new LegacyMigrationResult(LegacyMigrationStatus.Completed))));
                dialog.Width = 420;
                dialog.Show(); Drain(dialog);
                Assert.True(LegacyField<TextBlock>(dialog, "_settingsNote").IsVisible);
                foreach (int height in new[] { 500, 430 })
                {
                    dialog.Height = height; Drain(dialog);
                    foreach (Button button in Descendants(dialog).OfType<Button>())
                    {
                        Rect bounds = button.TransformToAncestor(dialog).TransformBounds(new Rect(button.RenderSize));
                        Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= dialog.ActualWidth
                            && bounds.Bottom <= dialog.ActualHeight, $"Import button outside dialog: {bounds}");
                    }
                    TextBlock status = LegacyField<TextBlock>(dialog, "_status");
                    Rect statusBounds = status.TransformToAncestor(dialog).TransformBounds(new Rect(status.RenderSize));
                    Assert.True(statusBounds.Top >= 0 && statusBounds.Bottom < dialog.ActualHeight);
                    Assert.NotEmpty(status.Text);
                    Assert.DoesNotContain(status, Descendants(Descendants(dialog).OfType<ScrollViewer>().Single()));
                    SaveStoreUpdateEvidence(dialog, $"legacy-dialog-{locale}-{theme}-420x{height}.png");
                }
                dialog.Close();
            }
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
            MyCapture.Core.Platform.AppPackage.IsPackagedOverride = null;
            application.Shutdown();
        }
    }

    private sealed class LegacyFixtureService : ILegacyLibraryMigrationService
    {
        internal Func<string, AppPaths, CancellationToken, Task<LegacyMigrationScan>> Scan { get; set; } =
            (_, _, _) => Task.FromResult(new LegacyMigrationScan(LegacyMigrationScanStatus.Ready, 12, 3, 4, 150000, true));
        public Task<LegacyMigrationScan> ScanAsync(string sourceDataRoot, AppPaths targetPaths, CancellationToken cancellationToken = default) => Scan(sourceDataRoot, targetPaths, cancellationToken);
        public Task<LegacyMigrationResult> ImportAsync(string sourceDataRoot, AppPaths targetPaths, MyCapture.Core.Queue.CaptureQueue quiescedTargetQueue,
            AppSettings currentSettings, IProgress<LegacyMigrationProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LegacyRecoveryResult> RecoverPendingAsync(AppPaths targetPaths, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LegacyRecoveryResult(LegacyRecoveryStatus.Clean));
        public Task<LegacyRecoveryResult> AbandonPendingAsync(AppPaths targetPaths, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LegacyRecoveryResult(LegacyRecoveryStatus.Clean));
    }
}
