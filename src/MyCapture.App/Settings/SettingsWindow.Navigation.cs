using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace MyCapture.App.Settings;

internal sealed partial class SettingsWindow
{
    private long _navigationGeneration;
    private ISettingsGuidePresenter _guidePresenter = SettingsGuidePresenter.Instance;

    internal ISettingsGuidePresenter GuidePresenter
    {
        get => _guidePresenter;
        set
        {
            _guidePresenter = value ?? throw new ArgumentNullException(nameof(value));
            RefreshGuideAvailability();
        }
    }

    internal void ShowSetting(string draftProperty)
    {
        ShowSettings();
        NavigateToSetting(draftProperty);
    }

    internal void ShowSection(SettingsSection section)
    {
        ShowSettings();
        NavigateToSection(section);
    }

    /// <summary>Changes only navigation: the selected category and the current draft are retained by the window.</summary>
    internal bool NavigateToSection(SettingsSection section)
    {
        SettingsSectionDefinition? definition = SettingsSectionCatalog.Sections.FirstOrDefault(item => item.Section == section);
        if (definition is null || FindName(definition.TabName) is not TabItem tab) return false;
        ClearSearchForNavigation();
        ++_navigationGeneration;
        CategoryTabs.SelectedItem = tab;
        tab.BringIntoView();
        return true;
    }

    /// <summary>Reveals the existing field, including a folded group, without rebuilding or saving the draft.</summary>
    internal bool NavigateToSetting(string draftProperty) => RevealSetting(draftProperty, moveFocus: true);

    internal FrameworkElement? FindSettingControl(string draftProperty)
    {
        SettingsFieldDefinition? field = SettingsSectionCatalog.Find(draftProperty);
        if (field is null) return null;
        if (field.ElementName is { } name) return FindName(name) as FrameworkElement;
        return LogicalDescendants(CategoryTabs).OfType<FrameworkElement>().FirstOrDefault(element =>
        {
            LocalValueEnumerator values = element.GetLocalValueEnumerator();
            while (values.MoveNext())
                if (BindingOperations.GetBinding(element, values.Current.Property)?.Path?.Path == draftProperty)
                    return true;
            return false;
        });
    }

    private void ClearSearchForNavigation()
    {
        CategoryTabs.ApplyTemplate();
        if (CategoryTabs.Template.FindName("SettingsSearchBox", CategoryTabs) is TextBox search) search.Clear();
        FilterSettings(string.Empty);
    }

    private bool RevealSetting(string draftProperty, bool moveFocus)
    {
        if (FindSettingControl(draftProperty) is not { } control) return false;
        FrameworkElement target = VisibleSearchTarget(control);
        ClearSearchForNavigation();
        if (LogicalAncestor<TabItem>(target) is not { } tab) return false;
        CategoryTabs.SelectedItem = tab;
        for (DependencyObject? current = target; current is not null; current = LogicalTreeHelper.GetParent(current))
            if (current is Expander expander) expander.IsExpanded = true;
        long generation = ++_navigationGeneration;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (generation != _navigationGeneration || !ReferenceEquals(CategoryTabs.SelectedItem, tab)) return;
            tab.BringIntoView();
            target.BringIntoView();
            if (!moveFocus) return;
            FrameworkElement focusTarget = target is HotkeyRecorder recorder
                ? LogicalDescendants(recorder).OfType<Button>().FirstOrDefault() ?? target
                : target;
            if (!focusTarget.IsVisible || !focusTarget.IsEnabled) return;
            _ = focusTarget.Focus();
            _ = Keyboard.Focus(focusTarget);
        }));
        return true;
    }

    private void RevealSettingError(string? draftProperty)
    {
        if (!_searchReady || string.IsNullOrEmpty(draftProperty)) return;
        if (_draft.GetErrors(draftProperty).Cast<string>().Any()) RevealSetting(draftProperty, moveFocus: false);
    }

    private bool NavigateToFirstError()
    {
        string? property = SettingsSectionCatalog.Fields.Select(field => field.DraftProperty)
            .FirstOrDefault(name => _draft.GetErrors(name).Cast<string>().Any());
        return property is not null && NavigateToSetting(property);
    }

    private IEnumerable<Button> GuideButtons() => LogicalDescendants(CategoryTabs).OfType<Button>()
        .Where(button => button.Tag is string id && SettingsSectionCatalog.Fields.Any(field => field.GuideId == id));

    private void InitializeSettingsGuides()
    {
        foreach (Button button in GuideButtons())
        {
            if (LogicalTreeHelper.GetParent(button) is not FrameworkElement row) continue;
            string? label = LogicalDescendants(row).OfType<FrameworkElement>()
                .Where(element => !ReferenceEquals(element, button))
                .SelectMany(SearchText).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
            if (label is not null) AutomationProperties.SetName(button, $"{label} · {button.Content}");
        }
        RefreshGuideAvailability();
    }

    private void RefreshGuideAvailability()
    {
        foreach (Button button in GuideButtons()) button.IsEnabled = _guidePresenter.IsAvailable;
    }

    private void OnSettingsGuide(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } && _guidePresenter.IsAvailable)
            _guidePresenter.Show(id, this);
    }
}
