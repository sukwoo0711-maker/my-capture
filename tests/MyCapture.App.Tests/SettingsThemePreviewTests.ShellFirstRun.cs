using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.App.Settings;
using MyCapture.App.Store;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    private const string ShellFirstRunEvidence = "MYCAPTURE_SHELL_FIRSTRUN_EVIDENCE";

    /// <summary>
    /// Renders the shell surfaces a new user meets: the welcome, the tray menu, the shortcut
    /// conflict notification, the rating invitation over settings, draft recovery and a tutorial
    /// whose shortcut is unassigned.
    /// </summary>
    /// <remarks>
    /// Set <c>MYCAPTURE_SHELL_FIRSTRUN_EVIDENCE</c> to a directory to keep the PNGs, and
    /// <c>MYCAPTURE_SHELL_FIRSTRUN_PHASE</c> to name them (default "after").
    /// </remarks>
    [Fact]
    public Task ShellFirstRun_SurfacesRenderInEvidenceThemes() =>
        RunIsolated(nameof(ShellFirstRun_SurfacesRenderInEvidenceThemes), RenderShellFirstRunEvidence);

    private static void RenderShellFirstRunEvidence()
    {
        string? requested = Environment.GetEnvironmentVariable(ShellFirstRunEvidence);
        string? root = string.IsNullOrWhiteSpace(requested) ? null : Directory.CreateDirectory(requested).FullName;
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_SHELL_FIRSTRUN_PHASE") is { Length: > 0 } label ? label : "after";
        Application application = CreateThemedApplication();
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            foreach (string locale in new[] { "ko-KR", "en-US" })
            {
                using var language = UiText.UseLanguage(locale);
                UiText.Configure(locale[..2]);
                ThemeService.Apply(AppTheme.Glass);
                void Save(BitmapSource image, string name)
                {
                    Assert.True(image.PixelWidth > 0 && image.PixelHeight > 0);
                    if (root is not null) SaveBitmap(image, Path.Combine(root, $"{name}-{locale}-{phase}.png"));
                }

                var welcome = new WelcomeWindow(new HotkeySettings(), AfterCaptureAction.Editor)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
                };
                welcome.Show();
                Drain(welcome);
                Save(RenderWindow(welcome), "welcome");
                welcome.Close();

                var offDesktop = new MonitorInfo("EVIDENCE", new RectD(-20000, -20000, 1080, 1920), new RectD(-20000, -20000, 1080, 1880), 96, false);
                using (var presenter = new ThemedShellPresenter(() => false) { NotificationMonitor = () => offDesktop })
                {
                    var commands = new TrayMenuCommands(() => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { },
                        () => { }, () => { }, () => { }, () => { }, () => { }, () => { }, PinClipboard: () => { }, HideOrShowAllPins: () => { });
                    IReadOnlyList<ThemedShellPresenter.MenuEntry> entries = TrayMenuLayout.Build(commands, NewInstallDefaults.Create().Hotkeys,
                        new TrayMenuState(CanRestoreClosedPin: true, ClipboardHasContent: false, PinCount: 2));
                    Save(RenderMenu(presenter.CreateMenu(entries), openBranch: 3), "tray-menu");

                    presenter.ShowActionNotification(UiText.Get("Text_5564316AA7EB"),
                        UiText.Format("Text_114E1105C3EE", "Ctrl+Shift+C", string.Empty), UiText.Get("Shell_ChangeShortcut"), () => { },
                        TrayBalloonKind.Error);
                    Window toast = presenter.NotificationWindowForTest!;
                    Drain(toast);
                    Save(RenderOnBackdrop(DetachContent(toast), toast.ActualWidth), "conflict-toast");
                    presenter.Dismiss();
                }

                SettingsWindow settings = CreateOffscreenSettingsWindow(new AppSettings());
                try
                {
                    var notice = new EngagementNotice();
                    EngagementNoticeHost.Attach(settings, notice, bottomInset: 76);
                    settings.Show();
                    notice.ShowUpdateCompleted("4.1.2.0");
                    Drain(settings);
                    Save(RenderWindow(settings), "settings-update-notice");
                    notice.Dismiss();
                    notice.ShowInvitation();
                    Drain(settings);
                    Save(RenderWindow(settings), "settings-rating-invitation");
                }
                finally { settings.CloseForExit(); }

                DateTimeOffset saved = DateTimeOffset.Now.AddHours(-3);
                var recovery = new DraftRecoveryDialog(
                [
                    new DraftRecoveryItem(Guid.NewGuid(), false, saved, 640, 360, EvidenceThumbnail()),
                    new DraftRecoveryItem(Guid.NewGuid(), true, saved.AddMinutes(-12), 1280, 720, null),
                ], (_, _) => Task.FromResult(false))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false,
                };
                recovery.Show();
                Drain(recovery);
                Save(RenderWindow(recovery), "draft-recovery");
                recovery.Close();

                Action<string>? previousOpen = FeatureTutorialWindow.OpenShortcutSetting;
                FeatureTutorialWindow.OpenShortcutSetting = _ => { };
                var tutorial = new FeatureTutorialWindow(NewInstallDefaults.Create().Hotkeys, "pin-clipboard", () => new FakeTutorialMedia())
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, Width = 720, Height = 780,
                };
                tutorial.Show();
                Drain(tutorial);
                Save(RenderWindow(tutorial), "tutorial-unassigned");
                tutorial.Close();
                FeatureTutorialWindow.OpenShortcutSetting = previousOpen;
            }
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            application.Shutdown();
        }
    }

    /// <summary>
    /// Measures what opening the tray menu costs on the UI thread: the entries with every check
    /// the shell makes when the menu opens, and the themed menu laid out at its open size. The
    /// popup itself is not opened, so nothing appears on the desktop.
    /// </summary>
    /// <remarks>
    /// Runs only when <c>MYCAPTURE_TRAY_MENU_PERF</c> names an output file; the median of the
    /// measured rounds and every round are appended with <c>MYCAPTURE_TRAY_MENU_PERF_LABEL</c>.
    /// </remarks>
    [Fact]
    public Task ShellFirstRun_TrayMenuOpenCostIsMeasured() =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYCAPTURE_TRAY_MENU_PERF"))
            ? Task.CompletedTask
            : RunIsolated(nameof(ShellFirstRun_TrayMenuOpenCostIsMeasured), MeasureTrayMenuOpen);

    private static void MeasureTrayMenuOpen()
    {
        string output = Environment.GetEnvironmentVariable("MYCAPTURE_TRAY_MENU_PERF")!;
        string label = Environment.GetEnvironmentVariable("MYCAPTURE_TRAY_MENU_PERF_LABEL") ?? "run";
        using var language = UiText.UseLanguage("ko-KR");
        Application application = CreateThemedApplication();
        try
        {
            ThemeService.Apply(AppTheme.Glass);
            var commands = new TrayMenuCommands(() => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { },
                () => { }, () => { }, () => { }, () => { }, () => { }, () => { });
            HotkeySettings keys = NewInstallDefaults.Create().Hotkeys;
            using var presenter = new ThemedShellPresenter(() => false);
            // Interleaved with the same menu built without the pin commands and the clipboard
            // check (the earlier menu's size), so machine load affects both sides alike.
            var variants = new (string Name, Func<IReadOnlyList<ThemedShellPresenter.MenuEntry>> Build)[]
            {
                ("without-pin-commands", () => TrayMenuLayout.Build(commands, keys, canRestoreClosedPin: false, scrollingActive: false)),
                ("as-opened", () => BuildTrayMenuForOpen(commands, keys)),
            };
            var samples = variants.ToDictionary(variant => variant.Name, _ => new List<double>());
            for (int round = 0; round < 43; round++)
            {
                IEnumerable<(string Name, Func<IReadOnlyList<ThemedShellPresenter.MenuEntry>> Build)> order =
                    round % 2 == 0 ? variants : Enumerable.Reverse(variants);
                foreach ((string name, Func<IReadOnlyList<ThemedShellPresenter.MenuEntry>> build) in order)
                {
                    long start = System.Diagnostics.Stopwatch.GetTimestamp();
                    ContextMenu menu = presenter.CreateMenu(build());
                    menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    menu.Arrange(new Rect(menu.DesiredSize));
                    menu.UpdateLayout();
                    double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    Assert.True(menu.Items.Count > 0 && menu.ActualHeight > 0);
                    if (round >= 3) samples[name].Add(elapsed); // The first rounds pay one-time template and JIT costs.
                }
            }
            long checkStart = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < 10000; i++) _ = MyCapture.App.Pinning.ClipboardAvailability.HasPinnableContent();
            double checkMicroseconds = System.Diagnostics.Stopwatch.GetElapsedTime(checkStart).TotalMilliseconds * 1000 / 10000;
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            foreach ((string name, List<double> values) in samples)
            {
                values.Sort();
                File.AppendAllText(output, string.Create(invariant,
                    $"{label}/{name}\tmedian_ms={values[values.Count / 2]:0.000}\trounds={values.Count}\tclipboard_check_us={checkMicroseconds:0.00}{Environment.NewLine}"));
            }
        }
        finally { application.Shutdown(); }
    }

    /// <summary>The entries exactly as the shell builds them each time the menu opens, clipboard check included.</summary>
    private static IReadOnlyList<ThemedShellPresenter.MenuEntry> BuildTrayMenuForOpen(TrayMenuCommands commands, HotkeySettings keys) =>
        TrayMenuLayout.Build(commands with { PinClipboard = () => { }, HideOrShowAllPins = () => { } }, keys,
            new TrayMenuState(false, false, MyCapture.App.Pinning.ClipboardAvailability.HasPinnableContent(), 0, false));

    /// <summary>
    /// Every shortcut the shell can point at has a recorder in Settings, and ShowShortcut opens the
    /// Shortcuts tab with focus on that row.
    /// </summary>
    [Fact]
    public Task ShellFirstRun_ShortcutRowsOpenFocusedInSettings() =>
        RunIsolated(nameof(ShellFirstRun_ShortcutRowsOpenFocusedInSettings), VerifyShortcutRowFocus);

    private static void VerifyShortcutRowFocus()
    {
        using var language = UiText.UseLanguage("ko-KR");
        Application application = CreateThemedApplication();
        SettingsWindow window = CreateOffscreenSettingsWindow(new AppSettings());
        try
        {
            foreach (GlobalHotkeyCommand command in Enum.GetValues<GlobalHotkeyCommand>())
                Assert.NotNull(window.FindHotkeyRecorder(ShortcutSettingTarget.ForCommand(command)));
            foreach (FeatureTutorial feature in TutorialCatalog.All)
                if (ShortcutSettingTarget.ForFeature(feature.Id) is { } row) Assert.NotNull(window.FindHotkeyRecorder(row));

            var tabs = Assert.IsType<TabControl>(window.FindName("CategoryTabs"));
            window.ShowShortcut(nameof(SettingsDraft.PasteToScreenHotkey));
            Drain(window);
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            HotkeyRecorder recorder = window.FindHotkeyRecorder(nameof(SettingsDraft.PasteToScreenHotkey))!;
            Assert.Equal(UiText.Get("SettingsUx_TabShortcuts"), System.Windows.Automation.AutomationProperties.GetName((TabItem)tabs.SelectedItem));
            Assert.True(recorder.IsVisible);
            // Focus is on the row's record button, inside the recorder.
            var focused = System.Windows.Input.FocusManager.GetFocusedElement(window) as DependencyObject;
            Assert.NotNull(focused);
            Assert.True(recorder.IsAncestorOf(focused!), $"Focus is on {focused}");
            SaveShellEvidence(window, "settings-shortcut-focus");

            // The settings tutorials' accessible names contain their visible label.
            foreach (Button button in Descendants(window).OfType<Button>().Where(button => Equals(button.Content, UiText.Get("TextCapture_TutorialOpen"))))
                Assert.EndsWith(" 사용법 보기", System.Windows.Automation.AutomationProperties.GetName(button), StringComparison.Ordinal);
        }
        finally
        {
            window.CloseForExit();
            application.Shutdown();
        }
    }

    private static void SaveShellEvidence(Window window, string name)
    {
        string? requested = Environment.GetEnvironmentVariable(ShellFirstRunEvidence);
        if (string.IsNullOrWhiteSpace(requested)) return;
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_SHELL_FIRSTRUN_PHASE") is { Length: > 0 } label ? label : "after";
        SaveBitmap(RenderWindow(window), Path.Combine(Directory.CreateDirectory(requested).FullName, $"{name}-ko-KR-{phase}.png"));
    }

    /// <summary>A small capture-like picture for the draft recovery evidence.</summary>
    private static BitmapSource EvidenceThumbnail()
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF9)), null, new Rect(0, 0, 192, 108));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2F, 0x55, 0x7A)), null, new Rect(0, 0, 192, 18));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xC9, 0xD3, 0xE0)), null, new Rect(14, 30, 110, 10));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xC9, 0xD3, 0xE0)), null, new Rect(14, 48, 150, 10));
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), 3), new Rect(10, 26, 120, 36));
        }
        var bitmap = new RenderTargetBitmap(192, 108, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Moves a window's laid-out content onto a detached host so it can be drawn on a backdrop.</summary>
    private static FrameworkElement DetachContent(Window window)
    {
        var content = (FrameworkElement)window.Content;
        double width = window.ActualWidth;
        window.Content = null;
        var host = new Border { Width = width, Child = content, Background = window.Background };
        host.SetResourceReference(TextElement.ForegroundProperty, "Text.Primary");
        return host;
    }
}
