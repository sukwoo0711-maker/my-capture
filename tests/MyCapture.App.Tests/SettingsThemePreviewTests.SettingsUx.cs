using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The settings window saves changes immediately, keeps one title, groups its
/// options into nine categories and keeps the GitHub token out of plain text.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task SettingsUx_CloseFlushesChangesAndDiscardsInvalidInput() =>
        RunIsolated(nameof(SettingsUx_CloseFlushesChangesAndDiscardsInvalidInput), VerifySettingsUxClose);

    [Fact]
    public Task SettingsUx_AllCloseRoutesRevertInvalidFieldsAndKeepSavedChanges() =>
        RunIsolated(nameof(SettingsUx_AllCloseRoutesRevertInvalidFieldsAndKeepSavedChanges), VerifyAllSettingsCloseRoutes);

    [Fact]
    public Task SettingsUx_ControlFFocusesAndSelectsSearchUnlessRecordingShortcut() =>
        RunIsolated(nameof(SettingsUx_ControlFFocusesAndSelectsSearchUnlessRecordingShortcut), VerifySettingsSearchShortcut);

    [Fact]
    public Task SettingsUx_InvalidReplacementOfFailedEditDoesNotTrapClose() =>
        RunIsolated(nameof(SettingsUx_InvalidReplacementOfFailedEditDoesNotTrapClose), () =>
        {
            Application application = CreateThemedApplication();
            SettingsUxFixture fixture = CreateSettingsUxWindow(new AppSettings(), _ => false);
            SettingsWindow window = fixture.Window;
            try
            {
                window.Show();
                Draft(window).MaxItems = "450";
                Assert.False(window.FlushPendingSettings());
                window.RequestClose();
                Assert.True(window.IsVisible); // A current valid write failure still needs recovery.
                Draft(window).MaxItems = "5";
                window.RequestClose();
                Assert.False(window.IsVisible);
                Assert.Equal("300", Draft(window).MaxItems);
                Assert.False(Draft(window).HasErrors);
                Assert.Equal(300, fixture.Saved().Queue.MaxItems);
            }
            finally { window.CloseForExit(); application.Shutdown(); }
        });

    [Fact]
    public Task SettingsUx_NineCategoriesWithDistinctIconsAndOneTitle() =>
        RunIsolated(nameof(SettingsUx_NineCategoriesWithDistinctIconsAndOneTitle), VerifySettingsUxCategories);

    [Fact]
    public Task SettingsUx_GitHubTokenIsAPasswordInTheNamedConnectionGroup() =>
        RunIsolated(nameof(SettingsUx_GitHubTokenIsAPasswordInTheNamedConnectionGroup), VerifySettingsUxToken);

    [Fact]
    public Task SettingsUx_SavedShortcutsStayIntactAndAlwaysHintOptionExplainsAlt() =>
        RunIsolated(nameof(SettingsUx_SavedShortcutsStayIntactAndAlwaysHintOptionExplainsAlt), VerifySettingsUxShortcuts);

    [Fact]
    public Task SettingsUx_CloseDialogOrdersApplyDiscardCancel() =>
        RunIsolated(nameof(SettingsUx_CloseDialogOrdersApplyDiscardCancel), VerifySettingsUxCloseDialog);

    private sealed record SettingsUxFixture(SettingsWindow Window, Func<AppSettings> Saved, Func<int> ApplyCount);

    private static SettingsUxFixture CreateSettingsUxWindow(AppSettings initial, Func<AppSettings, bool>? accept = null)
    {
        AppSettings saved = initial;
        int applied = 0;
        ThemeService.ApplyFromSettings(saved.General.Theme);
        var window = new SettingsWindow(() => saved, candidate =>
        {
            applied++;
            if (accept?.Invoke(candidate) == false) return SettingsApplyResult.NotSaved(["Synthetic failure"]);
            saved = candidate;
            ThemeService.ApplyFromSettings(saved.General.Theme);
            return new SettingsApplyResult(true, true, true, false, []);
        }, NullLogger.Instance, showSaveFailureMessage: _ => { },
            defaultCapturesRoot: () => System.IO.Path.Combine(AppContext.BaseDirectory, "settings-ux-captures"))
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = 940, Height = 700,
            ShowActivated = false, ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(window, false);
        return new SettingsUxFixture(window, () => saved, () => applied);
    }

    private static void VerifySettingsUxClose()
    {
        using var language = UiText.UseLanguage("ko-KR");
        Application application = CreateThemedApplication();
        SettingsUxFixture fixture = CreateSettingsUxWindow(new AppSettings { General = { Theme = AppThemeNames.Glass } });
        SettingsWindow window = fixture.Window;
        int asked = 0;
        window.ConfirmUnappliedChanges = () => { asked++; return SettingsCloseChoice.Cancel; };
        bool closed = false;
        try
        {
            window.ShowSettings();
            Drain(window);
            Assert.False(Draft(window).HasUnappliedChanges);
            window.Close();
            Assert.False(window.IsVisible);
            window.ShowSettings();
            SettingsWindow.CloseCommand.Execute(null, window);
            Assert.False(window.IsVisible);

            window.ShowSettings();
            Draft(window).MaxItems = "301";
            Draft(window).MaxItems = "300";
            window.Close();
            Assert.False(window.IsVisible);
            Assert.Equal(0, fixture.ApplyCount());

            window.ShowSettings();
            Draft(window).Theme = AppThemeNames.Daylight;
            Draft(window).MaxItems = "450";
            window.Close();
            Assert.False(window.IsVisible);
            Assert.Equal(450, fixture.Saved().Queue.MaxItems);
            Assert.Equal("450", Draft(window).MaxItems);
            Assert.Equal(AppThemeNames.GlassLight, fixture.Saved().General.Theme);
            Assert.Equal(AppTheme.GlassLight, ThemeService.Current);
            Assert.Equal(0, asked);

            window.ShowSettings();
            Assert.Equal("450", Draft(window).MaxItems);
            Draft(window).MaxItems = "555";
            var escape = Assert.Single(window.InputBindings.OfType<KeyBinding>(), binding => binding.Key == Key.Escape);
            Assert.IsType<RoutedUICommand>(escape.Command).Execute(null, window);
            Assert.False(window.IsVisible);
            Assert.Equal(555, fixture.Saved().Queue.MaxItems);
            window.ShowSettings();
            Draft(window).MaxItems = "5";
            int beforeInvalid = fixture.ApplyCount();
            window.Close();
            Assert.False(window.IsVisible);
            Assert.Equal(beforeInvalid, fixture.ApplyCount());
            Assert.Equal("555", Draft(window).MaxItems);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(window.FindName("ErrorSummary")).Visibility);
            window.ShowSettings();
            Draft(window).MaxItems = "5";
            var close = Assert.IsType<Button>(window.FindName("CancelButton"));
            Assert.False(close.IsCancel);
            close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.False(window.IsVisible);
            Assert.Equal(555, fixture.Saved().Queue.MaxItems);
            Assert.Equal("555", Draft(window).MaxItems);
            window.ShowSettings();
            Draft(window).MaxItems = "600";
            close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.False(window.IsVisible);
            Assert.Equal(600, fixture.Saved().Queue.MaxItems);

            window.ShowSettings();
            int resetPrompts = 0;
            window.ConfirmResetDefaults = () => { resetPrompts++; return false; };
            var defaults = Assert.IsType<Button>(window.FindName("DefaultsButton"));
            defaults.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(600, fixture.Saved().Queue.MaxItems);
            window.ConfirmResetDefaults = () => { resetPrompts++; return true; };
            defaults.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Drain(window);
            Assert.Equal(2, resetPrompts);
            Assert.Equal(1000, fixture.Saved().Queue.MaxItems);
            Assert.Equal(5L * 1024 * 1024 * 1024, fixture.Saved().Queue.MaxBytes);
            Assert.Equal(MyCapture.Core.Queue.CaptureRetention.NeverHours, fixture.Saved().Queue.ImageRetentionHours);
            Assert.Equal(AfterCaptureAction.QuickAccess, fixture.Saved().General.AfterCapture);
            Assert.Equal(AppThemeNames.System, fixture.Saved().General.Theme);
            Assert.False(fixture.Saved().General.ShowShortcutHints);
            Assert.False(fixture.Saved().General.AlwaysShowShortcutHints);
            Assert.False(Draft(window).HasUnappliedChanges);

            Draft(window).MaxItems = "5";
            Assert.True(window.PrepareForAppExit(keepVisible: true));
            Assert.Equal("1000", Draft(window).MaxItems);
            Assert.True(window.IsVisible);
            Draft(window).MaxItems = "555";
            Assert.True(window.PrepareForAppExit(keepVisible: true));
            Assert.Equal(555, fixture.Saved().Queue.MaxItems);
            Assert.True(window.IsVisible);
            Assert.Equal(0, asked);
            Draft(window).MaxItems = "999";
            window.CloseForExit();
            closed = true;
            Assert.Equal(999, fixture.Saved().Queue.MaxItems);
        }
        finally
        {
            if (!closed) window.CloseForExit();
            application.Shutdown();
        }
    }

    private static void VerifyAllSettingsCloseRoutes()
    {
        Application application = CreateThemedApplication();
        try
        {
            foreach (string route in new[] { "title-bar", "close-command", "button", "escape", "prepare-exit", "exit" })
            {
                SettingsUxFixture fixture = CreateSettingsUxWindow(new AppSettings());
                SettingsWindow window = fixture.Window;
                bool closed = false;
                try
                {
                    window.ShowSettings();
                    Draft(window).MaxItems = "555";
                    Draft(window).DelaySeconds = "3";
                    Draft(window).PlayCaptureSound = true;
                    Assert.True(window.FlushPendingSettings());
                    Assert.Equal(555, fixture.Saved().Queue.MaxItems);

                    Draft(window).MaxItems = "5";
                    Draft(window).DelaySeconds = "invalid";
                    bool notify = !fixture.Saved().General.NotifyOnQuickSave;
                    Draft(window).NotifyOnQuickSave = notify;
                    switch (route)
                    {
                        case "title-bar": window.Close(); break;
                        case "close-command": SettingsWindow.CloseCommand.Execute(null, window); break;
                        case "button":
                            Assert.IsType<Button>(window.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            break;
                        case "escape":
                            var escape = Assert.Single(window.InputBindings.OfType<KeyBinding>(), binding => binding.Key == Key.Escape);
                            Assert.IsType<RoutedUICommand>(escape.Command).Execute(null, window);
                            break;
                        case "prepare-exit":
                            Assert.True(window.PrepareForAppExit(keepVisible: true));
                            Assert.True(window.IsVisible);
                            window.CloseForExit();
                            closed = true;
                            break;
                        case "exit": window.CloseForExit(); closed = true; break;
                    }
                    Assert.False(window.IsVisible);
                    Assert.False(Draft(window).HasErrors);
                    Assert.Equal("555", Draft(window).MaxItems);
                    Assert.Equal("3", Draft(window).DelaySeconds);
                    Assert.Equal(555, fixture.Saved().Queue.MaxItems);
                    Assert.Equal(3, fixture.Saved().Capture.DelaySeconds);
                    Assert.True(fixture.Saved().General.PlayCaptureSound);
                    Assert.Equal(notify, fixture.Saved().General.NotifyOnQuickSave);
                }
                finally { if (!closed) window.CloseForExit(); }
            }
        }
        finally { application.Shutdown(); }
    }

    private static void VerifySettingsSearchShortcut()
    {
        using var language = UiText.UseLanguage("ko-KR");
        Application application = CreateThemedApplication();
        SettingsWindow window = CreateSettingsUxWindow(new AppSettings()).Window;
        try
        {
            window.Show();
            Drain(window);
            var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
            var search = Assert.IsType<TextBox>(tabs.Template.FindName("SettingsSearchBox", tabs));
            search.Text = "단축키";
            search.Select(1, 1);
            Assert.True(window.HandleSearchShortcut(Key.F, ModifierKeys.Control));
            Assert.Same(search, Keyboard.FocusedElement);
            Assert.Equal(0, search.SelectionStart);
            Assert.Equal(search.Text.Length, search.SelectionLength);
            Assert.False(window.HandleSearchShortcut(Key.F, ModifierKeys.Control | ModifierKeys.Shift));

            search.Clear();
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, UiText.Get("SettingsUx_TabShortcuts")));
            Drain(window);
            var recorder = Assert.IsType<HotkeyRecorder>(window.FindName("CaptureTextHotkeyRecorder"));
            Button record = Descendants(recorder).OfType<Button>().First();
            record.BringIntoView();
            Drain(window);
            record.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Same(recorder, HotkeyRecorder.Active);
            Assert.Same(record, Keyboard.FocusedElement);
            Assert.False(window.HandleSearchShortcut(Key.F, ModifierKeys.Control));
            Assert.Same(record, Keyboard.FocusedElement);
            Assert.True(recorder.HandleRecordedKey(Key.F, ModifierKeys.Control));
            Assert.Equal("Ctrl+F", recorder.Text);
            Assert.Null(HotkeyRecorder.Active);
        }
        finally { window.CloseForExit(); application.Shutdown(); }
    }

    private static void VerifySettingsUxCategories()
    {
        Application application = CreateThemedApplication();
        try
        {
            foreach (string locale in new[] { "ko-KR", "en-US" })
            {
                using var language = UiText.UseLanguage(locale);
                SettingsWindow window = CreateSettingsUxWindow(new AppSettings()).Window;
                try
                {
                    window.Show();
                    Drain(window);
                    var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
                    Assert.Null(window.FindName("ApplyButton"));
                    foreach (string name in new[] { "CancelButton" })
                    {
                        Button action = Assert.IsType<Button>(window.FindName(name));
                        Assert.Equal(32, action.ActualHeight, 1);
                        Assert.Equal(new Thickness(10, 4, 10, 4), action.Padding);
                    }
                    TabItem[] items = tabs.Items.OfType<TabItem>().ToArray();
                    string[] expected = ["SettingsUx_TabCapture", "SettingsUx_TabRecording", "SettingsUx_TabEditing",
                        "SettingsStructure_TabPins", "SettingsStructure_TabText", "SettingsStructure_TabStorage",
                        "SettingsUx_TabShortcuts", "SettingsUx_TabGeneral", "SettingsStructure_TabAbout"];
                    Assert.Equal(expected.Select(UiText.Get), items.Select(tab => (string)tab.Header));
                    Assert.Equal(items.Select(tab => (string)tab.Header), items.Select(AutomationProperties.GetName));
                    Assert.All(items, tab => Assert.False(string.IsNullOrWhiteSpace(tab.ToolTip as string)));
                    Assert.Equal(items.Length, items.Select(tab => tab.Tag).Distinct().Count());
                    Assert.All(items, tab => Assert.IsAssignableFrom<Geometry>(tab.Tag));

                    // The default size fits the list; the minimum size keeps its last category reachable by scrolling.
                    var categories = Assert.IsType<ScrollViewer>(tabs.Template.FindName("CategoryScroll", tabs));
                    Assert.Equal(0, categories.ScrollableHeight, 1);
                    window.Width = window.MinWidth;
                    window.Height = window.MinHeight;
                    Drain(window);
                    categories.ScrollToEnd();
                    Drain(window);
                    Assert.Equal(categories.ScrollableHeight, categories.VerticalOffset, 1);
                    Rect lastCategory = items[^1].TransformToAncestor(categories)
                        .TransformBounds(new Rect(items[^1].RenderSize));
                    Assert.True(lastCategory.Top >= -1 && lastCategory.Bottom <= categories.ActualHeight + 1);
                    categories.ScrollToTop();
                    window.Width = 940;
                    window.Height = 700;

                    // The title bar is the only title: no product header, navigation heading or
                    // page heading repeats the window or category name.
                    string[] retired = [UiText.Get("Text_115BCED49291"), UiText.Get("Text_F5BF49635D43"),
                        UiText.Get("Text_BDA4A255187A"), UiText.Get("Text_FEAA5199651D")];
                    Style heading = Assert.IsType<Style>(window.FindResource("Settings.GroupHeading"));
                    Button primary = Assert.IsType<Button>(window.FindName("CancelButton"));
                    foreach (TabItem tab in items)
                    {
                        tabs.SelectedItem = tab;
                        Drain(window);
                        TextBlock[] texts = Descendants(window).OfType<TextBlock>().Where(text => text.IsVisible).ToArray();
                        Assert.DoesNotContain(texts, text => retired.Contains(text.Text));
                        Assert.DoesNotContain(texts, text => ReferenceEquals(text.Style, heading) && text.Text == (string)tab.Header);
                        // The footer retains one close action.
                        Button[] filled = Descendants(window).OfType<Button>()
                            .Where(button => button.IsVisible && ReferenceEquals(button.Style, window.FindResource("Button.Primary")))
                            .ToArray();
                        Assert.DoesNotContain(filled, button => button.Name == "ApplyButton");
                        Assert.True(primary.IsVisible);
                    }

                    Assert.Contains(window.InputBindings.OfType<KeyBinding>(), binding =>
                        binding.Key == Key.S && binding.Modifiers == ModifierKeys.Control
                        && ReferenceEquals(binding.Command, SettingsWindow.ApplyCommand));
                    Assert.Equal(UiText.Get("Dialog.Close"), primary.Content);

                    // Settings backup belongs to the named storage group; resetting defaults remains in About.
                    var export = Assert.IsType<Button>(window.FindName("ExportSettingsButton"));
                    var import = Assert.IsType<Button>(window.FindName("ImportSettingsButton"));
                    var about = Assert.IsType<TabItem>(window.FindName("UpdateTab"));
                    Assert.Same(about, items[^1]);
                    var storage = Assert.IsType<TabItem>(window.FindName("StorageTab"));
                    var backup = Assert.IsType<Expander>(window.FindName("BackupExpander"));
                    Assert.True(IsInside(backup, storage));
                    Assert.True(IsInside(export, backup) && IsInside(import, backup));
                    var defaults = Assert.IsType<Button>(window.FindName("DefaultsButton"));
                    Assert.True(IsInside(defaults, about));
                    Assert.True(defaults.MinHeight >= 24);

                    // The retention description states the period that will apply.
                    Assert.True(window.NavigateToSection(SettingsSection.Storage));
                    Drain(window);
                    var summary = Assert.IsType<TextBlock>(window.FindName("RetentionSummary"));
                    Assert.Equal(UiText.Format("SettingsUx_RetentionSummary", SettingsUxText.Duration(168)), summary.Text);
                    Draft(window).ImageRetentionHours = "36";
                    Assert.Contains(SettingsUxText.Duration(36), summary.Text, StringComparison.Ordinal);
                    Draft(window).ImageRetentionHours = "0";
                    Assert.Equal(UiText.Get("SettingsUx_RetentionRange"), summary.Text);
                    SettingsWindow.CancelCommand.Execute(null, window);
                }
                finally
                {
                    window.CloseForExit();
                }
            }
        }
        finally
        {
            application.Shutdown();
        }

        static bool IsInside(DependencyObject element, DependencyObject ancestor)
        {
            for (DependencyObject? node = element; node is not null; node = LogicalTreeHelper.GetParent(node))
                if (ReferenceEquals(node, ancestor)) return true;
            return false;
        }
    }

    private static void VerifySettingsUxToken()
    {
        using var language = UiText.UseLanguage("ko-KR");
        Application application = CreateThemedApplication();
        const string secret = "ghp_SyntheticTokenValue123";
        SettingsUxFixture fixture = CreateSettingsUxWindow(new AppSettings { GitHub = { Token = secret } });
        SettingsWindow window = fixture.Window;
        try
        {
            window.Show();
            var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
            Assert.True(window.NavigateToSection(SettingsSection.Storage));
            Drain(window);

            var advanced = Assert.IsType<Expander>(window.FindName("AdvancedExpander"));
            Assert.Equal(UiText.Get("SettingsStructure_GitHub"), advanced.Header);
            Assert.False(advanced.IsExpanded);
            var box = Assert.IsType<PasswordBox>(window.FindName("GitHubTokenBox"));
            Assert.False(box.IsVisible);
            Assert.Equal(secret, box.Password);
            Assert.Equal(UiText.Get("GitHub_TokenLabel"), AutomationProperties.GetName(box));

            advanced.IsExpanded = true;
            Drain(window);
            Assert.True(box.IsVisible);
            Assert.True(Assert.IsType<TextBox>(window.FindName("GitHubIssueUrlBox")).IsVisible);
            var peer = Assert.IsAssignableFrom<AutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(box));
            Assert.True(peer.IsPassword());
            Assert.DoesNotContain(secret, peer.GetName(), StringComparison.Ordinal);
            Assert.DoesNotContain(Descendants(window).OfType<TextBox>(), text => text.Text.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(Descendants(window).OfType<FrameworkElement>(), element =>
                (AutomationProperties.GetName(element) ?? string.Empty).Contains(secret, StringComparison.Ordinal)
                || (AutomationProperties.GetHelpText(element) ?? string.Empty).Contains(secret, StringComparison.Ordinal));
            advanced.BringIntoView();
            Drain(window);
            SaveSettingsUxEvidence(RenderEditorWindow(window), "settings-advanced-token-glass-after.png");

            // Typing edits the draft; applying stores the new token; nothing else changed it.
            box.Password = "ghp_Replacement456";
            Assert.Equal("ghp_Replacement456", Draft(window).GitHubToken);
            Assert.True(Draft(window).HasUnappliedChanges);
            SettingsWindow.ApplyCommand.Execute(null, window);
            Assert.Equal("ghp_Replacement456", fixture.Saved().GitHub.Token);

            // Reopening shows the stored token again, still masked.
            window.ShowSettings();
            Drain(window);
            Assert.Equal("ghp_Replacement456", box.Password);
            Assert.False(Draft(window).HasUnappliedChanges);
        }
        finally
        {
            window.CloseForExit();
            application.Shutdown();
        }
    }

    private static void VerifySettingsUxShortcuts()
    {
        using var language = UiText.UseLanguage("ko-KR");
        Application application = CreateThemedApplication();
        try
        {
            foreach ((string name, AppSettings settings, bool expectedLegacyHints, bool expectedAlwaysHints) in new[]
            {
                ("new", NewInstallDefaults.Create(), false, false),
                ("existing", new AppSettings(), true, false),
                ("explicit-hints-off", new AppSettings { General = { ShowShortcutHints = false } }, false, false),
                ("always-hints-on", new AppSettings { General = { AlwaysShowShortcutHints = true } }, true, true),
            })
            {
                SettingsWindow window = CreateSettingsUxWindow(settings).Window;
                try
                {
                    window.Show();
                    var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
                    tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, UiText.Get("SettingsUx_TabShortcuts")));
                    Assert.True(window.NavigateToSetting(nameof(SettingsDraft.OpenLibraryHotkey)));
                    Assert.True(window.NavigateToSetting(nameof(SettingsDraft.ForceCoreShortcuts)));
                    Drain(window);
                    HotkeyRecorder[] recorders = Descendants(window).OfType<HotkeyRecorder>().ToArray();
                    Assert.Equal(15, recorders.Length);
                    HotkeyRecorder Recorder(string property) => recorders.Single(recorder =>
                        recorder.GetBindingExpression(HotkeyRecorder.TextProperty)?.ParentBinding.Path.Path == property);

                    // Capture and recording come first, then the optional commands.
                    Assert.Same(Recorder(nameof(SettingsDraft.CaptureHotkey)), recorders[0]);
                    Assert.Same(Recorder(nameof(SettingsDraft.RecordRegionHotkey)), recorders[1]);
                    Assert.Equal("Ctrl+Shift+C", recorders[0].Text);
                    Assert.Equal("Ctrl+Shift+X", recorders[1].Text);
                    HotkeyRecorder library = Recorder(nameof(SettingsDraft.OpenLibraryHotkey));
                    HotkeyRecorder paste = Recorder(nameof(SettingsDraft.PasteToScreenHotkey));
                    HotkeyRecorder hide = Recorder(nameof(SettingsDraft.HideAllPinsHotkey));
                    if (name == "new")
                    {
                        Assert.Equal("F3", paste.Text);
                        Assert.Equal("Shift+F3", hide.Text);
                        Assert.All(recorders.Skip(2).Where(recorder => recorder != paste && recorder != hide),
                            recorder => Assert.Equal(string.Empty, recorder.Text));
                        // An unassigned row says so on the button that records a new chord.
                        Button record = Descendants(library).OfType<Button>().First();
                        Assert.Contains(Descendants(record).OfType<TextBlock>(), text => text.Text == UiText.Get("Hotkey.Unassigned"));
                        Assert.Equal(UiText.Get("SettingsUx_RecordHotkeyTip"), record.ToolTip);
                    }
                    else
                    {
                        // Existing settings are shown exactly as saved.
                        Assert.Equal("Ctrl+Shift+Z", library.Text);
                        Assert.Equal("F3", paste.Text);
                    }

                    // "Default" appears only where a default chord exists; it would repeat "Clear" elsewhere.
                    foreach (HotkeyRecorder recorder in recorders)
                    {
                        Button[] buttons = Descendants(recorder).OfType<Button>().ToArray();
                        Button reset = buttons.Single(button => Equals(button.Content, UiText.Get("Hotkey.Reset")));
                        Assert.Equal(!string.IsNullOrEmpty(recorder.DefaultText), reset.IsVisible);
                        Assert.Contains(buttons, button => Equals(button.Content, UiText.Get("Hotkey.Clear")) && button.IsVisible);
                    }

                    // Changing the clipboard pin chord takes one recorder interaction.
                    paste.Accept(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, Hotkey.VkF3));
                    Assert.Equal("Ctrl+Alt+F3", Draft(window).PasteToScreenHotkey);
                    Assert.True(Draft(window).HasUnappliedChanges);

                    Assert.Null(window.FindName("ShowShortcutHintsCheck"));
                    Assert.Equal(expectedLegacyHints, Draft(window).ShowShortcutHints);
                    var force = Assert.IsType<CheckBox>(window.FindName("ForceCoreShortcutsCheck"));
                    Assert.True(force.IsVisible);
                    Assert.Same(window.FindResource("Settings.CheckBox"), force.Style);
                    Assert.Equal(UiText.Get("CoreShortcuts.Force"), AutomationProperties.GetName(force));
                    Assert.Equal(UiText.Get("CoreShortcuts.ForceDescription"), AutomationProperties.GetHelpText(force));
                    string[] assigned = recorders.Select(recorder => recorder.Text).ToArray();
                    force.IsChecked = true;
                    Assert.True(Draft(window).ForceCoreShortcuts);
                    Assert.Equal(assigned, recorders.Select(recorder => recorder.Text));
                    force.IsChecked = false;
                    Assert.False(Draft(window).ForceCoreShortcuts);
                    SaveSettingsUxEvidence(RenderEditorWindow(window), $"settings-shortcuts-{name}-install-glass-after.png");

                    Assert.True(window.NavigateToSetting(nameof(SettingsDraft.AlwaysShowShortcutHints)));
                    Drain(window);
                    var hints = Assert.IsType<TextBlock>(window.FindName("ShortcutHintsDescription"));
                    var alwaysHints = Assert.IsType<CheckBox>(window.FindName("AlwaysShowShortcutHintsCheck"));
                    Assert.Equal(UiText.Get("SettingsUx_AlwaysShowShortcutHintsHint"), KoreanWordWrap.Strip(hints.Text));
                    Assert.True(hints.IsVisible);
                    Assert.True(alwaysHints.IsVisible);
                    Assert.Same(window.FindResource("Settings.CheckBox"), alwaysHints.Style);
                    Assert.Equal(UiText.Get("SettingsUx_AlwaysShowShortcutHints"), AutomationProperties.GetName(alwaysHints));
                    Assert.Equal(UiText.Get("SettingsUx_AlwaysShowShortcutHintsHint"), AutomationProperties.GetHelpText(alwaysHints));
                    Assert.Equal(nameof(SettingsDraft.AlwaysShowShortcutHints),
                        alwaysHints.GetBindingExpression(ToggleButton.IsCheckedProperty)!.ParentBinding.Path.Path);
                    Assert.Equal(expectedAlwaysHints, alwaysHints.IsChecked);
                    Assert.Equal(expectedAlwaysHints, Draft(window).AlwaysShowShortcutHints);
                    SettingsWindow.CancelCommand.Execute(null, window);
                }
                finally
                {
                    window.CloseForExit();
                }
            }
        }
        finally
        {
            application.Shutdown();
        }
    }

    private static void VerifySettingsUxCloseDialog()
    {
        Application application = CreateThemedApplication();
        try
        {
            foreach (string locale in new[] { "ko-KR", "en-US" })
            {
                using var language = UiText.UseLanguage(locale);
                foreach (SettingsCloseChoice expected in Enum.GetValues<SettingsCloseChoice>())
                {
                    var dialog = new UnappliedChangesDialog
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000, Top = -10000, ShowActivated = false,
                    };
                    FluidMotion.SetWindowEntrance(dialog, false);
                    try
                    {
                        dialog.Show();
                        Drain(dialog);
                        Button[] buttons = Descendants(dialog).OfType<Button>().ToArray();
                        Assert.Equal(new[] { dialog.ApplyButton, dialog.DiscardButton, dialog.CancelButton }, buttons);
                        Assert.Equal(new[] { UiText.Get("SettingsUx_CloseApply"), UiText.Get("SettingsUx_CloseDiscard"), UiText.Get("Dialog.Cancel") },
                            buttons.Select(DialogActionCaption));
                        Assert.Equal(buttons.Select(DialogActionCaption), buttons.Select(AutomationProperties.GetName));
                        Assert.True(dialog.ApplyButton.IsDefault);
                        Assert.True(dialog.CancelButton.IsCancel);
                        Assert.False(dialog.DiscardButton.IsDefault || dialog.DiscardButton.IsCancel);
                        Assert.Same(dialog.FindResource("Button.Primary"), dialog.ApplyButton.Style);
                        Assert.Same(dialog.FindResource("Button.Secondary"), dialog.DiscardButton.Style);
                        Assert.Same(dialog.FindResource("Button.Secondary"), dialog.CancelButton.Style);
                        // Left to right: apply, don't apply, cancel (WinUI dialog order).
                        double[] lefts = buttons.Select(button => button.TranslatePoint(new Point(), dialog).X).ToArray();
                        Assert.True(lefts[0] < lefts[1] && lefts[1] < lefts[2]);
                        foreach (Button button in buttons) Assert.True(button.ActualWidth >= Descendants(button).OfType<TextBlock>().Max(text => text.ActualWidth));
                        Assert.Equal(UiText.Get("SettingsUx_CloseTitle"), dialog.Heading.Text);
                        if (expected == SettingsCloseChoice.Cancel)
                        {
                            foreach (AppTheme theme in SettingsUxEvidenceThemes)
                            {
                                ThemeService.Apply(theme);
                                Drain(dialog);
                                SaveSettingsUxEvidence(RenderEditorWindow(dialog), $"close-dialog-{AppThemeNames.ToSetting(theme)}-{locale}-after.png");
                            }
                            ThemeService.Apply(AppTheme.Glass);
                        }

                        Button pressed = expected switch
                        {
                            SettingsCloseChoice.Apply => dialog.ApplyButton,
                            SettingsCloseChoice.Discard => dialog.DiscardButton,
                            _ => dialog.CancelButton,
                        };
                        pressed.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        Assert.Equal(expected, dialog.Choice);
                        Assert.False(dialog.IsVisible);
                    }
                    finally
                    {
                        dialog.Close();
                    }
                }

                // Closing the dialog any other way keeps editing.
                var dismissed = new UnappliedChangesDialog { Left = -10000, Top = -10000, ShowActivated = false };
                dismissed.Show();
                dismissed.Close();
                Assert.Equal(SettingsCloseChoice.Cancel, dismissed.Choice);
            }
        }
        finally
        {
            application.Shutdown();
        }
    }

    private static SettingsDraft Draft(SettingsWindow window) => Assert.IsType<SettingsDraft>(window.DataContext);

    private static string DialogActionCaption(Button button) => SurfaceShortcutHintTests.PrimaryContent(button) is string text
        ? text : Assert.IsType<TextBlock>(SurfaceShortcutHintTests.PrimaryContent(button)).Text;
}
