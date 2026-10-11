using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace MyCapture.App.Themes;

/// <summary>What a transient message asks of the user. The kind, not the surface, decides how long it stays.</summary>
internal enum FeedbackKind
{
    /// <summary>A readout of a value the user is changing right now (zoom, opacity); the next change replaces it.</summary>
    Echo,

    /// <summary>Work has started. Its result replaces the message, so progress has no timeout of its own.</summary>
    Progress,

    /// <summary>A short success confirmation such as 복사됨.</summary>
    Confirmation,

    /// <summary>A result with details to read (a file name, a count) or a hint for undoing what just happened.</summary>
    Result,

    /// <summary>The action had no effect or only partly worked, and retrying the same way would not change that.</summary>
    Warning,

    /// <summary>A failure the user has to act on. It stays until dismissed or replaced by the next attempt's outcome.</summary>
    ActionRequired,
}

/// <summary>
/// One display-time policy for confirmations, results and failures across the app.
/// </summary>
/// <remarks>
/// <para>
/// Display time is separate from motion. A message fades with the <see cref="FluidMotion"/>
/// tokens (and appears at once when Windows animations are off); the times below are how long
/// it stays readable, and they only count down while nobody is pointing at or focused on it.
/// </para>
/// <para>
/// Surfaces drawn over the user's own content (a pin, the capture overlay) keep confirmations
/// shorter: the message covers the very pixels the user pinned or is aiming at, and the user
/// usually carries on working with that content right away.
/// </para>
/// </remarks>
internal static class FeedbackTiming
{
    /// <summary>A live value readout; long enough to read a number, gone before the next gesture.</summary>
    internal static readonly TimeSpan Echo = TimeSpan.FromSeconds(1);

    /// <summary>A success confirmation on a card, status line or notification.</summary>
    internal static readonly TimeSpan Confirmation = TimeSpan.FromSeconds(3);

    /// <summary>A success confirmation drawn over the user's own content.</summary>
    internal static readonly TimeSpan ConfirmationOverContent = TimeSpan.FromSeconds(2);

    /// <summary>A result with details, a recovery hint or a warning.</summary>
    internal static readonly TimeSpan Result = TimeSpan.FromSeconds(6);

    /// <summary>Work shorter than this shows only its text; longer work also shows a progress indicator.</summary>
    internal static readonly TimeSpan ProgressRevealDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>The least time a card that closes itself stays after reporting a result.</summary>
    internal static readonly TimeSpan HoldAfterResult = TimeSpan.FromSeconds(4);

    /// <summary>
    /// How long a message of <paramref name="kind"/> stays readable, or <see langword="null"/>
    /// when it stays until replaced (progress) or dismissed (a failure to act on).
    /// </summary>
    internal static TimeSpan? DisplayTime(FeedbackKind kind, bool overContent = false) => kind switch
    {
        FeedbackKind.Echo => Echo,
        FeedbackKind.Confirmation => overContent ? ConfirmationOverContent : Confirmation,
        FeedbackKind.Result or FeedbackKind.Warning => Result,
        _ => null,
    };

    /// <summary>Whether the message needs a close affordance because it never leaves on its own.</summary>
    internal static bool StaysUntilDismissed(FeedbackKind kind) => kind == FeedbackKind.ActionRequired;

    /// <summary>
    /// Asks screen readers to read a dynamic message once. <paramref name="activity"/> groups a
    /// surface's messages so a newer one replaces an unread older one.
    /// </summary>
    internal static void Announce(UIElement source, string message, FeedbackKind kind, string activity)
    {
        if (string.IsNullOrWhiteSpace(message) || !AutomationPeer.ListenerExists(AutomationEvents.Notification))
        {
            return;
        }

        AutomationPeer? peer = UIElementAutomationPeer.FromElement(source)
            ?? UIElementAutomationPeer.CreatePeerForElement(source);
        peer?.RaiseNotificationEvent(NotificationKind(kind), NotificationProcessing(kind), message, activity);
    }

    internal static AutomationNotificationKind NotificationKind(FeedbackKind kind) => kind switch
    {
        FeedbackKind.Confirmation or FeedbackKind.Result => AutomationNotificationKind.ActionCompleted,
        FeedbackKind.Warning or FeedbackKind.ActionRequired => AutomationNotificationKind.ActionAborted,
        _ => AutomationNotificationKind.Other,
    };

    internal static AutomationNotificationProcessing NotificationProcessing(FeedbackKind kind) =>
        kind is FeedbackKind.Warning or FeedbackKind.ActionRequired
            ? AutomationNotificationProcessing.ImportantMostRecent
            : AutomationNotificationProcessing.MostRecent;
}
