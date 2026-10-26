using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Pinning;
using MyCapture.App.Settings;
using MyCapture.Core.Annotations;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ImageExportDestinationTests
{
    [Fact]
    public async Task EditorSaveAs_RemembersSuccessfulFolderAcrossReloadAndSuggestsItNextTime()
    {
        AppPaths paths = RetainedPaths();
        var settings = new AppSettings();
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        string chosen = Path.Combine(paths.DataRoot, "chosen", "capture.png");
        int saves = 0;
        CaptureCommitService service = Editor(settings, paths, saved => { store.Save(saved); saves++; });
        service.SaveAsPrompt = _ => chosen;

        Assert.True(await service.CommitAsync(null, Result(EditorCommitAction.SaveAs)));
        Assert.True(File.Exists(chosen));
        Assert.Equal(Path.GetDirectoryName(chosen), settings.Export.LastSaveDirectory);
        Assert.Equal(1, saves);

        AppSettings reloaded = store.Load();
        Assert.Equal(settings.Export.LastSaveDirectory, reloaded.Export.LastSaveDirectory);
        CaptureCommitService reopened = Editor(reloaded, paths);
        reopened.SaveAsPrompt = suggested =>
        {
            Assert.Equal(Path.GetDirectoryName(chosen), Path.GetDirectoryName(suggested));
            return null;
        };
        Assert.False(await reopened.CommitAsync(null, Result(EditorCommitAction.SaveAs)));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("extension")]
    [InlineData("write-failure")]
    public async Task EditorSaveAs_UnsuccessfulExportPreservesHistoryAndDoesNotSaveSettings(string outcome)
    {
        AppPaths paths = RetainedPaths();
        var settings = new AppSettings { Export = { LastSaveDirectory = paths.DataRoot } };
        int saves = 0;
        CaptureCommitService service = Editor(settings, paths, _ => saves++);
        string blocker = Path.Combine(paths.DataRoot, "not-a-directory");
        if (outcome == "write-failure") await File.WriteAllTextAsync(blocker, "retained fixture");
        service.SaveAsPrompt = _ => outcome switch
        {
            "cancel" => null,
            "extension" => Path.Combine(paths.DataRoot, "wrong.jpg"),
            _ => Path.Combine(blocker, "capture.png"),
        };

        Assert.False(await service.CommitAsync(null, Result(EditorCommitAction.SaveAs)));
        Assert.Equal(paths.DataRoot, settings.Export.LastSaveDirectory);
        Assert.Equal(0, saves);
        Assert.Empty(Directory.EnumerateFiles(paths.DataRoot, "*.png", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuickSave_UsesRememberedFolderUnlessFixedOverrideExists_WithoutChangingHistory(bool fixedOverride)
    {
        AppPaths paths = RetainedPaths();
        string remembered = Directory.CreateDirectory(Path.Combine(paths.DataRoot, "remembered")).FullName;
        string configured = Path.Combine(paths.DataRoot, "fixed");
        var settings = new AppSettings
        {
            Export = { LastSaveDirectory = remembered, QuickSaveDirectoryOverride = fixedOverride ? configured : string.Empty },
        };
        int saves = 0;
        CaptureCommitService service = Editor(settings, paths, _ => saves++);

        string? announcedPath = null;
        service.QuickSaved += path =>
        {
            Assert.True(File.Exists(path));
            announcedPath = path;
        };
        Assert.True(await service.CommitAsync(null, Result(EditorCommitAction.QuickSave)));
        string expected = fixedOverride ? configured : remembered;
        Assert.Equal(expected, Path.GetDirectoryName(announcedPath));
        Assert.Single(Directory.EnumerateFiles(expected, "*.png"));
        Assert.Equal(remembered, settings.Export.LastSaveDirectory);
        Assert.Equal(0, saves);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("state")]
    [InlineData("crypto")]
    [InlineData("json")]
    public async Task SavedImage_RemainsSuccessfulWhenRememberingSettingsFails(string failure)
    {
        AppPaths paths = RetainedPaths();
        var settings = new AppSettings { Export = { LastSaveDirectory = paths.DataRoot } };
        string chosen = Path.Combine(paths.DataRoot, "different", "capture.png");
        CaptureCommitService service = Editor(settings, paths, _ => throw (failure switch
        {
            "state" => new InvalidOperationException("settings unavailable"),
            "crypto" => new System.Security.Cryptography.CryptographicException("settings unavailable"),
            "json" => new System.Text.Json.JsonException("settings unavailable"),
            _ => (Exception)new IOException("settings unavailable"),
        }));
        service.SaveAsPrompt = _ => chosen;

        Assert.True(await service.CommitAsync(null, Result(EditorCommitAction.SaveAs)));
        Assert.True(File.Exists(chosen));
        Assert.Equal(paths.DataRoot, settings.Export.LastSaveDirectory);
    }

    [Fact]
    public async Task PinSaveAs_SharesHistoryAndFixedQuickSaveContract()
    {
        AppPaths paths = RetainedPaths();
        var settings = new AppSettings { Export = { QuickSaveDirectoryOverride = Path.Combine(paths.DataRoot, "fixed") } };
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        int saves = 0;
        CaptureCommitService commit = Editor(settings, paths, saved => { store.Save(saved); saves++; });
        var pin = new PinImageSaveService(() => settings, () => paths, NullLogger<PinImageSaveService>.Instance,
            commit.RememberExportOptions, commit.ExportLocation, commit.RememberSavedExport);
        string chosen = Path.Combine(paths.DataRoot, "pin", "capture.png");
        pin.SaveAsPrompt = (_, _) => chosen;

        Assert.Equal(PinSaveStatus.Saved, (await pin.SaveAsAsync(Image(), null)).Status);
        Assert.Equal(Path.GetDirectoryName(chosen), store.Load().Export.LastSaveDirectory);
        Assert.Equal(1, saves);
        pin.SaveAsPrompt = (_, suggested) =>
        {
            Assert.Equal(Path.GetDirectoryName(chosen), Path.GetDirectoryName(suggested));
            return null;
        };
        Assert.Equal(PinSaveStatus.Cancelled, (await pin.SaveAsAsync(Image(), null)).Status);
        PinSaveResult quick = await pin.QuickSaveAsync(Image());
        Assert.Equal(PinSaveStatus.Saved, quick.Status);
        Assert.Equal(settings.Export.QuickSaveDirectoryOverride, Path.GetDirectoryName(quick.Path));
        Assert.Equal(Path.GetDirectoryName(chosen), settings.Export.LastSaveDirectory);
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task ReducedExport_PublishesBytesThenRemembersFolderAndOptionsOnce()
    {
        AppPaths paths = RetainedPaths();
        var settings = new AppSettings();
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        string destination = Path.Combine(paths.DataRoot, "reduced", "capture.png");
        byte[] prepared = ImageCodec.EncodePng(Image());
        var selected = new ImageExportOptions { ReductionPercent = 30, LongEdge = 800 };
        int saves = 0;
        CaptureCommitService commit = Editor(settings, paths, saved =>
        {
            Assert.Equal(prepared, File.ReadAllBytes(destination));
            Assert.Equal(selected, saved.Export.LastImageExport);
            store.Save(saved);
            saves++;
        });

        await ImageReductionExportDialog.WritePreparedExportAsync(destination, prepared, selected,
            _ => throw new InvalidOperationException("A successful Save As must publish a single combined snapshot."),
            commit.RememberSavedExport);

        Assert.Equal(1, saves);
        AppSettings reloaded = store.Load();
        Assert.Equal(Path.GetDirectoryName(destination), reloaded.Export.LastSaveDirectory);
        Assert.Equal(selected, reloaded.Export.LastImageExport);
    }

    [Fact]
    public async Task ReducedExport_SettingsFailureKeepsWrittenImageAndPriorFolder()
    {
        AppPaths paths = RetainedPaths();
        var settings = new AppSettings { Export = { LastSaveDirectory = paths.DataRoot } };
        string destination = Path.Combine(paths.DataRoot, "reduced", "capture.png");
        byte[] prepared = ImageCodec.EncodePng(Image());
        CaptureCommitService commit = Editor(settings, paths,
            _ => throw new System.Security.Cryptography.CryptographicException("settings unavailable"));

        await ImageReductionExportDialog.WritePreparedExportAsync(destination, prepared, new ImageExportOptions(),
            commit.RememberExportOptions, commit.RememberSavedExport);

        Assert.Equal(prepared, await File.ReadAllBytesAsync(destination));
        Assert.Equal(paths.DataRoot, settings.Export.LastSaveDirectory);
    }

    [Fact]
    public async Task ReducedExport_WriteFailureDoesNotRememberFolderOrOptions()
    {
        AppPaths paths = RetainedPaths();
        string blocker = Path.Combine(paths.DataRoot, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "retained fixture");
        int callbacks = 0;

        await Assert.ThrowsAnyAsync<IOException>(() => ImageReductionExportDialog.WritePreparedExportAsync(
            Path.Combine(blocker, "capture.png"), ImageCodec.EncodePng(Image()), new ImageExportOptions(),
            _ => callbacks++, (_, _) => callbacks++));

        Assert.Equal(0, callbacks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-folder")]
    [InlineData("missing")]
    public void MissingOrUnusableHistoryFallsBackWithoutChangingFixedPreference(string history)
    {
        AppPaths paths = RetainedPaths();
        var settings = new AppSettings
        {
            Export = { LastSaveDirectory = history == "missing" ? Path.Combine(paths.DataRoot, "absent") : history },
        };
        var location = new ImageExportLocation(() => settings, () => paths, NullLogger.Instance);
        Assert.Equal(paths.QuickSaveRoot, location.SaveAsDirectory);
        settings.Export.QuickSaveDirectoryOverride = Path.Combine(paths.DataRoot, "fixed");
        Assert.Equal(settings.Export.QuickSaveDirectoryOverride, location.SaveAsDirectory);
        Assert.Equal(settings.Export.QuickSaveDirectoryOverride, location.QuickSaveDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RememberedFolderThatBecomesMissingFallsBackAndResumesWhenAvailable(bool fixedOverride)
    {
        AppPaths paths = RetainedPaths();
        string remembered = Directory.CreateDirectory(Path.Combine(paths.DataRoot, "remembered")).FullName;
        string retained = Path.Combine(paths.DataRoot, "remembered-unavailable");
        Assert.StartsWith(paths.DataRoot + Path.DirectorySeparatorChar, remembered, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(paths.DataRoot + Path.DirectorySeparatorChar, retained, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(retained));
        await File.WriteAllTextAsync(Path.Combine(remembered, "retained-marker.txt"), "retained fixture");
        var settings = new AppSettings
        {
            Export = { LastSaveDirectory = remembered,
                QuickSaveDirectoryOverride = fixedOverride ? Path.Combine(paths.DataRoot, "fixed") : string.Empty },
        };
        int saves = 0;
        CaptureCommitService commit = Editor(settings, paths, _ => saves++);
        Assert.Equal(remembered, commit.ExportLocation.SaveAsDirectory);

        // An unavailable remembered path behaves like an unplugged volume. Rename only our
        // owned fixture and retain it, so no file is deleted to model the missing destination.
        Directory.Move(remembered, retained);
        try
        {
            Assert.False(Directory.Exists(remembered));
            string fallback = fixedOverride ? settings.Export.QuickSaveDirectoryOverride : paths.QuickSaveRoot;
            Assert.Equal(fallback, commit.ExportLocation.SaveAsDirectory);
            commit.SaveAsPrompt = suggested =>
            {
                Assert.Equal(fallback, Path.GetDirectoryName(suggested));
                return null;
            };
            Assert.False(await commit.CommitAsync(null, Result(EditorCommitAction.SaveAs)));
            Assert.True(await commit.CommitAsync(null, Result(EditorCommitAction.QuickSave)));
            Assert.Single(Directory.EnumerateFiles(fallback, "*.png"));
            Assert.Equal(remembered, settings.Export.LastSaveDirectory);
            Assert.Equal(0, saves);
        }
        finally
        {
            Assert.False(Directory.Exists(remembered));
            Directory.Move(retained, remembered);
        }

        Assert.True(File.Exists(Path.Combine(remembered, "retained-marker.txt")));
        Assert.Equal(remembered, commit.ExportLocation.SaveAsDirectory);
        Assert.Equal(fixedOverride ? settings.Export.QuickSaveDirectoryOverride : remembered,
            commit.ExportLocation.QuickSaveDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InaccessibleRememberedFolderFallsBackWithoutLosingHistory(bool fixedOverride)
    {
        AppPaths paths = RetainedPaths();
        DirectoryInfo remembered = Directory.CreateDirectory(Path.Combine(paths.DataRoot, "remembered"));
        Assert.StartsWith(paths.DataRoot + Path.DirectorySeparatorChar, remembered.FullName, StringComparison.OrdinalIgnoreCase);
        string originalAccess = remembered.GetAccessControl()
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        DirectorySecurity deniedAccess = remembered.GetAccessControl();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new InvalidOperationException("The test identity is unavailable.");
        deniedAccess.AddAccessRule(new FileSystemAccessRule(user,
            FileSystemRights.ListDirectory | FileSystemRights.CreateFiles, AccessControlType.Deny));
        var settings = new AppSettings
        {
            Export = { LastSaveDirectory = remembered.FullName,
                QuickSaveDirectoryOverride = fixedOverride ? Path.Combine(paths.DataRoot, "fixed") : string.Empty },
        };
        int saves = 0;
        CaptureCommitService commit = Editor(settings, paths, _ => saves++);
        Assert.Equal(remembered.FullName, commit.ExportLocation.SaveAsDirectory);

        try
        {
            remembered.SetAccessControl(deniedAccess);
            // Metadata remains visible: Directory.Exists alone cannot establish that a
            // native Save As dialog can open this directory or that a file can be saved.
            Assert.True(Directory.Exists(remembered.FullName));
            Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFileSystemEntries(remembered.FullName));
            string fallback = fixedOverride ? settings.Export.QuickSaveDirectoryOverride : paths.QuickSaveRoot;
            Assert.Equal(fallback, commit.ExportLocation.SaveAsDirectory);
            commit.SaveAsPrompt = suggested =>
            {
                Assert.Equal(fallback, Path.GetDirectoryName(suggested));
                return null;
            };
            Assert.False(await commit.CommitAsync(null, Result(EditorCommitAction.SaveAs)));
            Assert.True(await commit.CommitAsync(null, Result(EditorCommitAction.QuickSave)));
            Assert.Single(Directory.EnumerateFiles(fallback, "*.png"));
            Assert.Equal(remembered.FullName, settings.Export.LastSaveDirectory);
            Assert.Equal(0, saves);
        }
        finally
        {
            var restoredAccess = new DirectorySecurity();
            restoredAccess.SetSecurityDescriptorSddlForm(originalAccess, AccessControlSections.Access);
            remembered.SetAccessControl(restoredAccess);
            Assert.Equal(originalAccess, remembered.GetAccessControl()
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }

        Assert.Empty(Directory.GetFileSystemEntries(remembered.FullName));
        Assert.Equal(remembered.FullName, commit.ExportLocation.SaveAsDirectory);
        Assert.Equal(fixedOverride ? settings.Export.QuickSaveDirectoryOverride : remembered.FullName,
            commit.ExportLocation.QuickSaveDirectory);
    }

    [Fact]
    public void SettingsCloneAndDraftRetainHistory_AndOldSettingsDefaultToEmpty()
    {
        var settings = new AppSettings { Export = { LastSaveDirectory = @"C:\Pictures\Chosen" } };
        Assert.Equal(settings.Export.LastSaveDirectory, settings.DeepClone().Export.LastSaveDirectory);
        Assert.Equal(settings.Export.LastSaveDirectory, new SettingsDraft(settings).ToAppSettings().Export.LastSaveDirectory);
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Export\":{}}");
        Assert.Equal(string.Empty, old!.Export.LastSaveDirectory);
    }

    [Fact]
    public void SettingsDraft_DistinguishesAutomaticLocationFromExplicitFolder()
    {
        AppPaths paths = RetainedPaths();
        string remembered = Directory.CreateDirectory(Path.Combine(paths.DataRoot, "remembered")).FullName;
        var draft = new SettingsDraft(new AppSettings { Export = { LastSaveDirectory = remembered } });
        Assert.Equal(string.Empty, draft.QuickSaveDirectoryOverride);
        Assert.Equal(string.Empty, draft.ToAppSettings().Export.QuickSaveDirectoryOverride);
        var location = new ImageExportLocation(() => draft.ToAppSettings(), () => paths, NullLogger.Instance);
        Assert.Equal(remembered, location.QuickSaveDirectory);
        string defaultFolder = paths.QuickSaveRoot;
        draft.QuickSaveDirectoryOverride = defaultFolder;
        Assert.Equal(defaultFolder, draft.ToAppSettings().Export.QuickSaveDirectoryOverride);
        Assert.Equal(defaultFolder, location.QuickSaveDirectory);
        var reopened = new SettingsDraft(draft.ToAppSettings());
        Assert.Equal(defaultFolder, reopened.QuickSaveDirectoryOverride);
        reopened.QuickSaveDirectoryOverride = " ";
        Assert.Equal(string.Empty, reopened.ToAppSettings().Export.QuickSaveDirectoryOverride);
        var clearedLocation = new ImageExportLocation(() => reopened.ToAppSettings(), () => paths, NullLogger.Instance);
        Assert.Equal(remembered, clearedLocation.QuickSaveDirectory);
    }

    [Fact]
    public void ApplyingAnOlderSettingsDraftPreservesTheNewestSavedFolder() => StaTestHost.Run(() =>
    {
        AppPaths paths = RetainedPaths();
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        var live = new AppSettings { Export = { LastSaveDirectory = paths.DataRoot } };
        var draft = new SettingsDraft(live) { MaxItems = "450" };
        string newest = Directory.CreateDirectory(Path.Combine(paths.DataRoot, "newest")).FullName;
        live.Export.LastSaveDirectory = newest;
        using var window = new NativeMessageWindow();
        using var hotkeys = new GlobalHotkeyService(window, new FakeRegistrar(), NullLogger<GlobalHotkeyService>.Instance);
        hotkeys.Initialize(live.Hotkeys);
        var startup = new StartupRegistrationService(new FakeRunKeyStore(), @"C:\Apps\MyCapture.exe");
        var apply = new SettingsApplyService(store, () => live, next => live = next, null, hotkeys, startup, NullLogger.Instance);

        Assert.True(apply.Apply(draft.ToAppSettings()).Saved);
        Assert.Equal(newest, live.Export.LastSaveDirectory);
        Assert.Equal(newest, store.Load().Export.LastSaveDirectory);
        Assert.Equal(450, live.Queue.MaxItems);
    });

    [Theory]
    [InlineData("ko-KR", "버전 ")]
    [InlineData("en-US", "Version ")]
    public void DisplayedVersionComesFromTheRunningApplicationAssembly(string language, string prefix)
    {
        using var locale = UiText.UseLanguage(language);
        Version version = typeof(App).Assembly.GetName().Version!;
        Assert.Equal(version.Major, ApplicationVersion.Current.Major);
        Assert.Equal(version.Minor, ApplicationVersion.Current.Minor);
        Assert.Equal(Math.Max(0, version.Build), ApplicationVersion.Current.Patch);
        string numericVersion = FormattableString.Invariant($"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}");
        Assert.Equal(numericVersion, ApplicationVersion.Current.ToString());
        Assert.Equal(prefix + numericVersion, ApplicationVersion.DisplayText);
    }

    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            errorCode = 0;
            errorMessage = string.Empty;
            return true;
        }

        public void Unregister(int id) { }
    }

    private sealed class FakeRunKeyStore : IRunKeyStore
    {
        public string? GetValue(string name) => null;
        public void SetValue(string name, string value) { }
        public void DeleteValue(string name) { }
    }

    private static CaptureCommitService Editor(AppSettings settings, AppPaths paths, Action<AppSettings>? saveSettings = null)
    {
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue, NullLogger<CapturePersistenceService>.Instance);
        var commit = new CaptureCommitService(persistence, () => settings, () => paths,
            NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
        commit.PersistExportSettings = () => saveSettings?.Invoke(settings);
        return commit;
    }

    private static AppPaths RetainedPaths()
    {
        // Retain these tiny fixtures: cleanup must be an explicit, recoverable trash operation.
        string root = Environment.GetEnvironmentVariable("MYCAPTURE_TEST_RETAIN_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "MyCapture.RetainedTests");
        string path = Directory.CreateDirectory(Path.Combine(root, nameof(ImageExportDestinationTests), Guid.NewGuid().ToString("N"))).FullName;
        return AppPaths.CreateForRoot(path);
    }

    private static BitmapSource Image()
    {
        BitmapSource image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 24, 48, 96, 255 }, 4);
        image.Freeze();
        return image;
    }

    private static AnnotationEditingResult Result(EditorCommitAction action)
    {
        BitmapSource image = Image();
        var bounds = new RectD(0, 0, 1, 1);
        return new AnnotationEditingResult(new FrozenFrame(image, bounds, null, 0), bounds, image,
            AnnotationDocument.CreateFor(1, 1), action, new Dictionary<string, BitmapSource>(), new Dictionary<string, string>());
    }
}
