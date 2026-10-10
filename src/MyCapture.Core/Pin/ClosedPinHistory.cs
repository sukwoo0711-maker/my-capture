using System.Diagnostics.CodeAnalysis;
using MyCapture.Core.Settings;

namespace MyCapture.Core.Pin;

/// <summary>
/// Memory accounting for the closed-pin restore history.
/// </summary>
public static class ClosedPinMemory
{
    /// <summary>
    /// Upper bound on the estimated bytes the closed-pin history may retain (256 MiB).
    /// </summary>
    /// <remarks>
    /// The count limit alone says nothing about size: the maximum of 100 closed 4K pins
    /// (3840 x 2160 x 4 B, about 31.6 MiB each) would keep about 3.1 GiB of invisible pixels
    /// alive in a resident tray process. 256 MiB still holds the default 20 closed pins of a
    /// full-HD region (1920 x 1080 x 4 B, about 7.9 MiB each, about 158 MiB in total) or eight
    /// full 4K screens, which covers the "I closed that a moment ago" case the feature exists
    /// for while bounding the worst case. Oldest entries are dropped first.
    /// </remarks>
    public const long DefaultMaxRetainedBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Decoded size of a bitmap: one stride (rounded up to whole bytes) per row. An unknown
    /// format (0 bits per pixel) is counted as 32-bit BGRA, the format screen captures use,
    /// so the budget never under-counts an image whose format is not yet resolved.
    /// </summary>
    public static long EstimatePixelBytes(int pixelWidth, int pixelHeight, int bitsPerPixel)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return 0;
        }

        long bits = (long)pixelWidth * (bitsPerPixel > 0 ? bitsPerPixel : 32);
        long stride = (bits + 7) / 8;
        return stride * pixelHeight;
    }

    /// <summary>UTF-16 size of a retained source string (text and table pins).</summary>
    public static long EstimateTextBytes(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (long)text.Length * sizeof(char);
}

/// <summary>
/// The most recently closed pins, bounded both by the user's
/// <see cref="PinSettings.ClosedWindowRestoreLimit"/> and by a retained-byte budget.
/// </summary>
/// <remarks>
/// <para>
/// Restoring is last in, first out: the pin closed most recently comes back first. Each
/// entry keeps the pin's original content (the frozen source pixels or source text) so a
/// restored pin saves, copies and runs OCR on exactly what it showed before it closed.
/// </para>
/// <para>
/// A limit of 0 turns the feature off and discards anything already kept. Lowering the
/// limit trims the oldest entries on the next access, so a settings change takes effect
/// without a restart. An entry larger than the whole byte budget is never retained.
/// </para>
/// <para>
/// The history lives in memory only and is never written to disk. A pinned screen is often
/// sensitive (a chat, a document, a credential on screen), and persisting it would keep
/// pixels the user deliberately closed. Exiting the app discards it.
/// </para>
/// <para>
/// Not thread-safe: the owning pin manager runs entirely on the WPF dispatcher thread.
/// </para>
/// </remarks>
/// <typeparam name="T">The retained entry, for example a pin's content plus placement.</typeparam>
public sealed class ClosedPinHistory<T>
    where T : class
{
    // Oldest first, newest last.
    private readonly LinkedList<(T Item, long Bytes)> _entries = new();
    private readonly Func<int> _limit;
    private long _retainedBytes;

    /// <param name="limit">Reads the current count limit; clamped to the settings range.</param>
    /// <param name="maxRetainedBytes">Total byte budget across all retained entries.</param>
    public ClosedPinHistory(Func<int> limit, long maxRetainedBytes = ClosedPinMemory.DefaultMaxRetainedBytes)
    {
        _limit = limit ?? throw new ArgumentNullException(nameof(limit));
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetainedBytes);
        MaxRetainedBytes = maxRetainedBytes;
    }

    /// <summary>Total byte budget across all retained entries.</summary>
    public long MaxRetainedBytes { get; }

    /// <summary>The current count limit, clamped to the supported settings range.</summary>
    public int Limit => Math.Clamp(
        _limit(),
        SettingsRanges.ClosedWindowRestoreLimit.Min,
        SettingsRanges.ClosedWindowRestoreLimit.Max);

    /// <summary>Number of restorable entries under the current limit.</summary>
    public int Count
    {
        get
        {
            Trim();
            return _entries.Count;
        }
    }

    /// <summary>Estimated bytes currently retained under the current limit.</summary>
    public long RetainedBytes
    {
        get
        {
            Trim();
            return _retainedBytes;
        }
    }

    /// <summary>
    /// Remembers a closed entry as the newest one, then drops the oldest entries until both
    /// the count limit and the byte budget hold.
    /// </summary>
    /// <returns><see langword="false"/> when the entry was not retained.</returns>
    public bool Push(T item, long estimatedBytes)
    {
        ArgumentNullException.ThrowIfNull(item);
        long bytes = Math.Max(0, estimatedBytes);

        if (Limit == 0)
        {
            Clear();
            return false;
        }

        if (bytes > MaxRetainedBytes)
        {
            // Keeping it would evict everything else and still break the budget.
            Trim();
            return false;
        }

        _entries.AddLast((item, bytes));
        _retainedBytes += bytes;
        Trim();
        return true;
    }

    /// <summary>Removes and returns the most recently closed entry.</summary>
    public bool TryPop([MaybeNullWhen(false)] out T item)
    {
        Trim();
        if (_entries.Last is not { } newest)
        {
            item = null;
            return false;
        }

        _entries.RemoveLast();
        _retainedBytes -= newest.Value.Bytes;
        item = newest.Value.Item;
        return true;
    }

    /// <summary>Discards every retained entry.</summary>
    public void Clear()
    {
        _entries.Clear();
        _retainedBytes = 0;
    }

    /// <summary>
    /// Applies the current count limit and byte budget now, dropping the oldest entries.
    /// Called after settings change so lowering the limit (or turning it off with 0)
    /// releases retained screen pixels immediately rather than on the next use.
    /// </summary>
    public void Trim()
    {
        int limit = Limit;
        while (_entries.First is { } oldest
               && (_entries.Count > limit || _retainedBytes > MaxRetainedBytes))
        {
            _entries.RemoveFirst();
            _retainedBytes -= oldest.Value.Bytes;
        }
    }
}
