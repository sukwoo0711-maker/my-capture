namespace MyCapture.App.Recording;

/// <summary>One persisted choice shared by open players. This owns no playback resources.</summary>
internal sealed class PlaybackAudioPreference(Func<bool> read, Action<bool> save)
{
    internal bool IsMuted => read();
    internal event Action? Changed;

    internal void Toggle()
    {
        save(!IsMuted);
        Changed?.Invoke();
    }

    internal static PlaybackAudioPreference InMemory(bool muted = false) =>
        new(() => muted, value => muted = value);
}
