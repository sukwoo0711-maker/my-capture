using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Globalization;

namespace MyCapture.App.Settings;

internal sealed partial class SettingsWindow
{
    private readonly Dictionary<TabItem, FrameworkElement> _searchMatches = [];
    private SearchMatchAdorner? _searchHighlight;
    private long _searchGeneration;
    private bool _searchReady;
    private string _searchQuery = string.Empty;
    internal string SearchQuery => _searchQuery;
    internal FrameworkElement? FirstSearchMatch { get; private set; }

    private void InitializeSearch()
    {
        _searchReady = true;
        CategoryTabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, CategoryTabs)) RevealSearchMatch();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _searchQuery.Length == 0 || HotkeyRecorder.Active is not null) return;
            if (CategoryTabs.Template.FindName("SettingsSearchBox", CategoryTabs) is TextBox search) search.Clear();
            FilterSettings(string.Empty);
            e.Handled = true;
        };
    }

    private void OnSettingsSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchReady && sender is TextBox search) FilterSettings(search.Text);
    }

    internal void FilterSettings(string query)
    {
        _searchQuery = query.Trim();
        _searchMatches.Clear();
        foreach (TabItem tab in CategoryTabs.Items)
        {
            FrameworkElement? match = LogicalDescendants(tab).OfType<FrameworkElement>()
                .Where(element => element is not ComboBoxItem && LogicalAncestor<ComboBoxItem>(element) is null)
                .FirstOrDefault(element => SearchText(element).Any(MatchesQuery));
            bool visible = _searchQuery.Length == 0 || match is not null
                || SearchText(tab).Any(MatchesQuery);
            tab.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible && _searchQuery.Length > 0 && (match ?? tab.Content as FrameworkElement) is { } target)
                _searchMatches[tab] = VisibleSearchTarget(target);
        }
        if (CategoryTabs.SelectedItem is not TabItem selected || selected.Visibility != Visibility.Visible)
            CategoryTabs.SelectedItem = CategoryTabs.Items.OfType<TabItem>().FirstOrDefault(tab => tab.Visibility == Visibility.Visible);
        if (CategoryTabs.Template.FindName("SettingsSearchEmpty", CategoryTabs) is TextBlock empty)
            empty.Visibility = CategoryTabs.SelectedItem is null ? Visibility.Visible : Visibility.Collapsed;
        RevealSearchMatch();
    }

    private bool MatchesQuery(string text) => UiText.Culture.CompareInfo.IndexOf(text, _searchQuery, CompareOptions.IgnoreCase) >= 0;

    private FrameworkElement VisibleSearchTarget(FrameworkElement target)
    {
        // Inspect local Visibility, not IsVisible: every unselected tab is currently invisible.
        // The collapsed HEX editor points to its prerequisite choice without changing the setting.
        for (DependencyObject? node = target; node is FrameworkElement element && node is not TabItem;
            node = LogicalTreeHelper.GetParent(node))
        {
            if (element.Visibility == Visibility.Visible) continue;
            if (LogicalDescendants(element).Contains(CaptureGuideColorHexBox)) return CaptureGuideColorModeSelector;
            if (LogicalTreeHelper.GetParent(element) is FrameworkElement parent) return parent;
        }
        return target;
    }

    private static IEnumerable<string> SearchText(FrameworkElement element)
    {
        // Only labels and help are indexed. Never index user text, tokens, paths or selected values.
        yield return AutomationProperties.GetName(element);
        yield return AutomationProperties.GetHelpText(element);
        if (element is TextBlock label) yield return label.Text;
        if (element is HeaderedContentControl { Header: string header }) yield return header;
        if (element is ContentControl { Content: string caption }) yield return caption;
        if (element is SettingsField field)
        {
            if (field.Hint is string hint) yield return hint;
            if (field.Note is string note) yield return note;
        }
    }

    private void RevealSearchMatch()
    {
        long generation = ++_searchGeneration;
        if (_searchHighlight is { } previous)
            AdornerLayer.GetAdornerLayer(previous.AdornedElement)?.Remove(previous);
        _searchHighlight = null;
        FirstSearchMatch = null;
        if (CategoryTabs.SelectedItem is not TabItem selected || !_searchMatches.TryGetValue(selected, out FrameworkElement? target)) return;
        FirstSearchMatch = target;
        for (DependencyObject? node = target; node is not null; node = LogicalTreeHelper.GetParent(node))
            if (node is Expander expander) expander.IsExpanded = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (generation != _searchGeneration || !ReferenceEquals(FirstSearchMatch, target)) return;
            target.BringIntoView();
            if (AdornerLayer.GetAdornerLayer(target) is not { } layer) return;
            _searchHighlight = new SearchMatchAdorner(target);
            layer.Add(_searchHighlight);
        }));
    }

    private sealed class SearchMatchAdorner : Adorner
    {
        private readonly FrameworkElement _target;
        internal SearchMatchAdorner(FrameworkElement target) : base(target)
        {
            _target = target;
            IsHitTestVisible = false;
        }
        protected override void OnRender(DrawingContext drawingContext)
        {
            var brush = (Brush)_target.FindResource("Text.Primary");
            drawingContext.DrawRoundedRectangle(null, new Pen(brush, 2), new Rect(_target.RenderSize), 3, 3);
        }
    }
}
