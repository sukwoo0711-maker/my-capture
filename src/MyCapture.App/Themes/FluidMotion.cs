using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MyCapture.App.Themes;

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

    private static readonly DependencyProperty PressTransformProperty =
        DependencyProperty.RegisterAttached(
            "PressTransform",
            typeof(ScaleTransform),
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

    [ThreadStatic]
    private static bool? s_animationsEnabledOverrideForTest;

    private const double PressedScale = 0.985;

    public static void SetWindowEntrance(DependencyObject element, bool value) =>
        element.SetValue(WindowEntranceProperty, value);

    public static bool GetWindowEntrance(DependencyObject element) =>
        (bool)element.GetValue(WindowEntranceProperty);

    public static void SetPressFeedback(DependencyObject element, bool value) =>
        element.SetValue(PressFeedbackProperty, value);

    public static bool GetPressFeedback(DependencyObject element) =>
        (bool)element.GetValue(PressFeedbackProperty);

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
        ScaleTransform? scale = (ScaleTransform?)button.GetValue(PressTransformProperty);

        if (!AnimationsEnabled)
        {
            if (scale is not null)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                button.ClearValue(PressTransformProperty);
                if (ReferenceEquals(button.RenderTransform, scale))
                {
                    button.ClearValue(UIElement.RenderTransformProperty);
                }
            }

            return;
        }

        if (scale is null)
        {
            // A control that deliberately owns a transform keeps it; motion must never trample
            // component-specific rendering or replace a transform used for hit testing.
            if (button.ReadLocalValue(UIElement.RenderTransformProperty) != DependencyProperty.UnsetValue)
            {
                return;
            }

            scale = new ScaleTransform(1, 1);
            button.RenderTransformOrigin = new Point(0.5, 0.5);
            button.RenderTransform = scale;
            button.SetValue(PressTransformProperty, scale);
        }

        double target = pressed ? PressedScale : 1.0;
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
                    && ReferenceEquals(button.GetValue(PressTransformProperty), scale)
                    && ReferenceEquals(button.RenderTransform, scale))
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    button.ClearValue(PressTransformProperty);
                    button.ClearValue(UIElement.RenderTransformProperty);
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

    private static Duration ResourceDuration(string key, TimeSpan fallback) =>
        Application.Current?.TryFindResource(key) is Duration duration
            ? duration
            : new Duration(fallback);

    private static IEasingFunction ResourceEasing(string key, IEasingFunction fallback) =>
        Application.Current?.TryFindResource(key) as IEasingFunction ?? fallback;
}
