using Microsoft.Extensions.Logging;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.Core.Queue;

namespace MyCapture.App;

public partial class App
{
    private void SubscribeSavedLocationReceipt(CaptureCommitService commit) =>
        commit.FinishingCommitCompleted += record => ShowSavedLocationReceipt(record.Id);

    private void ShowSavedLocationReceipt(Guid captureId)
    {
        if (_settings is null || _shellPresenter is null || _preparingExit || _shutdownRequested) return;
        try
        {
            if (SavedLocationReceipt.TryShow(_settings, captureId,
                _preparingAdvancedCapture || _overlay?.IsSelecting == true,
                _shellPresenter.HasVisibleNotification || _quickAccess?.IsVisible == true,
                (title, open, tip) => _shellPresenter.ShowCaptureReceipt(title, open, tip, answersCommand: true),
                id => EnsureGalleryWindow()?.OpenCapture(id, edit: false)))
                SaveFirstUseTips();
        }
        catch (Exception ex)
        {
            // A courtesy notice cannot turn a durable edit into a failed commit.
            _log?.LogWarning(ex, "Could not show the saved-location receipt");
        }
    }

    private void ShowSavedLocationAfterCard(QuickAccessWindow card, CaptureRecord record)
    {
        if (!card.ChoseLibraryEditOrPin && !card.WasReplaced && !card.HasCaptureFailed)
            ShowSavedLocationReceipt(record.Id);
    }
}
