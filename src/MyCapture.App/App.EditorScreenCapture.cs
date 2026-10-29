using Microsoft.Extensions.Logging;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.Core.Queue;
using MyCapture.Platform.Display;
using System.Windows.Threading;

namespace MyCapture.App;

public partial class App
{
    private async Task SaveEditorScreenCaptureAsync(CaptureSelectionCompletedEventArgs selection)
    {
        IDisposable? pendingReceipt = null;
        try
        {
            if (_settings?.General.PlayCaptureSound == true) CaptureShutterSound.Play();
            CaptureDelivery delivery = BeginCaptureDelivery(selection, preserveEditor: true);
            CaptureRecord? record = null;
            try
            {
                if (_persistence is null || _commit is null)
                    throw new InvalidOperationException("Capture persistence is unavailable.");
                // This capture has no edit session. Protect it while the copy or the posted
                // receipt waits, including when persistence releases its own lease on return.
                pendingReceipt = _queue?.AcquireEvictionLease(delivery.Pending.Id);
                record = await _persistence.PersistPendingOriginalAsync(delivery.Pending, selection.SelectedBitmap);
            }
            catch (Exception error) { ReportCaptureSaveFailure(delivery.First, error, preserveEditor: true); }

            bool receiptTaken = delivery.First is { } first && record is not null && BindQuickAccessFirst(first, record);
            if (delivery.LateSource is { } lateSource)
                _sourceBackfillTask = Task.WhenAll(_sourceBackfillTask,
                    BackfillLateSourceAsync(delivery.Pending, published: record is not null, lateSource));
            RecordManualCaptureRegion(selection);
            bool copied = await CompleteCaptureCopyAsync(delivery, record, opensCaptureEditor: false);
            try { _tray?.SetCaptureCount(_queue?.Count ?? 0); }
            catch (Exception error) { _log?.LogWarning(error, "Could not update tray count"); }
            if (record is null) return;

            _overlay?.CurrentEditor?.ShowCaptureBlocked(UiText.Get(copied ? "Capture.CopiedAndSaved" : "Capture.Saved"));
            if (_services is not null && !receiptTaken)
            {
                _latestReceipt = record.Id;
                var region = new Core.Primitives.RectD(selection.Frame.ScreenBounds.Left + selection.BitmapRegion.Left,
                    selection.Frame.ScreenBounds.Top + selection.BitmapRegion.Top,
                    selection.BitmapRegion.Width, selection.BitmapRegion.Height);
                MonitorInfo monitor = selection.Frame.Monitor ?? MonitorEnumerator.GetFromCursor();
                IDisposable? receiptLease = pendingReceipt;
                // The selector and its persistence transition finish before the fallback receipt is shown.
                _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    try { ShowQuickAccess(record, selection.SelectedBitmap, region, monitor, copied, preserveEditor: true); }
                    finally { receiptLease?.Dispose(); }
                }));
                pendingReceipt = null;
            }
            _log?.LogInformation("Captured editor screen as a separate library record {RecordId}", record.Id);
        }
        finally
        {
            pendingReceipt?.Dispose();
            RestoreTrayAfterCapture();
        }
    }
}
