using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using MyCapture.Core.Localization;

namespace MyCapture.App.Store;

internal sealed class EngagementNotice : Border
{
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _review = new();
    internal EngagementNotice()
    {
        Visibility = Visibility.Collapsed;
        Margin = new Thickness(12, 8, 12, 4); Padding = new Thickness(12);
        CornerRadius = new CornerRadius(8); BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "Surface.Raised");
        SetResourceReference(BorderBrushProperty, "Border.Control");
        _message.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        _review.Content = UiText.Get("StoreRating_InviteAction");
        _review.SetResourceReference(StyleProperty, "Button.Secondary");
        _review.Margin = new Thickness(12, 0, 8, 0);
        AutomationProperties.SetName(_review, UiText.Get("StoreRating_InviteAction"));
        _review.Click += (_, _) => { ReviewChosen?.Invoke(); Visibility = Visibility.Collapsed; };
        var close = new Button { Content = UiText.Get("Dialog.Close") };
        close.SetResourceReference(StyleProperty, "Button.Secondary");
        close.Click += (_, _) => Visibility = Visibility.Collapsed;
        actions.Children.Add(_review); actions.Children.Add(close);
        var body = new DockPanel(); DockPanel.SetDock(actions, Dock.Right);
        body.Children.Add(actions); body.Children.Add(_message); Child = body;
        AutomationProperties.SetAutomationId(this, "EngagementNotice");
    }

    internal Action? ReviewChosen { get; set; }
    internal void ShowInvitation()
    {
        _message.Text = UiText.Get("StoreRating_InviteMessage");
        _review.Visibility = Visibility.Visible; Visibility = Visibility.Visible;
    }
    internal void ShowUpdateCompleted(string version)
    {
        _message.Text = UiText.Format("StoreUpdate_CompletedVersion", version);
        _review.Visibility = Visibility.Collapsed; Visibility = Visibility.Visible;
    }
}
