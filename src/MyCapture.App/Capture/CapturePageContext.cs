using MyCapture.Core.Capture;
using MyCapture.Platform.Capture;

namespace MyCapture.App.Capture;

/// <summary>Brackets the pixel acquisition, so selection never reads a later live browser tab.</summary>
internal static class CapturePageContext
{
    internal static async Task<(FrozenFrame Frame, IReadOnlyList<WindowCandidate> Windows)> AcquireAsync(
        IReadOnlyList<WindowCandidate> candidates, Func<FrozenFrame> acquireFrame,
        Func<IReadOnlyList<WindowCandidate>, Task<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>> read,
        Func<IReadOnlyList<WindowCandidate>>? refreshCandidates = null)
    {
        WindowCandidate[] original = candidates.ToArray();
        IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot> before = await ReadSafeAsync(read, original).ConfigureAwait(false);
        (FrozenFrame frame, bool stableWindows) = await Task.Run(() =>
        {
            // UIA can take long enough for a window to move or another surface to rise.
            // Sample native geometry/z-order right beside the pixel call, independently
            // of the browser-document identity checks and the selector's earlier list.
            WindowCandidate[]? immediatelyBefore = ReadCandidatesSafe(refreshCandidates, original);
            FrozenFrame acquired = acquireFrame();
            WindowCandidate[]? immediatelyAfter = ReadCandidatesSafe(refreshCandidates, original);
            bool stable = SameOrderedWindows(original, immediatelyBefore)
                && SameOrderedWindows(original, immediatelyAfter);
            return (acquired, stable);
        }).ConfigureAwait(false);
        IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot> after = before.Count == 0 || !stableWindows
            ? new Dictionary<IntPtr, BrowserDocumentSnapshot>()
            : await ReadSafeAsync(read, original).ConfigureAwait(false);
        WindowCandidate[] frozen = original.Select(candidate => candidate with
        {
            SourcePageUrl = stableWindows ? StableUrl(candidate, before, after) : string.Empty
        }).ToArray();
        return (frame, frozen);
    }

    private static WindowCandidate[]? ReadCandidatesSafe(Func<IReadOnlyList<WindowCandidate>>? refresh,
        WindowCandidate[] fallback)
    {
        if (refresh is null) return fallback;
        try { return refresh().ToArray(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static bool SameOrderedWindows(IReadOnlyList<WindowCandidate> original,
        IReadOnlyList<WindowCandidate>? current)
    {
        if (current is null || original.Count != current.Count) return false;
        for (int index = 0; index < original.Count; index++)
        {
            WindowCandidate first = original[index], last = current[index];
            if (first.Handle != last.Handle || first.ProcessId != last.ProcessId
                || first.ScreenBounds != last.ScreenBounds
                || !string.Equals(first.Title, last.Title, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static async Task<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>> ReadSafeAsync(
        Func<IReadOnlyList<WindowCandidate>, Task<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>> read,
        IReadOnlyList<WindowCandidate> candidates)
    {
        try { return await read(candidates).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new Dictionary<IntPtr, BrowserDocumentSnapshot>(); }
    }

    internal static string StableUrl(WindowCandidate candidate,
        IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot> before,
        IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot> after)
    {
        if (!before.TryGetValue(candidate.Handle, out BrowserDocumentSnapshot? first)
            || !after.TryGetValue(candidate.Handle, out BrowserDocumentSnapshot? last)
            || first != last || first.ProcessId == 0 || string.IsNullOrEmpty(first.DocumentId)
            || (candidate.ProcessId != 0 && first.ProcessId != candidate.ProcessId)
            || first.WindowTitle != candidate.Title) return string.Empty;
        return SourcePageUrl.Normalize(first.Url);
    }
}
