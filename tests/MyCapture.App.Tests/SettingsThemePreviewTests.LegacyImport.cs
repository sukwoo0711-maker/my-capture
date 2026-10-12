using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
    public Task LegacyImport_RecoveryFailureHoldsProtectionAndBlocksCloseUntilSuccessfulRetry() =>
        RunIsolated(nameof(LegacyImport_RecoveryFailureHoldsProtectionAndBlocksCloseUntilSuccessfulRetry), VerifyLegacyRecovery);

    [Fact]
    public Task LegacyImport_RefreshFailureLeavesRecoveryRetryAvailableAndProtectionHeld() =>
        RunIsolated(nameof(LegacyImport_RefreshFailureLeavesRecoveryRetryAvailableAndProtectionHeld), VerifyLegacyRefreshFailure);

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
                window.Close(); Assert.True(window.IsVisible);
                CompleteLegacyTask(window.RetryRecoveryAsync());
                Assert.False(window.IsRecoveryRequired); Assert.Equal(1, releases);
                window.Close(); Assert.False(window.IsVisible);
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
    public Task LegacyImport_StartupRecoveryDefersTheSpinnerAndPropagatesFailureBeforeLoading() =>
        RunIsolated(nameof(LegacyImport_StartupRecoveryDefersTheSpinnerAndPropagatesFailureBeforeLoading), VerifyLegacyStartupRecovery);

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
        int delays = 0, windows = 0, loads = 0;
        CompleteLegacyTask(LegacyMigrationRecoveryWindow.AwaitAsync(Task.CompletedTask,
            () => { delays++; return Task.CompletedTask; }, _ => windows++));
        Assert.Equal(0, delays); Assert.Equal(0, windows);
        Task faulted = LegacyMigrationRecoveryWindow.AwaitAsync(Task.FromException(new IOException("fixture recovery failure")),
            () => Task.CompletedTask, _ => windows++);
        Assert.Throws<IOException>(() => CompleteLegacyTask(faulted));
        if (faulted.IsCompletedSuccessfully) loads++;
        Assert.Equal(0, loads); Assert.Equal(0, windows);
        var recovery = new TaskCompletionSource();
        CompleteLegacyTask(LegacyMigrationRecoveryWindow.AwaitAsync(recovery.Task, () => Task.CompletedTask,
            task => { windows++; Assert.False(task.IsCompleted); recovery.SetResult(); }));
        Assert.Equal(1, windows);
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
                GitHub = { Token = "fixture-store-credential" }, Capture = { StartGuideShownCount = 2 },
                StoreRating = { SuccessfulCaptureCount = 17 }, StoreUpdate = { PendingFromVersion = "4.1.1.0" },
            };
            store.Save(current);
            byte[] before = File.ReadAllBytes(paths.SettingsFile);
            string oldSettings = Path.Combine(root, "legacy-settings.json");
            store.ExportTo(new AppSettings { General = { Theme = AppThemeNames.Daylight }, Queue = { MaxItems = 12 },
                GitHub = { Token = "fixture-legacy-credential" } }, oldSettings);
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
                Assert.Equal(AppThemeNames.Daylight, candidate.General.Theme);
                Assert.Equal(JsonSerializer.Serialize(current.Queue), JsonSerializer.Serialize(candidate.Queue));
                Assert.Equal(current.Export.QuickSaveDirectoryOverride, candidate.Export.QuickSaveDirectoryOverride);
                Assert.Equal(current.GitHub.Token, candidate.GitHub.Token);
                Assert.Equal(current.General.IsFirstRun, candidate.General.IsFirstRun);
                Assert.Equal(current.General.LaunchAtLogin, candidate.General.LaunchAtLogin);
                Assert.Equal(current.Capture.StartGuideShownCount, candidate.Capture.StartGuideShownCount);
                Assert.Equal(current.StoreRating.SuccessfulCaptureCount, candidate.StoreRating.SuccessfulCaptureCount);
                Assert.Equal(before, File.ReadAllBytes(paths.SettingsFile));
                Assert.Equal(0, applies);
                SettingsWindow.ApplyCommand.Execute(null, window);
                Assert.Equal(1, applies);
                Assert.Same(draft, window.DataContext);
                Assert.True(draft.HasUnappliedChanges);
                Assert.Equal(before, File.ReadAllBytes(paths.SettingsFile));
                Assert.Equal("4.1.1.0", current.StoreUpdate.PendingFromVersion);
                draft.MaxItems = "888";
                draft.QuickSaveDirectoryOverride = Path.Combine(root, "chosen-exports");
                draft.GitHubToken = "fixture-user-edited-credential";
                draft.LaunchAtLogin = false;
                candidate = window.LegacyApplyCandidate(draft.ToAppSettings());
                Assert.Equal(888, candidate.Queue.MaxItems);
                Assert.Equal(current.Queue.MaxBytes, candidate.Queue.MaxBytes);
                Assert.Equal(draft.QuickSaveDirectoryOverride, candidate.Export.QuickSaveDirectoryOverride);
                Assert.Equal(draft.GitHubToken, candidate.GitHub.Token);
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
            int releases = 0, recoveryAttempts = 0;
            var service = new LegacyFixtureService();
            var import = new TaskCompletionSource<LegacyMigrationResult>();
            var recovery = new TaskCompletionSource();
            var window = NewLegacyWindow(LegacyOperations(service, (_, _, _) => import.Task, () => releases++, () =>
            {
                recoveryAttempts++;
                return recoveryAttempts == 1 ? Task.FromException(new IOException("fixture recovery unavailable")) : recovery.Task;
            }));
            window.Show(); Drain(window);
            Task copying = window.ImportAsync();
            window.Close(); // Requests cancellation; completion is still protected.
            Assert.True(window.IsVisible);
            Assert.Equal(0, releases);
            import.SetResult(new(LegacyMigrationStatus.Failed, ErrorCode: "RecoveryRequired"));
            CompleteLegacyTask(copying);
            Assert.True(window.IsRecoveryRequired);
            Assert.Equal(0, releases);
            window.Close(); Assert.True(window.IsVisible);
            Assert.False(LegacyField<Button>(window, "_cancel").IsEnabled);
            CompleteLegacyTask(window.RetryRecoveryAsync());
            Assert.True(window.IsRecoveryRequired); Assert.Equal(0, releases);
            Task retry = window.RetryRecoveryAsync();
            Assert.True(window.IsWorking); Assert.Equal(0, releases);
            window.Close(); Assert.True(window.IsVisible);
            recovery.SetResult(); CompleteLegacyTask(retry);
            Assert.False(window.IsRecoveryRequired); Assert.Equal(1, releases);
            Assert.Null(window.Selection); // Recovery alone does not invent an import completion.
            window.Close(); Assert.False(window.IsVisible);
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
            service.Scan = (_, _, _) => Task.FromResult(new LegacyMigrationScan(LegacyMigrationScanStatus.Ready, 3, 1, 2, 200, true));
            CompleteLegacyTask(window.ScanAsync());
            firstScan.SetResult(new(LegacyMigrationScanStatus.Failed, ErrorCode: "InvalidSource")); Drain(window);
            Assert.Equal(UiText.Get("LegacyImport.Ready"), LegacyField<TextBlock>(window, "_status").Text);
            Assert.True(LegacyField<Button>(window, "_copy").IsEnabled);
            Assert.False(LegacyField<CheckBox>(window, "_includeSettings").IsChecked);
            Task copying = window.ImportAsync();
            observer!.Report(new(LegacyMigrationStage.Copying, 1, 4, 50, 200)); Drain(window);
            Assert.False(LegacyField<ProgressBar>(window, "_progress").IsIndeterminate);
            Assert.Equal(25, LegacyField<ProgressBar>(window, "_progress").Value);
            observer.Report(new(LegacyMigrationStage.Publishing, 4, 4, 200, 200)); Drain(window);
            Assert.True(LegacyField<ProgressBar>(window, "_progress").IsIndeterminate);
            import.SetResult(new(LegacyMigrationStatus.Completed, 4, 0)); CompleteLegacyTask(copying);
            string completed = LegacyField<TextBlock>(window, "_status").Text;
            observer.Report(new(LegacyMigrationStage.Copying, 0, 4, 0, 200)); Drain(window);
            Assert.Equal(completed, LegacyField<TextBlock>(window, "_status").Text);
            Assert.Equal(1, releases); Assert.NotNull(window.Selection);
            window.Close();
            Assert.NotEqual(LegacyDataImportWindow.ErrorText("OverlappingRoots"), LegacyDataImportWindow.ErrorText(null));
            Assert.NotEqual(LegacyDataImportWindow.ErrorText("TargetChanged"), LegacyDataImportWindow.ErrorText(null));
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
                    ((TabControl)settings.FindName("CategoryTabs")).SelectedItem = settings.FindName("UpdateTab"); Drain(settings);
                    Button entry = (Button)settings.FindName("LegacyImportAction");
                    Assert.True(entry.IsVisible); AssertFits(settings, entry);
                    SaveStoreUpdateEvidence(settings, $"legacy-information-{locale}-{theme}-780x560.png");
                    settings.LegacyImportPackaged = false; Assert.Equal(Visibility.Collapsed, entry.Visibility);
                }
                finally { settings.CloseForExit(); }
                var service = new LegacyFixtureService();
                var dialog = NewLegacyWindow(LegacyOperations(service, (_, _, _) => Task.FromResult(new LegacyMigrationResult(LegacyMigrationStatus.Completed))));
                dialog.Width = 420;
                dialog.Show(); Drain(dialog);
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
        public Task RecoverPendingAsync(AppPaths targetPaths, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
