using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using MyCapture.App.Themes;

namespace MyCapture.App.Capture;

/// <summary>
/// The Store rating invitation on the capture card (user decision of 2026-11-06): one row at the bottom of
/// the card, with Review and Close, there from the card's first appearance so nothing on the card moves
/// under the pointer.
/// </summary>
internal sealed partial class QuickAccessWindow
{
    private readonly Grid _ratingInvitation = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _ratingMessage = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private Button _ratingReview = null!;
    private Button _ratingClose = null!;
    private Func<Task?>? _reviewRating;
    private Action? _closeRating;
    private bool _ratingOffered;
    private bool _ratingRequestRunning;
    private bool _ratingUnavailable;

    private Grid BuildRatingInvitation()
    {
        _ratingInvitation.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _ratingInvitation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _ratingInvitation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        KoreanWordWrap.Show(_ratingMessage, UiText.Get("StoreRating_InviteMessage"));
        _ratingMessage.SetResourceReference(TextBlock.FontSizeProperty, "FontSize.Caption");
        _ratingMessage.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        AutomationProperties.SetAutomationId(_ratingMessage, "QuickAccess.RatingMessage");
        AutomationProperties.SetName(_ratingMessage, KoreanWordWrap.Strip(_ratingMessage.Text));
        _ratingInvitation.Children.Add(_ratingMessage);

        _ratingReview = Button("StoreRating_InviteAction", () => _ = ReviewRatingAsync());
        _ratingClose = Button("StoreRating_InviteCloseName", CloseRatingInvitation);
        _ratingReview.Content = UiText.Get("StoreRating_InviteAction");
        _ratingClose.Content = UiText.Get("Dialog.Close");
        AutomationProperties.SetAutomationId(_ratingReview, "QuickAccess.RatingReview");
        AutomationProperties.SetAutomationId(_ratingClose, "QuickAccess.RatingClose");
        foreach (Button button in new[] { _ratingReview, _ratingClose })
        {
            button.SetResourceReference(StyleProperty, "Button.Secondary");
            button.MinWidth = button.MinHeight = 28;
            button.Margin = new Thickness(8, 0, 0, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            button.ToolTip = AutomationProperties.GetName(button);
            ShortToolTip(button);
            _ratingInvitation.Children.Add(button);
        }
        Grid.SetColumn(_ratingReview, 1);
        Grid.SetColumn(_ratingClose, 2);
        return _ratingInvitation;
    }

    /// <summary>
    /// Puts the invitation on the card. Only before the card is first shown: an invitation never appears
    /// on a card already on screen. <paramref name="review"/> is Review: the Store dialog's request over
    /// this card, or null when it was refused. <paramref name="close"/> is Close: the invitation ends.
    /// </summary>
    internal void OfferRatingInvitation(Func<Task?> review, Action close)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(close);
        if (_closed || IsVisible || IsLoaded || new WindowInteropHelper(this).Handle != IntPtr.Zero || _ratingOffered)
            throw new InvalidOperationException("A rating invitation can only be offered once, before the card is shown.");
        _ratingOffered = true;
        _reviewRating = review;
        _closeRating = close;
        _ratingInvitation.Visibility = _ratingUnavailable ? Visibility.Hidden : Visibility.Visible;
    }

    /// <summary>
    /// Review: the card becomes the Store dialog's active owner, then the row leaves, keeping its space. It
    /// also leaves when Review is refused (an error since it showed, the invitation answered elsewhere, an
    /// attempt that could not be saved), so no button stays that does nothing. While the dialog it owns is
    /// open, the card does not close itself.
    /// </summary>
    private async Task ReviewRatingAsync()
    {
        if (_closed || _ratingInvitation.Visibility != Visibility.Visible || _ratingRequestRunning) return;
        _ratingRequestRunning = true;
        Task? request = null;
        try
        {
            ActivateForKeyboard();
            request = _reviewRating!();
        }
        catch (Exception)
        {
            // Nothing opened; the coordinator reports its own failures.
        }
        _ratingInvitation.Visibility = Visibility.Hidden;
        if (request is null)
        {
            _ratingRequestRunning = false;
            return;
        }
        try
        {
            await request;
        }
        catch (Exception)
        {
            // The Store requester owns error reporting. A failed dialog must release its owner's hold too.
        }
        finally
        {
            _ratingRequestRunning = false;
            if (!_closed) KeepAtLeastTheHoldAfterResult();
        }
    }

    private void CloseRatingInvitation()
    {
        if (_closed || _ratingInvitation.Visibility != Visibility.Visible || _ratingRequestRunning) return;
        _ratingInvitation.Visibility = Visibility.Hidden;
        _closeRating!();
    }

    /// <summary>A failure the card holds (ShowActionResult): the invitation leaves this card for good, keeping its space.</summary>
    private void HideRatingInvitationForFailure()
    {
        _ratingUnavailable = true;
        if (_ratingOffered) _ratingInvitation.Visibility = Visibility.Hidden;
    }

    internal (Grid Row, Button Review, Button Close, TextBlock Message) RatingInvitationForTest =>
        (_ratingInvitation, _ratingReview, _ratingClose, _ratingMessage);

    internal bool IsRatingRequestRunningForTest => _ratingRequestRunning;
}
