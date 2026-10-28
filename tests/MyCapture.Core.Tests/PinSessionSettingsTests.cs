using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class PinSessionSettingsTests
{
    [Fact]
    public void NewInstall_EnablesSessionRestoreWhileLegacyPropertyDefaultIsOff()
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);

        Assert.False(new PinSettings().RestoreOpenPinsOnLaunch);
        Assert.True(NewInstallDefaults.Create().Pin.RestoreOpenPinsOnLaunch);
        Assert.True(store.Load().Pin.RestoreOpenPinsOnLaunch);
    }

    [Fact]
    public void LegacySettingsFile_WithoutSessionField_LeavesRestoreOff()
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(workspace.Paths.SettingsFile, """
            {"schemaVersion":4,"pin":{"closeOnDoubleClick":true,"initialOpacity":0.8,"zoomStep":0.2}}
            """);
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);

        AppSettings loaded = store.Load();

        Assert.False(loaded.Pin.RestoreOpenPinsOnLaunch);
        Assert.Equal(0.8, loaded.Pin.InitialOpacity);
        Assert.Equal(0.2, loaded.Pin.ZoomStep);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitValue_RoundTripsThroughStoreCloneAndDraft(bool enabled)
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        var source = new AppSettings { Pin = { RestoreOpenPinsOnLaunch = enabled } };
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

    [Fact]
    public void DeepClone_CopiesEveryWritablePinSetting()
    {
        var source = new AppSettings
        {
            Pin =
            {
                CloseOnDoubleClick = false,
                RestoreOpenPinsOnLaunch = true,
                CtrlClickDebounceMs = 350,
                InitialOpacity = 0.75,
                ClosedWindowRestoreLimit = 5,
                ZoomStep = 0.2,
                ClickThroughHintShownCount = 2,
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
