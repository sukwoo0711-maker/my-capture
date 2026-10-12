using MyCapture.Core.Capture;
using MyCapture.Platform.Capture;

namespace MyCapture.App.Capture;

/// <summary>Brackets the pixel acquisition, so selection never reads a later live browser tab.</summary>
internal static class CapturePageContext
{
    /// <param name="readSources">
    /// Optional application/folder reader. It runs beside the page reader, before and after the pixels,
    /// and only values both reads agree on are kept (<see cref="WindowSourceService.StableDetails"/>).
    /// </param>
    internal static async Task<(FrozenFrame Frame, IReadOnlyList<WindowCandidate> Windows)> AcquireAsync(
        IReadOnlyList<WindowCandidate> candidates, Func<FrozenFrame> acquireFrame,
        Func<IReadOnlyList<WindowCandidate>, Task<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>> read,
        Func<IReadOnlyList<WindowCandidate>>? refreshCandidates = null,
        Func<IReadOnlyList<WindowCandidate>, Task<IReadOnlyDictionary<IntPtr, WindowSourceSnapshot>>>? readSources = null)
    {
        (FrozenFrame frame, Task<IReadOnlyList<WindowCandidate>> windows) = await AcquireFrameAsync(
            candidates, acquireFrame, read, refreshCandidates, readSources).ConfigureAwait(false);
        return (frame, await windows.ConfigureAwait(false));
    }

    /// <summary>
    /// Returns as soon as the pixels exist. <c>Windows</c> completes once the second read has
    /// confirmed which page and application details still describe those pixels; that read starts
    /// right after the pixels, exactly as in <see cref="AcquireAsync"/>, but nothing on screen has
    /// to wait for it. Each read walks every exposed browser window on every monitor, so it alone
    /// can take longer than the screen grab.
    /// </summary>
    internal static async Task<(FrozenFrame Frame, Task<IReadOnlyList<WindowCandidate>> Windows)> AcquireFrameAsync(
        IReadOnlyList<WindowCandidate> candidates, Func<FrozenFrame> acquireFrame,
        Func<IReadOnlyList<WindowCandidate>, Task<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>> read,
        Func<IReadOnlyList<WindowCandidate>>? refreshCandidates = null,
        Func<IReadOnlyList<WindowCandidate>, Task<IReadOnlyDictionary<IntPtr, WindowSourceSnapshot>>>? readSources = null)
    {
        WindowCandidate[] original = candidates.ToArray();
        WindowCandidate[] probeCandidates = GetUrlCandidates(original);
        Task<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>> beforePages = ReadSafeAsync(read, probeCandidates);
        Task<IReadOnlyDictionary<IntPtr, WindowSourceSnapshot>> beforeSourcesRead = readSources is null
            ? Task.FromResult(NoSources)
            : ReadSafeAsync(readSources, probeCandidates);
        await Task.WhenAll(beforePages, beforeSourcesRead).ConfigureAwait(false);
        IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot> before = await beforePages.ConfigureAwait(false);
        IReadOnlyDictionary<IntPtr, WindowSourceSnapshot> beforeSources = await beforeSourcesRead.ConfigureAwait(false);
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
        return (frame, ConfirmAfterPixelsAsync(original, probeCandidates, stableWindows, before, beforeSources, read, readSources));
    }

    private static async Task<IReadOnlyList<WindowCandidate>> ConfirmAfterPixelsAsync(
        WindowCandidate[] original, WindowCandidate[] probeCandidates, bool stableWindows,
        IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot> before,
        IReadOnlyDictionary<IntPtr, WindowSourceSnapshot> beforeSources,
        Func<IReadOnlyList<WindowCandidate>, Task<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>> read,
        Func<IReadOnlyList<WindowCandidate>, Task<IReadOnlyDictionary<IntPtr, WindowSourceSnapshot>>>? readSources)
    {
        // Only a document seen before the pixels can pass StableUrl. Keep original
        // z-order here, while the topology checks above still inspect every window.
        WindowCandidate[] afterCandidates = stableWindows
            ? probeCandidates.Where(candidate => before.ContainsKey(candidate.Handle)).ToArray()
            : [];
        WindowCandidate[] afterSourceCandidates = stableWindows && readSources is not null
            ? probeCandidates.Where(candidate => beforeSources.ContainsKey(candidate.Handle)).ToArray()
            : [];
        Task<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>> afterPages = afterCandidates.Length == 0
            ? Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(new Dictionary<IntPtr, BrowserDocumentSnapshot>())
            : ReadSafeAsync(read, afterCandidates);
        Task<IReadOnlyDictionary<IntPtr, WindowSourceSnapshot>> afterSourcesRead = afterSourceCandidates.Length == 0
            ? Task.FromResult(NoSources)
            : ReadSafeAsync(readSources!, afterSourceCandidates);
        await Task.WhenAll(afterPages, afterSourcesRead).ConfigureAwait(false);
        IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot> after = await afterPages.ConfigureAwait(false);
        IReadOnlyDictionary<IntPtr, WindowSourceSnapshot> afterSources = await afterSourcesRead.ConfigureAwait(false);
        return original.Select(candidate => candidate with
        {
            SourcePageUrl = stableWindows ? StableUrl(candidate, before, after) : string.Empty,
            Source = readSources is null ? candidate.Source
                : stableWindows ? StableSource(candidate, beforeSources, afterSources) : null,
        }).ToArray();
    }

    private static readonly IReadOnlyDictionary<IntPtr, WindowSourceSnapshot> NoSources =
        new Dictionary<IntPtr, WindowSourceSnapshot>();

    internal static CaptureSourceDetails? StableSource(WindowCandidate candidate,
        IReadOnlyDictionary<IntPtr, WindowSourceSnapshot> before,
        IReadOnlyDictionary<IntPtr, WindowSourceSnapshot> after)
    {
        CaptureSourceDetails details = WindowSourceService.StableDetails(before.GetValueOrDefault(candidate.Handle),
            after.GetValueOrDefault(candidate.Handle), candidate.ProcessId);
        return details.IsEmpty ? null : details;
    }

    private static WindowCandidate[] GetUrlCandidates(WindowCandidate[] candidates)
    {
        var exposed = new List<WindowCandidate>(candidates.Length);
        for (int index = 0; index < candidates.Length; index++)
        {
            var bounds = candidates[index].ScreenBounds;
            bool covered = false;
            for (int earlier = 0; earlier < index && !covered; earlier++)
            {
                var front = candidates[earlier].ScreenBounds;
                // URL attribution chooses the first window containing the selection
                // centre. A fully contained window can never win that lookup. Do not
                // merge occluders or round away even a fractional exposed edge.
                covered = !bounds.IsEmpty && !front.IsEmpty
                    && front.Left <= bounds.Left && front.Top <= bounds.Top
                    && front.Right >= bounds.Right && front.Bottom >= bounds.Bottom;
            }
            if (!covered) exposed.Add(candidates[index]);
        }
        return exposed.ToArray();
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

    private static async Task<IReadOnlyDictionary<IntPtr, T>> ReadSafeAsync<T>(
        Func<IReadOnlyList<WindowCandidate>, Task<IReadOnlyDictionary<IntPtr, T>>> read,
        IReadOnlyList<WindowCandidate> candidates)
    {
        try { return await read(candidates).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return new Dictionary<IntPtr, T>(); }
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
