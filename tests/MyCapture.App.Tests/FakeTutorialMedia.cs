using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.Tests;

namespace MyCapture.App.Tests;

/// <summary>
/// Stands in for the tutorial video. By default it reports Opened one dispatcher turn after Open,
/// as MediaElement does; tests raise Failed or Ended, or make any call throw.
/// </summary>
internal sealed class FakeTutorialMedia : ITutorialMedia
{
    private int _generation;

    public UIElement View { get; } = new Border { Background = new LinearGradientBrush(Colors.SteelBlue, Colors.SeaGreen, 0) };
    public List<string> OpenedPaths { get; } = [];
    public string? CurrentPath { get; private set; }
    public bool IsOpen => CurrentPath is not null;
    public bool IsPlaying { get; private set; }
    public bool AutoOpen { get; set; } = true;
    public int PlayCount { get; private set; }
    public int PauseCount { get; private set; }
    public int RewindCount { get; private set; }
    public int CloseCount { get; private set; }
    public bool Disposed { get; private set; }
    public Exception? ThrowOnOpen { get; set; }
    public Exception? ThrowOnPlay { get; set; }
    public Exception? ThrowOnPause { get; set; }
    public Exception? ThrowOnRewind { get; set; }
    public Exception? ThrowOnClose { get; set; }

    public event Action? Opened;
    public event Action<Exception?>? Failed;
    public event Action? Ended;

    public int Subscribers => (Opened?.GetInvocationList().Length ?? 0) + (Failed?.GetInvocationList().Length ?? 0)
        + (Ended?.GetInvocationList().Length ?? 0);

    public void Open(string path)
    {
        if (ThrowOnOpen is not null) throw ThrowOnOpen;
        int generation = ++_generation;
        CurrentPath = path;
        IsPlaying = false;
        OpenedPaths.Add(path);
        if (AutoOpen)
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (generation == _generation && IsOpen) Opened?.Invoke();
            }));
    }

    public void Play()
    {
        if (ThrowOnPlay is not null) throw ThrowOnPlay;
        PlayCount++;
        IsPlaying = true;
    }

    public void Pause()
    {
        if (ThrowOnPause is not null) throw ThrowOnPause;
        PauseCount++;
        IsPlaying = false;
    }

    public void Rewind()
    {
        if (ThrowOnRewind is not null) throw ThrowOnRewind;
        RewindCount++;
    }

    public void Close()
    {
        _generation++;
        CloseCount++;
        CurrentPath = null;
        IsPlaying = false;
        if (ThrowOnClose is not null) throw ThrowOnClose;
    }

    public void Dispose()
    {
        _generation++;
        CurrentPath = null;
        IsPlaying = false;
        Disposed = true;
    }

    /// <summary>Raises an event the way a late or misbehaving player would, whatever the state.</summary>
    public void RaiseOpened() => Opened?.Invoke();
    public void RaiseFailed(Exception? error) => Failed?.Invoke(error);
    public void RaiseEnded() => Ended?.Invoke();
}

internal static class TutorialTestAssets
{
    /// <summary>A fixture folder holding a non-empty {id}.ko.mp4 and {id}.en.mp4 for each feature named.</summary>
    internal static string Create(params string[] featureIds)
    {
        string directory = TestRecycleBin.CreateTempSubdirectory("mc-tutorial-assets-").FullName;
        foreach (string id in featureIds)
            foreach (string language in new[] { "ko", "en" })
                File.WriteAllBytes(Path.Combine(directory, $"{id}.{language}.mp4"), [0]);
        return directory;
    }

    internal static string All() => Create(TutorialCatalog.All.Select(feature => feature.Id).ToArray());

    /// <summary>Adds a small, valid first-frame still {id}.ko.jpg and {id}.en.jpg for each feature named.</summary>
    internal static void AddStills(string directory, params string[] featureIds)
    {
        var pixels = new byte[16 * 9 * 4];
        Array.Fill(pixels, (byte)0x80);
        BitmapSource still = BitmapSource.Create(16, 9, 96, 96, PixelFormats.Bgr32, null, pixels, 16 * 4);
        foreach (string id in featureIds)
            foreach (string language in new[] { "ko", "en" })
            {
                var encoder = new JpegBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(still));
                using var stream = File.Create(Path.Combine(directory, $"{id}.{language}.jpg"));
                encoder.Save(stream);
            }
    }
}

/// <summary>Collects Trace warnings written while it is installed.</summary>
internal sealed class TraceCapture : TraceListener
{
    private readonly List<string> _lines = [];

    internal TraceCapture() => Trace.Listeners.Add(this);

    internal IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToArray(); } }

    public override void Write(string? message) { lock (_lines) _lines.Add(message ?? ""); }

    public override void WriteLine(string? message) => Write(message);

    protected override void Dispose(bool disposing)
    {
        Trace.Listeners.Remove(this);
        base.Dispose(disposing);
    }
}
