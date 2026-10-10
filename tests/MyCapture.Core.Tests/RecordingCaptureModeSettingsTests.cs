using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Settings surface of <see cref="RecordingSettings.CaptureMode"/>: automatic (WGC first) by
/// default and for older files, a lenient reader that never discards the rest of the file, and
/// a value that survives the draft, clone, disk and export paths.
/// </summary>
public sealed class RecordingCaptureModeSettingsTests
{
    [Fact]
    public void Defaults_AreAutomatic()
    {
        var settings = new AppSettings();
        Assert.Equal(RecordingCaptureMode.Auto, settings.Recording.CaptureMode);

        var draft = new SettingsDraft(settings);
        Assert.Equal("auto", draft.RecordingCaptureMode);
        Assert.False(draft.HasErrors);
    }

    [Fact]
    public void OlderSettingsFile_WithoutCaptureMode_LoadsAutomatic()
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(workspace.Paths.SettingsFile, """
            {"schemaVersion":4,"recording":{"frameRate":"fps60","includeCursor":false}}
            """);

        AppSettings loaded = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance).Load();

        Assert.Equal(RecordingCaptureMode.Auto, loaded.Recording.CaptureMode);
        Assert.Equal(RecordingFrameRate.Fps60, loaded.Recording.FrameRate);
        Assert.False(loaded.Recording.IncludeCursor);
    }

    [Theory]
    [InlineData("\"gdi\"", RecordingCaptureMode.Gdi)]
    [InlineData("\"GDI\"", RecordingCaptureMode.Gdi)]
    [InlineData("\"auto\"", RecordingCaptureMode.Auto)]
    [InlineData("1", RecordingCaptureMode.Gdi)]
    [InlineData("0", RecordingCaptureMode.Auto)]
    // Unknown names, numbers and shapes fall back to Auto instead of failing the whole file.
    [InlineData("\"wgc\"", RecordingCaptureMode.Auto)]
    [InlineData("\"dxgi\"", RecordingCaptureMode.Auto)]
    [InlineData("42", RecordingCaptureMode.Auto)]
    [InlineData("-1", RecordingCaptureMode.Auto)]
    [InlineData("null", RecordingCaptureMode.Auto)]
    [InlineData("true", RecordingCaptureMode.Auto)]
    [InlineData("{\"mode\":\"gdi\"}", RecordingCaptureMode.Auto)]
    public void StoredValue_IsReadLenientlyAndKeepsTheRestOfTheFile(string jsonValue, RecordingCaptureMode expected)
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(
            workspace.Paths.SettingsFile,
            "{\"schemaVersion\":4,\"recording\":{\"frameRate\":\"fps60\",\"captureMode\":" + jsonValue +
            ",\"cursorHighlight\":false},\"pin\":{\"zoomStep\":0.25}}");

        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        AppSettings loaded = store.Load();

        Assert.Equal(expected, loaded.Recording.CaptureMode);
        // The other values prove the file was parsed, not replaced by defaults.
        Assert.Equal(RecordingFrameRate.Fps60, loaded.Recording.FrameRate);
        Assert.False(loaded.Recording.CursorHighlight);
        Assert.Equal(0.25, loaded.Pin.ZoomStep, 3);
        Assert.Empty(store.LastLoadWarnings);
    }

    [Fact]
    public void Gdi_SurvivesDraftCloneDiskReloadAndExportImport()
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        AppSettings live = store.Load();
        var draft = new SettingsDraft(live);
        string? changed = null;
        draft.PropertyChanged += (_, args) => changed = args.PropertyName;

        draft.RecordingCaptureMode = "gdi";

        Assert.Equal(nameof(SettingsDraft.RecordingCaptureMode), changed);
        Assert.Equal(RecordingCaptureMode.Auto, live.Recording.CaptureMode); // Cancel leaves the live value.
        AppSettings updated = draft.ToAppSettings();
        Assert.Equal(RecordingCaptureMode.Gdi, updated.Recording.CaptureMode);
        Assert.Equal(RecordingCaptureMode.Gdi, updated.DeepClone().Recording.CaptureMode);

        store.Save(updated);
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(workspace.Paths.SettingsFile)))
        {
            Assert.Equal(
                "gdi",
                document.RootElement.GetProperty("recording").GetProperty("captureMode").GetString());
        }

        AppSettings reloaded = store.Load();
        Assert.Equal(RecordingCaptureMode.Gdi, reloaded.Recording.CaptureMode);
        Assert.Equal("gdi", new SettingsDraft(reloaded).RecordingCaptureMode);

        string exported = Path.Combine(workspace.Root, "capture-mode-settings.json");
        store.ExportTo(updated, exported);
        Assert.Equal(RecordingCaptureMode.Gdi, store.ImportFrom(exported).Recording.CaptureMode);
    }

    [Fact]
    public void AutoIsWrittenByName()
    {
        string json = JsonSerializer.Serialize(new AppSettings(), MyCapture.Core.Serialization.JsonDefaults.Readable);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("auto", document.RootElement.GetProperty("recording").GetProperty("captureMode").GetString());
    }

    [Theory]
    [InlineData("gdi", "gdi")]
    [InlineData("GDI", "gdi")]
    [InlineData("auto", "auto")]
    [InlineData("wgc", "auto")]
    [InlineData("", "auto")]
    public void Draft_NormalizesTheSelectedValue(string selected, string expected)
    {
        var draft = new SettingsDraft(new AppSettings()) { RecordingCaptureMode = selected };

        Assert.Equal(expected, draft.RecordingCaptureMode);
        Assert.False(draft.HasErrors);
        Assert.Equal(RecordingCaptureModeNames.Parse(expected), draft.ToAppSettings().Recording.CaptureMode);
    }

    [Fact]
    public void ResetToDefaults_ReturnsToAutomatic()
    {
        var draft = new SettingsDraft(new AppSettings { Recording = { CaptureMode = RecordingCaptureMode.Gdi } });
        Assert.Equal("gdi", draft.RecordingCaptureMode);

        draft.ResetToDefaults();

        Assert.Equal("auto", draft.RecordingCaptureMode);
    }
}
