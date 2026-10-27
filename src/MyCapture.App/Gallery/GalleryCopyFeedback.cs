using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MyCapture.App.Themes;

namespace MyCapture.App.Gallery;

/// <summary>Which glyph of a copy button a symbol is (<see cref="GalleryCopyFeedback.PartProperty"/>).</summary>
public enum GalleryCopyFeedbackPart
{
    None,

    /// <summary>The button's own copy glyph, shown at rest.</summary>
    Glyph,

    /// <summary>The check mark that stands in for it after a copy succeeds.</summary>
    Check,
}

/// <summary>
/// Confirms a successful copy inside the button that started it: the copy glyph crossfades to a
/// check mark for <see cref="HoldTime"/> and back, and the button's accessible name and tooltip
/// read "복사됨" meanwhile. No notification window opens.
/// </summary>
/// <remarks>
/// The button's content stacks both glyphs in one cell, marked with <see cref="PartProperty"/>; the
/// glyph starts with <c>FluidMotion.FadeTo="1"</c> and the check at opacity 0. Both move through
/// <see cref="FluidMotion.FadeTo"/> on Motion.Fast, so the crossfade snaps when Windows animation
/// effects are off and the check still stays for the whole hold. Only opacity changes, so the
/// button keeps its size. A new copy restarts the hold; a button that is unloaded or shows another
/// capture returns to rest at once.
/// </remarks>
public static class GalleryCopyFeedback
{
    public static readonly DependencyProperty PartProperty = DependencyProperty.RegisterAttached(
        "Part", typeof(GalleryCopyFeedbackPart), typeof(GalleryCopyFeedback),
        new PropertyMetadata(GalleryCopyFeedbackPart.None));

    /// <summary>How long the check stays, as the quick-access card's copy button does (bench 2-B).</summary>
    internal static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(1.2);

    private static readonly DependencyProperty ConfirmationProperty = DependencyProperty.RegisterAttached(
        "Confirmation", typeof(Confirmation), typeof(GalleryCopyFeedback));

    public static void SetPart(DependencyObject element, GalleryCopyFeedbackPart value) => element.SetValue(PartProperty, value);

    public static GalleryCopyFeedbackPart GetPart(DependencyObject element) => (GalleryCopyFeedbackPart)element.GetValue(PartProperty);

    /// <summary>Whether <paramref name="button"/> is showing its check now.</summary>
    internal static bool IsConfirming(ButtonBase button) => button.GetValue(ConfirmationProperty) is Confirmation;

    /// <summary>True when <paramref name="button"/> has a check glyph to show.</summary>
    internal static bool CanConfirm(ButtonBase button) => FindPart(button, GalleryCopyFeedbackPart.Check) is not null;

    /// <summary>Shows the check in <paramref name="button"/>; false when it has none.</summary>
    internal static bool Confirm(ButtonBase button)
    {
        ArgumentNullException.ThrowIfNull(button);
        if (FindPart(button, GalleryCopyFeedbackPart.Check) is not { } check) return false;
        string copied = UiText.Get("QuickAccess.Copied");
        if (button.GetValue(ConfirmationProperty) is Confirmation running)
        {
            running.Restart();
        }
        else
        {
            var confirmation = new Confirmation(button, check, FindPart(button, GalleryCopyFeedbackPart.Glyph), copied);
            button.SetValue(ConfirmationProperty, confirmation);
            confirmation.Start();
        }

        FeedbackTiming.Announce(button, copied, FeedbackKind.Confirmation, "MyCapture.Library.Copy");
        return true;
    }

    private static UIElement? FindPart(ButtonBase button, GalleryCopyFeedbackPart part) => button.Content is DependencyObject content
        ? FindPart(content, part)
        : null;

    // The content's logical tree: available before the button is templated or loaded.
    private static UIElement? FindPart(DependencyObject node, GalleryCopyFeedbackPart part)
    {
        if (node is UIElement element && GetPart(element) == part) return element;
        foreach (object child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject descendant && FindPart(descendant, part) is { } found) return found;
        }

        return null;
    }

    private sealed class Confirmation
    {
        private readonly ButtonBase _button;
        private readonly UIElement _check;
        private readonly UIElement? _glyph;
        private readonly GalleryHeldValue _name;
        private readonly GalleryHeldValue _toolTip;
        private readonly DispatcherTimer _hold;

        internal Confirmation(ButtonBase button, UIElement check, UIElement? glyph, string copied)
        {
            _button = button;
            _check = check;
            _glyph = glyph;
            _name = GalleryHeldValue.Replace(button, AutomationProperties.NameProperty, copied);
            _toolTip = GalleryHeldValue.Replace(button, FrameworkElement.ToolTipProperty, copied);
            _hold = new DispatcherTimer(DispatcherPriority.Normal, button.Dispatcher) { Interval = HoldTime };
            _hold.Tick += (_, _) => End();
        }

        internal void Start()
        {
            if (_glyph is not null) FluidMotion.SetFadeTo(_glyph, 0);
            FluidMotion.SetFadeTo(_check, 1);
            _button.Unloaded += OnButtonUnloaded;
            _button.DataContextChanged += OnButtonDataContextChanged;
            _hold.Start();
        }

        internal void Restart()
        {
            _hold.Stop();
            _hold.Start();
        }

        internal void End()
        {
            if (!ReferenceEquals(_button.GetValue(ConfirmationProperty), this)) return;
            _hold.Stop();
            _button.Unloaded -= OnButtonUnloaded;
            _button.DataContextChanged -= OnButtonDataContextChanged;
            _button.ClearValue(ConfirmationProperty);
            FluidMotion.SetFadeTo(_check, 0);
            if (_glyph is not null) FluidMotion.SetFadeTo(_glyph, 1);
            _name.Restore();
            _toolTip.Restore();
        }

        private void OnButtonUnloaded(object sender, RoutedEventArgs e) => End();

        // The details pane's button stays while the selection moves to another capture.
        private void OnButtonDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => End();
    }
}
