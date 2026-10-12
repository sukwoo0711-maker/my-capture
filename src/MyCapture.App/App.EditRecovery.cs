using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyCapture.App.Editing;
using MyCapture.App.Recording;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Serialization;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

internal enum AppExitReason { Explicit, StoreUpdate }

public partial class App
{
    internal EditDraftStore? DraftStore { get; private set; }
    private bool _preparingExit;
    internal Func<Task<bool>>? PrepareCapturePersistenceForExitAsync { get; set; }
    /// <summary>Call after persistence and queue initialization, before normal editor creation.</summary>
    internal async Task InitializeEditRecoveryAsync()
    {
        if (DraftStore is not null || _services is null) return;
        var trash = new WindowsRecycleBin();
        DraftStore = new EditDraftStore(Path.Combine(_services.GetRequiredService<AppPaths>().DataRoot, "edit-drafts"),
            path => { _ = trash.RecycleDirectory(path); });
        IReadOnlyList<(EditDraft Draft, string Directory)> available;
        try { available = await Task.Run(DraftStore.ReadAvailable); }
        catch (Exception error) { _log?.LogError(error, "Could not enumerate editing drafts"); return; }
        foreach (var (draft, directory) in available)
        {
            var prompt = new EditorCloseDialog(UiText.Get("EditRecovery_Title"), UiText.Get("EditRecovery_Message"),
                UiText.Get("EditRecovery_Restore"), UiText.Get("EditRecovery_Discard"), UiText.Get("Dialog.Cancel"))
            { WindowStartupLocation = WindowStartupLocation.CenterScreen };
            _ = prompt.ShowDialog();
            if (prompt.Choice == EditorCloseChoice.Cancel) continue;
            try
            {
                if (prompt.Choice == EditorCloseChoice.DontSave) { await Task.Run(() => DraftStore.RetireAsync(draft.SessionId)); continue; }
                if (draft.Kind == "image") OpenRecoveredImage(draft, directory);
                else await OpenRecoveredVideoAsync(draft);
            }
            catch (Exception error)
            {
                _log?.LogError(error, "Could not reopen editing draft {SessionId}", draft.SessionId);
                Themes.ThemedDialog.Error(null, UiText.Get("EditRecovery_OpenFailed"), error);
            }
        }
    }

    /// <summary>Store API can install after this gate; the app remains alive to show progress.</summary>
    internal Task<bool> PrepareForStoreUpdateAsync() => PrepareForAppExitAsync(AppExitReason.StoreUpdate);
    internal async Task<bool> RequestSafeShutdownAsync()
    {
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
        if (_galleryWindow is not null) _galleryWindow.IsEnabled = false;
        try
        {
            if (_recorder?.RequiresCaptureExclusion == true
                && Themes.ThemedDialog.Show(UiText.Get("EditRecovery_RecordingMessage"), UiText.Get("EditRecovery_RecordingTitle"),
                    MessageBoxButton.OKCancel, defaultResult: MessageBoxResult.Cancel) != MessageBoxResult.OK) return false;
            if (_recorder is not null && !await _recorder.PrepareForAppExitAsync()) return false;
            if (PrepareCapturePersistenceForExitAsync is { } prepareCapture && !await prepareCapture()) return false;
            foreach (Window window in Windows.Cast<Window>().ToArray())
            {
                if (window is Settings.SettingsWindow settings && !settings.PrepareForAppExit(reason == AppExitReason.StoreUpdate)) return false;
                if (window is AnnotationEditorWindow image && !await image.PrepareForAppExitAsync()) return false;
                if (window is VideoEditorWindow video && !await video.PrepareForAppExitAsync()) return false;
            }
            await EditDraftSession.WaitForFinalizationsAsync();
            return true;
        }
        catch (Exception error) { _log?.LogError(error, "Could not prepare app exit ({Reason})", reason); return false; }
        finally
        {
            if (_galleryWindow is not null && galleryEnabled is { } enabled) _galleryWindow.IsEnabled = enabled;
            _preparingExit = false;
        }
    }
    /// <summary>No user prompts on Windows session ending; keep the last durable revisions.</summary>
    internal Task FlushEditingDraftsAsync() => Task.WhenAll(Windows.Cast<Window>().Select(window => window switch
    {
        AnnotationEditorWindow image => image.FlushDraftAsync(),
        VideoEditorWindow video => video.FlushDraftAsync(),
        _ => Task.CompletedTask,
    }));

    private static BitmapSource LoadDraftBitmap(string path)
    {
        using var stream = File.OpenRead(path);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
    private void OpenRecoveredImage(EditDraft draft, string directory)
    {
        var bitmap = LoadDraftBitmap(Path.Combine(directory, "base.png"));
        var original = LoadDraftBitmap(Path.Combine(directory, "original.png"));
        AnnotationDocument document = AnnotationDocument.TryFromJson(draft.Document) ?? throw new IOException("Invalid draft document.");
        var assets = draft.Metadata.Where(x => x.Key.StartsWith("asset:", StringComparison.Ordinal))
            .ToDictionary(x => x.Key[6..], x => draft.Files.ContainsKey(x.Value)
                ? LoadDraftBitmap(Path.Combine(directory, x.Value)) : throw new IOException("Invalid draft asset reference."));
        var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        var window = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), region, original,
            initialDocument: document, initialAssets: assets, sourceWindowTitle: draft.Metadata.GetValueOrDefault("title"));
        window.Editor.RestoreDraftGeometry(bitmap, int.Parse(draft.Metadata.GetValueOrDefault("turns", "0"),
            System.Globalization.CultureInfo.InvariantCulture), draft.Metadata.GetValueOrDefault("origin", "{\"x\":0,\"y\":0}"));
        var commit = CreateRecordedFrameCommitHandler();
        window.CommitRequested = commit.CommitAsync;
        window.Closed += (_, _) => commit.Dispose();
        window.AdoptRecoveredDraft(DraftStore!, draft.SessionId, draft.Revision);
        if (draft.Metadata.TryGetValue("crop", out string? crop)) window.Editor.RestorePendingDraftCrop(crop);
        window.Show();
    }
    private async Task OpenRecoveredVideoAsync(EditDraft draft)
    {
        if (_videoLibrary is null || _queue is null || _services is null) throw new IOException("Video library is unavailable.");
        var original = JsonSerializer.Deserialize<RecordingResult>(draft.Metadata["recording"], JsonDefaults.Compact)
            ?? throw new IOException("Invalid recording draft.");
        string source = Path.GetFullPath(draft.Metadata["source"]);
        string expected = Path.Combine(_services.GetRequiredService<AppPaths>().DataRoot, "edit-drafts", draft.SessionId.ToString("N"), "original-video.mp4");
        if (!string.Equals(source, expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid draft source location.");
        string sourceHash = await Task.Run(() =>
        {
            using var stream = File.OpenRead(source);
            return Convert.ToHexString(SHA256.HashData(stream));
        });
        if (sourceHash != draft.Metadata["sourceHash"]) throw new IOException("Draft recording changed.");
        var document = JsonSerializer.Deserialize<VideoEditDocument>(draft.Document, JsonDefaults.Compact)
            ?? throw new IOException("Invalid video edit document.");
        var record = _queue.Records.FirstOrDefault(x => x.IsVideo && string.Equals(_videoLibrary.SourcePath(x), original.OutputPath, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            using var capture = _videoLibrary.BeginCapture(original.Fps);
            await Task.Run(() => File.Copy(source, capture.StagingOutputPath, overwrite: false));
            var item = await _videoLibrary.CompleteCaptureAsync(capture, original with { OutputPath = capture.StagingOutputPath });
            record = item.Record;
        }
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
