using System.Windows;
using MyCapture.App.Diagnostics;
using MyCapture.App.Gallery;
using MyCapture.App.Settings;
using MyCapture.App.Themes;

namespace MyCapture.App;

public partial class App
{
    private LaunchFallbackLog? _launchFallbackLog;

    private void AttachEarlyLaunchLog()
    {
        // Also covers failures before service construction. No settings, library or clipboard is read.
        if (_fileLog is not null) _launchTiming?.Attach(_fileLog.Append);
        else
        {
            _launchFallbackLog ??= new LaunchFallbackLog(() => ResolveAppPaths().LogsRoot);
            _launchTiming?.Attach(_launchFallbackLog.Append);
        }
    }

    private void ObserveLaunchWindow(Window window) => ObserveLaunchWindow(window, () =>
    {
        _launchStatus?.Complete();
        _launchTiming?.FirstVisible(LaunchVisibleKind.Presentation);
    });

    internal static void ObserveLaunchWindow(Window window, Action rendered)
    {
        // Capture prewarming and restored pins are not the launch presentation.
        if (window is not (ThemedDialog or WelcomeWindow or GalleryWindow or SettingsWindow)) return;
        window.ContentRendered += OnLaunchWindowRendered;

        void OnLaunchWindowRendered(object? sender, EventArgs e)
        {
            window.ContentRendered -= OnLaunchWindowRendered;
            if (window.IsVisible) rendered();
        }
    }
}
