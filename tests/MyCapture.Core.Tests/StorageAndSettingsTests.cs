using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Creates and removes an isolated directory for a single test.
/// </summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "mycapture-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public AppPaths Paths => AppPaths.CreateForRoot(Root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked temp directory must not fail an otherwise passing test run.
        }
    }
}

public sealed class AtomicFileTests
{
    [Fact]
    public void WriteAllText_CreatesFileAndParentDirectory()
    {
        using var workspace = new TempWorkspace();
        string path = Path.Combine(workspace.Root, "nested", "deeper", "file.json");

        AtomicFile.WriteAllText(path, "{\"a\":1}");

        Assert.True(File.Exists(path));
        Assert.Equal("{\"a\":1}", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_LeavesNoTemporaryFileBehind()
    {
        using var workspace = new TempWorkspace();
        string path = Path.Combine(workspace.Root, "file.json");

        AtomicFile.WriteAllText(path, "first");
        AtomicFile.WriteAllText(path, "second");

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal("second", File.ReadAllText(path));
    }

    [Fact]
    public void CleanUpTemp_RemovesLegacyAndGuidTempsButPreservesSimilarUserFiles()
    {
        using var workspace = new TempWorkspace();
        string path = Path.Combine(workspace.Root, "file.json");
        string legacy = path + ".tmp";
        string unique = $"{path}.{Guid.NewGuid():N}.tmp";
        string unrelated = path + ".user-not-a-guid.tmp";
        File.WriteAllText(legacy, "legacy");
        File.WriteAllText(unique, "unique");
        File.WriteAllText(unrelated, "keep");

        AtomicFile.CleanUpTemp(path);

        Assert.False(File.Exists(legacy));
        Assert.False(File.Exists(unique));
        Assert.Equal("keep", File.ReadAllText(unrelated));
    }

    [Fact]
    public void WriteAllText_KeepsPreviousContentsAsBackup()
    {
        using var workspace = new TempWorkspace();
        string path = Path.Combine(workspace.Root, "file.json");

        AtomicFile.WriteAllText(path, "original");
        AtomicFile.WriteAllText(path, "replacement");

        Assert.Equal("original", File.ReadAllText(path + AtomicFile.BackupSuffix));
    }

    [Fact]
    public void ReadAllTextWithRecovery_FallsBackToBackupWhenPrimaryIsCorrupt()
    {
        using var workspace = new TempWorkspace();
        string path = Path.Combine(workspace.Root, "file.json");

        AtomicFile.WriteAllText(path, "GOOD");
        AtomicFile.WriteAllText(path, "ALSO-GOOD");

        // Simulate a torn write: the primary is garbage, the backup is intact.
        File.WriteAllText(path, "<<<corrupt");

        string? recovered = AtomicFile.ReadAllTextWithRecovery(
            path, text => text.StartsWith("GOOD", StringComparison.Ordinal));

        Assert.Equal("GOOD", recovered);
    }

    [Fact]
    public void ReadAllTextWithRecovery_ReturnsNullWhenNothingIsUsable()
    {
        using var workspace = new TempWorkspace();
        string path = Path.Combine(workspace.Root, "missing.json");

        Assert.Null(AtomicFile.ReadAllTextWithRecovery(path, _ => true));
    }

    [Fact]
    public void ReadAllTextWithRecovery_TreatsThrowingValidatorAsRejection()
    {
        using var workspace = new TempWorkspace();
        string path = Path.Combine(workspace.Root, "file.json");
        AtomicFile.WriteAllText(path, "content");

        string? recovered = AtomicFile.ReadAllTextWithRecovery(
            path, _ => throw new InvalidOperationException("validator blew up"));

        Assert.Null(recovered);
    }
}

public sealed class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+C")]
    [InlineData("F3")]
    [InlineData("Shift+F3")]
    [InlineData("Ctrl+Alt+Shift+Win+A")]
    [InlineData("Alt+PrintScreen")]
    public void ToString_RoundTripsThroughTryParse(string text)
    {
        Assert.True(Hotkey.TryParse(text, out Hotkey parsed));
        Assert.Equal(text, parsed.ToString());
    }

    [Fact]
    public void TryParse_IsCaseInsensitiveAndToleratesSpaces()
    {
        Assert.True(Hotkey.TryParse(" ctrl + shift + c ", out Hotkey parsed));

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, parsed.Modifiers);
        Assert.Equal(Hotkey.VkC, parsed.VirtualKey);
    }

    [Fact]
    public void TryParse_EmptyMeansUnassignedRatherThanInvalid()
    {
        Assert.True(Hotkey.TryParse("", out Hotkey parsed));
        Assert.False(parsed.IsAssigned);
    }

    [Fact]
    public void TryParse_RejectsModifiersWithoutAKey()
    {
        Assert.False(Hotkey.TryParse("Ctrl+Shift", out _));
    }

    [Fact]
    public void TryParse_RejectsUnknownKeyName()
    {
        Assert.False(Hotkey.TryParse("Ctrl+Banana", out _));
    }

    [Fact]
    public void FunctionKeysMapToContiguousVirtualKeyCodes()
    {
        Assert.True(Hotkey.TryParse("F1", out Hotkey f1));
        Assert.True(Hotkey.TryParse("F12", out Hotkey f12));

        Assert.Equal(Hotkey.VkF1, f1.VirtualKey);
        Assert.Equal(Hotkey.VkF1 + 11, f12.VirtualKey);
    }
}

public sealed class SettingsStoreTests
{
    private static SettingsStore CreateStore(TempWorkspace workspace) =>
        new(workspace.Paths, NullLogger<SettingsStore>.Instance);

    [Fact]
    public void Load_OnFirstRunReturnsDefaultsAndFlagsFirstRun()
    {
        using var workspace = new TempWorkspace();

        AppSettings settings = CreateStore(workspace).Load();

        Assert.True(settings.General.IsFirstRun);
        Assert.Equal(300, settings.Queue.MaxItems);
        Assert.Equal(2L * 1024 * 1024 * 1024, settings.Queue.MaxBytes);
        Assert.Equal("Ctrl+Shift+C", settings.Hotkeys.Capture.ToString());
        Assert.Equal("Ctrl+Shift+Z", settings.Hotkeys.OpenLibrary.ToString());
        Assert.Equal("Ctrl+X", settings.Hotkeys.RecordRegion.ToString());
        Assert.Equal("F3", settings.Hotkeys.PasteToScreen.ToString());
        Assert.True(settings.Export.CopyToClipboardOnQuickSave);
        Assert.Equal(RecordingFrameRate.Fps30, settings.Recording.FrameRate);
    }

    [Fact]
    public void Load_LegacyRecordingDefaultMigratesAndPreservesLibraryHotkey()
    {
        using var workspace = new TempWorkspace();
        SettingsStore store = CreateStore(workspace);
        var legacySettings = new AppSettings { SchemaVersion = 1 };
        legacySettings.Hotkeys.RecordRegion = new Hotkey(
            HotkeyModifiers.Control | HotkeyModifiers.Shift,
            Hotkey.VkX);
        store.Save(legacySettings);

        AppSettings settings = store.Load();

        Assert.Equal(2, settings.SchemaVersion);
        Assert.Equal("Ctrl+X", settings.Hotkeys.RecordRegion.ToString());
        Assert.Equal("Ctrl+Shift+Z", settings.Hotkeys.OpenLibrary.ToString());
        Assert.Contains(store.LastLoadWarnings, warning => warning.Contains("Ctrl+X", StringComparison.Ordinal));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryChangedValue()
    {
        using var workspace = new TempWorkspace();
        SettingsStore store = CreateStore(workspace);

        AppSettings original = store.Load();
        original.Queue.MaxItems = 450;
        original.Export.CopyToClipboardOnQuickSave = false;
        original.Hotkeys.Capture = new Hotkey(HotkeyModifiers.Alt, Hotkey.VkF1);
        original.Annotation.StrokeThickness = 7;
        original.Ocr.PreferredLanguages = ["en-US"];
        original.Recording.FrameRate = RecordingFrameRate.Fps60;
        original.Recording.StartDelaySeconds = 8;

        store.Save(original);
        AppSettings reloaded = CreateStore(workspace).Load();

        Assert.Equal(450, reloaded.Queue.MaxItems);
        Assert.False(reloaded.Export.CopyToClipboardOnQuickSave);
        Assert.Equal("Alt+F1", reloaded.Hotkeys.Capture.ToString());
        Assert.Equal(7, reloaded.Annotation.StrokeThickness);
        Assert.Equal(["en-US"], reloaded.Ocr.PreferredLanguages);
        Assert.Equal(RecordingFrameRate.Fps60, reloaded.Recording.FrameRate);
        Assert.Equal(8, reloaded.Recording.StartDelaySeconds);
        Assert.False(reloaded.General.IsFirstRun);
    }

    [Fact]
    public void Load_ClampsOutOfRangeValuesAndReportsThem()
    {
        using var workspace = new TempWorkspace();
        SettingsStore store = CreateStore(workspace);

        AppSettings settings = store.Load();
        settings.Queue.MaxItems = 1;                 // below the floor of 10
        settings.Pin.CtrlClickDebounceMs = 5;        // below the floor of 120
        settings.Pin.InitialOpacity = 0.0;           // below the floor of 0.2
        settings.Annotation.FontSize = 5000;         // above the ceiling of 400
        settings.Recording.FrameRate = (RecordingFrameRate)59;
        settings.Recording.StartDelaySeconds = 99;
        store.Save(settings);

        SettingsStore reloadStore = CreateStore(workspace);
        AppSettings reloaded = reloadStore.Load();

        Assert.Equal(10, reloaded.Queue.MaxItems);
        Assert.Equal(120, reloaded.Pin.CtrlClickDebounceMs);
        Assert.Equal(0.2, reloaded.Pin.InitialOpacity);
        Assert.Equal(400, reloaded.Annotation.FontSize);
        Assert.Equal(RecordingFrameRate.Fps30, reloaded.Recording.FrameRate);
        Assert.Equal(SettingsRanges.RecordingStartDelaySeconds.Max, reloaded.Recording.StartDelaySeconds);
        Assert.NotEmpty(reloadStore.LastLoadWarnings);
    }

    [Fact]
    public void Load_RestoresCaptureHotkeyWhenItWasClearedOut()
    {
        using var workspace = new TempWorkspace();
        File.WriteAllText(
            workspace.Paths.SettingsFile,
            """{ "hotkeys": { "capture": "" } }""");

        SettingsStore store = CreateStore(workspace);
        AppSettings settings = store.Load();

        // An app whose capture hotkey is unassigned is inert, so the default is
        // restored rather than respected.
        Assert.Equal("Ctrl+Shift+C", settings.Hotkeys.Capture.ToString());
        Assert.NotEmpty(store.LastLoadWarnings);
    }

    [Fact]
    public void Load_SurvivesACorruptSettingsFile()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(workspace.Paths.DataRoot);
        File.WriteAllText(workspace.Paths.SettingsFile, "{ this is not json ");

        SettingsStore store = CreateStore(workspace);
        AppSettings settings = store.Load();

        Assert.Equal(300, settings.Queue.MaxItems);
        Assert.NotEmpty(store.LastLoadWarnings);
    }

    [Fact]
    public void Load_AcceptsCommentsAndTrailingCommasBecauseTheFileIsUserEditable()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(workspace.Paths.DataRoot);
        File.WriteAllText(
            workspace.Paths.SettingsFile,
            """
            {
              // raise the cap
              "queue": { "maxItems": 500, },
            }
            """);

        AppSettings settings = CreateStore(workspace).Load();

        Assert.Equal(500, settings.Queue.MaxItems);
    }

    [Fact]
    public void Load_FallsBackToBackupWhenPrimaryIsTruncated()
    {
        using var workspace = new TempWorkspace();
        SettingsStore store = CreateStore(workspace);

        AppSettings good = store.Load();
        good.Queue.MaxItems = 321;
        store.Save(good);
        store.Save(good); // second save produces the .bak used for recovery

        File.WriteAllText(workspace.Paths.SettingsFile, "{ truncated");

        AppSettings recovered = CreateStore(workspace).Load();

        Assert.Equal(321, recovered.Queue.MaxItems);
    }
}
