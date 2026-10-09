using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using MyCapture.Core.Platform;

namespace MyCapture.App.Settings;

internal sealed partial class SettingsWindow
{
    /// <summary>MyCapture's Microsoft Store product page, the only distribution and update channel.</summary>
    internal const string StoreProductUri = "ms-windows-store://pdp/?productid=9PB33MC095VR";

    /// <summary>Opens a URI through the shell; replaced by tests so no Store window opens.</summary>
    internal Action<string> OpenStorePage { get; set; } =
        uri => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();

    private void InitializeUpdates()
    {
        UpdateTab.Header = UiText.Get("Text_9EA9BD59E20C");
        UpdateVersionLabel.Text = AppIdentity.Label + " · " + UiText.Format("Text_E285D4BC2FA3", AppIdentity.Version);
        UpdateDescription.Text = UiText.Get("Update.StoreManaged");
        UpdateOpenStore.Content = UiText.Get("Update.OpenStore");
        AutomationProperties.SetName(UpdateOpenStore, (string)UpdateOpenStore.Content);
    }

    private void OnOpenStoreClick(object sender, RoutedEventArgs e)
    {
        UpdateStatus.Text = string.Empty;
        try { OpenStorePage(StoreProductUri); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log.LogWarning(ex, "Microsoft Store product page could not be opened");
            UpdateStatus.Text = UiText.Get("Update.OpenStoreFailed");
        }
    }
}
