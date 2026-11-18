using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace MyCapture.App.Themes;

/// <summary>Indeterminate paint motion does not own or change ProgressBar's value or timing.</summary>
public sealed class ProgressIndicator : Border
{
    public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
        nameof(IsIndeterminate), typeof(bool), typeof(ProgressIndicator), new PropertyMetadata(false, OnStateChanged));

    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    internal bool IsPulsing { get; private set; }

    public ProgressIndicator()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => RefreshMotion();
    }

    private static void OnStateChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args) =>
        ((ProgressIndicator)owner).RefreshMotion();

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        SystemParameters.StaticPropertyChanged -= OnSystemPropertyChanged;
        SystemParameters.StaticPropertyChanged += OnSystemPropertyChanged;
        RefreshMotion();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        SystemParameters.StaticPropertyChanged -= OnSystemPropertyChanged;
        StopMotion();
    }

    private void OnSystemPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(SystemParameters.ClientAreaAnimation)) return;
        if (Dispatcher.CheckAccess()) RefreshMotion();
        else Dispatcher.BeginInvoke(new Action(RefreshMotion));
    }

    internal void RefreshMotion()
    {
        StopMotion();
        if (!IsIndeterminate || !IsLoaded || !IsVisible || !FluidMotion.AnimationsEnabled) return;
        BeginAnimation(OpacityProperty, new DoubleAnimation(.35, 1, FluidMotion.DeliberateDuration)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = TryFindResource("Motion.Ease.Standard") as IEasingFunction
                ?? new CubicEase { EasingMode = EasingMode.EaseOut },
        }, HandoffBehavior.SnapshotAndReplace);
        IsPulsing = true;
    }

    private void StopMotion()
    {
        BeginAnimation(OpacityProperty, null);
        SetCurrentValue(OpacityProperty, 1d);
        IsPulsing = false;
    }
}
