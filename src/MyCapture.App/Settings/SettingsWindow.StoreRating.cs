using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;

namespace MyCapture.App.Settings;

/// <summary>Manual "Rate in Microsoft Store" entry point on the update tab.</summary>
internal sealed partial class SettingsWindow
{
    /// <summary>Raised after the review page opened, so the automatic request is not repeated.</summary>
    internal Action? StoreReviewOpened { get; set; }

    private void InitializeStoreRating()
    {
        // Only the Store build has a listing to review; a development or portable copy hides it.
        StoreRatingButton.Visibility = AppPackage.IsPackaged ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnStoreRatingClick(object sender, RoutedEventArgs e)
    {
        if (!AppPackage.IsPackaged) return;
        try
        {
            Process.Start(new ProcessStartInfo(StoreRatingPolicy.ReviewUri) { UseShellExecute = true });
            StoreReviewOpened?.Invoke();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log.LogWarning(ex, "Could not open the Microsoft Store review page");
            SetStatusMessage(UiText.Get("StoreRating_OpenFailed"));
        }
    }
}
