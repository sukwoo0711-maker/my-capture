using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CaptureIndexCompatibilityTests
{
    private static readonly JsonSerializerOptions Version415Options = CreateVersion415Options();

    [Fact]
    public async Task CurrentIndexLoadsIn415InsteadOfStaleSchema2Backup()
    {
        var fixture = new Fixture();
        string first = fixture.Source("first.png", [1, 2, 3]);
        await fixture.Service.ImportAsync([first]);
        CaptureRecord original = Assert.Single(fixture.Queue.Records);
        string stale = JsonSerializer.Serialize(Load415(fixture.Paths), Version415Options);

        original.Title = "Latest title";
        original.Tags = "release, current";
        await fixture.Service.ImportAsync([
            fixture.Source("new.jpg", [4, 5, 6]), fixture.Source("new.mp4", [7, 8, 9])]);
        File.WriteAllText(fixture.Paths.IndexFile + AtomicFile.BackupSuffix, stale);

        CaptureIndexFile current = Load415(fixture.Paths);
        Assert.Equal(2, current.SchemaVersion);
        Assert.Equal(3, current.Records.Count);
        CaptureRecord retained = Assert.Single(current.Records, record => record.Id == original.Id);
        Assert.Equal("Latest title", retained.Title);
        Assert.Equal("release, current", retained.Tags);
        Assert.Contains(current.Records, record => record.IsVideo);
        Assert.All(current.Records, record =>
        {
            Assert.Null(record.OriginalContentHash);
            Assert.Null(record.ImportedFileName);
        });
        Assert.Empty(Directory.EnumerateFiles(fixture.Paths.CapturesRoot, "*pending*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("mp4")]
    public async Task SaveIn415KeepsLibraryAndReturningImportLazilyRestoresHash(string extension)
    {
        var fixture = new Fixture();
        string source = fixture.Source("original." + extension, [10, 11, 12]);
        await fixture.Service.ImportAsync([source]);
        CaptureRecord imported = Assert.Single(fixture.Queue.Records);
        string? expectedHash = imported.OriginalContentHash;
        CaptureIndexFile downgraded = Load415(fixture.Paths);
        CaptureRecord oldRecord = Assert.Single(downgraded.Records);
        oldRecord.Title = "Edited in 4.1.5";
        oldRecord.Tags = "preserved";
        string oldJson = JsonSerializer.Serialize(downgraded, Version415Options);
        Assert.DoesNotContain("originalContentHash", oldJson, StringComparison.Ordinal);
        Assert.DoesNotContain("importedFileName", oldJson, StringComparison.Ordinal);
        File.WriteAllText(fixture.Paths.IndexFile, oldJson);

        CaptureQueue returned = fixture.Reload();
        CaptureRecord loaded = Assert.Single(returned.Records);
        Assert.Equal(imported.Id, loaded.Id);
        Assert.Equal("Edited in 4.1.5", loaded.Title);
        Assert.Equal("preserved", loaded.Tags);
        Assert.Null(loaded.OriginalContentHash);
        Assert.Null(loaded.ImportedFileName);

        var service = new CaptureImportService(returned, Fixture.Prepare);
        int lazyReads = 0;
        service.BeforeDuplicateReadForTest = _ => { lazyReads++; return Task.CompletedTask; };
        Assert.Equal(new CaptureImportResult(0, 1, 0, 0, false), await service.ImportAsync([source]));
        Assert.Equal(1, lazyReads);
        Assert.Equal(expectedHash, loaded.OriginalContentHash);
        Assert.Null(loaded.ImportedFileName);
        Assert.Equal(expectedHash, Assert.Single(fixture.Reload().Records).OriginalContentHash);
        Assert.Single(Load415(fixture.Paths).Records);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OptionalImportFieldsSurviveSupportedSchemasAndSaveAsSchema2(int schema)
    {
        var fixture = new Fixture();
        await fixture.Service.ImportAsync([fixture.Source("kept.png", [20, 21])]);
        CaptureRecord expected = Assert.Single(fixture.Queue.Records);
        JsonNode json = JsonNode.Parse(File.ReadAllText(fixture.Paths.IndexFile))!;
        json["schemaVersion"] = schema;
        File.WriteAllText(fixture.Paths.IndexFile, json.ToJsonString());

        CaptureQueue loaded = fixture.Reload();
        CaptureRecord actual = Assert.Single(loaded.Records);
        Assert.Equal(expected.OriginalContentHash, actual.OriginalContentHash);
        Assert.Equal(expected.ImportedFileName, actual.ImportedFileName);
        loaded.Save();
        Assert.Equal(2, Load415(fixture.Paths).SchemaVersion);
        Assert.Equal(expected.OriginalContentHash, Assert.Single(fixture.Reload().Records).OriginalContentHash);
    }

    [Fact]
    public async Task PreReleaseSchema3PrimaryIsLoadedAheadOfSchema2BackupAndResavedCompatibly()
    {
        var fixture = new Fixture();
        await fixture.Service.ImportAsync([fixture.Source("older.png", [30])]);
        string stale = JsonSerializer.Serialize(Load415(fixture.Paths), Version415Options);
        CaptureRecord first = Assert.Single(fixture.Queue.Records);
        first.Title = "Updated after backup";
        await fixture.Service.ImportAsync([fixture.Source("newer.png", [31])]);
        JsonNode current = JsonNode.Parse(File.ReadAllText(fixture.Paths.IndexFile))!;
        current["schemaVersion"] = 3;
        File.WriteAllText(fixture.Paths.IndexFile, current.ToJsonString());
        File.WriteAllText(fixture.Paths.IndexFile + AtomicFile.BackupSuffix, stale);

        // This is the old failure: 4.1.5 rejects schema 3 and chooses the stale backup.
        Assert.Single(Load415(fixture.Paths).Records);
        CaptureQueue loaded = fixture.Reload();
        Assert.Equal(2, loaded.Count);
        Assert.Equal("Updated after backup", loaded.Find(first.Id)!.Title);
        Assert.All(loaded.Records, record => Assert.NotNull(record.OriginalContentHash));
        loaded.Save();
        Assert.Equal(2, Load415(fixture.Paths).Records.Count);
        Assert.Equal("Updated after backup", Assert.Single(Load415(fixture.Paths).Records,
            record => record.Id == first.Id).Title);
    }

    // 644a818 differs only by these two record properties. Removing them from the JSON
    // contract reproduces the 4.1.5 reader and writer without letting the new fields round-trip.
    private static JsonSerializerOptions CreateVersion415Options()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(CaptureRecord)) return;
            for (int i = info.Properties.Count - 1; i >= 0; i--)
                if (info.Properties[i].Name is "originalContentHash" or "importedFileName")
                    info.Properties.RemoveAt(i);
        });
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            TypeInfoResolver = resolver,
        };
    }

    private static CaptureIndexFile Load415(AppPaths paths)
    {
        string? text = AtomicFile.ReadAllTextWithRecovery(paths.IndexFile, text => TryParse415(text, out _));
        Assert.NotNull(text);
        Assert.True(TryParse415(text, out CaptureIndexFile? index));
        Assert.NotNull(index);
        // The 4.1.5 schema-2 path only merges pending records, not completed meta.json files.
        // All fixture records are completed, so only its final primary-file filter applies.
        index.Records = index.Records.Where(record => File.Exists(Path.Combine(paths.CapturesRoot,
            record.RelativeDirectory, record.IsVideo ? CaptureFileNames.VideoSource : CaptureFileNames.Original)))
            .OrderByDescending(record => record.CreatedAt).ToList();
        return index;
    }

    private static bool TryParse415(string text, out CaptureIndexFile? index)
    {
        index = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            index = JsonSerializer.Deserialize<CaptureIndexFile>(text, Version415Options);
            if (index is null || index.SchemaVersion is not (1 or 2) || index.Records is null) return false;
            var ids = new HashSet<Guid>();
            return index.Records.All(record => record is not null && IsValid415Record(record) && ids.Add(record.Id));
        }
        catch (JsonException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private static bool IsValid415Record(CaptureRecord record)
    {
        bool videoValid = record.IsVideo
            ? double.IsFinite(record.DurationMs) && record.DurationMs > 0
              && record.FrameRate is >= 1 and <= 120 && record.FrameCount > 0
            : double.IsFinite(record.DurationMs) && record.DurationMs >= 0
              && record.FrameRate >= 0 && record.FrameCount >= 0;
        string relative = record.RelativeDirectory;
        return record.Id != Guid.Empty && Enum.IsDefined(record.MediaKind) && record.ContentRevision >= 0
            && record.Width is >= 1 and <= 65_535 && record.Height is >= 1 and <= 65_535 && videoValid
            && double.IsFinite(record.DpiScale) && record.DpiScale is > 0 and <= 16 && record.TotalBytes >= 0
            && (record.OcrContentRevision is null || record.OcrContentRevision >= 0)
            && record.SourceMonitor is not null && record.SourceWindowTitle is not null && record.Title is not null
            && !string.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative)
            && relative.IndexOfAny(Path.GetInvalidPathChars()) < 0
            && relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } segments
            && segments.All(segment => segment is not "." and not "..");
    }

    // Retain fixtures within this worktree; no user profile access or permanent cleanup.
    private sealed class Fixture
    {
        public Fixture()
        {
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "MyCapture.slnx"))) root = root.Parent;
            Root = Path.Combine(root!.FullName, "artifacts", "index-compatibility-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Paths = AppPaths.CreateForRoot(Path.Combine(Root, "library"));
            Queue = Reload();
            Service = new CaptureImportService(Queue, Prepare);
        }

        public string Root { get; }
        public AppPaths Paths { get; }
        public CaptureQueue Queue { get; }
        public CaptureImportService Service { get; }
        public CaptureQueue Reload()
        {
            var queue = new CaptureQueue(Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            queue.Load();
            return queue;
        }

        public string Source(string name, byte[] bytes)
        {
            string path = Path.Combine(Root, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public static Task Prepare(CaptureRecord record, string directory, CancellationToken token)
        {
            record.Width = 40;
            record.Height = 30;
            if (record.IsVideo)
            {
                record.DurationMs = 1000;
                record.FrameRate = 30;
                record.FrameCount = 30;
            }
            if (CaptureImportService.OriginalFileName(record) == CaptureFileNames.ImportedJpeg)
                File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Original), [0]);
            return Task.CompletedTask;
        }
    }
}
