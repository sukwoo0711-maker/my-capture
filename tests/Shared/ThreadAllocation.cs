namespace MyCapture.Tests;

/// <summary>
/// Bytes the calling thread allocates while a repeated operation runs, measured in several windows.
/// </summary>
/// <remarks>
/// <para>
/// One window is not a measurement of the operation alone. The runtime does one-time work on the thread
/// that runs a method: a loop in a method that started at tier 0 moves to an on-stack-replacement body
/// partway through, and that compilation loads and initialises the types the rest of the method uses on
/// the same thread. Whether those types were already loaded depends on which tests ran first in
/// parallel. Traced locally, a window whose code allocates nothing measured 6,192 and 12,336 bytes when
/// that compilation fell inside it (about one fresh process in thirty, with the call counting delay at
/// zero). On 2026-11-12 a CI run measured 432 bytes and a full local run 40 bytes in such a window; those
/// two were not traced.
/// </para>
/// <para>
/// So the operation is measured in up to <see cref="Windows"/> windows, each running the same calls, and
/// it passes when one window stays within the limit. An operation that allocates on each call allocates in
/// every window and cannot pass; one-time runtime work happens once and leaves the later windows clean.
/// What this no longer catches: work that allocates only during the first windows and then stops.
/// </para>
/// </remarks>
internal static class ThreadAllocation
{
    internal const int Windows = 5;

    /// <summary>
    /// Runs <paramref name="body"/> in up to <see cref="Windows"/> windows, stopping at the first that allocated
    /// no more than <paramref name="limit"/> bytes, and returns what each window allocated.
    /// </summary>
    internal static long[] Measure(Action body, long limit = 0)
    {
        ArgumentNullException.ThrowIfNull(body);
        var windows = new long[Windows];
        int measured = 0;
        while (measured < Windows)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            body();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            windows[measured++] = allocated;
            if (allocated <= limit) break;
        }
        return windows[..measured];
    }

    /// <summary>Whether a window stayed within <paramref name="limit"/>.</summary>
    internal static bool AnyWithin(long[] windows, long limit) => Array.Exists(windows, allocated => allocated <= limit);

    internal static string Describe(long[] windows) => string.Join(", ", windows) + " bytes in " + windows.Length + " window(s)";
}
