namespace MyCapture.Core.Settings;

/// <summary>
/// Local bookkeeping for the Microsoft Store rating invitation, persisted in <c>settings.json</c>.
/// </summary>
/// <remarks>
/// Nothing here leaves the PC: the values only decide whether this install has had enough
/// successful uses to offer the invitation, and whether the user has already answered it. The
/// settings window never edits them, so an applied draft keeps the live values instead of the copy
/// taken when the window opened.
/// </remarks>
public sealed class StoreRatingState
{
    /// <summary>
    /// The first launch that recorded this state. A settings file written before the field
    /// existed is stamped on its first load. Since 2026-11-06 the invitation no longer waits on it.
    /// </summary>
    public DateTimeOffset? FirstSeenUtc { get; set; }

    /// <summary>
    /// Successful uses: a delivered capture, a new pin, copied OCR text or a saved recording. Counting
    /// stops at <see cref="StoreRatingPolicy.RequiredSuccessfulCaptures"/> because nothing reads past it.
    /// </summary>
    public int SuccessfulCaptureCount { get; set; }

    /// <summary>The most recent use ended in an error; never ask right after one.</summary>
    public bool LastCaptureFailed { get; set; }

    /// <summary>
    /// When the user chose Review on the invitation, or opened the review page from Settings.
    /// Once set the app never asks again, whatever the Store dialog returned.
    /// </summary>
    public DateTimeOffset? RequestAttemptedUtc { get; set; }

    /// <summary>
    /// When the invitation was last shown. A file that has it without <see cref="InvitationShownCount"/>
    /// comes from a build that showed its single invitation, and that counts as answered.
    /// </summary>
    public DateTimeOffset? InvitationShownUtc { get; set; }

    /// <summary>
    /// How many times the invitation was shown. One left unanswered is offered once more, no sooner than
    /// 24 hours after the first; a card row an error hid before it was answered is not counted.
    /// </summary>
    public int InvitationShownCount { get; set; }

    /// <summary>When the user closed the invitation with its Close button; it is not offered again.</summary>
    public DateTimeOffset? InvitationDismissedUtc { get; set; }
}

/// <summary>
/// Pure eligibility rules for the Store rating invitation. Callers supply the clock and the
/// package state so every boundary is testable without the Store or a real calendar.
/// </summary>
/// <remarks>
/// On 2026-11-06 the user lowered the threshold so the invitation comes sooner: two successful uses
/// (a delivered capture, a new pin, copied OCR text or a saved recording) are enough, with no waiting
/// day, and the capture card carries it too. On 2026-11-07 the follow-up decisions added four rules:
/// a card shown before its capture is recorded counts that capture as a success
/// (<see cref="ShouldRequestCountingSuccess"/>) and withdraws the invitation if the save or copy then
/// fails; a card counts its showing in memory and saves it once it has shown; a row an error hides
/// before it is answered is not counted; and an unanswered invitation is offered once more only
/// <see cref="SecondInvitationDelay"/> after the first showing, so it shows at most
/// <see cref="MaxInvitations"/> times. Close, Review or the review link in Settings end it. It is still
/// never offered right after an error, during the first run or outside the Store build, and the Store
/// dialog opens only from its Review button.
/// </remarks>
public static class StoreRatingPolicy
{
    /// <summary>Microsoft Store product ID of the MyCapture listing.</summary>
    public const string ProductId = "9PB33MC095VR";

    /// <summary>Opens the listing's review page in the Microsoft Store app.</summary>
    public const string ReviewUri = "ms-windows-store://review/?ProductId=" + ProductId;

    /// <summary>Successful uses before the invitation is offered.</summary>
    public const int RequiredSuccessfulCaptures = 2;

    /// <summary>How many times an unanswered invitation is shown before the app stops offering it.</summary>
    public const int MaxInvitations = 2;

    /// <summary>Minimum time between the first and second unanswered showings.</summary>
    public static readonly TimeSpan SecondInvitationDelay = TimeSpan.FromHours(24);

    /// <summary>
    /// A stamp further ahead than this is treated as a clock that was later corrected, and the
    /// first-seen time restarts instead of staying at that future date.
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
    /// Records one outcome. Returns whether the persisted state changed, so a caller can skip the
    /// settings write once the count is saturated and nothing else moved.
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
    /// True once the user has answered the invitation: Review or the review link in Settings (an
    /// attempt), Close, or a settings file from a build that showed its single invitation before
    /// showings were counted.
    /// </summary>
    public static bool IsAnswered(StoreRatingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.RequestAttemptedUtc is not null
            || state.InvitationDismissedUtc is not null
            || (state.InvitationShownUtc is not null && state.InvitationShownCount <= 0);
    }

    /// <summary>
    /// True only for a packaged (Store) install whose invitation is unanswered and was shown fewer
    /// than <see cref="MaxInvitations"/> times, whose last use succeeded, with enough successful uses.
    /// A counted showing with a timestamp must be at least <see cref="SecondInvitationDelay"/> old.
    /// </summary>
    public static bool ShouldRequest(StoreRatingState state, bool isPackaged, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        return isPackaged
            && !IsAnswered(state)
            && Math.Max(state.InvitationShownCount, 0) < MaxInvitations
            && (state.InvitationShownCount < 1 || state.InvitationShownUtc is not { } shown
                || now - shown >= SecondInvitationDelay)
            && !state.LastCaptureFailed
            && state.SuccessfulCaptureCount >= RequiredSuccessfulCaptures;
    }

    /// <summary>Tests a pending capture's success on a private copy, leaving the live state untouched.</summary>
    public static bool ShouldRequestCountingSuccess(StoreRatingState state, bool isPackaged, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        var predicted = new StoreRatingState
        {
            FirstSeenUtc = state.FirstSeenUtc,
            SuccessfulCaptureCount = state.SuccessfulCaptureCount,
            LastCaptureFailed = state.LastCaptureFailed,
            RequestAttemptedUtc = state.RequestAttemptedUtc,
            InvitationShownUtc = state.InvitationShownUtc,
            InvitationShownCount = state.InvitationShownCount,
            InvitationDismissedUtc = state.InvitationDismissedUtc,
        };
        RecordCapture(predicted, succeeded: true);
        return ShouldRequest(predicted, isPackaged, now);
    }

    /// <summary>
    /// Counts one showing in this state. The library and settings windows save it before showing
    /// anything; a card saves it once it has shown.
    /// </summary>
    public static void MarkInvitationShown(StoreRatingState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.InvitationShownCount = Math.Clamp(state.InvitationShownCount, 0, MaxInvitations - 1) + 1;
        state.InvitationShownUtc = now.ToUniversalTime();
    }

    /// <summary>Records the invitation's Close, keeping the first time.</summary>
    public static void MarkInvitationDismissed(StoreRatingState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.InvitationDismissedUtc ??= now.ToUniversalTime();
    }

    /// <summary>Records the single attempt. Call before showing anything so a crash cannot repeat it.</summary>
    public static void MarkAttempted(StoreRatingState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.RequestAttemptedUtc ??= now.ToUniversalTime();
    }
}
