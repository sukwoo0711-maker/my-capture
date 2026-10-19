using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Hover, press and selection fade through overlay layers instead of jumping between setters,
/// commit their state at once, and snap when Windows animation effects are off. A check box or
/// radio button presses its glyph, never its label.
/// </summary>
public sealed class ControlStateMotionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CheckingFadesTheIndicatorOnlyWhenMotionIsOn(bool motion) => Run(motion, (theme, host) =>
    {
        var check = new CheckBox { Content = "Option", Style = (Style)theme[typeof(CheckBox)] };
        var radio = new RadioButton { Content = "Choice", Style = (Style)theme[typeof(RadioButton)] };
        var toggle = new ToggleButton { Content = "Tool", Style = (Style)theme[typeof(ToggleButton)] };
        host.Children.Add(check);
        host.Children.Add(radio);
        host.Children.Add(toggle);
        Drain(host);

        check.IsChecked = true;
        radio.IsChecked = true;
        toggle.IsChecked = true;

        foreach (Control control in new Control[] { check, radio, toggle })
        {
            var layer = (UIElement)control.Template.FindName("CheckedLayer", control);
            // The checked state is committed at once; only its picture fades in.
            Assert.Equal(1, (double)layer.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.Equal(motion, layer.HasAnimatedProperties);
        }

        var mark = (UIElement)check.Template.FindName("Check", check);
        var scale = Assert.IsType<ScaleTransform>(mark.RenderTransform);
        Assert.Equal(1, (double)scale.GetAnimationBaseValue(ScaleTransform.ScaleXProperty));
        Assert.Equal(motion, scale.HasAnimatedProperties);
    });

    [Fact]
    public void LayersTakeTheirFirstStateWithoutAnimating() => Run(true, (theme, host) =>
    {
        // A control that is already checked when it appears shows the result, not a fade.
        var check = new CheckBox { Content = "Option", IsChecked = true, Style = (Style)theme[typeof(CheckBox)] };
        host.Children.Add(check);
        Drain(host);
        var layer = (UIElement)check.Template.FindName("CheckedLayer", check);
        Assert.Equal(1, layer.Opacity);
        Assert.False(layer.HasAnimatedProperties);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HoverLayerFadesOverTheFastToken(bool motion) => Run(motion, (theme, host) =>
    {
        var button = new Button { Content = "Save", Style = (Style)theme[typeof(Button)] };
        host.Children.Add(button);
        Drain(host);
        var layer = (Border)button.Template.FindName("HoverLayer", button);

        FluidMotion.SetFadeTo(layer, 1);

        Assert.Equal(1, (double)layer.GetAnimationBaseValue(UIElement.OpacityProperty));
        Assert.Equal(motion, layer.HasAnimatedProperties);

        // Hover and press use Motion.Fast (83ms); selection indicators use Motion.Normal (167ms).
        var toggle = new ToggleButton { Content = "Tool", Style = (Style)theme[typeof(ToggleButton)] };
        host.Children.Add(toggle);
        Drain(host);
        Assert.Equal(MotionSpeed.Fast, FluidMotion.GetTransition(layer));
        Assert.Equal(MotionSpeed.Fast, FluidMotion.GetTransition((DependencyObject)button.Template.FindName("PressedLayer", button)));
        Assert.Equal(MotionSpeed.Normal, FluidMotion.GetTransition((DependencyObject)toggle.Template.FindName("CheckedLayer", toggle)));
    });

    [Fact]
    public void TemplatesDriveStateLayersFromTriggersInsteadOfSwappingBrushes() => StaTestHost.Run(() =>
    {
        ResourceDictionary theme = SystemHighContrastThemeTests.LoadTheme();
        (Type Control, string Style, string Property, string Layer)[] wiring =
        [
            (typeof(Button), "", "IsMouseOver", "HoverLayer"),
            (typeof(Button), "", "IsPressed", "PressedLayer"),
            (typeof(Button), "Button.Primary", "IsMouseOver", "HoverLayer"),
            (typeof(Button), "Button.Ghost", "IsMouseOver", "HoverLayer"),
            (typeof(ToggleButton), "", "IsMouseOver", "HoverLayer"),
            (typeof(ToggleButton), "", "IsChecked", "CheckedLayer"),
            (typeof(ToggleButton), "Rail.ToolButton", "IsMouseOver", "HoverLayer"),
            (typeof(ToggleButton), "Rail.ToolButton", "IsPressed", "PressedLayer"),
            (typeof(ToggleButton), "Rail.ToolButton", "IsChecked", "CheckedLayer"),
            (typeof(CheckBox), "", "IsMouseOver", "HoverLayer"),
            (typeof(CheckBox), "", "IsChecked", "CheckedLayer"),
            (typeof(RadioButton), "", "IsMouseOver", "HoverRing"),
            (typeof(RadioButton), "", "IsChecked", "CheckedLayer"),
            (typeof(TextBox), "", "IsMouseOver", "HoverLayer"),
            (typeof(ComboBox), "", "IsMouseOver", "HoverLayer"),
            (typeof(ComboBoxItem), "", "IsHighlighted", "HoverLayer"),
            (typeof(ComboBoxItem), "", "IsSelected", "SelectedLayer"),
            (typeof(MenuItem), "", "IsHighlighted", "HoverLayer"),
        ];

        foreach ((Type control, string styleKey, string property, string layer) in wiring)
        {
            var style = (Style)theme[styleKey.Length == 0 ? control : styleKey];
            ControlTemplate template = TemplateOf(style);
            Trigger trigger = Assert.Single(template.Triggers.OfType<Trigger>(),
                candidate => candidate.Property.Name == property && Equals(candidate.Value, true)
                    && candidate.Setters.OfType<Setter>().Any(setter => setter.TargetName == layer));
            Setter fade = Assert.Single(trigger.Setters.OfType<Setter>(), setter => setter.TargetName == layer
                && (setter.Property == FluidMotion.FadeToProperty));
            Assert.Equal(1d, Convert.ToDouble(fade.Value, System.Globalization.CultureInfo.InvariantCulture));
            // No trigger recolours a template part with a hover or selection brush any more.
            Assert.DoesNotContain(template.Triggers.OfType<Trigger>().Where(t => t.Property.Name is "IsMouseOver" or "IsHighlighted")
                .SelectMany(t => t.Setters.OfType<Setter>()), setter => setter.Property == Border.BackgroundProperty);
        }
    });

    [Fact]
    public void MotionTemplatesAlsoLoadAsLooseXaml() => StaTestHost.Run(() =>
    {
        // Fixtures parse the theme dictionaries at run time and rewrite only the root's
        // namespace declarations, so template parts set motion through styles, not attributes.
        var window = new Window();
        Ocr410Fixture.LoadTheme(window);
        var check = new CheckBox { IsChecked = true };
        var cleared = new CheckBox();
        var radio = new RadioButton { IsChecked = true };
        var rail = new ToggleButton { IsChecked = true, Style = (Style)window.Resources["Rail.ToolButton"] };
        var slider = new Slider();
        Control[] controls = [check, cleared, radio, rail, slider];
        var host = new StackPanel();
        foreach (Control control in controls) host.Children.Add(control);
        window.Content = host;
        foreach (Control control in controls) Assert.True(control.ApplyTemplate());

        foreach (Control control in new Control[] { check, radio, rail })
        {
            var layer = (DependencyObject)control.Template.FindName("CheckedLayer", control);
            Assert.Equal(MotionSpeed.Normal, FluidMotion.GetTransition(layer));
        }

        // The selected tool is its filled tile; the owner asked to remove the detached bar.
        Assert.Null(rail.Template.FindName("Indicator", rail));
        var railSelection = Assert.IsType<Border>(rail.Template.FindName("CheckedLayer", rail));
        Assert.Equal(1, railSelection.Opacity);
        Assert.Same(window.FindResource("Surface.Selected"), railSelection.Background);
        Assert.Same(window.FindResource("Text.Primary"), rail.Foreground);
        Assert.Equal(1, FluidMotion.GetScaleTo((DependencyObject)check.Template.FindName("Check", check)));
        Assert.Equal(0.5, FluidMotion.GetScaleTo((DependencyObject)cleared.Template.FindName("Check", cleared)));
        Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RailSelectionFadesItsFilledTileWithoutChangingSharedBrushes(bool motion) => Run(motion, (theme, host) =>
    {
        var rail = new ToggleButton { Content = "Tool", Style = (Style)theme["Rail.ToolButton"] };
        host.Children.Add(rail);
        Drain(host);
        var layer = Assert.IsType<Border>(rail.Template.FindName("CheckedLayer", rail));
        var accent = Assert.IsType<SolidColorBrush>(theme["Surface.Selected"]);
        var ink = Assert.IsType<SolidColorBrush>(theme["Text.Primary"]);
        Color accentColor = accent.Color;
        Color inkColor = ink.Color;
        Assert.Null(rail.Template.FindName("Indicator", rail));
        Assert.Same(accent, layer.Background);
        Assert.Equal(MotionSpeed.Normal, FluidMotion.GetTransition(layer));

        foreach (bool selected in new[] { true, false })
        {
            rail.IsChecked = selected;

            Assert.Equal(selected ? 1d : 0d, (double)layer.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.Equal(motion, layer.HasAnimatedProperties);
            Assert.Same(accent, layer.Background);
            Assert.Equal(accentColor, accent.Color);
            Assert.Equal(inkColor, ink.Color);
            Assert.False(accent.HasAnimatedProperties);
            Assert.False(ink.HasAnimatedProperties);
            Assert.Same(selected ? ink : theme["Text.Muted"], rail.Foreground);
        }
    });

    [Theory]
    [InlineData(typeof(CheckBox))]
    [InlineData(typeof(RadioButton))]
    public void PressScalesTheGlyphAndLeavesTheLabelStill(Type type) => Run(true, (theme, host) =>
    {
        var control = (ToggleButton)Activator.CreateInstance(type)!;
        control.Content = "A sentence that must not shrink";
        control.Style = (Style)theme[type];
        host.Children.Add(control);
        Drain(host);
        var glyph = (UIElement)control.Template.FindName(FluidMotion.PressTargetName, control);

        control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
        });

        var scale = Assert.IsType<ScaleTransform>(glyph.RenderTransform);
        Assert.Equal(0.9, (double)scale.GetAnimationBaseValue(ScaleTransform.ScaleXProperty), 3);
        Assert.Equal(DependencyProperty.UnsetValue, control.ReadLocalValue(UIElement.RenderTransformProperty));
        Assert.True(control.RenderTransform is null || control.RenderTransform.Value.IsIdentity);

        control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
        });
        Assert.Equal(1, (double)scale.GetAnimationBaseValue(ScaleTransform.ScaleXProperty));
    });

    [Fact]
    public void ReducedMotionLeavesNoPressTransform() => Run(false, (theme, host) =>
    {
        var check = new CheckBox { Content = "Option", Style = (Style)theme[typeof(CheckBox)] };
        host.Children.Add(check);
        Drain(host);
        var glyph = (UIElement)check.Template.FindName(FluidMotion.PressTargetName, check);
        check.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
        });
        Assert.Equal(DependencyProperty.UnsetValue, glyph.ReadLocalValue(UIElement.RenderTransformProperty));
    });

    private static ControlTemplate TemplateOf(Style style)
    {
        for (Style? current = style; current is not null; current = current.BasedOn)
        {
            if (current.Setters.OfType<Setter>().FirstOrDefault(setter => setter.Property == Control.TemplateProperty) is { } setter)
                return Assert.IsType<ControlTemplate>(setter.Value);
        }

        throw new Xunit.Sdk.XunitException("The style sets no template.");
    }

    internal static void Run(bool motion, Action<ResourceDictionary, StackPanel> body) => StaTestHost.Run(() =>
    {
        bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
        bool? previousContrast = ThemeService.SystemHighContrastOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = motion;
        ThemeService.SystemHighContrastOverrideForTest = false;
        ResourceDictionary theme = SystemHighContrastThemeTests.LoadTheme();
        ThemeService.ApplyResources(MyCapture.Core.Themes.AppTheme.Midnight, theme, systemHighContrast: false);
        var host = new StackPanel { Resources = theme, Width = 240 };
        var window = new Window
        {
            Content = host, Width = 260, Height = 300, Left = -32000, Top = -32000,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        try
        {
            window.Show();
            body(theme, host);
        }
        finally
        {
            window.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = previous;
            ThemeService.SystemHighContrastOverrideForTest = previousContrast;
        }
    });

    internal static void Drain(DispatcherObject owner) =>
        owner.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(static () => { }));
}
