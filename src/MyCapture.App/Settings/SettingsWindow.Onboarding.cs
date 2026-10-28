using System.Windows;

namespace MyCapture.App.Settings;

internal sealed partial class SettingsWindow
{
    internal event EventHandler? FirstUseGuideRequested;

    private void OnFirstUseGuide(object sender, RoutedEventArgs e) => FirstUseGuideRequested?.Invoke(this, EventArgs.Empty);
}
