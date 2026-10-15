using System.IO;
using System.IO.Compression;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.App.Settings;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class FolderR2Tests
{
    [Fact]
    public async Task SettingsDraftOnlyAppliesExplicitFolderChangesAndKeepsTheLiveDestination()
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();
        var paths = AppPaths.CreateForRoot(root); paths.EnsureCreated();
        var settings = new AppSettings();
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        var draft = new SettingsDraft(settings);
        var folder = await queue.CreateFolderAsync("live");
        await queue.SetNewCaptureFolderAsync(folder.Id, true);
        var next = draft.ToAppSettings();
        SettingsApplyService.ReconcileFolderDestination(next.Queue, queue, draft.FolderDestinationEdited);
        queue.UpdateLimits(next.Queue);
        Assert.True(queue.UseCurrentFolderForNewCaptures);
        Assert.Equal(folder.Id, queue.DefaultCaptureFolderId);
        var changed = new SettingsDraft(next) { UseCurrentFolderForNewCaptures = false };
        Assert.True(changed.FolderDestinationEdited);
        var disabled = changed.ToAppSettings();
        SettingsApplyService.ReconcileFolderDestination(disabled.Queue, queue, changed.FolderDestinationEdited);
        queue.UpdateLimits(disabled.Queue);
        Assert.False(queue.UseCurrentFolderForNewCaptures);
        Assert.Null(queue.DefaultCaptureFolderId);
    }

    [Theory]
    [InlineData("+", "+")]
    [InlineData("Ctrl++", "Ctrl,+")]
    [InlineData("Ctrl+Shift++", "Ctrl,Shift,+")]
    [InlineData("Ctrl+S", "Ctrl,S")]
    public void PlusKeyRemainsAKeycap(string chord, string expected) => StaTestHost.Run(() =>
    {
        var panel = (Panel)ShortcutHelpWindow.Keycaps(chord);
        string actual = string.Join(',', panel.Children.OfType<Border>().Select(b => ((TextBlock)b.Child).Text));
        Assert.Equal(expected, actual);
    });

    [Fact]
    public void DeepTreeFitsItsFullDestinationAndTruncationCollisionsRemainDistinct()
    {
        string parent = string.Join('/', Enumerable.Range(0, 17).Select(i => new string('a', 70) + i));
        var first = new CaptureRecord(); var second = new CaptureRecord();
        var entries = new[] {
            new GalleryFolderExportEntry(first, 0, "one.png", parent + "/" + new string('b', 80) + ".png"),
            new GalleryFolderExportEntry(second, 0, "two.png", parent + "/" + new string('b', 79) + "c.png") };
        var dirs = Enumerable.Range(1, 17).Select(i => string.Join('/', parent.Split('/').Take(i))).ToArray();
        var plan = new GalleryFolderExportPlan(entries, dirs, [dirs[0]]);
        var result = GalleryExportPathBudget.Fit(plan, 130);
        Assert.Equal(17, result.Directories.Count);
        Assert.All(result.Entries, e => Assert.True(131 + e.RelativePath.Length <= 240, e.RelativePath));
        Assert.All(result.Directories, p => Assert.True(131 + p.Length <= 240, p));
        Assert.Equal(2, result.Entries.Select(e => e.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(result.Entries, e => e.RelativePath.Contains("~2"));
        Assert.Throws<PathTooLongException>(() => GalleryExportPathBudget.Fit(plan, 230));
    }

    [Fact]
    public async Task DeepFolderExportAndZipStayWithin240AndKeepPayloadsAndExistingArchives()
    {
        string root = ValidatedFilePaths.CreateTemporaryDirectory();
        var paths = AppPaths.CreateForRoot(root); paths.EnsureCreated();
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        CaptureFolder? parent = null;
        for (int depth = 0; depth < 16; depth++)
            parent = await queue.CreateFolderAsync(new string('f', 75) + depth, parent?.Id);
        var record = new CaptureRecord { Width = 1, Height = 1, Title = new string('n', 80) };
        queue.Add(record);
        Directory.CreateDirectory(queue.GetDirectory(record));
        byte[] png = ExportPngFixture.Create();
        File.WriteAllBytes(queue.GetFilePath(record, CaptureFileNames.Rendered), png);
        await queue.MoveCapturesAsync([record.Id], parent!.Id);
        var exporter = new GalleryFolderExportService(queue, Path.Combine(root, "stage"));
        using var prepared = await exporter.PrepareAsync(exporter.BuildPlan(CaptureFolderScope.All));
        Assert.All(Directory.GetFileSystemEntries(prepared.BatchRoot, "*", SearchOption.AllDirectories), p => Assert.True(p.Length <= 240, p));
        string destination = ValidatedFilePaths.DirectoryWithinRoot(root, new string('d', 50)); Directory.CreateDirectory(destination);
        await GalleryFolderExportService.ExportDirectoryAsync(prepared, destination);
        string exported = ValidatedFilePaths.FileWithinRoot(destination,
            Assert.Single(Directory.GetFiles(destination, "*.png", SearchOption.AllDirectories)), ".png");
        Assert.True(exported.Length <= 240); Assert.Equal(png, File.ReadAllBytes(exported));
        string existing = ValidatedFilePaths.FileWithinRoot(root, "export.zip", ".zip"); File.WriteAllText(existing, "keep");
        string archivePath = GalleryWindow.UniqueArchivePath(existing);
        Assert.EndsWith("export~2.zip", archivePath);
        await GalleryFolderExportService.ExportZipAsync(prepared, archivePath);
        Assert.Equal("keep", File.ReadAllText(existing));
        string extract = ValidatedFilePaths.DirectoryWithinRoot(root, "export~2");
        ZipFile.ExtractToDirectory(archivePath, extract);
        string extracted = ValidatedFilePaths.FileWithinRoot(extract,
            Assert.Single(Directory.GetFiles(extract, "*.png", SearchOption.AllDirectories)), ".png");
        Assert.True(extracted.Length <= 240); Assert.Equal(png, File.ReadAllBytes(extracted));
    }
}
