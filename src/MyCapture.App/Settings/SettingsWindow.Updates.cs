using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using MyCapture.App.Store;
using MyCapture.App.Updates;
using MyCapture.Core.Platform;

namespace MyCapture.App.Settings;

/// <summary>
/// The update tab. MyCapture is distributed only through the Microsoft Store, so every build
/// sends the user to the Store listing; none checks GitHub, downloads or runs an installer.
/// </summary>
internal sealed partial class SettingsWindow
{
    /// <summary>Opens the Store listing. Tests replace it so no process starts.</summary>
    internal StoreUpdateLink StoreLink { get; set; } = new();

    private static UpdateVersion CurrentUpdateVersion => UpdateVersion.FromVersion(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    private void InitializeUpdates()
    {
        UpdateTab.Header = UpdateStrings.Title;
        UpdateVersionLabel.Text = AppIdentity.Label + " · " + UpdateStrings.Current(CurrentUpdateVersion);
        if (AppPackage.IsPackaged)
        {
            // The Store updates the packaged build; the button only opens its listing.
            UpdateDescription.Text = UiText.Get("StoreUpdate_StoreDescription");
            UpdateAction.Content = UiText.Get("StoreUpdate_CheckInStore");
            AutomationProperties.SetHelpText(UpdateAction, UiText.Get("StoreUpdate_CheckInStoreHelp"));
        }
        else
        {
            // A development, portable or earlier GitHub install has no update channel of its
            // own any more. It points to the Store version, which keeps its data separately.
            UpdateDescription.Text = UiText.Get("StoreUpdate_UnpackagedDescription")
                + Environment.NewLine + Environment.NewLine + UiText.Get("StoreUpdate_UnpackagedNote");
            UpdateAction.Content = UiText.Get("StoreUpdate_OpenStore");
            AutomationProperties.SetHelpText(UpdateAction, UiText.Get("StoreUpdate_OpenStoreHelp"));
        }

        AutomationProperties.SetName(UpdateAction, (string)UpdateAction.Content);
    }

    private void OnUpdateAction(object sender, RoutedEventArgs e)
    {
        // Nothing to cancel or restart: the Store app (or the browser) takes over from here.
        if (StoreLink.Open(_log) is null)
            SetStatusMessage(UiText.Format("StoreUpdate_OpenFailed", StoreUpdateLink.WebUri));
    }
}
