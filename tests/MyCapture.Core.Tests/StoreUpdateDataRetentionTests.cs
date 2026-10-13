using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.Core.Tests;

/// <summary>
/// Local persistence across a version change in the same package data folders.
/// This does not deploy an MSIX or exercise the live Microsoft Store service.
/// </summary>
public sealed class StoreUpdateDataRetentionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderPackageData_SurvivesObservationSaveAndReopen(bool relocatedCaptures)
    {
        using var workspace = new TempWorkspace();
        string localState = Path.Combine(workspace.Root, "package", "LocalState");
        string localCache = Path.Combine(workspace.Root, "package", "LocalCache");
        string pictures = Path.Combine(workspace.Root, "Pictures");
        AppPaths originalPaths = AppPaths.CreateForPackage(localState, localCache, pictures);
        AppSettings previousSettings = NewInstallDefaults.Create();
        previousSettings.General.ThemeRevision = 1;
        previousSettings.General.Language = "en-US";
        previousSettings.General.LaunchAtLogin = false;
        previousSettings.General.ShowShortcutHints = true;
        previousSettings.Pin.ZoomStep = 0.23;
        previousSettings.Annotation.FontSize = 33;
        previousSettings.Hotkeys.OpenLibrary = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, Hotkey.VkF1 + 5);
        previousSettings.Queue.CapturesDirectoryOverride = relocatedCaptures
            ? Path.Combine(workspace.Root, "User-selected library") : string.Empty;
        AppPaths capturePaths = ResolveCaptures(originalPaths, previousSettings);
        capturePaths.EnsureCreated();

        var previousQueue = NewQueue(capturePaths, previousSettings);
        DateTimeOffset created = DateTimeOffset.UtcNow.AddMinutes(-5);
        CaptureFolder parent = await previousQueue.CreateFolderAsync("보관", createdAt: created);
        CaptureFolder child = await previousQueue.CreateFolderAsync("Annotated", parent.Id, created);
        await previousQueue.CreateFolderAsync("Empty", parent.Id, created);

        CaptureRecord image = AddImage(previousQueue, created);
        CaptureRecord video = AddVideo(previousQueue, created.AddMinutes(1));
        Assert.Equal(1, await previousQueue.MoveCapturesAsync([video.Id], child.Id));
        await previousQueue.SetNewCaptureFolderAsync(child.Id, true);
        previousQueue.Save();

        // Version 4.0.3 predates these observations. Keep the actual schema-4 settings
        // shape, including all known preferences and the saved folder destination.
        JsonObject olderSettings = KnownSettings(previousSettings);
        Assert.Equal(4, olderSettings["schemaVersion"]!.GetValue<int>());
        Assert.False(olderSettings.ContainsKey("storeUpdate"));
        Assert.False(olderSettings.ContainsKey("storeRating"));
        AtomicFile.WriteAllText(originalPaths.SettingsFile, olderSettings.ToJsonString(JsonDefaults.Readable));

        string expectedPreferences = olderSettings.ToJsonString(JsonDefaults.Compact);
        string expectedRecords = RecordSnapshot(previousQueue);
        string expectedFolders = FolderSnapshot(previousQueue);
        Dictionary<string, string> expectedPayloads = PayloadHashes(capturePaths.CapturesRoot);
        Assert.True(expectedPayloads.Count >= 9);
        string imageDirectory = previousQueue.GetDirectory(image);
        string videoDirectory = previousQueue.GetDirectory(video);

        // Resolve fresh paths as a replacement process does. Neither version nor install
        // directory participates in the LocalState/library path calculation.
        AppPaths replacementPaths = AppPaths.CreateForPackage(localState, localCache, pictures);
        Assert.Equal(originalPaths.SettingsFile, replacementPaths.SettingsFile);
        Assert.Equal(originalPaths.IndexFile, replacementPaths.IndexFile);
        var replacementStore = NewStore(replacementPaths);
        AppSettings replacementSettings = replacementStore.Load();
        Assert.False(replacementSettings.General.IsFirstRun);
        Assert.Empty(replacementStore.LastLoadWarnings);
        Assert.Null(replacementSettings.StoreUpdate.LastSeenVersion);
        AssertRetained(replacementPaths, replacementSettings, expectedPreferences, expectedRecords,
            expectedFolders, expectedPayloads, image.Id, video.Id, imageDirectory, videoDirectory, child.Id);

        // A first launch with no old observation must not invent an installation result.
        Assert.True(replacementSettings.StoreUpdate.ObserveLaunch("4.1.2.0", packaged: true));
        Assert.Null(replacementSettings.StoreUpdate.CompletionNoticeVersion);
        replacementStore.Save(replacementSettings);

        AppSettings reopenedSettings = NewStore(replacementPaths).Load();
        Assert.Equal("4.1.2.0", reopenedSettings.StoreUpdate.LastSeenVersion);
        Assert.False(reopenedSettings.StoreUpdate.ObserveLaunch("4.1.2.0", packaged: true));
        Assert.Null(reopenedSettings.StoreUpdate.CompletionNoticeVersion);
        AssertRetained(replacementPaths, reopenedSettings, expectedPreferences, expectedRecords,
            expectedFolders, expectedPayloads, image.Id, video.Id, imageDirectory, videoDirectory, child.Id);

        // Drain the reopened queue publication too, then verify the persisted index on
        // one more fresh load rather than checking only an in-memory collection.
        var reopenedQueue = NewQueue(ResolveCaptures(replacementPaths, reopenedSettings), reopenedSettings);
        reopenedQueue.Load();
        reopenedQueue.Save();
        NewStore(replacementPaths).Save(reopenedSettings);
        AssertRetained(replacementPaths, NewStore(replacementPaths).Load(), expectedPreferences, expectedRecords,
            expectedFolders, expectedPayloads, image.Id, video.Id, imageDirectory, videoDirectory, child.Id);
    }

    private static void AssertRetained(AppPaths paths, AppSettings settings, string expectedPreferences,
        string expectedRecords, string expectedFolders, Dictionary<string, string> expectedPayloads,
        Guid imageId, Guid videoId, string imageDirectory, string videoDirectory, Guid defaultFolder)
    {
        Assert.Equal(expectedPreferences, KnownSettings(settings).ToJsonString(JsonDefaults.Compact));
        AppPaths capturePaths = ResolveCaptures(paths, settings);
        var queue = NewQueue(capturePaths, settings);
        queue.Load();
        Assert.Equal(expectedRecords, RecordSnapshot(queue));
        Assert.Equal(expectedFolders, FolderSnapshot(queue));
        Assert.Equal(defaultFolder, queue.DefaultCaptureFolderId);
        Assert.True(queue.UseCurrentFolderForNewCaptures);
        Assert.Equal(defaultFolder, queue.ResolveNewCaptureFolderId());
        Assert.Equal(imageDirectory, queue.GetDirectory(queue.Find(imageId)!));
        Assert.Equal(videoDirectory, queue.GetDirectory(queue.Find(videoId)!));
        Assert.Null(queue.Find(imageId)!.FolderId);
        Assert.Equal(defaultFolder, queue.Find(videoId)!.FolderId);
        Dictionary<string, string> actualPayloads = PayloadHashes(capturePaths.CapturesRoot);
        Assert.Equal(expectedPayloads.OrderBy(pair => pair.Key), actualPayloads.OrderBy(pair => pair.Key));
        AnnotationDocument? document = AnnotationDocument.TryFromJson(File.ReadAllText(
            queue.GetFilePath(queue.Find(imageId)!, CaptureFileNames.Layers)));
        Assert.NotNull(document);
        Assert.Equal("보존된 주석 🖊", Assert.IsType<TextAnnotation>(document.Items[0]).Text);
        Assert.Equal("asset-01.png", Assert.IsType<ImageAnnotation>(document.Items[1]).AssetFileName);
    }

    private static CaptureRecord AddImage(CaptureQueue queue, DateTimeOffset created)
    {
        var record = new CaptureRecord { CreatedAt = created, UpdatedAt = created, Width = 1, Height = 1,
            Title = "이미지 🖼", HasAnnotations = true, ContentRevision = 3, OcrText = "retained OCR" };
        queue.Add(record);
        Directory.CreateDirectory(queue.GetDirectory(record));
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jWZkAAAAASUVORK5CYII=");
        foreach (string name in new[] { CaptureFileNames.Original, CaptureFileNames.Rendered, "asset-01.png" })
            File.WriteAllBytes(queue.GetFilePath(record, name), png);
        var document = AnnotationDocument.CreateFor(1, 1);
        document.Add(new TextAnnotation { Text = "보존된 주석 🖊", Rect = new RectD(0, 0, 1, 1) });
        document.Add(new ImageAnnotation { AssetFileName = "asset-01.png", SourceWidth = 1,
            SourceHeight = 1, Rect = new RectD(0, 0, 1, 1) });
        File.WriteAllText(queue.GetFilePath(record, CaptureFileNames.Layers), document.ToJson(), Encoding.UTF8);
        record.TotalBytes = Directory.EnumerateFiles(queue.GetDirectory(record)).Sum(path => new FileInfo(path).Length);
        queue.SaveRecordMetaOrThrow(record);
        queue.Save();
        return record;
    }

    private static CaptureRecord AddVideo(CaptureQueue queue, DateTimeOffset created)
    {
        var record = new CaptureRecord { CreatedAt = created, UpdatedAt = created, Width = 640, Height = 360,
            MediaKind = CaptureMediaKind.Video, Title = "Saved clip", DurationMs = 2500,
            FrameRate = 30, FrameCount = 75, ContentRevision = 2 };
        queue.Add(record);
        Directory.CreateDirectory(queue.GetDirectory(record));
        // Synthetic bytes test persistence only; this fixture does not invoke a decoder.
        File.WriteAllBytes(queue.GetFilePath(record, CaptureFileNames.VideoSource), [0, 0, 0, 24, 102, 116, 121, 112, 1, 2, 3]);
        File.WriteAllBytes(queue.GetFilePath(record, CaptureFileNames.VideoRendered), [0, 0, 0, 24, 102, 116, 121, 112, 4, 5, 6]);
        File.WriteAllText(queue.GetFilePath(record, CaptureFileNames.VideoEdits),
            """{"trimStartMs":250,"trimEndMs":2000,"text":"보관"}""", Encoding.UTF8);
        record.TotalBytes = Directory.EnumerateFiles(queue.GetDirectory(record)).Sum(path => new FileInfo(path).Length);
        queue.SaveRecordMetaOrThrow(record);
        queue.Save();
        return record;
    }

    private static JsonObject KnownSettings(AppSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, JsonDefaults.Readable)!.AsObject();
        node.Remove("storeUpdate");
        node.Remove("storeRating");
        return node;
    }

    private static AppPaths ResolveCaptures(AppPaths paths, AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.Queue.CapturesDirectoryOverride)
            ? paths : paths.WithCapturesRoot(settings.Queue.CapturesDirectoryOverride);

    private static SettingsStore NewStore(AppPaths paths) => new(paths, NullLogger<SettingsStore>.Instance);
    private static CaptureQueue NewQueue(AppPaths paths, AppSettings settings) =>
        new(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
    private static string RecordSnapshot(CaptureQueue queue) => JsonSerializer.Serialize(queue.Records, JsonDefaults.Compact);
    private static string FolderSnapshot(CaptureQueue queue) =>
        JsonSerializer.Serialize(queue.Folders.OrderBy(folder => folder.Id), JsonDefaults.Compact);
    private static Dictionary<string, string> PayloadHashes(string capturesRoot) =>
        Directory.EnumerateFiles(capturesRoot, "*", SearchOption.AllDirectories).ToDictionary(
            path => Path.GetRelativePath(capturesRoot, path),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);
}
