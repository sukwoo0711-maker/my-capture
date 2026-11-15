using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class PinSessionSettingsTests
{
    [Fact]
    public void NewInstall_EnablesSessionRestoreWhileThePropertyInitializerIsOff()
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);

        Assert.False(new PinSettings().RestoreOpenPinsOnLaunch);
        Assert.True(NewInstallDefaults.Create().Pin.RestoreOpenPinsOnLaunch);
        Assert.True(store.Load().Pin.RestoreOpenPinsOnLaunch);
        Assert.Equal(1, NewInstallDefaults.Create().Pin.RestoreOpenPinsRevision);
    }

    [Theory]
    [InlineData("""{"schemaVersion":4,"pin":{"closeOnDoubleClick":true,"initialOpacity":0.8,"zoomStep":0.2}}""")]
    [InlineData("""{"schemaVersion":4,"pin":{"restoreOpenPinsOnLaunch":false,"initialOpacity":0.8,"zoomStep":0.2}}""")]
    public void LegacySettingsFile_TurnsRestoreOnOnce_AndKeepsOtherPinSettings(string json)
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(workspace.Paths.SettingsFile, json);
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);

        AppSettings loaded = store.Load();

        Assert.True(loaded.Pin.RestoreOpenPinsOnLaunch);
        Assert.Equal(1, loaded.Pin.RestoreOpenPinsRevision);
        Assert.Equal(0.8, loaded.Pin.InitialOpacity);
        Assert.Equal(0.2, loaded.Pin.ZoomStep);
    }

    [Fact]
    public void LegacySettingsFile_RestoreTurnedOffAfterTheOneTimeChange_StaysOff()
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(workspace.Paths.SettingsFile, """
            {"schemaVersion":4,"pin":{"restoreOpenPinsOnLaunch":false}}
            """);
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        var draft = new SettingsDraft(store.Load());
        Assert.True(draft.RestoreOpenPinsOnLaunch);

        draft.RestoreOpenPinsOnLaunch = false;
        store.Save(draft.ToAppSettings());

        for (int launch = 0; launch < 2; launch++)
        {
            AppSettings reloaded = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance).Load();
            Assert.False(reloaded.Pin.RestoreOpenPinsOnLaunch);
            Assert.Equal(1, reloaded.Pin.RestoreOpenPinsRevision);
            store.Save(reloaded);
        }
    }

    [Fact]
    public void MigratedSettings_KeepTheMarkerThroughCloneAndBackgroundSave()
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(workspace.Paths.SettingsFile, """
            {"schemaVersion":4,"pin":{"restoreOpenPinsOnLaunch":false}}
            """);
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        AppSettings loaded = store.Load();

        store.SaveInBackground(loaded.DeepClone());
        Assert.True(store.WaitForPendingSaves(TimeSpan.FromSeconds(10)));

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(workspace.Paths.SettingsFile));
        var pin = document.RootElement.EnumerateObject()
            .Single(property => string.Equals(property.Name, "pin", StringComparison.OrdinalIgnoreCase)).Value;
        var fields = pin.EnumerateObject().ToDictionary(property => property.Name, property => property.Value,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1, fields["restoreOpenPinsRevision"].GetInt32());
        Assert.True(fields["restoreOpenPinsOnLaunch"].GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitValue_RoundTripsThroughStoreCloneAndDraft(bool enabled)
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        // A file written by this version carries the marker, so an explicit choice is kept.
        var source = new AppSettings { Pin = { RestoreOpenPinsOnLaunch = enabled, RestoreOpenPinsRevision = 1 } };
        store.Save(source);

        AppSettings loaded = store.Load();
        Assert.Equal(enabled, loaded.Pin.RestoreOpenPinsOnLaunch);
        AppSettings clone = loaded.DeepClone();
        loaded.Pin.RestoreOpenPinsOnLaunch = !enabled;
        Assert.Equal(enabled, clone.Pin.RestoreOpenPinsOnLaunch);

        var draft = new SettingsDraft(clone);
        Assert.Equal(enabled, draft.RestoreOpenPinsOnLaunch);
        Assert.False(draft.HasUnappliedChanges);
        Assert.Equal(enabled, draft.ToAppSettings().Pin.RestoreOpenPinsOnLaunch);
        draft.RestoreOpenPinsOnLaunch = !enabled;
        Assert.True(draft.HasUnappliedChanges);
        Assert.Equal(!enabled, draft.ToAppSettings().Pin.RestoreOpenPinsOnLaunch);
        draft.LoadFrom(clone);
        Assert.Equal(enabled, draft.RestoreOpenPinsOnLaunch);
        Assert.False(draft.HasUnappliedChanges);
        draft.RestoreOpenPinsOnLaunch = !enabled;
        draft.RestoreOpenPinsOnLaunch = enabled;
        Assert.False(draft.HasUnappliedChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LightweightImagePins_RoundTripsThroughStoreCloneAndDraft(bool enabled)
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        Assert.False(new PinSettings().LightweightImagePins);
        Assert.False(NewInstallDefaults.Create().Pin.LightweightImagePins);
        store.Save(new AppSettings { Pin = { LightweightImagePins = enabled } });

        AppSettings loaded = store.Load();
        Assert.Equal(enabled, loaded.Pin.LightweightImagePins);
        AppSettings clone = loaded.DeepClone();
        loaded.Pin.LightweightImagePins = !enabled;
        Assert.Equal(enabled, clone.Pin.LightweightImagePins);

        var draft = new SettingsDraft(clone);
        Assert.Equal(enabled, draft.LightweightImagePins);
        Assert.False(draft.HasUnappliedChanges);
        draft.LightweightImagePins = !enabled;
        Assert.True(draft.HasUnappliedChanges);
        Assert.Equal(!enabled, draft.ToAppSettings().Pin.LightweightImagePins);
        draft.LightweightImagePins = enabled;
        Assert.False(draft.HasUnappliedChanges);
        draft.LoadFrom(clone);
        Assert.Equal(enabled, draft.LightweightImagePins);
    }

    [Fact]
    public void LightweightImagePins_IsOffInAnOlderSettingsFile()
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(workspace.Paths.SettingsFile, """
            {"schemaVersion":4,"pin":{"closeOnDoubleClick":true}}
            """);
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);

        Assert.False(store.Load().Pin.LightweightImagePins);
    }

    [Fact]
    public void DeepClone_CopiesEveryWritablePinSetting()
    {
        var source = new AppSettings
        {
            Pin =
            {
                CloseOnDoubleClick = false,
                SnapWhileDragging = false,
                RestoreOpenPinsOnLaunch = true,
                RestoreOpenPinsRevision = 1,
                CtrlClickDebounceMs = 350,
                InitialOpacity = 0.75,
                ClosedWindowRestoreLimit = 5,
                ZoomStep = 0.2,
                ClickThroughHintShownCount = 2,
                LightweightImagePins = true,
            },
        };
        var defaults = new PinSettings();
        PropertyInfo[] writable = typeof(PinSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite)
            .ToArray();

        PinSettings clone = source.DeepClone().Pin;

        Assert.NotSame(source.Pin, clone);
        foreach (PropertyInfo property in writable)
        {
            Assert.NotEqual(property.GetValue(defaults), property.GetValue(source.Pin));
            Assert.Equal(property.GetValue(source.Pin), property.GetValue(clone));
        }
    }

    [Fact]
    public void Draft_ResetToDefaultsEnablesSessionRestore()
    {
        var draft = new SettingsDraft(new AppSettings());

        draft.ResetToDefaults();

        Assert.True(draft.RestoreOpenPinsOnLaunch);
        Assert.True(draft.ToAppSettings().Pin.RestoreOpenPinsOnLaunch);
        Assert.True(draft.HasUnappliedChanges);
    }
}
