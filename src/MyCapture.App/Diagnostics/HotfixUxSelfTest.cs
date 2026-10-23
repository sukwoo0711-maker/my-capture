using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Pinning;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Diagnostics;

/// <summary>Real WPF/native-display checks for the passive capture receipt. Uses a fresh diagnostic profile.</summary>
internal static class HotfixUxSelfTest
{
    internal const string CommandLineSwitch = "--selftest-hotfix-ux";
    /// <summary>Set to 1 when an operator may activate the owned windows; late activation is then a warning.</summary>
    internal const string AttendedForegroundVariable = "MYCAPTURE_SELFTEST_ATTENDED_FOREGROUND";
    /// <summary>Carries the fixture's trace path into PowerShell as data, never as script text.</summary>
    internal const string InputTraceVariable = "MYCAPTURE_SELFTEST_INPUT_TRACE";
    /// <summary>
    /// Time an owned window may take to become foreground when nobody is at the desktop: the run's
    /// own activation is synchronous, and the first render plus two dispatcher rounds take well
    /// under a second. Acquisition after this is caused by something outside the run.
    /// </summary>
    internal static readonly TimeSpan UnattendedForegroundBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AttendedForegroundLimit = TimeSpan.FromSeconds(30);

    internal enum ForegroundVerdict { Pass, Warning, Fail }

    /// <summary>
    /// Foreground acquisition is part of the verdict. Within the unattended budget the run activated
    /// its own window. Later acquisition came from an operator click or the foreground-lock timeout:
    /// an unattended run fails, and an attended run passes only with a warning that keeps the report
    /// from reading RESULT: PASS.
    /// </summary>
    internal static ForegroundVerdict EvaluateForeground(bool acquired, TimeSpan elapsed, bool attended) =>
        !acquired ? ForegroundVerdict.Fail
        : elapsed <= UnattendedForegroundBudget ? ForegroundVerdict.Pass
        : attended ? ForegroundVerdict.Warning : ForegroundVerdict.Fail;

    internal static TimeSpan ForegroundWaitLimit(bool attended) => attended ? AttendedForegroundLimit : UnattendedForegroundBudget;

    internal static string ResultLine(bool passed, int warnings) =>
        !passed ? "RESULT: FAIL" : warnings == 0 ? "RESULT: PASS" : $"RESULT: PASS WITH WARNINGS ({warnings})";

    internal static int Run(string output)
    {
        try { output = ValidatedFilePaths.CreateDirectory(output); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Trace.TraceError("{0}: {1}", UiText.Get("Dialog.Storage"), error);
            return 1;
        }
        Console.WriteLine("HOTFIX_UX_OUTPUT=" + output);
        var report = new StringBuilder($"MyCapture {typeof(HotfixUxSelfTest).Assembly.GetName().Version} real WPF/native QA\nUTC: {DateTimeOffset.UtcNow:O}\n");
        report.AppendLine("Production windows and command paths. Bounded synthetic input targets owned editors/cards and an isolated external PowerShell/WinForms image-paste/file-drop process. Every injection checks its expected foreground HWND; foreground loss aborts. Cursor is restored only while an owned target remains foreground. Fresh profile; no user settings/queue or startup registry access. The card copy/paste check writes a synthetic 320x180 image through the production Windows clipboard writer.");
        bool attended = string.Equals(Environment.GetEnvironmentVariable(AttendedForegroundVariable), "1", StringComparison.Ordinal);
        List<string> warnings = [];
        report.AppendLine(attended
            ? $"Foreground mode: attended ({AttendedForegroundVariable}=1). Owned windows must become foreground within {UnattendedForegroundBudget.TotalSeconds:0.0}s; an operator may activate them up to {AttendedForegroundLimit.TotalSeconds:0}s, reported as WARNING, and the result is then not PASS."
            : $"Foreground mode: unattended. Owned windows must become foreground within {UnattendedForegroundBudget.TotalSeconds:0.0}s by the run's own activation; otherwise the run fails without injecting input.");
        List<Window> owned = [];
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        AppTheme? oldTheme = ThemeService.IsAppliedForTest;
        Func<bool> oldCanShow = ShortcutHelpWindow.CanShowHelp;
        int result = 1;
        var originalCursor = WindowStyleFacade.GetCursorPosition();
        void Check(string label, bool passed)
        {
            report.AppendLine($"{(passed ? "PASS" : "FAIL")}: {label}");
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: {label}");
            if (!passed) throw new InvalidOperationException(label);
        }
        void RecordForeground(string target, bool acquired, TimeSpan elapsed)
        {
            string detail = $"{target} foreground {(acquired ? "acquired" : "not acquired")} after {elapsed.TotalSeconds:0.000}s " +
                $"(unattended budget {UnattendedForegroundBudget.TotalSeconds:0.0}s); foreground owner {DescribeForeground()}";
            switch (EvaluateForeground(acquired, elapsed, attended))
            {
                case ForegroundVerdict.Pass:
                    report.AppendLine("PASS: " + detail);
                    Console.WriteLine("PASS: " + detail);
                    break;
                case ForegroundVerdict.Warning:
                    string warning = "WARNING: " + detail + "; activated from outside the run (attended mode)";
                    warnings.Add(warning);
                    report.AppendLine(warning);
                    Console.WriteLine(warning);
                    break;
                default:
                    string failure = "FAIL: " + detail + "; no input was injected" + (attended ? "." :
                        $". Run from the active desktop session, or set {AttendedForegroundVariable}=1 so an operator may activate it (reported as WARNING, not PASS).");
                    report.AppendLine(failure);
                    Console.WriteLine(failure);
                    throw new TimeoutException(failure);
            }
        }
        try
        {
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            ThemeService.Apply(AppTheme.Midnight);
            BitmapSource sample = Sample();
            VerifyEditorMouseShortcuts(sample, owned, Check, report, attended, RecordForeground);
            var rect = new RectD(0, 0, sample.PixelWidth, sample.PixelHeight);
            var doc = AnnotationDocument.CreateFor(sample.PixelWidth, sample.PixelHeight);
            doc.Items.Add(new RectangleAnnotation { Rect = new RectD(30, 30, 160, 90), Stroke = ColorRgba.FromRgb(255, 0, 0), StrokeThickness = 8 });
            var editor = new AnnotationEditorWindow(new FrozenFrame(sample, rect, null, 0), rect, sample, initialDocument: doc);
            owned.Add(editor); editor.Show(); Pump(200); editor.Activate(); editor.Editor.Focus();
            var editorBounds = WindowStyleFacade.GetWindowBounds(new WindowInteropHelper(editor).Handle);
            MoveCursor(new WindowInteropHelper(editor).Handle, (editorBounds.Left + editorBounds.Right) / 2,
                (editorBounds.Top + editorBounds.Bottom) / 2);
            var card = Card(sample); owned.Add(card);
            IInputElement? focus = Keyboard.FocusedElement;
            IntPtr foreground = GetForegroundWindow();
            card.Show(); Pump(150);
            Check("card Show preserves native foreground and WPF editor focus", foreground == GetForegroundWindow() && ReferenceEquals(focus, Keyboard.FocusedElement) && !card.IsActive);
            Render(card, Path.Combine(output, "passive-card.png"), 96);
            foreach (Key key in new[] { Key.R, Key.A })
            {
                var keyEvent = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                editor.Editor.RaiseEvent(keyEvent);
                Check("editor routed " + key + " while card is visible", keyEvent.Handled && !card.IsActive && editor.IsActive);
            }
            Check("editor Ctrl+Z command while card visible", editor.Editor.HandleShortcut(Key.Z, ModifierKeys.Control) && !card.IsActive);
            EditorCommitAction? committed = null;
            editor.CommitRequested = value => { committed = value.Action; return Task.FromResult(false); };
            Check("editor Ctrl+C command accepted", editor.Editor.HandleShortcut(Key.C, ModifierKeys.Control));
            Pump(150);
            // Ctrl+C copies and keeps the editor open; Ctrl+Enter is the closing Done.
            Check("Ctrl+C reaches editor copy commit, not receipt", committed == EditorCommitAction.CopyToClipboard);
            // Keep the real dispatcher timer running while editor commands execute.
            var lifetime = Stopwatch.StartNew();
            while (card.IsVisible && lifetime.Elapsed < TimeSpan.FromSeconds(12)) Pump(100);
            report.AppendLine($"Receipt lifetime wait={lifetime.Elapsed.TotalSeconds:0.000}s; visible={card.IsVisible}; mouseOver={card.IsMouseOver}; editorActive={editor.IsActive}");
            report.AppendLine(card.TimeoutStateForTest);
            Check("passive receipt closes on its real dispatcher timer", !card.IsVisible);
            Check("editor Esc command accepted", editor.Editor.HandleShortcut(Key.Escape, ModifierKeys.None));
            // Esc steps back inside the editor (selection, tool) and never closes it.
            Pump(50); Check("Esc keeps editor open", editor.IsVisible);
            editor.Close(); Pump(50); Check("editor closes from code", !editor.IsVisible);

            // A separate application/process owns the input. It has no capture-card code,
            // global shortcuts, user document, or profile. Its paste/drop handlers
            // accept only the synthetic data supplied by this diagnostic.
            using (var external = ExternalInputFixture.Start(output, ForegroundWaitLimit(attended)))
            {
                IntPtr inputHandle = external.Handle;
                string startupObservation = $"External input startup: {external.StartupObservation}";
                report.AppendLine(startupObservation);
                Console.WriteLine(startupObservation);
                RecordForeground($"external fixture PID={external.Id}, HWND={inputHandle}", external.ForegroundAcquired, external.ForegroundElapsed);
                Check($"external text fixture PID={external.Id}, HWND={inputHandle}; separate process", external.Id != Environment.ProcessId);
                int copies = 0, saves = 0;
                Task<bool>? lastCopy = null;
                string dragPath = ValidatedFilePaths.FileWithinRoot(output, "native-card-drag.png", ".png");
                ImageCodec.SavePng(sample, dragPath);
                var keyboardCard = new QuickAccessWindow(sample, () => { copies++; return lastCopy = ClipboardImageService.CopyImageAsync(sample); }, () => { },
                    () => Task.CompletedTask, () => { saves++; return Task.FromResult(true); }, () => { }, () => Task.FromResult(dragPath));
                owned.Add(keyboardCard); keyboardCard.Show(); Pump(100);
                IntPtr cardHandle = new WindowInteropHelper(keyboardCard).Handle;
                report.AppendLine($"Native input targets: external={inputHandle}; card={cardHandle}");
                try
                {
                    var bounds = WindowStyleFacade.GetWindowBounds(cardHandle);
                    foreach (bool kept in new[] { false, true })
                    {
                        keyboardCard.IsKept = kept;
                        foreach (bool hover in new[] { false, true })
                        {
                            MoveCursor(inputHandle, hover ? bounds.Left + 20 : 60, hover ? bounds.Top + 20 : 60);
                            Pump(80);
                            Check($"kept={kept}/hover={hover}: external foreground preserved", GetForegroundWindow() == inputHandle && !keyboardCard.IsActive);
                            Check($"kept={kept}/hover={hover}: native pointer state", keyboardCard.IsPointerOver == hover);
                            string before = external.Text;
                            int traceOffset = external.ReadInputTrace().Length;
                            foreach (byte key in new byte[] { 0x43, 0x45, 0x50, 0x53 }) { SendKey(inputHandle, key); Pump(50); }
                            SendKey(inputHandle, 0x1B); Pump(50);
                            // Read the separate process's actual text acknowledgement without resending keys.
                            var acknowledgement = Stopwatch.StartNew();
                            bool inputStateReady = false, capsLock = false;
                            string inputStateError = "No complete input acknowledgement.";
                            string expectedText = before + "ceps";
                            while (acknowledgement.Elapsed < TimeSpan.FromSeconds(1))
                            {
                                string trace = external.ReadInputTrace();
                                inputStateReady = TryObserveExternalKeyBatch(trace.Length >= traceOffset ? trace[traceOffset..] : "",
                                    out capsLock, out inputStateError);
                                if (inputStateReady) expectedText = before + (capsLock ? "CEPS" : "ceps");
                                if (inputStateReady && external.Text == expectedText) break;
                                Pump(20);
                            }
                            string inputObservation = $"External input acknowledgement: kept={kept}; hover={hover}; elapsed={acknowledgement.Elapsed.TotalMilliseconds:0.000}ms; capsLock={(inputStateReady ? capsLock.ToString() : "unknown")}; inputState={inputStateError}; expected={expectedText}; actual={external.Text}";
                            report.AppendLine(inputObservation);
                            Console.WriteLine(inputObservation);
                            Check($"kept={kept}/hover={hover}: external key order, no modifiers and stable CapsLock", inputStateReady);
                            Check($"kept={kept}/hover={hover}: external C/E/P/S unchanged; text={external.Text}", external.Text == expectedText);
                            Check($"kept={kept}/hover={hover}: no card action or Esc dismissal", copies == 0 && saves == 0 && keyboardCard.IsVisible);
                        }
                        // RegisterHotKey cannot target a HWND owned by another process.
                        // A separate thread registration proves the card released the global chord.
                        var probe = new NativeHotkeyRegistrar(IntPtr.Zero);
                        bool available = probe.TryRegister(0x7103, new Hotkey(HotkeyModifiers.Alt, 0x51), out _, out _);
                        if (available) probe.Unregister(0x7103);
                        Check($"Alt+Q available to a separate registration iff kept={kept}", available == kept);
                    }
                    Click(inputHandle, CardControl<Button>(keyboardCard, "QuickAccess.Copy"));
                    Check("copy click preserves external foreground", GetForegroundWindow() == inputHandle && !keyboardCard.IsActive && copies == 1);
                    Check("card writes actual Windows image clipboard", lastCopy is not null && Await(lastCopy));
                    InjectKey(inputHandle, 0x11, 0); SendKey(inputHandle, 0x56); InjectKey(inputHandle, 0x11, 2); Pump(200);
                    // Observe delivery of this one chord; never resend it or change the expected image.
                    var pasteAcknowledgement = Stopwatch.StartNew();
                    string pasteText = "No text acknowledgement received.";
                    while (pasteAcknowledgement.Elapsed < TimeSpan.FromSeconds(1))
                    {
                        if (external.TryReadText(50, out string received))
                        {
                            pasteText = received;
                            if (received.EndsWith("[image:320x180]", StringComparison.Ordinal)) break;
                        }
                        Pump(20);
                    }
                    string pasteObservation = $"External image paste acknowledgement: elapsed={pasteAcknowledgement.Elapsed.TotalMilliseconds:0.000}ms; foreground={GetForegroundWindow()}; actual text={pasteText}";
                    report.AppendLine(pasteObservation);
                    Console.WriteLine(pasteObservation);
                    string inputTrace = external.ReadInputTrace();
                    report.AppendLine("External fixture input trace:\n" + inputTrace);
                    Console.WriteLine("External fixture input trace:\n" + inputTrace);
                    Check("Ctrl+V pastes image in external app without refocusing", external.Text.EndsWith("[image:320x180]", StringComparison.Ordinal));
                    Click(inputHandle, CardControl<Button>(keyboardCard, "QuickAccess.Pin"));
                    Click(inputHandle, CardControl<Button>(keyboardCard, "QuickAccess.Save"));
                    Check("pin/save clicks preserve external foreground", GetForegroundWindow() == inputHandle && !keyboardCard.IsActive && saves == 1);
                    var preview = CardControl<Image>(keyboardCard, "QuickAccess.Preview");
                    Click(inputHandle, preview);
                    Check("thumbnail click preserves external foreground", GetForegroundWindow() == inputHandle && !keyboardCard.IsActive);
                    DragToExternal(inputHandle, preview);
                    Check($"native thumbnail file drop reaches external app and preserves foreground; text={external.Text}; foreground={GetForegroundWindow()}",
                        external.Text.EndsWith("[drop:" + dragPath + "]", StringComparison.Ordinal) && GetForegroundWindow() == inputHandle && !keyboardCard.IsActive);
                    var keepButton = keyboardCard.KeepToggleForTest;
                    Click(inputHandle, keepButton); Click(inputHandle, keepButton);
                    Check("keep toggle clicks preserve external foreground", GetForegroundWindow() == inputHandle && keyboardCard.IsKept && !keyboardCard.IsActive);
                    foreach (bool library in new[] { false, true })
                    {
                        int invoked = 0;
                        var actionCard = new QuickAccessWindow(sample, () => Task.FromResult(true), () => invoked++,
                            () => Task.CompletedTask, () => Task.FromResult(true), () => invoked++, () => Task.FromResult(dragPath)) { IsKept = true };
                        owned.Add(actionCard); actionCard.Show(); Pump(80);
                        var action = CardControl<Button>(actionCard, library ? "QuickAccess.Library" : "QuickAccess.Edit");
                        Click(inputHandle, action);
                        Check($"{(library ? "library" : "edit")} click invokes action without activating card", invoked == 1 && GetForegroundWindow() == inputHandle && !actionCard.IsVisible);
                    }
                    // Kept cards enter only by a non-action background click.
                    var dimensions = CardControl<TextBlock>(keyboardCard, "QuickAccess.Dimensions");
                    double originalLeft = keyboardCard.Left;
                    Point dragStart = dimensions.PointToScreen(new Point(dimensions.ActualWidth / 2, dimensions.ActualHeight / 2));
                    MoveCursor(inputHandle, (int)dragStart.X, (int)dragStart.Y);
                    MouseButton(inputHandle, 0x0002); Pump(50);
                    for (int delta = 10; delta <= 80; delta += 10)
                    {
                        MoveCursor(inputHandle, (int)dragStart.X - delta, (int)dragStart.Y); Pump(25);
                    }
                    MouseButton(inputHandle, 0x0004); Pump(80);
                    Check("background drag moves the card without activating or invoking an action",
                        Math.Abs(keyboardCard.Left - originalLeft) > 40 && GetForegroundWindow() == inputHandle
                        && !keyboardCard.IsActive && copies == 1 && saves == 1);
                    Click(inputHandle, dimensions);
                    Check("background click activates the kept card", keyboardCard.IsActive && keyboardCard.IsKeyboardFocusWithin);
                    SendKey(cardHandle, 0x43); Pump(80);
                    Check("focused card C invokes copy", copies == 2 && lastCopy is not null && Await(lastCopy));

                    keyboardCard.IsKept = false;
                    external.Activate(); Pump(100);
                    InjectKey(inputHandle, 0x12, 0);
                    SendKey(inputHandle, 0x51);
                    InjectKey(inputHandle, 0x12, 2);
                    Pump(120);
                    Check("Alt+Q activates an automatic card", keyboardCard.IsActive && keyboardCard.IsKeyboardFocusWithin);
                    SendKey(cardHandle, 0x53); Pump(80);
                    Check("focused card S invokes save", saves == 2);
                    // Observe real pointer-triggered tooltips on this owned foreground HWND.
                    var tooltipTargets = keyboardCard.ActionButtonsForTest.Cast<ButtonBase>()
                        .Append(keyboardCard.KeepToggleForTest).ToArray();
                    foreach (ButtonBase target in tooltipTargets)
                    {
                        Point center = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
                        var latency = Stopwatch.StartNew();
                        double? openedAt = null;
                        ToolTipEventHandler opened = (_, _) => openedAt = latency.Elapsed.TotalMilliseconds;
                        target.ToolTipOpening += opened;
                        MoveCursor(cardHandle, (int)center.X, (int)center.Y); Pump(300);
                        target.ToolTipOpening -= opened;
                        // The service creates a ToolTip popup for a string-valued tooltip;
                        // IsOpen lives on that ToolTip, not on the owner button.
                        bool visible = PresentationSource.CurrentSources.OfType<HwndSource>()
                            .Where(source => source.RootVisual is not null)
                            .SelectMany(source => Ux402VisualMetrics.Descendants(source.RootVisual))
                            .OfType<ToolTip>().Any(tooltip => tooltip.IsOpen && Equals(tooltip.Content, target.ToolTip));
                        Check($"card tooltip {target.ToolTip}: visible={visible}, latency={openedAt:0}ms",
                            visible && openedAt is >= 0 and <= 300);
                    }
                    // Exercise the safety gate without injecting anything into a foreign window.
                    bool guarded = false;
                    try { RequireForeground(inputHandle); } catch (InvalidOperationException) { guarded = true; }
                    Check("foreground mismatch aborts injection", guarded);
                    SendKey(cardHandle, 0x1B); Pump(80);
                    Check("focused card Esc closes it", !keyboardCard.IsVisible);
                    external.Activate(); Pump(80);
                    var timed = Card(sample); owned.Add(timed); timed.Show(); Pump(100);
                    var timedCopy = CardControl<Button>(timed, "QuickAccess.Copy");
                    Click(inputHandle, timedCopy);
                    Check("automatic card remains passive after copy click", GetForegroundWindow() == inputHandle && !timed.IsActive);
                    MoveCursor(inputHandle, 70, 100);
                    var closeWatch = Stopwatch.StartNew();
                    while (timed.IsVisible && closeWatch.Elapsed < TimeSpan.FromSeconds(8)) Pump(100);
                    Check($"passive clicked card closes after pointer leaves ({closeWatch.Elapsed.TotalSeconds:0.00}s)", !timed.IsVisible && GetForegroundWindow() == inputHandle);
                    MoveCursor(inputHandle, originalCursor.X, originalCursor.Y);
                    report.AppendLine($"External input measurement: PID={external.Id}; final text={external.Text}; 4 scenarios x C/E/P/S/Esc; all key and pointer events foreground-guarded.");
                }
                finally
                {
                    IntPtr current = GetForegroundWindow();
                    if (current == inputHandle || current == cardHandle) MoveCursor(current, originalCursor.X, originalCursor.Y);
                    keyboardCard.Close();
                }
            }

            foreach (string language in new[] { "ko", "en" })
            using (MyCapture.Core.Localization.UiText.UseLanguage(language))
            foreach (AppTheme theme in new[] { AppTheme.Midnight, AppTheme.Glass, AppTheme.Workspace, AppTheme.Daylight, AppTheme.GlassLight, AppTheme.HighContrast })
            {
                ThemeService.Apply(theme);
                var receipt = Card(sample); owned.Add(receipt); receipt.IsKept = true;
                receipt.Show(); Pump(70); receipt.UpdateLayout();
                var keep = receipt.KeepToggleForTest;
                object kept = keep.Content;
                receipt.IsKept = false;
                Check($"{language}/{theme}: keep icon retains its accessible name and checked state",
                    kept is SymbolIcon && AutomationProperties.GetName(keep) == UiText.Get("QuickAccess.KeepName")
                    && ReferenceEquals(kept, keep.Content) && keep.IsChecked == false);
                receipt.IsKept = true;
                foreach (Button button in receipt.ActionButtonsForTest)
                {
                    // Edit is labelled; the remaining actions have icons and named shortcut tooltips.
                    var content = (FrameworkElement)CommandContent(button)!;
                    var texts = Ux402VisualMetrics.Descendants(content).OfType<TextBlock>().Where(text => text.IsVisible).ToArray();
                    Check($"{language}/{theme}/{AutomationProperties.GetName(button)}: icon, name and shortcut fit the action",
                        (content is SymbolIcon || texts.Length > 0 || content is TextBlock)
                        && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))
                        && !string.IsNullOrWhiteSpace(AutomationProperties.GetAcceleratorKey(button))
                        && content.ActualWidth <= button.ActualWidth && content.ActualHeight <= button.ActualHeight
                        && texts.All(text => text.ActualWidth <= content.ActualWidth + 0.5 && text.ActualHeight <= content.ActualHeight + 0.5));
                }
                foreach (int dpi in new[] { 96, 144, 192 }) Render(receipt, Path.Combine(output, $"card-{language}-{theme}-{dpi}.png"), dpi);
                receipt.Close();
            }

            ThemeService.Apply(AppTheme.Midnight);
            var engine = new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance);
            using var coordinator = new CaptureOverlayCoordinator(engine, new WindowCandidateService(NullLogger<WindowCandidateService>.Instance), NullLogger<CaptureOverlayCoordinator>.Instance);
            int display = 0;
            foreach (MonitorInfo monitor in MonitorEnumerator.GetAll())
            {
                display++;
                report.AppendLine($"Monitor {display}: {monitor.DeviceName}, {monitor.Bounds}, DPI={monitor.Dpi}");
                var backdrop = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Background = new SolidColorBrush(Color.FromRgb(17, 31, 47)),
                    Width = monitor.Bounds.Width / monitor.ScaleFactor, Height = monitor.Bounds.Height / monitor.ScaleFactor,
                    Left = monitor.Bounds.Left / monitor.ScaleFactor, Top = monitor.Bounds.Top / monitor.ScaleFactor, Topmost = true };
                owned.Add(backdrop); backdrop.Show();
                PhysicalWindowPositioner.PlaceTopmost(new WindowInteropHelper(backdrop).Handle, monitor.Bounds); Pump(350);
                FrozenFrame baseline = engine.CaptureMonitor(monitor, false);
                ImageCodec.SavePng(baseline.Bitmap, Path.Combine(output, $"display{display}-baseline.png"));
                foreach (string mode in new[] { "region", "window", "full" })
                {
                    var receipt = Card(sample, excludeFromCapture: false); owned.Add(receipt); receipt.IsKept = true; receipt.Show(); Pump(70); receipt.PlaceOnMonitor(monitor); Pump(80);
                    var (left, top, right, bottom) = WindowStyleFacade.GetWindowBounds(new WindowInteropHelper(receipt).Handle);
                    RectD cardBounds = new(left, top, right - left, bottom - top);
                    BitmapSource before = engine.CaptureRegion(cardBounds, false);
                    BitmapSource behind = ScreenCaptureEngine.Crop(baseline,
                        new RectD(cardBounds.X - monitor.Bounds.X, cardBounds.Y - monitor.Bounds.Y, cardBounds.Width, cardBounds.Height));
                    ImageCodec.SavePng(before, Path.Combine(output, $"display{display}-{mode}-before-card.png"));
                    Check($"display{display}/{mode}: exclusion disabled, card visible in capture before dismissal", !Pixels(before).SequenceEqual(Pixels(behind)));
                    IntPtr receiptHandle = new WindowInteropHelper(receipt).Handle;
                    var dismissal = Stopwatch.StartNew();
                    receipt.DismissForCapture(coordinator.NoteForegroundDismissal);
                    Check($"display{display}/{mode}: dismissal closes HWND before capture", !receipt.IsVisible && !IsWindow(receiptHandle));
                    Task settle = coordinator.WaitForPresentationDismissalAsync();
                    Check($"display{display}/{mode}: production dismissal requests compositor settle", !settle.IsCompleted);
                    Await(settle);
                    Check($"display{display}/{mode}: presentation wait completed after {dismissal.Elapsed.TotalMilliseconds:0.0}ms", dismissal.Elapsed.TotalMilliseconds >= 95);
                    BitmapSource captured = mode switch
                    {
                        "region" => engine.CaptureRegion(cardBounds, false),
                        "window" => engine.CaptureRegion(monitor.Bounds, false),
                        _ => engine.CaptureMonitor(monitor, false).Bitmap,
                    };
                    ImageCodec.SavePng(captured, Path.Combine(output, $"display{display}-{mode}-after-card.png"));
                    // Compare against the same desktop backdrop, including any OS watermark.
                    BitmapSource reference = mode == "region" ? ScreenCaptureEngine.Crop(baseline,
                        new RectD(cardBounds.X - monitor.Bounds.X, cardBounds.Y - monitor.Bounds.Y, cardBounds.Width, cardBounds.Height)) : baseline.Bitmap;
                    byte[] pixels = Pixels(captured), original = Pixels(reference); long different = 0;
                    for (int i = 0; i < pixels.Length; i += 4)
                        if (Math.Abs(pixels[i] - original[i]) > 3 || Math.Abs(pixels[i + 1] - original[i + 1]) > 3 || Math.Abs(pixels[i + 2] - original[i + 2]) > 3) different++;
                    Check($"display{display}/{mode}: no card pixels; differing={different}/{pixels.Length / 4}", different == 0);
                }
                backdrop.Close();
            }

            var edit = new AnnotationEditorControl(new FrozenFrame(sample, rect, null, 0), rect, sample, doc.Clone(), null);
            edit.ApplyCrop(new RectD(10, 10, 230, 130));
            BitmapSource expected = Await(edit.RenderCurrentImageAsync());
            ImageCodec.SavePng(expected, Path.Combine(output, "edited-expected.png"));
            var settings = new AppSettings();
            AppPaths paths = AppPaths.CreateForRoot(ValidatedFilePaths.CreateTemporaryDirectory());
            var saver = new PinImageSaveService(() => settings, () => paths, NullLogger<PinImageSaveService>.Instance);
            var updatedCard = Card(sample); owned.Add(updatedCard); updatedCard.IsKept = true; updatedCard.Show(); Pump(70);
            Render(updatedCard, Path.Combine(output, "preview-before-update.png"), 96);
            updatedCard.UpdatePreview(expected); updatedCard.UpdateLayout();
            Render(updatedCard, Path.Combine(output, "preview-after-update.png"), 96);
            Check("preview dimensions reflect edited crop", CardControl<TextBlock>(updatedCard, "QuickAccess.Dimensions").Text == "230 × 130");
            updatedCard.Close();
            string? saved = null; BitmapSource? pinned = null; byte[]? clipboardPng = null;
            var actions = new QuickAccessImageActions(edit.RenderCurrentImageAsync,
                bitmap => ClipboardImageService.CopyImageAsync(bitmap, null, data =>
                {
                    using var sink = new MemoryStream();
                    ((Stream)data.GetData("PNG")!).CopyTo(sink);
                    clipboardPng = sink.ToArray();
                }),
                bitmap =>
                {
                    pinned = bitmap;
                    var pin = new PinWindow(PinContent.FromImage(bitmap), new PinViewState(bitmap.PixelWidth, bitmap.PixelHeight, 1, 1, 0.1), 100, 100, () => settings.Pin);
                    owned.Add(pin); pin.Show(); Pump(100); Render(pin, Path.Combine(output, "edited-pin-window.png"), 96); pin.Close();
                }, async bitmap => { PinSaveResult save = await saver.QuickSaveAsync(bitmap); saved = save.Path; return save.Status == PinSaveStatus.Saved; },
                exportService: new GalleryDragExportService(ValidatedFilePaths.CreateTemporaryDirectory()));
            Check("edited receipt copy uses production PNG payload with isolated clipboard sink", Await(actions.CopyAsync()) && clipboardPng is not null);
            using var png = new MemoryStream(clipboardPng!);
            BitmapSource copied = BitmapDecoder.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            ImageCodec.SavePng(copied, Path.Combine(output, "edited-clipboard.png"));
            Await(actions.PinAsync()); Check("edited receipt saves PNG", Await(actions.SaveAsync()));
            string drag = Await(actions.ExportAsync());
            File.Copy(drag, Path.Combine(output, "edited-drag.png"));
            foreach ((string name, BitmapSource image) in new[] { ("clipboard", copied), ("pin", pinned!), ("save", ImageCodec.TryLoad(saved!)!), ("drag", ImageCodec.TryLoad(drag)!) })
                Check($"edited {name}: dimensions={image.PixelWidth}x{image.PixelHeight}, pixel-identical to edited/cropped canvas", image.PixelWidth == expected.PixelWidth && image.PixelHeight == expected.PixelHeight && Pixels(expected).SequenceEqual(Pixels(image)));

            ShortcutHelpWindow.CanShowHelp = () => false;
            ShortcutHelpWindow.ShowHelp();
            Check("help does not open while capture predicate is false", !Application.Current.Windows.OfType<ShortcutHelpWindow>().Any(w => w.IsVisible));
            ShortcutHelpWindow.CanShowHelp = () => true;
            var selection = new CaptureOverlayWindow(new FrozenFrame(sample, rect, null, 0), abortOnFocusLoss: false);
            owned.Add(selection); selection.Show(); Pump(100);
            foreach (Key key in new[] { Key.F1, Key.OemQuestion })
            {
                var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(selection), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                ShortcutHelpWindow.OnWindowKey(selection, input);
                Check($"visible capture overlay suppresses help key {key}", !input.Handled && !Application.Current.Windows.OfType<ShortcutHelpWindow>().Any(w => w.IsVisible));
            }
            selection.Close();
            ShortcutHelpWindow.ShowHelp(); Pump(70);
            Check("help opens outside capture", Application.Current.Windows.OfType<ShortcutHelpWindow>().Any(w => w.IsVisible));
            Check("capture dismisses help", ShortcutHelpWindow.DismissForCapture());
            Check("help is no longer visible", !Application.Current.Windows.OfType<ShortcutHelpWindow>().Any(w => w.IsVisible));

            using (MyCapture.Core.Localization.UiText.UseLanguage("ko"))
            {
                Exception limit = Failure(() => VideoLayerResourceBudget.ValidateFileLength(VideoLayerResourceBudget.MaximumDocumentFileBytes + 1));
                var changedOverlay = new CaptureOverlayWindow(new FrozenFrame(sample, rect, null, 0), abortOnFocusLoss: false);
                owned.Add(changedOverlay);
                Exception changed = Failure(() => changedOverlay.AttachFrame(new FrozenFrame(sample, new RectD(1, 1, rect.Width, rect.Height), null, 0)));
                string badImage = ValidatedFilePaths.FileWithinRoot(paths.DataRoot, "invalid-image.png", ".png"); File.WriteAllText(badImage, "invalid local QA fixture");
                Exception invalid = Failure(() => VideoLayerAssets.ReadImage(badImage, CancellationToken.None));
                foreach (Exception error in new Exception[] { limit, invalid, changed }) Check("localized error body preserved: " + error.Message, ThemedDialog.RecoveryMessage(error) == error.Message);
                var close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                bool visible = false;
                close.Tick += (_, _) =>
                {
                    close.Stop();
                    var dialog = Application.Current.Windows.OfType<ThemedDialog>().Single(w => w.IsVisible);
                    visible = dialog.ShowInTaskbar && dialog.Owner is null && !dialog.Topmost && dialog.IsActive;
                    Render(dialog, Path.Combine(output, "ownerless-localized-error.png"), 96); dialog.Close();
                };
                close.Start(); ThemedDialog.Error(null, "MyCapture QA", changed);
                Check("ownerless error is active and taskbar visible, without persistent Topmost", visible);
                void VerifyTopmostDialog(Window obstruction, bool explicitOwner, bool escape)
                {
                    owned.Add(obstruction); obstruction.Show(); Pump(100); obstruction.Activate();
                    Check($"{obstruction.GetType().Name}: excluded only from fallback owners", !ThemedDialog.IsSuitableOwner(obstruction));
                    bool above = false, dismissed = false;
                    Exception? failure = null;
                    var existingDialogs = Application.Current.Windows.OfType<ThemedDialog>().ToHashSet();
                    var elapsed = Stopwatch.StartNew();
                    TimeSpan limit = TimeSpan.FromSeconds(5);
                    var dismiss = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = limit };
                    ThemedDialog[] TestDialogs() => Application.Current.Windows.OfType<ThemedDialog>()
                        .Where(dialog => !existingDialogs.Contains(dialog)).ToArray();
                    void CloseTestDialogs()
                    {
                        foreach (ThemedDialog dialog in TestDialogs())
                        {
                            try { dialog.Close(); }
                            catch (Exception error) { failure ??= error; }
                        }
                    }
                    void DismissDialog(bool timedOut)
                    {
                        dismiss.Stop();
                        if (timedOut) timeout.Stop();
                        try
                        {
                            if (timedOut || elapsed.Elapsed >= limit)
                                throw new TimeoutException($"Dialog verification exceeded {limit.TotalSeconds:0} seconds.");
                            ThemedDialog dialog = TestDialogs().Single(window => window.IsVisible);
                            above = dialog.IsActive && GetForegroundWindow() == new WindowInteropHelper(dialog).Handle
                                && (explicitOwner ? ReferenceEquals(dialog.Owner, obstruction) : dialog.Owner is null && dialog.Topmost)
                                && IsAbove(new WindowInteropHelper(dialog).Handle, new WindowInteropHelper(obstruction).Handle);
                            Render(dialog, Path.Combine(output, $"dialog-above-{obstruction.GetType().Name}-{explicitOwner}.png"), 96);
                            if (escape)
                            {
                                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), Environment.TickCount, Key.Escape)
                                    { RoutedEvent = Keyboard.KeyDownEvent, Source = dialog };
                                InputManager.Current.ProcessInput(key);
                            }
                            else
                            {
                                Button button = SelectStandardCloseAction(dialog);
                                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            }
                            // Only the input above can satisfy dismissal; cleanup never does.
                            dismissed = !dialog.IsVisible;
                        }
                        catch (Exception error) { failure ??= error; }
                        finally { CloseTestDialogs(); }
                    }
                    dismiss.Tick += (_, _) => DismissDialog(timedOut: false);
                    timeout.Tick += (_, _) => DismissDialog(timedOut: true);
                    try
                    {
                        dismiss.Start();
                        timeout.Start();
                        if (explicitOwner) ThemedDialog.Error(obstruction, "MyCapture QA", changed);
                        else ThemedDialog.ShowUnhandled(changed, showFallback: (_, _) =>
                        {
                            failure ??= new InvalidOperationException("The topmost dialog unexpectedly reached the native MessageBox fallback.");
                        });
                    }
                    finally
                    {
                        dismiss.Stop();
                        timeout.Stop();
                        CloseTestDialogs();
                    }
                    if (elapsed.Elapsed >= limit)
                        failure ??= new TimeoutException($"Dialog verification exceeded {limit.TotalSeconds:0} seconds.");
                    if (failure is not null)
                        throw new InvalidOperationException($"{obstruction.GetType().Name}: dialog verification failed; explicitOwner={explicitOwner}", failure);
                    Check($"{obstruction.GetType().Name}: dialog remains above visible topmost UI; explicitOwner={explicitOwner}", above);
                    Check($"{obstruction.GetType().Name}: dialog dismissed by {(escape ? "Esc" : "button")}", dismissed);
                    obstruction.Close();
                }
                VerifyTopmostDialog(new CaptureOverlayWindow(new FrozenFrame(sample, rect, null, 0), abortOnFocusLoss: false), false, true);
                VerifyTopmostDialog(new PinWindow(PinContent.FromImage(sample), new PinViewState(320, 180, 1, 1, 0.1), 100, 100, () => settings.Pin), true, false);
                VerifyTopmostDialog(new PinWindow(PinContent.FromImage(sample), new PinViewState(320, 180, 1, 1, 0.1), 100, 100, () => settings.Pin), false, false);
                VerifyTopmostDialog(new RecordingControlWindow(new RectD(100, 100, 640, 360), new MyCapture.Core.Recording.RecordingSettings(),
                    () => throw new InvalidOperationException("Recording must not start in this dialog diagnostic."),
                    () => Path.Combine(output, "unused-recording.mp4"), NullLogger.Instance), false, false);
                VerifyTopmostDialog(new CountdownWindow(60), false, false);
                string? fallback = null;
                ThemedDialog.ShowUnhandled(changed, _ => throw new InvalidOperationException("injected theme failure"), (text, _) => fallback = text);
                Check("theme failure reaches MessageBox fallback boundary with localized body", fallback == changed.Message);
            }
            result = 0;
        }
        catch (Exception error) { report.AppendLine(error.ToString()); }
        finally
        {
            foreach (Window window in owned) window.Close();
            ShortcutHelpWindow.DismissForCapture(); ShortcutHelpWindow.CanShowHelp = oldCanShow;
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            if (oldTheme is AppTheme theme) ThemeService.Apply(theme);
            report.AppendLine(ResultLine(result == 0, warnings.Count));
            try
            {
                File.WriteAllText(ValidatedFilePaths.FileWithinRoot(output, "hotfix-ux-report.txt", ".txt"),
                    report.ToString(), new UTF8Encoding(false));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Trace.TraceError("{0}: {1}", UiText.Get("Dialog.Storage"), error);
                result = 1;
            }
        }
        return result;
    }

    internal static Button SelectStandardCloseAction(ThemedDialog dialog)
    {
        var panel = (StackPanel)((ScrollViewer)dialog.Content).Content;
        Button button = panel.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Single(action => action.IsCancel);
        object? content = CommandContent(button);
        string? caption = content is TextBlock text ? text.Text : content as string;
        if (!button.IsDefault || !button.IsVisible || !button.IsEnabled || caption != UiText.Get("Dialog.Close"))
            throw new InvalidOperationException("The dialog's sole cancel action must be the default, visible, enabled Close button.");
        return button;
    }

    private static object? CommandContent(ButtonBase button) => button.Content is Grid { Name: "CommandButtonFace" } face
        && face.Children.Count > 0 ? face.Children[0] : button.Content;

    internal static T CardControl<T>(QuickAccessWindow card, string automationId) where T : FrameworkElement =>
        Ux402VisualMetrics.Descendants((DependencyObject)card.Content).OfType<T>()
            .Single(control => AutomationProperties.GetAutomationId(control) == automationId);

    private static QuickAccessWindow Card(BitmapSource image, bool excludeFromCapture = true) => new(image, () => Task.FromResult(true), () => { }, () => Task.CompletedTask, () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"), excludeFromCapture);
    private static Exception Failure(Action action)
    {
        try { action(); } catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected the invalid fixture to be rejected.");
    }
    private static BitmapSource Sample()
    {
        var image = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual(); using (DrawingContext draw = visual.RenderOpen())
        { draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, 320, 180)); draw.DrawRectangle(Brushes.DodgerBlue, null, new Rect(70, 50, 120, 50)); }
        image.Render(visual); image.Freeze(); return image;
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; converted.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels;
    }
    private static void Render(Window window, string path, int dpi)
    {
        window.UpdateLayout(); double scale = dpi / 96d;
        Size client = Ux402VisualMetrics.ClientSize(window);
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(client.Width * scale)), Math.Max(1, (int)Math.Ceiling(client.Height * scale)), dpi, dpi, PixelFormats.Pbgra32);
        image.Render(window); ImageCodec.SavePng(image, path);
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Await(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(20)) Pump(20);
        if (!task.IsCompleted) throw new TimeoutException("Diagnostic operation did not complete in 20 seconds.");
        task.GetAwaiter().GetResult();
    }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static bool IsAbove(IntPtr dialog, IntPtr obstruction)
    {
        for (IntPtr window = GetWindow(dialog, 2 /* GW_HWNDNEXT */); window != IntPtr.Zero; window = GetWindow(window, 2))
            if (window == obstruction) return true;
        return false;
    }
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    internal static FileStream OpenExternalInputTrace(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    internal static bool TryObserveExternalKeyBatch(string trace, out bool capsLock, out string observation)
    {
        capsLock = false;
        string[] keys = ["C", "E", "P", "S", "Escape"];
        string[] events = trace.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("; KeyDown key=", StringComparison.Ordinal)).ToArray();
        observation = "Incomplete or unexpected key sequence.";
        if (events.Length != keys.Length) return false;
        for (int index = 0; index < keys.Length; index++)
        {
            string[] fields = events[index].TrimEnd('\r').Split("; ", StringSplitOptions.None);
            if (!fields.Contains("KeyDown key=" + keys[index], StringComparer.Ordinal)) return false;
            observation = "Unexpected modifiers or lost fixture focus.";
            if (!fields.Contains("modifiers=None", StringComparer.Ordinal)
                || !fields.Contains("control=False", StringComparer.Ordinal)
                || !fields.Contains("focused=True", StringComparer.Ordinal)) return false;
            string[] capsFields = fields.Where(field => field.StartsWith("capsLock=", StringComparison.Ordinal)).ToArray();
            observation = "Missing or changing CapsLock observation.";
            if (capsFields.Length != 1 || !bool.TryParse(capsFields[0]["capsLock=".Length..], out bool observed)) return false;
            if (index == 0) capsLock = observed;
            else if (observed != capsLock) return false;
        }
        observation = "C/E/P/S/Escape received once in order; modifiers=None; focus=True; CapsLock stable.";
        return true;
    }
    /// <summary>The current foreground HWND and its process name, so a refused activation names who held it.</summary>
    private static string DescribeForeground()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "HWND=0";
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        string name;
        try { using var process = Process.GetProcessById((int)pid); name = process.ProcessName; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { name = "unavailable"; }
        return $"HWND={hwnd}, PID={pid} ({name})";
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    private static void RequireForeground(IntPtr expected)
    {
        IntPtr actual = GetForegroundWindow();
        if (expected == IntPtr.Zero || actual != expected)
            throw new InvalidOperationException($"Input aborted: expected foreground {expected}, actual {actual}.");
    }
    private static void SendKey(IntPtr target, byte key) { InjectKey(target, key, 0); InjectKey(target, key, 2); }
    private static void Click(IntPtr foreground, FrameworkElement control, StringBuilder? report = null)
    {
        Point point = control.PointToScreen(new Point(control.ActualWidth / 2, control.ActualHeight / 2));
        bool moved = false, downSent = false, upSent = false;
        void Observe(string phase)
        {
            if (report is null) return;
            var bounds = WindowStyleFacade.GetWindowBounds(foreground);
            string state = $"HOTFIX_FIRST_CLICK_{phase}: UTC={DateTimeOffset.UtcNow:O}; expected={foreground}; actual={GetForegroundWindow()}; x={(int)point.X}; y={(int)point.Y}; control={control.ActualWidth:0.###}x{control.ActualHeight:0.###}; bounds={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}; moved={moved}; downSent={downSent}; upSent={upSent}.";
            report.AppendLine(state); Console.WriteLine(state);
        }
        try
        {
            Observe("MOVE_BEFORE"); MoveCursor(foreground, (int)point.X, (int)point.Y); moved = true; Observe("MOVE_AFTER");
            Observe("DOWN_BEFORE"); MouseButton(foreground, 0x0002); downSent = true; Observe("DOWN_AFTER");
            Observe("UP_BEFORE"); MouseButton(foreground, 0x0004); upSent = true; Observe("UP_AFTER");
            Pump(100);
        }
        finally { Observe("FINAL"); }
    }

    /// <summary>
    /// Clicks a control in a popup the foreground window owns. The owner stays the expected
    /// foreground for every injection, and the pointer must be over the popup's own HWND.
    /// </summary>
    private static void ClickOwnedPopup(IntPtr foreground, IntPtr popup, FrameworkElement control)
    {
        Point point = control.PointToScreen(new Point(control.ActualWidth / 2, control.ActualHeight / 2));
        MoveCursor(foreground, (int)point.X, (int)point.Y);
        if (popup == IntPtr.Zero || GetWindow(popup, 4 /* GW_OWNER */) != foreground || !WindowStyleFacade.IsPointerOverWindow(popup))
            throw new InvalidOperationException($"Input aborted: pointer {(int)point.X},{(int)point.Y} is not over popup {popup} owned by {foreground}.");
        Click(foreground, control);
    }

    private static void DragToExternal(IntPtr foreground, FrameworkElement preview)
    {
        Point start = preview.PointToScreen(new Point(preview.ActualWidth / 2, preview.ActualHeight / 2));
        MoveCursor(foreground, (int)start.X, (int)start.Y); MouseButton(foreground, 0x0002); Pump(50);
        // OLE's drag loop can suspend dispatcher timers. The bounded native input
        // worker remains independent of that loop and never traverses other apps.
        Task gesture = Task.Run(() =>
        {
            MoveCursor(foreground, (int)start.X + 12, (int)start.Y + 2);
            Thread.Sleep(150);
            MoveCursor(foreground, 120, 110);
            Thread.Sleep(150);
            MouseButton(foreground, 0x0004);
        });
        Await(gesture); Pump(200);
    }

    private static void VerifyEditorMouseShortcuts(BitmapSource sample, List<Window> owned, Action<string, bool> check, StringBuilder report,
        bool attended, Action<string, bool, TimeSpan> recordForeground)
    {
        var rect = new RectD(0, 0, sample.PixelWidth, sample.PixelHeight);
        var window = new AnnotationEditorWindow(new FrozenFrame(sample, rect, null, 0), rect, sample);
        bool contentRendered = false;
        window.ContentRendered += (_, _) => contentRendered = true;
        owned.Add(window); window.Show(); Pump(150);
        bool activationAccepted = window.Activate(); window.Editor.Focus();
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        void ObserveForeground(string phase, TimeSpan elapsed)
        {
            string observation = $"HOTFIX_EDITOR_FOREGROUND_{phase}: UTC={DateTimeOffset.UtcNow:O}; PID={Environment.ProcessId}; expected={hwnd}; actual={GetForegroundWindow()}; elapsed={elapsed.TotalSeconds:0.000}s; loaded={window.IsLoaded}; visible={window.IsVisible}; enabled={window.IsEnabled}; active={window.IsActive}; contentRendered={contentRendered}; editorFocus={ReferenceEquals(Keyboard.FocusedElement, window.Editor)}; activationAccepted={activationAccepted}; no input injected during foreground wait.";
            report.AppendLine(observation);
            Console.WriteLine(observation);
        }
        // A foreground-lock rejection must never redirect synthetic input to another app. The
        // wait is bounded by the unattended budget; only an attended run keeps the dispatcher
        // responsive longer so an operator can activate the editor, and that is reported.
        TimeSpan limit = ForegroundWaitLimit(attended);
        var foregroundWait = Stopwatch.StartNew();
        ObserveForeground("WAIT", foregroundWait.Elapsed);
        int stableRounds = 0;
        while (foregroundWait.Elapsed < limit)
        {
            Pump(50);
            if (GetForegroundWindow() == hwnd && window.IsActive && !ReferenceEquals(Keyboard.FocusedElement, window.Editor))
                window.Editor.Focus();
            bool ready = GetForegroundWindow() == hwnd && window.IsLoaded && window.IsVisible && window.IsEnabled
                && window.IsActive && contentRendered && ReferenceEquals(Keyboard.FocusedElement, window.Editor);
            stableRounds = ready ? stableRounds + 1 : 0;
            if (stableRounds > 0) ObserveForeground("STABLE_" + stableRounds, foregroundWait.Elapsed);
            if (stableRounds == 2) break;
        }
        TimeSpan acquiredAfter = foregroundWait.Elapsed;
        ObserveForeground(stableRounds == 2 ? "ACQUIRED" : "TIMEOUT", acquiredAfter);
        recordForeground("owned editor HWND=" + hwnd, stableRounds == 2, acquiredAfter);
        RequireForeground(hwnd);
        // The visible command for a key: the crop bar replaces the top commands while cropping.
        ButtonBase Button(string key) => Ux402VisualMetrics.Descendants(window).OfType<ButtonBase>()
            .Single(b => AutomationProperties.GetAcceleratorKey(b) == key && b.IsVisible);
        Click(hwnd, Button("A"), report);
        check("mouse rail click returns editor keyboard focus", ReferenceEquals(Keyboard.FocusedElement, window.Editor));
        foreach (var (key, hint) in new[] { ((byte)0x52, "R"), ((byte)0x45, "E") })
        {
            SendKey(hwnd, key); Pump(60);
            check($"native rail click then {hint} selects tool", ((ToggleButton)Button(hint)).IsChecked == true);
        }
        // Crop is an image command at the top, not a rail tool: X opens the crop frame.
        SendKey(hwnd, 0x58); Pump(60);
        check("native rail click then X opens crop", window.Editor.PendingCrop is not null);
        SendKey(hwnd, 0x1B); Pump(50);
        check("native Esc cancels pending crop", window.Editor.PendingCrop is null && window.IsVisible);
        Click(hwnd, Button("R"));
        var swatches = (WrapPanel)typeof(AnnotationEditorControl).GetField("_swatchPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window.Editor)!;
        swatches.BringIntoView(); Pump(50);
        check("native color swatch is visible", swatches.IsVisible);
        Click(hwnd, swatches.Children.OfType<Button>().First());
        check("mouse color swatch returns editor keyboard focus", ReferenceEquals(Keyboard.FocusedElement, window.Editor));
        SendKey(hwnd, 0x52); Pump(50);
        check("native color swatch click then R selects Rectangle", ((ToggleButton)Button("R")).IsChecked == true);
        // The hex input and its apply button live in a flyout that the + button opens. The flyout
        // is a WPF Popup: its own HWND, owned by the editor and never activated (WS_EX_NOACTIVATE),
        // so the editor stays foreground and every injection still requires the editor HWND.
        Button customOpen = window.Editor.CustomColorButtonForTest;
        Popup customPopup = window.Editor.CustomColorPopupForTest;
        customOpen.BringIntoView(); Pump(50);
        check("native custom color button is visible", customOpen.IsVisible && !customPopup.IsOpen);
        Click(hwnd, customOpen);
        IntPtr popupHwnd = customPopup.Child is Visual flyout && PresentationSource.FromVisual(flyout) is HwndSource popupSource
            ? popupSource.Handle : IntPtr.Zero;
        check($"mouse custom color button opens its owned flyout; popup={popupHwnd}; owner={GetWindow(popupHwnd, 4 /* GW_OWNER */)}; foreground={GetForegroundWindow()}",
            customPopup.IsOpen && popupHwnd != IntPtr.Zero && popupHwnd != hwnd
            && GetWindow(popupHwnd, 4 /* GW_OWNER */) == hwnd && GetForegroundWindow() == hwnd);
        var customInput = Ux402VisualMetrics.Descendants(customPopup.Child).OfType<TextBox>().Single(b => AutomationProperties.GetName(b) == UiText.Get("Tools.CustomColor"));
        check("custom color flyout focuses its input", ReferenceEquals(Keyboard.FocusedElement, customInput));
        ClickOwnedPopup(hwnd, popupHwnd, customInput);
        customInput.Text = "#22AA66";
        var customApply = Ux402VisualMetrics.Descendants(customPopup.Child).OfType<Button>().Single(b => AutomationProperties.GetName(b) == UiText.Get("Tools.CustomColor"));
        ClickOwnedPopup(hwnd, popupHwnd, customApply);
        // Closing the flyout returns focus to the + button that opened it, inside the editor.
        check("mouse custom color apply closes the flyout, leaves input and returns editor focus",
            !customPopup.IsOpen && window.Editor.StatusText == UiText.Format("Text_67CAE5B118A3", "#22AA66")
            && ReferenceEquals(Keyboard.FocusedElement, customOpen) && window.Editor.IsKeyboardFocusWithin && GetForegroundWindow() == hwnd);
        SendKey(hwnd, 0x45); Pump(50);
        check("native custom color apply then E selects Ellipse", ((ToggleButton)Button("E")).IsChecked == true);
        Click(hwnd, Button("X"));
        var widthInput = (TextBox)typeof(AnnotationEditorControl).GetField("_cropWidth", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window.Editor)!;
        var heightInput = (TextBox)typeof(AnnotationEditorControl).GetField("_cropHeight", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window.Editor)!;
        Click(hwnd, widthInput); widthInput.Text = "240"; SendKey(hwnd, 0x0D); Pump(50);
        check("native width Enter confirms value and preserves pending crop/input focus", window.Editor.PendingCrop?.Region.Width == 240
            && window.Editor.CreateCurrentImageSnapshot().CanvasWidth == 320 && ReferenceEquals(Keyboard.FocusedElement, widthInput));
        Click(hwnd, heightInput); heightInput.Text = "100"; SendKey(hwnd, 0x0D); Pump(50);
        check("native height Enter remains editable after width Enter", window.Editor.PendingCrop?.Region.Height == 100 && ReferenceEquals(Keyboard.FocusedElement, heightInput));
        Click(hwnd, widthInput); widthInput.Text = "abc"; SendKey(hwnd, 0x0D); Pump(50);
        // While cropping, the crop bar's Apply replaces Done.
        Click(hwnd, Ux402VisualMetrics.Descendants(window).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "EditorFlowCropApply"));
        check("native crop Apply blocks invalid input and preserves text/error", window.IsVisible && window.Editor.PendingCrop is not null
            && widthInput.Text == "abc" && !string.IsNullOrEmpty(AutomationProperties.GetHelpText(widthInput)));
        SendKey(hwnd, 0x1B); Pump(50);
        var topCrop = Ux402VisualMetrics.Descendants(window).OfType<Button>()
            .Single(b => AutomationProperties.GetAcceleratorKey(b) == "X");
        Click(hwnd, topCrop); window.Editor.PendingCrop!.SetWidth(200);
        SendKey(hwnd, 0x0D); Pump(80);
        check("native top crop button click then Enter applies crop", window.Editor.PendingCrop is null && window.Editor.CreateCurrentImageSnapshot().CanvasWidth == 200);
        Click(hwnd, Button("X")); SendKey(hwnd, 0x1B); Pump(50);
        check("native crop button click then Esc cancels", window.Editor.PendingCrop is null && window.IsVisible);
        window.Editor.Focus(); SendKey(hwnd, 0x09); Pump(50);
        check("native Tab retains button keyboard navigation", Keyboard.FocusedElement is ButtonBase);
        // Rotation now lives in the crop bar; Undo is the top command left after cropping.
        Click(hwnd, Button("Ctrl+Z"));
        check("mouse top command returns editor keyboard focus", ReferenceEquals(Keyboard.FocusedElement, window.Editor));
        SendKey(hwnd, 0x1B); Pump(80);
        // Esc only steps back (text, selection, tool); it never closes the editor and its edits.
        check("native top button click then Esc keeps the editor open", window.IsVisible);
        window.Close();
    }
    private static void InjectKey(IntPtr target, byte key, uint flags)
    {
        RequireForeground(target);
        KeyEvent(key, 0, flags, UIntPtr.Zero);
    }
    private static void MoveCursor(IntPtr target, int x, int y)
    {
        RequireForeground(target);
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("Diagnostic cursor move failed.");
    }
    private static void MouseButton(IntPtr target, uint flags)
    {
        RequireForeground(target);
        MouseEvent(flags, 0, 0, 0, UIntPtr.Zero);
    }
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint x, uint y, uint data, UIntPtr extra);

    /// <summary>
    /// Builds the external fixture's PowerShell process. The trace path travels in an environment
    /// variable and the script reads it as data: PowerShell also ends single-quoted strings at
    /// U+2018-U+201B, so no path is ever interpolated into script text.
    /// </summary>
    internal static ProcessStartInfo CreateExternalInputStartInfo(string inputTracePath, string script)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
        start.Environment[InputTraceVariable] = inputTracePath;
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-STA"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);
        return start;
    }

    /// <summary>Script prologue: the trace path from the environment and the append-only trace writer.</summary>
    internal const string ExternalInputTracePrologue =
        "$script:inputTracePath=$env:" + InputTraceVariable + "; " +
        "if([string]::IsNullOrEmpty($script:inputTracePath)){throw 'Fixture trace path is missing.'}; " +
        "function Write-InputTrace([string]$message){try{[System.IO.File]::AppendAllText($script:inputTracePath, [DateTimeOffset]::UtcNow.ToString('O')+'; PID='+$PID+'; '+$message+[Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))}catch{[Console]::Error.WriteLine('Fixture trace write failed: '+$_.Exception.ToString())}}; ";

    private sealed class ExternalInputFixture : IDisposable
    {
        private readonly Process _process;
        private readonly string _inputTracePath;
        internal IntPtr Handle { get; }
        internal int Id => _process.Id;
        internal string StartupObservation { get; }
        /// <summary>Whether the fixture became foreground, and how long after its window reported ready.</summary>
        internal bool ForegroundAcquired { get; }
        internal TimeSpan ForegroundElapsed { get; }
        private ExternalInputFixture(Process process, IntPtr handle, string startupObservation, string inputTracePath,
            bool foregroundAcquired, TimeSpan foregroundElapsed)
        {
            _process = process; Handle = handle; StartupObservation = startupObservation; _inputTracePath = inputTracePath;
            ForegroundAcquired = foregroundAcquired; ForegroundElapsed = foregroundElapsed;
        }
        // IME: the fixture stands in for another application. Its oracles are the WinForms KeyDown
        // KeyCode sequence and the exact resulting text, which together prove MyCapture neither
        // consumed nor reordered keys meant for that application. An IME in a composing mode
        // (Korean Hangul, Japanese kana, Chinese pinyin) reports those keys as Keys.ProcessKey and
        // composes text that depends on the user's current conversion mode, so neither oracle would
        // be defined. ImeMode.Disable detaches only this fixture TextBox's input context
        // (ImmAssociateContext(hwnd, NULL)); it neither reads nor changes the user's IME conversion
        // mode or keyboard layout. Forcing a conversion mode or switching the thread's input
        // language could change state Windows shares across the user's applications, so the
        // fixture keeps the user's layout and reports it. MyCapture's own cross-process keyboard
        // surface is RegisterHotKey (Alt+Q), which Windows matches before any IME and which is
        // checked separately; nothing in MyCapture observes another process's input context.
        internal static ExternalInputFixture Start(string output, TimeSpan foregroundLimit)
        {
            string inputTracePath = ValidatedFilePaths.FileWithinRoot(output, "external-input-events.txt", ".txt");
            var start = CreateExternalInputStartInfo(inputTracePath, "Add-Type -AssemblyName System.Windows.Forms; " +
                ExternalInputTracePrologue +
                "$f=New-Object System.Windows.Forms.Form; $f.Text='MyCapture external input measurement'; " +
                "$f.Width=340; $f.Height=160; $f.StartPosition='Manual'; $f.Left=40; $f.Top=40; " +
                "$t=New-Object System.Windows.Forms.TextBox; $t.Multiline=$true; $t.Dock='Fill'; $t.ImeMode=[System.Windows.Forms.ImeMode]::Disable; $f.Controls.Add($t); " +
                "$t.AllowDrop=$true; $t.Add_DragEnter({param($sender,$e) if($e.Data.GetDataPresent('FileDrop')){$e.Effect=[System.Windows.Forms.DragDropEffects]::Copy}}); " +
                "$t.Add_DragDrop({param($sender,$e) $t.AppendText('[drop:'+($e.Data.GetData('FileDrop')[0])+']')}); " +
                "$t.Add_KeyDown({param($sender,$e) Write-InputTrace ('KeyDown key='+$e.KeyCode+'; modifiers='+$e.Modifiers+'; control='+$e.Control+'; focused='+$t.Focused+'; capsLock='+[System.Windows.Forms.Control]::IsKeyLocked([System.Windows.Forms.Keys]::CapsLock)+'; text='+$t.Text); " +
                "if($e.Control -and $e.KeyCode -eq 'V'){$img=$null; try{$contains=[System.Windows.Forms.Clipboard]::ContainsImage(); Write-InputTrace ('Clipboard ContainsImage='+$contains); " +
                "try{$data=[System.Windows.Forms.Clipboard]::GetDataObject(); if($null -eq $data){Write-InputTrace 'Clipboard data object=null'}else{Write-InputTrace ('Clipboard formats='+[string]::Join(',', $data.GetFormats()))}}catch{Write-InputTrace ('Clipboard format observation exception='+$_.Exception.ToString())}; " +
                "if($contains){$img=[System.Windows.Forms.Clipboard]::GetImage(); if($null -eq $img){Write-InputTrace 'Clipboard GetImage=null'}else{Write-InputTrace ('Clipboard GetImage='+$img.Width+'x'+$img.Height)}; " +
                "$t.AppendText('[image:'+$img.Width+'x'+$img.Height+']'); $e.SuppressKeyPress=$true; Write-InputTrace ('Paste completed; text='+$t.Text)}}catch{Write-InputTrace ('Paste exception='+$_.Exception.ToString()); throw}finally{if($null -ne $img){$img.Dispose()}}}}); " +
                "$t.Add_KeyUp({param($sender,$e) Write-InputTrace ('KeyUp key='+$e.KeyCode+'; modifiers='+$e.Modifiers+'; control='+$e.Control+'; focused='+$t.Focused+'; text='+$t.Text)}); " +
                "$f.Add_Shown({$f.Activate(); $t.Focus(); " +
                "$f.Text='MyCapture external input measurement; focused='+$t.Focused+'; ime='+$t.ImeMode+'; language='+[System.Windows.Forms.InputLanguage]::CurrentInputLanguage.Culture.Name+'; layout='+[System.Windows.Forms.InputLanguage]::CurrentInputLanguage.LayoutName+'; capsLock='+[System.Windows.Forms.Control]::IsKeyLocked([System.Windows.Forms.Keys]::CapsLock)+' ready'; Write-InputTrace ('Shown; '+$f.Text)}); " +
                "[System.Windows.Forms.Application]::Run($f)");
            Process process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start external input fixture.");
            try
            {
                // Process start-up is not foreground time: the foreground clock starts when the
                // fixture's window reports ready, and stops at the caller's foreground limit.
                var startup = Stopwatch.StartNew();
                Stopwatch? foreground = null;
                while (true)
                {
                    process.Refresh();
                    if (process.HasExited) throw new InvalidOperationException("External fixture exited before showing its window.");
                    IntPtr hwnd = process.MainWindowHandle;
                    var title = new StringBuilder(256);
                    if (hwnd != IntPtr.Zero) _ = SendMessage(hwnd, 0x000D, new IntPtr(title.Capacity), title);
                    if (hwnd != IntPtr.Zero && title.ToString().EndsWith(" ready", StringComparison.Ordinal))
                    {
                        foreground ??= Stopwatch.StartNew();
                        if (GetForegroundWindow() != hwnd) _ = SetForegroundWindow(hwnd);
                        if (GetForegroundWindow() == hwnd || foreground.Elapsed >= foregroundLimit)
                            return new ExternalInputFixture(process, hwnd, title.ToString(), inputTracePath,
                                GetForegroundWindow() == hwnd, foreground.Elapsed);
                    }
                    else if (startup.Elapsed >= TimeSpan.FromSeconds(15))
                        throw new TimeoutException($"External fixture PID={process.Id} did not show its ready window within 15 seconds; no input was injected.");
                    Pump(50);
                }
            }
            catch { if (!process.HasExited) process.Kill(); process.Dispose(); throw; }
        }
        internal string ReadInputTrace()
        {
            try
            {
                if (!File.Exists(_inputTracePath)) return "No fixture key events were recorded.";
                using var stream = OpenExternalInputTrace(_inputTracePath);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return "Fixture input trace unavailable: " + error; }
        }
        internal string Text
        {
            get
            {
                if (TryReadText(1000, out string text)) return text;
                throw new TimeoutException($"External fixture PID={Id}, HWND={Handle} did not acknowledge WM_GETTEXT within 1000ms; foreground={GetForegroundWindow()}; input was not resent.");
            }
        }
        internal bool TryReadText(uint timeoutMilliseconds, out string text)
        {
            var buffer = new StringBuilder(1024);
            // A blocked fixture must not block the parent's STA indefinitely while reading clipboard data.
            bool acknowledged = SendMessageTimeout(GetWindow(Handle, 5 /* GW_CHILD */), 0x000D /* WM_GETTEXT */,
                new IntPtr(buffer.Capacity), buffer, 0x0003 /* SMTO_BLOCK | SMTO_ABORTIFHUNG */,
                timeoutMilliseconds, out _) != IntPtr.Zero;
            text = acknowledged ? buffer.ToString() : string.Empty;
            return acknowledged;
        }
        internal void Activate()
        {
            if (!SetForegroundWindow(Handle)) throw new InvalidOperationException("External fixture activation failed.");
        }
        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.CloseMainWindow();
                if (!_process.WaitForExit(3000)) _process.Kill();
            }
            _process.Dispose();
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, StringBuilder text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message,
        IntPtr wParam, StringBuilder text, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "keybd_event")] private static extern void KeyEvent(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr hwnd);
}
