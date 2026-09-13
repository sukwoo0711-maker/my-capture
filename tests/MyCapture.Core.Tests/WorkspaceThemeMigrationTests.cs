using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class WorkspaceThemeMigrationTests
{
    [Theory]
    [InlineData("midnight", "workspace")]
    [InlineData("unknown", "workspace")]
    [InlineData(null, "workspace")]
    [InlineData("daylight", "daylight")]
    [InlineData("high-contrast", "high-contrast")]
    public void LegacyThemeMigratesOnceAndExplicitReselectionSurvivesSave(string? legacy, string expected)
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);
        workspace.Paths.EnsureCreated();
        store.Save(new AppSettings { General = new GeneralSettings { Theme = legacy!, ThemeRevision = 0 } });
        AppSettings upgraded = store.Load();
        Assert.Equal(expected, upgraded.General.Theme);
        Assert.Equal(1, upgraded.General.ThemeRevision);
        AppSettings copy = upgraded.DeepClone();
        Assert.Equal(1, copy.General.ThemeRevision);
        var draft = new SettingsDraft(copy) { Theme = "midnight" };
        AppSettings chosen = draft.ToAppSettings();
        Assert.Equal(1, chosen.General.ThemeRevision);
        store.Save(chosen);
        Assert.Equal("midnight", store.Load().General.Theme);
    }
}
