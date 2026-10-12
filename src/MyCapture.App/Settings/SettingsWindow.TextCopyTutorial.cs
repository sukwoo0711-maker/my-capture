using System.Windows;

namespace MyCapture.App.Settings;

internal sealed partial class SettingsWindow
{
    private void OnTextCopyTutorial(object sender, RoutedEventArgs e) =>
        TextCopyTutorialWindow.ShowTutorial(this, _currentSettings().Hotkeys);
}
