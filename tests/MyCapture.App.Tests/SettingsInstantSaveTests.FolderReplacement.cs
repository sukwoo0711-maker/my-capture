using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsInstantSaveTests
{
    [Fact]
    public Task ResetToDefaultsReplacesFolderDestinationWithLiveQueue() =>
        Isolated(nameof(ResetToDefaultsReplacesFolderDestinationWithLiveQueue), () => VerifyFolderReplacement(reset: true));

    [Fact]
    public Task ImportReplacesFolderDestinationWithLiveQueue() =>
        Isolated(nameof(ImportReplacesFolderDestinationWithLiveQueue), () => VerifyFolderReplacement(reset: false));

    [Fact]
    public Task InvalidImportRetainsFolderDestinationIntentWithLiveQueue() =>
        Isolated(nameof(InvalidImportRetainsFolderDestinationIntentWithLiveQueue), () => VerifyFolderReplacement(reset: false, invalidImport: true));

    private static void VerifyFolderReplacement(bool reset, bool invalidImport = false)
    {
        Application app = CreateApplication();
        SettingsWindow? window = null;
        try
        {
            string root = CreateFolderReplacementRoot();
            AppPaths paths = AppPaths.CreateForRoot(root);
            var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
            AppSettings saved = NewInstallDefaults.Create();
            bool expected = reset ? NewInstallDefaults.Create().Queue.UseCurrentFolderForNewCaptures : true;
            saved.Queue.UseCurrentFolderForNewCaptures = !expected;
            store.Save(saved);
            var queue = new CaptureQueue(paths, saved.Queue, NullLogger<CaptureQueue>.Instance);
            queue.Load();
            Assert.Equal(!expected, queue.UseCurrentFolderForNewCaptures);

            using var messageWindow = new NativeMessageWindow();
            using var hotkeys = new GlobalHotkeyService(messageWindow, new FolderReplacementRegistrar(), NullLogger<GlobalHotkeyService>.Instance);
            hotkeys.Initialize(saved.Hotkeys);
            var startup = new StartupRegistrationService(new FolderReplacementRunKeyStore(), Path.Combine(root, "MyCapture-test.exe"));
            var service = new SettingsApplyService(store, () => saved, candidate => saved = candidate, queue, hotkeys, startup, NullLogger.Instance);
            int confirmations = 0;
            window = new SettingsWindow(() => saved, candidate => service.Apply(candidate), NullLogger.Instance,
                settingsStore: () => store, showSaveFailureMessage: message => Assert.Fail(message), applyDraft: service.Apply)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, Width = 940, Height = 700,
                ShowActivated = false, ShowInTaskbar = false,
                ConfirmResetDefaults = () => { confirmations++; return true; },
            };
            FluidMotion.SetWindowEntrance(window, false);
            window.Show();
            Pump();
            Assert.True(window.IsVisible);
            var draft = Assert.IsType<SettingsDraft>(window.DataContext);
            Assert.False(draft.FolderDestinationEdited);
            if (reset)
            {
                ((TabControl)window.FindName("CategoryTabs")).SelectedItem = window.FindName("UpdateTab");
                ((Button)window.FindName("DefaultsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, confirmations);
            }
            else
            {
                AppSettings imported = saved.DeepClone();
                imported.Queue.UseCurrentFolderForNewCaptures = expected;
                if (invalidImport)
                {
                    Assert.True(Hotkey.TryParse("Ctrl+Alt+F7", out Hotkey duplicate));
                    imported.Hotkeys.Capture = imported.Hotkeys.OpenLibrary = duplicate;
                }
                string importPath = Path.Combine(root, "imported-settings.json");
                store.ExportTo(imported, importPath);
                window.ImportSettingsFrom(importPath);
                Assert.Equal(0, confirmations);
            }
            Pump();
            Assert.Equal(expected, draft.UseCurrentFolderForNewCaptures);
            Assert.Equal(expected, saved.Queue.UseCurrentFolderForNewCaptures);
            Assert.Equal(expected, store.Load().Queue.UseCurrentFolderForNewCaptures);
            Assert.Equal(expected, queue.UseCurrentFolderForNewCaptures);
            Assert.Empty(draft.GetErrors(nameof(SettingsDraft.UseCurrentFolderForNewCaptures)).Cast<string>());
            if (invalidImport)
            {
                Assert.True(draft.ExportOptionsReplaced);
                Assert.True(draft.HasErrors);
                Assert.NotEmpty(draft.GetErrors(nameof(SettingsDraft.CaptureHotkey)).Cast<string>());
                draft.OpenLibraryHotkey = "Ctrl+Alt+F8";
                Pump(450);
            }
            Assert.False(draft.HasErrors);
            Assert.False(draft.ExportOptionsReplaced);
            Assert.True(window.FlushPendingSettings());
            Assert.Equal(expected, store.Load().Queue.UseCurrentFolderForNewCaptures);
            Assert.Equal(expected, queue.UseCurrentFolderForNewCaptures);
            Assert.True(window.IsVisible);
            window.RequestClose();
            Pump();
            Assert.False(window.IsVisible);
            Assert.False(draft.HasErrors);
        }
        finally
        {
            window?.CloseForExit();
            app.Shutdown();
        }
    }

    [Fact]
    public Task OrdinarySavePreservesFolderDestinationChangedInLiveQueue() =>
        Isolated(nameof(OrdinarySavePreservesFolderDestinationChangedInLiveQueue), () => VerifyPreservedFolderDestination(legacyImport: false));

    [Fact]
    public Task LegacyImportPreservesFolderDestinationWithLiveQueue() =>
        Isolated(nameof(LegacyImportPreservesFolderDestinationWithLiveQueue), () => VerifyPreservedFolderDestination(legacyImport: true));

    private static void VerifyPreservedFolderDestination(bool legacyImport)
    {
        Application app = CreateApplication();
        SettingsWindow? window = null;
        try
        {
            string root = CreateFolderReplacementRoot();
            AppPaths paths = AppPaths.CreateForRoot(root);
            var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
            AppSettings saved = NewInstallDefaults.Create();
            saved.Queue.UseCurrentFolderForNewCaptures = false;
            store.Save(saved);
            var queue = new CaptureQueue(paths, saved.Queue, NullLogger<CaptureQueue>.Instance);
            queue.Load();
            using var messageWindow = new NativeMessageWindow();
            using var hotkeys = new GlobalHotkeyService(messageWindow, new FolderReplacementRegistrar(), NullLogger<GlobalHotkeyService>.Instance);
            hotkeys.Initialize(saved.Hotkeys);
            var startup = new StartupRegistrationService(new FolderReplacementRunKeyStore(), Path.Combine(root, "MyCapture-test.exe"));
            var service = new SettingsApplyService(store, () => saved, candidate => saved = candidate, queue, hotkeys, startup, NullLogger.Instance);
            window = new SettingsWindow(() => saved, candidate => service.Apply(candidate), NullLogger.Instance,
                settingsStore: () => store, showSaveFailureMessage: message => Assert.Fail(message), applyDraft: service.Apply)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, Width = 940, Height = 700,
                ShowActivated = false, ShowInTaskbar = false,
            };
            FluidMotion.SetWindowEntrance(window, false);
            window.Show();
            Pump();
            var draft = Assert.IsType<SettingsDraft>(window.DataContext);
            Assert.False(draft.UseCurrentFolderForNewCaptures);
            CaptureFolder folder = AwaitFolderTask(queue.CreateFolderAsync("live folder"));
            Assert.True(AwaitFolderTask(queue.SetNewCaptureFolderAsync(folder.Id, true)));
            Assert.True(queue.UseCurrentFolderForNewCaptures);
            Assert.Equal(folder.Id, queue.DefaultCaptureFolderId);
            Assert.False(draft.UseCurrentFolderForNewCaptures); // The open editor still has the previous value.
            Assert.False(draft.FolderDestinationEdited);
            bool expectedSound = !saved.General.PlayCaptureSound;
            if (legacyImport)
            {
                AppSettings imported = saved.DeepClone();
                imported.Queue.UseCurrentFolderForNewCaptures = false;
                imported.Queue.DefaultCaptureFolderId = null;
                imported.General.PlayCaptureSound = expectedSound;
                string importPath = Path.Combine(root, "legacy-settings.json");
                store.ExportTo(imported, importPath);
                window.ImportLegacySettingsDraft(importPath);
            }
            else draft.PlayCaptureSound = expectedSound;
            Pump();
            Assert.Equal(expectedSound, store.Load().General.PlayCaptureSound);
            Assert.True(saved.Queue.UseCurrentFolderForNewCaptures);
            Assert.True(store.Load().Queue.UseCurrentFolderForNewCaptures);
            Assert.Equal(folder.Id, store.Load().Queue.DefaultCaptureFolderId);
            Assert.True(queue.UseCurrentFolderForNewCaptures);
            Assert.Equal(folder.Id, queue.DefaultCaptureFolderId);
            Assert.False(draft.HasErrors);
            Assert.False(draft.ExportOptionsReplaced);
            Assert.True(window.IsVisible);
            window.RequestClose();
            Pump();
            Assert.False(window.IsVisible);
        }
        finally
        {
            window?.CloseForExit();
            app.Shutdown();
        }
    }

    private static string CreateFolderReplacementRoot()
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MyCapture.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        string root = Path.Combine(repository.FullName, "artifacts", "t", "folder-replacement", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static T AwaitFolderTask<T>(Task<T> task)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) Pump();
        Assert.True(task.IsCompleted, "The isolated folder operation did not complete.");
        return task.GetAwaiter().GetResult();
    }
    private sealed class FolderReplacementRegistrar : IHotkeyRegistrar
    {
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            errorCode = 0;
            errorMessage = string.Empty;
            return true;
        }
        public void Unregister(int id) { }
    }

    private sealed class FolderReplacementRunKeyStore : IRunKeyStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? GetValue(string name) => _values.GetValueOrDefault(name);
        public void SetValue(string name, string value) => _values[name] = value;
        public void DeleteValue(string name) => _values.Remove(name);
    }
}