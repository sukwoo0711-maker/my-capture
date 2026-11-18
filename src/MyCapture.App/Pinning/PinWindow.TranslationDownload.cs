using System.Diagnostics;
using System.Windows.Threading;

namespace MyCapture.App.Pinning;

internal sealed partial class PinWindow
{
    // The share of the model received, shown by the bar while the model downloads.
    private double? _translationDownloadShare;
    private (long Received, long Total)? _queuedDownload;
    private long _translationDownloadShownAt;
    private DispatcherTimer? _translationDownloadTimer;
    // The picture's text is being read; the line says so until the reading ends or a count replaces it.
    private bool _translationReading;

    private double? TranslationDownloadShare => TranslationState == PinTranslationState.Downloading ? _translationDownloadShare : null;

    /// <summary>
    /// Shows how much of the model has arrived, at most once per <see cref="TranslationProgressInterval"/>;
    /// a newer amount waiting for its turn replaces an older one.
    /// </summary>
    internal void ShowTranslationDownloadProgress(long received, long total)
    {
        if (_isClosed || TranslationState != PinTranslationState.Downloading || _translationProgressLine is null || total <= 0) return;
        _queuedDownload = (Math.Clamp(received, 0, total), total);
        TimeSpan wait = _translationDownloadShownAt == 0 ? TimeSpan.Zero
            : TranslationProgressInterval - Stopwatch.GetElapsedTime(_translationDownloadShownAt);
        if (wait <= TimeSpan.Zero)
        {
            ShowQueuedDownload();
            return;
        }
        if (_translationDownloadTimer is null)
        {
            _translationDownloadTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _translationDownloadTimer.Tick += (_, _) => ShowQueuedDownload();
        }
        if (_translationDownloadTimer.IsEnabled) return;
        _translationDownloadTimer.Interval = wait;
        _translationDownloadTimer.Start();
    }

    internal void FlushTranslationDownloadForTest() => ShowQueuedDownload();

    private void ShowQueuedDownload()
    {
        _translationDownloadTimer?.Stop();
        if (_queuedDownload is not { } download) return;
        _queuedDownload = null;
        if (_isClosed || TranslationState != PinTranslationState.Downloading || _translationProgressLine is null) return;
        _translationDownloadShownAt = Stopwatch.GetTimestamp();
        _translationDownloadShare = (double)download.Received / download.Total;
        ReplaceTranslationLine(UiText.Format("Pin_TranslationDownloadProgress",
            Megabytes(download.Received, floor: true), Megabytes(download.Total)));
    }

    /// <summary>The same rounding as the OCR window's download: what arrived rounds down, the size up.</summary>
    private static string Megabytes(long bytes, bool floor = false)
    {
        double megabytes = bytes / 1_000_000d;
        return (floor ? Math.Floor(megabytes) : Math.Ceiling(megabytes)).ToString("N0", UiText.Culture);
    }

    /// <summary>A picture pin's text is read before anything is translated; its line says so.</summary>
    internal void BeginTranslationReading()
    {
        _translationDownloadShare = null;
        if (_isClosed || TranslationState != PinTranslationState.Translating || _translationProgressLine is null) return;
        _translationReading = true;
        ReplaceTranslationLine(UiText.Get("Pin_TranslationReading"));
    }

    /// <summary>The reading has ended: unless a count already replaced it, the line says the translation runs.</summary>
    internal void EndTranslationReading()
    {
        if (!_translationReading) return;
        _translationReading = false;
        if (_isClosed || TranslationState != PinTranslationState.Translating
            || _translationProgressLine != UiText.Get("Pin_TranslationReading")) return;
        ReplaceTranslationLine(UiText.Get("Pin_Translating"));
    }
}
