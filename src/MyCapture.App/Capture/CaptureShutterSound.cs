using System.IO;
using System.Media;
using System.Resources;

namespace MyCapture.App.Capture;

/// <summary>
/// The optional capture sound (General.PlayCaptureSound): a short two-click shutter that plays at
/// the moment a still capture is taken, before it is saved or copied.
/// </summary>
/// <remarks>
/// The sound is a WPF resource (Assets/Sounds/shutter.wav, synthesised by
/// build/tools/Sounds/make_shutter.py). One <see cref="SoundPlayer"/> loads it once, off the UI
/// thread, and plays it on a thread-pool thread. It plays with <see cref="SoundPlayer.PlaySync"/>
/// there rather than <see cref="SoundPlayer.Play"/>: an asynchronous play of an in-memory sound
/// keeps reading the player's managed buffer after the call returns, unpinned, while the
/// synchronous play pins it for the whole sound. Either way the UI thread never waits, and a
/// missing or broken sound is silent rather than an error.
/// </remarks>
internal static class CaptureShutterSound
{
    internal const string ResourceName = "assets/sounds/shutter.wav";

    private static readonly Lazy<SoundPlayer?> s_player = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether the sound loaded; false when the resource is missing or invalid. Loads it if needed.</summary>
    internal static bool IsAvailable => s_player.Value is not null;

    /// <summary>Loads the sound in the background so the first capture plays it without delay.</summary>
    internal static void Prepare()
    {
        if (s_player.IsValueCreated) return;
        _ = ThreadPool.UnsafeQueueUserWorkItem(static _ => _ = s_player.Value, null);
    }

    /// <summary>Plays the shutter once without blocking the caller; a newer play cuts off an older one.</summary>
    internal static void Play() =>
        _ = ThreadPool.UnsafeQueueUserWorkItem(static _ =>
        {
            try
            {
                s_player.Value?.PlaySync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // No audio device, a busy device or a damaged resource: the capture goes on silently.
            }
        }, null);

    /// <summary>The WAV bytes compiled into the application's WPF resources; null when absent.</summary>
    internal static byte[]? ReadResource()
    {
        var assembly = typeof(CaptureShutterSound).Assembly;
        var resources = new ResourceManager(assembly.GetName().Name + ".g", assembly);
        using Stream? stream = resources.GetStream(ResourceName);
        if (stream is null) return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static SoundPlayer? Load()
    {
        try
        {
            byte[]? wave = ReadResource();
            if (wave is null) return null;
            var player = new SoundPlayer(new MemoryStream(wave, writable: false));
            // Reads the whole sound now; playing never touches the resource again.
            player.Load();
            return player;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
