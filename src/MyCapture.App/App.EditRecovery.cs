using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyCapture.App.Editing;
using MyCapture.App.Recording;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Serialization;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

internal enum AppExitReason { Explicit, StoreUpdate, LegacyImport }

public partial class App
{
    internal EditDraftStore? DraftStore { get; private set; }
    private bool _preparingExit;
    internal Func<Task<bool>>? PrepareCapturePersistenceForExitAsync { get; set; }
    /// <summary>Call after persistence and queue initialization, before normal editor creation.</summary>
    internal async Task InitializeEditRecoveryAsync()
    {
        if (DraftStore is not null || _services is null) return;
        // Drafts are app-owned working copies; retired sessions are deleted, not recycled.
        DraftStore = new EditDraftStore(Path.Combine(_services.GetRequiredService<AppPaths>().DataRoot, "edit-drafts"));
        IReadOnlyList<(EditDraft Draft, string Directory)> available;
        try { available = await Task.Run(DraftStore.ReadAvailable); }
        catch (Exception error) { _log?.LogError(error, "Could not enumerate editing drafts"); return; }
        if (available.Count == 0) return;

        // One dialog lists every draft, newest first. It keeps initial recovery pending until it
        // closes, exactly as the one-draft-at-a-time prompts did.
        var drafts = new Dictionary<Guid, EditDraft>();
        var items = new List<DraftRecoveryItem>();
        foreach (EditDraft draft in available.Select(entry => entry.Draft).OrderByDescending(draft => draft.SavedUtc))
        {
            drafts[draft.SessionId] = draft;
            items.Add(await EditRecoveryPresentation.DescribeAsync(DraftStore, draft, VideoDraftThumbnailPath));
        }
        DraftRecoveryDialog? dialog = null;
        dialog = new DraftRecoveryDialog(items, (item, choice) => ResolveDraftAsync(drafts[item.SessionId], choice, dialog));
        dialog.ShowInFront();
        await dialog.Completion;
    }

    /// <summary>Recovers or discards one draft; a failure is reported and leaves the draft listed.</summary>
    private async Task<bool> ResolveDraftAsync(EditDraft draft, DraftRecoveryChoice choice, Window? owner)
    {
        try
        {
            if (choice == DraftRecoveryChoice.Discard)
            {
                await Task.Run(() => DraftStore!.RetireAsync(draft.SessionId));
                return true;
            }
            if (draft.Kind == "image") await OpenRecoveredImageAsync(draft);
            else await OpenRecoveredVideoAsync(draft);
            return true;
        }
        catch (Exception error)
        {
            _log?.LogError(error, "Could not {Choice} editing draft {SessionId}", choice, draft.SessionId);
            Themes.ThemedDialog.Error(owner is { IsVisible: true } ? owner : null,
                UiText.Get(choice == DraftRecoveryChoice.Discard ? "EditRecovery_DiscardFailed" : "EditRecovery_OpenFailed"), error);
            return false;
        }
    }

    /// <summary>The library thumbnail of the recording a video draft edits, when it is still there.</summary>
    private string? VideoDraftThumbnailPath(EditDraft draft)
    {
        if (_queue is null || _videoLibrary is null || !draft.Metadata.TryGetValue("source", out string? source)) return null;
        string full = Path.GetFullPath(source);
        CaptureRecord? record = _queue.Records.FirstOrDefault(x => x.IsVideo
            && string.Equals(Path.GetFullPath(_videoLibrary.SourcePath(x)), full, StringComparison.OrdinalIgnoreCase));
        return record is null ? null : _queue.GetFilePath(record, CaptureFileNames.Thumbnail);
    }

    /// <summary>Store API can install after this gate; the app remains alive to show progress.</summary>
    internal Task<bool> PrepareForStoreUpdateAsync() => PrepareForAppExitAsync(AppExitReason.StoreUpdate);
    internal async Task<bool> RequestSafeShutdownAsync()
    {
        await StopLegacyImportForExitAsync();
        if (_storeInstallationPending) return false;
        if (!await PrepareForAppExitAsync(AppExitReason.Explicit)) return false;
        Shutdown(0); return true;
    }
    private async Task<bool> PrepareForAppExitAsync(AppExitReason reason)
    {
        Dispatcher.VerifyAccess();
        if (_preparingExit) return false;
        _preparingExit = true;
        bool? galleryEnabled = _galleryWindow?.IsEnabled;
        var settingsStates = new List<(Settings.SettingsWindow Window, bool Enabled)>();
        if (_galleryWindow is not null) _galleryWindow.IsEnabled = false;
        try
        {
            if (_recorder?.RequiresCaptureExclusion == true
                && Themes.ThemedDialog.Show(UiText.Get(reason == AppExitReason.LegacyImport ? "LegacyImport.RecordingMessage" : "EditRecovery_RecordingMessage"), UiText.Get("EditRecovery_RecordingTitle"),
                    MessageBoxButton.OKCancel, defaultResult: MessageBoxResult.Cancel) != MessageBoxResult.OK) return false;
            if (_recorder is not null && !await _recorder.PrepareForAppExitAsync()) return false;
            if (PrepareCapturePersistenceForExitAsync is { } prepareCapture && !await prepareCapture()) return false;
            foreach (var settings in Windows.Cast<Window>().OfType<Settings.SettingsWindow>().ToArray())
            {
                if (!settings.PrepareForAppExit(reason != AppExitReason.Explicit)) return false;
                settingsStates.Add((settings, settings.IsEnabled));
                settings.IsEnabled = false;
            }
            foreach (Window window in Windows.Cast<Window>().ToArray())
            {
                if (window is AnnotationEditorWindow image && !await image.PrepareForAppExitAsync()) return false;
                if (window is VideoEditorWindow video && !await video.PrepareForAppExitAsync()) return false;
            }
            await EditDraftSession.WaitForFinalizationsAsync();
            return true;
        }
        catch (Exception error) { _log?.LogError(error, "Could not prepare app exit ({Reason})", reason); return false; }
        finally
        {
            foreach (var (settings, settingsEnabled) in settingsStates) settings.IsEnabled = settingsEnabled;
            if (_galleryWindow is not null && galleryEnabled is { } enabled) _galleryWindow.IsEnabled = enabled;
            _preparingExit = false;
            RequestLibraryMaintenance();
        }
    }
    /// <summary>No user prompts on Windows session ending; keep the last durable revisions.</summary>
    internal Task FlushEditingDraftsAsync() => Task.WhenAll(Windows.Cast<Window>().Select(window => window switch
    {
        AnnotationEditorWindow image => image.FlushDraftAsync(),
        VideoEditorWindow video => video.FlushDraftAsync(),
        _ => Task.CompletedTask,
    }));

    internal bool PreserveDraftsForSessionEnding()
    {
        bool wasPreparing = _preparingExit;
        _preparingExit = true;
        var windows = Windows.Cast<Window>().Select(window => (Window: window, Enabled: window.IsEnabled)).ToArray();
        foreach (var (window, _) in windows) window.IsEnabled = false;
        try
        {
            return EditRecoveryPresentation.DrainSessionEnding(Dispatcher, async () =>
            {
                while (Windows.Cast<Window>().OfType<AnnotationEditorWindow>().Any(window => window.Editor.IsEditingOperationInProgress))
                    await Task.Delay(50);
                await FlushEditingDraftsAsync();
                await EditDraftSession.WaitForFinalizationsAsync();
            }, TimeSpan.FromSeconds(3), message => _log?.LogWarning("Session-ending draft flush: {Message}", message));
        }
        finally
        {
            foreach (var (window, enabled) in windows) window.IsEnabled = enabled;
            _preparingExit = wasPreparing;
        }
    }

    private async Task OpenRecoveredImageAsync(EditDraft draft)
    {
        var decoded = await EditRecoveryPresentation.DecodeImageAsync(DraftStore!, draft);
        var bitmap = decoded.Current;
        var original = decoded.Original;
        var document = decoded.Document;
        var assets = decoded.Assets;
        var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        var window = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), region, original,
            initialDocument: document, initialAssets: assets, sourceWindowTitle: draft.Metadata.GetValueOrDefault("title"));
        if (draft.Metadata.TryGetValue(AnnotationEditorWindow.DraftFrameStillImageKey, out string? frameStillImage))
            window.FrameStillImage = System.Text.Json.JsonSerializer.Deserialize<VideoFrameStillImage>(frameStillImage);
        window.Editor.RestoreDraftGeometry(bitmap, int.Parse(draft.Metadata.GetValueOrDefault("turns", "0"),
            System.Globalization.CultureInfo.InvariantCulture), draft.Metadata.GetValueOrDefault("origin", "{\"x\":0,\"y\":0}"));
        if (FindDraftRecord(draft) is { } record && _commit is { } commitService)
        {
            // The draft was editing a library capture that still exists unchanged: save back
            // onto it, exactly like the editor it came from, instead of creating a new capture.
            CaptureEditSession editSession = commitService.BeginEditSession(record);
            window.CommitRequested = result => CommitRecoveredDraftAsync(record, result, editSession);
            window.DraftRecordSource = () => (record.Id, editSession.ExpectedContentRevision);
            window.Closed += (_, _) => editSession.Dispose();
        }
        else
        {
            var commit = CreateRecordedFrameCommitHandler();
            window.CommitRequested = commit.CommitAsync;
            window.Closed += (_, _) => commit.Dispose();
        }
        window.AdoptRecoveredDraft(DraftStore!, draft.SessionId, draft.Revision);
        if (draft.Metadata.TryGetValue("crop", out string? crop)) window.Editor.RestorePendingDraftCrop(crop);
        window.Show();
    }
    /// <summary>
    /// The library image an image draft was editing, when it still exists at the content
    /// revision the draft started from. Otherwise the draft becomes a new capture, so newer
    /// saved content is never overwritten.
    /// </summary>
    internal CaptureRecord? FindDraftRecord(EditDraft draft) =>
        _queue is not null ? FindDraftRecord(_queue, draft) : null;

    internal static CaptureRecord? FindDraftRecord(CaptureQueue queue, EditDraft draft)
    {
        if (!draft.Metadata.TryGetValue(AnnotationEditorWindow.DraftRecordIdKey, out string? idText)
            || !Guid.TryParseExact(idText, "N", out Guid id)
            || !long.TryParse(draft.Metadata.GetValueOrDefault(AnnotationEditorWindow.DraftRecordRevisionKey),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long revision))
            return null;
        return queue.Find(id) is { IsImage: true } record && record.ContentRevision == revision
            && File.Exists(queue.GetFilePath(record, CaptureFileNames.Original)) ? record : null;
    }

    private async Task<bool> CommitRecoveredDraftAsync(CaptureRecord record, AnnotationEditingResult result,
        CaptureEditSession editSession)
    {
        try
        {
            bool close = await _commit!.CommitAsync(record, result, editSession);
            if (close)
            {
                _galleryWindow?.RefreshFromQueue();
                _tray?.SetCaptureCount(_queue?.Count ?? 0);
            }
            return close;
        }
        catch (Exception error)
        {
            _log?.LogError(error, "Could not save the recovered draft to capture {Id}", record.Id);
            _tray?.ShowBalloon(UiText.Get("Text_C1A40373A077"), Themes.ThemedDialog.RecoveryMessage(error),
                TrayBalloonKind.Error);
            return false; // The editor stays open with the recovered edits.
        }
    }

    private async Task OpenRecoveredVideoAsync(EditDraft draft)
    {
        if (_videoLibrary is null || _queue is null || _services is null) throw new IOException("Video library is unavailable.");
        var original = JsonSerializer.Deserialize<RecordingResult>(draft.Metadata["recording"], JsonDefaults.Compact)
            ?? throw new IOException("Invalid recording draft.");
        // The draft references the immutable library original (path + hash); it holds no copy.
        string source = Path.GetFullPath(draft.Metadata["source"]);
        var record = _queue.Records.FirstOrDefault(x => x.IsVideo
                && string.Equals(Path.GetFullPath(_videoLibrary.SourcePath(x)), source, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("The draft's library video is no longer available.");
        string sourceHash = await Task.Run(() => EditDraftStore.HashFile(source));
        if (sourceHash != draft.Metadata["sourceHash"]) throw new IOException("Draft recording changed.");
        var document = JsonSerializer.Deserialize<VideoEditDocument>(draft.Document, JsonDefaults.Compact)
            ?? throw new IOException("Invalid video edit document.");
        var editSession = _videoLibrary.BeginEdit(record);
        var editor = new VideoEditorWindow(original with { OutputPath = source }, _services.GetRequiredService<AppPaths>(),
            _services.GetRequiredService<ILoggerFactory>(), document)
        {
            RenderStagingPathFactory = () => _videoLibrary.CreateRenderStagingPath(record),
            VideoCommitHandler = (doc, stage, token) => _videoLibrary.CommitEditAsync(record, editSession, doc, stage, token),
            FrameImageCommitHandlerFactory = CreateRecordedFrameCommitHandler,
        };
        editor.Closed += (_, _) => editSession.Dispose();
        editor.AdoptRecoveredDraft(DraftStore!, draft.SessionId, draft.Revision);
        editor.Show();
    }
}
