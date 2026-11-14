using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingAudioSettingsStoreTests
{
    [Fact]
    public void OlderFileWithoutAudioLoadsBothSourcesOff()
    {
        string root = OwnedTestDirectory.Create("mc-recording-audio-settings-old-");
        AppPaths paths = AppPaths.CreateForRoot(root);
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsFile,
            """{"schemaVersion":4,"recording":{"frameRate":"fps60","cursorHighlight":false}}""");
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        AppSettings loaded = store.Load();
        Assert.Equal(RecordingFrameRate.Fps60, loaded.Recording.FrameRate);
        Assert.False(loaded.Recording.CursorHighlight);
        Assert.False(loaded.Recording.RecordSystemAudio);
        Assert.False(loaded.Recording.RecordMicrophone);
        Assert.Empty(store.LastLoadWarnings);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SaveReloadAndExportImportPreserveBothPreferences(bool system, bool microphone)
    {
        string root = OwnedTestDirectory.Create("mc-recording-audio-settings-");
        AppPaths paths = AppPaths.CreateForRoot(root);
        var store = new SettingsStore(paths, NullLogger<SettingsStore>.Instance);
        var draft = new SettingsDraft(store.Load()) { RecordSystemAudio = system, RecordMicrophone = microphone };
        store.Save(draft.ToAppSettings());
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(paths.SettingsFile)))
        {
            var recording = document.RootElement.GetProperty("recording");
            Assert.Equal(system, recording.GetProperty("recordSystemAudio").GetBoolean());
            Assert.Equal(microphone, recording.GetProperty("recordMicrophone").GetBoolean());
        }
        AppSettings loaded = store.Load();
        Assert.Equal(system, loaded.Recording.RecordSystemAudio);
        Assert.Equal(microphone, loaded.Recording.RecordMicrophone);
        string exportPath = Path.Combine(root, "audio-settings.json");
        store.ExportTo(loaded, exportPath);
        AppSettings imported = store.ImportFrom(exportPath);
        Assert.Equal(system, imported.Recording.RecordSystemAudio);
        Assert.Equal(microphone, imported.Recording.RecordMicrophone);
        // Keep this owned fixture as a diagnostic artifact in the test run's temporary root.
    }
}
