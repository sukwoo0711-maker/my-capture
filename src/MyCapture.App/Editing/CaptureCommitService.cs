using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MyCapture.App.Threading;
using MyCapture.Core.Annotations;
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
/// This is the single place the four commit actions converge. All of them flatten the
/// annotations onto the original at 1:1 physical pixels and finalise the capture in the
/// queue; they differ only in the extra step (clipboard, quick save, save dialog) and in
/// whether a failure keeps the editor open.
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
    private readonly Func<AppPaths> _paths;
    private readonly ILogger<CaptureCommitService> _log;
    private readonly Func<BitmapSource, Task<bool>> _copyImageAsync;

    internal CaptureCommitService(
        CapturePersistenceService persistence,
        Func<AppSettings> settings,
        Func<AppPaths> paths,
        ILogger<CaptureCommitService> log,
        Func<BitmapSource, Task<bool>>? copyImageAsync = null)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _copyImageAsync = copyImageAsync ?? ClipboardImageService.CopyImageAsync;
    }

    /// <summary>
    /// Optional hook for showing a Save As dialog, returning the chosen path or
    /// <see langword="null"/> if cancelled. Injectable so the action can be unit-tested
    /// without a real dialog.
    /// </summary>
    internal Func<string, string?>? SaveAsPrompt { get; set; }

    /// <summary>
    /// Returns whether the record is inside the staged finalisation window. Gallery callers use
    /// this to avoid reading, deleting, or re-editing an older generation while its replacement
    /// is still being encoded and committed.
    /// </summary>
    internal bool IsRecordBusy(Guid recordId) => _persistence.IsBusy(recordId);

    internal CaptureEditSession BeginEditSession(CaptureRecord record) =>
        new(record, _persistence.AcquireEditLease(record.Id));

    /// <summary>
    /// Copies the untouched pixels produced by the explicit free-region capture command.
    /// This path is deliberately independent of quick-save preferences and editor actions:
    /// once the user releases the capture drag, the image is offered to the shared exact-PNG
    /// clipboard writer even if the editor is later cancelled or committed with Done.
    /// </summary>
    internal async Task<bool> CopyCapturedRegionAsync(BitmapSource capturedBitmap)
    {
        ArgumentNullException.ThrowIfNull(capturedBitmap);

        try
        {
            bool copied = await _copyImageAsync(capturedBitmap);
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

    /// <summary>
    /// Runs the commit for <paramref name="record"/> against <paramref name="result"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the editor should close. A cancelled/failed Save As or a
    /// failed explicit clipboard copy returns <see langword="false"/> so the user can retry.
    /// </returns>
    internal async Task<bool> CommitAsync(
        CaptureRecord? record,
        AnnotationEditingResult result,
        CaptureEditSession? editSession = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (record is not null && editSession is not null && editSession.RecordId != record.Id)
        {
            throw new ArgumentException("The edit session belongs to a different capture.", nameof(editSession));
        }
        editSession?.ThrowIfDisposed();

        AnnotationDocument snapshot = CreatePersistenceSnapshot(result.Document);
        BitmapSource flattened = await StaThreadTask.RunAsync(
            () => Flatten(result.SelectedBitmap, snapshot, result.ImageAssetBitmaps),
            "MyCapture annotation renderer");

        switch (result.Action)
        {
            case EditorCommitAction.SaveAs:
                // Choose the destination before persisting so a cancel keeps the editor open
                // with no side effects the user did not ask for.
                bool exported = await ImageExportTransaction.RunAsync(async () =>
                {
                    string? chosen = ResolveSaveAsPath();
                    if (chosen is null)
                    {
                        _log.LogInformation("Save As cancelled; keeping editor open");
                        return false;
                    }

                    if (!HasPngExtension(chosen))
                    {
                        _log.LogWarning("Rejected Save As path with a non-PNG extension: {Path}", chosen);
                        return false;
                    }

                    return await TrySavePngAsync(flattened, chosen);
                });
                if (!exported)
                {
                    return false;
                }

                await PersistFinalIfAvailableAsync(
                    record, result.Action, flattened, snapshot, result.ImageAssetBitmaps, editSession);
                return await CopyEditedImageAsync(flattened, "Save As");

            case EditorCommitAction.QuickSave:
                await PersistFinalIfAvailableAsync(
                    record, result.Action, flattened, snapshot, result.ImageAssetBitmaps, editSession);
                bool quickSaved = await ImageExportTransaction.RunAsync(() => QuickSaveAsync(flattened));
                if (!quickSaved)
                {
                    return false;
                }

                bool quickSaveCopied = await CopyEditedImageAsync(flattened, "Quick save");

                // Queue persistence is already durable, but Quick Save is an explicit export
                // request. Keep the editor open when that request fails so the user sees the
                // failure and can choose another destination instead of losing the retry path.
                return quickSaveCopied;

            case EditorCommitAction.CopyToClipboard:
                await PersistFinalIfAvailableAsync(
                    record, result.Action, flattened, snapshot, result.ImageAssetBitmaps, editSession);
                bool clipboardCopied = await _copyImageAsync(flattened);
                if (!clipboardCopied)
                {
                    _log.LogWarning(
                        record is null
                            ? "Recovery clipboard copy failed; keeping the editor open"
                            : "Capture persisted, but explicit clipboard copy failed");
                }

                return clipboardCopied;

            case EditorCommitAction.Done:
            default:
                if (record is null)
                {
                    _log.LogWarning("Cannot finish editing without a queue record; waiting for an explicit export or copy");
                    return false;
                }

                await PersistFinalIfAvailableAsync(
                    record, result.Action, flattened, snapshot, result.ImageAssetBitmaps, editSession);
                return await CopyEditedImageAsync(flattened, "Done");
        }
    }

    private async Task<bool> CopyEditedImageAsync(BitmapSource flattened, string operation)
    {
        bool copied = await _copyImageAsync(flattened);
        if (!copied)
        {
            _log.LogWarning(
                "{Operation} persisted the edited image, but the clipboard copy failed; keeping the editor open",
                operation);
        }

        return copied;
    }

    private static BitmapSource Flatten(
        BitmapSource selectedBitmap,
        AnnotationDocument document,
        IReadOnlyDictionary<string, BitmapSource> imageAssetBitmaps)
    {
        AnnotationImageStore store = AnnotationImageStore.FromDecoded(imageAssetBitmaps);
        var renderer = new AnnotationRenderer(store);
        return AnnotationFlattener.Flatten(selectedBitmap, document, renderer);
    }

    private async Task PersistFinalIfAvailableAsync(
        CaptureRecord? record,
        EditorCommitAction action,
        BitmapSource flattened,
        AnnotationDocument snapshot,
        IReadOnlyDictionary<string, BitmapSource> imageAssetBitmaps,
        CaptureEditSession? editSession)
    {
        if (record is null)
        {
            _log.LogWarning("Queue persistence is unavailable; running {Action} in recovery-export mode", action);
            return;
        }

        await _persistence.FinalizeAsync(
            record,
            flattened,
            snapshot,
            imageAssetBitmaps,
            editSession?.ExpectedContentRevision);
        editSession?.AdvanceTo(record.ContentRevision);
    }

    private async Task<bool> QuickSaveAsync(BitmapSource flattened)
    {
        string directory = ResolveQuickSaveDirectory();
        try
        {
            string pattern = _settings().Export.FileNamePattern;
            (string path, long bytes) = await Task.Run(() =>
            {
                Directory.CreateDirectory(directory);
                string stem = QuickSaveNaming.BuildStem(pattern, DateTimeOffset.Now);
                byte[] encoded = ImageCodec.EncodePng(flattened);
                string savedPath = QuickSaveNaming.WriteCollisionFreeExport(directory, stem, ".png", encoded);
                return (savedPath, encoded.LongLength);
            });
            _log.LogInformation("Quick-saved {Bytes} bytes to {Path}", bytes, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            // Quick save failure must not fail the persist that already succeeded.
            _log.LogWarning(ex, "Quick save to {Directory} failed", directory);
            return false;
        }
    }

    private async Task<bool> TrySavePngAsync(BitmapSource flattened, string path)
    {
        try
        {
            long bytes = await Task.Run(() => ImageCodec.SavePngExport(flattened, path));
            _log.LogInformation("Saved {Bytes} bytes to {Path}", bytes, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Save to {Path} failed; keeping editor open", path);
            return false;
        }
    }

    private static AnnotationDocument CreatePersistenceSnapshot(AnnotationDocument liveDocument)
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
        string overridePath = _settings().Export.QuickSaveDirectoryOverride;
        return string.IsNullOrWhiteSpace(overridePath) ? _paths().QuickSaveRoot : overridePath;
    }

    private string? ResolveSaveAsPath()
    {
        string directory = ResolveQuickSaveDirectory();
        string stem = QuickSaveNaming.BuildStem(_settings().Export.FileNamePattern, DateTimeOffset.Now);
        string suggested = QuickSaveNaming.ResolvePath(directory, stem, ".png");

        if (SaveAsPrompt is not null)
        {
            return SaveAsPrompt(suggested);
        }

        var dialog = new SaveFileDialog
        {
            Title = "다른 이름으로 저장",
            Filter = "PNG 이미지|*.png",
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
            _ = System.Windows.MessageBox.Show(
                "PNG 파일 이름(.png)을 선택해 주세요.",
                "다른 이름으로 저장",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
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
