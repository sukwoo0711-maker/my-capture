using MyCapture.Platform.Capture;

namespace MyCapture.App.Capture;

/// <summary>Primes source-reader first use without retaining results or showing a surface.</summary>
internal static class CaptureReaderPrewarm
{
    internal static async Task RunAsync(
        Func<IReadOnlyList<WindowCandidate>> readWindows,
        Func<IReadOnlyList<WindowCandidate>, Task> readPages,
        Func<IReadOnlyList<WindowCandidate>, Task>? readApplications)
    {
        IReadOnlyList<WindowCandidate> windows = readWindows();
        await Task.WhenAll(StartAsync(readPages, windows), StartAsync(readApplications, windows));
    }

    private static async Task StartAsync(Func<IReadOnlyList<WindowCandidate>, Task>? read,
        IReadOnlyList<WindowCandidate> windows)
    {
        if (read is not null) await read(windows).ConfigureAwait(false);
    }
}
