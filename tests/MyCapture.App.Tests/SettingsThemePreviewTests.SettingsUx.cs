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
/// The settings window asks before losing unapplied changes, keeps one title, groups its
/// options into seven categories and keeps the GitHub token out of plain text.
/// </summary>
public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task SettingsUx_CloseAsksOnlyWithUnappliedChanges_AndEachChoiceBehaves() =>
        RunIsolated(nameof(SettingsUx_CloseAsksOnlyWithUnappliedChanges_AndEachChoiceBehaves), VerifySettingsUxClose);

    [Fact]
    public Task SettingsUx_SevenCategoriesWithDistinctIconsAndOneTitle() =>
        RunIsolated(nameof(SettingsUx_SevenCategoriesWithDistinctIconsAndOneTitle), VerifySettingsUxCategories);

    [Fact]
    public Task SettingsUx_GitHubTokenIsAPasswordInTheFoldedAdvancedGroup() =>
        RunIsolated(nameof(SettingsUx_GitHubTokenIsAPasswordInTheFoldedAdvancedGroup), VerifySettingsUxToken);

    [Fact]
    public Task SettingsUx_NewInstallAndExistingShortcutsAndHintsShowAsSaved() =>
        RunIsolated(nameof(SettingsUx_NewInstallAndExistingShortcutsAndHintsShowAsSaved), VerifySettingsUxShortcuts);

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
        }, NullLogger.Instance, showSaveFailureMessage: _ => { })
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
        var answers = new Queue<SettingsCloseChoice>();
        int asked = 0;
        window.ConfirmUnappliedChanges = () => { asked++; return answers.Dequeue(); };
        bool closed = false;
        try
        {
            // Nothing changed: the title bar close, Alt+F4 (the same Closing path) and Esc close at once.
            window.ShowSettings();
            Drain(window);
            Assert.False(Draft(window).HasUnappliedChanges);
            window.Close();
            Assert.False(window.IsVisible);
            window.ShowSettings();
            SettingsWindow.CloseCommand.Execute(null, window);
            Assert.False(window.IsVisible);
            Assert.Equal(0, asked);

            // A value typed and typed back is not a change.
            window.ShowSettings();
            Draft(window).MaxItems = "301";
            Draft(window).MaxItems = "300";
            window.Close();
            Assert.False(window.IsVisible);
            Assert.Equal(0, asked);

            // Cancel in the prompt keeps the window, the edit and the theme preview.
            window.ShowSettings();
            Draft(window).Theme = AppThemeNames.Daylight;
            Draft(window).MaxItems = "450";
            answers.Enqueue(SettingsCloseChoice.Cancel);
            window.Close();
            Assert.True(window.IsVisible);
            Assert.Equal(1, asked);
            Assert.Equal("450", Draft(window).MaxItems);
            Assert.Equal(AppTheme.Daylight, ThemeService.Current);

            // Esc asks too; "Don't apply" discards and restores the saved theme.
            answers.Enqueue(SettingsCloseChoice.Discard);
            var escape = Assert.Single(window.InputBindings.OfType<KeyBinding>(), binding => binding.Key == Key.Escape);
            Assert.IsType<RoutedUICommand>(escape.Command).Execute(null, window);
            Assert.False(window.IsVisible);
            Assert.Equal(2, asked);
            Assert.Equal(AppTheme.Glass, ThemeService.Current);
            Assert.Equal(300, fixture.Saved().Queue.MaxItems);
            Assert.Equal(0, fixture.ApplyCount());

            // "Apply and close" applies, then hides.
            window.ShowSettings();
            Assert.Equal("300", Draft(window).MaxItems);
            Draft(window).MaxItems = "450";
            answers.Enqueue(SettingsCloseChoice.Apply);
            window.Close();
            Assert.False(window.IsVisible);
            Assert.Equal(3, asked);
            Assert.Equal(1, fixture.ApplyCount());
            Assert.Equal(450, fixture.Saved().Queue.MaxItems);

            // "Apply and close" with a validation error keeps the window and the typed value.
            window.ShowSettings();
            Draft(window).MaxItems = "5";
            answers.Enqueue(SettingsCloseChoice.Apply);
            window.Close();
            Assert.True(window.IsVisible);
            Assert.Equal(1, fixture.ApplyCount());
            Assert.Equal("5", Draft(window).MaxItems);
            Assert.Equal(Visibility.Visible, Assert.IsType<Border>(window.FindName("ErrorSummary")).Visibility);

            // The Cancel button is the deliberate discard: it never asks.
            var cancel = Assert.IsType<Button>(window.FindName("CancelButton"));
            Assert.False(cancel.IsCancel);
            cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.False(window.IsVisible);
            Assert.Equal(4, asked);
            Assert.Equal(450, fixture.Saved().Queue.MaxItems);

            // "Reset to defaults" is pending until Apply, so closing asks.
            window.ShowSettings();
            Assert.IsType<Button>(window.FindName("DefaultsButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(Draft(window).HasUnappliedChanges);
            answers.Enqueue(SettingsCloseChoice.Discard);
            window.Close();
            Assert.Equal(5, asked);
            Assert.False(window.IsVisible);

            // The shared exit/update gate preserves a canceled or invalid draft and keeps
            // the Store consent owner visible after a successful apply.
            window.ShowSettings();
            Draft(window).MaxItems = "555";
            answers.Enqueue(SettingsCloseChoice.Cancel);
            Assert.False(window.PrepareForAppExit(keepVisible: true));
            Assert.Equal("555", Draft(window).MaxItems);
            Draft(window).MaxItems = "5";
            answers.Enqueue(SettingsCloseChoice.Apply);
            Assert.False(window.PrepareForAppExit(keepVisible: true));
            Assert.True(window.IsVisible);
            answers.Enqueue(SettingsCloseChoice.Discard);
            Assert.True(window.PrepareForAppExit(keepVisible: true));
            Assert.Equal("450", Draft(window).MaxItems);
            Draft(window).MaxItems = "555";
            answers.Enqueue(SettingsCloseChoice.Apply);
            Assert.True(window.PrepareForAppExit(keepVisible: true));
            Assert.Equal(555, fixture.Saved().Queue.MaxItems);
            Assert.True(window.IsVisible);

            // After the shared guard, the final close does not prompt again.
            window.ShowSettings();
            Draft(window).MaxItems = "999";
            window.CloseForExit();
            closed = true;
            Assert.Equal(9, asked);
        }
        finally
        {
            if (!closed) window.CloseForExit();
            application.Shutdown();
        }
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
                    TabItem[] items = tabs.Items.OfType<TabItem>().ToArray();
                    string[] expected = ["SettingsUx_TabGeneral", "SettingsUx_TabCapture", "SettingsUx_TabRecording",
                        "SettingsUx_TabEditing", "SettingsUx_TabShortcuts", "SettingsUx_TabStorage", "SettingsUx_TabAbout"];
                    Assert.Equal(expected.Select(UiText.Get), items.Select(tab => (string)tab.Header));
                    Assert.Equal(items.Select(tab => (string)tab.Header), items.Select(AutomationProperties.GetName));
                    Assert.All(items, tab => Assert.False(string.IsNullOrWhiteSpace(tab.ToolTip as string)));
                    Assert.Equal(items.Length, items.Select(tab => tab.Tag).Distinct().Count());
                    Assert.All(items, tab => Assert.IsAssignableFrom<Geometry>(tab.Tag));

                    // Every category is reachable without scrolling the list, at the default and minimum sizes.
                    var categories = Assert.IsType<ScrollViewer>(tabs.Template.FindName("CategoryScroll", tabs));
                    Assert.Equal(0, categories.ScrollableHeight, 1);
                    window.Width = window.MinWidth;
                    window.Height = window.MinHeight;
                    Drain(window);
                    Assert.Equal(0, categories.ScrollableHeight, 1);
                    window.Width = 940;
                    window.Height = 700;

                    // The title bar is the only title: no product header, navigation heading or
                    // page heading repeats the window or category name.
                    string[] retired = [UiText.Get("Text_115BCED49291"), UiText.Get("Text_F5BF49635D43"),
                        UiText.Get("Text_BDA4A255187A"), UiText.Get("Text_FEAA5199651D")];
                    Style heading = Assert.IsType<Style>(window.FindResource("Settings.GroupHeading"));
                    Button primary = Assert.IsType<Button>(window.FindName("ApplyButton"));
                    foreach (TabItem tab in items)
                    {
                        tabs.SelectedItem = tab;
                        Drain(window);
                        TextBlock[] texts = Descendants(window).OfType<TextBlock>().Where(text => text.IsVisible).ToArray();
                        Assert.DoesNotContain(texts, text => retired.Contains(text.Text));
                        Assert.DoesNotContain(texts, text => ReferenceEquals(text.Style, heading) && text.Text == (string)tab.Header);
                        // One filled button per screen: Apply.
                        Button[] filled = Descendants(window).OfType<Button>()
                            .Where(button => button.IsVisible && ReferenceEquals(button.Style, window.FindResource("Button.Primary")))
                            .ToArray();
                        Assert.Equal(new[] { primary }, filled);
                    }

                    // Labels carry no key notation; keys are in tooltips and accelerator properties.
                    Assert.Equal(UiText.Get("Text_6A1C963D5BC5"), primary.Content);
                    Assert.Equal("Ctrl+S", AutomationProperties.GetAcceleratorKey(primary));
                    // Nothing is pending, so the secondary command closes instead of cancelling.
                    Assert.Equal(UiText.Get("Dialog.Close"), Assert.IsType<Button>(window.FindName("CancelButton")).Content);

                    // Settings backup moved off the command bar onto the About page.
                    var export = Assert.IsType<Button>(window.FindName("ExportSettingsButton"));
                    var import = Assert.IsType<Button>(window.FindName("ImportSettingsButton"));
                    var about = Assert.IsType<TabItem>(window.FindName("UpdateTab"));
                    Assert.Same(about, items[^1]);
                    Assert.True(IsInside(export, about) && IsInside(import, about));

                    // The retention description states the period that will apply.
                    tabs.SelectedItem = items[5];
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
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, UiText.Get("SettingsUx_TabStorage")));
            Drain(window);

            var advanced = Assert.IsType<Expander>(window.FindName("AdvancedExpander"));
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
            foreach ((string name, AppSettings settings, bool expectedHints) in new[]
            {
                ("new", NewInstallDefaults.Create(), true),
                ("existing", new AppSettings(), true),
                ("explicit-hints-off", new AppSettings { General = { ShowShortcutHints = false } }, false),
            })
            {
                SettingsWindow window = CreateSettingsUxWindow(settings).Window;
                try
                {
                    window.Show();
                    var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
                    tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, UiText.Get("SettingsUx_TabShortcuts")));
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
                    if (name == "new")
                    {
                        Assert.All(recorders.Skip(2), recorder => Assert.Equal(string.Empty, recorder.Text));
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

                    // Assigning an unassigned command takes one recorder interaction.
                    paste.Accept(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, Hotkey.VkF3));
                    Assert.Equal("Ctrl+Alt+F3", Draft(window).PasteToScreenHotkey);
                    Assert.True(Draft(window).HasUnappliedChanges);

                    var hints = Assert.IsType<CheckBox>(window.FindName("ShowShortcutHintsCheck"));
                    Assert.Equal(expectedHints, hints.IsChecked);
                    Assert.True(hints.IsVisible);
                    SaveSettingsUxEvidence(RenderEditorWindow(window), $"settings-shortcuts-{name}-install-glass-after.png");
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
                            buttons.Select(button => (string)button.Content));
                        Assert.Equal(buttons.Select(button => (string)button.Content), buttons.Select(AutomationProperties.GetName));
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
}
