using Microsoft.Extensions.Logging;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.Core.Capture;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

public partial class App
{
    /// <summary>The same independent copy, receipt and failure delivery for both still-capture paths.</summary>
    private CaptureDelivery BeginCaptureDelivery(CaptureSelectionCompletedEventArgs selection, bool preserveEditor = false)
    {
        Task<CaptureSelectionSource>? lateSource = selection.PendingSource;
        CaptureSelectionSource source = lateSource is { IsCompletedSuccessfully: true } ready
            ? ready.Result : new CaptureSelectionSource(selection.SourceTitle, selection.SourcePageUrl, selection.SourceDetails);
        if (lateSource?.IsCompleted == true) lateSource = null;
        CaptureRecord pending = CapturePersistenceService.CreatePendingRecord(selection.SelectedBitmap,
            selection.Frame.DpiScale, selection.SourceTitle, selection.Frame.Monitor?.DeviceName ?? string.Empty,
            initialRenderShowsSourceWindowTitle: _settings?.Export.ShowSourceWindowTitle == true,
            sourcePageUrl: source.PageUrl, sourceDetails: source.Details);
        if (pending.SourcePageUrl.Length > 0) pending.Tags = UiText.Get("Capture.WebpageTag");
        // Start before persistence; a failed disk write must not discard the clipboard result.
        Task<bool>? copy = _commit?.CopyCapturedRegionAsync(selection.SelectedBitmap, selection.SourceTitle,
            pending.InitialRenderShowsSourceWindowTitle, pending.SourcePageUrl,
            CaptureProvenance.FromRecord(pending), CaptureSelectionProvenance.Pending(selection));
        QuickAccessFirst? first = copy is not null ? ShowQuickAccessFirst(selection, copy, preserveEditor) : null;
        return new CaptureDelivery(pending, lateSource, copy, first);
    }

    private void ReportCaptureSaveFailure(QuickAccessFirst? first, Exception error, bool preserveEditor = false)
    {
        _log?.LogError(error, "Could not persist the captured original");
        string title = UiText.Get("Text_4033027EAEDB");
        string message = LibrarySaveFailureMessage(preserveEditor ? AfterCaptureAction.QuickAccess
            : _settings?.General.AfterCapture ?? AfterCaptureAction.Editor, error);
        try
        {
            if (first is null || !ShowQuickAccessFirstFailure(first, title, message))
                ShowCaptureNotice(title, message, TrayBalloonKind.Error);
        }
        catch (Exception noticeError)
        {
            // The independent copy must still be observed when shell feedback itself fails.
            _log?.LogWarning(noticeError, "Could not show library-save failure notification");
        }
    }

    private async Task<bool> CompleteCaptureCopyAsync(CaptureDelivery delivery, CaptureRecord? saved)
    {
        if (delivery.Copy is null) return false;
        bool copied = false;
        try
        {
            copied = await delivery.Copy;
            ReceiptTimeline.Mark?.Invoke("copy-resumed");
        }
        catch (Exception error)
        {
            _log?.LogWarning(error, "Automatic region-capture clipboard copy failed unexpectedly");
        }
        if (delivery.First is { } first) ReportCopyToQuickAccessFirst(first, copied);
        if (!copied && delivery.First is not { CopyFailureShown: true, SaveFailed: false })
        {
            string message = UiText.Get(saved is null ? "Text_4048C8E18C61" : "Text_9E0860CEAEC2");
            try { ShowCaptureNotice(UiText.Get("Text_422F61DDCB7C"), message, TrayBalloonKind.Warning, playSound: false); }
            catch (Exception error) { _log?.LogWarning(error, "Could not show automatic clipboard failure notification"); }
        }
        return copied;
    }

    private void RecordManualCaptureRegion(CaptureSelectionCompletedEventArgs selection)
    {
        // Full-screen/window/scroll selections have synthetic bounds and opt out explicitly.
        if (!selection.RecordForRepeat || _lastRegions is null) return;
        try
        {
            var region = new Core.Primitives.RectD(selection.Frame.ScreenBounds.Left + selection.BitmapRegion.Left,
                selection.Frame.ScreenBounds.Top + selection.BitmapRegion.Top,
                selection.BitmapRegion.Width, selection.BitmapRegion.Height).ToPixelBounds();
            MonitorInfo? monitor = selection.Frame.Monitor ?? MonitorEnumerator.GetAll().FirstOrDefault(candidate =>
                region.Left >= candidate.Bounds.Left && region.Top >= candidate.Bounds.Top
                && region.Right <= candidate.Bounds.Right && region.Bottom <= candidate.Bounds.Bottom);
            _lastRegions.Record(monitor is null ? RegionHistoryEntry.Legacy(region)
                : new RegionHistoryEntry(region, monitor.DeviceName, monitor.Bounds, monitor.Dpi));
        }
        catch (Exception error) { _log?.LogWarning(error, "Could not update capture region history"); }
    }

    private sealed record CaptureDelivery(CaptureRecord Pending, Task<CaptureSelectionSource>? LateSource,
        Task<bool>? Copy, QuickAccessFirst? First);
}
