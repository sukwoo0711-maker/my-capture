using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CaptureSelectionGridSettingsTests
{
    [Theory]
    [InlineData("""{"capture":{"showSelectionGrid":true}}""", true)]
    [InlineData("""{"capture":{"showSelectionGrid":false}}""", false)]
    public void CursorGuides_PreserveExistingGridPreference(string json, bool enabled)
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(workspace.Paths.SettingsFile, json);
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        Assert.Equal(enabled, store.Load().Capture.ShowSelectionGrid);
    }

    [Fact]
    public void GridDefaultsOn_ForNewAndExistingSettingsWithoutTheNewField()
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(workspace.Paths.SettingsFile, """{"capture":{"showMagnifier":false}}""");
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        Assert.True(new AppSettings().Capture.ShowSelectionGrid);
        Assert.True(store.Load().Capture.ShowSelectionGrid);
        Assert.False(store.Load().Capture.ShowMagnifier);
    }

    [Fact]
    public void GridOff_SurvivesDraftCloneDiskReloadAndExportImport()
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        AppSettings live = store.Load();
        var draft = new SettingsDraft(live);
        string? changed = null;
        draft.PropertyChanged += (_, args) => changed = args.PropertyName;
        draft.ShowSelectionGrid = false;
        Assert.Equal(nameof(SettingsDraft.ShowSelectionGrid), changed);
        Assert.True(live.Capture.ShowSelectionGrid); // Cancel leaves the live setting unchanged.

        AppSettings updated = draft.ToAppSettings();
        Assert.False(updated.Capture.ShowSelectionGrid);
        Assert.False(updated.DeepClone().Capture.ShowSelectionGrid);
        store.Save(updated);
        Assert.False(store.Load().Capture.ShowSelectionGrid);
        Assert.False(new SettingsDraft(store.Load()).ShowSelectionGrid);

        string exported = Path.Combine(workspace.Root, "grid-settings.json");
        store.ExportTo(updated, exported);
        Assert.False(store.ImportFrom(exported).Capture.ShowSelectionGrid);
    }
}
