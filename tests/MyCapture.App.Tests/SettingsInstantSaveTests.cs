using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsInstantSaveTests
{
    [Fact]
    public Task InstantSavePersistsDebouncesAndKeepsInvalidValuesInline() => Isolated(nameof(InstantSavePersistsDebouncesAndKeepsInvalidValuesInline), () =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        Application app = CreateApplication();
        string root = TestRecycleBin.CreateTempSubdirectory("mc-instant-").FullName;
        var store = new SettingsStore(AppPaths.CreateForRoot(root), NullLogger<SettingsStore>.Instance);
        AppSettings saved = NewInstallDefaults.Create();
        store.Save(saved);
        int saves = 0;
        bool rejectHotkey = false, failSave = false;
        var window = new SettingsWindow(() => saved, candidate =>
        {
            saves++;
            if (failSave) return SettingsApplyResult.NotSaved(["Synthetic save failure"]);
            bool reject = rejectHotkey && candidate.Hotkeys.Capture != saved.Hotkeys.Capture;
            if (reject) candidate.Hotkeys = saved.DeepClone().Hotkeys;
            bool languageChanged = candidate.General.Language != saved.General.Language;
            store.Save(candidate);
            saved = candidate;
            return new SettingsApplyResult(true, !reject, true, languageChanged,
                reject ? ["Synthetic hotkey collision"] : languageChanged ? [UiText.Get("Settings.LanguageRestart")] : []);
        }, NullLogger.Instance, settingsStore: () => store);
        try
        {
            SettingsDraft draft = (SettingsDraft)window.DataContext;
            draft.PlayCaptureSound = !draft.PlayCaptureSound;
            Assert.Equal(0, saves); // A setter has not finished validating at notification time.
            Pump();
            Assert.Equal(1, saves);
            Assert.Equal(draft.PlayCaptureSound, store.Load().General.PlayCaptureSound);
            Assert.Same(draft, window.DataContext);
            Assert.Equal(UiText.Get("Settings_Saved"), ((TextBlock)window.FindName("SettingsStatus")).Text);

            int priorDelay = saved.Capture.DelaySeconds;
            window.ImportSettingsFrom(Path.Combine(root, "missing-settings.json"));
            string importFailure = ((TextBlock)window.FindName("SettingsStatus")).Text;
            Assert.StartsWith(UiText.Get("Settings_ImportFailed"), importFailure, StringComparison.Ordinal);
            Pump(2100);
            Assert.Equal(importFailure, ((TextBlock)window.FindName("SettingsStatus")).Text);

            draft.DelaySeconds = "6";
            Pump(180);
            draft.DelaySeconds = "7";
            draft.ShowMagnifier = !draft.ShowMagnifier;
            Pump();
            Assert.Equal(2, saves);
            Assert.Equal(priorDelay, store.Load().Capture.DelaySeconds); // A toggle never flushes typing early.
            Pump(200);
            Assert.Equal(2, saves);
            Pump(250);
            Assert.Equal(3, saves);
            Assert.Equal(7, store.Load().Capture.DelaySeconds);

            Hotkey original = saved.Hotkeys.Capture;
            draft.CaptureHotkey = "not a shortcut";
            draft.IncludeCursor = !draft.IncludeCursor;
            Pump(450);
            Assert.Equal(original, store.Load().Hotkeys.Capture);
            Assert.Equal(draft.IncludeCursor, saved.Capture.IncludeCursor);
            Assert.True(draft.GetErrors(nameof(SettingsDraft.CaptureHotkey)).Cast<string>().Any());
            Assert.Equal(Visibility.Visible, ((Border)window.FindName("ErrorSummary")).Visibility);
            Assert.DoesNotContain(UiText.Get("Settings_Saved"), ((TextBlock)window.FindName("SettingsStatus")).Text);

            rejectHotkey = true;
            draft.CaptureHotkey = "Ctrl+Alt+F8";
            Pump(450);
            Assert.Equal(original, saved.Hotkeys.Capture);
            Assert.Contains("Synthetic hotkey collision", draft.GetErrors(nameof(SettingsDraft.CaptureHotkey)).Cast<string>());
            draft.PlayCaptureSound = !draft.PlayCaptureSound;
            Pump();
            Assert.Contains("Synthetic hotkey collision", draft.GetErrors(nameof(SettingsDraft.CaptureHotkey)).Cast<string>());
            rejectHotkey = false;
            draft.CaptureHotkey = original.ToString();
            Assert.True(window.FlushPendingSettings());

            // Another editor changed a setting while this window remained open.
            saved.Annotation.StrokeThickness = 7;
            long exactByteBudget = 2 * SettingsRanges.BytesPerGiB + 12345;
            saved.Queue.MaxBytes = exactByteBudget;
            draft.NotifyOnQuickSave = !draft.NotifyOnQuickSave;
            Pump();
            Assert.Equal(7, store.Load().Annotation.StrokeThickness);
            Assert.Equal(exactByteBudget, store.Load().Queue.MaxBytes);
            draft.ShowShortcutHints = !draft.ShowShortcutHints;
            Pump();
            Assert.Equal(7, saved.Annotation.StrokeThickness);

            draft.Language = "en-US";
            Pump();
            string restart = UiText.Get("Settings.LanguageRestart");
            Assert.Contains(restart, ((TextBlock)window.FindName("SettingsStatus")).Text);
            draft.ShowMagnifier = !draft.ShowMagnifier;
            Pump(2100);
            Assert.Contains(restart, ((TextBlock)window.FindName("SettingsStatus")).Text);

            failSave = true;
            bool priorSound = draft.PlayCaptureSound;
            draft.PlayCaptureSound = !priorSound;
            Pump();
            Assert.False(window.FlushPendingSettings());
            draft.PlayCaptureSound = priorSound;
            Assert.True(window.FlushPendingSettings()); // Reverting a failed edit cannot trap Close.
            failSave = false;
            draft.DelaySeconds = "4";
            int beforeClose = saves;
            window.RequestClose();
            Assert.Equal(4, store.Load().Capture.DelaySeconds);
            Pump(450);
            Assert.Equal(beforeClose + 1, saves); // Queued callback/timer cannot duplicate the flush.
        }
        finally { window.CloseForExit(); app.Shutdown(); }
    });

    [Fact]
    public Task MergedHotkeyValidationRecoversWhenRelatedFieldChanges() => Isolated(nameof(MergedHotkeyValidationRecoversWhenRelatedFieldChanges), () =>
    {
        Application app = CreateApplication();
        AppSettings saved = NewInstallDefaults.Create();
        Assert.True(Hotkey.TryParse("Ctrl+1", out Hotkey first));
        Assert.True(Hotkey.TryParse("Ctrl+2", out Hotkey second));
        Assert.True(Hotkey.TryParse("Ctrl+3", out Hotkey third));
        saved.Hotkeys.Capture = first;
        saved.Hotkeys.OpenLibrary = second;
        var window = new SettingsWindow(() => saved, candidate =>
        {
            saved = candidate;
            return new SettingsApplyResult(true, true, true, false, []);
        }, NullLogger.Instance);
        try
        {
            var draft = (SettingsDraft)window.DataContext;
            draft.CaptureHotkey = "invalid";
            draft.OpenLibraryHotkey = "Ctrl+1";
            draft.PlayCaptureSound = !draft.PlayCaptureSound;
            Pump(450);
            Assert.Equal(first, saved.Hotkeys.Capture);
            Assert.Equal(second, saved.Hotkeys.OpenLibrary);
            Assert.Equal(draft.PlayCaptureSound, saved.General.PlayCaptureSound);
            Assert.NotEmpty(draft.GetErrors(nameof(SettingsDraft.OpenLibraryHotkey)).Cast<string>());
            draft.CaptureHotkey = "Ctrl+3";
            Pump(450);
            Assert.Equal(third, saved.Hotkeys.Capture);
            Assert.Equal(first, saved.Hotkeys.OpenLibrary);
            Assert.False(draft.HasErrors);
            Assert.True(window.FlushPendingSettings());
        }
        finally { window.CloseForExit(); app.Shutdown(); }
    });

    [Fact]
    public Task SearchAndResetWorkInBothLanguagesWithoutShowingWindows() => Isolated(nameof(SearchAndResetWorkInBothLanguagesWithoutShowingWindows), () =>
    {
        Application app = CreateApplication();
        try
        {
            foreach (string languageName in new[] { "ko-KR", "en" })
            {
                using var language = UiText.UseLanguage(languageName);
                AppSettings saved = NewInstallDefaults.Create();
                saved.Capture.DelaySeconds = 9;
                var window = new SettingsWindow(() => saved, candidate =>
                {
                    saved = candidate;
                    return new SettingsApplyResult(true, true, true, false, []);
                }, NullLogger.Instance);
                try
                {
                    Render(window, $"settings-general-{languageName}.png", 940, 700);
                    var tabs = (TabControl)window.FindName("CategoryTabs");
                    var search = (TextBox)tabs.Template.FindName("SettingsSearchBox", tabs);
                    search.Text = UiText.Get("Settings.Language");
                    Pump();
                    Assert.Contains(tabs.Items.OfType<TabItem>(), tab => tab.Visibility == Visibility.Visible);
                    Assert.NotNull(window.FirstSearchMatch);
                    Assert.Equal(UiText.Get("SettingsUx_TabGeneral"), ((TabItem)tabs.SelectedItem).Header);
                    Render(window, $"settings-search-{languageName}.png", 940, 700);
                    search.Text = "no_such_setting_5390";
                    Assert.All(tabs.Items.OfType<TabItem>(), tab => Assert.Equal(Visibility.Collapsed, tab.Visibility));
                    search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, Key.Escape)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    Assert.Equal(string.Empty, search.Text);
                    Assert.All(tabs.Items.OfType<TabItem>(), tab => Assert.Equal(Visibility.Visible, tab.Visibility));

                    if (languageName == "en")
                    {
                        CultureInfo previousCulture = CultureInfo.CurrentCulture;
                        try
                        {
                            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
                            window.FilterSettings("EDITING");
                            Assert.Equal(Visibility.Visible, tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, UiText.Get("SettingsUx_TabEditing"))).Visibility);
                            window.FilterSettings("English");
                            Assert.IsNotType<ComboBoxItem>(window.FirstSearchMatch);
                        }
                        finally { CultureInfo.CurrentCulture = previousCulture; window.FilterSettings(string.Empty); }
                    }

                    var about = (TabItem)window.FindName("UpdateTab");
                    var reset = (Button)window.FindName("DefaultsButton");
                    Assert.Contains(reset, Descendants(about));
                    int confirmations = 0;
                    window.ConfirmResetDefaults = () => { confirmations++; return false; };
                    reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(9, saved.Capture.DelaySeconds);
                    window.ConfirmResetDefaults = () => { confirmations++; return true; };
                    reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(2, confirmations);
                    Assert.Equal(NewInstallDefaults.Create().Capture.DelaySeconds, saved.Capture.DelaySeconds);
                    Assert.Null(window.FindName("ApplyButton"));
                    tabs.SelectedItem = about;
                    Render(window, $"settings-about-{languageName}.png", 940, 700);
                }
                finally { window.CloseForExit(); }
            }
        }
        finally { app.Shutdown(); }
    });

    [Fact]
    public Task SupportActionsAreExplicitInjectedAndRenderable() => Isolated(nameof(SupportActionsAreExplicitInjectedAndRenderable), () =>
    {
        Application app = CreateApplication();
        try
        {
            foreach (string languageName in new[] { "ko-KR", "en" })
            foreach (bool packaged in new[] { false, true })
            {
                using var language = UiText.UseLanguage(languageName);
                var actions = new FakeSupportActions();
                var window = new SupportWindow(actions, _ => Snapshot(packaged));
                try
                {
                    Assert.Empty(actions.Opened);
                    Assert.Null(actions.Copied);
                    Assert.True(window.DiagnosticsBox.IsReadOnly);
                    Assert.Equal(packaged ? Visibility.Visible : Visibility.Collapsed, window.StoreReviewButton.Visibility);
                    window.MessageBox.Text = "검색 & scale + #? 한글";
                    window.CopyDiagnostics();
                    Assert.Contains(window.DiagnosticsBox.Text, actions.Copied);
                    Assert.Contains(window.MessageBox.Text, actions.Copied);
                    window.OpenGitHub();
                    Assert.StartsWith(SupportDiagnostics.IssueUri + "?title=", Assert.Single(actions.Opened));
                    window.OpenStoreReview();
                    Assert.Equal(packaged ? 2 : 1, actions.Opened.Count);
                    if (packaged) Assert.Equal(StoreRatingPolicy.ReviewUri, actions.Opened[1]);
                    Render(window, $"support-{languageName}-{packaged}.png", 680, 640);
                    Render(window, $"support-minimum-{languageName}-{packaged}.png", 480, 500, scale: 2);
                    foreach (Button button in Descendants((DependencyObject)window.Content).OfType<Button>().Where(button => button.Visibility == Visibility.Visible))
                    {
                        Assert.True(button.ActualWidth >= 24 && button.ActualWidth <= 432);
                        Assert.True(button.ActualHeight >= 24);
                        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                    }
                    var scroll = (ScrollViewer)window.Content;
                    Assert.True(scroll.ScrollableHeight > 0);
                    scroll.ScrollToEnd();
                    scroll.UpdateLayout();
                    Assert.Equal(scroll.ScrollableHeight, scroll.VerticalOffset, precision: 2);
                }
                finally { window.Close(); }
            }
        }
        finally { app.Shutdown(); }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en")]
    public void SupportUrlPreservesEncodingDiagnosticsAndUnicodeWithinLimit(string languageName)
    {
        using var language = UiText.UseLanguage(languageName);
        string diagnostics = SupportReport.Format(Snapshot(true));
        string message = "한글 & + = # ? /\n" + string.Concat(Enumerable.Repeat("가😀", 5000));
        string url = SupportReport.IssueUrl(diagnostics, message);
        Assert.True(url.Length <= SupportReport.MaximumIssueUrlLength);
        string encoded = url[(url.IndexOf("&body=", StringComparison.Ordinal) + 6)..];
        string body = Uri.UnescapeDataString(encoded);
        Assert.StartsWith(diagnostics, body);
        Assert.Contains("한글 & + = # ? /", body);
        Assert.DoesNotContain('\uFFFD', body);
        Assert.Equal(encoded, Uri.EscapeDataString(body));
        Assert.DoesNotContain(@"C:\Users\", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-name", SupportReport.Format(Snapshot(true) with
        { Build = @"C:\Users\private-name\build", WindowsVersion = @"C:\Users\private-name" }));
    }

    [Fact]
    public void TraySupportEntryRunsInjectedCommand()
    {
        bool opened = false;
        Action noop = () => { };
        var commands = new TrayMenuCommands(noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop, noop,
            ContactSupport: () => opened = true);
        var entries = TrayMenuLayout.Build(commands, new HotkeySettings(), new TrayMenuState());
        var support = Assert.Single(entries, entry => entry.Label == UiText.Get("Support_Title"));
        support.Action!();
        Assert.True(opened);
        Assert.NotSame(support, entries.Last());
    }

    private static SupportDiagnosticSnapshot Snapshot(bool packaged) => new("4.4.0.0", "4.4.0+abc123", packaged, "10.0.26100", 1.5, "ko-KR", "Glass");
    private sealed class FakeSupportActions : ISupportActions
    {
        internal string? Copied;
        internal List<string> Opened = [];
        public void CopyText(string text) => Copied = text;
        public bool TryOpenUri(string uri) { Opened.Add(uri); return true; }
    }
    private static Task Isolated(string name, Action action) => SettingsThemePreviewTests.RunIsolated(name, action, typeof(SettingsInstantSaveTests));
    private static Application CreateApplication()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        ThemeService.Apply(AppTheme.Glass);
        return app;
    }
    private static void Pump(int milliseconds = 1)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (DependencyObject item in Descendants(child)) yield return item;
        }
    }
    private static void Render(Window window, string name, double width, double height, double scale = 1)
    {
        // Render the content tree without Show, an HWND, activation or real input.
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        Pump();
        var image = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (DrawingContext drawing = background.RenderOpen())
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        image.Render(background);
        image.Render(content);
        if (Environment.GetEnvironmentVariable("MYCAPTURE_L7_EVIDENCE") is not { Length: > 0 } root) return;
        Directory.CreateDirectory(root);
        using FileStream file = File.Create(Path.Combine(root, name));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(file);
    }
    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
