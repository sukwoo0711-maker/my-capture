using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Updates come only from the Microsoft Store. Settings carry only typed Store version observations;
/// legacy updater sections are ignored without losing the rest of the file, and no app code
/// outside the dormant <c>Updates</c> folder reaches the GitHub updater.
/// </summary>
public sealed class StoreOnlyUpdateTests
{
    private static readonly Regex GitHubUpdaterReference = new(
        @"\b(?:GitHubUpdateService|UpdateSession|UpdateInstaller)\b", RegexOptions.Compiled);

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
        Assert.Equal("4.1.1.0", loaded.StoreUpdate.PendingFromVersion);
        Assert.Equal("4.1.2.0", loaded.StoreUpdate.CompletionNoticeVersion);

        // The next save writes only known settings, so the stale section disappears.
        store.Save(loaded);
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(workspace.Paths.SettingsFile));
        AssertOnlyStoreObservation(saved.RootElement);
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
    }

    [Fact]
    public void DefaultSettingsJson_HasOnlyTypedStoreVersionObservation()
    {
        string json = JsonSerializer.Serialize(new AppSettings(), MyCapture.Core.Serialization.JsonDefaults.Readable);
        using JsonDocument document = JsonDocument.Parse(json);

        AssertOnlyStoreObservation(document.RootElement);
        foreach (JsonProperty property in document.RootElement.GetProperty("storeUpdate").EnumerateObject())
            Assert.Equal(JsonValueKind.Null, property.Value.ValueKind);
    }

    [Fact]
    public void AppSource_ReachesTheGitHubUpdaterOnlyFromItsOwnFolder()
    {
        string root = RepositoryRoot();
        string updates = Path.Combine(root, "src", "MyCapture.App", "Updates") + Path.DirectorySeparatorChar;
        // The dormant updater still exists, so the scan below is not vacuous.
        Assert.True(File.Exists(Path.Combine(updates, "GitHubUpdateService.cs")));

        var offenders = new List<string>();
        int scanned = 0;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(p => (p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                && !IsBuildOutput(p, root)
                && !p.StartsWith(updates, StringComparison.OrdinalIgnoreCase)))
        {
            scanned++;
            string text = File.ReadAllText(file);
            foreach (Match match in GitHubUpdaterReference.Matches(text))
            {
                int line = text.Take(match.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetRelativePath(root, file)}:{line}: {match.Value}");
            }
        }

        Assert.True(scanned > 100, $"Only {scanned} source files were scanned.");
        Assert.True(offenders.Count == 0,
            "Only the Microsoft Store delivers updates; the GitHub updater must stay disconnected:\n"
            + string.Join(Environment.NewLine, offenders));
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

    private static void AssertOnlyStoreObservation(JsonElement settings)
    {
        Assert.Equal(new[] { "$.storeUpdate" }, UpdatePropertyPaths(settings, "$"));
        JsonElement observations = settings.GetProperty("storeUpdate");
        Assert.Equal(JsonValueKind.Object, observations.ValueKind);
        Assert.Equal(new[] { "completionNoticeVersion", "lastSeenVersion", "pendingFromVersion" },
            observations.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        foreach (JsonProperty property in observations.EnumerateObject())
        {
            Assert.True(property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.String, property.Name);
            if (property.Value.ValueKind == JsonValueKind.String)
                Assert.True(Version.TryParse(property.Value.GetString(), out _), property.Name);
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(dir.FullName, "src"))
                && Directory.Exists(Path.Combine(dir.FullName, "build")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException($"Repository root was not found above '{AppContext.BaseDirectory}'.");
    }

    private static bool IsBuildOutput(string path, string root) =>
        Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase));
}
