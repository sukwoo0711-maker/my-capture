using System.Windows.Controls;

namespace MyCapture.App.Recording;

/// <summary>Fails silent until a window has an audio track and explicitly starts playback.</summary>
internal sealed class VideoPlaybackAudio : IDisposable
{
    private readonly MediaElement _media;
    private readonly PlaybackAudioPreference _preference;
    private bool _playing;
    private bool _disposed;
    private static int _activeCount;

    internal VideoPlaybackAudio(MediaElement media, PlaybackAudioPreference preference)
    {
        _media = media;
        _preference = preference;
        _preference.Changed += Apply;
        Interlocked.Increment(ref _activeCount);
        Apply();
    }

    internal static int ActiveCount => Volatile.Read(ref _activeCount);
    internal bool HasAudio { get; private set; }
    internal bool IsMuted => _preference.IsMuted;
    internal event Action? Changed;

    internal void SetSource(bool hasAudio)
    {
        HasAudio = hasAudio;
        Apply();
    }

    internal void SetPlaying(bool playing)
    {
        _playing = playing;
        Apply();
    }

    internal void ToggleMute()
    {
        if (!_disposed && HasAudio) _preference.Toggle();
    }

    private void Apply()
    {
        if (_disposed) return;
        bool audible = HasAudio && _playing && !_preference.IsMuted;
        // Mute first; changing the gain must never expose a previously playing source.
        _media.IsMuted = !audible;
        _media.Volume = audible ? 1 : 0;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        SetPlaying(false);
        _disposed = true;
        _preference.Changed -= Apply;
        Changed = null;
        Interlocked.Decrement(ref _activeCount);
    }
}
