using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Themes;

namespace MyCapture.App.Capture;

/// <summary>
/// A small, transient, always-on-top window that counts down before a delayed capture and
/// lets the user cancel with Esc.
/// </summary>
/// <remarks>
/// <para>
/// Kept deliberately separate from the capture overlay. The capture-before-wait invariant
/// requires that nothing this window draws can end up in the frozen frame, so it is fully
/// torn down before the capture runs: the owner closes it first, then schedules the capture
/// on a later dispatcher turn. Because it is its own top-level window it never shares an HWND
/// or a frozen background with the overlay.
/// </para>
/// <para>
/// It owns no capture logic. It raises <see cref="Elapsed"/> when the countdown reaches zero
/// and <see cref="Cancelled"/> when the user presses Esc (or closes it), and the owner
/// decides what happens next.
/// </para>
/// </remarks>
internal sealed class CountdownWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _label;
    private int _remaining;
    private bool _finished;

    internal CountdownWindow(int seconds)
    {
        _remaining = Math.Max(1, seconds);

        // This is a transient pre-capture surface rather than a pixel-accurate selection
        // overlay, so a short content reveal makes the delay feel intentional and responsive.
        FluidMotion.SetWindowEntrance(this, true);

        Title = "MyCapture 지연 캡처";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.WidthAndHeight;

        _label = new TextBlock
        {
            Text = FormatText(_remaining),
            Foreground = Application.Current?.TryFindResource("Text.Primary") as Brush ?? Brushes.White,
            FontFamily = Application.Current?.TryFindResource("Font.Display") as FontFamily
                ?? new FontFamily("Segoe UI"),
            FontSize = 54,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };

        // AutomationProperties give a screen reader something meaningful to announce while
        // the countdown runs; the label is a Polite live region and each tick explicitly
        // raises a live-region change so the remaining seconds are read aloud as they update.
        AutomationProperties.SetName(_label, "지연 캡처 카운트다운");
        AutomationProperties.SetLiveSetting(_label, AutomationLiveSetting.Polite);

        // Number plus Korean context only — no English all-caps eyebrow. The panel stays a
        // single calm surface so the digit is the sole focal point.
        var content = new StackPanel { Margin = new Thickness(34, 24, 34, 22) };
        content.Children.Add(_label);
        content.Children.Add(new TextBlock
        {
            Text = "초 후 캡처됩니다",
            Foreground = Application.Current?.TryFindResource("Text.Secondary") as Brush ?? Brushes.LightGray,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        });
        content.Children.Add(new TextBlock
        {
            Text = "Esc로 취소",
            Foreground = Application.Current?.TryFindResource("Text.Muted") as Brush ?? Brushes.LightGray,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        });

        Content = new Border
        {
            Background = Application.Current?.TryFindResource("Surface.Floating") as Brush
                ?? new SolidColorBrush(Color.FromArgb(0xEE, 0x15, 0x1E, 0x2B)),
            BorderBrush = Application.Current?.TryFindResource("Border.Subtle") as Brush ?? Brushes.DimGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Effect = Application.Current?.TryFindResource("Shadow.Floating") as System.Windows.Media.Effects.Effect,
            Child = content,
        };

        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += OnTick;

        Loaded += (_, _) => _timer.Start();
        KeyDown += OnKeyDown;
    }

    /// <summary>Raised on the dispatcher thread when the countdown completes.</summary>
    internal event EventHandler? Elapsed;

    /// <summary>Raised when the user cancels (Esc) before the countdown completes.</summary>
    internal event EventHandler? Cancelled;

    private void OnTick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining <= 0)
        {
            Complete();
            return;
        }

        _label.Text = FormatText(_remaining);
        RaiseCountdownAnnouncement();
    }

    /// <summary>
    /// Forces a UI Automation live-region event for the countdown label. WPF exposes
    /// AutomationProperties.LiveSetting but does not reliably raise the corresponding event when
    /// a plain TextBlock.Text changes, so each tick raises it explicitly to keep the remaining
    /// seconds audible to screen readers.
    /// </summary>
    private void RaiseCountdownAnnouncement()
    {
        if (!IsLoaded)
        {
            return;
        }

        AutomationPeer? peer = UIElementAutomationPeer.FromElement(_label)
            ?? UIElementAutomationPeer.CreatePeerForElement(_label);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelCountdown();
        }
    }

    private void Complete()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        Elapsed?.Invoke(this, EventArgs.Empty);
    }

    private void CancelCountdown()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        KeyDown -= OnKeyDown;
        base.OnClosed(e);
    }

    private static string FormatText(int seconds) => seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
