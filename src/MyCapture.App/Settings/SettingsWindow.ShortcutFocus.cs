using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace MyCapture.App.Settings;

internal sealed partial class SettingsWindow
{
    /// <summary>
    /// Shows the window on the Shortcuts tab with keyboard focus on the recorder that edits
    /// <paramref name="draftProperty"/> (a <see cref="MyCapture.Core.Settings.SettingsDraft"/>
    /// hotkey property), scrolled into view. An unknown property just shows the window.
    /// </summary>
    internal void ShowShortcut(string draftProperty)
    {
        ShowSettings();
        if (CategoryTabs.Template.FindName("SettingsSearchBox", CategoryTabs) is TextBox search) search.Clear();
        FilterSettings(string.Empty);
        if (FindHotkeyRecorder(draftProperty) is not { } recorder) return;
        if (LogicalAncestor<TabItem>(recorder) is { } tab) CategoryTabs.SelectedItem = tab;
        // The tab's content is laid out only once it is selected.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            recorder.BringIntoView();
            if (LogicalDescendants(recorder).OfType<Button>().FirstOrDefault() is { } record)
            {
                _ = record.Focus();
                _ = Keyboard.Focus(record);
            }
        }));
    }

    /// <summary>The shortcut recorder bound to <paramref name="draftProperty"/>, on any tab.</summary>
    internal HotkeyRecorder? FindHotkeyRecorder(string draftProperty) =>
        LogicalDescendants(CategoryTabs).OfType<HotkeyRecorder>().FirstOrDefault(recorder =>
            BindingOperations.GetBinding(recorder, HotkeyRecorder.TextProperty)?.Path?.Path == draftProperty);

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            yield return node;
            foreach (DependencyObject descendant in LogicalDescendants(node)) yield return descendant;
        }
    }

    private static T? LogicalAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (DependencyObject? current = LogicalTreeHelper.GetParent(element); current is not null;
            current = LogicalTreeHelper.GetParent(current))
        {
            if (current is T match) return match;
        }
        return null;
    }
}
