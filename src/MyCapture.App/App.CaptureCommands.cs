using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

public partial class App
{
    internal HotkeySettings ReadCaptureHotkeys() => _settings?.Hotkeys ?? new HotkeySettings();

    /// <summary>Window command bars use the same guarded commands as the tray and global shortcuts.</summary>
    internal void ExecuteCaptureCommand(GlobalHotkeyCommand command)
    {
        switch (command)
        {
            case GlobalHotkeyCommand.CaptureRegion: HandleCaptureRequested(); break;
            case GlobalHotkeyCommand.CaptureFullScreen: HandleCaptureFullScreen(); break;
            case GlobalHotkeyCommand.CaptureWindow: HandleCaptureWindow(); break;
            case GlobalHotkeyCommand.RecordRegion: HandleRecordRegion(); break;
        }
    }
}
