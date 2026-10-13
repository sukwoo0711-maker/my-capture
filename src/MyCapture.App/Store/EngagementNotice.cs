using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;

namespace MyCapture.App.Store;

/// <summary>
/// The update-done and rating card: a floating InfoBar laid over the bottom right of a library or
/// settings window. It never moves the window's content, fades in with the shared motion tokens
/// (at once when Windows animations are off) and stays until it is closed or its action is used.
/// </summary>
internal sealed class EngagementNotice : Grid
{
    /// <summary>Width of the card.</summary>
    internal const double CardWidth = 360;

    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _review = new();

    internal EngagementNotice()
    {
        Visibility = Visibility.Collapsed;
        Width = CardWidth;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        FluidMotion.SetTransition(this, MotionSpeed.Deliberate);
        // The shadow is its own layer, so the text is not drawn through the effect.
        var shadow = new Border { CornerRadius = new CornerRadius(8), IsHitTestVisible = false };
        shadow.SetResourceReference(Border.BackgroundProperty, "Surface.Floating");
        shadow.SetResourceReference(EffectProperty, "Shadow.Card");
        var card = new Border { Padding = new Thickness(14, 12, 12, 12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Surface.Floating");
        card.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        _message.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        _review.Content = UiText.Get("StoreRating_InviteAction");
        _review.SetResourceReference(StyleProperty, "Button.Secondary");
        _review.Margin = new Thickness(0, 0, 8, 0);
        AutomationProperties.SetName(_review, UiText.Get("StoreRating_InviteAction"));
        _review.Click += (_, _) => { ReviewChosen?.Invoke(); Dismiss(); };
        var close = new Button { Content = UiText.Get("Dialog.Close") };
        close.SetResourceReference(StyleProperty, "Button.Secondary");
        AutomationProperties.SetName(close, UiText.Get("Dialog.Close"));
        close.Click += (_, _) => Dismiss();
        actions.Children.Add(_review); actions.Children.Add(close);
        var body = new StackPanel();
        body.Children.Add(_message); body.Children.Add(actions);
        card.Child = body;
        Children.Add(shadow);
        Children.Add(card);
        AutomationProperties.SetAutomationId(this, "EngagementNotice");
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite);
    }

    internal Action? ReviewChosen { get; set; }

    /// <summary>The message the card shows.</summary>
    internal string Message => _message.Text;

    internal void ShowInvitation()
    {
        _message.Text = UiText.Get("StoreRating_InviteMessage");
        _review.Visibility = Visibility.Visible;
        Reveal();
    }

    internal void ShowUpdateCompleted(string version)
    {
        _message.Text = UiText.Format("StoreUpdate_CompletedVersion", DisplayVersion(version));
        _review.Visibility = Visibility.Collapsed;
        Reveal();
    }

    /// <summary>"4.1.2.0" as people read it, "4.1.2"; a real fourth part stays.</summary>
    internal static string DisplayVersion(string version) =>
        Version.TryParse(version, out Version? parsed) && parsed.Build >= 0
            ? parsed.Revision > 0 ? parsed.ToString(4) : parsed.ToString(3)
            : version;

    internal void Dismiss() => Visibility = Visibility.Collapsed;

    private void Reveal()
    {
        bool wasHidden = Visibility != Visibility.Visible;
        Visibility = Visibility.Visible;
        if (!wasHidden) return;
        // From transparent to opaque over the deliberate motion token; snaps when motion is off.
        // Only this card's own opacity moves; no shared brush is touched.
        FluidMotion.SetFadeTo(this, 0);
        Opacity = 0;
        FluidMotion.SetFadeTo(this, 1);
    }
}

/// <summary>
/// Lays an <see cref="EngagementNotice"/> over a window's content and decides when a return to
/// that window counts: it must still be the active window a moment after it was activated, so
/// the card never appears under a click that is already on its way.
/// </summary>
internal static class EngagementNoticeHost
{
    /// <summary>How long the window must stay active before a card may appear.</summary>
    internal static readonly TimeSpan ReturnDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Puts <paramref name="notice"/> over the window's existing content, bottom right, above a
    /// footer of <paramref name="bottomInset"/> DIPs (a settings window's Apply row).
    /// </summary>
    internal static void Attach(Window window, EngagementNotice notice, double bottomInset)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(notice);
        if (window.Content is not UIElement existing) return;
        window.Content = null;
        notice.Margin = new Thickness(16, 16, 16, bottomInset);
        var host = new Grid();
        host.Children.Add(existing);
        host.Children.Add(notice);
        window.Content = host;
    }

    /// <summary>
    /// Calls <paramref name="returned"/> once the window has been active for
    /// <see cref="ReturnDelay"/> after each activation. A deactivation before then cancels it.
    /// </summary>
    internal static EngagementReturnWatch WatchReturns(Window window, Action returned)
    {
        ArgumentNullException.ThrowIfNull(window);
        var watch = new EngagementReturnWatch(window.Dispatcher, () => window.IsActive && window.IsVisible, returned, ReturnDelay);
        window.Activated += (_, _) => watch.Activated();
        window.Deactivated += (_, _) => watch.Deactivated();
        window.Closed += (_, _) => watch.Deactivated();
        return watch;
    }
}

/// <summary>
/// Waits a moment after a window is activated and reports a return only if it is still active
/// then. Each activation restarts the wait; a deactivation cancels it.
/// </summary>
internal sealed class EngagementReturnWatch
{
    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _stillActive;
    private readonly Action _returned;

    internal EngagementReturnWatch(Dispatcher dispatcher, Func<bool> stillActive, Action returned, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _stillActive = stillActive ?? throw new ArgumentNullException(nameof(stillActive));
        _returned = returned ?? throw new ArgumentNullException(nameof(returned));
        _timer = new DispatcherTimer(DispatcherPriority.Input, dispatcher) { Interval = delay };
        _timer.Tick += (_, _) => Elapse();
    }

    /// <summary>The wait after an activation is running.</summary>
    internal bool IsWaiting => _timer.IsEnabled;

    /// <summary>How long the window must stay active.</summary>
    internal TimeSpan Delay => _timer.Interval;

    internal void Activated()
    {
        _timer.Stop();
        _timer.Start();
    }

    internal void Deactivated() => _timer.Stop();

    /// <summary>The wait ran out: a return counts if the window is still active.</summary>
    internal void Elapse()
    {
        if (!_timer.IsEnabled) return;
        _timer.Stop();
        if (_stillActive()) _returned();
    }
}
