using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Capture;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class CaptureSourceTests
{
    private const string AppName = "Microsoft Edge";
    private const string AppPath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    private const string Folder = @"D:\Projects\계약 검토\2026";

    [Fact]
    public void SourceFieldsRoundTripThroughJsonAndAreOmittedWhileAbsent()
    {
        var record = new CaptureRecord { Width = 10, Height = 10 };
        record.SetSourceDetails(new CaptureSourceDetails(AppName, AppPath, Folder));
        foreach (JsonSerializerOptions options in new[] { JsonDefaults.Compact, JsonDefaults.Readable })
        {
            string json = JsonSerializer.Serialize(record, options);
            CaptureRecord loaded = JsonSerializer.Deserialize<CaptureRecord>(json, options)!;
            Assert.Equal(AppName, loaded.SourceAppName);
            Assert.Equal(AppPath, loaded.SourceAppPath);
            Assert.Equal(Folder, loaded.SourceDocumentPath);
            Assert.Equal(new CaptureSourceDetails(AppName, AppPath, Folder), loaded.SourceDetails);
        }

        var unattributed = new CaptureRecord();
        unattributed.SetSourceDetails(CaptureSourceDetails.Empty);
        JsonObject written = JsonNode.Parse(JsonSerializer.Serialize(unattributed, JsonDefaults.Compact))!.AsObject();
        Assert.False(written.ContainsKey("sourceAppName"));
        Assert.False(written.ContainsKey("sourceAppPath"));
        Assert.False(written.ContainsKey("sourceDocumentPath"));
        Assert.True(written.ContainsKey("sourcePageUrl")); // Unchanged older field shape.
    }

    [Fact]
    public void RecordsWrittenBeforeSourceFieldsLoadWithNoSource()
    {
        var legacy = new CaptureRecord { SourceWindowTitle = "Docs", SourcePageUrl = "https://example.com/" };
        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(legacy, JsonDefaults.Compact))!.AsObject();
        Assert.False(json.ContainsKey("sourceAppName"));
        CaptureRecord loaded = JsonSerializer.Deserialize<CaptureRecord>(json.ToJsonString(), JsonDefaults.Compact)!;
        Assert.Null(loaded.SourceAppName);
        Assert.Null(loaded.SourceAppPath);
        Assert.Null(loaded.SourceDocumentPath);
        Assert.True(loaded.SourceDetails.IsEmpty);
        Assert.Equal("Docs", loaded.SourceWindowTitle);
    }

    [Fact]
    public void RecordsWithSourceFieldsStillLoadInABuildThatDoesNotKnowThem()
    {
        var record = new CaptureRecord { Title = "계약 검토", Width = 640, Height = 480 };
        record.SetSourceDetails(new CaptureSourceDetails(AppName, AppPath, Folder));
        string json = JsonSerializer.Serialize(new[] { record }, JsonDefaults.Compact);

        // The shape of a record before this change: the shared options skip unknown members.
        PreviousRecordShape loaded = Assert.Single(JsonSerializer.Deserialize<PreviousRecordShape[]>(json, JsonDefaults.Compact)!);
        Assert.Equal(record.Id, loaded.Id);
        Assert.Equal("계약 검토", loaded.Title);
        Assert.Equal(640, loaded.Width);
        Assert.Equal(string.Empty, loaded.SourcePageUrl);
    }

    [Fact]
    public void QueueIndexKeepsSourceFieldsAcrossSaveAndLoad()
    {
        using var workspace = new TempWorkspace();
        var queue = new CaptureQueue(workspace.Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        CaptureRecord withSource = ImageRecord(DateTimeOffset.Now);
        withSource.SetSourceDetails(new CaptureSourceDetails("Windows 탐색기", @"C:\Windows\explorer.exe", Folder));
        CaptureRecord without = ImageRecord(DateTimeOffset.Now.AddSeconds(-1));
        foreach (CaptureRecord record in new[] { withSource, without })
        {
            string directory = Path.Combine(workspace.Paths.CapturesRoot, record.RelativeDirectory);
            // codeql[cs/path-injection] -- isolated TempWorkspace capture root
            Directory.CreateDirectory(directory);
            // codeql[cs/path-injection] -- isolated TempWorkspace capture root
            File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Original), [1, 2, 3, 4]);
            queue.Add(record);
        }
        queue.Save();

        var reloaded = new CaptureQueue(workspace.Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        reloaded.Load();
        CaptureRecord first = Assert.Single(reloaded.Records, record => record.Id == withSource.Id);
        Assert.Equal(new CaptureSourceDetails("Windows 탐색기", @"C:\Windows\explorer.exe", Folder), first.SourceDetails);
        CaptureRecord second = Assert.Single(reloaded.Records, record => record.Id == without.Id);
        Assert.Null(second.SourceAppName);
        Assert.Null(second.SourceDocumentPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\folder")]
    [InlineData(@"\rooted-without-drive")]
    [InlineData("C:drive-relative")]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    [InlineData("file:///C:/Users/test")]
    [InlineData("https://example.com/folder")]
    [InlineData(@"\\?\C:\Users\test")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData("C:\\Users\\test\nC:\\Other")]
    [InlineData("C:\\Users\\te\u0000st")]
    [InlineData("C:\\Users\\a|b")]
    public void UnsafeOrUnopenableLocationsBecomeEmpty(string? path) =>
        Assert.Empty(CaptureSourceText.NormalizeLocalPath(path));

    [Theory]
    [InlineData(@"C:\Users\test\Documents\", @"C:\Users\test\Documents")]
    [InlineData(@"  D:\Projects\계약 검토  ", @"D:\Projects\계약 검토")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\Users\test\..\shared", @"C:\Users\shared")]
    [InlineData(@"\\fileserver\team\보고서", @"\\fileserver\team\보고서")]
    public void FullyQualifiedLocalAndNetworkFoldersAreKept(string path, string expected) =>
        Assert.Equal(expected, CaptureSourceText.NormalizeLocalPath(path));

    [Theory]
    [InlineData(null, "")]
    [InlineData("  ", "")]
    [InlineData(" Microsoft\tEdge \r\n", "Microsoft Edge")]
    [InlineData("Windows   탐색기", "Windows 탐색기")]
    public void AppNamesBecomeOneTrimmedLine(string? value, string expected) =>
        Assert.Equal(expected, CaptureSourceText.NormalizeAppName(value));

    [Fact]
    public void LongAppNamesAreCutWithoutSplittingACharacter()
    {
        string emoji = char.ConvertFromUtf32(0x1F600);
        string name = new string('a', CaptureSourceText.MaxAppNameLength - 1) + emoji + "tail";
        string normalized = CaptureSourceText.NormalizeAppName(name);
        Assert.Equal(CaptureSourceText.MaxAppNameLength - 1, normalized.Length);
        Assert.False(char.IsHighSurrogate(normalized[^1]));
    }

    [Fact]
    public void StoredValuesAreValidatedOnReadAndEmptyValuesAreNeverWritten()
    {
        var record = new CaptureRecord
        {
            SourceAppName = "  Notepad  ",
            SourceAppPath = "notepad.exe",
            SourceDocumentPath = "::{645FF040-5081-101B-9F08-00AA002F954E}",
        };
        Assert.Equal(new CaptureSourceDetails("Notepad", "", ""), record.SourceDetails);

        record.SetSourceDetails(new CaptureSourceDetails("", AppPath, "relative"));
        Assert.Null(record.SourceAppName);
        Assert.Equal(AppPath, record.SourceAppPath);
        Assert.Null(record.SourceDocumentPath);

        record.SetSourceDetails(null);
        Assert.Null(record.SourceAppPath);
        Assert.Same(CaptureSourceDetails.Empty, CaptureSourceDetails.Create(null, " ", "relative"));
    }

    [Fact]
    public void SearchFindsTheApplicationExecutableAndFolderButNotTheProgramDirectory()
    {
        var record = new CaptureRecord { Title = "회의 자료" };
        record.SetSourceDetails(new CaptureSourceDetails(AppName, AppPath, Folder));
        var other = new CaptureRecord { Title = "다른 캡처" };

        Assert.Equal(CaptureMatchField.SourceApp, Assert.Single(CaptureTextSearch.Search([record, other], "edge")).Fields);
        Assert.Equal(CaptureMatchField.SourceApp, Assert.Single(CaptureTextSearch.Search([record, other], "msedge.exe")).Fields);
        Assert.Equal(CaptureMatchField.SourceDocumentPath,
            Assert.Single(CaptureTextSearch.Search([record, other], "계약")).Fields);
        CaptureSearchHit combined = Assert.Single(CaptureTextSearch.Search([record, other], "edge 2026 회의"));
        Assert.Equal(CaptureMatchField.SourceApp | CaptureMatchField.SourceDocumentPath | CaptureMatchField.Title, combined.Fields);
        // The executable's folder would make "Program Files" match nearly every capture.
        Assert.Empty(CaptureTextSearch.Search([record, other], "Program"));
        Assert.True(CaptureTextSearch.IsMatch(record, "Projects"));

        Assert.Contains(AppName, record.SearchHaystack, StringComparison.Ordinal);
        Assert.Contains("msedge.exe", record.SearchHaystack, StringComparison.Ordinal);
        Assert.Contains(Folder, record.SearchHaystack, StringComparison.Ordinal);
        Assert.DoesNotContain("Program Files", record.SearchHaystack, StringComparison.Ordinal);
    }

    private static CaptureRecord ImageRecord(DateTimeOffset createdAt)
    {
        var record = new CaptureRecord { CreatedAt = createdAt, UpdatedAt = createdAt, Width = 4, Height = 4, TotalBytes = 4 };
        record.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(record.Id, createdAt);
        return record;
    }

    private sealed class PreviousRecordShape
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public int Width { get; set; }
        public string SourceWindowTitle { get; set; } = string.Empty;
        public string SourcePageUrl { get; set; } = string.Empty;
    }
}
