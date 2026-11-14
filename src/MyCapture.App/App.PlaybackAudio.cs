using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyCapture.App.Recording;
using MyCapture.Core.Settings;

namespace MyCapture.App;

public partial class App
{
    private PlaybackAudioPreference? _playbackAudioPreference;

    internal static PlaybackAudioPreference PlaybackAudioForWindow() =>
        Current is App app && app._settings is not null
            ? app._playbackAudioPreference ??= new PlaybackAudioPreference(
                () => app._settings!.Recording.PlaybackMuted,
                app.SavePlaybackMute)
            : PlaybackAudioPreference.InMemory();

    private void SavePlaybackMute(bool muted)
    {
        AppSettings settings = _settings!;
        bool previous = settings.Recording.PlaybackMuted;
        settings.Recording.PlaybackMuted = muted;
        try { _services!.GetRequiredService<SettingsStore>().Save(settings); }
        catch (Exception error)
        {
            settings.Recording.PlaybackMuted = previous;
            _services?.GetService<ILogger<App>>()?.LogWarning(error, "Could not save the playback mute preference");
        }
    }
}
