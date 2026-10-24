using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MyCapture.App.Themes;
using Shapes = System.Windows.Shapes;

namespace MyCapture.App.Capture;

/// <summary>
/// A small, transient, always-on-top window that counts down before a delayed capture and
/// lets the user cancel with Esc.
/// </summary>
/// <remarks>
/// <para>
/// Kept deliberately separate from the capture overlay. The capture-before-wait invariant
/// requires that nothing this window draws can end up in the frozen frame. The owner closes it
/// first and starts the capture only once <see cref="Window.Closed"/> has been raised, so the
/// short shrink a layered countdown plays as it leaves ends before the freeze. Because it is its
/// own top-level window it never shares an HWND or a frozen background with the overlay.
/// </para>
/// <para>
/// Each second the new digit lands while the previous one fades out, and a thin ring in the text colour
/// around the digit runs down over that second. With Windows animation effects off the digit
/// changes in place and the ring steps once a second through the whole countdown.
/// </para>
/// <para>
/// It owns no capture logic. It raises <see cref="Elapsed"/> when the countdown reaches zero
/// and <see cref="Cancelled"/> when the user presses Esc (or closes it), and the owner
/// decides what happens next.
/// </para>
/// </remarks>
internal sealed class CountdownWindow : Window
{
    private const double DigitSize = 54;
    private const double LandingScale = 1.22;
    private const double LeavingScale = 0.88;

    // The ring circles the digit inside the space the digit and the line under it already had,
    // so the window keeps its size.
    private const double RingDiameter = 78;
    private const double RingThickness = 2.5;

    // A beat longer than the fade: the digit is the only thing moving on this surface.
    private static readonly Duration LandingDuration = new(TimeSpan.FromMilliseconds(200));

    private readonly DispatcherTimer _timer;
    private readonly TextBlock _label;
    private readonly TextBlock _leaving;
    private readonly ScaleTransform _labelScale = new(1, 1);
    private readonly ScaleTransform _leavingScale = new(1, 1);
    private readonly Shapes.Path _ring;
    private readonly double _ringLength;
    private readonly int _seconds;
    private int _remaining;
    private int _digitChange;
    private bool _finished;
    private Outcome _outcome;
    private bool _exitFinished;
    private bool _closed;

    internal CountdownWindow(int seconds)
    {
        _seconds = _remaining = Math.Max(1, seconds);

        // This is a transient pre-capture surface rather than a pixel-accurate selection
        // overlay, so a short content reveal makes the delay feel intentional and responsive.
        FluidMotion.SetWindowEntrance(this, true);

        Title = UiText.Get("Text_48FA3D0E60A0");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = CaptureWindowExclusion.SupportsLayeredWindows;
        Background = AllowsTransparency ? Brushes.Transparent : CaptureWindowExclusion.OpaqueBackground();
        if (!AllowsTransparency) ModernWindowChrome.SetEnabled(this, false);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.WidthAndHeight;

        _label = CreateDigit(FormatText(_remaining));
        _label.RenderTransform = _labelScale;

        // AutomationProperties give a screen reader something meaningful to announce while
        // the countdown runs; the label is a Polite live region and each tick explicitly
        // raises a live-region change so the remaining seconds are read aloud as they update.
        AutomationProperties.SetName(_label, UiText.Get("Text_DB39E24CEA42"));
        AutomationProperties.SetLiveSetting(_label, AutomationLiveSetting.Polite);

        // The previous value, only while it fades out over the new one. Screen readers hear the
        // live label alone.
        _leaving = CreateDigit(string.Empty);
        _leaving.Opacity = 0;
        _leaving.RenderTransform = _leavingScale;
        _leaving.IsHitTestVisible = false;
        AutomationProperties.SetIsOffscreenBehavior(_leaving, IsOffscreenBehavior.Offscreen);

        // Both digits scale around the ink of the figure, and the ring is centred on it, not on
        // the line box with its descender space.
        double inkCentre = InkCentre(_label, out double lineHeight);
        _label.RenderTransformOrigin = _leaving.RenderTransformOrigin = new Point(0.5, inkCentre / lineHeight);

        // The text colour, not the accent: accents are kept for primary actions, pin frames and
        // recording, and the digit stays the one focal point of this panel.
        Brush ink = Application.Current?.TryFindResource("Text.Primary") as Brush ?? Brushes.White;
        Brush track = Application.Current?.TryFindResource("Border.Subtle") as Brush
            ?? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        double radius = (RingDiameter - RingThickness) / 2;
        // Dash lengths count stroke thicknesses. The gap is longer than the ring so only one dash
        // is ever on it, and its offset runs the visible arc back to twelve o'clock.
        _ringLength = 2 * Math.PI * radius / RingThickness;
        _ring = new Shapes.Path
        {
            Data = RingGeometry(radius),
            Stroke = ink,
            StrokeThickness = RingThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeDashCap = PenLineCap.Round,
            StrokeDashArray = [_ringLength, _ringLength + 2],
        };
        var ring = new Canvas
        {
            // No size of its own: the ring draws around the digit without moving anything.
            Width = 0,
            Height = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, inkCentre, 0, 0),
            IsHitTestVisible = false,
        };
        foreach (Shapes.Shape part in new Shapes.Shape[]
        {
            new Shapes.Ellipse { Width = RingDiameter, Height = RingDiameter, Stroke = track, StrokeThickness = RingThickness },
            _ring,
        })
        {
            Canvas.SetLeft(part, -RingDiameter / 2);
            Canvas.SetTop(part, -RingDiameter / 2);
            ring.Children.Add(part);
        }

        var digit = new Grid();
        digit.Children.Add(ring);
        digit.Children.Add(_label);
        digit.Children.Add(_leaving);

        // Number plus Korean context only — no English all-caps eyebrow. The panel stays a
        // single calm surface so the digit is the sole focal point. The space above the digit
        // and under it is shared out so the ring clears the line below; the panel's height is
        // unchanged.
        var content = new StackPanel { Margin = new Thickness(34, 11, 34, 22) };
        content.Children.Add(digit);
        content.Children.Add(new TextBlock
        {
            Text = UiText.Get("Text_651505CC0499"),
            Foreground = Application.Current?.TryFindResource("Text.Secondary") as Brush ?? Brushes.LightGray,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 15, 0, 0),
        });
        content.Children.Add(new TextBlock
        {
            Text = UiText.Get("Text_D75A61C65264"),
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
            CornerRadius = new CornerRadius(AllowsTransparency ? 18 : 0),
            Effect = AllowsTransparency ? Application.Current?.TryFindResource("Shadow.Floating") as System.Windows.Media.Effects.Effect : null,
            Child = content,
        };

        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += OnTick;

        Loaded += (_, _) =>
        {
            _timer.Start();
            PlayDigit(previous: null);
            RestartRing();
        };
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

        string previous = _label.Text;
        _label.Text = FormatText(_remaining);
        RaiseCountdownAnnouncement();
        PlayDigit(previous);
        RestartRing();
    }

    /// <summary>
    /// The new value lands (1.22 to 1 over 200ms, fading in over the normal token) while the
    /// previous one shrinks to 0.88 and fades over the fast token. The value itself has already
    /// changed; without motion the digits only snap.
    /// </summary>
    private void PlayDigit(string? previous)
    {
        int change = ++_digitChange;
        _label.BeginAnimation(OpacityProperty, null);
        _labelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _labelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _leaving.BeginAnimation(OpacityProperty, null);
        _leavingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _leavingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        bool animate = FluidMotion.AnimationsEnabled;
        _leaving.Text = animate && previous is not null ? previous : string.Empty;
        if (!animate)
        {
            return;
        }

        _label.BeginAnimation(OpacityProperty, Settle(0, 1, FluidMotion.NormalDuration, FluidMotion.SoftLandingEasing));
        AnimationTimeline land = Settle(LandingScale, 1, LandingDuration, FluidMotion.StandardEasing);
        _labelScale.BeginAnimation(ScaleTransform.ScaleXProperty, land);
        _labelScale.BeginAnimation(ScaleTransform.ScaleYProperty, land);
        if (previous is null)
        {
            return;
        }

        AnimationTimeline fade = Settle(1, 0, FluidMotion.FastDuration, FluidMotion.StandardEasing);
        fade.Completed += (_, _) =>
        {
            if (change == _digitChange) _leaving.Text = string.Empty;
        };
        _leaving.BeginAnimation(OpacityProperty, fade);
        AnimationTimeline shrink = Settle(1, LeavingScale, FluidMotion.FastDuration, FluidMotion.StandardEasing);
        _leavingScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        _leavingScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    /// <summary>
    /// Fills the ring and runs it down, linearly, until the next tick. Without motion it shows the
    /// share of the whole countdown still to go, set once per tick.
    /// </summary>
    private void RestartRing()
    {
        _ring.BeginAnimation(Shapes.Shape.StrokeDashOffsetProperty, null);
        if (!FluidMotion.AnimationsEnabled)
        {
            _ring.StrokeDashOffset = _ringLength * (_seconds - _remaining) / _seconds;
            return;
        }

        _ring.StrokeDashOffset = 0;
        // One stroke thickness past empty, so the round cap of a zero-length dash never shows.
        _ring.BeginAnimation(Shapes.Shape.StrokeDashOffsetProperty,
            new DoubleAnimation(0, _ringLength + 1, new Duration(_timer.Interval)));
    }

    private static DoubleAnimation Settle(double from, double to, Duration duration, IEasingFunction easing) =>
        new(from, to, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };

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
        _outcome = Outcome.Elapsed;
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
        _outcome = Outcome.Cancelled;
        _timer.Stop();
        _timer.Tick -= OnTick;
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The owner's Close after the countdown elapsed or was cancelled plays the exit, and the window
    /// closes when it ends. The window stops taking input at once.
    /// </summary>
    /// <remarks>
    /// When the countdown elapsed the window shrinks away, and the owner starts the capture only
    /// after <see cref="Window.Closed"/>, so the exit never reaches the frozen frame. A cancelled
    /// countdown fades down. Any other close (shutdown, a
    /// close during the exit, a window that never counted down) is immediate, and so is every close
    /// with Windows animation effects off or on an opaque window.
    /// </remarks>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        ExitMotion? exit = _outcome switch
        {
            Outcome.Elapsed => ExitMotion.Shrink,
            Outcome.Cancelled => ExitMotion.FadeDown,
            _ => null,
        };
        if (e.Cancel || _exitFinished || exit is not { } motion || FluidMotion.IsExiting(this)
            || !FluidMotion.AnimationsEnabled || !IsVisible || !AllowsTransparency)
        {
            return;
        }

        // The same conditions make BeginExit play rather than close at once; should it close at
        // once anyway, the close already under way is simply not cancelled.
        bool starting = true, closeNow = false;
        FluidMotion.BeginExit(this, motion, () =>
        {
            if (starting) closeNow = true;
            else CloseAfterExit();
        });
        starting = false;
        e.Cancel = !closeNow;
    }

    private void CloseAfterExit()
    {
        if (_closed)
        {
            return;
        }

        _exitFinished = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        KeyDown -= OnKeyDown;
        _ring.BeginAnimation(Shapes.Shape.StrokeDashOffsetProperty, null);
        // A close that overtook the exit ends it here.
        FluidMotion.CompleteExit(this);
        base.OnClosed(e);
    }

    private static TextBlock CreateDigit(string text) => new()
    {
        Text = text,
        Foreground = Application.Current?.TryFindResource("Text.Primary") as Brush ?? Brushes.White,
        FontFamily = Application.Current?.TryFindResource("Font.Display") as FontFamily
            ?? new FontFamily("Segoe UI"),
        FontSize = DigitSize,
        FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
    };

    /// <summary>The vertical centre of a figure's ink in <paramref name="digit"/>'s line box.</summary>
    private static double InkCentre(TextBlock digit, out double lineHeight)
    {
        var probe = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(digit.FontFamily, digit.FontStyle, digit.FontWeight, digit.FontStretch),
            digit.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(digit).PixelsPerDip);
        lineHeight = probe.Height;
        // OverhangAfter is negative when the ink ends above the bottom of the line box.
        double inkBottom = probe.Height + probe.OverhangAfter;
        return inkBottom - (probe.Extent / 2);
    }

    /// <summary>A circle from twelve o'clock, clockwise, left open so its dash has one start.</summary>
    private static Geometry RingGeometry(double radius)
    {
        double centre = RingDiameter / 2;
        var top = new Point(centre, centre - radius);
        var bottom = new Point(centre, centre + radius);
        var size = new Size(radius, radius);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(top, isFilled: false, isClosed: false);
            g.ArcTo(bottom, size, 0, false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);
            g.ArcTo(top, size, 0, false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }

    private static string FormatText(int seconds) => seconds.ToString(CultureInfo.InvariantCulture);

    private enum Outcome
    {
        None,
        Elapsed,
        Cancelled,
    }
}
