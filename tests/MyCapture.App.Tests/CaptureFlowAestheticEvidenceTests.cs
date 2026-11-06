using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using Xunit;
using Path = System.IO.Path;

namespace MyCapture.App.Tests;

/// <summary>Opt-in render/measurement fixture; no capture, clipboard, profile or real input.</summary>
public sealed class CaptureFlowAestheticEvidenceTests
{
    private const string Gate = "MYCAPTURE_AES_CAPTURE_FLOW";
    private const string Batch = "MYCAPTURE_AES_CAPTURE_FLOW_SCALE";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Color Light = Color.FromRgb(243, 243, 243);
    private static readonly Color Dark = Color.FromRgb(31, 31, 31);

    [Fact]
    public async Task RenderCaptureFlowMatrix()
    {
        string? requested = Environment.GetEnvironmentVariable(Gate);
        if (string.IsNullOrWhiteSpace(requested)) return;
        Assert.True(Path.IsPathFullyQualified(requested), "Evidence output must be absolute.");
        string root = DiagnosticOutputPaths.Create(requested);
        // Reuse the existing isolated WPF process harness, one DPI per child to keep its timeout.
        if (Environment.GetEnvironmentVariable("MYCAPTURE_SETTINGS_THEME_TEST_CHILD") == "1")
        {
            int percent = int.Parse(Environment.GetEnvironmentVariable(Batch)!, CultureInfo.InvariantCulture);
            await SettingsThemePreviewTests.RunIsolated(nameof(RenderCaptureFlowMatrix),
                () => RenderBatch(root, percent), typeof(CaptureFlowAestheticEvidenceTests));
            return;
        }
        string? previous = Environment.GetEnvironmentVariable(Batch);
        try
        {
            foreach (int percent in new[] { 125, 150, 100 })
            {
                Environment.SetEnvironmentVariable(Batch, percent.ToString(CultureInfo.InvariantCulture));
                await SettingsThemePreviewTests.RunIsolated(nameof(RenderCaptureFlowMatrix),
                    () => throw new InvalidOperationException("Parent must use the isolated harness."),
                    typeof(CaptureFlowAestheticEvidenceTests));
            }
        }
        finally { Environment.SetEnvironmentVariable(Batch, previous); }
        Report combined = new();
        foreach (int percent in new[] { 100, 125, 150 })
        {
            Report part = JsonSerializer.Deserialize<Report>(File.ReadAllText(
                DiagnosticOutputPaths.Child(root, $"metrics-{percent}.json")), JsonOptions)!;
            combined.Renders.AddRange(part.Renders);
            combined.Contrast.AddRange(part.Contrast);
            combined.Geometry.AddRange(part.Geometry);
            combined.Borders.AddRange(part.Borders);
            combined.Notes.AddRange(part.Notes);
        }
        combined.Notes = combined.Notes.Distinct().ToList();
        combined.Motion = ScanMotion();
        File.WriteAllText(DiagnosticOutputPaths.Child(root, "metrics.json"), JsonSerializer.Serialize(combined, JsonOptions));
        File.WriteAllText(DiagnosticOutputPaths.Child(root, "metrics.md"), Markdown(combined));
        Assert.All(new[] { "selector", "card", "notify", "tray", "countdown", "scroll", "recording" },
            surface => Assert.Contains(combined.Renders, row => row.Surface == surface));
        Assert.Equal(combined.Renders.Count, combined.Renders.Select(row => row.File).Distinct().Count());
    }

    private static void RenderBatch(string root, int percent)
    {
        // Keep HWND placement at the test thread's 96 DPI; vary only the WPF render DPI.
        // A fictitious 120/144-DPI monitor cannot change Windows' real HWND size constraints.
        using var nativeDpi = TestDpiContext.At96Dpi();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        AppTheme previousTheme = ThemeService.Current;
        bool applied = ThemeService.IsAppliedForTest is not null;
        bool? highContrast = ThemeService.SystemHighContrastOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        ThemeService.SystemHighContrastOverrideForTest = false;
        var report = new Report();
        var session = new Evidence(root, percent / 100d, report);
        using var keyboard = new ReceiptKeyboardRouter(_ => new EvidenceHotkeyRegistrar());
        using var presenter = new ThemedShellPresenter(() => false, keyboardRouter: keyboard);
        try
        {
            AppTheme[] themes = percent == 100
                ? [AppTheme.Daylight, AppTheme.Midnight, AppTheme.HighContrast]
                : [AppTheme.Daylight, AppTheme.Midnight];
            foreach (AppTheme theme in themes)
            foreach (string language in new[] { "ko-KR", "en" })
            {
                using var languageScope = UiText.UseLanguage(language);
                ThemeService.Apply(theme);
                session.Theme = theme;
                session.Language = language[..2];
                Selectors(session);
                Cards(session, keyboard);
                Notifications(session, presenter);
                if (theme == AppTheme.HighContrast) continue;
                Trays(session, presenter);
                Countdowns(session);
                Scrolling(session);
                Recordings(session);
            }
            session.ContactSheets();
            File.WriteAllText(DiagnosticOutputPaths.Child(root, $"metrics-{percent}.json"),
                JsonSerializer.Serialize(report, JsonOptions));
        }
        finally
        {
            foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
            ThemeService.SystemHighContrastOverrideForTest = highContrast;
            ThemeService.Apply(previousTheme);
            typeof(ThemeService).GetField("_applied", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, applied);
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            app.Shutdown();
        }
    }

    private static void Selectors(Evidence e)
    {
        var desktop = CaptureCueEvidenceTests.Desktop();
        foreach (string state in CaptureCueEvidenceTests.States.Concat(new[] { "magnifier-window", "key-panel", "ratio-drag" }))
        {
            CaptureOverlayView view;
            if (state == "magnifier-window")
            {
                view = new CaptureOverlayView(desktop, showMagnifier: true)
                {
                    ReadCursor = () => new PointD(300, 300), DetectWindows = true,
                    ReadMonitors = () => CaptureCueEvidenceTests.Monitors,
                    WindowCandidates = [CaptureCueEvidenceTests.Browser, CaptureCueEvidenceTests.Notes,
                        CaptureCueEvidenceTests.Taskbar, CaptureCueEvidenceTests.Shell],
                    CopyColorText = _ => Task.FromResult(true), TextCaptureAvailable = true,
                    ShowModeBar = true, OffersOtherActions = true,
                };
                CaptureCueEvidenceTests.UsePalette(view, CaptureGuidePalette.Create(e.Theme, null, false));
                view.InitializePointer();
            }
            else view = CaptureCueEvidenceTests.EvidenceView(desktop, e.Theme,
                state is "drag" or "ratio-drag" or "key-panel" ? "window-hover" : state);
            try
            {
                view.SimulatedPointerInside = true;
                if (state is "drag" or "ratio-drag")
                {
                    PointD pointer = new(300, 250);
                    view.ReadCursor = () => pointer;
                    view.InitializePointer();
                    if (state == "ratio-drag") view.SelectRatio(SelectionRatio.Ratio16x9);
                    view.SimulatePointerDown();
                    pointer = new PointD(520, 430);
                    view.SimulatePointerMove();
                }
                if (state == "key-panel") { view.ShowKeyPanel = true; view.NotifyFirstFramePresented(); }
                // Flushing is necessary even in colour mode: it builds the magnifier visual.
                view.FlushFeedbackForTest();
                // The harness changes DPI and queues new feedback. Settle that DPI before the final frame.
                _ = CaptureCueEvidenceTests.Render(view, e.Scale);
                view.FlushFeedbackForTest();
                RenderTargetBitmap bitmap = CaptureCueEvidenceTests.Render(view, e.Scale);
                e.Save("selector", state, view, bitmap, topLevel: false);
                if (state == "key-panel") Assert.NotNull(view.KeyPanelForTest);
                if (state == "ratio-drag") Assert.Contains("16:9", view.ReadoutForTest);
                if (state is "color-mode" or "magnifier-window")
                {
                    Assert.NotNull(view.MagnifierBitmapForTest);
                    Assert.False(Field<DrawingVisual>(view, "_magnifierVisual").ContentBounds.IsEmpty);
                    Assert.False(string.IsNullOrWhiteSpace(view.SampleLabelForTest));
                }
            }
            finally { view.ReleaseResources(); }
        }
        var palette = CaptureGuidePalette.Create(e.Theme, null, false);
        foreach ((string backdropName, Color backdrop) in new[] { ("desktop-light", Color.FromRgb(247, 248, 250)), ("desktop-dark", Color.FromRgb(30, 36, 49)) })
        {
            Color dimmed = Over(BrushColor(palette["CaptureGuide.Dimmer"]), backdrop);
            e.Pair("CaptureGuide.ChipSurface / CaptureGuide.Text", palette["CaptureGuide.Text"], palette["CaptureGuide.ChipSurface"], dimmed, backdropName);
            e.Pair("CaptureGuide.SelectionBorder / CaptureGuide.AccentText", palette["CaptureGuide.AccentText"], palette["CaptureGuide.SelectionBorder"], dimmed, backdropName);
            e.Pair("CaptureGuide.Dimmer / CaptureGuide.Text", palette["CaptureGuide.Text"], palette["CaptureGuide.Dimmer"], backdrop, backdropName);
        }
    }

    private static void Cards(Evidence e, ReceiptKeyboardRouter keyboard)
    {
        // Reuse the synthetic card sample; this helper never accesses capture or user storage.
        var sample = (BitmapSource)typeof(SettingsThemePreviewTests).GetMethod("FeedbackSample", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        foreach (string state in new[] { "normal", "saving", "saved", "copy-failed", "text-copy-failed", "unkept", "first-use-tip", "keyboard-focus", "narrow", "long-detail" })
        {
            double workWidth = state == "narrow" ? 260 : 1280;
            var bounds = new RectD(-12000, -12000, workWidth, 720);
            var monitor = new MonitorInfo("evidence", bounds, bounds, 96, true);
            var save = new TaskCompletionSource<bool>();
            QuickAccessWindow card = state == "long-detail"
                ? QuickAccessWindow.ForPendingCapture(sample, true, targetMonitor: monitor, keyboardRouter: keyboard, excludeFromCapture: false)
                : new QuickAccessWindow(sample, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
                    () => save.Task, () => { }, () => Task.FromResult("unused.png"), excludeFromCapture: false,
                    targetMonitor: monitor, copyText: () => Task.FromResult(new TextCaptureOutcome(TextCaptureStatus.Failed, null, 0)),
                    firstCaptureTip: state == "first-use-tip" ? Local("캡처한 이미지를 편집하거나 화면에 고정할 수 있습니다.", "Edit the capture or pin it to the screen.") : null,
                    keyboardRouter: keyboard);
            card.IsKept = true;
            string renderedState = state;
            try
            {
                ShowOffscreen(card);
                if (state is "saving" or "saved")
                {
                    card.ShortcutActions["Save"]();
                    card.ElapseProgressDelayForTest();
                    Assert.True(card.StatusForTest.Progress);
                    if (state == "saved") { save.SetResult(true); Pump(); Assert.Equal(FeedbackKind.Confirmation, card.StatusForTest.Kind); }
                }
                if (state == "copy-failed")
                    card.RunAsync("Copy", () => Task.FromResult(false), "QuickAccess.Copying", "QuickAccess.Copied").GetAwaiter().GetResult();
                if (state == "text-copy-failed") { card.ShortcutActions["CopyText"](); Pump(); }
                if (state == "unkept") { card.IsKept = false; card.Advance(TimeSpan.FromSeconds(2), interacting: false); }
                if (state == "long-detail") card.ShowCaptureFailure(
                    Local("캡처한 이미지를 저장하지 못했습니다. 저장 위치를 확인하십시오.", "The captured image could not be saved. Check the destination folder."),
                    Local("선택한 폴더에 파일을 만들 수 없습니다. 충분한 공간과 폴더 접근 권한을 확인한 후 다시 시도하십시오.", "A file cannot be created in the selected folder. Check the available space and folder access, then try again."));
                if (state == "keyboard-focus")
                {
                    Button first = card.ActionButtonsForTest[0];
                    // Isolate the focus visual without activating the HWND or issuing input.
                    first.Focusable = true;
                    bool focused = first.Focus();
                    Pump();
                    if (!focused || !first.IsKeyboardFocused) renderedState = "keyboard-focus-unavailable";
                    e.Report.Notes.Add($"card keyboard-focus {e.Tag}: Focus()={focused}; IsKeyboardFocused={first.IsKeyboardFocused}; Focusable={first.Focusable}. No native activation or input was issued.");
                }
                if (state == "narrow") Assert.False(QuickAccessActionPanel.LabelOf(card.ActionButtonsForTest[0])!.IsVisible);
                e.Window("card", renderedState, card);
            }
            finally { card.CloseNow(); save.TrySetResult(false); Pump(); }
        }
    }

    private static void Notifications(Evidence e, ThemedShellPresenter presenter)
    {
        foreach (string kind in new[] { "info", "warning", "error" })
            Notice(e, "notify", kind, Local("알림", "Notice"), Local("캡처 작업을 확인하십시오.", "Review the capture operation."));
        Window action = presenter.CreateActionNotification(UiText.Get("QuickAccess.Saved"), "MyCapture_20261106_131000.png", UiText.Get("QuickAccess.OpenFolder"), () => { });
        ShowAndSave(e, "notify", "action-saved", action);
        foreach (bool tip in new[] { false, true })
            ShowAndSave(e, "notify", tip ? "receipt-tip-pin" : "receipt", presenter.CreateCaptureReceipt(
                Local("캡처가 저장되었습니다.", "Capture saved."), () => { },
                tip: tip ? Local("필요한 작업을 선택하십시오.", "Choose the next action.") : null, pin: tip ? () => { } : null));
        foreach (Color color in new[] { Color.FromRgb(30, 144, 255), Colors.White })
        {
            string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            ShowAndSave(e, "notify", "color-" + hex[1..].ToLowerInvariant(), ThemedShellPresenter.CreateNotificationWindow(
                ThemedShellPresenter.CreateColorCopyContent(new ColorCopyFeedback(hex, color.R, color.G, color.B)),
                UiText.Get("Tools_ColorPicker"), new Thickness(20)));
        }
        Notice(e, "notify", "long-two-line", Local("캡처 작업 결과", "Capture result"),
            Local("캡처한 이미지를 지정한 폴더에 저장했습니다.\n파일을 열어 결과를 확인하십시오.", "The captured image has been saved to the selected folder.\nOpen the file to review the result."));
        e.Report.Notes.Add("Plain info/warning/error use the identical CreateNotificationWindow content from ShowNotification; kind only changes presentation lifetime. Factories are used without placement or clipboard commands.");
    }

    private static Window PlainNotice(string title, string message)
    {
        var content = new StackPanel();
        content.Children.Add(KoreanWordWrap.Show(new TextBlock { FontSize = TypeScale.BodyLarge, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }, title));
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) };
        KoreanWordWrap.SetText(body, message);
        content.Children.Add(body);
        content.Children.Add(new Button { Content = UiText.Get("Shell.Close"), MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Right });
        return ThemedShellPresenter.CreateNotificationWindow(content, title, new Thickness(20));
    }

    private static void Notice(Evidence e, string surface, string state, string title, string message) =>
        ShowAndSave(e, surface, state, PlainNotice(title, message));

    private static void ShowAndSave(Evidence e, string surface, string state, Window window)
    {
        try { ShowOffscreen(window); e.Window(surface, state, window); }
        finally { window.Close(); }
    }

    private static void Trays(Evidence e, ThemedShellPresenter presenter)
    {
        Action noOp = () => { };
        var commands = new TrayMenuCommands(noOp, noOp, noOp, noOp, noOp, noOp, noOp, noOp,
            noOp, noOp, noOp, noOp, noOp, PinClipboard: noOp, HideOrShowAllPins: noOp,
            ClearClickThrough: noOp, ContactSupport: noOp, InstallUpdate: noOp, FirstUseGuide: noOp);
        foreach ((string name, TrayMenuState state) in new[]
        {
            ("empty", new TrayMenuState(ClipboardHasContent: false)),
            ("pins", new TrayMenuState(CanRestoreClosedPin: true, PinCount: 2, ClickThroughPinCount: 1, UpdateAvailable: true, UpdateVersion: "4.11.0")),
            ("busy", new TrayMenuState(CanRestoreClosedPin: true, ScrollingActive: true, PinCount: 2, PinsHidden: true, UpdateAvailable: true, UpdateBusy: true)),
        })
        {
            var entries = TrayMenuLayout.Build(commands, NewInstallDefaults.Create().Hotkeys, state);
            ContextMenu menu = presenter.CreateMenu(entries);
            RenderMenu(name, menu);
            int branch = 0;
            foreach (var entry in entries.Where(entry => entry.Children is { Count: > 0 }))
                RenderMenu($"{name}-branch{++branch}", presenter.CreateMenu(entry.Children!));
        }
        e.Report.Notes.Add("Tray menus/submenus are laid out offscreen using the existing SettingsGuideTrayEvidenceTests harness. Native Popup placement clamps to a real monitor: live popup highlight/keyboard focus skipped to keep every surface offscreen. Menu alpha ring measures the menu visual bounds, not the native popup HWND.");

        void RenderMenu(string state, ContextMenu menu)
        {
            ResourceDictionary resources = SystemHighContrastThemeTests.LoadTheme();
            ThemeService.ApplyResources(e.Theme, resources, systemHighContrast: false);
            menu.Resources = resources;
            Layout(menu, e.Scale, new Size(440, double.PositiveInfinity));
            e.Save("tray", state, menu, Render(menu, e.Scale), topLevel: true);
        }
    }

    private static void Countdowns(Evidence e)
    {
        foreach ((int remaining, int ms) in new[] { (3, 0), (2, 60), (1, 500), (0, 40) })
        {
            var window = new CountdownWindow(3);
            try
            {
                ShowOffscreen(window);
                Field<DispatcherTimer>(window, "_timer").Stop();
                window.PoseAt(remaining, TimeSpan.FromMilliseconds(ms));
                e.Window("countdown", $"{remaining}-{ms}ms", window);
            }
            finally { window.Close(); }
        }
    }

    private static void Scrolling(Evidence e)
    {
        Notice(e, "scroll", "progress", UiText.Get("Text_6182AA2DFF8D"), UiText.Get("Text_82B423E5AFB8"));
        Notice(e, "scroll", "cancelled", UiText.Get("Text_6EB131FB1810"), UiText.Get("Text_F018AA1A2813"));
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tray-busy.ico");
        using FileStream stream = File.OpenRead(iconPath);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        foreach (int size in new[] { 16, 20, 24, 32 })
        {
            BitmapFrame source = decoder.Frames.OrderBy(frame => Math.Abs(frame.PixelWidth - size)).First();
            var icon = new Image { Source = source, Width = size / e.Scale, Height = size / e.Scale };
            Layout(icon, e.Scale, new Size(icon.Width, icon.Height));
            var bitmap = new RenderTargetBitmap(size, size, 96 * e.Scale, 96 * e.Scale, PixelFormats.Pbgra32);
            bitmap.Render(icon);
            e.Save("scroll", $"busy-icon-{size}px", icon, bitmap, topLevel: false);
        }
    }

    private static void Recordings(Evidence e)
    {
        foreach (string state in new[] { "ready", "recording", "paused", "saving", "saved" })
        {
            var window = new RecordingControlWindow(new RectD(200, 200, 640, 360), new RecordingSettings(),
                () => throw new InvalidOperationException("Evidence must never start a recorder."),
                () => throw new InvalidOperationException("Evidence must never create a recording."), NullLogger.Instance)
            { ReadPauseHotkey = () => Hotkey.None };
            try
            {
                if (state is "recording" or "paused" or "saving") window.PreviewRecordingForTest(state == "paused", TimeSpan.FromSeconds(63));
                if (state == "saving")
                {
                    // The post-recorder state preceding ShowCompletionStatus, as in PreviewSavedForTest.
                    typeof(RecordingControlWindow).GetField("_stopping", PrivateInstance)!.SetValue(window, true);
                    typeof(RecordingControlWindow).GetField("_finished", PrivateInstance)!.SetValue(window, true);
                    window.PauseButtonForTest.IsEnabled = false;
                    window.ShowCompletionStatus(UiText.Get("Text_528544494302"));
                }
                if (state == "saved") window.PreviewSavedForTest(TimeSpan.FromSeconds(63));
                // These three handlers relocate the HWND to a physical monitor. Omit placement only
                // for this evidence fixture; retain the real window/content/templates and dimensions.
                foreach ((string eventName, string methodName) in new[] { ("SourceInitialized", "OnSourceInitialized"), ("ContentRendered", "OnContentRendered"), ("DpiChanged", "OnDpiChanged") })
                {
                    EventInfo evt = typeof(Window).GetEvent(eventName)!;
                    MethodInfo method = typeof(RecordingControlWindow).GetMethod(methodName, PrivateInstance | BindingFlags.DeclaredOnly)!;
                    evt.RemoveEventHandler(window, method.CreateDelegate(evt.EventHandlerType!, window));
                }
                ShowOffscreen(window);
                e.Window("recording", state, window);
            }
            finally { window.CompleteAndClose(); }
        }
        e.Report.Notes.Add("Recording uses PreviewRecordingForTest/PreviewSavedForTest and ShowCompletionStatus, never a recorder. SourceInitialized/ContentRendered/DpiChanged native-placement handlers are detached in the fixture so the real window stays offscreen. Content and planned window size are retained; native monitor-placement/capture-exclusion lifecycle is outside this evidence.");
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, PrivateInstance)!.GetValue(target)!;
    private static string Local(string ko, string en) => UiText.Get("Shell.Close").Contains("닫", StringComparison.Ordinal) ? ko : en;

    private static void ShowOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = window.Top = -10000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        FluidMotion.SetWindowEntrance(window, false);
        TestDpiContext.Initialize96DpiWindow(window);
        window.Show();
        Pump();
        Assert.True(window.Left < -1000 && window.Top < -1000, "Evidence window left its offscreen area.");
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    private static void Layout(FrameworkElement element, double scale, Size constraint)
    {
        VisualTreeHelper.SetRootDpi(element, new DpiScale(scale, scale));
        element.Measure(constraint);
        element.Arrange(new Rect(new Point(), element.DesiredSize));
        element.UpdateLayout();
    }

    private static RenderTargetBitmap Render(FrameworkElement element, double scale)
    {
        Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale),
            (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private sealed class EvidenceHotkeyRegistrar : IHotkeyRegistrar
    {
        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        { errorCode = 0; errorMessage = string.Empty; return true; }
        public void Unregister(int id) { }
    }

    private sealed class Evidence(string root, double scale, Report report)
    {
        internal double Scale => scale;
        internal Report Report => report;
        internal AppTheme Theme { get; set; }
        internal string Language { get; set; } = "ko";
        internal string Tag => $"{Theme.ToString().ToLowerInvariant()}-{Language}-{scale * 100:0}";
        private string Folder(string surface) => DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, surface));

        internal void Window(string surface, string state, Window window)
        {
            // The card's visible-window DPI handler selects a physical monitor. Hide it
            // while changing visual DPI so it retains the synthetic monitor; reuse its HWND.
            bool card = window is QuickAccessWindow;
            if (card) window.Hide();
            VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
            window.InvalidateMeasure();
            window.UpdateLayout();
            if (card) { window.Show(); Pump(); }
            Assert.True(window.Left < -1000 && window.Top < -1000);
            DpiScale dpi = VisualTreeHelper.GetDpi(window);
            Assert.Equal(scale, dpi.DpiScaleX);
            Assert.Equal(scale, dpi.DpiScaleY);
            if (surface == "card" && state == "narrow")
            {
                Assert.Equal(260, window.MaxWidth);
                Assert.True(window.ActualWidth <= 260);
            }
            Save(surface, state, window, Render(window, scale), topLevel: true);
        }

        internal void Save(string surface, string state, FrameworkElement element, BitmapSource bitmap, bool topLevel)
        {
            string name = $"{surface}-{state}-{Tag}";
            var shadows = Walk(element).Where(item => item.Node is UIElement { Effect: DropShadowEffect })
                .Select(item => { var effect = (DropShadowEffect)((UIElement)item.Node).Effect; return new ShadowRow(item.Path, effect.BlurRadius, effect.ShadowDepth, effect.Direction, effect.Opacity, effect.Color.ToString()); }).ToList();
            Store(bitmap, name + ".png", "transparent", topLevel ? EdgeAlpha(bitmap) : null, shadows);
            Inventory(element, name, scale, report);
            if (topLevel)
            {
                foreach ((string label, Color color) in new[] { ("light", Light), ("dark", Dark) })
                {
                    // Composite the already-clipped window raster, never an expanded VisualBrush.
                    double width = bitmap.PixelWidth / scale, height = bitmap.PixelHeight / scale;
                    var visual = new DrawingVisual();
                    using (DrawingContext dc = visual.RenderOpen())
                    {
                        dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, width + 80, height + 80));
                        dc.DrawImage(bitmap, new Rect(40, 40, width, height));
                    }
                    var composite = new RenderTargetBitmap((int)Math.Ceiling((width + 80) * scale),
                        (int)Math.Ceiling((height + 80) * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    composite.Render(visual);
                    Store(composite, name + "-on-" + label + ".png", label, null, []);
                    TextContrast(element, name, color, label, report);
                }
            }
            else TextContrast(element, name, Light, "light", report);

            void Store(BitmapSource image, string filename, string backdrop, int? edge, List<ShadowRow> effects)
            {
                SavePng(image, DiagnosticOutputPaths.Child(Folder(surface), filename));
                report.Renders.Add(new RenderRow(surface, state, Tag, surface + "/" + filename, scale,
                    image.PixelWidth, image.PixelHeight, backdrop, edge, effects));
            }
        }

        internal void Pair(string pair, Brush foreground, Brush background, Color backdrop, string backdropName)
        {
            Color bg = Over(BrushColor(background), backdrop);
            Color fg = Over(BrushColor(foreground), bg);
            double ratio = Ratio(fg, bg);
            report.Contrast.Add(new ContrastRow("selector-palette-" + Tag, pair, pair, backdropName,
                fg.ToString(), bg.ToString(), ratio, 12, 400, 4.5, ratio < 4.5, "solid palette; desktop dimmer composed explicitly"));
        }

        internal void ContactSheets()
        {
            foreach (var group in report.Renders.GroupBy(row => row.Surface))
            {
                const int columns = 6, tileWidth = 440, imageHeight = 260, tileHeight = 310;
                var rows = group.ToArray();
                int width = columns * tileWidth, height = (int)Math.Ceiling(rows.Length / (double)columns) * tileHeight;
                var visual = new DrawingVisual();
                using (DrawingContext dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.LightGray, null, new Rect(0, 0, width, height));
                    for (int i = 0; i < rows.Length; i++)
                    {
                        string filename = Path.GetFileName(rows[i].File);
                        var image = new BitmapImage();
                        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                        image.UriSource = new Uri(DiagnosticOutputPaths.Child(Folder(group.Key), filename));
                        image.DecodePixelWidth = tileWidth - 16; image.EndInit(); image.Freeze();
                        double factor = Math.Min((tileWidth - 16d) / image.PixelWidth, imageHeight / (double)image.PixelHeight);
                        double x = i % columns * tileWidth, y = i / columns * tileHeight;
                        dc.DrawImage(image, new Rect(x + 8, y + 4, image.PixelWidth * factor, image.PixelHeight * factor));
                        var caption = new FormattedText(filename, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                            new Typeface("Segoe UI"), 11, Brushes.Black, 1) { MaxTextWidth = tileWidth - 16, MaxTextHeight = 44 };
                        dc.DrawText(caption, new Point(x + 8, y + imageHeight + 8));
                    }
                }
                Assert.True(width < 4000);
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                SavePng(bitmap, DiagnosticOutputPaths.Child(Folder("contact"), $"{group.Key}-{scale * 100:0}.png"));
            }
        }
    }

    private static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private static IEnumerable<(DependencyObject Node, string Path)> Walk(DependencyObject root, string path = "root")
    {
        yield return (root, path + ":" + root.GetType().Name);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Walk(VisualTreeHelper.GetChild(root, i), path + "/" + i)) yield return item;
    }

    private static int EdgeAlpha(BitmapSource source)
    {
        int stride = source.PixelWidth * 4;
        byte[] pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        int max = 0;
        for (int x = 0; x < source.PixelWidth; x++)
        {
            max = Math.Max(max, pixels[x * 4 + 3]);
            max = Math.Max(max, pixels[(source.PixelHeight - 1) * stride + x * 4 + 3]);
        }
        for (int y = 0; y < source.PixelHeight; y++)
        {
            max = Math.Max(max, pixels[y * stride + 3]);
            max = Math.Max(max, pixels[y * stride + (source.PixelWidth - 1) * 4 + 3]);
        }
        return max;
    }

    private static void Inventory(FrameworkElement root, string render, double scale, Report report)
    {
        foreach ((DependencyObject node, string path) in Walk(root))
        {
            if (node is FrameworkElement element) Add("Margin", Values(element.Margin), true);
            if (node is Border border)
            {
                Add("CornerRadius", [border.CornerRadius.TopLeft, border.CornerRadius.TopRight, border.CornerRadius.BottomRight, border.CornerRadius.BottomLeft], false);
                Add("Padding", Values(border.Padding), true);
                Add("BorderThickness", Values(border.BorderThickness), false);
                if (scale != 1 && Values(border.BorderThickness).Any(value => value > 0) && border.ActualWidth > 0 && border.ActualHeight > 0)
                {
                    Rect bounds = ReferenceEquals(border, root) ? new Rect(border.RenderSize)
                        : border.TransformToAncestor(root).TransformBounds(new Rect(border.RenderSize));
                    double[] edges = [bounds.Left * scale, bounds.Top * scale, bounds.Right * scale, bounds.Bottom * scale];
                    report.Borders.Add(new BorderRow(render, path, edges, edges.Any(value => Math.Abs(value - Math.Round(value)) > 0.0001)));
                }
            }
            if (node is Control control) { Add("Padding", Values(control.Padding), true); Add("BorderThickness", Values(control.BorderThickness), false); Add("FontSize", [control.FontSize], false); }
            if (node is TextBlock text) { Add("Padding", Values(text.Padding), true); Add("FontSize", [text.FontSize], false); }
            if (node is AccessText access) Add("FontSize", [access.FontSize], false);
            if (node is SymbolIcon icon) Add("SymbolIcon.Size", [icon.Size], false);
            if (node is Shape shape) Add("StrokeThickness", [shape.StrokeThickness], false);
            void Add(string property, double[] values, bool spacing)
            {
                double[] allowed = [0, 2, 4, 6, 8, 12, 16, 999, ThemedShellPresenter.CardCorner.TopLeft];
                bool flag = spacing ? values.Any(value => Math.Abs(value / 2 - Math.Round(value / 2)) > 0.0001)
                    : property == "CornerRadius" && values.Any(value => !allowed.Contains(value));
                report.Geometry.Add(new GeometryRow(render, path, property, values, flag));
            }
        }
    }

    private static double[] Values(Thickness value) => [value.Left, value.Top, value.Right, value.Bottom];

    private static void TextContrast(FrameworkElement root, string render, Color backdrop, string backdropName, Report report)
    {
        foreach ((DependencyObject node, string path) in Walk(root))
        {
            string text; Brush foreground; double size; FontWeight weight;
            if (node is TextBlock tb) { text = tb.Text; foreground = tb.Foreground; size = tb.FontSize; weight = tb.FontWeight; }
            else if (node is AccessText at) { text = at.Text; foreground = at.Foreground; size = at.FontSize; weight = at.FontWeight; }
            else continue;
            if (string.IsNullOrWhiteSpace(text) || foreground is not SolidColorBrush || node is UIElement { Visibility: not Visibility.Visible }) continue;
            Color fg = BrushColor(foreground), bg = Colors.Transparent;
            string note = "";
            bool visible = true;
            for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is UIElement { Visibility: not Visibility.Visible } || current is UIElement { Opacity: <= 0 }) visible = false;
                Brush? background = current switch { Border b => b.Background, Panel p => p.Background, Control c => c.Background, TextBlock t => t.Background, _ => null };
                if (background is SolidColorBrush solid) { bg = Over(bg, BrushColor(solid)); fg = Over(fg, BrushColor(solid)); }
                else if (background is not null) note = "non-solid ancestor background; solid-only estimate";
                if (current is UIElement ui) { bg = Alpha(bg, ui.Opacity); fg = Alpha(fg, ui.Opacity); }
                if (ReferenceEquals(current, root)) break;
            }
            if (!visible) continue;
            bg = Over(bg, backdrop); fg = Over(fg, backdrop);
            double ratio = Ratio(fg, bg);
            double threshold = size >= 24 || size >= 18.66 && weight.ToOpenTypeWeight() >= 700 ? 3 : 4.5;
            report.Contrast.Add(new ContrastRow(render, path, text, backdropName, fg.ToString(), bg.ToString(), ratio, size,
                weight.ToOpenTypeWeight(), threshold, ratio < threshold, note));
        }
    }

    private static Color BrushColor(Brush brush) => brush is SolidColorBrush solid ? Alpha(solid.Color, brush.Opacity) : Colors.Transparent;
    private static Color Alpha(Color color, double opacity) => Color.FromArgb((byte)Math.Round(color.A * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B);
    private static Color Over(Color foreground, Color background)
    {
        double fa = foreground.A / 255d, ba = background.A / 255d, a = fa + ba * (1 - fa);
        if (a == 0) return Colors.Transparent;
        byte Mix(byte f, byte b) => (byte)Math.Round((f * fa + b * ba * (1 - fa)) / a);
        return Color.FromArgb((byte)Math.Round(a * 255), Mix(foreground.R, background.R), Mix(foreground.G, background.G), Mix(foreground.B, background.B));
    }
    private static double Ratio(Color a, Color b)
    {
        static double Linear(byte v) { double s = v / 255d; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        static double L(Color c) => .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        return (Math.Max(L(a), L(b)) + .05) / (Math.Min(L(a), L(b)) + .05);
    }

    private static List<MotionRow> ScanMotion()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "MyCapture.App"))) directory = directory.Parent;
        Assert.NotNull(directory);
        string source = Path.Combine(directory.FullName, "src", "MyCapture.App");
        string[] patterns = ["CaptureOverlayView*.cs", "CaptureModeBar.cs", "CaptureStartGuideCard.cs", "CaptureKeyPanel*.cs", "QuickAccessWindow.cs", "QuickAccessActionPanel.cs", "CaptureFlight*.cs", "CountdownWindow.cs", "ThemedShellPresenter.cs", "RecordingControlWindow.cs"];
        var results = new List<MotionRow>();
        var calls = new Regex(@"(?:TimeSpan\.)?(?<unit>FromMilliseconds|FromSeconds)\s*\(\s*(?<value>[^)]+)\)", RegexOptions.CultureInvariant);
        var durations = new Regex(@"new\s+Duration\s*\(\s*(?<value>[^\r\n;]+)", RegexOptions.CultureInvariant);
        foreach (string file in patterns.SelectMany(pattern => Directory.EnumerateFiles(source, pattern, SearchOption.AllDirectories)).Distinct().Order())
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string code = lines[i].Split("//", 2)[0];
                foreach (Match match in calls.Matches(code).Concat(durations.Matches(code)
                    .Where(match => !calls.IsMatch(match.Value))))
                {
                    string expression = match.Groups["value"].Value.Trim();
                    double? ms = double.TryParse(expression, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                        ? value * (match.Groups["unit"].Value == "FromSeconds" ? 1000 : 1) : null;
                    string context = string.Join(" ", lines.Skip(Math.Max(0, i - 2)).Take(5).Select(line => line.Trim()));
                    results.Add(new MotionRow(Path.GetRelativePath(source, file).Replace('\\', '/'), i + 1, expression, ms,
                        ms is not null && ms != 83 && ms != 167 && ms != 250, context));
                }
            }
        }
        return results;
    }

    private static string Markdown(Report report)
    {
        var text = new StringBuilder("# Capture-flow numeric evidence\n\n");
        text.AppendLine("All raster images use RenderTargetBitmap. Transparent images are clipped to their window bounds before compositing with a 40 DIP margin. Geometry is DIP; border edges are device pixels; alpha is 0–255.\n");
        foreach (var group in report.Renders.GroupBy(row => row.Surface)) text.AppendLine($"- {group.Key}: {group.Count()} PNGs");
        text.AppendLine("\n## Reachability and measurement notes\n");
        foreach (string note in report.Notes) text.AppendLine("- " + note);
        text.AppendLine("\nText rows report computed solid-foreground/ancestor-background contrast, not pixel antialiasing. Transparent window contrast is evaluated independently on both backdrops. Non-solid backgrounds are identified as estimates. Selector OnRender pairs are listed separately. A nonzero edge alpha is a boundary-contact flag; when shadow effects exist it flags possible shadow clipping, and may also include opaque content.\n");
        text.AppendLine("## Every flagged contrast occurrence\n\nRender | Element/text | Backdrop | FG / BG | Ratio | DIP/weight | Threshold\n---|---|---|---|---:|---|---:");
        foreach (var row in report.Contrast.Where(row => row.Flag)) text.AppendLine(CultureInfo.InvariantCulture,
            $"{row.Render} | {Escape(row.Path + " " + row.Text)} | {row.Backdrop} | {row.Foreground} / {row.Background} | {row.Ratio:F3} | {row.FontSize:F2}/{row.Weight} | {row.Threshold}");
        text.AppendLine("\n## Every nonzero transparent-window edge\n\nFile | Maximum alpha | Shadow parameters\n---|---:|---");
        foreach (var row in report.Renders.Where(row => row.EdgeAlpha > 0)) text.AppendLine($"{row.File} | {row.EdgeAlpha} | {Escape(JsonSerializer.Serialize(row.Shadows))}");
        text.AppendLine("\n## Every flagged geometry occurrence\n\nRender | Element | Property | Values\n---|---|---|---");
        foreach (var row in report.Geometry.Where(row => row.Flag)) text.AppendLine($"{row.Render} | {row.Path} | {row.Property} | {string.Join(", ", row.Values.Select(value => value.ToString("0.###", CultureInfo.InvariantCulture)))}");
        text.AppendLine("\n## Every fractional border occurrence\n\nRender | Element | Bounds L/T/R/B (px)\n---|---|---");
        foreach (var row in report.Borders.Where(row => row.Flag)) text.AppendLine($"{row.Render} | {row.Path} | {string.Join(", ", row.Edges.Select(value => value.ToString("0.###", CultureInfo.InvariantCulture)))}");
        text.AppendLine("\n## Static duration scan\n\nThe scan includes all duration call sites in the requested source set (including holds, timers and waits); context is preserved to distinguish animation durations. Symbolic expressions are unresolved, never treated as token matches.\n\nFile:line | Expression | ms | Non-token literal | Context\n---|---|---:|---|---");
        foreach (var row in report.Motion) text.AppendLine($"{row.File}:{row.Line} | {Escape(row.Expression)} | {row.Milliseconds?.ToString(CultureInfo.InvariantCulture) ?? "unresolved"} | {row.NonToken} | {Escape(row.Context)}");
        return text.ToString();
    }
    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " / ", StringComparison.Ordinal);

    public sealed class Report
    {
        public List<RenderRow> Renders { get; set; } = [];
        public List<ContrastRow> Contrast { get; set; } = [];
        public List<GeometryRow> Geometry { get; set; } = [];
        public List<BorderRow> Borders { get; set; } = [];
        public List<MotionRow> Motion { get; set; } = [];
        public List<string> Notes { get; set; } = [];
    }
    public sealed record RenderRow(string Surface, string State, string Tag, string File, double Scale, int Width, int Height, string Backdrop, int? EdgeAlpha, List<ShadowRow> Shadows);
    public sealed record ShadowRow(string Path, double BlurRadius, double Depth, double Direction, double Opacity, string Color);
    public sealed record ContrastRow(string Render, string Path, string Text, string Backdrop, string Foreground, string Background, double Ratio, double FontSize, int Weight, double Threshold, bool Flag, string Note);
    public sealed record GeometryRow(string Render, string Path, string Property, double[] Values, bool Flag);
    public sealed record BorderRow(string Render, string Path, double[] Edges, bool Flag);
    public sealed record MotionRow(string File, int Line, string Expression, double? Milliseconds, bool NonToken, string Context);
}
