using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
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

    internal ShortcutHelpWindow()
    {
        Title = UiText.Get("Help.Title");
        Width = 660; Height = 600;
        MinWidth = 420; MinHeight = 300;
        ResizeMode = ResizeMode.NoResize;
        StandardWindowTheme.Apply(this);
        SourceInitialized += (_, _) => CaptureWindowExclusion.DisableTransitions(this);
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // The title bar already names the window; the body starts with the search box.
        var root = new DockPanel { Margin = new Thickness(24, 16, 24, 24) };
        HotkeySettings keys = ReadHotkeys();
        {
            var search = new TextBox { MinHeight = 40, Padding = new Thickness(36, 8, 12, 8) };
            System.Windows.Automation.AutomationProperties.SetName(search, UiText.Get("Help.Search"));
            search.ToolTip = UiText.Get("Help.Search");
            // TextBoxBase already copies Padding onto its content host; the shared template adds it
            // again as that host's margin, which pushed typed text 36 to the right of the hint.
            search.Loaded += (_, _) =>
            {
                if (search.Template?.FindName("PART_ContentHost", search) is FrameworkElement host) host.Margin = new Thickness(0);
            };
            var searchBox = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            searchBox.Children.Add(search);
            var searchIcon = new SymbolIcon
            {
                Size = SymbolIcon.InlineSize,
                Data = Geometry.Parse("M8.75,3.5 A5.25,5.25 0 1 1 8.74,3.5 M12.5,12.5 L16.75,16.75"),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12,0,0,0), IsHitTestVisible = false };
            searchIcon.SetResourceReference(SymbolIcon.DataProperty, "Icon.Search");
            searchIcon.SetResourceReference(SymbolIcon.StrokeProperty, "Text.Secondary");
            searchBox.Children.Add(searchIcon);
            // Border 1 + padding 36 + the text view's own 2: the hint starts where typed text does.
            var hint = new TextBlock { Text = UiText.Get("Help.Search"), Margin = new Thickness(39,0,12,0),
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            hint.SetResourceReference(TextBlock.ForegroundProperty,"Text.Secondary");
            searchBox.Children.Add(hint);
            DockPanel.SetDock(searchBox, Dock.Top); root.Children.Add(searchBox);

            // While a search term is present, one list across every category replaces the tabs,
            // so a match on another tab is never hidden behind the selected one. The tabs keep
            // their selection and come back unchanged when the search is cleared.
            var count = new TextBlock { FontSize = 12, Margin = new Thickness(12, 0, 12, 4), Visibility = Visibility.Collapsed };
            count.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            AutomationProperties.SetLiveSetting(count, AutomationLiveSetting.Polite);
            DockPanel.SetDock(count, Dock.Top); root.Children.Add(count);

            var emptyTitle = new TextBlock { Text = UiText.Get("Help.SearchEmpty"), FontSize = 14, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetLiveSetting(emptyTitle, AutomationLiveSetting.Polite);
            var clear = new Button { Content = UiText.Get("Help.SearchClear"), MinWidth = 120, Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center };
            clear.SetResourceReference(StyleProperty, "Button.Secondary");
            AutomationProperties.SetName(clear, UiText.Get("Help.SearchClear"));
            clear.Click += (_, _) => { search.Clear(); search.Focus(); };
            var empty = new StackPanel { Margin = new Thickness(0, 40, 0, 0), Visibility = Visibility.Collapsed };
            empty.Children.Add(emptyTitle);
            empty.Children.Add(clear);
            DockPanel.SetDock(empty, Dock.Top); root.Children.Add(empty);

            var results = new StackPanel();
            // Docked to the top, it grows into the space the hidden tabs leave and scrolls beyond it.
            var resultsView = new ScrollViewer { Content = results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed };
            DockPanel.SetDock(resultsView, Dock.Top); root.Children.Add(resultsView);
            var tabs = new TabControl();
            (string Tab, (string Action, string Keys)[] Entries)[] sections =
            [
                ("Help.Capture", [("Text_096A13A757DB", Display(keys.Capture)), ("Text_75306DAE2E60", Display(keys.RecordRegion)),
                    ("RecordingPauseHotkey_Command", Display(keys.PauseRecording)),
                    ("Text_66BA5DCDA810", Display(keys.OpenLibrary)), ("Text_36458FABBDBB", Display(keys.CaptureWindow)),
                    ("Text_254C836B9ED2", Display(keys.CaptureFullScreen)), ("Hotkey.Scroll", Display(keys.ScrollingCapture)), ("Hotkey.Delay", Display(keys.DelayedCapture)),
                    ("QuickAccess.Title", "Alt+Q"), ("TextCapture_Title", Display(keys.CaptureText)),
                    ("ColorPick_HelpLabel", "C"), ("TextCapture_HelpToggle", "T")]),
                ("Help.Annotation", [("Text_DC0760235344", "R"), ("Tools.Ellipse", "E"), ("Text_2785CE58DF74", "A"),
                    ("Text_37DB9D0B1C8E", "P"), ("Tools.Highlighter", "H"), ("Text_258AD4B095A1", "T"), ("Tools.StepNumber", "N"),
                    ("Text_3F8046019B32", "M"), ("EditorBlur_Name", "B"), ("Tools_Crop", "X"),
                    ("EditorUx_RotateLeftHint", "Q"), ("EditorUx_RotateRightHint", "W"), ("QuickAccess.Save", "Ctrl+S"), ("QuickAccess.Copy", "Ctrl+C")]),
                ("Folder410.Title", [("Folder410.NewFolder", "Ctrl+Shift+N"), ("Folder410.Rename", "F2"),
                    ("Folder410.MoveCaptures", "M"), ("Folder410.Delete", "Del")]),
                ("Help.Video", [("Help.Play", "Space"), ("Help.Frame", FrameStepKeys), ("Video.TrimIn", "I"), ("Video.TrimOut", "O"), ("Help.Gif", "G")]),
                ("Text_33EFD44366A7", [("Text_1FBCF51E2FF5", Display(keys.PasteToScreen)), ("Text_4FED89116C1F", Display(keys.HideAllPins)),
                    ("Onboard_PinAllowClickThrough", Display(keys.ToggleClickThrough)), ("Onboard_TrayReopenPin", Display(keys.RestoreClosedPin)),
                    ("QuickAccess.Copy", "Ctrl+C"), ("QuickAccess.Save", "Ctrl+S"), ("Dialog.Close", "Esc")]),
            ];
            var matches = new List<(string Search, FrameworkElement Row)>();
            foreach (var section in sections)
            {
                string category = UiText.Get(section.Tab);
                var panel = new StackPanel { Margin = new Thickness(12) };
                foreach (var entry in section.Entries)
                {
                    string label = UiText.Get(entry.Action);
                    var row = new UniformGrid { Columns = 2, Margin = new Thickness(0, 6, 0, 6) };
                    var action = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                    if (IsTextTutorialEntry(entry.Action))
                    {
                        var tutorialAction = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                        tutorialAction.Children.Add(action);
                        tutorialAction.Children.Add(TutorialButton());
                        row.Children.Add(tutorialAction);
                    }
                    else row.Children.Add(action);
                    row.Children.Add(Keycaps(entry.Keys));
                    panel.Children.Add(row);
                    FrameworkElement result = ResultRow(label, category, entry.Keys, IsTextTutorialEntry(entry.Action) ? keys : null);
                    results.Children.Add(result);
                    matches.Add(($"{label} {entry.Keys} {category}", result));
                }
                var tab = new TabItem { Header = category, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
                tab.SetResourceReference(StyleProperty, "Tab.Help");
                tabs.Items.Add(tab);
            }
            search.TextChanged += (_, _) =>
            {
                hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                string query = search.Text.Trim();
                bool searching = query.Length > 0;
                int found = 0;
                foreach ((string text, FrameworkElement row) in matches)
                {
                    bool match = searching && text.Contains(query, StringComparison.CurrentCultureIgnoreCase);
                    row.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                    if (match) found++;
                }
                tabs.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
                resultsView.Visibility = count.Visibility = searching && found > 0 ? Visibility.Visible : Visibility.Collapsed;
                empty.Visibility = searching && found == 0 ? Visibility.Visible : Visibility.Collapsed;
                count.Text = UiText.Format(found == 1 ? "Help.SearchCountOne" : "Help.SearchCount", found);
                if (!searching) return;
                resultsView.ScrollToTop();
                // Screen readers hear the new count, or that nothing matched.
                TextBlock status = found > 0 ? count : emptyTitle;
                UIElementAutomationPeer.CreatePeerForElement(status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            };
            root.Children.Add(tabs);

            // Esc first clears a search, then closes.
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                if (search.Text.Length > 0) { search.Clear(); search.Focus(); }
                else Close();
            };
        }
        Content = root;
    }

    /// <summary>
    /// One search result: the action with its category in small text under it, then its keys.
    /// Same two columns as a tab row, at least 36 high with 12 at each side.
    /// </summary>
    private FrameworkElement ResultRow(string label, string category, string chord, HotkeySettings? tutorialKeys = null)
    {
        var grid = new Grid { MinHeight = 36 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        // Semibold like a tab row, whose text takes the selected tab's weight.
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        var tag = new TextBlock { Text = category, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
        tag.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        text.Children.Add(tag);
        if (tutorialKeys is not null) text.Children.Add(TutorialButton());
        grid.Children.Add(text);
        FrameworkElement keycaps = Keycaps(chord);
        Grid.SetColumn(keycaps, 1);
        grid.Children.Add(keycaps);
        return new Border { Padding = new Thickness(12, 4, 12, 4), Child = grid, Visibility = Visibility.Collapsed };
    }

    /// <summary>
    /// The previous and next frame keys as the recording editor's step buttons show them (",", ".")
    /// and as its shortcut table routes them, so the help and the editor never disagree.
    /// </summary>
    internal static string FrameStepKeys =>
        $"{ShortcutCatalog.Get(ShortcutScope.Video, "PreviousFrame").Hint} / {ShortcutCatalog.Get(ShortcutScope.Video, "NextFrame").Hint}";

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
        if (sender is ShortcutHelpWindow or TextCopyTutorialWindow or CaptureOverlayWindow || HotkeyRecorder.Active is not null || !CanShowHelp()) return;
        if (IsHelpKey(e.Key, Keyboard.Modifiers, Keyboard.FocusedElement))
        { e.Handled = true; ShowHelp(); }
    }

    internal static bool IsHelpKey(Key key, ModifierKeys modifiers, IInputElement? focused) =>
        key == Key.F1 || (key == Key.OemQuestion && modifiers == ModifierKeys.Shift && focused is not (TextBoxBase or PasswordBox));

    internal static bool DismissForCapture()
    {
        bool tutorialClosed = TextCopyTutorialWindow.DismissForCapture();
        Window[] windows = Application.Current?.Windows.OfType<ShortcutHelpWindow>().Where(w => w.IsVisible).ToArray() ?? [];
        foreach (Window window in windows) window.Close();
        // A capture shortcut pressed over the first-run welcome closes it without opening the library.
        WelcomeWindow[] welcomes = Application.Current?.Windows.OfType<WelcomeWindow>().Where(w => w.IsVisible).ToArray() ?? [];
        foreach (WelcomeWindow welcome in welcomes) welcome.DismissForCapture();
        if (_open?.IsVisible == true) { _open.Close(); return true; }
        return tutorialClosed || windows.Length > 0 || welcomes.Length > 0;
    }

    private static bool IsTextTutorialEntry(string action) => action is "TextCapture_Title" or "TextCapture_HelpToggle";

    private Button TutorialButton()
    {
        var button = new Button { Content = UiText.Get("TextCapture_TutorialOpen"), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0), MinHeight = 32 };
        button.SetResourceReference(StyleProperty, "Button.Secondary");
        AutomationProperties.SetName(button, UiText.Get("TextCapture_TutorialTitle"));
        button.Click += (_, _) => TextCopyTutorialWindow.ShowTutorial(this, ReadHotkeys());
        return button;
    }

    private static string Display(Hotkey key) => key.IsAssigned ? key.ToString() : UiText.Get("Hotkey.Unassigned");

    internal static FrameworkElement Keycaps(string chord)
    {
        var keys = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(keys, chord);
        if (chord == UiText.Get("Hotkey.Unassigned"))
        {
            var empty = new TextBlock { Text = chord, Margin = new Thickness(0, 5, 0, 5) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            keys.Children.Add(empty);
            return keys;
        }
        string[] alternatives = chord.Split(" / ", StringSplitOptions.RemoveEmptyEntries);
        for (int a = 0; a < alternatives.Length; a++)
        {
            if (a > 0) keys.Children.Add(new TextBlock { Text = " / ", VerticalAlignment = VerticalAlignment.Center });
            string alternative = alternatives[a];
            string[] parts = alternative.TrimEnd('+').Split('+', StringSplitOptions.RemoveEmptyEntries);
            if (alternative.EndsWith('+')) parts = [.. parts, "+"];
            foreach (string key in parts)
            {
                var cap = new Border { CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1,1,1,2),
                    Padding = new Thickness(8,4,8,4), Margin = new Thickness(0,2,4,2),
                    Child = new TextBlock { Text = key, FontSize = 12, FontWeight = FontWeights.SemiBold } };
                cap.SetResourceReference(Border.BackgroundProperty,"Surface.Raised");
                cap.SetResourceReference(Border.BorderBrushProperty,"Border.Strong");
                keys.Children.Add(cap);
            }
        }
        return keys;
    }
}
