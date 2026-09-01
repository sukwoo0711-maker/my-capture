using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Gallery;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Ocr;

namespace MyCapture.App.Ocr;

/// <summary>Progress of a batch OCR indexing pass.</summary>
/// <param name="Processed">Records attempted so far this pass.</param>
/// <param name="Total">Records that needed indexing when the pass started.</param>
/// <param name="Indexed">Records whose current image generation was indexed this pass.</param>
public readonly record struct OcrIndexingProgress(int Processed, int Total, int Indexed)
{
    public double Fraction => Total <= 0 ? 1.0 : (double)Processed / Total;
}

/// <summary>Outcome of a batch OCR indexing pass.</summary>
public enum OcrIndexingOutcome
{
    /// <summary>Every targeted record was processed.</summary>
    Completed,

    /// <summary>The pass was cancelled partway.</summary>
    Cancelled,

    /// <summary>The OS OCR engine is unavailable (no language pack), so nothing was attempted.</summary>
    Unavailable,

    /// <summary>Nothing needed indexing.</summary>
    NothingToDo,
}

/// <summary>
/// Makes the <em>whole</em> persistent capture queue full-text searchable by recognising and
/// caching OCR text for every capture that does not yet have it.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece that turns "search a past capture by the text inside it" from a
/// per-capture manual action into a property of the entire history — the product's one
/// genuine lock-in candidate. Without it, only captures the user happened to OCR by hand are
/// findable, which the external review correctly identified as the gap that made the moat a
/// claim rather than a feature.
/// </para>
/// <para>
/// Deliberately gentle so it never competes with the capture hotkey on a weak PC: it runs one
/// record at a time on a background task, yields between records, honours cancellation, and
/// stops immediately if the OS engine is unavailable. Persistence reuses
/// <see cref="GalleryController.CacheOcr"/> so an indexed result is written to the record, its
/// recovery metadata, and the on-disk index exactly like a manual OCR — surviving restart.
/// </para>
/// </remarks>
public sealed class OcrIndexingService
{
    private readonly GalleryController _gallery;
    private readonly IOcrService _ocr;
    private readonly Func<CaptureRecord, string> _directoryResolver;
    private readonly Func<OcrSettings> _settings;
    private readonly ILogger<OcrIndexingService> _log;
    private readonly Dispatcher? _mutationDispatcher;

    /// <summary>Test-only observation point executed immediately before the dispatcher-owned cache mutation.</summary>
    internal Action? BeforeCacheOcrForTest { get; set; }

    public OcrIndexingService(
        GalleryController gallery,
        IOcrService ocr,
        Func<CaptureRecord, string> directoryResolver,
        Func<OcrSettings> settings,
        ILogger<OcrIndexingService> log,
        Dispatcher? mutationDispatcher = null)
    {
        _gallery = gallery ?? throw new ArgumentNullException(nameof(gallery));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _directoryResolver = directoryResolver ?? throw new ArgumentNullException(nameof(directoryResolver));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _mutationDispatcher = mutationDispatcher;
    }

    /// <summary>Whether the OS OCR engine can run at all (a language pack is installed).</summary>
    public bool IsAvailable => _ocr.IsAvailable;

    /// <summary>Current full-text search coverage of still images in the queue.</summary>
    public OcrCoverage Coverage =>
        CaptureTextSearch.MeasureCoverage(_gallery.Records.Where(record => record.IsImage));

    /// <summary>
    /// Recognises and caches OCR text for every capture that lacks it.
    /// </summary>
    /// <param name="progress">Optional progress sink, invoked after each record.</param>
    /// <param name="cancellationToken">Stops the pass; already-indexed records are kept.</param>
    public async Task<OcrIndexingOutcome> IndexMissingAsync(
        IProgress<OcrIndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_ocr.IsAvailable)
        {
            _log.LogInformation("OCR indexing skipped: no OS OCR engine (language pack) available");
            return OcrIndexingOutcome.Unavailable;
        }

        IReadOnlyList<CaptureRecord> work = _gallery
            .RecordsMissingOcr()
            .Where(record => record.IsImage)
            .ToList();
        if (work.Count == 0)
        {
            return OcrIndexingOutcome.NothingToDo;
        }

        OcrSettings settings = _settings();
        int processed = 0;
        int indexed = 0;

        _log.LogInformation("OCR indexing started for {Count} capture(s)", work.Count);

        foreach (CaptureRecord record in work)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                progress?.Report(new OcrIndexingProgress(processed, work.Count, indexed));
                _log.LogInformation("OCR indexing cancelled after {Processed}/{Total}", processed, work.Count);
                return OcrIndexingOutcome.Cancelled;
            }

            string? imagePath = ResolveImagePath(record);
            if (imagePath is not null)
            {
                try
                {
                    Guid recordId = record.Id;
                    long requestedContentRevision = record.ContentRevision;
                    OcrRequest request = OcrRequest.FromFile(
                        imagePath,
                        settings.UpscaleFactor,
                        settings.PreferredLanguages);

                    OcrResult result = await _ocr.RecognizeAsync(request, cancellationToken).ConfigureAwait(false);

                    if (result.Status == OcrStatus.Success && result.HasText)
                    {
                        bool cached = await CacheOcrOnOwnerAsync(
                                recordId,
                                result.Text,
                                result.LanguageTag,
                                requestedContentRevision)
                            .ConfigureAwait(false);
                        if (cached)
                        {
                            indexed++;
                        }
                    }
                    else if (result.Status == OcrStatus.NoText)
                    {
                        // Persist a generation-scoped empty result so a genuinely text-free
                        // image is not reprocessed on every pass. It is indexed but naturally
                        // contributes no words to full-text search.
                        bool cached = await CacheOcrOnOwnerAsync(
                                recordId,
                                string.Empty,
                                result.LanguageTag,
                                requestedContentRevision)
                            .ConfigureAwait(false);
                        if (cached)
                        {
                            indexed++;
                        }

                        _log.LogDebug("Capture {Id} contains no recognisable text", recordId);
                    }
                    else if (result.Status == OcrStatus.Unavailable)
                    {
                        // Engine went away mid-pass (language pack removed): stop cleanly.
                        _log.LogWarning("OCR engine became unavailable during indexing");
                        progress?.Report(new OcrIndexingProgress(processed, work.Count, indexed));
                        return OcrIndexingOutcome.Unavailable;
                    }
                    else if (result.Status == OcrStatus.Cancelled)
                    {
                        progress?.Report(new OcrIndexingProgress(processed, work.Count, indexed));
                        _log.LogInformation(
                            "OCR indexing cancelled during recognition after {Processed}/{Total}",
                            processed,
                            work.Count);
                        return OcrIndexingOutcome.Cancelled;
                    }
                }
                catch (OperationCanceledException)
                {
                    return OcrIndexingOutcome.Cancelled;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.LogWarning(ex, "OCR indexing could not read image for {Id}", record.Id);
                }
            }

            processed++;
            progress?.Report(new OcrIndexingProgress(processed, work.Count, indexed));

            // Yield so a burst of recognitions never monopolises the machine.
            await Task.Yield();
        }

        _log.LogInformation("OCR indexing finished: {Indexed}/{Total} current generations indexed", indexed, work.Count);
        return OcrIndexingOutcome.Completed;
    }

    private async Task<bool> CacheOcrOnOwnerAsync(
        Guid id,
        string text,
        string? languageTag,
        long expectedContentRevision)
    {
        if (_mutationDispatcher is null || _mutationDispatcher.CheckAccess())
        {
            BeforeCacheOcrForTest?.Invoke();
            return _gallery.CacheOcr(id, text, languageTag, expectedContentRevision);
        }

        // Recognition deliberately finishes off-thread. Queue records and their observable
        // collection belong to WPF's dispatcher, so generation-neutral OCR metadata must join
        // the same serial UI mutation stream as editor finalisation before writing meta/index.
        return await _mutationDispatcher.InvokeAsync(
                () =>
                {
                    BeforeCacheOcrForTest?.Invoke();
                    return _gallery.CacheOcr(id, text, languageTag, expectedContentRevision);
                },
                DispatcherPriority.Background)
            .Task
            .ConfigureAwait(false);
    }

    private string? ResolveImagePath(CaptureRecord record)
    {
        if (!record.IsImage)
        {
            return null;
        }

        string dir = _directoryResolver(record);
        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }

        // Prefer the rendered (annotated) image so recognised text matches what the user sees;
        // fall back to the original when there are no annotations.
        string rendered = Path.Combine(dir, CaptureFileNames.Rendered);
        if (File.Exists(rendered))
        {
            return rendered;
        }

        string original = Path.Combine(dir, CaptureFileNames.Original);
        return File.Exists(original) ? original : null;
    }
}
