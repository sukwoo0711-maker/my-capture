using Microsoft.Extensions.Logging;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Platform.Shell;
using MyCapture.Platform.Display;
using System.Windows.Threading;

namespace MyCapture.App;

public partial class App
{
    private async Task SaveEditorScreenCaptureAsync(CaptureSelectionCompletedEventArgs selection)
    {
        try
        {
            if (_persistence is null || _commit is null)
                throw new InvalidOperationException("Capture persistence is unavailable.");
            if (_settings?.General.PlayCaptureSound == true) CaptureShutterSound.Play();
            Task<CaptureSelectionSource>? lateSource = selection.PendingSource;
            CaptureSelectionSource source = lateSource is { IsCompletedSuccessfully: true } ready
                ? ready.Result : new CaptureSelectionSource(selection.SourceTitle, selection.SourcePageUrl, selection.SourceDetails);
            var pending = CapturePersistenceService.CreatePendingRecord(selection.SelectedBitmap,
                selection.Frame.DpiScale, selection.SourceTitle, selection.Frame.Monitor?.DeviceName ?? string.Empty,
                initialRenderShowsSourceWindowTitle: _settings?.Export.ShowSourceWindowTitle == true,
                sourcePageUrl: source.PageUrl, sourceDetails: source.Details);
            if (pending.SourcePageUrl.Length > 0) pending.Tags = UiText.Get("Capture.WebpageTag");
            var record = await _persistence.PersistPendingOriginalAsync(pending, selection.SelectedBitmap);
            if (lateSource is { IsCompleted: false })
                _sourceBackfillTask = Task.WhenAll(_sourceBackfillTask, BackfillLateSourceAsync(record, published: true, lateSource));
            bool copied = await _commit.CopyCapturedRegionAsync(selection.SelectedBitmap, selection.SourceTitle,
                pending.InitialRenderShowsSourceWindowTitle, pending.SourcePageUrl,
                Core.Capture.CaptureProvenance.FromRecord(pending), CaptureSelectionProvenance.Pending(selection));
            _tray?.SetCaptureCount(_queue?.Count ?? 0);
            _overlay?.CurrentEditor?.ShowCaptureBlocked(UiText.Get(copied ? "Capture.CopiedAndSaved" : "Capture.Saved"));
            _latestReceipt = record.Id;
            var region = new Core.Primitives.RectD(selection.Frame.ScreenBounds.Left + selection.BitmapRegion.Left,
                selection.Frame.ScreenBounds.Top + selection.BitmapRegion.Top,
                selection.BitmapRegion.Width, selection.BitmapRegion.Height);
            MonitorInfo monitor = selection.Frame.Monitor ?? MonitorEnumerator.GetFromCursor();
            // The selector and its persistence transition finish before the receipt is shown.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                ShowQuickAccess(record, selection.SelectedBitmap, region, monitor, copied, preserveEditor: true)));
            _log?.LogInformation("Captured editor screen as a separate library record {RecordId}", record.Id);
        }
        catch (Exception error)
        {
            _log?.LogError(error, "Could not save editor screen capture");
            ShowCommandNotice(UiText.Get("Text_4033027EAEDB"), ThemedDialog.RecoveryMessage(error), TrayBalloonKind.Error);
        }
        finally
        {
            RestoreTrayAfterCapture();
        }
    }
}
