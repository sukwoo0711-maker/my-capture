using System.Windows;

namespace MyCapture.App.Settings;

internal sealed partial class SettingsWindow
{
    private void OnTextCopyTutorial(object sender, RoutedEventArgs e) =>
        FeatureTutorialWindow.ShowTutorial(this, () => _currentSettings().Hotkeys, "text-copy");
    private void OnFeatureTutorial(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string featureId })
            FeatureTutorialWindow.ShowTutorial(this, () => _currentSettings().Hotkeys, featureId);
    }
}
