using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MyCapture.App.Themes;

namespace MyCapture.App.Settings;

internal sealed partial class SettingsWindow
{
    private void InitializeSettingsMotion()
    {
        CategoryTabs.SelectionChanged += OnSettingsCategoryChanged;
        foreach (DependencyObject element in LogicalDescendants(CategoryTabs))
        {
            if (element is Expander expander)
            {
                expander.Expanded += OnSettingsGroupExpanded;
                expander.Collapsed += OnSettingsGroupCollapsed;
            }
            if (element is ComboBox combo)
            {
                combo.DropDownOpened += OnSettingsComboOpened;
                combo.DropDownClosed += OnSettingsComboClosed;
            }
        }
    }

    private void OnSettingsCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, CategoryTabs)) return;
        foreach (object item in e.RemovedItems)
            if (item is TabItem { Content: UIElement previous }) ResetSettingsReveal(previous);
        if (CategoryTabs.SelectedContent is UIElement page)
            RevealSettingsSurface(page, FluidMotion.DeliberateDuration, IsLoaded);
    }

    private void OnSettingsGroupExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander || !ReferenceEquals(e.Source, expander)) return;
        expander.ApplyTemplate();
        if (expander.Template.FindName("ExpandSite", expander) is UIElement content)
            RevealSettingsSurface(content, FluidMotion.NormalDuration, expander.IsLoaded);
    }

    private void OnSettingsGroupCollapsed(object sender, RoutedEventArgs e)
    {
        if (sender is Expander expander && ReferenceEquals(e.Source, expander)
            && expander.Template.FindName("ExpandSite", expander) is UIElement content)
            ResetSettingsReveal(content);
    }

    private void OnSettingsComboOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox combo && combo.Template.FindName("PART_Popup", combo) is Popup { Child: UIElement content })
            RevealSettingsSurface(content, FluidMotion.FastDuration, combo.IsLoaded);
    }

    private void OnSettingsComboClosed(object? sender, EventArgs e)
    {
        if (sender is ComboBox combo && combo.Template.FindName("PART_Popup", combo) is Popup { Child: UIElement content })
            ResetSettingsReveal(content);
    }

    private static void RevealSettingsSurface(UIElement surface, Duration duration, bool animate)
    {
        ResetSettingsReveal(surface);
        if (!animate || !FluidMotion.AnimationsEnabled) return;
        // The new page, expanded rows and popup hit targets already occupy their final bounds.
        surface.SetCurrentValue(OpacityProperty, 0d);
        FluidMotion.Animate(surface, OpacityProperty, 1, animate: true, duration);
    }

    private static void ResetSettingsReveal(UIElement surface)
    {
        surface.BeginAnimation(OpacityProperty, null);
        surface.SetCurrentValue(OpacityProperty, 1d);
    }
}
