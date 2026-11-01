using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Themes;

namespace MyCapture.App;

/// <summary>A replaceable set of visual clocks and token-guarded timers. Never owns lesson state.</summary>
internal sealed class WelcomeMotion(Dispatcher dispatcher)
{
    private readonly List<DispatcherTimer> _timers = [];
    private readonly List<Action> _resets = [];
    private int _token;

    internal void Stop()
    {
        _token++;
        foreach (DispatcherTimer timer in _timers) timer.Stop();
        _timers.Clear();
        foreach (Action reset in _resets) reset();
        _resets.Clear();
    }

    internal void After(TimeSpan delay, Action action)
    {
        int token = _token;
        var timer = new DispatcherTimer(DispatcherPriority.Render, dispatcher) { Interval = delay };
        timer.Tick += Tick;
        _timers.Add(timer);
        timer.Start();
        void Tick(object? sender, EventArgs args)
        {
            timer.Stop();
            timer.Tick -= Tick;
            _timers.Remove(timer);
            if (token == _token) action();
        }
    }

    internal void Fade(FrameworkElement element, double from, double to, Duration duration,
        IEasingFunction easing, TimeSpan delay = default, Action? then = null)
    {
        element.Opacity = from;
        element.BeginAnimation(UIElement.OpacityProperty, Frames(from, to, duration, easing, delay));
        // A fade-out hides its element in the same operation that restores opacity, so no frame
        // can show it back at full strength between two timers.
        Track(() => { then?.Invoke(); element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = 1; }, delay + duration.TimeSpan);
    }

    internal void Slide(FrameworkElement element, double from, double to, bool horizontal,
        Duration duration, IEasingFunction easing, TimeSpan delay = default)
    {
        var transform = horizontal ? new TranslateTransform(from, 0) : new TranslateTransform(0, from);
        element.RenderTransform = transform;
        DependencyProperty property = horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        transform.BeginAnimation(property, Frames(from, to, duration, easing, delay));
        Track(() => { transform.BeginAnimation(property, null); transform.X = transform.Y = 0; }, delay + duration.TimeSpan);
    }

    internal void Fill(FrameworkElement element)
    {
        var scale = new ScaleTransform(0, 1);
        element.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            Frames(0, 1, FluidMotion.DeliberateDuration, FluidMotion.SoftLandingEasing));
        Track(() => { scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.ScaleX = 1; }, FluidMotion.DeliberateDuration.TimeSpan);
    }

    internal void Press(FrameworkElement cap)
    {
        var scale = new ScaleTransform(1, 1);
        var translation = new TranslateTransform();
        cap.RenderTransformOrigin = new Point(.5, .5);
        cap.RenderTransform = new TransformGroup { Children = { scale, translation } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Frames(1, .97, FluidMotion.FastDuration, FluidMotion.StandardEasing));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Frames(1, .97, FluidMotion.FastDuration, FluidMotion.StandardEasing));
        translation.BeginAnimation(TranslateTransform.YProperty, Frames(0, 2, FluidMotion.FastDuration, FluidMotion.StandardEasing));
        Action reset = () =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            translation.BeginAnimation(TranslateTransform.YProperty, null);
            scale.ScaleX = scale.ScaleY = 1; translation.Y = 0;
        };
        _resets.Add(reset);
        After(FluidMotion.FastDuration.TimeSpan, () =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Frames(.97, 1, FluidMotion.NormalDuration, FluidMotion.SoftLandingEasing));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Frames(.97, 1, FluidMotion.NormalDuration, FluidMotion.SoftLandingEasing));
            translation.BeginAnimation(TranslateTransform.YProperty, Frames(2, 0, FluidMotion.NormalDuration, FluidMotion.SoftLandingEasing));
            After(FluidMotion.NormalDuration.TimeSpan, () => { reset(); _resets.Remove(reset); });
        });
    }

    internal void Check(SymbolIcon icon, Geometry glyph)
    {
        icon.Data = Geometry.Empty;
        Action reset = () => { StrokeDrawMotion.Stop(icon); icon.Data = glyph; };
        _resets.Add(reset);
        After(TimeSpan.FromMilliseconds(60), () =>
        {
            StrokeDrawMotion.Play(icon, glyph, new Duration(TimeSpan.FromMilliseconds(200)));
            After(TimeSpan.FromMilliseconds(200), () => { reset(); _resets.Remove(reset); });
        });
    }

    private void Track(Action reset, TimeSpan duration)
    {
        _resets.Add(reset);
        After(duration, () => { reset(); _resets.Remove(reset); });
    }

    private static DoubleAnimationUsingKeyFrames Frames(double from, double to, Duration duration,
        IEasingFunction easing, TimeSpan delay = default)
    {
        // The clock begins immediately, including the hold, so a delayed part never flashes.
        var animation = new DoubleAnimationUsingKeyFrames { Duration = delay + duration.TimeSpan };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > TimeSpan.Zero) animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + duration.TimeSpan), easing));
        return animation;
    }
}
