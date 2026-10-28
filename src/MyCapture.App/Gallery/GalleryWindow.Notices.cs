using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.App.Themes;

namespace MyCapture.App.Gallery;

/// <summary>
/// The library's update and rating notices (see <c>LibraryEngagementNotice</c>). Nothing is laid over the
/// cards: a notice is one trimmed line in a slot of its own on the status bar, and an available update is a
/// button where the version number is.
/// </summary>
/// <remarks>
/// The slot is separate from the action status line, which carries a result and its follow-up (Undo after a
/// delete). A notice only shows while that line is empty and goes away, neutral in colour, after its own
/// lifetime; user actions and recovery follow-ups always win, and a notice that had to wait shows afterwards
/// with the time it still had.
/// </remarks>
internal sealed partial class GalleryWindow
{
    /// <summary>How long a rating invitation stays on the status bar.</summary>
    internal static readonly TimeSpan InvitationLifetime = TimeSpan.FromSeconds(20);

    /// <summary>How long "updated to X" stays on the status bar.</summary>
    internal static readonly TimeSpan UpdateDoneLifetime = TimeSpan.FromSeconds(8);

    private sealed class LibraryNoticeState(string text, string? actionLabel, Action? action, Action? shown, Action? expired)
    {
        internal string Text { get; } = text;
        internal string? ActionLabel { get; } = actionLabel;
        internal Action? Action { get; } = action;
        internal Action? Shown { get; } = shown;
        internal Action? Expired { get; } = expired;
        internal bool WasShown { get; set; }
    }

    private Action? _installUpdate;
    private Action? _dismissUpdate;
    private LibraryNoticeState? _notice;
    private FeedbackCountdown? _noticeCountdown;

    /// <summary>The status line's current message.</summary>
    internal string ActionStatusText => LibraryActionStatus.Text;

    /// <summary>True while the status bar offers an update.</summary>
    internal bool IsUpdateAvailableShown => UpdateAvailableButton.Visibility == Visibility.Visible;

    /// <summary>True while a notice is on the status bar.</summary>
    internal bool IsNoticeVisible => IsVisible && WindowState != WindowState.Minimized && LibraryNotice.IsVisible;

    /// <summary>True while <paramref name="text"/> is the notice waiting for, or on, the status bar.</summary>
    internal bool HasNotice(string text) => _notice?.Text == text;

    internal string NoticeTextForTest => LibraryNoticeText.Text;

    internal string NoticeActionForTest => LibraryNoticeAction.Visibility == Visibility.Visible ? LibraryNoticeAction.Content as string ?? string.Empty : string.Empty;

    /// <summary>Puts a result with a follow-up action on the status line, as a delete does with Undo.</summary>
    internal void ShowEngagementStatusForTest(string text, string actionLabel, Func<Task> action) =>
        ShowActionStatus(text, actionLabel, actionLabel, action);

    internal void InvokeNoticeActionForTest() => OnNoticeActionClick(this, new RoutedEventArgs());

    /// <summary>Spends notice time as the countdown's clock would.</summary>
    internal void AdvanceNoticeForTest(TimeSpan elapsed) => _noticeCountdown?.Advance(elapsed);

    internal TimeSpan? NoticeRemainingForTest => _noticeCountdown?.Remaining;

    private void InitializeLibraryNotices()
    {
        IsVisibleChanged += (_, _) => RefreshNotice();
        StateChanged += (_, _) => RefreshNotice();
        // Effective visibility can change after the window's event, as WPF updates its descendants.
        LibraryNotice.IsVisibleChanged += (_, _) => RecordNoticeShown();
        Closed += (_, _) => ClearNotice();
    }

    /// <summary>
    /// Offers a notice. It shows at once when the status line is free; otherwise it waits behind the result
    /// that holds it. Returns whether it is on screen now.
    /// </summary>
    /// <param name="shown">Called once, when the notice first appears.</param>
    /// <param name="expired">Called when its lifetime ends unused.</param>
    internal bool ShowNotice(string text, string? actionLabel, Action? action, TimeSpan lifetime,
        Action? shown = null, Action? expired = null)
    {
        _notice = new LibraryNoticeState(text, actionLabel, action, shown, expired);
        _noticeCountdown ??= new FeedbackCountdown(Dispatcher,
            () => !IsNoticeVisible || LibraryStatusBar.IsMouseOver || LibraryStatusBar.IsKeyboardFocusWithin, ExpireNotice);
        _noticeCountdown.Start(lifetime);
        return RefreshNotice();
    }

    /// <summary>Takes the notice away without calling its expiry.</summary>
    internal void ClearNotice()
    {
        _notice = null;
        _noticeCountdown?.Stop();
        RefreshNotice();
    }

    private void ExpireNotice()
    {
        LibraryNoticeState? expired = _notice;
        _notice = null;
        RefreshNotice();
        expired?.Expired?.Invoke();
    }

    /// <summary>Shows or hides the notice slot to match the notice and whether the status line is free.</summary>
    private bool RefreshNotice()
    {
        if (LibraryNotice is null) return false;
        if (_notice is not { } notice || !IsVisible || WindowState == WindowState.Minimized
            || !string.IsNullOrEmpty(LibraryActionStatus.Text))
        {
            LibraryNotice.Visibility = Visibility.Collapsed;
            return false;
        }

        bool wasHidden = LibraryNotice.Visibility != Visibility.Visible;
        LibraryNoticeText.Text = notice.Text;
        LibraryNoticeText.ToolTip = notice.Text;
        bool hasAction = notice.Action is not null && !string.IsNullOrEmpty(notice.ActionLabel);
        LibraryNoticeAction.Content = notice.ActionLabel;
        AutomationProperties.SetName(LibraryNoticeAction, notice.ActionLabel ?? string.Empty);
        LibraryNoticeAction.Visibility = hasAction ? Visibility.Visible : Visibility.Collapsed;
        LibraryNotice.Visibility = Visibility.Visible;
        if (wasHidden)
        {
            // Opacity only, like every library message; it appears at once when Windows animations are off.
            FluidMotion.SetFadeTo(LibraryNotice, 0);
            LibraryNotice.Opacity = 0;
            FluidMotion.SetFadeTo(LibraryNotice, 1);
        }

        RecordNoticeShown();
        return IsNoticeVisible;
    }

    private void RecordNoticeShown()
    {
        if (IsNoticeVisible && string.IsNullOrEmpty(LibraryActionStatus.Text)
            && _notice is { WasShown: false } notice)
        {
            notice.WasShown = true;
            FeedbackTiming.Announce(LibraryNoticeText, notice.Text, FeedbackKind.Result, "MyCapture.Library.Notice");
            notice.Shown?.Invoke();
        }

    }

    private void OnNoticeActionClick(object sender, RoutedEventArgs e)
    {
        Action? action = _notice?.Action;
        ClearNotice();
        action?.Invoke();
    }

    /// <summary>Replaces the version text with a button that installs the update.</summary>
    internal void SetUpdateAvailable(string label, string toolTip, bool enabled, Action install, Action? dismiss = null)
    {
        _installUpdate = install;
        _dismissUpdate = dismiss;
        // One trimmed line, so a long label never widens the status bar past the column's limit.
        UpdateAvailableButton.Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis };
        UpdateAvailableButton.ToolTip = toolTip;
        AutomationProperties.SetName(UpdateAvailableButton, label);
        AutomationProperties.SetHelpText(UpdateAvailableButton, toolTip);
        UpdateAvailableButton.IsEnabled = enabled;
        UpdateAvailableButton.Visibility = Visibility.Visible;
        UpdateDismissButton.Visibility = dismiss is null ? Visibility.Collapsed : Visibility.Visible;
        CurrentVersionLabel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Brings the version text back.</summary>
    internal void ClearUpdateAvailable()
    {
        _installUpdate = null;
        _dismissUpdate = null;
        UpdateAvailableButton.Visibility = Visibility.Collapsed;
        UpdateDismissButton.Visibility = Visibility.Collapsed;
        CurrentVersionLabel.Visibility = Visibility.Visible;
    }

    private void OnUpdateAvailableClick(object sender, RoutedEventArgs e) => _installUpdate?.Invoke();

    private void OnUpdateDismissClick(object sender, RoutedEventArgs e) => _dismissUpdate?.Invoke();

    private void OnStatusBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        SummaryColumn.MaxWidth = Math.Max(160, LibraryStatusBar.ActualWidth * 0.55);
    }
}
