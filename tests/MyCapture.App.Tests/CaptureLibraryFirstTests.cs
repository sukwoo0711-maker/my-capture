using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Imaging;
using MyCapture.Tests;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureLibraryFirstTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedPageUrlSurvivesInitialSaveAndInterruptedPublication(bool interrupt) => StaTestHost.Run(() =>
    {
        const string url = "https://github.com/example/review/pull/313?diff=split#comment";
        using var fixture = new Fixture();
        if (interrupt) fixture.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("interrupted URL metadata publication");
        fixture.Wait(fixture.Select(new CaptureSelectionCompletedEventArgs(fixture.Frame, fixture.Region,
            fixture.Frame.Bitmap, "Review — Browser", recordForRepeat: false, copyToClipboardImmediately: false, sourcePageUrl: url)));
        CaptureRecord pending = fixture.PendingRecord;
        Assert.Equal(url, pending.SourcePageUrl);
        Assert.Equal(MyCapture.Core.Localization.UiText.Get("Capture.WebpageTag"), pending.Tags);
        var queue = new CaptureQueue(fixture.Paths, fixture.Settings.Queue, NullLogger<CaptureQueue>.Instance);
        queue.Load();
        _ = new CapturePersistenceService(queue, fixture.Paths, () => fixture.Settings.Queue, NullLogger<CapturePersistenceService>.Instance);
        CaptureRecord reloaded = Assert.Single(queue.Records);
        Assert.Equal(url, reloaded.SourcePageUrl);
        Assert.Equal(pending.Tags, reloaded.Tags);
        CaptureRecord metadata = JsonSerializer.Deserialize<CaptureRecord>(File.ReadAllText(queue.GetFilePath(reloaded, CaptureFileNames.Meta)),
            MyCapture.Core.Serialization.JsonDefaults.Readable)!;
        Assert.Equal(url, metadata.SourcePageUrl);
    });

    [Theory]
    [InlineData(true, "valid")]
    [InlineData(true, "missing")]
    [InlineData(true, "corrupt")]
    [InlineData(true, "wrong-size")]
    [InlineData(false, "valid")]
    [InlineData(false, "missing")]
    [InlineData(false, "corrupt")]
    [InlineData(false, "wrong-size")]
    [InlineData(true, "legacy-valid")]
    [InlineData(false, "legacy-missing")]
    [InlineData(false, "legacy-corrupt")]
    public void PendingOriginalRecovery_PreservesOrRebuildsInitialTitleDespiteSettingDrift(bool showTitle, string renderState) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Settings.Export.ShowSourceWindowTitle = showTitle;
        fixture.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("injected first publication failure");
        fixture.Wait(fixture.Select(fixture.Selection));
        Assert.Null(fixture.CurrentRecord);
        CaptureRecord pending = fixture.PendingRecord;
        Assert.Equal(showTitle, pending.InitialRenderShowsSourceWindowTitle);
        string originalPath = fixture.Queue.GetFilePath(pending, CaptureFileNames.Original);
        string renderedPath = fixture.Queue.GetFilePath(pending, CaptureFileNames.Rendered);
        string thumbPath = fixture.Queue.GetFilePath(pending, CaptureFileNames.Thumbnail);
        string marker = fixture.Queue.GetFilePath(pending, CaptureFileNames.OriginalPending);
        byte[] originalBytes = File.ReadAllBytes(originalPath);
        BitmapSource initialRendered = Assert.IsAssignableFrom<BitmapSource>(ImageCodec.TryLoad(renderedPath));
        byte[] expected = Pixels(initialRendered);
        byte[] expectedThumbnail = Pixels(ImageCodec.TryLoad(thumbPath)!);
        byte[]? validRenderedBytes = null;
        CaptureRecord journal = JsonSerializer.Deserialize<CaptureRecord>(File.ReadAllText(marker))!;
        Assert.Equal(showTitle, journal.InitialRenderShowsSourceWindowTitle);
        if (showTitle) Assert.NotEqual(Pixels(fixture.Frame.Bitmap), expected);
        else Assert.Equal(Pixels(fixture.Frame.Bitmap), expected);

        switch (renderState)
        {
            case "missing": TestRecycleBin.DeleteFile(renderedPath); break;
            case "corrupt": File.WriteAllBytes(renderedPath, [1, 2, 3]); break;
            case "wrong-size":
                ImageCodec.SavePng(new CroppedBitmap(fixture.Frame.Bitmap, new Int32Rect(0, 0, 8, 8)), renderedPath);
                break;
            case "legacy-valid":
            case "legacy-missing":
            case "legacy-corrupt":
                JsonNode legacy = JsonNode.Parse(File.ReadAllText(marker))!;
                Assert.True(legacy.AsObject().Remove(nameof(CaptureRecord.InitialRenderShowsSourceWindowTitle)));
                File.WriteAllText(marker, legacy.ToJsonString());
                if (renderState == "legacy-missing") TestRecycleBin.DeleteFile(renderedPath);
                if (renderState == "legacy-corrupt") File.WriteAllBytes(renderedPath, [1, 2, 3]);
                break;
        }
        if (renderState is "valid" or "legacy-valid")
        {
            // Distinguish preserving a valid PNG file from decoding/re-encoding its pixels.
            // A normal renderer does not retain this ancillary metadata on a re-encode.
            BitmapSource validImage = ImageCodec.TryLoad(renderedPath)!;
            var metadata = new BitmapMetadata("png");
            metadata.SetQuery("/tEXt/{str=RecoveryEvidence}", "preserve these PNG bytes");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(validImage, null, metadata, null));
            using (FileStream output = File.Create(renderedPath)) encoder.Save(output);
            validRenderedBytes = File.ReadAllBytes(renderedPath);
        }
        fixture.Settings.Export.ShowSourceWindowTitle = !showTitle;
        var restartedQueue = new CaptureQueue(fixture.Paths, fixture.Settings.Queue, NullLogger<CaptureQueue>.Instance);
        restartedQueue.Load();
        var restarted = new CapturePersistenceService(restartedQueue, fixture.Paths, () => fixture.Settings.Queue,
            NullLogger<CapturePersistenceService>.Instance);
        CaptureRecord recovered = Assert.Single(restartedQueue.Records);
        Assert.Equal(pending.Id, recovered.Id);
        Assert.Equal(pending.CreatedAt, recovered.CreatedAt);
        if (renderState.StartsWith("legacy-", StringComparison.Ordinal)) Assert.Null(recovered.InitialRenderShowsSourceWindowTitle);
        else Assert.Equal(showTitle, recovered.InitialRenderShowsSourceWindowTitle);
        if (validRenderedBytes is not null) Assert.Equal(validRenderedBytes, File.ReadAllBytes(renderedPath));
        using (FileStream currentFile = File.OpenRead(renderedPath))
        {
            BitmapFrame onDisk = BitmapFrame.Create(currentFile, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.Equal(expected, Pixels(onDisk));
        }
        Assert.Equal(expected, Pixels(ImageCodec.TryLoad(renderedPath)!));
        Assert.Equal(expectedThumbnail, Pixels(ImageCodec.TryLoad(thumbPath)!));
        Assert.Equal(originalBytes, File.ReadAllBytes(originalPath));
        Assert.Empty(AnnotationDocument.TryFromJson(File.ReadAllText(
            restartedQueue.GetFilePath(recovered, CaptureFileNames.Layers)))!.Items);
        Assert.False(File.Exists(marker));
        Assert.False(restarted.IsBusy(recovered.Id));
        // Queue/meta JSON copies must retain the new flag on a second startup as well.
        var nextQueue = new CaptureQueue(fixture.Paths, fixture.Settings.Queue, NullLogger<CaptureQueue>.Instance);
        nextQueue.Load();
        Assert.Equal(recovered.InitialRenderShowsSourceWindowTitle, Assert.Single(nextQueue.Records).InitialRenderShowsSourceWindowTitle);
        _ = new CapturePersistenceService(nextQueue, fixture.Paths, () => fixture.Settings.Queue,
            NullLogger<CapturePersistenceService>.Instance);
        Assert.Equal(expected, Pixels(ImageCodec.TryLoad(renderedPath)!));
        GC.KeepAlive(initialRendered); // Recovery must not mistake a live URI-cached image for the current file.
        if (validRenderedBytes is not null) Assert.Equal(validRenderedBytes, File.ReadAllBytes(renderedPath));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PendingOriginalRetry_UsesCapturedTitleOptionInsteadOfChangedSetting(bool showTitle) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Settings.Export.ShowSourceWindowTitle = showTitle;
        fixture.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("injected first publication failure");
        fixture.Wait(fixture.Select(fixture.Selection));
        CaptureRecord pending = fixture.PendingRecord;
        string renderedPath = fixture.Queue.GetFilePath(pending, CaptureFileNames.Rendered);
        byte[] expected = Pixels(ImageCodec.TryLoad(renderedPath)!);
        fixture.Settings.Export.ShowSourceWindowTitle = !showTitle;
        int publications = 0;
        fixture.Persistence.BeforeRecordMetadataCommit = id =>
        {
            Assert.Equal(pending.Id, id);
            Assert.Equal(expected, Pixels(ImageCodec.TryLoad(renderedPath)!));
            publications++;
        };
        var result = new AnnotationEditingResult(fixture.Frame, fixture.Region, fixture.Frame.Bitmap,
            AnnotationDocument.CreateFor(64, 48), EditorCommitAction.Done,
            new Dictionary<string, BitmapSource>(), new Dictionary<string, string>(),
            sourceWindowTitle: pending.SourceWindowTitle, showSourceWindowTitle: showTitle);
        Assert.True(fixture.Wait(fixture.Commit(result)));
        Assert.True(publications > 0);
        Assert.Same(pending, fixture.CurrentRecord);
        Assert.Equal(expected, Pixels(fixture.CopiedImages[^1]));
        Assert.Equal(showTitle, pending.InitialRenderShowsSourceWindowTitle);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstSaveAsRetry_UsesPendingCaptureNameAndTime_WithoutPublishingOnCancel(bool reduced) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Settings.Export.ShowSourceWindowTitle = true;
        fixture.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("injected first publication failure");
        fixture.Wait(fixture.Select(fixture.Selection));
        Assert.Null(fixture.CurrentRecord);
        CaptureRecord pending = fixture.PendingRecord;
        pending.CreatedAt = pending.CreatedAt.AddMinutes(-5);
        string expectedName = QuickSaveNaming.BuildStem(fixture.Settings.Export.FileNamePattern,
            pending.CreatedAt, pending.SourceWindowTitle) + ".png";
        string marker = fixture.Queue.GetFilePath(pending, CaptureFileNames.OriginalPending);
        byte[] markerBeforeCancel = File.ReadAllBytes(marker);
        int publications = 0, prompts = 0;
        bool accepted = false;
        fixture.Persistence.BeforeRecordMetadataCommit = _ => publications++;
        fixture.CommitService.SaveAsPrompt = suggested =>
        {
            Assert.False(reduced);
            Assert.Equal(expectedName, Path.GetFileName(suggested));
            prompts++;
            return accepted ? Path.Combine(fixture.Paths.DataRoot, expectedName) : null;
        };
        fixture.CommitService.ReducedExportPrompt = (_, suggested) =>
        {
            Assert.True(reduced);
            Assert.Equal(expectedName, Path.GetFileName(suggested));
            prompts++;
            return accepted;
        };
        var result = new AnnotationEditingResult(fixture.Frame, fixture.Region, fixture.Frame.Bitmap,
            AnnotationDocument.CreateFor(64, 48), EditorCommitAction.SaveAs,
            new Dictionary<string, BitmapSource>(), new Dictionary<string, string>(), reduceExport: reduced,
            sourceWindowTitle: pending.SourceWindowTitle, showSourceWindowTitle: true);
        Assert.False(fixture.Wait(fixture.Commit(result)));
        Assert.Null(fixture.CurrentRecord);
        Assert.Equal(0, publications);
        Assert.Equal(0, pending.ContentRevision);
        Assert.Equal(markerBeforeCancel, File.ReadAllBytes(marker));
        Assert.Empty(fixture.CopiedImages);
        accepted = true;
        Assert.True(fixture.Wait(fixture.Commit(result)));
        Assert.Equal(2, prompts);
        Assert.True(publications > 0);
        Assert.Same(pending, fixture.CurrentRecord);
        Assert.Equal(pending.Id, Assert.Single(fixture.Queue.Records).Id);
        Assert.Equal(1, pending.ContentRevision);
        Assert.False(File.Exists(marker));
        if (!reduced) Assert.True(File.Exists(Path.Combine(fixture.Paths.DataRoot, expectedName)));
    });

    [Fact]
    public void TitleEnabled_EscapeKeepsTitledLibraryResultAndUntouchedOriginal() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Settings.Export.ShowSourceWindowTitle = true;
        Func<bool>? previous = AnnotationEditorPreferences.ReadShowSourceWindowTitle;
        AnnotationEditorPreferences.ReadShowSourceWindowTitle = () => true;
        using var coordinator = new CaptureOverlayCoordinator(
            new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
            NullLogger<CaptureOverlayCoordinator>.Instance)
        {
            SelectionPersistRequested = selection => fixture.Select(new CaptureSelectionCompletedEventArgs(
                selection.Frame, selection.BitmapRegion, selection.SelectedBitmap, selection.SourceTitle,
                recordForRepeat: false, copyToClipboardImmediately: true)),
            RequiresCaptureExclusion = () => true,
        };
        AnnotationEditorWindow? editor = null;
        coordinator.ApplyCaptureExclusion = window =>
        {
            editor = Assert.IsType<AnnotationEditorWindow>(window);
            CaptureRecord record = Assert.Single(fixture.Queue.Records);
            Assert.Equal(Pixels(fixture.Frame.Bitmap), Pixels(ImageCodec.TryLoad(
                fixture.Queue.GetFilePath(record, CaptureFileNames.Original))!));
            Assert.NotEqual(Pixels(fixture.Frame.Bitmap), Pixels(ImageCodec.TryLoad(
                fixture.Queue.GetFilePath(record, CaptureFileNames.Rendered))!));
            Assert.NotEmpty(fixture.CopiedImages);
            Assert.Equal(Pixels(fixture.CopiedImages[^1]), Pixels(ImageCodec.TryLoad(
                fixture.Queue.GetFilePath(record, CaptureFileNames.Rendered))!));
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000;
            window.Top = -10000;
            window.ShowActivated = false;
            return true;
        };
        try
        {
            Assert.True(coordinator.StartWithSelection(fixture.Frame, fixture.Region, "매출.xlsx - Excel"));
            PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
            Assert.NotNull(editor);
            Assert.True(editor.Editor.HandleShortcut(Key.Escape, ModifierKeys.None));
            var reloaded = new CaptureQueue(fixture.Paths, fixture.Settings.Queue, NullLogger<CaptureQueue>.Instance);
            reloaded.Load();
            CaptureRecord saved = Assert.Single(reloaded.Records);
            Assert.NotEqual(Pixels(fixture.Frame.Bitmap), Pixels(ImageCodec.TryLoad(
                reloaded.GetFilePath(saved, CaptureFileNames.Rendered))!));
            Assert.False(saved.HasAnnotations);
        }
        finally
        {
            coordinator.Cancel();
            AnnotationEditorPreferences.ReadShowSourceWindowTitle = previous;
        }
    });

    [Theory]
    [InlineData((int)EditorCommitAction.Done)]
    [InlineData((int)EditorCommitAction.CopyToClipboard)]
    [InlineData((int)EditorCommitAction.QuickSave)]
    [InlineData((int)EditorCommitAction.SaveAs)]
    public void TitleEnabled_AllCommitActionsSharePixels_AndReeditCanRemoveTitle(int actionValue) => StaTestHost.Run(() =>
    {
        var action = (EditorCommitAction)actionValue;
        using var fixture = new Fixture();
        fixture.Settings.Export.ShowSourceWindowTitle = true;
        fixture.Wait(fixture.Select(fixture.Selection));
        CaptureRecord record = Assert.Single(fixture.Queue.Records);
        var document = AnnotationDocument.CreateFor(64, 48);
        string chosen = Path.Combine(fixture.Paths.DataRoot, "title-save-as.png");
        fixture.CommitService.SaveAsPrompt = _ => chosen;
        var result = fixture.Result(document, action);
        Assert.True(fixture.Wait(fixture.Commit(result)));
        byte[] expected = Pixels(ImageCodec.TryLoad(fixture.Queue.GetFilePath(record, CaptureFileNames.Rendered))!);
        Assert.Equal(expected, Pixels(fixture.CopiedImages[^1]));
        if (action == EditorCommitAction.SaveAs) Assert.Equal(expected, Pixels(ImageCodec.TryLoad(chosen)!));
        if (action == EditorCommitAction.QuickSave)
            Assert.Equal(expected, Pixels(ImageCodec.TryLoad(Assert.Single(Directory.GetFiles(fixture.Paths.QuickSaveRoot, "*.png")))!));

        // Another save starts from the same original and layers, so the title is never doubled.
        Assert.True(fixture.Wait(fixture.Commit(fixture.Result(document, EditorCommitAction.Done))));
        Assert.Equal(expected, Pixels(fixture.CopiedImages[^1]));
        Assert.Empty(AnnotationDocument.TryFromJson(File.ReadAllText(
            fixture.Queue.GetFilePath(record, CaptureFileNames.Layers)))!.Items);
        fixture.Settings.Export.ShowSourceWindowTitle = false;
        Assert.True(fixture.Wait(fixture.Commit(fixture.Result(document, EditorCommitAction.Done))));
        Assert.Equal(Pixels(fixture.Frame.Bitmap), Pixels(fixture.CopiedImages[^1]));
        Assert.Equal(Pixels(fixture.Frame.Bitmap), Pixels(ImageCodec.TryLoad(
            fixture.Queue.GetFilePath(record, CaptureFileNames.Original))!));
        Assert.Single(fixture.Queue.Records);
    });

    [Fact]
    public void TitledSaveAs_CancelAndReducedExportRetryKeepTheExistingRecord() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Settings.Export.ShowSourceWindowTitle = true;
        fixture.Wait(fixture.Select(fixture.Selection));
        CaptureRecord record = Assert.Single(fixture.Queue.Records);
        long revision = record.ContentRevision;
        fixture.CommitService.SaveAsPrompt = _ => null;
        Assert.False(fixture.Wait(fixture.Commit(fixture.Result(AnnotationDocument.CreateFor(64, 48), EditorCommitAction.SaveAs))));
        Assert.Equal(revision, record.ContentRevision);
        BitmapSource? reducedInput = null;
        fixture.CommitService.ReducedExportPrompt = (image, _) => { reducedInput = image; return true; };
        var reduced = new AnnotationEditingResult(fixture.Frame, fixture.Region, fixture.Frame.Bitmap,
            AnnotationDocument.CreateFor(64, 48), EditorCommitAction.SaveAs,
            new Dictionary<string, BitmapSource>(), new Dictionary<string, string>(), reduceExport: true);
        Assert.True(fixture.Wait(fixture.Commit(reduced)));
        Assert.Equal(Pixels(reducedInput!), Pixels(fixture.CopiedImages[^1]));
        Assert.Equal(record.Id, Assert.Single(fixture.Queue.Records).Id);
    });

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
    }

    [Fact]
    public void Selection_IsDurableBeforeEditorOpens_AndEscapeKeepsLibraryOriginal() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Queue.BeforePublicationWriteForTest = (_, _) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        AnnotationEditorWindow? editor = null;
        using var coordinator = new CaptureOverlayCoordinator(
            new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            new WindowCandidateService(NullLogger<WindowCandidateService>.Instance),
            NullLogger<CaptureOverlayCoordinator>.Instance)
        {
            SelectionPersistRequested = fixture.Select,
            RequiresCaptureExclusion = () => true,
            ApplyCaptureExclusion = window =>
            {
                editor = Assert.IsType<AnnotationEditorWindow>(window);
                CaptureRecord record = Assert.Single(fixture.Queue.Records);
                Assert.True(File.Exists(fixture.Paths.IndexFile));
                Assert.True(File.Exists(fixture.Queue.GetFilePath(record, CaptureFileNames.Original)));
                Assert.True(File.Exists(Path.Combine(fixture.Queue.GetDirectory(record), "meta.json")));
                Assert.False(File.Exists(fixture.Queue.GetFilePath(record, CaptureFileNames.OriginalPending)));
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -10000;
                window.Top = -10000;
                window.ShowActivated = false;
                return true;
            },
        };
        try
        {
            Assert.True(coordinator.StartWithSelection(fixture.Frame, fixture.Region, "매출.xlsx - Excel"));
            PumpUntil(() => entered.IsSet);
            Assert.Null(editor);
            Assert.False(coordinator.LastTransitionForTest.IsCompleted);
            release.Set();
            PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
            Assert.NotNull(editor);
            Assert.True(editor.Editor.HandleShortcut(Key.Escape, ModifierKeys.None));
            Assert.False(coordinator.IsActive);
            var reloaded = new CaptureQueue(fixture.Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            reloaded.Load();
            Assert.Equal("매출.xlsx - Excel", Assert.Single(reloaded.Records).SourceWindowTitle);
        }
        finally
        {
            release.Set();
            PumpUntil(() => coordinator.LastTransitionForTest.IsCompleted);
            coordinator.Cancel();
        }
    });

    [Fact]
    public void EditedSave_ReplacesRenderedImageForTheSameRecord_AndUsesTitleInExportSuggestion() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Wait(fixture.Select(fixture.Selection));
        CaptureRecord record = Assert.Single(fixture.Queue.Records);
        string originalPath = fixture.Queue.GetFilePath(record, CaptureFileNames.Original);
        string renderedPath = fixture.Queue.GetFilePath(record, CaptureFileNames.Rendered);
        byte[] original = File.ReadAllBytes(originalPath);
        var document = AnnotationDocument.CreateFor(64, 48);
        document.Add(new RectangleAnnotation
        {
            Rect = new RectD(4, 4, 40, 30),
            Stroke = ColorRgba.FromRgb(255, 0, 0),
            StrokeThickness = 4,
        });
        AnnotationEditingResult edit = fixture.Result(document, EditorCommitAction.Done);
        Assert.True(fixture.Wait(fixture.Commit(edit)));
        Assert.Equal(record.Id, Assert.Single(fixture.Queue.Records).Id);
        Assert.Equal(original, File.ReadAllBytes(originalPath));
        Assert.NotEqual(original, File.ReadAllBytes(renderedPath));
        Assert.True(record.HasAnnotations);
        Assert.Single(AnnotationDocument.TryFromJson(File.ReadAllText(
            fixture.Queue.GetFilePath(record, CaptureFileNames.Layers)))!.Items);
        Assert.True(fixture.Wait(fixture.Commit(edit)));
        Assert.Equal(record.Id, Assert.Single(fixture.Queue.Records).Id);

        string? suggested = null;
        fixture.CommitService.SaveAsPrompt = path => { suggested = path; return null; };
        Assert.False(fixture.Wait(fixture.Commit(fixture.Result(document, EditorCommitAction.SaveAs))));
        Assert.Equal(QuickSaveNaming.BuildStem(QuickSaveNaming.DefaultPattern, record.CreatedAt,
            fixture.Selection.SourceTitle) + ".png", Path.GetFileName(suggested));
    });

    [Fact]
    public void FailedInitialSave_RemainsUncommitted_AndRetryUsesOriginalIdentity() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("synthetic storage failure");
        fixture.Wait(fixture.Select(fixture.Selection));
        Assert.Null(fixture.CurrentRecord);
        CaptureRecord pending = fixture.PendingRecord;
        fixture.Persistence.BeforeRecordMetadataCommit = null;
        Assert.True(fixture.Wait(fixture.Commit(fixture.Result(
            AnnotationDocument.CreateFor(64, 48), EditorCommitAction.Done))));
        Assert.Equal(pending.Id, Assert.Single(fixture.Queue.Records).Id);
        Assert.Equal(pending.Id, fixture.CurrentRecord!.Id);
        Assert.False(File.Exists(fixture.Queue.GetFilePath(pending, CaptureFileNames.OriginalPending)));
    });

    [Fact]
    public void FirstQuickSaveRetry_UsesRecoveredRecordTitleAndOriginalCaptureTime() => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture();
        fixture.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("synthetic storage failure");
        fixture.Wait(fixture.Select(fixture.Selection));
        Assert.Null(fixture.CurrentRecord);
        CaptureRecord pending = fixture.PendingRecord;
        // Distinguish capture time from retry time deterministically without a timed wait.
        DateTimeOffset capturedAt = pending.CreatedAt.AddMinutes(-5);
        pending.CreatedAt = capturedAt;
        fixture.Persistence.BeforeRecordMetadataCommit = null;

        Assert.True(fixture.Wait(fixture.Commit(fixture.Result(
            AnnotationDocument.CreateFor(64, 48), EditorCommitAction.QuickSave))));

        CaptureRecord recovered = Assert.Single(fixture.Queue.Records);
        Assert.Equal(pending.Id, recovered.Id);
        Assert.Same(recovered, fixture.CurrentRecord);
        Assert.Equal(capturedAt, recovered.CreatedAt);
        string exported = Assert.Single(Directory.GetFiles(fixture.Paths.QuickSaveRoot, "*.png"));
        Assert.Equal(QuickSaveNaming.BuildStem(QuickSaveNaming.DefaultPattern, capturedAt,
            fixture.Selection.SourceTitle) + ".png", Path.GetFileName(exported));
        using FileStream stream = File.OpenRead(exported);
        BitmapFrame image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal(64, image.PixelWidth);
        Assert.Equal(48, image.PixelHeight);
        Assert.False(File.Exists(fixture.Queue.GetFilePath(recovered, CaptureFileNames.OriginalPending)));
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CapacityPressure_CannotEvictCaptureBetweenPersistenceAndEditor(bool retry, bool byteLimit) => StaTestHost.Run(() =>
    {
        using var fixture = new Fixture(byteLimit
            ? new QueueSettings { MaxBytes = 1 }
            : new QueueSettings { MaxItems = 1 });
        if (!byteLimit)
        {
            CaptureRecord older = fixture.Wait(fixture.Persistence.PersistOriginalAsync(
                fixture.Frame.Bitmap, 1, "older pinned capture", string.Empty));
            older.IsPinned = true;
        }
        var evicted = new List<Guid>();
        fixture.Queue.Evicted += (_, args) => evicted.Add(args.Record.Id);
        if (retry)
            fixture.Persistence.BeforeRecordMetadataCommit = _ => throw new IOException("synthetic capacity-pressure save failure");
        fixture.Wait(fixture.Select(fixture.Selection));
        CaptureRecord pending = fixture.PendingRecord;
        AnnotationEditingResult result = fixture.Result(AnnotationDocument.CreateFor(64, 48), EditorCommitAction.Done);
        if (retry)
        {
            Assert.Null(fixture.CurrentRecord);
            Assert.Null(fixture.CurrentEditSession);
            // A second failure must also release its editor lease; a later successful retry
            // still publishes and edits the same identity while the capacity is exceeded.
            Assert.False(fixture.Wait(fixture.Commit(result)));
            Assert.Null(fixture.CurrentEditSession);
            fixture.Persistence.BeforeRecordMetadataCommit = null;
            Assert.True(fixture.Wait(fixture.Commit(result)));
        }
        Assert.Same(pending, fixture.CurrentRecord);
        Assert.Same(pending, fixture.Queue.Find(pending.Id));
        Assert.DoesNotContain(pending.Id, evicted);
        Assert.NotNull(fixture.CurrentEditSession);
        Assert.True(File.Exists(fixture.Queue.GetFilePath(pending, CaptureFileNames.Original)));
        Assert.True(fixture.Wait(fixture.Commit(result)));
        Assert.Same(pending, fixture.Queue.Find(pending.Id));
        Assert.DoesNotContain(pending.Id, evicted);

        // The queue deliberately retains the newest sole unpinned/oversized capture.
        // After the editor releases its lease, a newer capture makes this one eligible
        // for normal capacity eviction. A leaked retry lease would prevent that eviction.
        fixture.ReleaseEditSession();
        Assert.False(fixture.Persistence.IsBusy(pending.Id));
        CaptureRecord successor = fixture.Wait(fixture.Persistence.PersistOriginalAsync(
            fixture.Frame.Bitmap, 1, "next capture", string.Empty));
        Assert.Same(successor, fixture.Queue.Find(successor.Id));
        Assert.Null(fixture.Queue.Find(pending.Id));
        Assert.Contains(pending.Id, evicted);
    });

    private static void PumpUntil(Func<bool> completed)
    {
        var timeout = Stopwatch.StartNew();
        while (!completed() && timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
        Assert.True(completed(), "The capture transition did not finish.");
    }

    private sealed class Fixture : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly App _app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;
        private readonly string _root = TestRecycleBin.CreateTempSubdirectory("mycapture-library-first-").FullName;
        internal AppPaths Paths { get; }
        internal CaptureQueue Queue { get; }
        internal CapturePersistenceService Persistence { get; }
        internal CaptureCommitService CommitService { get; }
        internal AppSettings Settings { get; } = new();
        internal List<BitmapSource> CopiedImages { get; } = [];
        internal FrozenFrame Frame { get; }
        internal RectD Region { get; } = new(0, 0, 64, 48);
        internal CaptureSelectionCompletedEventArgs Selection => new(Frame, Region, Frame.Bitmap,
            "CaptureService.cs - my-capture - Visual Studio Code", recordForRepeat: false, copyToClipboardImmediately: false);
        internal CaptureRecord? CurrentRecord => (CaptureRecord?)Get("_currentRecord");
        internal CaptureRecord PendingRecord => (CaptureRecord)Get("_pendingRecord")!;
        internal IDisposable? CurrentEditSession => (IDisposable?)Get("_currentEditSession");

        internal Fixture(QueueSettings? queueSettings = null)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Paths = AppPaths.CreateForRoot(_root);
            AppSettings settings = Settings;
            if (queueSettings is not null) settings.Queue = queueSettings;
            Queue = new CaptureQueue(Paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            Persistence = new CapturePersistenceService(Queue, Paths, () => settings.Queue,
                NullLogger<CapturePersistenceService>.Instance);
            CommitService = new CaptureCommitService(Persistence, () => settings, () => Paths,
                NullLogger<CaptureCommitService>.Instance, image => { CopiedImages.Add(image); return Task.FromResult(true); });
            Set("_queue", Queue);
            Set("_settings", settings);
            Set("_persistence", Persistence);
            Set("_commit", CommitService);
            BitmapSource bitmap = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32,
                null, Enumerable.Repeat((byte)255, 64 * 48 * 4).ToArray(), 64 * 4);
            bitmap.Freeze();
            Frame = new FrozenFrame(bitmap, Region, null, 0);
        }

        internal Task Select(CaptureSelectionCompletedEventArgs selection) =>
            (Task)typeof(App).GetMethod("OnCaptureSelectionCompletedAsync", Flags)!.Invoke(_app, [selection])!;
        internal Task<bool> Commit(AnnotationEditingResult result) =>
            (Task<bool>)typeof(App).GetMethod("HandleCommitAsync", Flags)!.Invoke(_app, [result])!;
        internal AnnotationEditingResult Result(AnnotationDocument document, EditorCommitAction action) =>
            new(Frame, Region, Frame.Bitmap, document, action,
                new Dictionary<string, BitmapSource>(), new Dictionary<string, string>());
        internal void Wait(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        internal T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
        private object? Get(string name) => typeof(App).GetField(name, Flags)!.GetValue(_app);
        private void Set(string name, object value) => typeof(App).GetField(name, Flags)!.SetValue(_app, value);

        internal void ReleaseEditSession()
        {
            CurrentEditSession?.Dispose();
            typeof(App).GetField("_currentEditSession", Flags)!.SetValue(_app, null);
        }

        public void Dispose()
        {
            ReleaseEditSession();
            SynchronizationContext.SetSynchronizationContext(_previous);
            if (Directory.Exists(_root))
                TestRecycleBin.DeleteDirectory(_root, recursive: true);
        }
    }
}
