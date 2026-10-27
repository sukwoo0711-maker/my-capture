using System.Windows;
using System.Windows.Automation;

namespace MyCapture.App.Gallery;

/// <summary>
/// The library's update and rating notices (see <c>LibraryEngagementNotice</c>): results go to the
/// status line and fade, an available update is a button in the status bar. Nothing is laid over the cards.
/// </summary>
internal sealed partial class GalleryWindow
{
    private Action? _installUpdate;

    /// <summary>The status line's current message.</summary>
    internal string ActionStatusText => LibraryActionStatus.Text;

    /// <summary>True while the status bar offers an update.</summary>
    internal bool IsUpdateAvailableShown => UpdateAvailableButton.Visibility == Visibility.Visible;

    /// <summary>Shows a message on the status line, with its follow-up action beside it when there is one.</summary>
    internal void ShowEngagementStatus(string text, string? actionLabel = null, Func<Task>? action = null) =>
        ShowActionStatus(text, actionLabel, actionLabel, action);

    /// <summary>Replaces the version text with a button that installs the update.</summary>
    internal void SetUpdateAvailable(string label, string toolTip, bool enabled, Action install)
    {
        _installUpdate = install;
        UpdateAvailableButton.Content = label;
        UpdateAvailableButton.ToolTip = toolTip;
        AutomationProperties.SetName(UpdateAvailableButton, label);
        AutomationProperties.SetHelpText(UpdateAvailableButton, toolTip);
        UpdateAvailableButton.IsEnabled = enabled;
        UpdateAvailableButton.Visibility = Visibility.Visible;
        CurrentVersionLabel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Brings the version text back.</summary>
    internal void ClearUpdateAvailable()
    {
        _installUpdate = null;
        UpdateAvailableButton.Visibility = Visibility.Collapsed;
        CurrentVersionLabel.Visibility = Visibility.Visible;
    }

    private void OnUpdateAvailableClick(object sender, RoutedEventArgs e) => _installUpdate?.Invoke();
}
