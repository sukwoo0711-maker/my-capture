using MyCapture.Core.Settings;

namespace MyCapture.App;

/// <summary>A shared, bounded introduction to where finished images live.</summary>
internal static class SavedLocationReceipt
{
    internal static bool TryShow(AppSettings settings, Guid captureId, bool selecting, bool receiptVisible,
        Func<string, Action, string, bool> show, Action<Guid> openLibrary)
    {
        // Busy surfaces are skipped, never queued for later presentation.
        if (selecting || receiptVisible) return false;
        return FirstUseTips.ShowSavedLocation(settings.General, () => show(
            UiText.Get("SavedLocation.Title"), () => openLibrary(captureId),
            settings.Hotkeys.OpenLibrary.IsAssigned
                ? UiText.Format("SavedLocation.Hotkey", settings.Hotkeys.OpenLibrary)
                : UiText.Get("SavedLocation.NotificationArea")));
    }
}
