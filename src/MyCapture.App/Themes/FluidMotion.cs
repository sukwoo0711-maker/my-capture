using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MyCapture.App.Themes;

/// <summary>Which motion token a state transition uses (Tokens.xaml Motion.*).</summary>
public enum MotionSpeed
{
    /// <summary>83ms: hover and press.</summary>
    Fast,

    /// <summary>167ms: selection, check and toggle indicators.</summary>
    Normal,

    /// <summary>250ms: panel changes.</summary>
    Deliberate,
}

/// <summary>How a floating window leaves the screen (<see cref="FluidMotion.BeginExit"/>).</summary>
public enum ExitMotion
{
    /// <summary>Fades and drops 6 DIP: the window entrance played backwards.</summary>
    FadeDown,

    /// <summary>Fades and slides 8 DIP right: a card docked at the screen's right edge.</summary>
    FadeRight,

    /// <summary>Fades and shrinks to 0.97 around the centre: a pin landing played backwards.</summary>
    Shrink,
}

/// <summary>
/// Small, cancellable motion primitives shared by the desktop chrome.
/// </summary>
/// <remarks>
/// Motion is deliberately limited to opacity and render transforms, so it never changes
/// layout bounds or delays a semantic state change. Every gesture replaces an in-flight
/// animation and <see cref="SystemParameters.ClientAreaAnimation"/> is checked at the moment
/// the gesture occurs, which makes the Windows reduced-motion preference authoritative.
/// </remarks>
public static class FluidMotion
{
    public static readonly DependencyProperty WindowEntranceProperty =
        DependencyProperty.RegisterAttached(
            "WindowEntrance",
            typeof(bool),
            typeof(FluidMotion),
            new PropertyMetadata(false, OnWindowEntranceChanged));

    public static readonly DependencyProperty PressFeedbackProperty =
        DependencyProperty.RegisterAttached(
            "PressFeedback",
            typeof(bool),
            typeof(FluidMotion),
            new PropertyMetadata(false, OnPressFeedbackChanged));

    /// <summary>
    /// Slides a switch template's thumb and fades its accent fill when the toggle changes.
    /// The template names <c>PART_SwitchTrack</c>, <c>PART_SwitchFill</c>,
    /// <c>PART_SwitchThumb</c> and <c>PART_SwitchThumbOn</c>.
    /// </summary>
    public static readonly DependencyProperty SwitchMotionProperty =
        DependencyProperty.RegisterAttached(
            "SwitchMotion",
            typeof(bool),
            typeof(FluidMotion),
            new PropertyMetadata(false, OnSwitchMotionChanged));

    /// <summary>
    /// The opacity a template layer settles at. Triggers set this instead of Opacity and the
    /// layer fades there over its <see cref="TransitionProperty"/> token; it snaps while the
    /// layer is not loaded yet or Windows animation effects are off. The state itself (the
    /// trigger, focus, IsChecked) never waits for the fade.
    /// </summary>
    public static readonly DependencyProperty FadeToProperty =
        DependencyProperty.RegisterAttached(
            "FadeTo",
            typeof(double),
            typeof(FluidMotion),
            new PropertyMetadata(0d, OnFadeToChanged));

    /// <summary>The uniform scale an element settles at, animated like <see cref="FadeToProperty"/>.</summary>
    public static readonly DependencyProperty ScaleToProperty =
        DependencyProperty.RegisterAttached(
            "ScaleTo",
            typeof(double),
            typeof(FluidMotion),
            new PropertyMetadata(1d, OnScaleToChanged));

    /// <summary>
    /// The angle, in degrees, an element settles at around its centre, animated like
    /// <see cref="FadeToProperty"/>: an expander chevron turns instead of jumping.
    /// </summary>
    public static readonly DependencyProperty RotateToProperty =
        DependencyProperty.RegisterAttached(
            "RotateTo",
            typeof(double),
            typeof(FluidMotion),
            new PropertyMetadata(0d, OnRotateToChanged));

    /// <summary>The motion token <see cref="FadeToProperty"/>, <see cref="ScaleToProperty"/> and <see cref="RotateToProperty"/> use.</summary>
    public static readonly DependencyProperty TransitionProperty =
        DependencyProperty.RegisterAttached(
            "Transition",
            typeof(MotionSpeed),
            typeof(FluidMotion),
            new PropertyMetadata(MotionSpeed.Fast));

    /// <summary>
    /// A button template part that takes the press feedback instead of the whole control, so a
    /// check box or radio button scales its glyph and leaves the label still.
    /// </summary>
    internal const string PressTargetName = "PART_PressTarget";

    private static readonly DependencyProperty PressTransformProperty =
        DependencyProperty.RegisterAttached(
            "PressTransform",
            typeof(ScaleTransform),
            typeof(FluidMotion));

    private static readonly DependencyProperty RotationTransformProperty =
        DependencyProperty.RegisterAttached(
            "RotationTransform",
            typeof(RotateTransform),
            typeof(FluidMotion));

    private static readonly DependencyProperty WindowEntranceTransformProperty =
        DependencyProperty.RegisterAttached(
            "WindowEntranceTransform",
            typeof(TranslateTransform),
            typeof(FluidMotion));

    private static readonly DependencyProperty WindowEntranceRequestProperty =
        DependencyProperty.RegisterAttached(
            "WindowEntranceRequest",
            typeof(int),
            typeof(FluidMotion));

    // The pending close of a window whose exit is playing; CompleteExit runs it early.
    private static readonly DependencyProperty ExitFinishProperty =
        DependencyProperty.RegisterAttached(
            "ExitFinish",
            typeof(Action),
            typeof(FluidMotion));

    [ThreadStatic]
    private static bool? s_animationsEnabledOverrideForTest;

    // 0.97 moves a 90-DIP button edge 1.4 DIP: the smallest press a fingertip notices.
    private const double PressedScale = 0.97;

    // An 18px glyph needs a deeper press than a whole button to read as pressed at all.
    private const double PressedGlyphScale = 0.9;

    public static void SetWindowEntrance(DependencyObject element, bool value) =>
        element.SetValue(WindowEntranceProperty, value);

    public static bool GetWindowEntrance(DependencyObject element) =>
        (bool)element.GetValue(WindowEntranceProperty);

    public static void SetPressFeedback(DependencyObject element, bool value) =>
        element.SetValue(PressFeedbackProperty, value);

    public static bool GetPressFeedback(DependencyObject element) =>
        (bool)element.GetValue(PressFeedbackProperty);

    public static void SetSwitchMotion(DependencyObject element, bool value) =>
        element.SetValue(SwitchMotionProperty, value);

    public static bool GetSwitchMotion(DependencyObject element) =>
        (bool)element.GetValue(SwitchMotionProperty);

    public static void SetFadeTo(DependencyObject element, double value) =>
        element.SetValue(FadeToProperty, value);

    public static double GetFadeTo(DependencyObject element) =>
        (double)element.GetValue(FadeToProperty);

    public static void SetScaleTo(DependencyObject element, double value) =>
        element.SetValue(ScaleToProperty, value);

    public static double GetScaleTo(DependencyObject element) =>
        (double)element.GetValue(ScaleToProperty);

    public static void SetRotateTo(DependencyObject element, double value) =>
        element.SetValue(RotateToProperty, value);

    public static double GetRotateTo(DependencyObject element) =>
        (double)element.GetValue(RotateToProperty);

    public static void SetTransition(DependencyObject element, MotionSpeed value) =>
        element.SetValue(TransitionProperty, value);

    public static MotionSpeed GetTransition(DependencyObject element) =>
        (MotionSpeed)element.GetValue(TransitionProperty);

    /// <summary>Whether motion should run for the current Windows session.</summary>
    internal static bool AnimationsEnabled =>
        s_animationsEnabledOverrideForTest ?? SystemParameters.ClientAreaAnimation;

    /// <summary>Overrides the Windows motion preference on the current test thread only.</summary>
    internal static bool? AnimationsEnabledOverrideForTest
    {
        get => s_animationsEnabledOverrideForTest;
        set => s_animationsEnabledOverrideForTest = value;
    }

    internal static Duration FastDuration =>
        ResourceDuration("Motion.Fast", TimeSpan.FromMilliseconds(83));

    internal static Duration NormalDuration =>
        ResourceDuration("Motion.Normal", TimeSpan.FromMilliseconds(167));

    internal static Duration DeliberateDuration =>
        ResourceDuration("Motion.Deliberate", TimeSpan.FromMilliseconds(250));

    internal static IEasingFunction StandardEasing =>
        ResourceEasing("Motion.Ease.Standard", new CubicEase { EasingMode = EasingMode.EaseOut });

    internal static IEasingFunction SoftLandingEasing =>
        ResourceEasing("Motion.Ease.SoftLanding", new QuadraticEase { EasingMode = EasingMode.EaseOut });

    private static void OnWindowEntranceChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not Window window)
        {
            return;
        }

        window.IsVisibleChanged -= OnWindowIsVisibleChanged;
        if (e.NewValue is true)
        {
            window.IsVisibleChanged += OnWindowIsVisibleChanged;
            if (window.IsVisible)
            {
                QueueWindowEntrance(window);
            }
        }
        else
        {
            InvalidateQueuedWindowEntrance(window);
            ResetWindowEntranceTransform(window.Content as FrameworkElement);
        }
    }

    private static void OnWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        if (e.NewValue is true)
        {
            QueueWindowEntrance(window);
        }
        else
        {
            InvalidateQueuedWindowEntrance(window);
        }
    }

    private static void QueueWindowEntrance(Window window)
    {
        int request = unchecked((int)window.GetValue(WindowEntranceRequestProperty) + 1);
        window.SetValue(WindowEntranceRequestProperty, request);

        // Visibility can change before the content has its final render size. Queue one
        // render-priority turn so every reveal uses settled layout and cannot jump. The
        // generation check collapses a rapid Show/Hide/Show burst to its last visible state.
        _ = window.Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() =>
            {
                if (window.IsVisible
                    && (int)window.GetValue(WindowEntranceRequestProperty) == request)
                {
                    AnimateWindowEntrance(window);
                }
            }));
    }

    private static void InvalidateQueuedWindowEntrance(Window window) =>
        window.SetValue(
            WindowEntranceRequestProperty,
            unchecked((int)window.GetValue(WindowEntranceRequestProperty) + 1));

    private static void AnimateWindowEntrance(Window window)
    {
        if (!window.IsVisible || window.Content is not FrameworkElement surface)
        {
            return;
        }

        surface.BeginAnimation(UIElement.OpacityProperty, null);
        ResetWindowEntranceTransform(surface);
        if (!AnimationsEnabled)
        {
            return;
        }

        double targetOpacity = surface.Opacity;
        var fade = new DoubleAnimation(0, targetOpacity, NormalDuration)
        {
            EasingFunction = SoftLandingEasing,
            FillBehavior = FillBehavior.Stop,
        };
        surface.BeginAnimation(
            UIElement.OpacityProperty,
            fade,
            HandoffBehavior.SnapshotAndReplace);

        // Preserve any transform explicitly owned by the screen. A short vertical settle is
        // added only when the root had no local transform, then removed after completion.
        if (surface.ReadLocalValue(UIElement.RenderTransformProperty) != DependencyProperty.UnsetValue)
        {
            return;
        }

        var translate = new TranslateTransform();
        surface.RenderTransform = translate;
        surface.SetValue(WindowEntranceTransformProperty, translate);
        var settle = new DoubleAnimation(8, 0, NormalDuration)
        {
            EasingFunction = StandardEasing,
            FillBehavior = FillBehavior.Stop,
        };
        settle.Completed += (_, _) =>
        {
            if (ReferenceEquals(surface.GetValue(WindowEntranceTransformProperty), translate))
            {
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                surface.ClearValue(WindowEntranceTransformProperty);
                if (ReferenceEquals(surface.RenderTransform, translate))
                {
                    surface.ClearValue(UIElement.RenderTransformProperty);
                }
            }
        };
        translate.BeginAnimation(
            TranslateTransform.YProperty,
            settle,
            HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// Plays a short exit on a floating window's content, then runs <paramref name="close"/>.
    /// </summary>
    /// <remarks>
    /// The window stops taking input at once, so callers change their own state immediately and
    /// only the pixels linger. <paramref name="close"/> runs synchronously when Windows animation
    /// effects are off, the window is hidden, or it is not layered (an opaque window would fade
    /// its content into its own background instead of the desktop). A second request while the
    /// exit plays is ignored; <see cref="CompleteExit"/> finishes it early. After
    /// <paramref name="close"/> the content is restored, so a window that hides can show again.
    /// </remarks>
    internal static void BeginExit(Window window, ExitMotion motion, Action close, Duration? duration = null)
    {
        if (window.GetValue(ExitFinishProperty) is Action)
        {
            return;
        }

        if (!AnimationsEnabled || !window.IsVisible || !window.AllowsTransparency
            || window.Content is not FrameworkElement surface)
        {
            close();
            return;
        }

        InvalidateQueuedWindowEntrance(window);
        surface.BeginAnimation(UIElement.OpacityProperty, null);
        ResetWindowEntranceTransform(surface);

        Duration length = duration ?? FastDuration;
        bool hitTestable = window.IsHitTestVisible;
        window.IsHitTestVisible = false;

        // A screen-owned transform is kept; only an unowned root gets the drop, slide or shrink.
        Transform? owned = null;
        if (surface.ReadLocalValue(UIElement.RenderTransformProperty) == DependencyProperty.UnsetValue)
        {
            if (motion == ExitMotion.Shrink)
            {
                var scale = new ScaleTransform(1, 1);
                surface.RenderTransformOrigin = new Point(0.5, 0.5);
                var shrink = new DoubleAnimation(1, 0.97, length) { EasingFunction = StandardEasing };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
                owned = scale;
            }
            else
            {
                var shift = new TranslateTransform();
                var move = new DoubleAnimation(0, motion == ExitMotion.FadeRight ? 8 : 6, length) { EasingFunction = StandardEasing };
                shift.BeginAnimation(
                    motion == ExitMotion.FadeRight ? TranslateTransform.XProperty : TranslateTransform.YProperty,
                    move);
                owned = shift;
            }

            surface.RenderTransform = owned;
        }

        var fallback = new DispatcherTimer(DispatcherPriority.Normal, window.Dispatcher)
        {
            // Completed never fires while the window cannot render (locked session, minimised
            // owner); the close still happens shortly after the exit would have ended.
            Interval = length.TimeSpan + TimeSpan.FromMilliseconds(250),
        };
        bool finished = false;
        void Finish()
        {
            if (finished)
            {
                return;
            }

            finished = true;
            fallback.Stop();
            window.ClearValue(ExitFinishProperty);
            try
            {
                close();
            }
            finally
            {
                surface.BeginAnimation(UIElement.OpacityProperty, null);
                if (owned is not null && ReferenceEquals(surface.RenderTransform, owned))
                {
                    surface.ClearValue(UIElement.RenderTransformProperty);
                }

                window.IsHitTestVisible = hitTestable;
            }
        }

        window.SetValue(ExitFinishProperty, (Action)Finish);
        fallback.Tick += (_, _) => Finish();
        var fade = new DoubleAnimation(surface.Opacity, 0, length) { EasingFunction = StandardEasing };
        fade.Completed += (_, _) => Finish();
        surface.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        fallback.Start();
    }

    /// <summary>Whether <see cref="BeginExit"/> is playing on this window.</summary>
    internal static bool IsExiting(Window window) => window.GetValue(ExitFinishProperty) is Action;

    /// <summary>Ends a playing exit now (shutdown, a capture starting): its close runs at once.</summary>
    internal static void CompleteExit(Window window)
    {
        if (window.GetValue(ExitFinishProperty) is Action finish)
        {
            finish();
        }
    }

    private static void ResetWindowEntranceTransform(FrameworkElement? surface)
    {
        if (surface?.GetValue(WindowEntranceTransformProperty) is not TranslateTransform entranceTransform)
        {
            return;
        }

        entranceTransform.BeginAnimation(TranslateTransform.YProperty, null);
        surface.ClearValue(WindowEntranceTransformProperty);
        if (ReferenceEquals(surface.RenderTransform, entranceTransform))
        {
            surface.ClearValue(UIElement.RenderTransformProperty);
        }
    }

    private static void OnPressFeedbackChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not ButtonBase button)
        {
            return;
        }

        DetachPressHandlers(button);
        if (e.NewValue is true)
        {
            button.PreviewMouseLeftButtonDown += OnButtonPressed;
            button.PreviewMouseLeftButtonUp += OnButtonReleased;
            button.MouseLeave += OnButtonReleased;
            button.LostMouseCapture += OnButtonReleased;
            button.PreviewKeyDown += OnButtonKeyDown;
            button.PreviewKeyUp += OnButtonKeyUp;
            button.LostKeyboardFocus += OnButtonReleased;
            button.Unloaded += OnButtonReleased;
            button.IsEnabledChanged += OnButtonIsEnabledChanged;
        }
    }

    private static void DetachPressHandlers(ButtonBase button)
    {
        button.PreviewMouseLeftButtonDown -= OnButtonPressed;
        button.PreviewMouseLeftButtonUp -= OnButtonReleased;
        button.MouseLeave -= OnButtonReleased;
        button.LostMouseCapture -= OnButtonReleased;
        button.PreviewKeyDown -= OnButtonKeyDown;
        button.PreviewKeyUp -= OnButtonKeyUp;
        button.LostKeyboardFocus -= OnButtonReleased;
        button.Unloaded -= OnButtonReleased;
        button.IsEnabledChanged -= OnButtonIsEnabledChanged;
    }

    private static void OnButtonPressed(object sender, MouseButtonEventArgs e)
    {
        _ = e;
        if (sender is ButtonBase button && button.IsEnabled)
        {
            AnimatePress(button, pressed: true);
        }
    }

    private static void OnButtonReleased(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is ButtonBase button)
        {
            AnimatePress(button, pressed: false);
        }
    }

    private static void OnButtonKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is ButtonBase button && e.Key is Key.Space or Key.Enter)
        {
            AnimatePress(button, pressed: true);
        }
    }

    private static void OnButtonKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is ButtonBase button && e.Key is Key.Space or Key.Enter)
        {
            AnimatePress(button, pressed: false);
        }
    }

    private static void OnButtonIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is ButtonBase button && e.NewValue is false)
        {
            AnimatePress(button, pressed: false);
        }
    }

    private static void AnimatePress(ButtonBase button, bool pressed)
    {
        UIElement element = button.Template?.FindName(PressTargetName, button) as UIElement ?? button;
        ScaleTransform? scale = (ScaleTransform?)element.GetValue(PressTransformProperty);

        if (!AnimationsEnabled)
        {
            if (scale is not null)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                element.ClearValue(PressTransformProperty);
                if (ReferenceEquals(element.RenderTransform, scale))
                {
                    element.ClearValue(UIElement.RenderTransformProperty);
                }
            }

            return;
        }

        if (scale is null)
        {
            // A control that deliberately owns a transform keeps it; motion must never trample
            // component-specific rendering or replace a transform used for hit testing.
            if (element.ReadLocalValue(UIElement.RenderTransformProperty) != DependencyProperty.UnsetValue)
            {
                return;
            }

            scale = new ScaleTransform(1, 1);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = scale;
            element.SetValue(PressTransformProperty, scale);
        }

        double pressedScale = ReferenceEquals(element, button) ? PressedScale : PressedGlyphScale;
        double target = pressed ? pressedScale : 1.0;
        Duration duration = pressed ? FastDuration : NormalDuration;
        IEasingFunction easing = pressed ? StandardEasing : SoftLandingEasing;
        var x = new DoubleAnimation(scale.ScaleX, target, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop,
        };
        var y = new DoubleAnimation(scale.ScaleY, target, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop,
        };

        scale.ScaleX = target;
        scale.ScaleY = target;
        if (!pressed)
        {
            y.Completed += (_, _) =>
            {
                if (!button.IsPressed
                    && ReferenceEquals(element.GetValue(PressTransformProperty), scale)
                    && ReferenceEquals(element.RenderTransform, scale))
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    element.ClearValue(PressTransformProperty);
                    element.ClearValue(UIElement.RenderTransformProperty);
                }
            };
        }

        scale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            x,
            HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            y,
            HandoffBehavior.SnapshotAndReplace);
    }

    private static void OnSwitchMotionChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not ToggleButton toggle)
        {
            return;
        }

        toggle.Loaded -= OnSwitchLoaded;
        toggle.Checked -= OnSwitchToggled;
        toggle.Unchecked -= OnSwitchToggled;
        if (e.NewValue is true)
        {
            toggle.Loaded += OnSwitchLoaded;
            toggle.Checked += OnSwitchToggled;
            toggle.Unchecked += OnSwitchToggled;
            if (toggle.IsLoaded)
            {
                MoveSwitch(toggle, animate: false);
            }
        }
    }

    private static void OnSwitchLoaded(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is ToggleButton toggle)
        {
            MoveSwitch(toggle, animate: false);
        }
    }

    private static void OnSwitchToggled(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is ToggleButton toggle)
        {
            MoveSwitch(toggle, animate: toggle.IsLoaded);
        }
    }

    /// <summary>Places the thumb and fill for the current state, sliding when motion is on.</summary>
    internal static void MoveSwitch(ToggleButton toggle, bool animate)
    {
        toggle.ApplyTemplate();
        if (toggle.Template?.FindName("PART_SwitchThumb", toggle) is not FrameworkElement thumb)
        {
            return;
        }

        bool on = toggle.IsChecked == true;
        // Template transforms may be frozen; the thumb owns one it can animate.
        if (thumb.RenderTransform is not TranslateTransform shift || shift.IsFrozen)
        {
            shift = new TranslateTransform();
            thumb.RenderTransform = shift;
        }

        double travel = toggle.Template.FindName("PART_SwitchTrack", toggle) is FrameworkElement track
            && track.ActualWidth > 0 && thumb.ActualWidth > 0
                ? track.ActualWidth - thumb.ActualWidth - (2 * thumb.Margin.Left)
                : 16;
        Animate(shift, TranslateTransform.XProperty, on ? travel : 0, animate);
        if (toggle.Template.FindName("PART_SwitchFill", toggle) is UIElement fill)
        {
            Animate(fill, UIElement.OpacityProperty, on ? 1 : 0, animate);
        }

        if (toggle.Template.FindName("PART_SwitchThumbOn", toggle) is UIElement thumbOn)
        {
            Animate(thumbOn, UIElement.OpacityProperty, on ? 1 : 0, animate);
        }
    }

    private static void Animate(IAnimatable target, DependencyProperty property, double to, bool animate) =>
        Animate(target, property, to, animate, NormalDuration);

    private static void Animate(IAnimatable target, DependencyProperty property, double to, bool animate,
        Duration duration)
    {
        var owner = (DependencyObject)target;
        double from = (double)owner.GetValue(property);
        target.BeginAnimation(property, null);
        owner.SetValue(property, to);
        if (!animate || !AnimationsEnabled || from.Equals(to))
        {
            return;
        }

        var slide = new DoubleAnimation(from, to, duration)
        {
            EasingFunction = StandardEasing,
            FillBehavior = FillBehavior.Stop,
        };
        target.BeginAnimation(property, slide, HandoffBehavior.SnapshotAndReplace);
    }

    private static void OnFadeToChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is UIElement element)
        {
            if (element is FrameworkElement layer)
            {
                layer.Unloaded -= OnStateLayerUnloaded;
                layer.Unloaded += OnStateLayerUnloaded;
            }
            Animate(element, UIElement.OpacityProperty, (double)e.NewValue, IsShown(element),
                TransitionDuration(element));
        }
    }

    private static void OnScaleToChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not UIElement element)
        {
            return;
        }

        // Template transforms may be frozen or shared; the element owns one it can animate.
        if (element.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
        {
            scale = new ScaleTransform(1, 1);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = scale;
        }

        double to = (double)e.NewValue;
        bool animate = IsShown(element);
        Duration duration = TransitionDuration(element);
        Animate(scale, ScaleTransform.ScaleXProperty, to, animate, duration);
        Animate(scale, ScaleTransform.ScaleYProperty, to, animate, duration);
    }

    private static void OnRotateToChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not UIElement element)
        {
            return;
        }

        // Even a mutable transform can be shared by two template consumers. Only reuse
        // the transform created for this element; clone an existing rotation otherwise.
        var rotate = (RotateTransform?)element.GetValue(RotationTransformProperty);
        if (rotate is null || !ReferenceEquals(element.RenderTransform, rotate))
        {
            rotate = element.RenderTransform is RotateTransform existing
                ? existing.CloneCurrentValue()
                : new RotateTransform();
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            element.RenderTransform = rotate;
            element.SetValue(RotationTransformProperty, rotate);
        }

        if (element is FrameworkElement part)
        {
            part.Unloaded -= OnRotationUnloaded;
            part.Unloaded += OnRotationUnloaded;
        }
        Animate(rotate, RotateTransform.AngleProperty, (double)e.NewValue, IsShown(element), TransitionDuration(element));
    }

    private static void OnStateLayerUnloaded(object sender, RoutedEventArgs e)
    {
        var layer = (FrameworkElement)sender;
        Animate(layer, UIElement.OpacityProperty, GetFadeTo(layer), animate: false);
    }

    private static void OnRotationUnloaded(object sender, RoutedEventArgs e)
    {
        var part = (FrameworkElement)sender;
        if (part.GetValue(RotationTransformProperty) is RotateTransform rotate)
            Animate(rotate, RotateTransform.AngleProperty, GetRotateTo(part), animate: false);
    }

    // A layer that is still being templated takes its first state at once.
    private static bool IsShown(UIElement element) => element is FrameworkElement { IsLoaded: true };

    private static Duration TransitionDuration(DependencyObject element) => GetTransition(element) switch
    {
        MotionSpeed.Normal => NormalDuration,
        MotionSpeed.Deliberate => DeliberateDuration,
        _ => FastDuration,
    };

    private static Duration ResourceDuration(string key, TimeSpan fallback) =>
        Application.Current?.TryFindResource(key) is Duration duration
            ? duration
            : new Duration(fallback);

    private static IEasingFunction ResourceEasing(string key, IEasingFunction fallback) =>
        Application.Current?.TryFindResource(key) as IEasingFunction ?? fallback;
}
