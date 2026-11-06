using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using Xunit.Abstractions;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// Explicit desktop evidence from synthetic controls only. No desktop/screen DC,
/// keyboard input, clipboard, IME setter, user settings or application process is used.
/// Collection success is distinct from review of the saved images/lifetime observations.
/// </summary>
public sealed class EditorColorFlyoutEvidenceTests(ITestOutputHelper output)
{
    internal const string EnableVariable = "MYCAPTURE_COLOR_FLYOUT_EVIDENCE";
    internal const string OutputVariable = "MYCAPTURE_COLOR_FLYOUT_EVIDENCE_OUTPUT";

    [ColorFlyoutEvidenceFact]
    public void SyntheticColorFlyoutRecordsNativeImagesAndLifetime() => StaTestHost.Run(() =>
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable(EnableVariable));
        Assert.Null(Application.Current); // Run this opt-in Fact in its own test process.
        string root = FreshRoot();
        var rows = new List<object>();
        var failures = new List<Exception>();
        var clock = Stopwatch.StartNew();
        using var dpi = TestDpiContext.AtPerMonitorDpi();
        using var language = UiText.UseLanguage("ko-KR");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        AppTheme previousTheme = ThemeService.Current;
        NativePoint originalCursor = default;
        bool cursorSaved = false;
        nint originalLayout = GetKeyboardLayout(0);
        int clickAttempts = 0;
        bool inputUnsettled = false;
        try
        {
            Neutral();
            Assert.True(GetCursorPos(out originalCursor));
            cursorSaved = true;
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            MonitorInfo monitor = Assert.Single(MonitorEnumerator.GetAll(), item => item.IsPrimary);
            Assert.InRange(monitor.Dpi, 96U, 768U);
            foreach (AppTheme theme in new[] { AppTheme.Daylight, AppTheme.Midnight })
            {
                ThemeService.Apply(theme);
                foreach ((string size, double width, double height) in new[]
                    { ("wide", 1000d, 640d), ("minimum", 680d, 460d) })
                    Case(theme, size, width, height, monitor, theme == AppTheme.Daylight && size == "wide");
            }
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            Cleanup(() => { Neutral(); Assert.Equal(originalLayout, GetKeyboardLayout(0)); });
            Cleanup(() =>
            {
                if (cursorSaved)
                {
                    Assert.False(inputUnsettled, "Injected mouse-up was not observed; cursor movement is prohibited.");
                    Neutral();
                    Assert.True(SetCursorPos(originalCursor.X, originalCursor.Y));
                    Assert.True(GetCursorPos(out NativePoint restored));
                    Assert.Equal(originalCursor, restored);
                }
            });
            Cleanup(() => ThemeService.Apply(previousTheme));
            Cleanup(application.Shutdown);
            Cleanup(() => SaveJson(Path.Combine(root, "report.json"), new
            {
                schema = "color-flyout-native-evidence-v1", status = failures.Count == 0 ? "COLLECTED" : "FAILED_OR_INCOMPLETE",
                elapsedMs = clock.Elapsed.TotalMilliseconds, processId = Environment.ProcessId, clickAttempts, keyboardInputs = 0, inputUnsettled,
                captureMethod = "PrintWindow(PW_CLIENTONLY | PW_RENDERFULLCONTENT), exact owned HWND only",
                desktopPixelsRead = false, visualReview = "NOT_REVIEWED", lifetimeCompatibility = "OBSERVATIONS_ONLY",
                rows, errors = failures.Select(error => new { type = error.GetType().FullName, error.Message }).ToArray(),
            }));
        }
        output.WriteLine("Synthetic native evidence: {0}; images require review; lifetime observations are not a compatibility verdict.", root);
        if (failures.Count > 0) throw new AggregateException("Color flyout evidence/cleanup failed.", failures);

        void Cleanup(Action action)
        {
            try { action(); } catch (Exception error) { failures.Add(error); }
        }

        void Case(AppTheme theme, string size, double width, double height, MonitorInfo monitor, bool lifetime)
        {
            string label = $"{theme}-{size}";
            double scale = monitor.ScaleFactor;
            Assert.True(monitor.WorkArea.Width >= width * scale + 32 && monitor.WorkArea.Height >= height * scale + 32,
                "The actual display cannot contain the requested fixture; no input/capture was performed.");
            Assert.Equal(width, Math.Round(width * scale) / scale);
            Assert.Equal(height, Math.Round(height * scale) / scale);
            BitmapSource bitmap = Solid(640, 360, 0x36);
            var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
            var window = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), region, bitmap,
                title: "MyCapture synthetic color flyout", pointerMonitor: monitor)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = (monitor.WorkArea.X + (monitor.WorkArea.Width - width * scale) / 2) / scale,
                Top = (monitor.WorkArea.Y + (monitor.WorkArea.Height - height * scale) / 2) / scale,
                Width = width, Height = height, ShowInTaskbar = false, ShowActivated = false,
            };
            TestDpiContext.InitializeMonitorDpiWindow(window, monitor.Dpi);
            bool rendered = false, closed = false;
            window.ContentRendered += (_, _) => rendered = true;
            window.Closed += (_, _) => closed = true;
            nint owner = 0, lastFlyout = 0;
            Window? alternate = null;
            HwndSource? ownerSource = null, flyoutSource = null;
            int nativeUps = 0;
            var events = new List<object>();
            // Caption-only observations and an independently named all-AND completion oracle.
            EditorCaptionCompletionOracle? captionOracle = null;
            CaptionCompletionState? captionTerminal = null;
            NativeRect captionBeforeRect = default, captionAfterRect = default;
            string? captionCompletionKind = null;
            var captionEvents = new List<object>(32);
            bool captionInFlight = false, captionEventLimitReached = false;
            long captionStartedTicks = 0;
            int captionClickAttempt = 0;
            nint captionTarget = 0;
            NativePoint captionPoint = default;
            object CaptionState(string stage, nint hwnd, int message, nint wparam, nint lparam)
            {
                long ticks = Stopwatch.GetTimestamp();
                // Read independently of nativeUps; the original && gate may short-circuit.
                short asyncLeft = GetAsyncKeyState(1);
                bool queueLeftDown = (GetKeyState(1) & 0x8000) != 0;
                if (message != 0)
                    captionOracle?.Observe(hwnd.ToInt64(), message, wparam.ToInt64(), lparam.ToInt64(), ticks, queueLeftDown);
                return new { stage, ticks, sinceCaptionTicks = ticks - captionStartedTicks,
                    stopwatchFrequency = Stopwatch.Frequency, clickAttempt = captionClickAttempt,
                    originalPoint = new { captionPoint.X, captionPoint.Y },
                    owner = owner.ToInt64(), target = captionTarget.ToInt64(), hwnd = hwnd.ToInt64(),
                    message, wParam = wparam.ToInt64(), lParam = lparam.ToInt64(),
                    systemCommand = message == 0x0112 ? wparam.ToInt64() & 0xFFF0 : (long?)null,
                    capture = GetCapture().ToInt64(), foreground = GetForegroundWindow().ToInt64(),
                    nativeFocus = GetFocus().ToInt64(), ownerAlive = owner != 0 && IsWindow(owner),
                    asyncLeftHighBit = (asyncLeft & 0x8000) != 0, queueLeftDown, nativeUps,
                    interpretation = "CAPTION_OBSERVATION_WITH_SEPARATE_ALL_AND_ORACLE" };
            }
            nint Hook(nint hwnd, int message, nint wparam, nint lparam, ref bool handled)
            {
                if (message is 0x0202 or 0x00A2) nativeUps++;
                if (events.Count < 128 && message is 0x0006 or 0x0007 or 0x0008 or 0x0086 or 0x00A1 or 0x00A2 or 0x0201 or 0x0202 or 0x0215)
                    events.Add(new { atMs = clock.Elapsed.TotalMilliseconds, hwnd = hwnd.ToInt64(), message });
                if (captionInFlight && message is 0x0112 or 0x0231 or 0x0232 or 0x0215 or 0x001F
                    or 0x00A1 or 0x00A2 or 0x0201 or 0x0202)
                {
                    if (captionEvents.Count < 64)
                        captionEvents.Add(CaptionState("caption-window-message", hwnd, message, wparam, lparam));
                    else
                    {
                        captionEventLimitReached = true;
                        captionOracle?.Fail("Caption native observation overflow.");
                    }
                }
                return 0;
            }
            var controller = Controller(window.Editor);
            var originalColor = controller.StrokeColor;
            int originalUndo = controller.Undo.UndoCount;
            int originalItems = controller.Document.Items.Count;
            var originalTool = controller.Tool;
            TextBox input = window.Editor.CustomColorInputForTest;
            Button opener = window.Editor.CustomColorButtonForTest;
            Border flyout = FindFlyout(input);
            Assert.Equal(232d, flyout.Width);
            Assert.Equal("AnnotationCustomColorHex", AutomationProperties.GetAutomationId(input));
            Assert.True(InputMethod.GetIsInputMethodEnabled(input));
            var observations = new List<object>();
            int failuresBeforeCase = failures.Count;
            try
            {
                window.Show();
                Wait(window, () => rendered, "ContentRendered");
                window.UpdateLayout();
                TestDpiContext.AssertMonitorDpi(window, monitor.Dpi);
                Wait(window, () => window.Opacity == 1 && window.Editor.Opacity == 1
                    && window.RenderTransform.Value.IsIdentity && window.Editor.RenderTransform.Value.IsIdentity,
                    "natural window entrance completion");
                observations.Add(new { stage = "initial-shown-size", window.Width, window.Height,
                    window.ActualWidth, window.ActualHeight, window.MinWidth, window.MinHeight,
                    window.ConstructorSizingMeasurement, window.SourceSizingMeasurement });
                Assert.Equal(AnnotationEditorWindow.MinimumSize.Width, window.MinWidth);
                Assert.Equal(AnnotationEditorWindow.MinimumSize.Height, window.MinHeight);
                Assert.True(width >= window.MinWidth && height >= window.MinHeight);
                // Establish the diagnostic's requested size after the real automatic
                // first-show path; do not change product minima or suppress that path.
                window.Width = width;
                window.Height = height;
                window.Left = Math.Floor(monitor.WorkArea.Left + (monitor.WorkArea.Width - width * scale) / 2) / scale;
                window.Top = Math.Floor(monitor.WorkArea.Top + (monitor.WorkArea.Height - height * scale) / 2) / scale;
                window.UpdateLayout();
                Wait(window, () => window.ActualWidth == width && window.ActualHeight == height,
                    "exact requested fixture size after automatic sizing");
                ownerSource = Assert.IsType<HwndSource>(PresentationSource.FromVisual(window));
                owner = ownerSource.Handle;
                Own(owner);
                Assert.True(GetWindowRect(owner, out NativeRect actualBounds));
                Assert.True(actualBounds.Left >= monitor.WorkArea.Left && actualBounds.Top >= monitor.WorkArea.Top
                    && actualBounds.Right <= monitor.WorkArea.Right && actualBounds.Bottom <= monitor.WorkArea.Bottom,
                    "The entire native fixture must remain inside the actual primary work area.");
                observations.Add(new { stage = "requested-size-ready", window.ActualWidth, window.ActualHeight,
                    window.MinWidth, window.MinHeight, actualBounds.Left, actualBounds.Top,
                    actualBounds.Right, actualBounds.Bottom, workArea = monitor.WorkArea });
                ownerSource.AddHook(Hook);
                Assert.True(window.Activate());
                Assert.True(SetForegroundWindow(owner));
                Wait(window, () => GetForegroundWindow() == owner, "owned foreground");
                Assert.Equal(width, window.ActualWidth);
                Assert.Equal(height, window.ActualHeight);
                Assert.False(flyout.IsVisible);
                Observe("initial");
                OpenFromClosed();
                Capture("open");
                if (lifetime)
                {
                    Click(opener, "opener-reclick");
                    Observe("after-opener-reclick");
                    EnsureVisibleForLifetime();
                    var status = Field<TextBlock>(window.Editor, "_dimensionsText");
                    Assert.True(status.IsVisible && status.ActualWidth > 4 && status.ActualHeight > 4);
                    Click(status, "outside-client");
                    Observe("after-outside-client");
                    EnsureVisibleForLifetime();
                    ClickCaption();
                    Observe("after-caption-click");
                    EnsureVisibleForLifetime();
                    alternate = new Window
                    {
                        Owner = window, Title = "Synthetic deactivation target", Width = 300, Height = 180,
                        Left = window.Left + 80, Top = window.Top + 80,
                        WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false,
                        Content = new TextBlock { Text = "Synthetic only", Background = Brushes.White, Foreground = Brushes.Black },
                    };
                    alternate.Show();
                    nint alternateHandle = new WindowInteropHelper(alternate).Handle;
                    Own(alternateHandle);
                    Assert.True(alternate.Activate());
                    Assert.True(SetForegroundWindow(alternateHandle));
                    Wait(window, () => GetForegroundWindow() == alternateHandle, "synthetic deactivation");
                    Observe("after-deactivate-to-owned-window");
                    alternate.Close(); alternate = null;
                    Assert.True(window.Activate());
                    Assert.True(SetForegroundWindow(owner));
                    Wait(window, () => GetForegroundWindow() == owner, "return to owned editor");
                    Observe("after-activate-return");
                    EnsureVisibleForLifetime();
                    window.Content = null;
                    Wait(window, () => !window.Editor.IsLoaded, "editor unloaded");
                    Observe("after-editor-unloaded");
                    window.Content = window.Editor;
                    Wait(window, () => window.Editor.IsLoaded, "editor reattached");
                    window.UpdateLayout();
                    EnsureVisibleForLifetime();
                }
                Unchanged();
                Observe("before-owner-close");
                window.Close();
                Wait(window, () => closed, "owner closed");
                Wait(window, () => !IsWindow(owner) && (lastFlyout == 0 || !IsWindow(lastFlyout)), "native owner/host destruction");
                Assert.False(IsWindow(owner));
                if (lastFlyout != 0) Assert.False(IsWindow(lastFlyout), "A native color host survived its owner.");
                Observe("after-owner-close");
            }
            finally
            {
                Cleanup(() => alternate?.Close());
                Cleanup(() => Invoke(window.Editor, "CloseCustomColorFlyout", false));
                Cleanup(() => { if (!closed) window.Close(); });
                Cleanup(() => { if (flyoutSource is { IsDisposed: false }) flyoutSource.RemoveHook(Hook); });
                Cleanup(() => { if (ownerSource is { IsDisposed: false }) ownerSource.RemoveHook(Hook); });
                rows.Add(new { label, theme = theme.ToString(), requestedWidth = width, requestedHeight = height,
                    monitorDpi = monitor.Dpi, sourceImage = new { bitmap.PixelWidth, bitmap.PixelHeight },
                    observations, events, nativeEventLimit = 128, nativeEventLimitReached = events.Count == 128,
                    captionEvents, captionNativeEventLimit = 64, captionEventLimitReached });
            }
            if (failures.Count != failuresBeforeCase)
                throw new InvalidOperationException("Scene cleanup failed; subsequent scenes/input are stopped.");

            void Unchanged()
            {
                Assert.Equal(originalColor, controller.StrokeColor);
                Assert.Equal(originalUndo, controller.Undo.UndoCount);
                Assert.Equal(originalTool, controller.Tool);
            }
            void Observe(string stage)
            {
                Unchanged();
                nint current = (PresentationSource.FromVisual(input) as HwndSource)?.Handle ?? 0;
                observations.Add(new { stage, atMs = clock.Elapsed.TotalMilliseconds, open = flyout.IsVisible,
                    input.IsKeyboardFocused, input.IsKeyboardFocusWithin,
                    selectedAll = input.SelectionStart == 0 && input.SelectionLength == input.Text.Length,
                    managedFocusType = Keyboard.FocusedElement?.GetType().FullName,
                    managedFocusIsInput = ReferenceEquals(Keyboard.FocusedElement, input),
                    foreground = GetForegroundWindow().ToInt64(), nativeFocus = GetFocus().ToInt64(), capture = GetCapture().ToInt64(),
                    owner = owner.ToInt64(), target = current.ToInt64(), sameHwnd = current != 0 && current == owner,
                    ownerAlive = owner != 0 && IsWindow(owner), targetAlive = current != 0 && IsWindow(current),
                    inputText = input.Text, color = AnnotationEditorControl.ColorCode(controller.StrokeColor),
                    undo = controller.Undo.UndoCount, tool = controller.Tool.ToString(),
                });
            }
            void OpenFromClosed()
            {
                Assert.False(flyout.IsVisible);
                Click(opener, "open");
                RequireOwnedVisibleHost();
                Assert.True(input.IsKeyboardFocused && ReferenceEquals(input, Keyboard.FocusedElement));
                Assert.True(input.SelectionStart == 0 && input.SelectionLength == input.Text.Length);
                Observe("open-ready");
            }
            void EnsureVisibleForLifetime()
            {
                if (!flyout.IsVisible) OpenFromClosed();
                else
                {
                    // No opener gesture occurred. Preserve the observed focus rather than
                    // requiring a fresh-open focus transition after deactivation/reattachment.
                    RequireOwnedVisibleHost();
                    Observe("already-visible-lifetime");
                }
            }
            void RequireOwnedVisibleHost()
            {
                Wait(window, () => flyout.IsVisible && input.IsVisible, "color surface visible");
                var source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(input));
                Own(source.Handle);
                Assert.True(source.Handle == owner || GetWindow(source.Handle, 4) == owner,
                    "The actual flyout is neither the owner client nor its direct owned HWND.");
                if (source.Handle != owner && !ReferenceEquals(source, flyoutSource))
                {
                    if (flyoutSource is { IsDisposed: false }) flyoutSource.RemoveHook(Hook);
                    flyoutSource = source;
                    flyoutSource.AddHook(Hook);
                }
                lastFlyout = source.Handle;
            }
            void Capture(string stage)
            {
                var source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(input));
                nint target = source.Handle;
                var pictures = new List<object> { CaptureClient(root, label + "-owner.png", owner, owner, window) };
                if (target != owner) pictures.Add(CaptureClient(root, label + "-flyout.png", target, owner, input));
                Point corner = flyout.PointToScreen(default);
                DpiScale actualDpi = VisualTreeHelper.GetDpi(flyout);
                observations.Add(new { stage = stage + "-images", pictures,
                    flyoutScreen = new { corner.X, corner.Y, width = flyout.ActualWidth * actualDpi.DpiScaleX,
                        height = flyout.ActualHeight * actualDpi.DpiScaleY },
                    flyoutDip = new { flyout.ActualWidth, flyout.ActualHeight }, actualDpi.DpiScaleX, actualDpi.DpiScaleY,
                    surfaceKind = target == owner ? "owner-client" : "separate-owned-hwnd", visualReview = "NOT_REVIEWED" });
            }
            void Click(FrameworkElement element, string stage)
            {
                Assert.True(element.IsVisible && element.IsEnabled && element.ActualWidth > 4 && element.ActualHeight > 4);
                HwndSource source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(element));
                Point center = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
                var point = new NativePoint { X = checked((int)Math.Round(center.X)), Y = checked((int)Math.Round(center.Y)) };
                Assert.Equal(source.Handle, WindowFromPoint(point));
                IInputElement? hit = ((UIElement)source.RootVisual).InputHitTest(
                    ((Visual)source.RootVisual).PointFromScreen(new Point(point.X, point.Y)));
                Assert.True(ReferenceEquals(hit, element) || hit is DependencyObject node && element.IsAncestorOf(node));
                SendClick(source.Handle, point, stage);
            }
            void ClickCaption()
            {
                Assert.True(GetWindowRect(owner, out NativeRect bounds));
                NativePoint? selected = null;
                for (int y = bounds.Top + 8; y < Math.Min(bounds.Top + 40, bounds.Bottom); y += 4)
                    for (int x = bounds.Left + 80; x < Math.Min(bounds.Left + 360, bounds.Right); x += 40)
                    {
                        var candidate = new NativePoint { X = x, Y = y };
                        if (WindowFromPoint(candidate) == owner && HitTest(owner, candidate) == 2) { selected = candidate; break; }
                    }
                Assert.True(selected.HasValue, "No safe HTCAPTION point was observed; no caption click sent.");
                Assert.Equal(2, HitTest(owner, selected.Value));
                SendClick(owner, selected.Value, "nonclient-caption");
            }
            void SendClick(nint target, NativePoint point, string stage)
            {
                Neutral(); Own(owner); Own(target);
                Assert.Equal(owner, GetForegroundWindow());
                Assert.Equal(target, WindowFromPoint(point));
                Assert.True(GetCapture() == 0 || GetCapture() == owner || GetCapture() == lastFlyout);
                Assert.Equal(0, GetSystemMetrics(23));
                int x = GetSystemMetrics(76), y = GetSystemMetrics(77), w = GetSystemMetrics(78), h = GetSystemMetrics(79);
                Assert.True(w > 1 && h > 1 && point.X >= x && point.X < x + w && point.Y >= y && point.Y < y + h);
                Assert.InRange(++clickAttempts, 1, 16);
                int previousUps = nativeUps;
                var commands = new[]
                {
                    MouseEvent(0x0001 | 0x8000 | 0x4000, (int)Math.Round((point.X - x) * 65535d / (w - 1)),
                        (int)Math.Round((point.Y - y) * 65535d / (h - 1))), MouseEvent(0x0002), MouseEvent(0x0004),
                };
                bool observeCaption = stage == "nonclient-caption";
                if (observeCaption)
                {
                    Assert.Equal(owner, target);
                    Assert.True(GetWindowRect(owner, out captionBeforeRect));
                }
                Assert.False(inputUnsettled, "Previous injected input has not settled.");
                inputUnsettled = true; // Cleared only by owned up or the caption-specific complete evidence chain.
                if (observeCaption)
                {
                    captionClickAttempt = clickAttempts;
                    captionPoint = point;
                    captionTarget = target;
                    captionStartedTicks = Stopwatch.GetTimestamp();
                    captionOracle = new EditorCaptionCompletionOracle(owner.ToInt64(), point.X, point.Y, captionStartedTicks);
                    captionTerminal = null;
                    captionCompletionKind = null;
                    captionInFlight = true;
                    observations.Add(CaptionState("caption-before-send", target, 0, 0, 0));
                }
                try
                {
                    uint sent = SendInput(3, commands, Marshal.SizeOf<Input>());
                    // A queued down need not yet be reflected by GetAsyncKeyState. A
                    // partial pair remains FAIL after one owned release and its actual drain.
                    if (sent == 2)
                    {
                        bool sameScope = GetForegroundWindow() == owner && IsWindow(owner)
                            && (GetCapture() == 0 || GetCapture() == owner || GetCapture() == lastFlyout);
                        uint release = 0;
                        bool releaseObserved = false;
                        try
                        {
                            if (sameScope)
                            {
                                Own(owner);
                                release = SendInput(1, [MouseEvent(0x0004)], Marshal.SizeOf<Input>());
                                if (release == 1)
                                {
                                    Wait(window, () => nativeUps > previousUps && (GetAsyncKeyState(1) & 0x8000) == 0,
                                        "partial-input owned release");
                                    Neutral();
                                    inputUnsettled = false;
                                    releaseObserved = true;
                                }
                            }
                        }
                        finally
                        {
                            observations.Add(new { stage = "partial-input-release", sent, sameScope, release, releaseObserved,
                                status = releaseObserved ? "RELEASE_OBSERVED_ORIGINAL_INPUT_FAILED" : "RELEASE_UNCONFIRMED_NO_MORE_INPUT" });
                        }
                    }
                    observations.Add(new { stage = "native-click", purpose = stage, target = target.ToInt64(), point.X, point.Y, sent });
                    Assert.Equal(3U, sent);
                    if (observeCaption)
                    {
                        Wait(window, () =>
                        {
                            if (nativeUps > previousUps && (GetAsyncKeyState(1) & 0x8000) == 0)
                            {
                                captionCompletionKind = "OWNED_MOUSE_UP";
                                return true;
                            }
                            return CaptionCommandComplete(sent);
                        }, "owned mouse-up or complete owned caption command",
                            afterDispatch: () => captionOracle!.AfterDispatcherReturned(Stopwatch.GetTimestamp()));
                    }
                    else
                        Wait(window, () => nativeUps > previousUps && (GetAsyncKeyState(1) & 0x8000) == 0, "owned mouse-up");
                    Neutral();
                    inputUnsettled = false;
                }
                finally
                {
                    if (observeCaption)
                    {
                        captionInFlight = false;
                        observations.Add(new { stage = "caption-settle-finally",
                            state = CaptionState("caption-after-original-gate", target, 0, 0, 0),
                            previousUps, nativeUps, inputUnsettled, captionCompletionKind, captionOracle, captionTerminal,
                            beforeRect = new { captionBeforeRect.Left, captionBeforeRect.Top, captionBeforeRect.Right, captionBeforeRect.Bottom },
                            afterRect = new { captionAfterRect.Left, captionAfterRect.Top, captionAfterRect.Right, captionAfterRect.Bottom },
                            flyoutVisible = flyout.IsVisible,
                            input.IsKeyboardFocused, managedFocusType = Keyboard.FocusedElement?.GetType().FullName });
                    }
                }
            }
            bool CaptionCommandComplete(uint sent)
            {
                if (captionOracle is not { ReadyForState: true } oracle) return false;
                try
                {
                    // This runs only in Wait's predicate after its original synchronous Invoke returned.
                    Own(owner);
                    var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
                    if (!GetGUIThreadInfo(GetCurrentThreadId(), ref info) || !GetWindowRect(owner, out captionAfterRect))
                    {
                        oracle.Fail("Caption terminal GUI/rect query failed.");
                        return false;
                    }
                    bool neutral = Keyboard.Modifiers == ModifierKeys.None;
                    foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 1, 2, 4, 5, 6 })
                        neutral &= (GetAsyncKeyState(key) & 0x8000) == 0;
                    var state = new CaptionCompletionState
                    {
                        Ticks = Stopwatch.GetTimestamp(), GuiRead = true, GuiFlags = info.Flags,
                        Active = info.Active.ToInt64(), Focus = info.Focus.ToInt64(), Foreground = GetForegroundWindow().ToInt64(),
                        Capture = info.Capture.ToInt64(), MenuOwner = info.MenuOwner.ToInt64(), MoveSize = info.MoveSize.ToInt64(),
                        ThreadCapture = GetCapture().ToInt64(), ExactOwner = true,
                        SameRect = captionBeforeRect.Equals(captionAfterRect), QueueLeftDown = (GetKeyState(1) & 0x8000) != 0,
                        AllNeutral = neutral, FlyoutClosed = !flyout.IsVisible,
                        ModelUnchanged = originalColor.Equals(controller.StrokeColor) && originalUndo == controller.Undo.UndoCount
                            && originalTool == controller.Tool && originalItems == controller.Document.Items.Count,
                    };
                    captionTerminal = state;
                    if (state.Foreground != owner.ToInt64() || state.Active != owner.ToInt64() || state.Focus != owner.ToInt64()
                        || !state.SameRect || !state.ModelUnchanged)
                        oracle.Fail("Caption owner/focus/rectangle/model changed.");
                    bool complete = oracle.IsComplete(true, sent, state);
                    if (complete) captionCompletionKind = "OWNED_CAPTION_COMMAND_COMPLETED";
                    return complete;
                }
                catch
                {
                    oracle.Fail("Caption terminal observation threw.");
                    throw;
                }
            }
        }
    });

    private static Border FindFlyout(TextBox input)
    {
        var content = Assert.IsType<StackPanel>(input.Parent);
        var flyout = Assert.IsType<Border>(content.Parent);
        Assert.Same(content, flyout.Child);
        Assert.Single(content.Children.OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == "AnnotationCustomColorApply");
        return flyout;
    }

    private static object CaptureClient(string root, string name, nint target, nint owner, Visual visual)
    {
        Own(owner); Own(target);
        Assert.Equal(owner, GetForegroundWindow());
        Assert.Equal(target, Assert.IsType<HwndSource>(PresentationSource.FromVisual(visual)).Handle);
        Assert.True(target == owner || GetWindow(target, 4) == owner);
        Assert.True(GetClientRect(target, out NativeRect rect));
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        Assert.InRange(width, 8, 8192); Assert.InRange(height, 8, 8192);
        Assert.True((long)width * height <= 16_777_216);
        // This DC has no screen source: only the exact owned HWND paints the fresh DIB.
        nint dc = CreateCompatibleDC(0); Assert.NotEqual(0, dc);
        nint bitmap = 0, previous = 0;
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 0xED; pixels[i + 1] = 0x11; pixels[i + 2] = 0xDA; }
        try
        {
            var info = new BitmapInfo { Header = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 } };
            bitmap = CreateDIBSection(dc, ref info, 0, out nint bits, 0, 0);
            Assert.NotEqual(0, bitmap); Assert.NotEqual(0, bits);
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            previous = SelectObject(dc, bitmap); Assert.True(previous != 0 && previous != -1);
            Assert.True(RedrawWindow(target, 0, 0, 0x0001 | 0x0004 | 0x0080 | 0x0100 | 0x0400));
            Assert.Equal(0, DwmFlush());
            bool printed = PrintWindow(target, dc, 3);
            Assert.True(GdiFlush());
            Own(target);
            Assert.Equal(target, Assert.IsType<HwndSource>(PresentationSource.FromVisual(visual)).Handle);
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
            image.Freeze();
            string path = DiagnosticOutputPaths.Child(root, name);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(file);
            }
            int sentinel = 0; var colors = new HashSet<int>();
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i] == 0xED && pixels[i + 1] == 0x11 && pixels[i + 2] == 0xDA) sentinel++;
                if (colors.Count < 32) colors.Add(pixels[i] | pixels[i + 1] << 8 | pixels[i + 2] << 16);
            }
            string hash;
            using (var stream = File.OpenRead(path)) hash = Convert.ToHexString(SHA256.HashData(stream));
            var result = new { file = name, sha256 = hash, width, height, target = target.ToInt64(), printed,
                unpaintedSentinelPixels = sentinel, distinctColorsAtLeast = colors.Count, nativeDpi = GetDpiForWindow(target) };
            SaveJson(DiagnosticOutputPaths.Child(root, name + ".json"), result);
            Assert.True(printed && colors.Count >= 16 && sentinel < width * height / 20,
                "Native render was empty/incomplete; saved image is not accepted as a visual baseline.");
            return result;
        }
        finally
        {
            if (previous != 0 && previous != -1) _ = SelectObject(dc, previous);
            if (bitmap != 0) _ = DeleteObject(bitmap); // GDI resource, not a filesystem operation.
            _ = DeleteDC(dc);
        }
    }

    private static string FreshRoot()
    {
        string requested = Environment.GetEnvironmentVariable(OutputVariable) ?? throw new InvalidOperationException("Evidence output is required.");
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "MyCapture.slnx")))
            repo = Directory.GetParent(repo)?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
        // A new child of repository artifacts (not artifacts itself); existing evidence is preserved and rejected.
        return TestDiagnosticArtifacts.NewDirectory(requested, Path.Combine(repo, "artifacts"));
    }
    private static void SaveJson(string path, object data)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, data, new JsonSerializerOptions { WriteIndented = true });
    }
    private static void Wait(Window window, Func<bool> predicate, string stage, Action? afterDispatch = null)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            afterDispatch?.Invoke();
            if (predicate()) return;
            Thread.Sleep(10);
        } while (clock.Elapsed < TimeSpan.FromSeconds(3));
        Assert.True(predicate(), stage + " was not observed within 3s; no compensating input.");
    }
    private static void Own(nint hwnd)
    {
        Assert.NotEqual(0, hwnd); Assert.True(IsWindow(hwnd));
        Assert.Equal(GetCurrentThreadId(), GetWindowThreadProcessId(hwnd, out uint pid));
        Assert.Equal((uint)Environment.ProcessId, pid);
    }
    private static void Neutral()
    {
        foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 1, 2, 4, 5, 6 })
            Assert.Equal(0, GetAsyncKeyState(key) & 0x8000);
        Assert.Equal(ModifierKeys.None, Keyboard.Modifiers);
    }
    private static int HitTest(nint hwnd, NativePoint point)
    {
        Assert.InRange(point.X, short.MinValue, short.MaxValue); Assert.InRange(point.Y, short.MinValue, short.MaxValue);
        return checked((int)SendMessage(hwnd, 0x0084, 0, (nint)((point.Y << 16) | (point.X & 0xFFFF))));
    }
    private static Input MouseEvent(uint flags, int x = 0, int y = 0) => new() { Type = 0, Mouse = new() { X = x, Y = y, Flags = flags } };
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretRect;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important; }
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(nint hwnd, nint rect, nint region, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}

internal sealed class ColorFlyoutEvidenceFactAttribute : FactAttribute
{
    public ColorFlyoutEvidenceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EditorColorFlyoutEvidenceTests.EnableVariable) != "1")
            Skip = "Explicit synthetic color flyout desktop evidence requires MYCAPTURE_COLOR_FLYOUT_EVIDENCE=1.";
    }
}
