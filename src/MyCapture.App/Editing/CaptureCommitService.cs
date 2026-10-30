using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MyCapture.App.Threading;
using MyCapture.Core.Annotations;
using MyCapture.Core.Capture;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Editing;

/// <summary>
/// Executes an editor commit: flatten, persist into the queue, and perform whatever
/// clipboard/export the chosen <see cref="EditorCommitAction"/> requires.
/// </summary>
/// <remarks>
/// <para>
/// This is the single place the editor commit actions converge. All of them flatten the
/// annotations onto the original at 1:1 physical pixels. Done, Pin, Quick Save and Save As also
/// finalise the capture in the queue (the library) and copy the result, then close the
/// editor; Copy only puts the pixels on the clipboard and leaves the editor open. See
/// <see cref="EditorCommitAction"/> for each action's exact steps.
/// </para>
/// <para>
/// The WPF flatten and optional file dialog run on the UI thread. Clipboard PNG encoding and
/// transient retry waits are handed to <see cref="ClipboardImageService"/> asynchronously so
/// another process holding the clipboard cannot stall the editor transition.
/// </para>
/// </remarks>
internal sealed class CaptureCommitService
{
    private readonly CapturePersistenceService _persistence;
    private readonly Func<AppSettings> _settings;
    private readonly ILogger<CaptureCommitService> _log;
    private readonly Func<BitmapSource, string?, CaptureProvenance?, Task<CaptureProvenance>?, Task<bool>> _copyImageAsync;

    internal CaptureCommitService(
        CapturePersistenceService persistence,
        Func<AppSettings> settings,
        Func<AppPaths> paths,
        ILogger<CaptureCommitService> log,
        Func<BitmapSource, string?, Task<bool>>? copyImageAsync = null)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(paths);
        _log = log ?? throw new ArgumentNullException(nameof(log));
        ExportLocation = new ImageExportLocation(settings, paths, log, () => PersistExportSettings?.Invoke());
        _copyImageAsync = copyImageAsync is null
            ? (image, url, provenance, pending) => ClipboardImageService.CopyImageWithProvenanceAsync(image, url, provenance, pendingProvenance: pending)
            : (image, url, _, _) => copyImageAsync(image, url);
    }

    /// <summary>
    /// Optional hook for showing a Save As dialog, returning the chosen path or
    /// <see langword="null"/> if cancelled. Injectable so the action can be unit-tested
    /// without a real dialog.
    /// </summary>
    internal Func<string, string?>? SaveAsPrompt { get; set; }

    internal Func<ImageExportOptions, ImageExportOptions?>? ExportOptionsPrompt { get; set; }

    internal Func<BitmapSource, string, bool>? ReducedExportPrompt { get; set; }

    /// <summary>
    /// Raised with the written file's path when an editor's Quick Save has written it, before the
    /// clipboard copy that completes the commit. Runs on the caller's context.
    /// </summary>
    internal event Action<string>? QuickSaved;

    /// <summary>
    /// A finishing edit succeeded, including its library save and required export/copy.
    /// Both editor hosts use this service; recovery exports without a library record do not announce.
    /// </summary>
    internal event Action<CaptureRecord>? FinishingCommitCompleted;

    internal static bool IsFinishingCommit(EditorCommitAction action) =>
        action is EditorCommitAction.Done or EditorCommitAction.Pin or EditorCommitAction.QuickSave or EditorCommitAction.SaveAs;

    internal Action? PersistExportSettings { get; set; }
    internal Action<BitmapSource, CaptureProvenance>? PinImage { get; set; }
    internal Action? PinFailed { get; set; }
    internal ImageExportLocation ExportLocation { get; }
    internal ImageExportOptions LastExportOptions => _settings().Export.LastImageExport;
    internal void RememberSavedExport(string path, ImageExportOptions options)
    {
        // Publish the folder and options in one ordered settings snapshot, after the file exists.
        _settings().Export.LastImageExport = options;
        ExportLocation.RememberSavedPath(path, persistEvenIfUnchanged: true);
    }

    internal void RememberExportOptions(ImageExportOptions options)
    {
        _settings().Export.LastImageExport = options;
        try { PersistExportSettings?.Invoke(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidOperationException or System.Text.Json.JsonException
                                   or System.Security.Cryptography.CryptographicException)
        { _log.LogWarning(ex, "Could not persist the last export options"); }
    }

    /// <summary>
    /// Returns whether the record is inside the staged finalisation window. Gallery callers use
    /// this to avoid reading, deleting, or re-editing an older generation while its replacement
    /// is still being encoded and committed.
    /// </summary>
    internal bool IsRecordBusy(Guid recordId) => _persistence.IsBusy(recordId);

    internal bool IsRecordBeingWritten(Guid recordId) => _persistence.IsWriteInProgress(recordId);

    internal CaptureEditSession BeginEditSession(CaptureRecord record) =>
        new(record, _persistence.AcquireEditLease(record.Id));

    /// <summary>
    /// Copies every completed capture through the shared exact-PNG clipboard writer,
    /// adding the source title when enabled. App starts this once alongside library
    /// persistence in every after-capture mode, independently of quick-save preferences
    /// and later editor actions.
    /// </summary>
    internal async Task<bool> CopyCapturedRegionAsync(BitmapSource capturedBitmap, string? sourceWindowTitle = null,
        bool? showSourceWindowTitle = null, string? sourcePageUrl = null, CaptureProvenance? provenance = null,
        Task<CaptureProvenance>? pendingProvenance = null)
    {
        ArgumentNullException.ThrowIfNull(capturedBitmap);

        try
        {
            BitmapSource result = await RenderCapturedImageAsync(capturedBitmap, sourceWindowTitle, showSourceWindowTitle);
            bool copied = await _copyImageAsync(result, sourcePageUrl, provenance, pendingProvenance);
            if (!copied)
            {
                _log.LogWarning("Automatic captured-region clipboard copy failed");
            }

            return copied;
        }
        catch (Exception ex) when (ex is IOException
                                   or COMException
                                   or ExternalException
                                   or InvalidOperationException
                                   or NotSupportedException
                                   or ArgumentException)
        {
            // Persistence and clipboard integration are deliberately separate at the caller.
            // A clipboard fault must never turn a successful capture into a failed or missing one.
            _log.LogWarning(ex, "Automatic captured-region clipboard copy threw");
            return false;
        }
    }

    internal Task<BitmapSource> RenderCapturedImageAsync(BitmapSource original, string? sourceWindowTitle,
        bool? showSourceWindowTitle = null)
    {
        if (!(showSourceWindowTitle ?? _settings().Export.ShowSourceWindowTitle) || string.IsNullOrWhiteSpace(sourceWindowTitle))
            return Task.FromResult(original);

        return StaThreadTask.RunAsync(() => Flatten(original,
            AnnotationDocument.CreateFor(original.PixelWidth, original.PixelHeight),
            new Dictionary<string, BitmapSource>(), sourceWindowTitle), "MyCapture capture title renderer");
    }

    /// <summary>
    /// Runs the commit for <paramref name="record"/> against <paramref name="result"/>.
    /// </summary>
    /// <returns>
    /// Whether the action succeeded. For Done, Quick Save and Save As that means the editor
    /// should close; a cancelled/failed save or a failed clipboard copy returns
    /// <see langword="false"/> so the user can retry. For Copy it only reports whether the
    /// clipboard took the image; the editor stays open either way.
    /// </returns>
    internal async Task<bool> CommitAsync(
        CaptureRecord? record,
        AnnotationEditingResult result,
        CaptureEditSession? editSession = null,
        Func<Task<(CaptureRecord Record, CaptureEditSession? Session)>>? createRecordAsync = null,
        CaptureRecord? namingRecord = null,
        bool replaceOriginal = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (record is not null && editSession is not null && editSession.RecordId != record.Id)
        {
            throw new ArgumentException("The edit session belongs to a different capture.", nameof(editSession));
        }
        editSession?.ThrowIfDisposed();
        result.SaveCompleted = false;
        result.ClipboardCopyFailed = false;

        // A failed first publication still has valid capture provenance. Use it only for
        // export naming; it must not bypass the create/persist callback below.
        CaptureRecord? exportRecord = record ?? namingRecord;

        AnnotationDocument snapshot = CreatePersistenceSnapshot(result.Document);
        // An open editor keeps the option it previews; re-opening a library capture reads
        // the current preference again. Always draw over original pixels, never a prior result.
        string? sourceWindowTitle = (result.ShowSourceWindowTitle ?? _settings().Export.ShowSourceWindowTitle)
            ? result.SourceWindowTitle ?? exportRecord?.SourceWindowTitle
            : null;
        BitmapSource flattened = await StaThreadTask.RunAsync(
            () => Flatten(result.SelectedBitmap, snapshot, result.ImageAssetBitmaps, sourceWindowTitle),
            "MyCapture annotation renderer");

        switch (result.Action)
        {
            case EditorCommitAction.Pin:
                if (record is null && createRecordAsync is null) return false;
                record = await PersistFinalIfAvailableAsync(
                    record, result.Action, flattened, snapshot, result.ImageAssetBitmaps, editSession, createRecordAsync,
                    replaceOriginal ? result.SelectedBitmap : null);
                result.SaveCompleted = record is not null;
                if (record is null || !await CopyEditedImageAsync(flattened, "Pin", result, record)) return false;
                try
                {
                    if (PinImage is null) throw new InvalidOperationException("Pinning is unavailable.");
                    PinImage(flattened, CaptureProvenance.FromRecord(record));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log.LogWarning(ex, "Could not pin the saved edit");
                    try { PinFailed?.Invoke(); }
                    catch (Exception noticeError) when (noticeError is not OutOfMemoryException)
                    { _log.LogWarning(noticeError, "Could not report the pin failure"); }
                }
                // The edit is saved and copied whether or not the pin opened.
                return CompleteCommit(record, result.Action, true);

            case EditorCommitAction.SaveAs:
                // Choose the destination before persisting so a cancel keeps the editor open
                // with no side effects the user did not ask for.
                bool exported = result.ReduceExport
                    ? ShowReducedExport(flattened, exportRecord)
                    : await ImageExportTransaction.RunAsync(async () =>
                {
                    var destination = ResolveSaveAsDestination(exportRecord);
                    if (destination is null)
                    {
                        _log.LogInformation("Save As cancelled; keeping editor open");
                        return false;
                    }

                    (string chosen, ImageExportOptions options) = destination.Value;
                    if (!HasPngExtension(chosen))
                    {
                        _log.LogWarning("Rejected Save As path with a non-PNG extension: {Path}", chosen);
                        return false;
                    }

                    if (!await TrySavePngAsync(flattened, chosen, options, exportRecord?.SourcePageUrl)) return false;
                    RememberSavedExport(chosen, options);
                    return true;
                });
                if (!exported)
                {
                    return false;
                }
                result.SaveCompleted = true;

                record = await PersistFinalIfAvailableAsync(
                    record, result.Action, flattened, snapshot, result.ImageAssetBitmaps, editSession, createRecordAsync,
                    replaceOriginal ? result.SelectedBitmap : null);
                return CompleteCommit(record, result.Action, await CopyEditedImageAsync(flattened, "Save As", result, record ?? exportRecord));

            case EditorCommitAction.QuickSave:
                record = await PersistFinalIfAvailableAsync(
                    record, result.Action, flattened, snapshot, result.ImageAssetBitmaps, editSession, createRecordAsync,
                    replaceOriginal ? result.SelectedBitmap : null);
                result.SaveCompleted = record is not null;
                string? quickSavedPath = await ImageExportTransaction.RunAsync(() => QuickSaveAsync(flattened, record ?? exportRecord));
                if (quickSavedPath is null)
                {
                    return false;
                }

                // The file exists whatever the clipboard does next, so it is announced now.
                result.SaveCompleted = true;
                AnnounceQuickSave(quickSavedPath);
                bool quickSaveCopied = await CopyEditedImageAsync(flattened, "Quick save", result, record ?? exportRecord);

                // Queue persistence is already durable, but Quick Save is an explicit export
                // request. Keep the editor open when that request fails so the user sees the
                // failure and can choose another destination instead of losing the retry path.
                return CompleteCommit(record, result.Action, quickSaveCopied);

            case EditorCommitAction.CopyToClipboard:
                // Copy is the editor's secondary action: the edited pixels go to the clipboard
                // and nothing else changes. The library keeps its current version until the
                // user finishes with Done (or saves a file), and the editor stays open.
                result.ClipboardCopyFailed = true;
                bool clipboardCopied = await _copyImageAsync(flattened, exportRecord?.SourcePageUrl, CaptureProvenance.FromRecord(exportRecord), null);
                result.ClipboardCopyFailed = !clipboardCopied;
                if (!clipboardCopied)
                {
                    _log.LogWarning("Explicit clipboard copy of the edited image failed");
                }

                return clipboardCopied;

            case EditorCommitAction.Done:
            default:
                if (record is null && createRecordAsync is null)
                {
                    _log.LogWarning("Cannot finish editing without a queue record; waiting for an explicit export or copy");
                    return false;
                }

                // The library save and the clipboard copy each encode their own PNG of the same
                // flattened pixels, so once the save has passed its checks (the capture is still at
                // this editor's generation) the copy runs beside its writing. A save those checks
                // refuse ends before any copy, as when the copy followed it. Once the clipboard holds
                // the result the editor may leave the screen (CopiedToClipboard); the commit still
                // completes only when both have, with the save's failure first.
                var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<CaptureRecord?> persisting = PersistFinalIfAvailableAsync(
                    record, result.Action, flattened, snapshot, result.ImageAssetBitmaps, editSession, createRecordAsync,
                    replaceOriginal ? result.SelectedBitmap : null, () => admitted.TrySetResult());
                if (await Task.WhenAny(admitted.Task, persisting) != admitted.Task)
                {
                    // Refused (rethrown here, without a copy) or no record to save into.
                    record = await persisting;
                    result.SaveCompleted = record is not null;
                    return CompleteCommit(record, result.Action, await CopyEditedImageAsync(flattened, "Done", result, record ?? exportRecord));
                }

                Task<bool> copying = CopyEditedImageAsync(flattened, "Done", result, exportRecord);
                try
                {
                    if (await copying && !persisting.IsFaulted && !persisting.IsCanceled) result.CopiedToClipboard?.Invoke();
                }
                finally
                {
                    record = await persisting;
                    result.SaveCompleted = record is not null;
                }

                return CompleteCommit(record, result.Action, await copying);
        }
    }

    private bool CompleteCommit(CaptureRecord? savedRecord, EditorCommitAction action, bool succeeded)
    {
        if (succeeded && savedRecord is not null && IsFinishingCommit(action))
        {
            try { FinishingCommitCompleted?.Invoke(savedRecord); }
            catch (Exception ex)
            {
                // A courtesy receipt cannot turn a completed save into a failed commit.
                _log.LogWarning(ex, "Could not announce a completed library edit");
            }
        }
        return succeeded;
    }

    private async Task<bool> CopyEditedImageAsync(BitmapSource flattened, string operation, AnnotationEditingResult result, CaptureRecord? source = null)
    {
        // Keep the completed save visible even if the clipboard delegate throws and the
        // host catches the exception to leave the editor open.
        result.ClipboardCopyFailed = true;
        bool copied = await _copyImageAsync(flattened, source?.SourcePageUrl, CaptureProvenance.FromRecord(source), null);
        result.ClipboardCopyFailed = !copied;
        if (!copied)
        {
            _log.LogWarning(
                "{Operation} persisted the edited image, but the clipboard copy failed; keeping the editor open",
                operation);
        }

        return copied;
    }

    private bool ShowReducedExport(BitmapSource flattened, CaptureRecord? record)
    {
        string directory = ExportLocation.SaveAsDirectory;
        string stem = BuildExportStem(record);
        string suggested = QuickSaveNaming.ResolvePath(directory, stem, ".png");
        return ReducedExportPrompt is not null
            ? ReducedExportPrompt(flattened, suggested)
            : ImageReductionExportDialog.Show(System.Windows.Application.Current?.Windows
                .OfType<System.Windows.Window>().FirstOrDefault(window => window.IsActive), flattened, suggested,
                record?.SourcePageUrl, LastExportOptions, RememberExportOptions, RememberSavedExport);
    }

    internal static BitmapSource Flatten(
        BitmapSource selectedBitmap,
        AnnotationDocument document,
        IReadOnlyDictionary<string, BitmapSource> imageAssetBitmaps,
        string? sourceWindowTitle = null)
    {
        AnnotationImageStore store = AnnotationImageStore.FromDecoded(imageAssetBitmaps);
        var renderer = new AnnotationRenderer(store);
        return AnnotationFlattener.Flatten(selectedBitmap, document, renderer, sourceWindowTitle);
    }

    private async Task<CaptureRecord?> PersistFinalIfAvailableAsync(
        CaptureRecord? record,
        EditorCommitAction action,
        BitmapSource flattened,
        AnnotationDocument snapshot,
        IReadOnlyDictionary<string, BitmapSource> imageAssetBitmaps,
        CaptureEditSession? editSession,
        Func<Task<(CaptureRecord Record, CaptureEditSession? Session)>>? createRecordAsync,
        BitmapSource? replacementOriginal,
        Action? admitted = null)
    {
        if (record is null && createRecordAsync is not null) (record, editSession) = await createRecordAsync();
        if (record is null)
        {
            _log.LogWarning("Queue persistence is unavailable; running {Action} in recovery-export mode", action);
            return null;
        }

        await _persistence.FinalizeAsync(
            record,
            flattened,
            snapshot,
            imageAssetBitmaps,
            editSession?.ExpectedContentRevision,
            replacementOriginal,
            admitted);
        editSession?.AdvanceTo(record.ContentRevision);
        return record;
    }

    private void AnnounceQuickSave(string path)
    {
        try
        {
            QuickSaved?.Invoke(path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            // The notice is a courtesy; the saved file and the rest of the commit stand.
            _log.LogWarning(ex, "Could not announce a quick save");
        }
    }

    /// <returns>The written file, or <see langword="null"/> when the quick save failed.</returns>
    private async Task<string?> QuickSaveAsync(BitmapSource flattened, CaptureRecord? record)
    {
        string directory = ResolveQuickSaveDirectory();
        try
        {
            string pattern = _settings().Export.FileNamePattern;
            DateTimeOffset timestamp = record?.CreatedAt ?? DateTimeOffset.Now;
            string? title = record?.SourceWindowTitle;
            (string path, long bytes) = await Task.Run(() =>
            {
                Directory.CreateDirectory(directory);
                string stem = QuickSaveNaming.BuildStem(pattern, timestamp, title);
                byte[] encoded = ImageCodec.EncodePng(flattened, record?.SourcePageUrl);
                string savedPath = QuickSaveNaming.WriteCollisionFreeExport(directory, stem, ".png", encoded);
                return (savedPath, encoded.LongLength);
            });
            _log.LogInformation("Quick-saved {Bytes} bytes to {Path}", bytes, path);
            return path;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            // Quick save failure must not fail the persist that already succeeded.
            _log.LogWarning(ex, "Quick save to {Directory} failed", directory);
            return null;
        }
    }

    private async Task<bool> TrySavePngAsync(BitmapSource flattened, string path, ImageExportOptions options, string? sourcePageUrl = null)
    {
        try
        {
            long bytes = await Task.Run(() =>
            {
                byte[] bytes = ImageExportEncoder.EncodePngCopy(flattened, options, sourcePageUrl);
                AtomicFile.WriteExportBytes(path, bytes);
                return bytes.LongLength;
            });
            _log.LogInformation("Saved {Bytes} bytes to {Path}", bytes, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Save to {Path} failed; keeping editor open", path);
            return false;
        }
    }

    internal static AnnotationDocument CreatePersistenceSnapshot(AnnotationDocument liveDocument)
    {
        AnnotationDocument snapshot = liveDocument.Clone();
        for (int index = 0; index < snapshot.Items.Count; index++)
        {
            snapshot.Items[index].Id = liveDocument.Items[index].Id;
        }

        return snapshot;
    }

    private string ResolveQuickSaveDirectory()
    {
        return ExportLocation.QuickSaveDirectory;
    }

    private string BuildExportStem(CaptureRecord? record) => QuickSaveNaming.BuildStem(
        _settings().Export.FileNamePattern, record?.CreatedAt ?? DateTimeOffset.Now, record?.SourceWindowTitle);

    private (string Path, ImageExportOptions Options)? ResolveSaveAsDestination(CaptureRecord? record)
    {
        string directory = ExportLocation.SaveAsDirectory;
        string stem = BuildExportStem(record);
        string suggested = QuickSaveNaming.ResolvePath(directory, stem, ".png");

        ImageExportOptions? options = ExportOptionsPrompt is not null
            ? ExportOptionsPrompt(LastExportOptions)
            : SaveAsPrompt is not null ? LastExportOptions : ImageExportOptionsPanel.ShowCopyOptions(
                System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().FirstOrDefault(window => window.IsActive), LastExportOptions);
        if (options is null) return null;

        if (SaveAsPrompt is not null)
        {
            string? chosen = SaveAsPrompt(suggested);
            return chosen is null ? null : (chosen, options);
        }

        var dialog = new SaveFileDialog
        {
            Title = UiText.Get("Text_57950AFF46BC"),
            Filter = UiText.Get("Text_4DCACB688866"),
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
            ValidateNames = true,
            InitialDirectory = Directory.Exists(directory) ? directory : null,
            FileName = Path.GetFileName(suggested),
        };

        dialog.FileOk += (_, e) =>
        {
            if (HasPngExtension(dialog.FileName))
            {
                return;
            }

            e.Cancel = true;
            _ = MyCapture.App.Themes.ThemedDialog.Show(
                UiText.Get("Text_932DB1A4ED7F"),
                UiText.Get("Text_57950AFF46BC"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        };

        if (dialog.ShowDialog() != true) return null;
        return (dialog.FileName, options);
    }

    private static bool HasPngExtension(string path)
    {
        try
        {
            return string.Equals(
                Path.GetExtension(path),
                ".png",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// Optimistic generation token owned by one editor window. It advances only after that window's
/// own successful persistence, allowing a clipboard retry while rejecting another editor's
/// intervening update.
/// </summary>
internal sealed class CaptureEditSession : IDisposable
{
    private IDisposable? _evictionLease;

    internal CaptureEditSession(CaptureRecord record, IDisposable evictionLease)
    {
        ArgumentNullException.ThrowIfNull(record);
        _evictionLease = evictionLease ?? throw new ArgumentNullException(nameof(evictionLease));
        RecordId = record.Id;
        ExpectedContentRevision = record.ContentRevision;
    }

    internal Guid RecordId { get; }

    internal long ExpectedContentRevision { get; private set; }

    internal void AdvanceTo(long contentRevision)
    {
        ThrowIfDisposed();
        ExpectedContentRevision = contentRevision;
    }

    internal void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_evictionLease is null, this);
    }

    public void Dispose() => Interlocked.Exchange(ref _evictionLease, null)?.Dispose();
}
