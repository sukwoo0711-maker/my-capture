namespace MyCapture.Core.Settings;

/// <summary>
/// Local bookkeeping for the one-time Microsoft Store rating request, persisted in
/// <c>settings.json</c>.
/// </summary>
/// <remarks>
/// Nothing here leaves the PC: the values only decide whether this install has had enough
/// successful captures, over enough days, to ask once. The settings window never edits them,
/// so an applied draft keeps the live values instead of the copy taken when the window opened.
/// </remarks>
public sealed class StoreRatingState
{
    /// <summary>
    /// The first launch that recorded this state. A settings file written before the field
    /// existed is stamped on its first load, so an existing user waits from that load.
    /// </summary>
    public DateTimeOffset? FirstSeenUtc { get; set; }

    /// <summary>
    /// Region, window, full-screen and scrolling captures that delivered their result. Counting
    /// stops at <see cref="StoreRatingPolicy.RequiredSuccessfulCaptures"/> because nothing reads past it.
    /// </summary>
    public int SuccessfulCaptureCount { get; set; }

    /// <summary>The most recent capture ended in an error; never ask right after one.</summary>
    public bool LastCaptureFailed { get; set; }

    /// <summary>
    /// When the request was attempted, or the user opened the review page from Settings.
    /// Once set the app never asks again, whatever the Store dialog returned.
    /// </summary>
    public DateTimeOffset? RequestAttemptedUtc { get; set; }
}

/// <summary>
/// Pure eligibility rules for the Store rating request. Callers supply the clock and the
/// package state so every boundary is testable without the Store or a real calendar.
/// </summary>
/// <remarks>
/// Apps that asked for a rating before first use drew a quarter of their Korean reviews as
/// complaints about it; asking once, after repeated success, drew almost none. So the request
/// waits for <see cref="RequiredSuccessfulCaptures"/> successes and <see cref="RequiredAge"/>
/// of use, never follows an error, and is attempted at most once per install.
/// </remarks>
public static class StoreRatingPolicy
{
    /// <summary>Microsoft Store product ID of the MyCapture listing.</summary>
    public const string ProductId = "9PB33MC095VR";

    /// <summary>Opens the listing's review page in the Microsoft Store app.</summary>
    public const string ReviewUri = "ms-windows-store://review/?ProductId=" + ProductId;

    public const int RequiredSuccessfulCaptures = 10;

    public static readonly TimeSpan RequiredAge = TimeSpan.FromDays(3);

    /// <summary>
    /// A stamp further ahead than this is treated as a clock that was later corrected, and the
    /// wait restarts instead of blocking the request until that future date.
    /// </summary>
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromDays(1);

    /// <summary>Stamps the first-seen time when it is missing. Returns whether it changed.</summary>
    public static bool EnsureFirstSeen(StoreRatingState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.FirstSeenUtc is { } first && first <= now + FutureTolerance) return false;
        state.FirstSeenUtc = now.ToUniversalTime();
        return true;
    }

    /// <summary>
    /// Records one capture outcome. Returns whether the persisted state changed, so a caller can
    /// skip the settings write once the count is saturated and nothing else moved.
    /// </summary>
    public static bool RecordCapture(StoreRatingState state, bool succeeded)
    {
        ArgumentNullException.ThrowIfNull(state);
        int count = Math.Clamp(state.SuccessfulCaptureCount, 0, RequiredSuccessfulCaptures);
        bool changed = count != state.SuccessfulCaptureCount;
        if (succeeded && count < RequiredSuccessfulCaptures)
        {
            count++;
            changed = true;
        }

        state.SuccessfulCaptureCount = count;
        if (state.LastCaptureFailed == succeeded)
        {
            state.LastCaptureFailed = !succeeded;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// True only for a packaged (Store) install that has not been asked, whose last capture
    /// succeeded, with enough successes and enough days since it was first seen.
    /// </summary>
    public static bool ShouldRequest(StoreRatingState state, bool isPackaged, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        return isPackaged
            && state.RequestAttemptedUtc is null
            && !state.LastCaptureFailed
            && state.SuccessfulCaptureCount >= RequiredSuccessfulCaptures
            && state.FirstSeenUtc is { } first
            && now - first >= RequiredAge;
    }

    /// <summary>Records the single attempt. Call before showing anything so a crash cannot repeat it.</summary>
    public static void MarkAttempted(StoreRatingState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.RequestAttemptedUtc ??= now.ToUniversalTime();
    }
}
