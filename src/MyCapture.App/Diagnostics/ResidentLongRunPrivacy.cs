using System.Diagnostics;
using System.Security.Cryptography;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;

namespace MyCapture.App.Diagnostics;

/// <summary>Opt-in input suppliers. They never enumerate windows or inspect clipboard contents.</summary>
internal sealed class ResidentLongRunPrivacy
{
    private long _frameCount, _prewarmCount, _windowReads, _pageReads, _sourceReads, _clipboardQueries;
    private int _prewarmCompleted;
    private string? _prewarmError;
    private string? _prewarmPixelHash;
    private double _prewarmMilliseconds;
    internal bool PrewarmFinished => Volatile.Read(ref _prewarmCompleted) != 0;
    internal bool PrewarmSucceeded => PrewarmFinished && Volatile.Read(ref _prewarmError) is null;

    internal FrozenFrame AcquireFrame(RectD desktop)
    {
        Interlocked.Increment(ref _frameCount);
        return ResidentProbeImages.DesktopFrame(desktop, ResidentLongRunContract.Seed);
    }

    internal void PrewarmSyntheticPixel()
    {
        long began = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _prewarmCount);
        try
        {
            var pixel = ResidentProbeImages.Pattern(1, 1, ResidentLongRunContract.Seed);
            byte[] bytes = new byte[4];
            pixel.CopyPixels(bytes, 4, 0);
            _prewarmPixelHash = Convert.ToHexString(SHA256.HashData(bytes));
        }
        catch (Exception error)
        {
            Volatile.Write(ref _prewarmError, error.GetType().FullName);
            throw;
        }
        finally
        {
            _prewarmMilliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            Volatile.Write(ref _prewarmCompleted, 1);
        }
    }

    internal IReadOnlyList<WindowCandidate> ReadWindows(RectD ignored)
    {
        Interlocked.Increment(ref _windowReads);
        return [];
    }

    internal Task<IReadOnlyDictionary<nint, BrowserDocumentSnapshot>> ReadPages(IReadOnlyList<WindowCandidate> windows)
    {
        if (windows.Count != 0) throw new InvalidOperationException("Synthetic reader received real window candidates.");
        Interlocked.Increment(ref _pageReads);
        return Task.FromResult<IReadOnlyDictionary<nint, BrowserDocumentSnapshot>>(new Dictionary<nint, BrowserDocumentSnapshot>());
    }

    internal Task<IReadOnlyDictionary<nint, WindowSourceSnapshot>> ReadSources(IReadOnlyList<WindowCandidate> windows)
    {
        if (windows.Count != 0) throw new InvalidOperationException("Synthetic reader received real window candidates.");
        Interlocked.Increment(ref _sourceReads);
        return Task.FromResult<IReadOnlyDictionary<nint, WindowSourceSnapshot>>(new Dictionary<nint, WindowSourceSnapshot>());
    }

    internal bool ClipboardAvailable()
    {
        Interlocked.Increment(ref _clipboardQueries);
        return false;
    }

    internal sealed record Observation(long SyntheticFrames, long SyntheticPrewarmCalls, bool SyntheticPrewarmFinished,
        string? PrewarmErrorType, string? SyntheticPixelSha256, double PrewarmMilliseconds, long WindowSupplierCalls,
        long PageSupplierCalls, long SourceSupplierCalls, long ClipboardAvailabilitySupplierCalls,
        string NativeReadCoverage);

    internal Observation Snapshot() => new(Interlocked.Read(ref _frameCount), Interlocked.Read(ref _prewarmCount),
        PrewarmFinished, _prewarmError, _prewarmPixelHash, _prewarmMilliseconds, Interlocked.Read(ref _windowReads),
        Interlocked.Read(ref _pageReads), Interlocked.Read(ref _sourceReads), Interlocked.Read(ref _clipboardQueries),
        "Named input boundaries supplied synthetically; no OS-wide native-call interception is claimed.");
}
