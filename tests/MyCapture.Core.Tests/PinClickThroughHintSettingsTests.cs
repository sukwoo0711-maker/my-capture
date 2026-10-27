using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class PinClickThroughHintSettingsTests
{
    [Fact]
    public void CountSurvivesSaveReloadCloneAndUnrelatedDraftEdits()
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        AppSettings settings = store.Load();
        Assert.Equal(0, settings.Pin.ClickThroughHintShownCount);
        settings.Pin.ClickThroughHintShownCount = 2;
        store.Save(settings);
        AppSettings loaded = store.Load();
        Assert.Equal(2, loaded.Pin.ClickThroughHintShownCount);
        Assert.Equal(2, loaded.DeepClone().Pin.ClickThroughHintShownCount);
        var draft = new SettingsDraft(loaded) { CloseOnDoubleClick = false };
        Assert.Equal(2, draft.ToAppSettings().Pin.ClickThroughHintShownCount);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(100, 3)]
    public void CountIsBounded(int value, int expected) =>
        Assert.Equal(expected, new PinSettings { ClickThroughHintShownCount = value }.ClickThroughHintShownCount);
}
