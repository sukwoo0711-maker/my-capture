using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MyCapture.App.Themes;

namespace MyCapture.App.Pinning;

/// <summary>
/// The thin bar under a pin's translation message. With a count it fills to the share of the work done and eases to
/// each new share, while a glint passes along the filled part, so a bar that waits for a long sentence still shows
/// that work goes on. Without a count a highlight slides along the empty bar.
/// </summary>
/// <remarks>
/// Only transforms move, and only while the bar is on screen and asked to animate: <see cref="Hide"/>,
/// <see cref="Stop"/>, a hidden pin or a hidden message stop them. Without Windows animations the bar does not
/// move at all, and a bar without a count is not shown, since it would only be an empty track.
/// </remarks>
internal sealed class PinProgressStrip : Grid
{
    internal const double BarHeight = 3;

    // The highlight is a third of the bar wide and starts and ends just outside it.
    private const double SweepReach = 0.65;
    private const int SweepFrameRate = 30;
    private static readonly TimeSpan SweepPeriod = TimeSpan.FromMilliseconds(1400);

    private readonly Grid _fill;
    private readonly Rectangle _sweep;
    private readonly ScaleTransform _fillScale = new(0, 1);
    private readonly TranslateTransform _sweepOffset = new(-SweepReach, 0);
    private AnimationClock? _fillEase;

    internal PinProgressStrip()
    {
        Height = BarHeight;
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        // One mask for the highlight and the glint, so both move with one animation.
        var highlight = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            RelativeTransform = _sweepOffset,
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0.35),
                new GradientStop(Colors.Black, 0.5),
                new GradientStop(Colors.Transparent, 0.65),
            },
        };
        _sweep = Bar(0.9, highlight);
        _fill = new Grid
        {
            RenderTransform = _fillScale,
            RenderTransformOrigin = new Point(0, 0.5),
            Visibility = Visibility.Collapsed,
            Children = { Bar(0.75), Bar(0.25, highlight) },
        };
        Children.Add(Bar(0.22));
        Children.Add(_fill);
        Children.Add(_sweep);
        IsVisibleChanged += (_, _) => UpdateMotion();
    }

    /// <summary>The share of the work done, or null while there is no count.</summary>
    internal double? Value { get; private set; }

    /// <summary>Whether the bar is asked to animate; it moves only while it is also on screen.</summary>
    internal bool Animated { get; private set; }

    /// <summary>Whether the highlight or the glint is moving now.</summary>
    internal bool IsMoving { get; private set; }

    /// <summary>Whether the fill is still easing toward <see cref="Value"/>.</summary>
    internal bool IsEasing => _fillEase is not null;

    /// <summary>The share the fill shows at this moment, part way through an ease.</summary>
    internal double ShownValue => _fillScale.ScaleX;

    /// <summary>Whether a transform of the bar is animated at all; false once nothing moves.</summary>
    internal bool HasRunningAnimations => _fillScale.HasAnimatedProperties || _sweepOffset.HasAnimatedProperties;

    /// <param name="value">The share done, or null without a count.</param>
    /// <param name="animate">Whether Windows animations are on.</param>
    internal void Show(double? value, bool animate)
    {
        Animated = animate;
        if (value is { } share)
        {
            double target = Math.Clamp(share, 0, 1);
            double from = Value is null ? 0 : _fillScale.ScaleX;
            StopFillEase();
            _fillScale.ScaleX = target;
            if (animate && Math.Abs(target - from) > 1e-6)
            {
                AnimationClock ease = new DoubleAnimation(from, target, FluidMotion.DeliberateDuration)
                {
                    EasingFunction = FluidMotion.SoftLandingEasing,
                    FillBehavior = FillBehavior.Stop,
                }.CreateClock();
                ease.Completed += OnFillEased;
                _fillEase = ease;
                _fillScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, ease, HandoffBehavior.SnapshotAndReplace);
            }
            Value = target;
            _fill.Visibility = Visibility.Visible;
            _sweep.Visibility = Visibility.Collapsed;
        }
        else
        {
            ClearFill();
            _sweep.Visibility = Visibility.Visible;
        }
        Visibility = value is null && !animate ? Visibility.Collapsed : Visibility.Visible;
        UpdateMotion();
    }

    /// <summary>
    /// Takes the bar off a message that is not the translation's; it keeps its share for when that message returns.
    /// <paramref name="keepSpace"/> keeps the height of a shown bar in a message that is fading out.
    /// </summary>
    internal void Hide(bool keepSpace)
    {
        Animated = false;
        StopFillEase();
        Visibility = keepSpace && Visibility != Visibility.Collapsed ? Visibility.Hidden : Visibility.Collapsed;
        UpdateMotion();
    }

    /// <summary>The translation has ended: nothing moves and the next one starts empty.</summary>
    internal void Stop()
    {
        Hide(keepSpace: false);
        ClearFill();
    }

    private void ClearFill()
    {
        StopFillEase();
        _fillScale.ScaleX = 0;
        _fill.Visibility = Visibility.Collapsed;
        Value = null;
    }

    // A finished ease lets go of its clock, so nothing of the bar stays animated once the fill has arrived.
    private void OnFillEased(object? sender, EventArgs e) => StopFillEase();

    private void StopFillEase()
    {
        if (_fillEase is { } ease) ease.Completed -= OnFillEased;
        _fillEase = null;
        _fillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
    }

    private void UpdateMotion()
    {
        bool move = Animated && IsVisible;
        if (move == IsMoving) return;
        IsMoving = move;
        if (!move)
        {
            _sweepOffset.BeginAnimation(TranslateTransform.XProperty, null);
            return;
        }
        var sweep = new DoubleAnimation(-SweepReach, SweepReach, SweepPeriod)
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        // A slow highlight needs no more frames; the pin redraws for it while the translator works.
        Timeline.SetDesiredFrameRate(sweep, SweepFrameRate);
        _sweepOffset.BeginAnimation(TranslateTransform.XProperty, sweep);
    }

    private static Rectangle Bar(double opacity, Brush? mask = null)
    {
        var bar = new Rectangle
        {
            RadiusX = BarHeight / 2,
            RadiusY = BarHeight / 2,
            Opacity = opacity,
            OpacityMask = mask,
        };
        bar.SetResourceReference(Shape.FillProperty, "Text.Primary");
        return bar;
    }
}
