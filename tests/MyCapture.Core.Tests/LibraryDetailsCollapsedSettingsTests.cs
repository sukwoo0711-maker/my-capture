using System.Text.Json;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class LibraryDetailsCollapsedSettingsTests
{
    [Fact]
    public void DetailsPaneIsOpenByDefaultAndLegacyFilesStayOpen()
    {
        Assert.False(new AppSettings().General.LibraryDetailsCollapsed);
        Assert.False(JsonSerializer.Deserialize<AppSettings>("{}", JsonDefaults.Readable)!.General.LibraryDetailsCollapsed);
        Assert.False(JsonSerializer.Deserialize<AppSettings>("{\"general\":{}}", JsonDefaults.Readable)!.General.LibraryDetailsCollapsed);
    }

    [Fact]
    public void CollapsedChoiceSurvivesJsonAndClone()
    {
        var settings = new AppSettings { General = { LibraryDetailsCollapsed = true } };
        AppSettings saved = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings, JsonDefaults.Readable), JsonDefaults.Readable)!;
        Assert.True(saved.General.LibraryDetailsCollapsed);
        AppSettings clone = saved.DeepClone();
        Assert.True(clone.General.LibraryDetailsCollapsed);
        clone.General.LibraryDetailsCollapsed = false;
        Assert.True(saved.General.LibraryDetailsCollapsed);
    }
}
