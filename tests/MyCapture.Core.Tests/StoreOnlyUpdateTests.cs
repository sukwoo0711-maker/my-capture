using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Store and GitHub releases share typed update preferences and version observations.
/// Legacy updater fields are ignored without losing the rest of the settings file.
/// </summary>
public sealed class StoreOnlyUpdateTests
{
    [Fact]
    public void SettingsFileWithAnUpdateSection_LoadsAndKeepsTheOtherValues()
    {
        using var workspace = new TempWorkspace();
        workspace.Paths.EnsureCreated();
        File.WriteAllText(workspace.Paths.SettingsFile, """
            {"schemaVersion":4,
             "updates":{"checkOnStartup":true,"channel":"github","lastCheckedUtc":"2026-01-01T00:00:00Z"},
             "update":{"skippedVersion":"9.9.9"},
             "storeUpdate":{"lastSeenVersion":"4.1.1.0","pendingFromVersion":"4.1.1.0",
                 "completionNoticeVersion":"4.1.2.0","channel":"github","installerPath":"C:\\private\\setup.exe",
                 "updates":{"downloadUrl":"https://example.invalid/setup.exe"}},
             "recording":{"frameRate":"fps60","includeCursor":false},
             "pin":{"zoomStep":0.25}}
            """);
        var store = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance);

        AppSettings loaded = store.Load();

        Assert.Equal(RecordingFrameRate.Fps60, loaded.Recording.FrameRate);
        Assert.False(loaded.Recording.IncludeCursor);
        Assert.Equal(0.25, loaded.Pin.ZoomStep, 3);
        Assert.False(loaded.General.IsFirstRun);
        Assert.Empty(store.LastLoadWarnings);
        Assert.Equal("4.1.1.0", loaded.StoreUpdate.LastSeenVersion);
        Assert.Equal("4.1.2.0", loaded.StoreUpdate.CompletionNoticeVersion);
        Assert.True(loaded.Updates.AutoCheck);
        Assert.Null(loaded.Updates.LastCheckUtc);
        Assert.Null(loaded.Updates.DismissedVersion);

        // The next save writes the new contract, dropping unknown legacy updater fields.
        store.Save(loaded);
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(workspace.Paths.SettingsFile));
        AssertTypedUpdateSettings(saved.RootElement);
        Assert.Equal("4.1.2.0", saved.RootElement.GetProperty("storeUpdate").GetProperty("completionNoticeVersion").GetString());
        Assert.Equal("fps60", saved.RootElement.GetProperty("recording").GetProperty("frameRate").GetString());
    }

    [Fact]
    public void ImportedFileWithAnUpdateSection_IsAccepted()
    {
        using var workspace = new TempWorkspace();
        string path = Path.Combine(workspace.Root, "exported-with-updates.json");
        File.WriteAllText(path, """{"schemaVersion":4,"updates":{"checkOnStartup":true},"pin":{"zoomStep":0.25}}""");

        AppSettings imported = new SettingsStore(workspace.Paths, NullLogger<SettingsStore>.Instance).ImportFrom(path);

        Assert.Equal(0.25, imported.Pin.ZoomStep, 3);
        Assert.True(imported.Updates.AutoCheck);
        Assert.Null(imported.Updates.LastCheckUtc);
    }

    [Fact]
    public void DefaultSettingsJson_HasAutomaticChecksAndTypedVersionObservations()
    {
        string json = JsonSerializer.Serialize(new AppSettings(), MyCapture.Core.Serialization.JsonDefaults.Readable);
        using JsonDocument document = JsonDocument.Parse(json);

        AssertTypedUpdateSettings(document.RootElement);
        Assert.True(document.RootElement.GetProperty("updates").GetProperty("autoCheck").GetBoolean());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("updates").GetProperty("lastCheckUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("updates").GetProperty("dismissedVersion").ValueKind);
        foreach (JsonProperty property in document.RootElement.GetProperty("storeUpdate").EnumerateObject())
            Assert.Equal(JsonValueKind.Null, property.Value.ValueKind);
    }

    [Fact]
    public void SharedUpdateState_RoundTripsWithoutAnInstallerOrChannelInSettings()
    {
        var checkedAt = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        var settings = new AppSettings
        {
            Updates = new()
            {
                AutoCheck = false,
                LastCheckUtc = checkedAt,
                DismissedVersion = "4.5.3",
            },
            StoreUpdate = new() { LastSeenVersion = "4.5.2", CompletionNoticeVersion = "4.5.3" },
        };
        string json = JsonSerializer.Serialize(settings, MyCapture.Core.Serialization.JsonDefaults.Readable);
        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(json, MyCapture.Core.Serialization.JsonDefaults.Readable)!;

        Assert.False(loaded.Updates.AutoCheck);
        Assert.Equal(checkedAt, loaded.Updates.LastCheckUtc);
        Assert.Equal("4.5.3", loaded.Updates.DismissedVersion);
        Assert.Equal("4.5.2", loaded.StoreUpdate.LastSeenVersion);
        Assert.Equal("4.5.3", loaded.StoreUpdate.CompletionNoticeVersion);
        using JsonDocument document = JsonDocument.Parse(json);
        AssertTypedUpdateSettings(document.RootElement);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"updates\":null}")]
    [InlineData("{\"updates\":{\"checkOnStartup\":false,\"channel\":\"github\"}}")]
    public void MissingNullOrLegacyUpdatePreferences_DefaultToAutomaticChecks(string json)
    {
        AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(json, MyCapture.Core.Serialization.JsonDefaults.Readable)!;
        Assert.True(loaded.Updates.AutoCheck);
        Assert.Null(loaded.Updates.LastCheckUtc);
        Assert.Null(loaded.Updates.DismissedVersion);
    }

    [Fact]
    public void DeepClone_PreservesUpdateAndInvitationStateWithoutSharingIt()
    {
        var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        var settings = new AppSettings
        {
            Updates = new() { AutoCheck = false, LastCheckUtc = now, DismissedVersion = "4.5.3" },
            StoreUpdate = new() { LastSeenVersion = "4.5.2", CompletionNoticeVersion = "4.5.3" },
            StoreRating = new() { InvitationShownUtc = now },
        };

        AppSettings clone = settings.DeepClone();

        Assert.NotSame(settings.Updates, clone.Updates);
        Assert.NotSame(settings.StoreUpdate, clone.StoreUpdate);
        Assert.False(clone.Updates.AutoCheck);
        Assert.Equal(now, clone.Updates.LastCheckUtc);
        Assert.Equal("4.5.3", clone.Updates.DismissedVersion);
        Assert.Equal("4.5.2", clone.StoreUpdate.LastSeenVersion);
        Assert.Equal("4.5.3", clone.StoreUpdate.CompletionNoticeVersion);
        Assert.Equal(now, clone.StoreRating.InvitationShownUtc);
        clone.Updates.DismissedVersion = "4.5.4";
        clone.StoreUpdate.CompletionNoticeVersion = null;
        Assert.Equal("4.5.3", settings.Updates.DismissedVersion);
        Assert.Equal("4.5.3", settings.StoreUpdate.CompletionNoticeVersion);
    }

    private static List<string> UpdatePropertyPaths(JsonElement element, string path)
    {
        var found = new List<string>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string child = path + "." + property.Name;
                if (property.Name.Contains("update", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("installer", StringComparison.OrdinalIgnoreCase)) found.Add(child);
                found.AddRange(UpdatePropertyPaths(property.Value, child));
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
                found.AddRange(UpdatePropertyPaths(item, $"{path}[{index++}]"));
        }

        return found;
    }

    private static void AssertTypedUpdateSettings(JsonElement settings)
    {
        Assert.Equal(new[] { "$.storeUpdate", "$.updates" }, UpdatePropertyPaths(settings, "$"));
        JsonElement updates = settings.GetProperty("updates");
        Assert.Equal(new[] { "autoCheck", "dismissedVersion", "lastCheckUtc" },
            updates.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        JsonElement observations = settings.GetProperty("storeUpdate");
        Assert.Equal(JsonValueKind.Object, observations.ValueKind);
        // pendingFromVersion, written but never read by unreleased 4.1.2 candidates, is dropped on save.
        Assert.Equal(new[] { "completionNoticeVersion", "lastSeenVersion" },
            observations.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        foreach (JsonProperty property in observations.EnumerateObject())
        {
            Assert.True(property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.String, property.Name);
            if (property.Value.ValueKind == JsonValueKind.String)
                Assert.True(Version.TryParse(property.Value.GetString(), out _), property.Name);
        }
    }
}
