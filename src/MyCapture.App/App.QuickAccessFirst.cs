using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

/// <summary>
/// Card first: a still capture's quick access card shows on the dispatcher turn after the capture
/// lifts off the screen, while the capture is still being saved and copied, instead of after both.
/// </summary>
/// <remarks>
/// <para>
/// The card shows the captured image at once and takes the flight as before. Its actions that need
/// the saved capture (edit, pin, save, library, copy, copy text, dragging the preview) stay disabled
/// until the save succeeds; then the record, the latest receipt, the card's eviction lease and its
/// actions are bound (<see cref="BindQuickAccessFirst"/>). Its status says 복사 중… until the
/// automatic copy reports, unless the copy had already finished when it showed.
/// </para>
/// <para>
/// A failed save turns the card on screen into the failure, with today's reason, instead of the
/// notification; the recovery editor still opens. A failed copy is the card's own failure to retry
/// with C, unless the card cannot retry it (no saved capture, or the card is gone): then today's
/// notification names where to retry. Capture counts, the Store rating record, the first-use tip and
/// the welcome announcement still count only once the save and copy have reported.
/// </para>
/// </remarks>
public partial class App
{
    /// <summary>
    /// Test-only: the card shows after the save and copy, the ordering before card first, so the
    /// receipt probe can compare both in one build. Off in the product.
    /// </summary>
    internal bool ShowsCardAfterSaveForTest { get; set; }

    /// <summary>Test hook: each notification the still capture handler shows, with its title and message.</summary>
    internal Action<string, string>? CaptureNoticeForTest { get; set; }

    private QuickAccessFirst? _quickAccessFirst;

    /// <summary>A notification from the still capture handler: a failed save or copy no card reports.</summary>
    private void ShowCaptureNotice(string title, string message, TrayBalloonKind kind, bool playSound = true)
    {
        CaptureNoticeForTest?.Invoke(title, message);
        _tray?.ShowBalloon(title, message, kind, playSound);
    }

    /// <summary>
    /// Schedules the card of a capture that will show one, on the turn after its lift; null when the
    /// capture shows no card first (another mode, a text capture, no library).
    /// </summary>
    private QuickAccessFirst? ShowQuickAccessFirst(CaptureSelectionCompletedEventArgs e, Task<bool> copy)
    {
        CancelQuickAccessFirst();
        // The conditions of the card after the save (ShowQuickAccess); a scrolling capture shows it without a flight.
        if (ShowsCardAfterSaveForTest || _services is null || _queue is null || _commit is null || e.TextCapture
            || _settings?.General.AfterCapture != AfterCaptureAction.QuickAccess) return null;
        var region = new RectD(e.Frame.ScreenBounds.Left + e.BitmapRegion.Left,
            e.Frame.ScreenBounds.Top + e.BitmapRegion.Top, e.BitmapRegion.Width, e.BitmapRegion.Height);
        var first = new QuickAccessFirst(e.SelectedBitmap, region, e.Frame.Monitor ?? MonitorEnumerator.GetFromCursor());
        _quickAccessFirst = first;
        // Posted after the lift at the same priority, so the card follows it on the next turn, before the
        // save's and the copy's continuations (Normal priority).
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => ShowQuickAccessFirstCard(first, copy)));
        ReceiptTimeline.Mark?.Invoke("card-first-scheduled");
        return first;
    }

    private void ShowQuickAccessFirstCard(QuickAccessFirst first, Task<bool> copy)
    {
        if (first.Phase != QuickAccessFirstPhase.Scheduled) return;
        first.Phase = QuickAccessFirstPhase.NotShown;
        // As ShowQuickAccess: never over a selector, or while an advanced capture takes its frame.
        if (!ReferenceEquals(_quickAccessFirst, first) || _settings is null || _preparingAdvancedCapture
            || _overlay?.IsSelecting == true) return;
        ReceiptTimeline.Mark?.Invoke("card-op-start");
        // The newer card replaces the older one at once, without its exit.
        _quickAccess?.CloseNow();
        CaptureFlight? flight = _captureFlight is { } lifted && ReferenceEquals(lifted.Image, first.Image) ? lifted : null;
        bool? copied = copy.IsCompleted ? copy.IsCompletedSuccessfully && copy.Result : null;
        QuickAccessWindow? card = null;
        try
        {
            card = QuickAccessWindow.ForPendingCapture(first.Image, copied, first.Region, first.Monitor,
                _settings.General.FirstCaptureTipShown ? null : UiText.Get("Onboard_FirstCaptureTip"), _receiptKeyboard);
            ReceiptTimeline.Mark?.Invoke("card-constructed");
            QuickAccessWindow shown = card;
            shown.Closed += (_, _) =>
            {
                _overlay?.NoteForegroundDismissal(shown.IsCaptureExcluded);
                first.Lease?.Dispose();
                first.Lease = null;
                first.Card = null;
                if (ReferenceEquals(_quickAccessFirst, first)) _quickAccessFirst = null;
                if (ReferenceEquals(_quickAccess, shown)) _quickAccess = null;
                _storeRating?.NotifyFlowEnded();
            };
            _quickAccess = shown;
            first.Card = shown;
            first.Phase = QuickAccessFirstPhase.Shown;
            if (copied is { } result)
            {
                first.CopyReported = true;
                first.CopyFailureShown = !result;
            }
            // The lifted capture flies into this card; without one the card uses its usual entrance.
            flight?.Land(shown);
            ReceiptTimeline.Mark?.Invoke("card-show");
            shown.Show();
            ReceiptTimeline.Mark?.Invoke("card-shown");
            if (copied is null) _ = ObserveQuickAccessFirstCopyAsync(first, copy);
        }
        catch (Exception ex)
        {
            flight?.Finish();
            // The card then shows the usual way once the capture is saved and copied.
            first.Phase = QuickAccessFirstPhase.NotShown;
            first.Card = null;
            if (card is not null && ReferenceEquals(_quickAccess, card)) _quickAccess = null;
            _log?.LogWarning(ex, "Could not show the optional capture receipt");
        }
    }

    private async Task ObserveQuickAccessFirstCopyAsync(QuickAccessFirst first, Task<bool> copy)
    {
        bool copied;
        try
        {
            copied = await copy;
        }
        catch (Exception)
        {
            // The capture handler logs an unexpected copy failure.
            copied = false;
        }

        ReportCopyToQuickAccessFirst(first, copied);
    }

    /// <summary>
    /// The automatic copy's result reaches the card shown first, once: from the copy itself when it
    /// finishes after the card showed, or from the capture handler, whichever comes first.
    /// </summary>
    private void ReportCopyToQuickAccessFirst(QuickAccessFirst first, bool copied)
    {
        if (first.CopyReported || first.Card is not { IsOpen: true } card || !ReferenceEquals(_quickAccess, card)) return;
        // Without its saved capture the card cannot retry the copy; the notification names the editor's retry.
        if (!copied && first.SaveFailed) return;
        first.CopyReported = true;
        first.CopyFailureShown = !copied;
        card.ReportInitialCopy(copied);
        ReceiptTimeline.Mark?.Invoke("card-copy-reported");
    }

    /// <summary>
    /// The save failed: the card on screen shows <paramref name="title"/> and the reason. False when no
    /// card shows it (its turn has not come, or it is gone), so the notification does.
    /// </summary>
    private bool ShowQuickAccessFirstFailure(QuickAccessFirst first, string title, string message)
    {
        first.SaveFailed = true;
        // A card whose turn has not come does not show at all.
        if (first.Phase == QuickAccessFirstPhase.Scheduled) first.Phase = QuickAccessFirstPhase.Cancelled;
        if (first.Phase != QuickAccessFirstPhase.Shown || first.Card is not { IsOpen: true } card
            || !ReferenceEquals(_quickAccess, card)) return false;
        card.ShowCaptureFailure(title, message);
        ReceiptTimeline.Mark?.Invoke("card-failed");
        return true;
    }

    /// <summary>
    /// The save succeeded: the card shown first can act on <paramref name="record"/> from now on. True
    /// when the card-first path has dealt with the receipt (bound, or dropped because the card was
    /// closed or replaced); false when no card showed first, so the receipt shows the usual way.
    /// </summary>
    private bool BindQuickAccessFirst(QuickAccessFirst first, CaptureRecord record)
    {
        switch (first.Phase)
        {
            case QuickAccessFirstPhase.Scheduled:
                // Its turn has not come yet: the card shows the usual way once saved and copied.
                first.Phase = QuickAccessFirstPhase.Cancelled;
                return false;
            case QuickAccessFirstPhase.NotShown:
                return false;
            case QuickAccessFirstPhase.Cancelled:
                // A newer capture has taken its place.
                return true;
        }

        // Closed by the user or replaced: the capture is saved, and no other card shows for it.
        if (first.Card is not { IsOpen: true } card || !ReferenceEquals(_quickAccessFirst, first)
            || !ReferenceEquals(_quickAccess, card) || _queue is null || _settings is null) return true;
        _latestReceipt = record.Id;
        first.Lease = _queue.AcquireEvictionLease(record.Id);
        try
        {
            card.BindCapture(CreateQuickAccessActions(record, () => first.Card));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            first.Lease.Dispose();
            first.Lease = null;
            _log?.LogWarning(ex, "Could not bind the saved capture to its receipt");
            return true;
        }

        ReceiptTimeline.Mark?.Invoke("card-bound");
        if (FirstUseTips.ShowCaptureOnce(_settings.General, () => { })) SaveFirstUseTips();
        return true;
    }

    /// <summary>A newer capture or a capture command takes the card's place; one not yet shown never shows.</summary>
    private void CancelQuickAccessFirst()
    {
        if (_quickAccessFirst is { Phase: QuickAccessFirstPhase.Scheduled } scheduled) scheduled.Phase = QuickAccessFirstPhase.Cancelled;
        _quickAccessFirst = null;
    }

    /// <summary>
    /// A capture command arriving while this capture is still being saved and copied is refused
    /// (<see cref="CaptureBlock.EditorOpening"/>: the earlier capture is about to appear in front). Its card,
    /// shown first, is already in front or about to be: it stays as the answer.
    /// </summary>
    private bool IsQuickAccessFirstAnsweringCapture() =>
        _quickAccessFirst is { } first
        && (first.Phase == QuickAccessFirstPhase.Scheduled
            || first.Phase == QuickAccessFirstPhase.Shown && first.Card is { IsOpen: true } card && ReferenceEquals(_quickAccess, card))
        && ReadCaptureGuardState().EditorOpening;

    private enum QuickAccessFirstPhase
    {
        /// <summary>Its turn, right after the lift, has not come yet.</summary>
        Scheduled,

        /// <summary>The card is on screen, or was until closed or replaced.</summary>
        Shown,

        /// <summary>Its turn came but the card could not show; the receipt shows the usual way.</summary>
        NotShown,

        /// <summary>Replaced before its turn: it never shows.</summary>
        Cancelled,
    }

    /// <summary>One still capture whose card shows first, until its save and copy have reported.</summary>
    private sealed class QuickAccessFirst(BitmapSource image, RectD region, MonitorInfo monitor)
    {
        internal BitmapSource Image { get; } = image;

        /// <summary>Where the capture was taken, in virtual-desktop physical pixels.</summary>
        internal RectD Region { get; } = region;

        internal MonitorInfo Monitor { get; } = monitor;

        internal QuickAccessFirstPhase Phase { get; set; }

        internal QuickAccessWindow? Card { get; set; }

        /// <summary>Keeps the saved capture in the library while its card can act on it.</summary>
        internal IDisposable? Lease { get; set; }

        internal bool SaveFailed { get; set; }

        internal bool CopyReported { get; set; }

        /// <summary>The card showed the copy failure, with C to retry.</summary>
        internal bool CopyFailureShown { get; set; }
    }
}
