using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.App.Capture;
using MyCapture.Core.Settings;

namespace MyCapture.App;

internal sealed class ShortcutHelpWindow : Window
{
    internal static Func<HotkeySettings> ReadHotkeys { get; set; } = () => new HotkeySettings();
    internal static Action? OpenSettings { get; set; }
    internal static Func<bool> CanShowHelp { get; set; } = () => true;
    private static ShortcutHelpWindow? _open;

    internal ShortcutHelpWindow(bool onboarding = false)
    {
        Title = UiText.Get(onboarding ? "Help.Welcome" : "Help.Title");
        Width = onboarding ? 480 : 660; Height = onboarding ? 360 : 560;
        MinWidth = 420; MinHeight = 300;
        StandardWindowTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(24) };
        var title = new TextBlock { Text = Title, FontSize = 22, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);
        HotkeySettings keys = ReadHotkeys();
        if (onboarding)
        {
            var steps = new[] { ("Text_096A13A757DB", keys.Capture), ("Text_75306DAE2E60", keys.RecordRegion), ("Text_66BA5DCDA810", keys.OpenLibrary) };
            var panel = new StackPanel();
            var progress = new TextBlock { Margin = new Thickness(0, 0, 0, 12) };
            var task = new TextBlock { FontSize = 18, Margin = new Thickness(0, 0, 0, 10) };
            var chord = new TextBlock { FontSize = 24, Margin = new Thickness(0, 0, 0, 24) };
            var next = new Button { MinHeight = 36, IsDefault = true };
            var settings = new Button { Content = UiText.Get("Help.Settings"), MinHeight = 36, Margin = new Thickness(0, 8, 0, 0) };
            int step = 0;
            void RenderStep() { progress.Text = $"{step + 1} / 3"; task.Text = UiText.Get(steps[step].Item1); chord.Text = Display(steps[step].Item2); next.Content = UiText.Get(step == 2 ? "Help.Start" : "Help.Next"); }
            next.Click += (_, _) => { if (++step == 3) Close(); else RenderStep(); };
            settings.Click += (_, _) => { Close(); OpenSettings?.Invoke(); };
            foreach (UIElement element in new UIElement[] { progress, task, chord, next, settings }) panel.Children.Add(element);
            RenderStep(); root.Children.Add(panel);
        }
        else
        {
            var search = new TextBox { MinHeight = 36, Margin = new Thickness(0, 0, 0, 12) };
            System.Windows.Automation.AutomationProperties.SetName(search, UiText.Get("Help.Search"));
            search.ToolTip = UiText.Get("Help.Search");
            DockPanel.SetDock(search, Dock.Top); root.Children.Add(search);
            var tabs = new TabControl();
            (string Tab, (string Action, string Keys)[] Entries)[] sections =
            [
                ("Help.Capture", [("Text_096A13A757DB", Display(keys.Capture)), ("Text_75306DAE2E60", Display(keys.RecordRegion)),
                    ("Text_66BA5DCDA810", Display(keys.OpenLibrary)), ("Text_36458FABBDBB", Display(keys.CaptureWindow)),
                    ("Text_254C836B9ED2", Display(keys.CaptureFullScreen)), ("Hotkey.Scroll", Display(keys.ScrollingCapture)), ("Hotkey.Delay", Display(keys.DelayedCapture)),
                    ("QuickAccess.Title", "Alt+Q")]),
                ("Help.Annotation", [("Text_DC0760235344", "R"), ("Tools.Ellipse", "E"), ("Text_2785CE58DF74", "A"),
                    ("Text_37DB9D0B1C8E", "P"), ("Tools.Highlighter", "H"), ("Text_258AD4B095A1", "T"), ("Tools.StepNumber", "N"),
                    ("Text_3F8046019B32", "M"), ("Tools_Crop", "X"), ("QuickAccess.Save", "Ctrl+S"), ("QuickAccess.Copy", "Ctrl+C")]),
                ("Help.Video", [("Help.Play", "Space"), ("Help.Frame", "Ctrl+← / Ctrl+→"), ("Video.TrimIn", "I"), ("Video.TrimOut", "O"), ("Help.Gif", "G")]),
                ("QuickAccess.Pin", [("QuickAccess.Pin", Display(keys.PasteToScreen)), ("Text_4FED89116C1F", Display(keys.HideAllPins)),
                    ("Text_67FE4AF7D334", Display(keys.ToggleClickThrough)), ("QuickAccess.Copy", "Ctrl+C"), ("QuickAccess.Save", "Ctrl+S"), ("Dialog.Close", "Esc")]),
            ];
            var rows = new List<(string Search, FrameworkElement Row)>();
            foreach (var section in sections)
            {
                var panel = new StackPanel { Margin = new Thickness(12) };
                foreach (var entry in section.Entries)
                {
                    string label = UiText.Get(entry.Action);
                    var row = new UniformGrid { Columns = 2, Margin = new Thickness(0, 6, 0, 6) };
                    row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
                    row.Children.Add(new TextBlock { Text = entry.Keys, TextWrapping = TextWrapping.Wrap });
                    panel.Children.Add(row); rows.Add(($"{label} {entry.Keys}", row));
                }
                var tab = new TabItem { Header = UiText.Get(section.Tab), Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
                tab.SetResourceReference(StyleProperty, "Tab.Help");
                tabs.Items.Add(tab);
            }
            search.TextChanged += (_, _) => { foreach (var row in rows) row.Row.Visibility = row.Search.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase) ? Visibility.Visible : Visibility.Collapsed; };
            root.Children.Add(tabs);
        }
        Content = root;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    internal static void ShowHelp()
    {
        if (!CanShowHelp()) return;
        if (_open is not null) { _open.Activate(); return; }
        _open = new ShortcutHelpWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    internal static void OnWindowKey(object sender, KeyEventArgs e)
    {
        if (sender is ShortcutHelpWindow or CaptureOverlayWindow || HotkeyRecorder.Active is not null || !CanShowHelp()) return;
        if (IsHelpKey(e.Key, Keyboard.Modifiers, Keyboard.FocusedElement))
        { e.Handled = true; ShowHelp(); }
    }

    internal static bool IsHelpKey(Key key, ModifierKeys modifiers, IInputElement? focused) =>
        key == Key.F1 || (key == Key.OemQuestion && modifiers == ModifierKeys.Shift && focused is not (TextBoxBase or PasswordBox));

    internal static bool DismissForCapture()
    {
        Window[] windows = Application.Current?.Windows.OfType<ShortcutHelpWindow>().Where(w => w.IsVisible).ToArray() ?? [];
        foreach (Window window in windows) window.Close();
        if (_open?.IsVisible == true) { _open.Close(); return true; }
        return windows.Length > 0;
    }

    private static string Display(Hotkey key) => key.IsAssigned ? key.ToString() : UiText.Get("Hotkey.Unassigned");
}
