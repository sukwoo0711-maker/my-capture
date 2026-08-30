using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
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
/// Runs on the UI thread, synchronously, because it renders WPF visuals and shows a file
/// dialog. Every step is bounded — one flatten, one set of file writes, one clipboard
/// attempt with a short bounded retry — so it never stalls the capture path.
/// </para>
/// </remarks>
internal sealed class CaptureCommitService
{
    private readonly CapturePersistenceService _persistence;
    private readonly Func<AppSettings> _settings;
    private readonly Func<AppPaths> _paths;
    private readonly ILogger<CaptureCommitService> _log;

    internal CaptureCommitService(
        CapturePersistenceService persistence,
        Func<AppSettings> settings,
        Func<AppPaths> paths,
        ILogger<CaptureCommitService> log)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Optional hook for showing a Save As dialog, returning the chosen path or
    /// <see langword="null"/> if cancelled. Injectable so the action can be unit-tested
    /// without a real dialog.
    /// </summary>
    internal Func<string, string?>? SaveAsPrompt { get; set; }

    /// <summary>
    /// Runs the commit for <paramref name="record"/> against <paramref name="result"/>.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the editor should close. Only a cancelled or failed Save
    /// As returns <see langword="false"/>; every other action always persists and closes.
    /// </returns>
    internal bool Commit(CaptureRecord record, AnnotationEditingResult result)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(result);

        BitmapSource flattened = Flatten(result);

        switch (result.Action)
        {
            case EditorCommitAction.SaveAs:
                // Choose the destination before persisting so a cancel keeps the editor open
                // with no side effects the user did not ask for.
                string? chosen = ResolveSaveAsPath();
                if (chosen is null)
                {
                    _log.LogInformation("Save As cancelled; keeping editor open");
                    return false;
                }

                if (!TrySavePng(flattened, chosen))
                {
                    return false;
                }

                PersistFinal(record, result, flattened);
                return true;

            case EditorCommitAction.QuickSave:
                PersistFinal(record, result, flattened);
                QuickSave(flattened);
                if (_settings().Export.CopyToClipboardOnQuickSave)
                {
                    _ = ClipboardImageService.CopyImage(flattened);
                }

                return true;

            case EditorCommitAction.CopyToClipboard:
                PersistFinal(record, result, flattened);
                _ = ClipboardImageService.CopyImage(flattened);
                return true;

            case EditorCommitAction.Done:
            default:
                PersistFinal(record, result, flattened);
                return true;
        }
    }

    private static BitmapSource Flatten(AnnotationEditingResult result)
    {
        AnnotationImageStore store = AnnotationImageStore.FromDecoded(result.ImageAssetBitmaps);
        var renderer = new AnnotationRenderer(store);
        return AnnotationFlattener.Flatten(result.SelectedBitmap, result.Document, renderer);
    }

    private void PersistFinal(CaptureRecord record, AnnotationEditingResult result, BitmapSource flattened) =>
        _persistence.Finalize(record, flattened, result.Document, result.ImageAssetBitmaps);

    private void QuickSave(BitmapSource flattened)
    {
        string directory = ResolveQuickSaveDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            string stem = QuickSaveNaming.BuildStem(_settings().Export.FileNamePattern, DateTimeOffset.Now);
            string path = QuickSaveNaming.ResolvePath(directory, stem, ".png");
            long bytes = ImageCodec.SavePng(flattened, path);
            _log.LogInformation("Quick-saved {Bytes} bytes to {Path}", bytes, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Quick save failure must not fail the persist that already succeeded.
            _log.LogWarning(ex, "Quick save to {Directory} failed", directory);
        }
    }

    private bool TrySavePng(BitmapSource flattened, string path)
    {
        try
        {
            long bytes = ImageCodec.SavePng(flattened, path);
            _log.LogInformation("Saved {Bytes} bytes to {Path}", bytes, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Save to {Path} failed; keeping editor open", path);
            return false;
        }
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
            InitialDirectory = Directory.Exists(directory) ? directory : null,
            FileName = Path.GetFileName(suggested),
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
