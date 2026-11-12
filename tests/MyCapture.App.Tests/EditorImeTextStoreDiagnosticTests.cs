using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MyCapture.App.Editing;
using Xunit;
using Xunit.Abstractions;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in, one real T per isolated STA: compare ordinary WPF input, ordinary Popup
/// input and the actual editor color input after observing deferred TSF initialization.
/// Existing editor behavior tests and product input handling remain unchanged.
/// AssemblyInfo.cs disables test parallelization for this entire test assembly.
/// </summary>
public sealed class EditorImeTextStoreDiagnosticTests(ITestOutputHelper output)
{
    [RealKoreanImeFact]
    public void PlainTextBoxReportsItsTextStoreAndComposes() => Run("plain-textbox");

    [RealKoreanImeFact]
    public void PlainPopupReportsItsTextStoreAndComposes() => Run("plain-popup");

    [RealKoreanImeFact]
    public void ProductColorInlineHostReportsItsTextStoreAndComposes() => Run("product-color");

    [OnScreenPlainPopupDiagnosticFact]
    public void OnScreenPlainPopupReportsItsTextStoreAndComposes()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("MYCAPTURE_IME_ONSCREEN_D0"));
        Assert.Null(Environment.GetEnvironmentVariable("MYCAPTURE_IME_TSF_SET_FOCUS"));
        Assert.Null(Environment.GetEnvironmentVariable("MYCAPTURE_IME_NATIVE_FOCUS_OBSERVE"));
        Run("plain-popup", onScreenOwner: true);
    }

    [NativeClickPlainPopupDiagnosticFact]
    public void OnScreenPlainPopupNativeClickReportsItsTextStoreAndComposes()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("MYCAPTURE_IME_ONSCREEN_NATIVE_CLICK"));
        Assert.Null(Environment.GetEnvironmentVariable("MYCAPTURE_IME_ONSCREEN_D0"));
        Assert.Null(Environment.GetEnvironmentVariable("MYCAPTURE_IME_TSF_SET_FOCUS"));
        Assert.Null(Environment.GetEnvironmentVariable("MYCAPTURE_IME_NATIVE_FOCUS_OBSERVE"));
        Run("plain-popup", onScreenOwner: true, focusByNativeClick: true);
    }

    [NativeClickPopupBoundaryDiagnosticFact]
    public void OnScreenPlainPopupNativeClickReportsFocusBoundariesAndComposes()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("MYCAPTURE_IME_ONSCREEN_POPUP_B2"));
        Assert.Equal("1", Environment.GetEnvironmentVariable("MYCAPTURE_REAL_IME_TEST"));
        Assert.Null(Environment.GetEnvironmentVariable("MYCAPTURE_IME_ONSCREEN_NATIVE_CLICK"));
        Assert.Null(Environment.GetEnvironmentVariable("MYCAPTURE_IME_ONSCREEN_WINDOW_CLICK"));
        Assert.Null(Environment.GetEnvironmentVariable("MYCAPTURE_IME_ONSCREEN_D0"));
        Assert.Null(Environment.GetEnvironmentVariable("MYCAPTURE_IME_TSF_SET_FOCUS"));
        Assert.Null(Environment.GetEnvironmentVariable("MYCAPTURE_IME_NATIVE_FOCUS_OBSERVE"));
        Run("plain-popup", onScreenOwner: true, focusByNativeClick: true, observePopupFocus: true);
    }

    [NativeProductColorOpenerDiagnosticFact]
    public void OnScreenProductColorInlineHostOpenerNativeClickComposes()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("MYCAPTURE_IME_PRODUCT_OPENER"));
        Assert.Equal("1", Environment.GetEnvironmentVariable("MYCAPTURE_REAL_IME_TEST"));
        foreach (string name in new[] { "MYCAPTURE_IME_ONSCREEN_NATIVE_CLICK", "MYCAPTURE_IME_ONSCREEN_POPUP_B2",
            "MYCAPTURE_IME_ONSCREEN_WINDOW_CLICK", "MYCAPTURE_IME_ONSCREEN_D0",
            "MYCAPTURE_IME_TSF_SET_FOCUS", "MYCAPTURE_IME_NATIVE_FOCUS_OBSERVE" })
            Assert.Null(Environment.GetEnvironmentVariable(name));
        RunProductOpenerNativeClick();
    }

    private void RunProductOpenerNativeClick() => StaTestHost.Run(() =>
    {
        const string kind = "product-opener";
        var clock = Stopwatch.StartNew();
        void Trace(string message) => output.WriteLine($"{clock.Elapsed.TotalMilliseconds:F3} ms [{kind}]: {message}");
        AssertNeutralModifiers();
        IntPtr previousLayout = GetKeyboardLayout(0);
        IntPtr[] loadedBefore = LoadedLayouts();
        IntPtr korean = IntPtr.Zero;
        Window? window = null;
        TextBox? box = null;
        FrameworkElement? colorHost = null;
        UIElement? placement = null;
        EditorWindowHost? product = null;
        NativeProductOpenerClick? nativeClick = null;
        DependencyPropertyChangedEventHandler? openedObserver = null;
        Action<string>? assertProductUnchanged = null;
        bool captured = false, bodyPassed = false;
        InputMethodState previousState = InputMethodState.DoNotCare;
        ImeConversionModeValues previousMode = ImeConversionModeValues.DoNotCare;
        Exception? bodyFailure = null;
        var cleanupFailures = new List<Exception>();
        string phase = "preparation";
        int keyAttempts = 0, openedCount = 0;
        try
        {
            Trace($"Runtime={Environment.Version}; PresentationFramework={typeof(TextBox).Assembly.GetName().Version}; MVID={typeof(TextBox).Module.ModuleVersionId}.");
            korean = LoadKeyboardLayout("00000412", 1);
            Assert.True(korean != IntPtr.Zero && (korean.ToInt64() & 0xFFFF) == 0x0412);
            bool rendered = false;
            Rect workArea = SystemParameters.WorkArea;
            Assert.True(workArea.Width >= 1000 && workArea.Height >= 640,
                "The work area cannot contain the unchanged 1000x640 editor; no click/key sent.");
            product = EditorWindowHost.Create(beforeShow: candidate =>
            {
                candidate.Left = workArea.Left + (workArea.Width - 1000) / 2;
                candidate.Top = workArea.Top + (workArea.Height - 640) / 2;
                candidate.ContentRendered += (_, _) => { rendered = true; Trace("ContentRendered"); };
            });
            window = product.Window;
            box = product.Editor.CustomColorInputForTest;
            colorHost = product.Editor.CustomColorFlyoutContentForTest;
            Button opener = product.Editor.CustomColorButtonForTest;
            AnnotationEditorController controller = Controller(product.Editor);
            var originalColor = controller.StrokeColor;
            int originalUndoCount = controller.Undo.UndoCount;
            var originalTool = controller.Tool;
            assertProductUnchanged = stage =>
            {
                Trace($"PRODUCT_UNCHANGED: stage={stage}; color={AnnotationEditorControl.ColorCode(controller.StrokeColor)}; undo={controller.Undo.UndoCount}; tool={controller.Tool}; expectedColor={AnnotationEditorControl.ColorCode(originalColor)}; expectedUndo={originalUndoCount}; expectedTool={originalTool}.");
                Assert.Equal(originalColor, controller.StrokeColor);
                Assert.Equal(originalUndoCount, controller.Undo.UndoCount);
                Assert.Equal(originalTool, controller.Tool);
            };
            assertProductUnchanged("before-opener");
            placement = opener;
            TextBox target = box;
            FrameworkElement inlineHost = colorHost;
            // Visibility precedes the product's Focus/SelectAll. Count the actual open
            // transition here; verify completed focus after the existing mouse-up boundary.
            openedObserver = (_, e) =>
            {
                if (e.NewValue is true) { openedCount++; Trace($"PRODUCT_INLINE_VISIBLE: count={openedCount}."); }
            };
            inlineHost.IsVisibleChanged += openedObserver;
            Observe(target, Trace);
            Pump(window, () => rendered);
            Assert.True(rendered, "ContentRendered did not complete; no click/key sent.");
            IntPtr owner = new WindowInteropHelper(window).Handle;
            window.Activate();
            SetForegroundWindow(owner);
            Pump(window, () => GetForegroundWindow() == owner);
            Assert.Equal(owner, GetForegroundWindow());
            Assert.NotEqual(IntPtr.Zero, ActivateKeyboardLayout(korean, 0));
            Assert.Equal(korean, GetKeyboardLayout(0));
            Assert.False(product.Editor.IsCustomColorFlyoutOpenForTest);
            Assert.False(target.IsKeyboardFocused);
            Assert.True(InputMethod.GetIsInputMethodEnabled(target));
            phase = "opener-click";
            nativeClick = new NativeProductOpenerClick(clock, Trace);
            nativeClick.ClickOnce(window, opener, target, product.Editor);
            nativeClick.RequireMouseUpProcessed(window, target);
            Assert.Equal(1, openedCount);
            Assert.True(ReferenceEquals(Keyboard.FocusedElement, target) && target.IsKeyboardFocused,
                "The product opener did not focus its input; no T sent.");
            Assert.True(target.Text.Length > 0 && target.SelectionStart == 0 && target.SelectionLength == target.Text.Length,
                "The product opener did not SelectAll; no T sent.");
            RequireInlineProductHost(window, product.Editor, target);
            AssertOwnedFocus(window, target, null, placement, Trace);
            // Same bounded initialization barrier used by the existing IME diagnostics.
            // It does not focus the TextBox or create a TSF object.
            DrainInitialization(window);
            AssertOwnedFocus(window, target, null, placement, Trace);
            previousState = InputMethod.Current.ImeState;
            previousMode = InputMethod.Current.ImeConversionMode;
            captured = true;
            Trace($"Captured IME state={previousState}, mode={previousMode}.");
            InputMethod.Current.ImeState = InputMethodState.On;
            InputMethod.Current.ImeConversionMode = ImeConversionModeValues.Native;
            Pump(window, () => InputMethod.Current.ImeState == InputMethodState.On
                && InputMethod.Current.ImeConversionMode.HasFlag(ImeConversionModeValues.Native));
            Assert.Equal(InputMethodState.On, InputMethod.Current.ImeState);
            Assert.True(InputMethod.Current.ImeConversionMode.HasFlag(ImeConversionModeValues.Native));
            Assert.True(target.Text.Length > 0 && target.SelectionStart == 0 && target.SelectionLength == target.Text.Length,
                "Product SelectAll did not remain valid; no compensating SelectAll or T sent.");
            phase = "c-support-and-input-guards";
            TsfFocusObservation beforeKey = Snapshot(target, "product-opener-c-before-key", Trace);
            Assert.True(beforeKey.Supported, "Existing TSF identities could not be observed at C; no T sent.");
            nativeClick.RequireReadyForKey(window, target);
            phase = "body-key-and-text";
            keyAttempts++;
            TypeT(window, target, null, placement, korean, Trace);
            Pump(window, () => target.Text.Contains('ㅅ'));
            Trace($"Body result text={CodePoints(target.Text)}; keyAttempts={keyAttempts}.");
            Assert.Contains("ㅅ", target.Text);
            Assert.Equal(1, keyAttempts);
            assertProductUnchanged("body-after-text");
            bodyPassed = true;
            Trace("BODY_PASS: real input changed Text before restoration.");
        }
        catch (Exception error)
        {
            bodyFailure = error;
            Trace($"BODY_FAIL: phase={phase}; keyAttempts={keyAttempts}; {error.GetType().Name}: {error.Message}");
            Trace("BODY_EXCEPTION " + JsonSerializer.Serialize(new { type = error.GetType().FullName, detail = error.ToString() }));
            throw;
        }
        finally
        {
            try
            {
                try
                {
                    ObserveCleanup("ime-restore", () =>
                    {
                        if (captured && window is not null && box is not null)
                        {
                            // Same restoration boundary as EditorRealImeTests: never reclaim a
                            // foreground window from another app, and never bypass ownership guards.
                            Assert.Equal(new WindowInteropHelper(window).Handle, GetForegroundWindow());
                            RequireInlineProductHost(window, product!.Editor, box);
                            Assert.True(box.Focus());
                            Pump(window, () => box.IsKeyboardFocused);
                            AssertOwnedFocus(window, box, null, placement, Trace, allowOwnedPopupNativeFocus: false);
                            if (previousMode != ImeConversionModeValues.DoNotCare)
                            {
                                Snapshot(box, "before-restore-ime-mode", Trace);
                                InputMethod.Current.ImeConversionMode = previousMode;
                                Snapshot(box, "after-restore-ime-mode", Trace);
                            }
                            if (previousState != InputMethodState.DoNotCare)
                            {
                                Snapshot(box, "before-restore-ime-state", Trace);
                                InputMethod.Current.ImeState = previousState;
                                Snapshot(box, "after-restore-ime-state", Trace);
                            }
                            Pump(window, () => (previousMode == ImeConversionModeValues.DoNotCare || InputMethod.Current.ImeConversionMode == previousMode)
                                && (previousState == InputMethodState.DoNotCare || InputMethod.Current.ImeState == previousState));
                            Trace($"Restored IME state={InputMethod.Current.ImeState}, mode={InputMethod.Current.ImeConversionMode}; expected={previousState}/{previousMode}; bodyPassed={bodyPassed}.");
                            if (previousMode != ImeConversionModeValues.DoNotCare) Assert.Equal(previousMode, InputMethod.Current.ImeConversionMode);
                            if (previousState != InputMethodState.DoNotCare) Assert.Equal(previousState, InputMethod.Current.ImeState);
                        }
                    }, cleanupFailures, Trace);
                }
                finally
                {
                    try
                    {
                        ObserveCleanup("native-mouse-restore", () =>
                        {
                            if (nativeClick is not null && window is not null) nativeClick.Restore(window);
                        }, cleanupFailures, Trace);
                    }
                    finally
                    {
                        try
                        {
                            ObserveCleanup("layout-restore", () =>
                            {
                                Assert.NotEqual(IntPtr.Zero, ActivateKeyboardLayout(previousLayout, 0));
                                Trace($"Restored HKL=0x{GetKeyboardLayout(0).ToInt64():X}; expected=0x{previousLayout.ToInt64():X}.");
                                Assert.Equal(previousLayout, GetKeyboardLayout(0));
                            }, cleanupFailures, Trace);
                        }
                        finally
                        {
                            try
                            {
                                ObserveCleanup("layout-unload", () =>
                                {
                                    if (korean != IntPtr.Zero && !loadedBefore.Contains(korean))
                                    {
                                        Assert.True(UnloadKeyboardLayout(korean));
                                        Assert.DoesNotContain(korean, LoadedLayouts());
                                    }
                                }, cleanupFailures, Trace);
                            }
                            finally
                            {
                                try { ObserveCleanup("popup-close", () => { if (product is not null && product.Editor.IsCustomColorFlyoutOpenForTest) product.Editor.HandleShortcut(Key.Escape, ModifierKeys.None); }, cleanupFailures, Trace); }
                                finally
                                {
                                    try
                                    {
                                        ObserveCleanup("product-color-undo-unchanged-after-close",
                                            () => assertProductUnchanged?.Invoke("after-ime-restore-and-popup-close"), cleanupFailures, Trace);
                                    }
                                    finally
                                    {
                                        try { ObserveCleanup("product-opened-observer-remove", () => { if (colorHost is not null && openedObserver is not null) colorHost.IsVisibleChanged -= openedObserver; }, cleanupFailures, Trace); }
                                        finally { ObserveCleanup("owner-close", () => { if (product is not null) product.Dispose(); else window?.Close(); }, cleanupFailures, Trace); }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception cleanupError)
            {
                // Original nested cleanup still runs and fails. Retain errors that a later
                // finally would otherwise replace, including the first body exception.
                if (!cleanupFailures.Contains(cleanupError)) cleanupFailures.Add(cleanupError);
                if (bodyFailure is null && cleanupFailures.Count == 1) throw;
                var failures = new List<Exception>();
                if (bodyFailure is not null) failures.Add(bodyFailure);
                failures.AddRange(cleanupFailures);
                throw new AggregateException("IME diagnostic body/cleanup failures are preserved in execution order.", failures);
            }
        }
        Trace("CASE_PASS: body and IME/layout restoration completed.");
    });

    private sealed class NativeProductOpenerClick(Stopwatch clock, Action<string> trace)
    {
        private readonly NativeClickMessage[] _messages = new NativeClickMessage[256];
        private int _messageCount, _dropped, _flushed;
        private HwndSource? _ownerSource, _targetSource;
        private bool _ownerHooked, _cursorSaved, _releaseAttempted;
        private NativePoint _savedCursor;
        private NativeRect _virtualScreen;
        private uint _sent;
        private bool _attempted;
        private long _sentAt;
        private TextBox? _colorInput;
        private AnnotationEditorControl? _colorEditor;
        private Button? _opener;

        internal void ClickOnce(Window window, Button box, TextBox colorInput, AnnotationEditorControl colorEditor)
        {
            Assert.False(_attempted);
            _colorInput = colorInput; _colorEditor = colorEditor; _opener = box;
            Assert.False(colorEditor.IsCustomColorFlyoutOpenForTest);
            Assert.False(colorInput.IsKeyboardFocused);
            Assert.Same(box, colorEditor.CustomColorButtonForTest);
            Assert.True(colorEditor.CustomColorFlyoutContentForTest.IsAncestorOf(colorInput));
            IntPtr owner = new WindowInteropHelper(window).Handle;
            Assert.Equal(owner, GetForegroundWindow());
            Assert.NotEqual(IntPtr.Zero, owner);
            Assert.True(window.IsVisible && window.IsEnabled && box.IsVisible && box.IsEnabled);
            _ownerSource = Assert.IsType<HwndSource>(PresentationSource.FromVisual(window));
            _targetSource = Assert.IsType<HwndSource>(PresentationSource.FromVisual(box));
            Assert.False(_ownerSource.IsDisposed || _targetSource.IsDisposed);
            Assert.Same(_ownerSource, _targetSource);
            Assert.Equal(owner, _targetSource.Handle);
            IntPtr target = _targetSource.Handle;
            Assert.NotEqual(IntPtr.Zero, target);
            Assert.Equal(0, GetWindowLong(target, -20) & 0x08000000);
            Assert.True(IsWindowVisible(target) && IsWindowEnabled(target));
            Assert.False(box.IsKeyboardFocused, "Target already has focus before the sole click; no click/key sent.");
            uint thread = GetCurrentThreadId();
            Assert.Equal(thread, GetWindowThreadProcessId(owner, out uint ownerProcess));
            Assert.Equal((uint)Environment.ProcessId, ownerProcess);
            Assert.Equal(thread, GetWindowThreadProcessId(target, out uint targetProcess));
            Assert.Equal(ownerProcess, targetProcess);
            AssertNeutralModifiers();
            AssertNeutralMouseButtons();
            Assert.Equal(0, GetSystemMetrics(23)); // Reject swapped primary buttons; do not change them.
            Assert.True(IsOwnedRestingCapture(), "Capture is outside the exact owned Window; no click/key sent.");
            uint dpi = GetDpiForWindow(target);
            Assert.InRange(dpi, 96U, 768U);
            Assert.Equal(dpi, GetDpiForWindow(owner));
            System.Windows.DpiScale visualDpi = System.Windows.Media.VisualTreeHelper.GetDpi(box);
            Assert.Equal(dpi / 96d, visualDpi.DpiScaleX);
            Assert.Equal(dpi / 96d, visualDpi.DpiScaleY);
            Assert.True(double.IsFinite(box.ActualWidth) && double.IsFinite(box.ActualHeight)
                && box.ActualWidth > 4 && box.ActualHeight > 4);
            Point center = box.PointToScreen(new Point(box.ActualWidth / 2, box.ActualHeight / 2));
            Assert.True(double.IsFinite(center.X) && double.IsFinite(center.Y));
            var point = new NativePoint { X = checked((int)Math.Round(center.X)), Y = checked((int)Math.Round(center.Y)) };
            _virtualScreen = VirtualScreen();
            Assert.True(Contains(_virtualScreen, point), "Target is outside the physical virtual desktop.");
            Assert.True(GetWindowRect(target, out NativeRect targetBounds) && Contains(targetBounds, point));
            Point localPoint = box.PointFromScreen(new Point(point.X, point.Y));
            Assert.True(localPoint.X > 1 && localPoint.X < box.ActualWidth - 1
                && localPoint.Y > 1 && localPoint.Y < box.ActualHeight - 1);
            WaitForOwnedHit(window, box, owner, target, point, targetBounds, dpi);
            Assert.Equal(target, WindowFromPoint(point));
            RequireOpenerHit(window, box, point);
            Assert.True(GetCursorPos(out _savedCursor));
            _cursorSaved = true;
            Assert.True(Contains(_virtualScreen, _savedCursor));
            _ownerSource.AddHook(Hook); _ownerHooked = true;
            trace($"PRODUCT_OPENER_CLICK_PRE: owner=0x{owner.ToInt64():X}; target=0x{target.ToInt64():X}; physical={point.X},{point.Y}; dpi={dpi}; savedCursor={_savedCursor.X},{_savedCursor.Y}; nativeCapture=0x{GetCapture().ToInt64():X}.");
            // Final native ownership checks are adjacent to SendInput; no dispatch between them.
            Assert.Equal(owner, GetForegroundWindow());
            Assert.True(window.IsVisible && window.IsEnabled && box.IsVisible && box.IsEnabled);
            Assert.Equal(target, WindowFromPoint(point));
            RequireOpenerHit(window, box, point);
            Assert.False(colorEditor.IsCustomColorFlyoutOpenForTest || colorInput.IsKeyboardFocused);
            Assert.True(IsOwnedRestingCapture());
            AssertNeutralModifiers();
            AssertNeutralMouseButtons();
            int width = _virtualScreen.Right - _virtualScreen.Left, height = _virtualScreen.Bottom - _virtualScreen.Top;
            var inputs = new[]
            {
                MouseEvent(0x0001 | 0x8000 | 0x4000,
                    (int)Math.Round((point.X - _virtualScreen.Left) * 65535d / (width - 1)),
                    (int)Math.Round((point.Y - _virtualScreen.Top) * 65535d / (height - 1))),
                MouseEvent(0x0002), MouseEvent(0x0004),
            };
            _attempted = true;
            _sentAt = Stopwatch.GetTimestamp();
            _sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            int error = Marshal.GetLastWin32Error();
            trace($"PRODUCT_OPENER_SENDINPUT_RETURN: requested=3; inserted={_sent}; lastError={error}; order=move/down/up; no input retry.");
            Assert.Equal(3U, _sent);
        }

        private static void RequireOpenerHit(Window window, Button opener, NativePoint point)
        {
            IInputElement? hit = window.InputHitTest(window.PointFromScreen(new Point(point.X, point.Y)));
            Assert.True(ReferenceEquals(hit, opener) || (hit is DependencyObject descendant && opener.IsAncestorOf(descendant)),
                "The physical point does not hit the exact product opener subtree; no click/key sent.");
        }

        private void WaitForOwnedHit(Window window, Button box,
            IntPtr owner, IntPtr target, NativePoint point, NativeRect targetBounds, uint dpi)
        {
            HwndSource ownerSource = Assert.IsType<HwndSource>(_ownerSource);
            HwndSource targetSource = Assert.IsType<HwndSource>(_targetSource);
            int observations = 0, renderCallbacks = 0;
            double startedMs = clock.Elapsed.TotalMilliseconds;
            double? firstOwnedHitMs = null;
            object? initial = null;
            bool conditionObserved = false;
            EventHandler rendering = (_, _) => renderCallbacks++;

            object Observe()
            {
                IntPtr hit = WindowFromPoint(point);
                observations++;
                if (hit == target && firstOwnedHitMs is null) firstOwnedHitMs = clock.Elapsed.TotalMilliseconds;
                return new
                {
                    observedMs = clock.Elapsed.TotalMilliseconds,
                    hitEqualsExactWindow = hit == target,
                    hit = DescribeNativeWindow(hit, owner, target),
                    owner = DescribeNativeWindow(owner, owner, target),
                    target = DescribeNativeWindow(target, owner, target),
                    foreground = GetForegroundWindow().ToInt64(),
                    point = new { point.X, point.Y },
                    targetOpacity = box.Opacity,
                    targetHitTestVisible = box.IsHitTestVisible,
                    rootOpacity = (_targetSource?.RootVisual as UIElement)?.Opacity,
                    rootHitTestVisible = (_targetSource?.RootVisual as UIElement)?.IsHitTestVisible,
                };
            }

            void RequireUnchangedOwnerAndGeometry()
            {
                Assert.False(ownerSource.IsDisposed || targetSource.IsDisposed);
                Assert.Equal(owner, new WindowInteropHelper(window).Handle);
                Assert.Equal(owner, ownerSource.Handle);
                Assert.Equal(target, targetSource.Handle);
                Assert.Same(ownerSource, PresentationSource.FromVisual(window));
                Assert.Same(targetSource, PresentationSource.FromVisual(box));
                Assert.Same(ownerSource, targetSource);
                Assert.Equal(owner, target);
                Assert.True(window.IsVisible && window.IsEnabled && box.IsVisible && box.IsEnabled);
                Assert.False(box.IsKeyboardFocused, "Target acquired focus before the sole click; no click/key sent.");
                Assert.Equal(owner, GetForegroundWindow());
                Assert.Equal(0, GetWindowLong(target, -20) & 0x08000000);
                Assert.True(IsWindowVisible(owner) && IsWindowEnabled(owner));
                Assert.True(IsWindowVisible(target) && IsWindowEnabled(target));
                uint thread = GetCurrentThreadId();
                Assert.Equal(thread, GetWindowThreadProcessId(owner, out uint ownerProcess));
                Assert.Equal((uint)Environment.ProcessId, ownerProcess);
                Assert.Equal(thread, GetWindowThreadProcessId(target, out uint targetProcess));
                Assert.Equal(ownerProcess, targetProcess);
                Assert.Equal(dpi, GetDpiForWindow(owner));
                Assert.Equal(dpi, GetDpiForWindow(target));
                System.Windows.DpiScale currentDpi = System.Windows.Media.VisualTreeHelper.GetDpi(box);
                Assert.Equal(dpi / 96d, currentDpi.DpiScaleX);
                Assert.Equal(dpi / 96d, currentDpi.DpiScaleY);
                Assert.True(GetWindowRect(target, out NativeRect currentBounds));
                Assert.Equal(targetBounds, currentBounds);
                Assert.Equal(_virtualScreen, VirtualScreen());
                Point center = box.PointToScreen(new Point(box.ActualWidth / 2, box.ActualHeight / 2));
                Assert.True(double.IsFinite(center.X) && double.IsFinite(center.Y));
                Assert.Equal(point.X, checked((int)Math.Round(center.X)));
                Assert.Equal(point.Y, checked((int)Math.Round(center.Y)));
                Assert.True(Contains(currentBounds, point) && Contains(_virtualScreen, point));
                Point local = box.PointFromScreen(new Point(point.X, point.Y));
                Assert.True(local.X > 1 && local.X < box.ActualWidth - 1
                    && local.Y > 1 && local.Y < box.ActualHeight - 1);
                AssertNeutralModifiers();
                AssertNeutralMouseButtons();
                Assert.Equal(0, GetSystemMetrics(23));
                Assert.True(IsOwnedRestingCapture());
            }

            System.Windows.Media.CompositionTarget.Rendering += rendering;
            try
            {
                initial = Observe();
                // Same existing three-second dispatcher pump. Rendering callbacks are observations,
                // not proof of display presentation. Only the unchanged exact native hit permits input.
                Pump(window, () =>
                {
                    RequireUnchangedOwnerAndGeometry();
                    IntPtr hit = WindowFromPoint(point);
                    observations++;
                    if (hit == target && firstOwnedHitMs is null)
                        firstOwnedHitMs = clock.Elapsed.TotalMilliseconds;
                    conditionObserved = hit == target;
                    return conditionObserved;
                });
                RequireUnchangedOwnerAndGeometry();
            }
            finally
            {
                System.Windows.Media.CompositionTarget.Rendering -= rendering;
                object final = Observe();
                trace("PRODUCT_OPENER_HIT_READINESS " + JsonSerializer.Serialize(new
                {
                    startedMs, finishedMs = clock.Elapsed.TotalMilliseconds,
                    deadlineMilliseconds = 3000, observations, renderCallbacks,
                    firstOwnedHitMs, conditionObserved, initial, final,
                    windowTitles = "NOT_READ", pixels = "NOT_READ",
                    observationScope = "Only this bounded pre-input wait; no focus/style/input changes",
                }));
            }
        }

        private static object DescribeNativeWindow(IntPtr handle, IntPtr owner, IntPtr target)
        {
            uint thread = GetWindowThreadProcessId(handle, out uint process);
            var className = new System.Text.StringBuilder(256);
            Marshal.SetLastPInvokeError(0);
            int classLength = GetClassName(handle, className, className.Capacity);
            int classError = classLength == 0 ? Marshal.GetLastPInvokeError() : 0;
            bool rectangleObserved = GetWindowRect(handle, out NativeRect rect);
            int style = GetWindowLong(handle, -16), extendedStyle = GetWindowLong(handle, -20);
            return new
            {
                hwnd = handle.ToInt64(), equalsOwner = handle == owner, equalsTarget = handle == target,
                threadId = thread, processId = process, identityObserved = thread != 0,
                className = classLength > 0 ? className.ToString() : null, classError,
                style, extendedStyle, layered = (extendedStyle & 0x00080000) != 0,
                transparent = (extendedStyle & 0x00000020) != 0,
                nativeOwner = GetWindow(handle, 4).ToInt64(), visible = IsWindowVisible(handle),
                enabled = IsWindowEnabled(handle), rectangleObserved,
                rectangle = rectangleObserved ? new[] { rect.Left, rect.Top, rect.Right, rect.Bottom } : null,
            };
        }

        internal void RequireMouseUpProcessed(Window window, TextBox box)
        {
            Pump(window, () => _colorEditor?.IsCustomColorFlyoutOpenForTest == true && box.IsKeyboardFocused
                && HasMessage(0x0202) && MouseButtonsNeutral() && IsOwnedRestingCapture());
            Assert.True(HasMessage(0x0201) && HasMessage(0x0202),
                "The exact owner did not observe the complete native down/up pair; no T sent.");
            RequireReadyForKey(window, box);
        }

        internal void RequireReadyForKey(Window window, TextBox box)
        {
            Assert.Same(_colorInput, box);
            Assert.NotNull(_colorEditor);
            Assert.True(_colorEditor.IsCustomColorFlyoutOpenForTest && box.IsKeyboardFocused);
            AssertNeutralMouseButtons();
            Assert.True(IsOwnedRestingCapture(), "Foreign or unfinished button capture remains; no T sent.");
            RequireInlineProductHost(window, _colorEditor, box);
            AssertOwnedFocus(window, box, null, _opener, trace);
            Assert.Equal(3U, _sent);
            Assert.False(_releaseAttempted);
            FlushMessages();
        }

        internal void Restore(Window window)
        {
            var errors = new List<Exception>();
            void Attempt(string stage, Action action)
            {
                try { action(); }
                catch (Exception error) { errors.Add(error); trace($"PRODUCT_OPENER_CLEANUP_FAIL {stage}: {error}"); }
            }
            Attempt("owned-left-up", () =>
            {
                // SendInput inserts in order. A returned prefix of two includes our DOWN
                // without UP. One compensating UP is cleanup, never a click or T retry.
                if (_sent == 2 && !_releaseAttempted)
                {
                    _releaseAttempted = true;
                    Input[] release = [MouseEvent(0x0004)];
                    uint released = SendInput(1, release, Marshal.SizeOf<Input>());
                    trace($"PRODUCT_OPENER_COMPENSATING_LEFTUP: inserted={released}; lastError={Marshal.GetLastWin32Error()}; original failure retained.");
                    Assert.Equal(1U, released);
                    Pump(window, () => HasMessage(0x0202) && MouseButtonsNeutral() && IsOwnedRestingCapture());
                    Assert.True(HasMessage(0x0202) && MouseButtonsNeutral() && IsOwnedRestingCapture());
                }
            });
            Attempt("cursor-restore", () =>
            {
                if (_cursorSaved && _sent > 0)
                {
                    // Drain the submitted MOVE before restoring it. Do not take the cursor
                    // back if another user/window has acquired its current physical point.
                    Pump(window, CursorIsInOwnedWindow);
                    Assert.True(CursorIsInOwnedWindow(), "Cursor left the owned Window; preserve its current position and report incomplete restoration.");
                    Assert.Equal(_virtualScreen, VirtualScreen());
                    AssertNeutralMouseButtons();
                    Assert.True(SetCursorPos(_savedCursor.X, _savedCursor.Y));
                    Assert.True(GetCursorPos(out NativePoint restored));
                    Assert.Equal(_savedCursor.X, restored.X);
                    Assert.Equal(_savedCursor.Y, restored.Y);
                    trace($"PRODUCT_OPENER_CURSOR_RESTORED: {restored.X},{restored.Y}.");
                }
            });
            Attempt("owner-hook-remove", () => { if (_ownerHooked) { _ownerSource!.RemoveHook(Hook); _ownerHooked = false; } });
            Attempt("message-observation", FlushMessages);
            trace($"PRODUCT_OPENER_INPUT_ACCOUNT: clickBatches={(_attempted ? 1 : 0)}; inserted={_sent}; compensatingLeftUp={_releaseAttempted}; droppedMessages={_dropped}.");
            if (errors.Count != 0) throw new AggregateException("Product opener mouse cleanup failures.", errors);
        }

        private bool CursorIsInOwnedWindow() => _targetSource is { IsDisposed: false }
            && GetCursorPos(out NativePoint position) && WindowFromPoint(position) == _targetSource.Handle;

        private bool IsOwnedRestingCapture() => _targetSource is { IsDisposed: false }
            && Mouse.Captured is null && GetCapture() == IntPtr.Zero;

        private bool HasMessage(int message) => _messages.Take(_messageCount).Any(value =>
            value.Message == message && value.Hwnd == _targetSource?.Handle && value.Timestamp >= _sentAt);

        private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message is 0x0021 or 0x0006 or 0x0007 or 0x0008 or 0x0281 or 0x010D
                or 0x0200 or 0x0201 or 0x0202 or 0x0215)
            {
                if (_messageCount < _messages.Length)
                    _messages[_messageCount++] = new(Stopwatch.GetTimestamp(), clock.Elapsed.TotalMilliseconds,
                        hwnd, message, wParam, lParam);
                else _dropped++;
            }
            handled = false;
            return IntPtr.Zero;
        }

        private void FlushMessages()
        {
            while (_flushed < _messageCount)
            {
                NativeClickMessage value = _messages[_flushed++];
                trace($"PRODUCT_OPENER_HWND_MESSAGE: observedMs={value.ElapsedMs:F3}; ticks={value.Timestamp}; hwnd=0x{value.Hwnd.ToInt64():X}; msg=0x{value.Message:X}; wParam=0x{value.WParam.ToInt64():X}; lParam=0x{value.LParam.ToInt64():X}.");
            }
            Assert.Equal(0, _dropped);
        }

        private static bool Contains(NativeRect rect, NativePoint point) => point.X >= rect.Left && point.X < rect.Right
            && point.Y >= rect.Top && point.Y < rect.Bottom;
        private static NativeRect VirtualScreen()
        {
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
            Assert.True(width > 1 && height > 1);
            return new NativeRect { Left = left, Top = top, Right = checked(left + width), Bottom = checked(top + height) };
        }
        private static Input MouseEvent(uint flags, int x = 0, int y = 0) => new()
        {
            Type = 0, Data = new InputUnion { Mouse = new MouseInput { X = x, Y = y, Flags = flags } },
        };
        private readonly record struct NativeClickMessage(long Timestamp, double ElapsedMs,
            IntPtr Hwnd, int Message, IntPtr WParam, IntPtr LParam);
    }

    [NativeClickPlainWindowDiagnosticFact]
    public void OnScreenPlainWindowNativeClickReportsItsTextStoreAndComposes()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("MYCAPTURE_IME_ONSCREEN_WINDOW_CLICK"));
        Assert.Equal("1", Environment.GetEnvironmentVariable("MYCAPTURE_REAL_IME_TEST"));
        Assert.Null(Environment.GetEnvironmentVariable("MYCAPTURE_IME_ONSCREEN_NATIVE_CLICK"));
        Assert.Null(Environment.GetEnvironmentVariable("MYCAPTURE_IME_ONSCREEN_D0"));
        Assert.Null(Environment.GetEnvironmentVariable("MYCAPTURE_IME_TSF_SET_FOCUS"));
        Assert.Null(Environment.GetEnvironmentVariable("MYCAPTURE_IME_NATIVE_FOCUS_OBSERVE"));
        RunWindowNativeD0();
    }


    private void RunWindowNativeD0() => StaTestHost.Run(() =>
    {
        const string kind = "window-native-d0";
        var clock = Stopwatch.StartNew();
        void Trace(string message) => output.WriteLine($"{clock.Elapsed.TotalMilliseconds:F3} ms [{kind}]: {message}");
        AssertNeutralModifiers();
        IntPtr previousLayout = GetKeyboardLayout(0);
        IntPtr[] loadedBefore = LoadedLayouts();
        IntPtr korean = IntPtr.Zero;
        Window? window = null;
        TextBox? box = null;
        Popup? popup = null;
        UIElement? placement = null;
        EditorWindowHost? product = null;
        NativeWindowClick? nativeClick = null;
        WindowFocusSamples? samples = null;
        bool captured = false;
        bool bodyPassed = false;
        InputMethodState previousState = InputMethodState.DoNotCare;
        ImeConversionModeValues previousMode = ImeConversionModeValues.DoNotCare;
        Exception? bodyFailure = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            Trace($"Runtime={Environment.Version}; PresentationFramework={typeof(TextBox).Assembly.GetName().Version}; MVID={typeof(TextBox).Module.ModuleVersionId}.");
            korean = LoadKeyboardLayout("00000412", 1);
            Assert.True(korean != IntPtr.Zero && (korean.ToInt64() & 0xFFFF) == 0x0412);
            bool rendered = false;
            box = new TextBox { Text = "#EF4444", Style = null, MinWidth = 260, MinHeight = 32, FontSize = 16 };
            InputMethod.SetIsInputMethodEnabled(box, true);
            var anchor = new Button { Content = "Synthetic input diagnostic", MinWidth = 260, MinHeight = 32 };
            var content = new StackPanel();
            content.Children.Add(anchor);
            content.Children.Add(box);
            Rect workArea = SystemParameters.WorkArea;
            Assert.True(workArea.Width >= 400 && workArea.Height >= 160,
                "The primary work area cannot contain the unchanged 400x160 owner; no key was sent.");
            window = new Window
            {
                Content = content, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = workArea.Left + (workArea.Width - 400) / 2,
                Top = workArea.Top + (workArea.Height - 160) / 2, Width = 400, Height = 160,
                ShowActivated = false, ShowInTaskbar = false,
            };
            // Attach before Show, so any earlier focus is explicitly observed.
            samples = new WindowFocusSamples(box, Trace);
            window.ContentRendered += (_, _) => { rendered = true; Trace("ContentRendered"); };
            window.Show();
            window.UpdateLayout();
            Pump(window, () => rendered);
            Assert.True(rendered, "ContentRendered did not complete. No key was sent.");
            TracePlacement(window, popup, "after-content-rendered", Trace);
            if (box is not null) Snapshot(box, "after-content-rendered", Trace);
            IntPtr owner = new WindowInteropHelper(window).Handle;
            window.Activate();
            SetForegroundWindow(owner);
            Pump(window, () => GetForegroundWindow() == owner);
            Assert.Equal(owner, GetForegroundWindow());
            if (box is not null) Snapshot(box, "after-foreground-pump", Trace);
            Assert.NotEqual(IntPtr.Zero, ActivateKeyboardLayout(korean, 0));
            Assert.Equal(korean, GetKeyboardLayout(0));
            TextBox target = Assert.IsType<TextBox>(box);
            TracePlacement(window, popup, "after-target-preparation", Trace);
            Snapshot(target, "after-target-preparation", Trace);
            Observe(target, Trace);
            nativeClick = new NativeWindowClick(clock, Trace);
            nativeClick.ClickOnce(window, target, samples);
            Pump(window, () => target.IsKeyboardFocused, afterDispatch: samples.AfterDispatch);
            nativeClick.RequireMouseUpProcessed(window, target, samples);
            samples.RequireFirstTwo();
            AssertOwnedFocus(window, target, popup, placement, Trace);
            Snapshot(target, "prepared-focus-before-additional-idle", Trace);

            // v10.0.11 TextEditor.OnTextViewUpdated queues InitTextStore at Background:
            // https://github.com/dotnet/wpf/blob/v10.0.11/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Documents/TextEditor.cs#L1392
            // Earlier ContentRendered/foreground/helper Pumps may already have initialized TSF.
            // This is an additional idle barrier, not a claim of observing pre-initialization.
            // One bounded idle barrier observes that initialization; no internal mutation,
            // focus reset, extra key, forced completion or retry is performed.
            DrainInitialization(window);
            Snapshot(target, "after-context-idle", Trace);
            AssertOwnedFocus(window, target, popup, placement, Trace, allowOwnedPopupNativeFocus: false);
            previousState = InputMethod.Current.ImeState;
            previousMode = InputMethod.Current.ImeConversionMode;
            captured = true;
            Trace($"Captured IME state={previousState}, mode={previousMode}.");
            Snapshot(target, "before-ime-state-on", Trace);
            InputMethod.Current.ImeState = InputMethodState.On;
            Snapshot(target, "after-ime-state-on", Trace);
            Snapshot(target, "before-ime-mode-native", Trace);
            InputMethod.Current.ImeConversionMode = ImeConversionModeValues.Native;
            Snapshot(target, "after-ime-mode-native", Trace);
            Pump(window, () => InputMethod.Current.ImeState == InputMethodState.On
                && InputMethod.Current.ImeConversionMode.HasFlag(ImeConversionModeValues.Native));
            Assert.Equal(InputMethodState.On, InputMethod.Current.ImeState);
            Assert.True(InputMethod.Current.ImeConversionMode.HasFlag(ImeConversionModeValues.Native));
            target.SelectAll();
            samples.BeforeKey();
            AssertNeutralMouseButtons();
            Assert.Equal(IntPtr.Zero, GetCapture());
            Assert.Null(Mouse.Captured);
            TypeT(window, target, popup, placement, korean, Trace, allowOwnedPopupNativeFocus: false);
            Snapshot(target, "after-key-dispatch-before-body-wait", Trace);
            Pump(window, () => target.Text.Contains('ㅅ'));
            Snapshot(target, "after-key", Trace);
            Trace($"Body result text={CodePoints(target.Text)}; productTool={(product?.Editor is { } editor ? Controller(editor).Tool.ToString() : "not-applicable")}.");
            Assert.Contains("ㅅ", target.Text);
            bodyPassed = true;
            Trace("BODY_PASS: real input changed Text before restoration.");
        }
        catch (Exception error)
        {
            bodyFailure = error;
            Trace($"BODY_FAIL: {error.GetType().Name}: {error.Message}");
            Trace("BODY_EXCEPTION " + JsonSerializer.Serialize(new { type = error.GetType().FullName, detail = error.ToString() }));
            throw;
        }
        finally
        {
            try
            {
                try
                {
                    ObserveCleanup("ime-restore", () =>
                    {
                        if (captured && window is not null && box is not null)
                        {
                            // Same restoration boundary as EditorRealImeTests: never reclaim a
                            // foreground window from another app, and never bypass ownership guards.
                            Assert.Equal(new WindowInteropHelper(window).Handle, GetForegroundWindow());
                            if (popup is not null) Assert.True(popup.IsOpen);
                            Assert.True(box.Focus());
                            Pump(window, () => box.IsKeyboardFocused);
                            AssertOwnedFocus(window, box, popup, placement, Trace, allowOwnedPopupNativeFocus: false);
                            if (previousMode != ImeConversionModeValues.DoNotCare)
                            {
                                Snapshot(box, "before-restore-ime-mode", Trace);
                                InputMethod.Current.ImeConversionMode = previousMode;
                                Snapshot(box, "after-restore-ime-mode", Trace);
                            }
                            if (previousState != InputMethodState.DoNotCare)
                            {
                                Snapshot(box, "before-restore-ime-state", Trace);
                                InputMethod.Current.ImeState = previousState;
                                Snapshot(box, "after-restore-ime-state", Trace);
                            }
                            Pump(window, () => (previousMode == ImeConversionModeValues.DoNotCare || InputMethod.Current.ImeConversionMode == previousMode)
                                && (previousState == InputMethodState.DoNotCare || InputMethod.Current.ImeState == previousState));
                            Trace($"Restored IME state={InputMethod.Current.ImeState}, mode={InputMethod.Current.ImeConversionMode}; expected={previousState}/{previousMode}; bodyPassed={bodyPassed}.");
                            if (previousMode != ImeConversionModeValues.DoNotCare) Assert.Equal(previousMode, InputMethod.Current.ImeConversionMode);
                            if (previousState != InputMethodState.DoNotCare) Assert.Equal(previousState, InputMethod.Current.ImeState);
                        }
                    }, cleanupFailures, Trace);
                }
                finally
                {
                    try
                    {
                        ObserveCleanup("native-mouse-restore", () =>
                        {
                            if (nativeClick is not null && window is not null) nativeClick.Restore(window);
                        }, cleanupFailures, Trace);
                    }
                    finally
                    {
                        try
                        {
                            ObserveCleanup("layout-restore", () =>
                            {
                                Assert.NotEqual(IntPtr.Zero, ActivateKeyboardLayout(previousLayout, 0));
                                Trace($"Restored HKL=0x{GetKeyboardLayout(0).ToInt64():X}; expected=0x{previousLayout.ToInt64():X}.");
                                Assert.Equal(previousLayout, GetKeyboardLayout(0));
                            }, cleanupFailures, Trace);
                        }
                        finally
                        {
                            try
                            {
                                ObserveCleanup("layout-unload", () =>
                                {
                                    if (korean != IntPtr.Zero && !loadedBefore.Contains(korean))
                                    {
                                        Assert.True(UnloadKeyboardLayout(korean));
                                        Assert.DoesNotContain(korean, LoadedLayouts());
                                    }
                                }, cleanupFailures, Trace);
                            }
                            finally
                            {
                                try { ObserveCleanup("popup-close", () => { if (popup is not null) popup.IsOpen = false; }, cleanupFailures, Trace); }
                                finally
                                {
                                    try { ObserveCleanup("window-observer-remove", () => samples?.Dispose(), cleanupFailures, Trace); }
                                    finally { ObserveCleanup("owner-close", () => { if (product is not null) product.Dispose(); else window?.Close(); }, cleanupFailures, Trace); }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception cleanupError)
            {
                // Original nested cleanup still runs and fails. Retain errors that a later
                // finally would otherwise replace, including the first body exception.
                if (!cleanupFailures.Contains(cleanupError)) cleanupFailures.Add(cleanupError);
                if (bodyFailure is null && cleanupFailures.Count == 1) throw;
                var failures = new List<Exception>();
                if (bodyFailure is not null) failures.Add(bodyFailure);
                failures.AddRange(cleanupFailures);
                throw new AggregateException("IME diagnostic body/cleanup failures are preserved in execution order.", failures);
            }
        }
        Trace("CASE_PASS: body and IME/layout restoration completed.");
    });

    // Window D0 observes only existing objects at an event/Invoke-return boundary.
    // No callback creates a TSF manager, pumps, changes focus, or sends input.
    private sealed class WindowFocusSamples : IDisposable
    {
        private readonly TextBox _box;
        private readonly Action<string> _trace;
        private readonly KeyboardFocusChangedEventHandler _handler;
        private bool _armed, _a, _b, _c;
        private int _focusEvents, _dispatchReturns;
        private Exception? _observationFailure;
        private TsfFocusObservation _sampleA, _sampleB, _sampleC;

        internal WindowFocusSamples(TextBox box, Action<string> trace)
        {
            _box = box; _trace = trace; _handler = OnGotFocus;
            box.AddHandler(Keyboard.GotKeyboardFocusEvent, _handler, true);
        }

        internal void ArmClick()
        {
            Assert.False(_armed);
            Assert.False(_box.IsKeyboardFocused, "Target already has focus before the sole click; no click/key sent.");
            _armed = true;
        }

        private void OnGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!ReferenceEquals(e.NewFocus, _box)) return;
            _focusEvents++;
            _trace($"WINDOW_D0_FOCUS_EVENT: count={_focusEvents}; clickArmed={_armed}; sampleAAlreadyObserved={_a}.");
            if (!_armed || _a) return;
            _a = true;
            Observe("window-a-got-keyboard-focus", value => _sampleA = value);
        }

        internal void AfterDispatch()
        {
            _dispatchReturns++;
            if (!_a || _b) return;
            _b = true;
            Observe("window-b-first-existing-invoke-return", value => _sampleB = value);
        }

        internal void RequireFirstTwo()
        {
            Assert.Null(_observationFailure);
            Assert.True(_a && _b, "Window D0 requires A and B from existing event/Invoke boundaries; no T sent.");
            Assert.True(_sampleA.Supported && _sampleB.Supported,
                "Existing TSF identities could not be observed at A/B; no T sent.");
        }

        internal void BeforeKey()
        {
            RequireFirstTwo();
            Assert.False(_c);
            _c = true;
            Observe("window-c-before-key", value => _sampleC = value);
            Assert.Null(_observationFailure);
            Assert.True(_sampleC.Supported, "Existing TSF identities could not be observed at C; no T sent.");
        }

        private void Observe(string stage, Action<TsfFocusObservation> keep)
        {
            try
            {
                HwndSource? source = PresentationSource.FromVisual(_box) as HwndSource;
                _trace("WINDOW_D0_FOCUS_SAMPLE " + JsonSerializer.Serialize(new
                {
                    stage, focusEvents = _focusEvents, dispatchReturns = _dispatchReturns,
                    nativeFocus = GetFocus().ToInt64(), foreground = GetForegroundWindow().ToInt64(),
                    targetSource = source?.Handle.ToInt64(), sourceDisposed = source?.IsDisposed,
                    _box.IsKeyboardFocused, managedFocusIsTarget = ReferenceEquals(Keyboard.FocusedElement, _box),
                }));
                keep(Snapshot(_box, stage, _trace));
            }
            catch (Exception error)
            {
                _observationFailure ??= error;
                _trace($"WINDOW_D0_OBSERVATION_FAIL {stage}: {error}");
            }
        }

        public void Dispose() => _box.RemoveHandler(Keyboard.GotKeyboardFocusEvent, _handler);
    }

    private sealed class PopupFocusSamples : IDisposable
    {
        private readonly TextBox _box;
        private readonly Action<string> _trace;
        private readonly KeyboardFocusChangedEventHandler _handler;
        private bool _armed, _a, _b, _c;
        private int _focusEvents, _dispatchReturns;
        private Exception? _observationFailure;
        private TsfFocusObservation _sampleA, _sampleB, _sampleC;

        internal PopupFocusSamples(TextBox box, Action<string> trace)
        {
            _box = box; _trace = trace; _handler = OnGotFocus;
            box.AddHandler(Keyboard.GotKeyboardFocusEvent, _handler, true);
        }

        internal void ArmClick()
        {
            Assert.False(_armed);
            Assert.False(_box.IsKeyboardFocused, "Target already has focus before the sole click; no click/key sent.");
            _armed = true;
        }

        private void OnGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!ReferenceEquals(e.NewFocus, _box)) return;
            _focusEvents++;
            _trace($"POPUP_B2_FOCUS_EVENT: count={_focusEvents}; clickArmed={_armed}; sampleAAlreadyObserved={_a}.");
            if (!_armed || _a) return;
            _a = true;
            Observe("popup-a-got-keyboard-focus", value => _sampleA = value);
        }

        internal void AfterDispatch()
        {
            _dispatchReturns++;
            if (!_a || _b) return;
            _b = true;
            Observe("popup-b-first-existing-invoke-return", value => _sampleB = value);
        }

        internal void RequireFirstTwo()
        {
            Assert.Null(_observationFailure);
            Assert.True(_a && _b, "Popup B2 requires A and B from existing event/Invoke boundaries; no T sent.");
            Assert.True(_sampleA.Supported && _sampleB.Supported,
                "Existing TSF identities could not be observed at A/B; no T sent.");
        }

        internal void BeforeKey()
        {
            RequireFirstTwo();
            Assert.False(_c);
            _c = true;
            Observe("popup-c-before-key", value => _sampleC = value);
            Assert.Null(_observationFailure);
            Assert.True(_sampleC.Supported, "Existing TSF identities could not be observed at C; no T sent.");
        }

        private void Observe(string stage, Action<TsfFocusObservation> keep)
        {
            try
            {
                HwndSource? source = PresentationSource.FromVisual(_box) as HwndSource;
                _trace("POPUP_B2_FOCUS_SAMPLE " + JsonSerializer.Serialize(new
                {
                    stage, focusEvents = _focusEvents, dispatchReturns = _dispatchReturns,
                    nativeFocus = GetFocus().ToInt64(), foreground = GetForegroundWindow().ToInt64(),
                    targetSource = source?.Handle.ToInt64(), sourceDisposed = source?.IsDisposed,
                    _box.IsKeyboardFocused, managedFocusIsTarget = ReferenceEquals(Keyboard.FocusedElement, _box),
                }));
                keep(Snapshot(_box, stage, _trace));
            }
            catch (Exception error)
            {
                _observationFailure ??= error;
                _trace($"POPUP_B2_OBSERVATION_FAIL {stage}: {error}");
            }
        }

        public void Dispose() => _box.RemoveHandler(Keyboard.GotKeyboardFocusEvent, _handler);
    }

    private static void RequireWindowTextStoreReady(TextBox box, Action<string> trace)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        Type editorType = typeof(TextBoxBase).Assembly.GetType("System.Windows.Documents.TextEditor")
            ?? throw new NotSupportedException("Window D0: missing TextEditor type; no click/key sent.");
        MethodInfo getEditor = editorType.GetMethod("_GetTextEditor", flags)
            ?? throw new NotSupportedException("Window D0: missing TextEditor._GetTextEditor; no click/key sent.");
        object editor = getEditor.Invoke(null, [box])
            ?? throw new NotSupportedException("Window D0: existing TextEditor absent; no click/key sent.");
        object? ReadField(Type type, object instance, string name, Type? expectedType = null)
        {
            FieldInfo field = type.GetField(name, flags)
                ?? throw new NotSupportedException($"Window D0: missing {type.FullName}.{name}; no click/key sent.");
            if (expectedType is not null && field.FieldType != expectedType)
                throw new NotSupportedException($"Window D0: unexpected {type.FullName}.{name} type; no click/key sent.");
            return field.GetValue(instance);
        }
        bool started = (bool)ReadField(editorType, editor, "_textStoreInitStarted", typeof(bool))!;
        bool pending = (bool)ReadField(editorType, editor, "_pendingTextStoreInit", typeof(bool))!;
        object? store = ReadField(editorType, editor, "_textstore");
        object? document = null, sink = null;
        if (store is not null)
        {
            if (store.GetType().FullName != "System.Windows.Documents.TextStore")
                throw new NotSupportedException("Window D0: unexpected existing TextStore type; no click/key sent.");
            PropertyInfo documentProperty = store.GetType().GetProperty("DocumentManager", flags)
                ?? throw new NotSupportedException("Window D0: missing TextStore.DocumentManager; no click/key sent.");
            // The exact v10.0.11 getter returns _documentmanager; it creates nothing.
            document = documentProperty.GetValue(store);
            sink = ReadField(store.GetType(), store, "_sink");
        }
        trace("WINDOW_D0_PRECLICK_INITIALIZATION " + JsonSerializer.Serialize(new
        {
            initStarted = started, initPending = pending, storePresent = store is not null,
            documentPresent = document is not null, sinkPresent = sink is not null,
            fieldContract = "TextEditor._textStoreInitStarted/_pendingTextStoreInit/_textstore; TextStore.DocumentManager/_sink",
            preClickExtraIdleBarrier = false,
        }));
        Assert.True(started && !pending && store is not null && document is not null && sink is not null,
            "Existing TextStore is not fully initialized at the unchanged pre-click boundary; no click/key sent.");
    }

    [DllImport("user32.dll")] private static extern IntPtr GetFocus();


    private sealed class NativeWindowClick(Stopwatch clock, Action<string> trace)
    {
        private readonly NativeClickMessage[] _messages = new NativeClickMessage[256];
        private int _messageCount, _dropped, _flushed;
        private HwndSource? _ownerSource, _targetSource;
        private bool _ownerHooked, _cursorSaved, _releaseAttempted;
        private NativePoint _savedCursor;
        private NativeRect _virtualScreen;
        private uint _sent;
        private bool _attempted;
        private long _sentAt;

        internal void ClickOnce(Window window, TextBox box, WindowFocusSamples samples)
        {
            Assert.False(_attempted);
            IntPtr owner = new WindowInteropHelper(window).Handle;
            Assert.Equal(owner, GetForegroundWindow());
            Assert.NotEqual(IntPtr.Zero, owner);
            Assert.True(window.IsVisible && window.IsEnabled && box.IsVisible && box.IsEnabled);
            _ownerSource = Assert.IsType<HwndSource>(PresentationSource.FromVisual(window));
            _targetSource = Assert.IsType<HwndSource>(PresentationSource.FromVisual(box));
            Assert.False(_ownerSource.IsDisposed || _targetSource.IsDisposed);
            Assert.Same(_ownerSource, _targetSource);
            Assert.Equal(owner, _targetSource.Handle);
            IntPtr target = _targetSource.Handle;
            Assert.NotEqual(IntPtr.Zero, target);
            Assert.Equal(0, GetWindowLong(target, -20) & 0x08000000);
            Assert.True(IsWindowVisible(target) && IsWindowEnabled(target));
            Assert.False(box.IsKeyboardFocused, "Target already has focus before the sole click; no click/key sent.");
            uint thread = GetCurrentThreadId();
            Assert.Equal(thread, GetWindowThreadProcessId(owner, out uint ownerProcess));
            Assert.Equal((uint)Environment.ProcessId, ownerProcess);
            Assert.Equal(thread, GetWindowThreadProcessId(target, out uint targetProcess));
            Assert.Equal(ownerProcess, targetProcess);
            AssertNeutralModifiers();
            AssertNeutralMouseButtons();
            Assert.Equal(0, GetSystemMetrics(23)); // Reject swapped primary buttons; do not change them.
            Assert.True(IsOwnedRestingCapture(), "Capture is outside the exact owned Window; no click/key sent.");
            uint dpi = GetDpiForWindow(target);
            Assert.InRange(dpi, 96U, 768U);
            Assert.Equal(dpi, GetDpiForWindow(owner));
            System.Windows.DpiScale visualDpi = System.Windows.Media.VisualTreeHelper.GetDpi(box);
            Assert.Equal(dpi / 96d, visualDpi.DpiScaleX);
            Assert.Equal(dpi / 96d, visualDpi.DpiScaleY);
            Assert.True(double.IsFinite(box.ActualWidth) && double.IsFinite(box.ActualHeight)
                && box.ActualWidth > 4 && box.ActualHeight > 4);
            Point center = box.PointToScreen(new Point(box.ActualWidth / 2, box.ActualHeight / 2));
            Assert.True(double.IsFinite(center.X) && double.IsFinite(center.Y));
            var point = new NativePoint { X = checked((int)Math.Round(center.X)), Y = checked((int)Math.Round(center.Y)) };
            _virtualScreen = VirtualScreen();
            Assert.True(Contains(_virtualScreen, point), "Target is outside the physical virtual desktop.");
            Assert.True(GetWindowRect(target, out NativeRect targetBounds) && Contains(targetBounds, point));
            Point localPoint = box.PointFromScreen(new Point(point.X, point.Y));
            Assert.True(localPoint.X > 1 && localPoint.X < box.ActualWidth - 1
                && localPoint.Y > 1 && localPoint.Y < box.ActualHeight - 1);
            WaitForOwnedHit(window, box, owner, target, point, targetBounds, dpi);
            Assert.Equal(target, WindowFromPoint(point));
            Assert.True(GetCursorPos(out _savedCursor));
            _cursorSaved = true;
            Assert.True(Contains(_virtualScreen, _savedCursor));
            _ownerSource.AddHook(Hook); _ownerHooked = true;
            RequireWindowTextStoreReady(box, trace);
            Snapshot(box, "before-window-d0-native-click", trace);
            trace($"WINDOW_D0_CLICK_PRE: owner=0x{owner.ToInt64():X}; target=0x{target.ToInt64():X}; physical={point.X},{point.Y}; dpi={dpi}; savedCursor={_savedCursor.X},{_savedCursor.Y}; nativeCapture=0x{GetCapture().ToInt64():X}.");
            // Final native ownership checks are adjacent to SendInput; no dispatch between them.
            Assert.Equal(owner, GetForegroundWindow());
            Assert.True(window.IsVisible && window.IsEnabled && box.IsVisible && box.IsEnabled);
            Assert.Equal(target, WindowFromPoint(point));
            Assert.True(IsOwnedRestingCapture());
            AssertNeutralModifiers();
            AssertNeutralMouseButtons();
            int width = _virtualScreen.Right - _virtualScreen.Left, height = _virtualScreen.Bottom - _virtualScreen.Top;
            var inputs = new[]
            {
                MouseEvent(0x0001 | 0x8000 | 0x4000,
                    (int)Math.Round((point.X - _virtualScreen.Left) * 65535d / (width - 1)),
                    (int)Math.Round((point.Y - _virtualScreen.Top) * 65535d / (height - 1))),
                MouseEvent(0x0002), MouseEvent(0x0004),
            };
            samples.ArmClick();
            _attempted = true;
            _sentAt = Stopwatch.GetTimestamp();
            _sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            int error = Marshal.GetLastWin32Error();
            trace($"WINDOW_D0_SENDINPUT_RETURN: requested=3; inserted={_sent}; lastError={error}; order=move/down/up; no input retry.");
            Assert.Equal(3U, _sent);
        }

        private void WaitForOwnedHit(Window window, TextBox box,
            IntPtr owner, IntPtr target, NativePoint point, NativeRect targetBounds, uint dpi)
        {
            HwndSource ownerSource = Assert.IsType<HwndSource>(_ownerSource);
            HwndSource targetSource = Assert.IsType<HwndSource>(_targetSource);
            int observations = 0, renderCallbacks = 0;
            double startedMs = clock.Elapsed.TotalMilliseconds;
            double? firstOwnedHitMs = null;
            object? initial = null;
            bool conditionObserved = false;
            EventHandler rendering = (_, _) => renderCallbacks++;

            object Observe()
            {
                IntPtr hit = WindowFromPoint(point);
                observations++;
                if (hit == target && firstOwnedHitMs is null) firstOwnedHitMs = clock.Elapsed.TotalMilliseconds;
                return new
                {
                    observedMs = clock.Elapsed.TotalMilliseconds,
                    hitEqualsExactWindow = hit == target,
                    hit = DescribeNativeWindow(hit, owner, target),
                    owner = DescribeNativeWindow(owner, owner, target),
                    target = DescribeNativeWindow(target, owner, target),
                    foreground = GetForegroundWindow().ToInt64(),
                    point = new { point.X, point.Y },
                    targetOpacity = box.Opacity,
                    targetHitTestVisible = box.IsHitTestVisible,
                    rootOpacity = (_targetSource?.RootVisual as UIElement)?.Opacity,
                    rootHitTestVisible = (_targetSource?.RootVisual as UIElement)?.IsHitTestVisible,
                };
            }

            void RequireUnchangedOwnerAndGeometry()
            {
                Assert.False(ownerSource.IsDisposed || targetSource.IsDisposed);
                Assert.Equal(owner, new WindowInteropHelper(window).Handle);
                Assert.Equal(owner, ownerSource.Handle);
                Assert.Equal(target, targetSource.Handle);
                Assert.Same(ownerSource, PresentationSource.FromVisual(window));
                Assert.Same(targetSource, PresentationSource.FromVisual(box));
                Assert.Same(ownerSource, targetSource);
                Assert.Equal(owner, target);
                Assert.True(window.IsVisible && window.IsEnabled && box.IsVisible && box.IsEnabled);
                Assert.False(box.IsKeyboardFocused, "Target acquired focus before the sole click; no click/key sent.");
                Assert.Equal(owner, GetForegroundWindow());
                Assert.Equal(0, GetWindowLong(target, -20) & 0x08000000);
                Assert.True(IsWindowVisible(owner) && IsWindowEnabled(owner));
                Assert.True(IsWindowVisible(target) && IsWindowEnabled(target));
                uint thread = GetCurrentThreadId();
                Assert.Equal(thread, GetWindowThreadProcessId(owner, out uint ownerProcess));
                Assert.Equal((uint)Environment.ProcessId, ownerProcess);
                Assert.Equal(thread, GetWindowThreadProcessId(target, out uint targetProcess));
                Assert.Equal(ownerProcess, targetProcess);
                Assert.Equal(dpi, GetDpiForWindow(owner));
                Assert.Equal(dpi, GetDpiForWindow(target));
                System.Windows.DpiScale currentDpi = System.Windows.Media.VisualTreeHelper.GetDpi(box);
                Assert.Equal(dpi / 96d, currentDpi.DpiScaleX);
                Assert.Equal(dpi / 96d, currentDpi.DpiScaleY);
                Assert.True(GetWindowRect(target, out NativeRect currentBounds));
                Assert.Equal(targetBounds, currentBounds);
                Assert.Equal(_virtualScreen, VirtualScreen());
                Point center = box.PointToScreen(new Point(box.ActualWidth / 2, box.ActualHeight / 2));
                Assert.True(double.IsFinite(center.X) && double.IsFinite(center.Y));
                Assert.Equal(point.X, checked((int)Math.Round(center.X)));
                Assert.Equal(point.Y, checked((int)Math.Round(center.Y)));
                Assert.True(Contains(currentBounds, point) && Contains(_virtualScreen, point));
                Point local = box.PointFromScreen(new Point(point.X, point.Y));
                Assert.True(local.X > 1 && local.X < box.ActualWidth - 1
                    && local.Y > 1 && local.Y < box.ActualHeight - 1);
                AssertNeutralModifiers();
                AssertNeutralMouseButtons();
                Assert.Equal(0, GetSystemMetrics(23));
                Assert.True(IsOwnedRestingCapture());
            }

            System.Windows.Media.CompositionTarget.Rendering += rendering;
            try
            {
                initial = Observe();
                // Same existing three-second dispatcher pump. Rendering callbacks are observations,
                // not proof of display presentation. Only the unchanged exact native hit permits input.
                Pump(window, () =>
                {
                    RequireUnchangedOwnerAndGeometry();
                    IntPtr hit = WindowFromPoint(point);
                    observations++;
                    if (hit == target && firstOwnedHitMs is null)
                        firstOwnedHitMs = clock.Elapsed.TotalMilliseconds;
                    conditionObserved = hit == target;
                    return conditionObserved;
                });
                RequireUnchangedOwnerAndGeometry();
            }
            finally
            {
                System.Windows.Media.CompositionTarget.Rendering -= rendering;
                object final = Observe();
                trace("WINDOW_D0_HIT_READINESS " + JsonSerializer.Serialize(new
                {
                    startedMs, finishedMs = clock.Elapsed.TotalMilliseconds,
                    deadlineMilliseconds = 3000, observations, renderCallbacks,
                    firstOwnedHitMs, conditionObserved, initial, final,
                    windowTitles = "NOT_READ", pixels = "NOT_READ",
                    observationScope = "Only this bounded pre-input wait; no focus/style/input changes",
                }));
            }
        }

        private static object DescribeNativeWindow(IntPtr handle, IntPtr owner, IntPtr target)
        {
            uint thread = GetWindowThreadProcessId(handle, out uint process);
            var className = new System.Text.StringBuilder(256);
            Marshal.SetLastPInvokeError(0);
            int classLength = GetClassName(handle, className, className.Capacity);
            int classError = classLength == 0 ? Marshal.GetLastPInvokeError() : 0;
            bool rectangleObserved = GetWindowRect(handle, out NativeRect rect);
            int style = GetWindowLong(handle, -16), extendedStyle = GetWindowLong(handle, -20);
            return new
            {
                hwnd = handle.ToInt64(), equalsOwner = handle == owner, equalsTarget = handle == target,
                threadId = thread, processId = process, identityObserved = thread != 0,
                className = classLength > 0 ? className.ToString() : null, classError,
                style, extendedStyle, layered = (extendedStyle & 0x00080000) != 0,
                transparent = (extendedStyle & 0x00000020) != 0,
                nativeOwner = GetWindow(handle, 4).ToInt64(), visible = IsWindowVisible(handle),
                enabled = IsWindowEnabled(handle), rectangleObserved,
                rectangle = rectangleObserved ? new[] { rect.Left, rect.Top, rect.Right, rect.Bottom } : null,
            };
        }

        internal void RequireMouseUpProcessed(Window window, TextBox box, WindowFocusSamples samples)
        {
            Pump(window, () => box.IsKeyboardFocused && HasMessage(0x0202)
                && MouseButtonsNeutral() && IsOwnedRestingCapture(), afterDispatch: samples.AfterDispatch);
            Assert.True(HasMessage(0x0201) && HasMessage(0x0202), "The owned Window did not observe the complete native down/up pair; no T sent.");
            Assert.True(box.IsKeyboardFocused);
            AssertNeutralMouseButtons();
            Assert.True(IsOwnedRestingCapture(), "TextBox/foreign mouse capture remains; no T sent.");
            Assert.Equal(new WindowInteropHelper(window).Handle, GetForegroundWindow());
            FlushMessages();
            Snapshot(box, "after-window-d0-mouse-up-pump", trace);
        }

        internal void Restore(Window window)
        {
            var errors = new List<Exception>();
            void Attempt(string stage, Action action)
            {
                try { action(); }
                catch (Exception error) { errors.Add(error); trace($"WINDOW_D0_CLEANUP_FAIL {stage}: {error}"); }
            }
            Attempt("owned-left-up", () =>
            {
                // SendInput inserts in order. A returned prefix of two includes our DOWN
                // without UP. One compensating UP is cleanup, never a click or T retry.
                if (_sent == 2 && !_releaseAttempted)
                {
                    _releaseAttempted = true;
                    Input[] release = [MouseEvent(0x0004)];
                    uint released = SendInput(1, release, Marshal.SizeOf<Input>());
                    trace($"WINDOW_D0_COMPENSATING_LEFTUP: inserted={released}; lastError={Marshal.GetLastWin32Error()}; original failure retained.");
                    Assert.Equal(1U, released);
                    Pump(window, () => HasMessage(0x0202) && MouseButtonsNeutral() && IsOwnedRestingCapture());
                    Assert.True(HasMessage(0x0202) && MouseButtonsNeutral() && IsOwnedRestingCapture());
                }
            });
            Attempt("cursor-restore", () =>
            {
                if (_cursorSaved && _sent > 0)
                {
                    // Drain the submitted MOVE before restoring it. Do not take the cursor
                    // back if another user/window has acquired its current physical point.
                    Pump(window, CursorIsInOwnedWindow);
                    Assert.True(CursorIsInOwnedWindow(), "Cursor left the owned Window; preserve its current position and report incomplete restoration.");
                    Assert.Equal(_virtualScreen, VirtualScreen());
                    AssertNeutralMouseButtons();
                    Assert.True(SetCursorPos(_savedCursor.X, _savedCursor.Y));
                    Assert.True(GetCursorPos(out NativePoint restored));
                    Assert.Equal(_savedCursor.X, restored.X);
                    Assert.Equal(_savedCursor.Y, restored.Y);
                    trace($"WINDOW_D0_CURSOR_RESTORED: {restored.X},{restored.Y}.");
                }
            });
            Attempt("owner-hook-remove", () => { if (_ownerHooked) { _ownerSource!.RemoveHook(Hook); _ownerHooked = false; } });
            Attempt("message-observation", FlushMessages);
            trace($"WINDOW_D0_INPUT_ACCOUNT: clickBatches={(_attempted ? 1 : 0)}; inserted={_sent}; compensatingLeftUp={_releaseAttempted}; droppedMessages={_dropped}.");
            if (errors.Count != 0) throw new AggregateException("Window D0 mouse cleanup failures.", errors);
        }

        private bool CursorIsInOwnedWindow() => _targetSource is { IsDisposed: false }
            && GetCursorPos(out NativePoint position) && WindowFromPoint(position) == _targetSource.Handle;

        private bool IsOwnedRestingCapture()
        {
            if (_targetSource is null || _targetSource.IsDisposed) return false;
            return Mouse.Captured is null && GetCapture() == IntPtr.Zero;
        }

        private bool HasMessage(int message) => _messages.Take(_messageCount).Any(value =>
            value.Message == message && value.Hwnd == _targetSource?.Handle && value.Timestamp >= _sentAt);

        private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message is 0x0021 or 0x0006 or 0x0007 or 0x0008 or 0x0281 or 0x010D
                or 0x0200 or 0x0201 or 0x0202 or 0x0215)
            {
                if (_messageCount < _messages.Length)
                    _messages[_messageCount++] = new(Stopwatch.GetTimestamp(), clock.Elapsed.TotalMilliseconds,
                        hwnd, message, wParam, lParam);
                else _dropped++;
            }
            handled = false;
            return IntPtr.Zero;
        }

        private void FlushMessages()
        {
            while (_flushed < _messageCount)
            {
                NativeClickMessage value = _messages[_flushed++];
                trace($"WINDOW_D0_HWND_MESSAGE: observedMs={value.ElapsedMs:F3}; ticks={value.Timestamp}; hwnd=0x{value.Hwnd.ToInt64():X}; msg=0x{value.Message:X}; wParam=0x{value.WParam.ToInt64():X}; lParam=0x{value.LParam.ToInt64():X}.");
            }
            Assert.Equal(0, _dropped);
        }

        private static bool Contains(NativeRect rect, NativePoint point) => point.X >= rect.Left && point.X < rect.Right
            && point.Y >= rect.Top && point.Y < rect.Bottom;
        private static NativeRect VirtualScreen()
        {
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
            Assert.True(width > 1 && height > 1);
            return new NativeRect { Left = left, Top = top, Right = checked(left + width), Bottom = checked(top + height) };
        }
        private static Input MouseEvent(uint flags, int x = 0, int y = 0) => new()
        {
            Type = 0, Data = new InputUnion { Mouse = new MouseInput { X = x, Y = y, Flags = flags } },
        };
        private readonly record struct NativeClickMessage(long Timestamp, double ElapsedMs,
            IntPtr Hwnd, int Message, IntPtr WParam, IntPtr LParam);
    }

    private void Run(string kind, bool onScreenOwner = false, bool focusByNativeClick = false, bool observePopupFocus = false) => StaTestHost.Run(() =>
    {
        bool setTsfFocus = Environment.GetEnvironmentVariable("MYCAPTURE_IME_TSF_SET_FOCUS") switch
        {
            null => false,
            "1" => true,
            _ => throw new InvalidOperationException("MYCAPTURE_IME_TSF_SET_FOCUS must be unset (D0) or 1 (D1)."),
        };
        if (focusByNativeClick)
        {
            Assert.True(onScreenOwner); Assert.Equal("plain-popup", kind); Assert.False(setTsfFocus);
            Assert.Null(Environment.GetEnvironmentVariable("MYCAPTURE_IME_NATIVE_FOCUS_OBSERVE"));
        }
        if (observePopupFocus)
        {
            Assert.True(onScreenOwner && focusByNativeClick);
            Assert.Equal("plain-popup", kind); Assert.False(setTsfFocus);
        }
        var clock = Stopwatch.StartNew();
        void Trace(string message) => output.WriteLine($"{clock.Elapsed.TotalMilliseconds:F3} ms [{kind}]: {message}");
        AssertNeutralModifiers();
        IntPtr previousLayout = GetKeyboardLayout(0);
        IntPtr[] loadedBefore = LoadedLayouts();
        IntPtr korean = IntPtr.Zero;
        Window? window = null;
        TextBox? box = null;
        Popup? popup = null;
        UIElement? placement = null;
        EditorWindowHost? product = null;
        NativePopupClick? nativeClick = null;
        PopupFocusSamples? focusSamples = null;
        bool captured = false;
        bool bodyPassed = false;
        InputMethodState previousState = InputMethodState.DoNotCare;
        ImeConversionModeValues previousMode = ImeConversionModeValues.DoNotCare;
        Exception? bodyFailure = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            Trace($"Runtime={Environment.Version}; PresentationFramework={typeof(TextBox).Assembly.GetName().Version}; MVID={typeof(TextBox).Module.ModuleVersionId}.");
            korean = LoadKeyboardLayout("00000412", 1);
            Assert.True(korean != IntPtr.Zero && (korean.ToInt64() & 0xFFFF) == 0x0412);
            bool rendered = false;
            if (kind == "product-color")
            {
                product = EditorWindowHost.Create(beforeShow: candidate =>
                    candidate.ContentRendered += (_, _) => { rendered = true; Trace("ContentRendered"); });
                window = product.Window;
            }
            else
            {
                box = new TextBox { Text = "#EF4444", Style = null, MinWidth = 260, MinHeight = 32, FontSize = 16 };
                InputMethod.SetIsInputMethodEnabled(box, true);
                var anchor = new Button { Content = "Synthetic input diagnostic", MinWidth = 260, MinHeight = 32 };
                var content = new StackPanel();
                content.Children.Add(anchor);
                if (kind == "plain-textbox") content.Children.Add(box);
                else
                {
                    placement = anchor;
                    popup = new Popup
                    {
                        PlacementTarget = anchor, Placement = PlacementMode.Bottom,
                        StaysOpen = false, AllowsTransparency = true,
                        Child = new Border { Child = box },
                    };
                    content.Children.Add(popup);
                }
                double ownerLeft = -4000, ownerTop = -4000;
                if (onScreenOwner)
                {
                    Assert.Equal("plain-popup", kind);
                    Rect workArea = SystemParameters.WorkArea;
                    Assert.True(workArea.Width >= 400 && workArea.Height >= 160,
                        "The primary work area cannot contain the unchanged 400x160 owner; no key was sent.");
                    ownerLeft = workArea.Left + (workArea.Width - 400) / 2;
                    ownerTop = workArea.Top + (workArea.Height - 160) / 2;
                    Trace($"A_OWNER_PLACEMENT: WorkArea={workArea}; Left={ownerLeft}; Top={ownerTop}; only owner position differs from D0.");
                }
                window = new Window
                {
                    Content = content, WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = ownerLeft, Top = ownerTop, Width = 400, Height = 160,
                    ShowActivated = false, ShowInTaskbar = false,
                };
                if (observePopupFocus) focusSamples = new PopupFocusSamples(box, Trace);
                window.ContentRendered += (_, _) => { rendered = true; Trace("ContentRendered"); };
                window.Show();
                window.UpdateLayout();
            }
            Pump(window, () => rendered);
            Assert.True(rendered, "ContentRendered did not complete. No key was sent.");
            TracePlacement(window, popup, "after-content-rendered", Trace);
            if (box is not null) Snapshot(box, "after-content-rendered", Trace);
            IntPtr owner = new WindowInteropHelper(window).Handle;
            window.Activate();
            SetForegroundWindow(owner);
            Pump(window, () => GetForegroundWindow() == owner);
            Assert.Equal(owner, GetForegroundWindow());
            if (box is not null) Snapshot(box, "after-foreground-pump", Trace);
            Assert.NotEqual(IntPtr.Zero, ActivateKeyboardLayout(korean, 0));
            Assert.Equal(korean, GetKeyboardLayout(0));
            if (product is not null)
            {
                box = ColorInput(product.Editor);
                RequireInlineProductHost(window, product.Editor, box);
                popup = null;
                placement = product.Editor.CustomColorButtonForTest;
            }
            else if (popup is not null)
            {
                popup.IsOpen = true;
                window.UpdateLayout();
            }
            TextBox target = Assert.IsType<TextBox>(box);
            // ColorInput opens the actual product host; each path is now prepared.
            TracePlacement(window, popup, "after-target-preparation", Trace);
            Snapshot(target, "after-target-preparation", Trace);
            Observe(target, Trace);
            if (focusByNativeClick)
            {
                nativeClick = new NativePopupClick(clock, Trace);
                Action? armObservation = focusSamples is null ? null : focusSamples.ArmClick;
                Action? afterDispatch = focusSamples is null ? null : focusSamples.AfterDispatch;
                nativeClick.ClickOnce(window, target, Assert.IsType<Popup>(popup), Assert.IsAssignableFrom<UIElement>(placement), armObservation);
                Pump(window, () => target.IsKeyboardFocused, afterDispatch: afterDispatch);
                nativeClick.RequireMouseUpProcessed(window, target, afterDispatch);
                focusSamples?.RequireFirstTwo();
            }
            else
            {
                Assert.True(target.Focus());
                Pump(window, () => target.IsKeyboardFocused);
            }
            AssertOwnedFocus(window, target, popup, placement, Trace, allowOwnedPopupNativeFocus: focusByNativeClick);
            Snapshot(target, "prepared-focus-before-additional-idle", Trace);

            // v10.0.11 TextEditor.OnTextViewUpdated queues InitTextStore at Background:
            // https://github.com/dotnet/wpf/blob/v10.0.11/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Documents/TextEditor.cs#L1392
            // Earlier ContentRendered/foreground/helper Pumps may already have initialized TSF.
            // This is an additional idle barrier, not a claim of observing pre-initialization.
            // One bounded idle barrier observes that initialization; no internal mutation,
            // focus reset, extra key, forced completion or retry is performed.
            DrainInitialization(window);
            Snapshot(target, "after-context-idle", Trace);
            AssertOwnedFocus(window, target, popup, placement, Trace, allowOwnedPopupNativeFocus: focusByNativeClick);
            previousState = InputMethod.Current.ImeState;
            previousMode = InputMethod.Current.ImeConversionMode;
            captured = true;
            Trace($"Captured IME state={previousState}, mode={previousMode}.");
            if (setTsfFocus)
            {
                // D1 changes one public TSF focus operation after the same initialization
                // barrier. Native/managed focus, key count and the Text deadline are unchanged.
                AssertOwnedFocus(window, target, popup, placement, Trace, allowOwnedPopupNativeFocus: focusByNativeClick);
                AssertNeutralModifiers();
                TsfFocusObservation beforeFocus = Snapshot(target, "before-d1-public-set-focus", Trace);
                Assert.True(beforeFocus.Supported && beforeFocus.SameThreadManager,
                    "D1 requires supported D0 observations of the existing target and identical thread managers; no key was sent.");
                SetExistingTsfDocumentFocusOnce(target, Trace);
                TsfFocusObservation afterFocus = Snapshot(target, "after-d1-public-set-focus", Trace);
                Assert.True(afterFocus.Supported && afterFocus.SameThreadManager && afterFocus.TargetFocused,
                    "D1 SetFocus did not leave both existing managers focused on the target document; no key was sent.");
                AssertOwnedFocus(window, target, popup, placement, Trace, allowOwnedPopupNativeFocus: focusByNativeClick);
            }
            Snapshot(target, "before-ime-state-on", Trace);
            InputMethod.Current.ImeState = InputMethodState.On;
            Snapshot(target, "after-ime-state-on", Trace);
            Snapshot(target, "before-ime-mode-native", Trace);
            InputMethod.Current.ImeConversionMode = ImeConversionModeValues.Native;
            Snapshot(target, "after-ime-mode-native", Trace);
            Pump(window, () => InputMethod.Current.ImeState == InputMethodState.On
                && InputMethod.Current.ImeConversionMode.HasFlag(ImeConversionModeValues.Native));
            Assert.Equal(InputMethodState.On, InputMethod.Current.ImeState);
            Assert.True(InputMethod.Current.ImeConversionMode.HasFlag(ImeConversionModeValues.Native));
            target.SelectAll();
            if (focusSamples is not null) focusSamples.BeforeKey();
            else Snapshot(target, "before-key", Trace);
            TypeT(window, target, popup, placement, korean, Trace, allowOwnedPopupNativeFocus: focusByNativeClick);
            Snapshot(target, "after-key-dispatch-before-body-wait", Trace);
            Pump(window, () => target.Text.Contains('ㅅ'));
            Snapshot(target, "after-key", Trace);
            Trace($"Body result text={CodePoints(target.Text)}; productTool={(product?.Editor is { } editor ? Controller(editor).Tool.ToString() : "not-applicable")}.");
            Assert.Contains("ㅅ", target.Text);
            bodyPassed = true;
            Trace("BODY_PASS: real input changed Text before restoration.");
        }
        catch (Exception error)
        {
            bodyFailure = error;
            Trace($"BODY_FAIL: {error.GetType().Name}: {error.Message}");
            Trace("BODY_EXCEPTION " + JsonSerializer.Serialize(new { type = error.GetType().FullName, detail = error.ToString() }));
            throw;
        }
        finally
        {
            try
            {
                try
                {
                    ObserveCleanup("ime-restore", () =>
                    {
                        if (captured && window is not null && box is not null)
                        {
                            // Same restoration boundary as EditorRealImeTests: never reclaim a
                            // foreground window from another app, and never bypass ownership guards.
                            Assert.Equal(new WindowInteropHelper(window).Handle, GetForegroundWindow());
                            if (product is not null) RequireInlineProductHost(window, product.Editor, box);
                            if (popup is not null) Assert.True(popup.IsOpen);
                            Assert.True(box.Focus());
                            Pump(window, () => box.IsKeyboardFocused);
                            AssertOwnedFocus(window, box, popup, placement, Trace, allowOwnedPopupNativeFocus: focusByNativeClick);
                            if (previousMode != ImeConversionModeValues.DoNotCare)
                            {
                                Snapshot(box, "before-restore-ime-mode", Trace);
                                InputMethod.Current.ImeConversionMode = previousMode;
                                Snapshot(box, "after-restore-ime-mode", Trace);
                            }
                            if (previousState != InputMethodState.DoNotCare)
                            {
                                Snapshot(box, "before-restore-ime-state", Trace);
                                InputMethod.Current.ImeState = previousState;
                                Snapshot(box, "after-restore-ime-state", Trace);
                            }
                            Pump(window, () => (previousMode == ImeConversionModeValues.DoNotCare || InputMethod.Current.ImeConversionMode == previousMode)
                                && (previousState == InputMethodState.DoNotCare || InputMethod.Current.ImeState == previousState));
                            Trace($"Restored IME state={InputMethod.Current.ImeState}, mode={InputMethod.Current.ImeConversionMode}; expected={previousState}/{previousMode}; bodyPassed={bodyPassed}.");
                            if (previousMode != ImeConversionModeValues.DoNotCare) Assert.Equal(previousMode, InputMethod.Current.ImeConversionMode);
                            if (previousState != InputMethodState.DoNotCare) Assert.Equal(previousState, InputMethod.Current.ImeState);
                        }
                    }, cleanupFailures, Trace);
                }
                finally
                {
                    try
                    {
                        ObserveCleanup("native-mouse-restore", () =>
                        {
                            if (nativeClick is not null && window is not null) nativeClick.Restore(window);
                        }, cleanupFailures, Trace);
                    }
                    finally
                    {
                        try
                        {
                            ObserveCleanup("layout-restore", () =>
                            {
                                Assert.NotEqual(IntPtr.Zero, ActivateKeyboardLayout(previousLayout, 0));
                                Trace($"Restored HKL=0x{GetKeyboardLayout(0).ToInt64():X}; expected=0x{previousLayout.ToInt64():X}.");
                                Assert.Equal(previousLayout, GetKeyboardLayout(0));
                            }, cleanupFailures, Trace);
                        }
                        finally
                        {
                            try
                            {
                                ObserveCleanup("layout-unload", () =>
                                {
                                    if (korean != IntPtr.Zero && !loadedBefore.Contains(korean))
                                    {
                                        Assert.True(UnloadKeyboardLayout(korean));
                                        Assert.DoesNotContain(korean, LoadedLayouts());
                                    }
                                }, cleanupFailures, Trace);
                            }
                            finally
                            {
                                try { ObserveCleanup("popup-close", () => { if (product is not null && product.Editor.IsCustomColorFlyoutOpenForTest) product.Editor.HandleShortcut(Key.Escape, ModifierKeys.None); if (popup is not null) popup.IsOpen = false; }, cleanupFailures, Trace); }
                                finally
                                {
                                    try
                                    {
                                        if (focusSamples is not null)
                                            ObserveCleanup("focus-observer-remove", focusSamples.Dispose, cleanupFailures, Trace);
                                    }
                                    finally { ObserveCleanup("owner-close", () => { if (product is not null) product.Dispose(); else window?.Close(); }, cleanupFailures, Trace); }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception cleanupError)
            {
                // Original nested cleanup still runs and fails. Retain errors that a later
                // finally would otherwise replace, including the first body exception.
                if (!cleanupFailures.Contains(cleanupError)) cleanupFailures.Add(cleanupError);
                if (bodyFailure is null && cleanupFailures.Count == 1) throw;
                var failures = new List<Exception>();
                if (bodyFailure is not null) failures.Add(bodyFailure);
                failures.AddRange(cleanupFailures);
                throw new AggregateException("IME diagnostic body/cleanup failures are preserved in execution order.", failures);
            }
        }
        Trace("CASE_PASS: body and IME/layout restoration completed.");
    });

    [NativePopupFocusObservationFact]
    public void PlainPopupObservesNativeSetFocusWithoutInput() => ObserveNativePopupFocus("plain-popup");

    [InlineProductHostObservationFact]
    public void ProductColorInlineHostUsesOwnerHwndWithoutNativeSetFocus() => StaTestHost.Run(() =>
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("MYCAPTURE_IME_INLINE_HOST_OBSERVE"));
        Assert.Equal("1", Environment.GetEnvironmentVariable("MYCAPTURE_REAL_IME_TEST"));
        Assert.Null(Environment.GetEnvironmentVariable("MYCAPTURE_IME_NATIVE_FOCUS_OBSERVE"));
        Assert.Null(Environment.GetEnvironmentVariable("MYCAPTURE_IME_TSF_SET_FOCUS"));
        AssertNeutralModifiers(); AssertNeutralMouseButtons();
        using EditorWindowHost product = EditorWindowHost.Create();
        var controller = Controller(product.Editor);
        var color = controller.StrokeColor; var tool = controller.Tool; int undo = controller.Undo.UndoCount;
        TextBox input = ColorInput(product.Editor);
        RequireInlineProductHost(product.Window, product.Editor, input);
        Assert.Same(input, Keyboard.FocusedElement);
        Assert.True(input.IsKeyboardFocused);
        Assert.Null(Mouse.Captured);
        Assert.Equal(IntPtr.Zero, GetCapture());
        Assert.Equal(color, controller.StrokeColor); Assert.Equal(tool, controller.Tool); Assert.Equal(undo, controller.Undo.UndoCount);
        Assert.True(product.Editor.HandleShortcut(Key.Escape, ModifierKeys.None));
        Assert.False(product.Editor.IsCustomColorFlyoutOpenForTest);
        Assert.Equal(color, controller.StrokeColor); Assert.Equal(tool, controller.Tool); Assert.Equal(undo, controller.Undo.UndoCount);
        output.WriteLine("INLINE_HOST_STRUCTURAL_PASS: same HWND, no new Popup; nativeSetFocusCalls=0; keysSent=0; IME composition=NOT_EVALUATED; historical product D2 is a separate frozen experiment.");
    });

    // D2 is observation-only. D0/D1 and their functional assertions are unchanged.
    // The sole experiment operation is one native SetFocus on the exact owned Popup.
    private void ObserveNativePopupFocus(string kind) => StaTestHost.Run(() =>
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("MYCAPTURE_IME_NATIVE_FOCUS_OBSERVE"));
        Assert.Null(Environment.GetEnvironmentVariable("MYCAPTURE_IME_TSF_SET_FOCUS"));
        Assert.Equal("plain-popup", kind);
        var clock = Stopwatch.StartNew();
        void Trace(string message) => output.WriteLine($"{clock.Elapsed.TotalMilliseconds:F3} ms [d2-{kind}]: {message}");
        AssertNeutralModifiers();
        IntPtr previousLayout = GetKeyboardLayout(0);
        IntPtr[] loadedBefore = LoadedLayouts();
        IntPtr korean = IntPtr.Zero;
        IntPtr popupHandle = IntPtr.Zero;
        Window? window = null;
        TextBox? box = null;
        Popup? popup = null;
        UIElement? placement = null;
        bool captured = false;
        bool observationCompleted = false;
        bool ownerClosed = false;
        int nativeSetFocusCalls = 0;
        InputMethodState previousState = InputMethodState.DoNotCare;
        ImeConversionModeValues previousMode = ImeConversionModeValues.DoNotCare;
        Exception? bodyFailure = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            Trace($"Runtime={Environment.Version}; PresentationFramework={typeof(TextBox).Assembly.GetName().Version}; MVID={typeof(TextBox).Module.ModuleVersionId}; keysSent=0; functionalResult=NOT_EVALUATED.");
            korean = LoadKeyboardLayout("00000412", 1);
            Assert.True(korean != IntPtr.Zero && (korean.ToInt64() & 0xFFFF) == 0x0412);
            bool rendered = false;
            {
                box = new TextBox { Text = "#EF4444", Style = null, MinWidth = 260, MinHeight = 32, FontSize = 16 };
                InputMethod.SetIsInputMethodEnabled(box, true);
                var anchor = new Button { Content = "Synthetic input diagnostic", MinWidth = 260, MinHeight = 32 };
                var content = new StackPanel();
                content.Children.Add(anchor);
                placement = anchor;
                popup = new Popup
                {
                    PlacementTarget = anchor, Placement = PlacementMode.Bottom,
                    StaysOpen = false, AllowsTransparency = true,
                    Child = new Border { Child = box },
                };
                content.Children.Add(popup);
                window = new Window
                {
                    Content = content, WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000, Top = -4000, Width = 400, Height = 160,
                    ShowActivated = false, ShowInTaskbar = false,
                };
                window.ContentRendered += (_, _) => { rendered = true; Trace("ContentRendered"); };
                window.Show();
                window.UpdateLayout();
            }
            window.Closed += (_, _) => ownerClosed = true;
            Pump(window, () => rendered);
            Assert.True(rendered, "D2 ContentRendered did not complete; keysSent=0.");
            TracePlacement(window, popup, "after-content-rendered", Trace);
            if (box is not null) Snapshot(box, "after-content-rendered", Trace);
            IntPtr owner = new WindowInteropHelper(window).Handle;
            // Same one-time foreground preparation as D0, before the experiment.
            window.Activate();
            SetForegroundWindow(owner);
            Pump(window, () => GetForegroundWindow() == owner);
            Assert.Equal(owner, GetForegroundWindow());
            if (box is not null) Snapshot(box, "after-foreground-pump", Trace);
            Assert.NotEqual(IntPtr.Zero, ActivateKeyboardLayout(korean, 0));
            Assert.Equal(korean, GetKeyboardLayout(0));
            {
                Assert.NotNull(popup);
                popup.IsOpen = true;
                window.UpdateLayout();
            }
            TextBox target = Assert.IsType<TextBox>(box);
            Assert.NotNull(popup);
            TracePlacement(window, popup, "after-target-preparation", Trace);
            Snapshot(target, "after-target-preparation", Trace);
            Observe(target, Trace);
            Assert.True(target.Focus());
            Pump(window, () => target.IsKeyboardFocused);
            AssertOwnedFocus(window, target, popup, placement, Trace);
            Snapshot(target, "prepared-focus-before-additional-idle", Trace);
            DrainInitialization(window);
            TsfFocusObservation prepared = Snapshot(target, "after-context-idle", Trace);
            AssertOwnedFocus(window, target, popup, placement, Trace);
            Assert.True(prepared.Supported && prepared.SameThreadManager,
                "D2 requires supported observations of the existing document and managers; keysSent=0.");
            previousState = InputMethod.Current.ImeState;
            previousMode = InputMethod.Current.ImeConversionMode;
            captured = true;
            Trace($"Captured IME state={previousState}, mode={previousMode}; D2 body has no IME setter.");
            popupHandle = Assert.IsType<HwndSource>(PresentationSource.FromVisual(target)).Handle;
            AssertNeutralModifiers();
            Assert.Equal(korean, GetKeyboardLayout(0));
            AssertOwnedFocus(window, target, popup, placement, Trace);
            Assert.True(TraceNativeFocusD2(window, target, popupHandle, "before-native-set-focus", Trace));
            Snapshot(target, "before-native-set-focus", Trace);

            Marshal.SetLastPInvokeError(0);
            nativeSetFocusCalls++;
            IntPtr previousNative = SetNativeFocusD2(popupHandle);
            int nativeError = Marshal.GetLastWin32Error();
            Trace("D2_NATIVE_RETURN " + JsonSerializer.Serialize(new
            {
                invocation = nativeSetFocusCalls, previousHwnd = $"0x{previousNative.ToInt64():X}",
                previousWasOwner = previousNative == owner,
                win32Error = previousNative == IntPtr.Zero ? (int?)nativeError : null,
                hResult = (int?)null, hResultStatus = "NOT_APPLICABLE_WIN32_HWND_RETURN", keysSent = 0,
            }));
            // Record all post-call observations before testing their completeness/safety.
            // Native owner->Popup and managed focus changes are observations, not content success.
            bool remainedOwned = TraceNativeFocusD2(window, target, popupHandle, "after-native-set-focus", Trace);
            TracePlacement(window, popup, "after-native-set-focus", Trace);
            TsfFocusObservation after = Snapshot(target, "after-native-set-focus", Trace);
            Assert.Equal(1, nativeSetFocusCalls);
            Assert.True(remainedOwned, "D2 focus left the exact owned owner/Popup scope; keysSent=0.");
            Assert.Equal(owner, previousNative);
            Assert.True(after.Supported && after.SameThreadManager,
                "D2 post-call TSF observation is incomplete; keysSent=0.");
            observationCompleted = true;
            Trace("D2_OBSERVED: functionalResult=NOT_EVALUATED; keysSent=0; no content assertion or functional PASS.");
        }
        catch (Exception error)
        {
            bodyFailure = error;
            Trace($"D2_OBSERVATION_FAIL: {error.GetType().FullName}; HRESULT=0x{error.HResult:X8}; {error.Message}; keysSent=0.");
            Trace("BODY_EXCEPTION " + JsonSerializer.Serialize(new { type = error.GetType().FullName, detail = error.ToString() }));
            throw;
        }
        finally
        {
            try
            {
                try
                {
                    ObserveCleanup("ime-restore", () =>
                    {
                        if (captured && window is not null && box is not null)
                        {
                            // No native/managed refocus during cleanup. Restore only while the exact
                            // target is still focused inside the owned pair; otherwise preserve the
                            // inability to restore IME settings as a failure and continue nested cleanup.
                            bool safe = TraceNativeFocusD2(window, box, popupHandle, "before-restore-ime", Trace);
                            Assert.True(safe && popup is { IsOpen: true } && box.IsKeyboardFocused
                                && ReferenceEquals(box, Keyboard.FocusedElement),
                                "D2 IME restoration unavailable without refocusing; no focus was reclaimed; keysSent=0.");
                            if (previousMode != ImeConversionModeValues.DoNotCare)
                            {
                                Snapshot(box, "before-restore-ime-mode", Trace);
                                InputMethod.Current.ImeConversionMode = previousMode;
                                Snapshot(box, "after-restore-ime-mode", Trace);
                            }
                            if (previousState != InputMethodState.DoNotCare)
                            {
                                Assert.True(TraceNativeFocusD2(window, box, popupHandle, "before-restore-ime-state", Trace));
                                Assert.Same(box, Keyboard.FocusedElement);
                                Snapshot(box, "before-restore-ime-state", Trace);
                                InputMethod.Current.ImeState = previousState;
                                Snapshot(box, "after-restore-ime-state", Trace);
                            }
                            Pump(window, () => (previousMode == ImeConversionModeValues.DoNotCare || InputMethod.Current.ImeConversionMode == previousMode)
                                && (previousState == InputMethodState.DoNotCare || InputMethod.Current.ImeState == previousState));
                            Trace($"Restored IME state={InputMethod.Current.ImeState}, mode={InputMethod.Current.ImeConversionMode}; expected={previousState}/{previousMode}; observationCompleted={observationCompleted}; keysSent=0.");
                            if (previousMode != ImeConversionModeValues.DoNotCare) Assert.Equal(previousMode, InputMethod.Current.ImeConversionMode);
                            if (previousState != InputMethodState.DoNotCare) Assert.Equal(previousState, InputMethod.Current.ImeState);
                        }
                    }, cleanupFailures, Trace);
                }
                catch (Exception error)
                {
                    Trace($"D2_IME_RESTORE_FAIL: {error.GetType().FullName}; HRESULT=0x{error.HResult:X8}; keysSent=0.");
                    throw;
                }
                finally
                {
                    try
                    {
                        ObserveCleanup("layout-restore", () =>
                        {
                            Assert.NotEqual(IntPtr.Zero, ActivateKeyboardLayout(previousLayout, 0));
                            Trace($"Restored HKL=0x{GetKeyboardLayout(0).ToInt64():X}; expected=0x{previousLayout.ToInt64():X}; keysSent=0.");
                            Assert.Equal(previousLayout, GetKeyboardLayout(0));
                        }, cleanupFailures, Trace);
                    }
                    finally
                    {
                        try
                        {
                            ObserveCleanup("layout-unload", () =>
                            {
                                if (korean != IntPtr.Zero && !loadedBefore.Contains(korean))
                                {
                                    Assert.True(UnloadKeyboardLayout(korean));
                                    Assert.DoesNotContain(korean, LoadedLayouts());
                                }
                            }, cleanupFailures, Trace);
                        }
                        finally
                        {
                            try { ObserveCleanup("popup-close", () => { if (popup is not null) popup.IsOpen = false; }, cleanupFailures, Trace); }
                            finally
                            {
                                ObserveCleanup("owner-close", () =>
                                {
                                    window?.Close();
                                    Trace($"D2 cleanup: ownerClosed={ownerClosed}; popupOpen={popup?.IsOpen}; no explicit native focus restoration was attempted.");
                                    if (window is not null) Assert.True(ownerClosed, "D2 owned window did not report Closed.");
                                    if (popup is not null) Assert.False(popup.IsOpen);
                                }, cleanupFailures, Trace);
                            }
                        }
                    }
                }
            }
            catch (Exception cleanupError)
            {
                // Original nested cleanup still runs and fails. Retain errors that a later
                // finally would otherwise replace, including the first body exception.
                if (!cleanupFailures.Contains(cleanupError)) cleanupFailures.Add(cleanupError);
                if (bodyFailure is null && cleanupFailures.Count == 1) throw;
                var failures = new List<Exception>();
                if (bodyFailure is not null) failures.Add(bodyFailure);
                failures.AddRange(cleanupFailures);
                throw new AggregateException("IME diagnostic body/cleanup failures are preserved in execution order.", failures);
            }
        }
        Trace($"D2_OBSERVATION_COMPLETE: nativeSetFocusCalls={nativeSetFocusCalls}; keysSent=0; functionalResult=NOT_EVALUATED; IME/HKL restoration completed.");
    });

    private static void RequireInlineProductHost(Window window, AnnotationEditorControl editor, TextBox input)
    {
        FrameworkElement host = editor.CustomColorFlyoutContentForTest;
        Assert.True(editor.IsCustomColorFlyoutOpenForTest && host.IsVisible && host.IsEnabled);
        Assert.Same(editor.CustomColorInputForTest, input);
        Assert.True(editor.IsAncestorOf(host) && host.IsAncestorOf(input));
        HwndSource source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(window));
        Assert.False(source.IsDisposed);
        Assert.NotEqual(IntPtr.Zero, source.Handle);
        Assert.Same(source, PresentationSource.FromVisual(host));
        Assert.Same(source, PresentationSource.FromVisual(input));
        Assert.Same(source, PresentationSource.FromVisual(editor.CustomColorButtonForTest));
        Assert.Equal(new WindowInteropHelper(window).Handle, source.Handle);
    }

    private static void ObserveCleanup(string stage, Action cleanup, List<Exception> failures, Action<string> trace)
    {
        try { cleanup(); }
        catch (Exception error)
        {
            failures.Add(error);
            trace("CLEANUP_EXCEPTION " + JsonSerializer.Serialize(new
            {
                stage, type = error.GetType().FullName, detail = error.ToString(),
            }));
            throw;
        }
    }

    private static bool TraceNativeFocusD2(Window window, TextBox box, IntPtr popupHandle,
        string stage, Action<string> trace)
    {
        IntPtr owner = new WindowInteropHelper(window).Handle;
        uint thread = GetCurrentThreadId();
        uint process = (uint)Environment.ProcessId;
        IntPtr foreground = GetForegroundWindow();
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        bool infoAvailable = GetGUIThreadInfo(thread, ref info);
        bool InOwnedPair(IntPtr hwnd) => hwnd != IntPtr.Zero && (hwnd == owner || hwnd == popupHandle)
            && GetWindowThreadProcessId(hwnd, out uint pid) == thread && pid == process;
        object? managed = Keyboard.FocusedElement;
        HwndSource? managedSource = managed is System.Windows.Media.Visual visual
            ? PresentationSource.FromVisual(visual) as HwndSource : null;
        IntPtr managedHwnd = managedSource?.Handle ?? IntPtr.Zero;
        bool managedOwned = managed is null || managedSource is { IsDisposed: false } && InOwnedPair(managedHwnd);
        bool withinOwned = infoAvailable && InOwnedPair(owner) && InOwnedPair(popupHandle)
            && GetWindow(popupHandle, 4) == owner && InOwnedPair(foreground)
            && InOwnedPair(info.Focus) && InOwnedPair(info.Active) && managedOwned;
        string Kind(IntPtr hwnd) => hwnd == IntPtr.Zero ? "null" : hwnd == owner ? "owner"
            : hwnd == popupHandle ? "popup" : "outside-exact-owned-pair";
        trace("D2_NATIVE_FOCUS " + JsonSerializer.Serialize(new
        {
            stage, thread, process, infoAvailable, withinOwned,
            foreground = Kind(foreground), foregroundHwnd = $"0x{foreground.ToInt64():X}",
            nativeFocus = Kind(info.Focus), nativeFocusHwnd = $"0x{info.Focus.ToInt64():X}",
            active = Kind(info.Active), activeHwnd = $"0x{info.Active.ToInt64():X}",
            managedIsTarget = ReferenceEquals(managed, box), managedType = managed?.GetType().FullName,
            managedSource = Kind(managedHwnd), managedOwned, box.IsKeyboardFocused,
            keysSent = 0, functionalResult = "NOT_EVALUATED",
        }));
        return withinOwned;
    }

    [DllImport("user32.dll", EntryPoint = "SetFocus", SetLastError = true)]
    private static extern IntPtr SetNativeFocusD2(IntPtr window);

    private static TsfFocusObservation Snapshot(TextBox box, string stage, Action<string> trace)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var unsupported = new List<string>();
        Type? editorType = typeof(TextBoxBase).Assembly.GetType("System.Windows.Documents.TextEditor");
        if (editorType is null) unsupported.Add("missing:System.Windows.Documents.TextEditor");
        object? Read(Type? type, object? instance, string name, bool property = false)
        {
            if (type is null) return null;
            MemberInfo? member = property ? type.GetProperty(name, flags) : type.GetField(name, flags);
            if (member is null) { unsupported.Add($"missing:{type.FullName}.{name}"); return null; }
            try
            {
                return member is PropertyInfo p ? p.GetValue(instance) : ((FieldInfo)member).GetValue(instance);
            }
            catch (Exception error) when (error is TargetInvocationException or MemberAccessException or ArgumentException or TargetException)
            { unsupported.Add($"unreadable:{type.FullName}.{name}:{error.GetType().Name}"); return null; }
        }
        MethodInfo? getEditor = editorType?.GetMethod("_GetTextEditor", flags);
        if (editorType is not null && getEditor is null) unsupported.Add("missing:System.Windows.Documents.TextEditor._GetTextEditor");
        object? editor = null;
        try { editor = getEditor?.Invoke(null, [box]); }
        catch (Exception error) when (error is TargetInvocationException or MemberAccessException or ArgumentException or TargetException)
        { unsupported.Add($"unreadable:TextEditor._GetTextEditor:{error.GetType().Name}"); }
        object? Field(string name) => editor is null ? null : Read(editorType, editor, name);
        object? store = Field("_textstore");
        object? view = Field("_textView");
        object? document = Read(store?.GetType(), store, "DocumentManager", property: true);
        // Read the existing thread-local slot directly; do not call TextServicesHost.Current,
        // TextServicesLoader.Load or any registration/focus/completion method for diagnostics.
        object? slotValue = Read(editorType, null, "_threadLocalStoreSlot");
        if (slotValue is not null && slotValue is not LocalDataStoreSlot) unsupported.Add("unsupported:TextEditor._threadLocalStoreSlot type");
        object? local = slotValue is LocalDataStoreSlot slot ? Thread.GetData(slot) : null;
        object? host = Read(local?.GetType(), local, "TextServicesHost", property: true);
        object? count = Read(host?.GetType(), host, "_registeredtextstorecount");
        object? sink = Read(store?.GetType(), store, "_sink");
        object? viewValid = Read(view?.GetType(), view, "IsValid", property: true);
        object? initStarted = Field("_textStoreInitStarted"), initPending = Field("_pendingTextStoreInit");
        trace("TEXTSTORE " + JsonSerializer.Serialize(new
        {
            stage, editor = editor?.GetType().FullName, unsupported,
            diagnosticSupported = unsupported.Count == 0, initStarted, initPending,
            store = store?.GetType().FullName, documentPresent = document is not null,
            sinkPresent = sink is not null,
            registeredCount = count, view = view?.GetType().FullName,
            viewValid,
            box.IsLoaded, box.IsVisible, box.IsEnabled, box.IsReadOnly, box.IsKeyboardFocused,
            imeEnabled = InputMethod.GetIsInputMethodEnabled(box), box.MaxLength,
            box.ActualWidth, box.ActualHeight,
            contentHostPresent = box.Template?.FindName("PART_ContentHost", box) is not null,
            text = CodePoints(box.Text),
        }));
        return TraceTsfFocus(box, stage, host, document, trace);
    }

    // D0 observes existing objects only. In v10.0.11 Dispatcher.InputMethod is a
    // plain slot getter; InputMethod.Current/DefaultTextStore.Current and the
    // TextServicesContext.ThreadManager getter can create objects and are not used.
    private static TsfFocusObservation TraceTsfFocus(TextBox box, string stage, object? host, object? targetDocument,
        Action<string> trace)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var unsupported = new List<string>();
        var identities = new List<IntPtr>();
        var queries = new List<object>();
        object? inputMethod = null, context = null, defaultStore = null;
        object? hostManager = null, contextManager = null;
        IntPtr targetIdentity = IntPtr.Zero, defaultIdentity = IntPtr.Zero, emptyIdentity = IntPtr.Zero;
        string emptyDim = "not-observed";
        var emptyDimErrors = new List<string>();
        bool? sameThreadManager = null;
        int targetFocusCount = 0;

        void Error(string step, Exception exception)
        {
            Exception actual = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            unsupported.Add($"{step}:{actual.GetType().FullName}:0x{actual.HResult:X8}");
        }
        object? Field(object? instance, string name, out FieldInfo? field)
        {
            field = null;
            if (instance is null) { unsupported.Add($"absent-instance:{name}"); return null; }
            field = instance.GetType().GetField(name, flags);
            if (field is null) { unsupported.Add($"missing:{instance.GetType().FullName}.{name}"); return null; }
            return field.GetValue(instance);
        }
        IntPtr Identity(object? instance, string label)
        {
            if (instance is null) { unsupported.Add($"absent-instance:{label}"); return IntPtr.Zero; }
            if (!Marshal.IsComObject(instance)) { unsupported.Add($"not-com:{label}"); return IntPtr.Zero; }
            IntPtr identity = Marshal.GetIUnknownForObject(instance);
            identities.Add(identity); // One Release for every acquisition, including aliases.
            return identity;
        }
        void ObserveExistingEmptyDocument()
        {
            // Read only the already-existing context field. The EmptyDocumentManager getter
            // creates a DIM and must not be called. Absence is independent of D0 Supported.
            if (context is null) { emptyDim = "context-absent"; return; }
            try
            {
                FieldInfo? field = context.GetType().GetField("_dimEmpty", flags);
                if (field is null) { emptyDim = "missing-field"; return; }
                object? document = field.GetValue(context);
                if (document is null) { emptyDim = "absent"; return; }
                if (!Marshal.IsComObject(document)) { emptyDim = "not-com"; return; }
                emptyIdentity = Marshal.GetIUnknownForObject(document);
                identities.Add(emptyIdentity); // Borrowed RCW; balance only this AddRef below.
                emptyDim = "present";
            }
            catch (Exception error)
            {
                emptyDim = "unreadable";
                emptyDimErrors.Add($"{error.GetType().FullName}:0x{error.HResult:X8}");
            }
        }
        void Query(object? manager, FieldInfo? field, string name)
        {
            if (manager is null) { unsupported.Add($"absent-instance:{name}"); return; }
            Type? contract = field?.FieldType;
            MethodInfo? getFocus = contract?.GetMethod("GetFocus", flags);
            ParameterInfo[] parameters = getFocus?.GetParameters() ?? [];
            // Invoke WPF's existing COM interface method; no custom vtable or activation.
            if (contract?.GUID != new Guid("aa80e801-2021-11d2-93e0-0060b067b86e")
                || getFocus is null || getFocus.ReturnType != typeof(void)
                || parameters.Length != 1 || !parameters[0].IsOut || !parameters[0].ParameterType.IsByRef)
            { unsupported.Add($"unsupported-contract:{name}.GetFocus"); return; }
            object?[] arguments = [null];
            try
            {
                getFocus.Invoke(manager, arguments);
                object? focus = arguments[0];
                IntPtr focusIdentity = focus is null ? IntPtr.Zero : Identity(focus, name + ".focus");
                bool? isTarget = focus is null || targetIdentity == IntPtr.Zero || focusIdentity == IntPtr.Zero
                    ? null : focusIdentity == targetIdentity;
                bool? isDefault = focus is null || defaultIdentity == IntPtr.Zero || focusIdentity == IntPtr.Zero
                    ? null : focusIdentity == defaultIdentity;
                bool? isEmpty = focus is null || emptyIdentity == IntPtr.Zero || focusIdentity == IntPtr.Zero
                    ? null : focusIdentity == emptyIdentity;
                string classification = focus is null ? "null" : isTarget == true ? "target"
                    : isDefault == true ? "default" : isTarget == false && isDefault == false
                        ? (isEmpty == true ? "empty" : "other") : "unclassified";
                if (isTarget == true) targetFocusCount++;
                queries.Add(new { manager = name, classification, isTarget, isDefault, isEmpty });
            }
            catch (Exception error) { Error(name + ".GetFocus", error); }
            finally
            {
                // GetFocus's out interface is the one new RCW acquisition. Balance it
                // once; never release borrowed managers/documents or FinalRelease an RCW.
                if (arguments[0] is { } acquired && Marshal.IsComObject(acquired))
                {
                    try { Marshal.ReleaseComObject(acquired); }
                    catch (Exception error) { Error(name + ".GetFocus.Release", error); }
                }
            }
        }

        try
        {
            PropertyInfo? inputSlot = typeof(Dispatcher).GetProperty("InputMethod", flags);
            if (inputSlot is null) unsupported.Add("missing:Dispatcher.InputMethod");
            else inputMethod = inputSlot.GetValue(box.Dispatcher);
            context = Field(inputMethod, "_textservicesContext", out _);
            defaultStore = Field(inputMethod, "_defaulttextstore", out _);
            object? defaultDocument = Field(defaultStore, "_doc", out _);
            hostManager = Field(host, "_threadManager", out FieldInfo? hostField);
            contextManager = Field(context, "_threadManager", out FieldInfo? contextField);
            targetIdentity = Identity(targetDocument, "targetDocument");
            defaultIdentity = Identity(defaultDocument, "defaultDocument");
            IntPtr hostIdentity = Identity(hostManager, "hostManager");
            IntPtr contextIdentity = Identity(contextManager, "contextManager");
            if (hostIdentity != IntPtr.Zero && contextIdentity != IntPtr.Zero)
                sameThreadManager = hostIdentity == contextIdentity;
            ObserveExistingEmptyDocument();
            Query(hostManager, hostField, "textServicesHost");
            Query(contextManager, contextField, "textServicesContext");
        }
        catch (Exception error) { Error("observe", error); }
        finally
        {
            foreach (IntPtr identity in identities)
            {
                try { Marshal.Release(identity); }
                catch (Exception error) { Error("identity.Release", error); }
            }
        }
        trace("TSF_FOCUS " + JsonSerializer.Serialize(new
        {
            stage, supported = unsupported.Count == 0, unsupported,
            inputMethodPresent = inputMethod is not null, contextPresent = context is not null,
            defaultStorePresent = defaultStore is not null, targetDocumentPresent = targetDocument is not null,
            hostManagerPresent = hostManager is not null, contextManagerPresent = contextManager is not null,
            sameThreadManager, emptyDim, emptyDimErrors, queries,
        }));
        return new TsfFocusObservation(unsupported.Count == 0 && queries.Count == 2,
            sameThreadManager == true, targetFocusCount == 2);
    }

    private readonly record struct TsfFocusObservation(bool Supported, bool SameThreadManager, bool TargetFocused);

    // Invoke the public ITfThreadMgr::SetFocus contract through WPF's already-loaded
    // interface type. Reflection only borrows existing WPF objects; no private WPF
    // focus method, new manager/store, AssociateFocus, native SetFocus or retry is used.
    private static void SetExistingTsfDocumentFocusOnce(TextBox box, Action<string> trace)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        Assert.True(box.Dispatcher.CheckAccess());
        Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
        Assert.Same(box, Keyboard.FocusedElement);
        Assert.True(box.IsKeyboardFocused && InputMethod.GetIsInputMethodEnabled(box));
        object RequiredField(Type type, object? instance, string name) =>
            type.GetField(name, flags)?.GetValue(instance)
                ?? throw new NotSupportedException($"D1 missing/absent existing field: {type.FullName}.{name}.");
        object RequiredProperty(Type type, object instance, string name) =>
            type.GetProperty(name, flags)?.GetValue(instance)
                ?? throw new NotSupportedException($"D1 missing/absent existing property: {type.FullName}.{name}.");
        Type editorType = typeof(TextBoxBase).Assembly.GetType("System.Windows.Documents.TextEditor")
            ?? throw new NotSupportedException("D1 TextEditor type is unavailable.");
        object editor = editorType.GetMethod("_GetTextEditor", flags)?.Invoke(null, [box])
            ?? throw new NotSupportedException("D1 existing TextEditor is unavailable.");
        object store = RequiredField(editorType, editor, "_textstore");
        _ = RequiredField(store.GetType(), store, "_sink");
        object document = RequiredProperty(store.GetType(), store, "DocumentManager");
        LocalDataStoreSlot slot = RequiredField(editorType, null, "_threadLocalStoreSlot") as LocalDataStoreSlot
            ?? throw new NotSupportedException("D1 TextEditor thread-local slot contract differs.");
        object local = Thread.GetData(slot) ?? throw new NotSupportedException("D1 existing thread-local store is absent.");
        object host = RequiredProperty(local.GetType(), local, "TextServicesHost");
        FieldInfo managerField = host.GetType().GetField("_threadManager", flags)
            ?? throw new NotSupportedException("D1 existing host manager field is unavailable.");
        object manager = managerField.GetValue(host) ?? throw new NotSupportedException("D1 existing host manager is absent.");
        object input = RequiredProperty(typeof(Dispatcher), box.Dispatcher, "InputMethod");
        object context = RequiredField(input.GetType(), input, "_textservicesContext");
        object otherManager = RequiredField(context.GetType(), context, "_threadManager");
        Type contract = managerField.FieldType;
        MethodInfo? setFocus = contract.GetMethod("SetFocus", flags);
        ParameterInfo[] parameters = setFocus?.GetParameters() ?? [];
        if (contract.GUID != new Guid("aa80e801-2021-11d2-93e0-0060b067b86e")
            || setFocus is null || setFocus.ReturnType != typeof(void) || parameters.Length != 1
            || parameters[0].ParameterType.IsByRef || parameters[0].IsOut
            || parameters[0].ParameterType.GUID != new Guid("aa80e7f4-2021-11d2-93e0-0060b067b86e")
            || !parameters[0].ParameterType.IsInstanceOfType(document))
            throw new NotSupportedException("D1 ITfThreadMgr.SetFocus/ITfDocumentMgr contract differs.");
        var identities = new List<IntPtr>();
        IntPtr Identity(object value)
        {
            if (!Marshal.IsComObject(value)) throw new NotSupportedException("D1 existing object is not a COM object.");
            IntPtr identity = Marshal.GetIUnknownForObject(value);
            identities.Add(identity);
            return identity;
        }
        try
        {
            Assert.Equal(Identity(manager), Identity(otherManager));
            Assert.NotEqual(IntPtr.Zero, Identity(document));
            trace("D1_SET_FOCUS begin: existing target, existing identical managers, public COM contract, invocation=1.");
            setFocus.Invoke(manager, [document]);
            trace("D1_SET_FOCUS returned: invocation=1; immediate D0 target identity check follows.");
        }
        catch (Exception error)
        {
            Exception actual = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
            trace($"D1_SET_FOCUS failed: {actual.GetType().FullName}; HRESULT=0x{actual.HResult:X8}.");
            throw;
        }
        finally
        {
            // Balance only identities acquired here. These managers/document are borrowed;
            // never ReleaseComObject or FinalReleaseComObject on the borrowed instances.
            foreach (IntPtr identity in identities) Marshal.Release(identity);
        }
    }

    private static void TracePlacement(Window window, Popup? popup, string stage, Action<string> trace)
    {
        IntPtr owner = new WindowInteropHelper(window).Handle;
        IntPtr popupHandle = popup?.Child is { } child && PresentationSource.FromVisual(child) is HwndSource source
            ? source.Handle : IntPtr.Zero;
        string Bounds(IntPtr handle) => handle != IntPtr.Zero && GetWindowRect(handle, out NativeRect rect)
            ? $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom} physical-px" : "NOT_OBSERVED";
        trace($"PLACEMENT {stage}: window={Bounds(owner)}; popup={Bounds(popupHandle)}; popupOpen={popup?.IsOpen}.");
    }

    private static void Observe(TextBox box, Action<string> trace)
    {
        box.TextChanged += (_, _) => trace("TextChanged " + CodePoints(box.Text));
        box.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) => trace("GotKeyboardFocus")), true);
        box.AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) => trace("LostKeyboardFocus")), true);
        box.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
            trace($"PreviewKeyDown key={e.Key}, ime={e.ImeProcessedKey}, handled={e.Handled}")), true);
        foreach (RoutedEvent routed in new[] { TextCompositionManager.PreviewTextInputStartEvent,
                     TextCompositionManager.PreviewTextInputUpdateEvent, TextCompositionManager.PreviewTextInputEvent,
                     TextCompositionManager.TextInputStartEvent, TextCompositionManager.TextInputUpdateEvent,
                     TextCompositionManager.TextInputEvent })
            box.AddHandler(routed, new TextCompositionEventHandler((_, e) =>
            {
                trace($"{e.RoutedEvent.Name} type={e.TextComposition.GetType().FullName}, handled={e.Handled}, "
                    + $"text={CodePoints(e.TextComposition.Text)}, composition={CodePoints(e.TextComposition.CompositionText)}");
                if (e.RoutedEvent == TextCompositionManager.PreviewTextInputStartEvent
                    || e.RoutedEvent == TextCompositionManager.TextInputStartEvent)
                    Snapshot(box, "event-" + e.RoutedEvent.Name, trace);
            }), true);
    }

    private static void DrainInitialization(Window window)
    {
        bool reachedIdle = false;
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Send,
            (_, _) => frame.Continue = false, window.Dispatcher);
        DispatcherOperation operation = window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
            new Action(() => { reachedIdle = true; frame.Continue = false; }));
        try { Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); if (!reachedIdle) operation.Abort(); }
        Assert.True(reachedIdle, "The initialization idle barrier timed out. No key was sent.");
    }

    private static void TypeT(Window window, TextBox box, Popup? popup, UIElement? placement, IntPtr layout, Action<string> trace,
        bool allowOwnedPopupNativeFocus = false)
    {
        AssertOwnedFocus(window, box, popup, placement, trace, allowOwnedPopupNativeFocus);
        if (allowOwnedPopupNativeFocus) AssertNeutralMouseButtons();
        AssertNeutralModifiers();
        Assert.Equal(layout, GetKeyboardLayout(0));
        Assert.True(InputMethod.GetIsInputMethodEnabled(box));
        Assert.Equal(InputMethodState.On, InputMethod.Current.ImeState);
        Assert.True(InputMethod.Current.ImeConversionMode.HasFlag(ImeConversionModeValues.Native));
        ushort vk = (ushort)KeyInterop.VirtualKeyFromKey(Key.T);
        ushort scan = (ushort)MapVirtualKey(vk, 0);
        var inputs = new[]
        {
            new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk, ScanCode = scan } } },
            new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk, ScanCode = scan, Flags = 2 } } },
        };
        trace("Send real T once.");
        Assert.Equal((uint)inputs.Length, SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()));
        Snapshot(box, "after-sendinput-before-dispatch", trace);
        Pump(window, () => false, TimeSpan.FromMilliseconds(150));
    }

    private static void AssertOwnedFocus(Window window, TextBox box, Popup? popup, UIElement? placement, Action<string> trace,
        bool allowOwnedPopupNativeFocus = false)
    {
        IntPtr owner = new WindowInteropHelper(window).Handle;
        Assert.NotEqual(IntPtr.Zero, owner);
        Assert.Equal(owner, GetForegroundWindow());
        Assert.Same(box, Keyboard.FocusedElement);
        Assert.True(box.IsKeyboardFocused && box.IsVisible && box.IsEnabled);
        HwndSource source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(box));
        Assert.False(source.IsDisposed);
        IntPtr target = source.Handle;
        uint thread = GetCurrentThreadId();
        Assert.Equal(thread, GetWindowThreadProcessId(owner, out uint ownerProcess));
        Assert.Equal((uint)Environment.ProcessId, ownerProcess);
        Assert.Equal(thread, GetWindowThreadProcessId(target, out uint targetProcess));
        Assert.Equal(ownerProcess, targetProcess);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        Assert.True(GetGUIThreadInfo(thread, ref info));
        Assert.Equal(thread, GetWindowThreadProcessId(info.Focus, out uint focusProcess));
        Assert.Equal(ownerProcess, focusProcess);
        trace($"HWND owner=0x{owner.ToInt64():X}, target=0x{target.ToInt64():X}, nativeFocus=0x{info.Focus.ToInt64():X}, thread={thread}.");
        if (popup is null) Assert.Equal(owner, target);
        else
        {
            Assert.True(popup.IsOpen);
            Assert.NotNull(popup.Child);
            Assert.True(popup.Child.IsVisible && popup.Child.IsAncestorOf(box));
            Assert.Same(source, PresentationSource.FromVisual(popup.Child));
            Assert.Same(placement, popup.PlacementTarget);
            Assert.NotNull(placement);
            HwndSource ownerSource = Assert.IsType<HwndSource>(PresentationSource.FromVisual(placement));
            Assert.Equal(owner, ownerSource.Handle);
            Assert.NotEqual(owner, target);
            Assert.Equal(owner, GetWindow(target, 4));
            int style = GetWindowLong(target, -20);
            Assert.Equal(0x08000000, style & 0x08000000);
            Assert.True(IsWindowVisible(target) && IsWindowEnabled(target));
            trace($"Popup direct owner=0x{GetWindow(target, 4).ToInt64():X}, exstyle=0x{style:X8}.");
        }
        if (allowOwnedPopupNativeFocus)
        {
            Assert.NotNull(popup);
            Assert.True(info.Focus == owner || info.Focus == target,
                "B1 native focus must remain on the exact same-thread owned window or Popup.");
        }
        else Assert.Equal(owner, info.Focus);
    }

    // B1 observes one real mouse route. Hooks only buffer messages; they never pump,
    // focus, inspect TSF, change handled status to true, or call application code.
    private sealed class NativePopupClick(Stopwatch clock, Action<string> trace)
    {
        private readonly NativeClickMessage[] _messages = new NativeClickMessage[256];
        private int _messageCount, _dropped, _flushed;
        private HwndSource? _ownerSource, _popupSource;
        private bool _ownerHooked, _popupHooked, _cursorSaved, _releaseAttempted;
        private NativePoint _savedCursor;
        private NativeRect _virtualScreen;
        private uint _sent;
        private bool _attempted;
        private long _sentAt;

        internal void ClickOnce(Window window, TextBox box, Popup popup, UIElement placement, Action? armObservation = null)
        {
            Assert.False(_attempted);
            IntPtr owner = new WindowInteropHelper(window).Handle;
            Assert.Equal(owner, GetForegroundWindow());
            Assert.NotEqual(IntPtr.Zero, owner);
            Assert.True(window.IsVisible && window.IsEnabled && box.IsVisible && box.IsEnabled);
            Assert.True(popup.IsOpen && !popup.StaysOpen && popup.AllowsTransparency);
            Assert.Same(placement, popup.PlacementTarget);
            Assert.NotNull(popup.Child);
            Assert.True(popup.Child.IsVisible && popup.Child.IsAncestorOf(box));
            _ownerSource = Assert.IsType<HwndSource>(PresentationSource.FromVisual(placement));
            _popupSource = Assert.IsType<HwndSource>(PresentationSource.FromVisual(box));
            Assert.False(_ownerSource.IsDisposed || _popupSource.IsDisposed);
            Assert.Equal(owner, _ownerSource.Handle);
            Assert.Same(_popupSource, PresentationSource.FromVisual(popup.Child));
            IntPtr target = _popupSource.Handle;
            Assert.NotEqual(IntPtr.Zero, target);
            Assert.NotEqual(owner, target);
            Assert.Equal(owner, GetWindow(target, 4));
            Assert.Equal(0x08000000, GetWindowLong(target, -20) & 0x08000000);
            Assert.True(IsWindowVisible(target) && IsWindowEnabled(target));
            uint thread = GetCurrentThreadId();
            Assert.Equal(thread, GetWindowThreadProcessId(owner, out uint ownerProcess));
            Assert.Equal((uint)Environment.ProcessId, ownerProcess);
            Assert.Equal(thread, GetWindowThreadProcessId(target, out uint targetProcess));
            Assert.Equal(ownerProcess, targetProcess);
            AssertNeutralModifiers();
            AssertNeutralMouseButtons();
            Assert.Equal(0, GetSystemMetrics(23)); // Reject swapped primary buttons; do not change them.
            Assert.True(IsOwnedRestingCapture(), "Capture is outside the exact owned PopupRoot; no click/key sent.");
            uint dpi = GetDpiForWindow(target);
            Assert.InRange(dpi, 96U, 768U);
            Assert.Equal(dpi, GetDpiForWindow(owner));
            System.Windows.DpiScale visualDpi = System.Windows.Media.VisualTreeHelper.GetDpi(box);
            Assert.Equal(dpi / 96d, visualDpi.DpiScaleX);
            Assert.Equal(dpi / 96d, visualDpi.DpiScaleY);
            Assert.True(double.IsFinite(box.ActualWidth) && double.IsFinite(box.ActualHeight)
                && box.ActualWidth > 4 && box.ActualHeight > 4);
            Point center = box.PointToScreen(new Point(box.ActualWidth / 2, box.ActualHeight / 2));
            Assert.True(double.IsFinite(center.X) && double.IsFinite(center.Y));
            var point = new NativePoint { X = checked((int)Math.Round(center.X)), Y = checked((int)Math.Round(center.Y)) };
            _virtualScreen = VirtualScreen();
            Assert.True(Contains(_virtualScreen, point), "Target is outside the physical virtual desktop.");
            Assert.True(GetWindowRect(target, out NativeRect popupBounds) && Contains(popupBounds, point));
            Point localPoint = box.PointFromScreen(new Point(point.X, point.Y));
            Assert.True(localPoint.X > 1 && localPoint.X < box.ActualWidth - 1
                && localPoint.Y > 1 && localPoint.Y < box.ActualHeight - 1);
            WaitForOwnedHit(window, box, popup, placement, owner, target, point, popupBounds, dpi);
            Assert.Equal(target, WindowFromPoint(point));
            Assert.True(GetCursorPos(out _savedCursor));
            _cursorSaved = true;
            Assert.True(Contains(_virtualScreen, _savedCursor));
            _ownerSource.AddHook(Hook); _ownerHooked = true;
            _popupSource.AddHook(Hook); _popupHooked = true;
            Snapshot(box, "before-b1-native-click", trace);
            trace($"B1_CLICK_PRE: owner=0x{owner.ToInt64():X}; popup=0x{target.ToInt64():X}; physical={point.X},{point.Y}; dpi={dpi}; savedCursor={_savedCursor.X},{_savedCursor.Y}; nativeCapture=0x{GetCapture().ToInt64():X}.");
            // B2 arms its existing-object observer after readiness and before the original final guards.
            // This callback has no dispatch, native call or focus mutation; B1 passes null.
            armObservation?.Invoke();
            // Final native ownership checks are adjacent to SendInput; no dispatch between them.
            Assert.Equal(owner, GetForegroundWindow());
            Assert.True(popup.IsOpen && box.IsVisible && box.IsEnabled);
            Assert.Equal(target, WindowFromPoint(point));
            Assert.True(IsOwnedRestingCapture());
            AssertNeutralModifiers();
            AssertNeutralMouseButtons();
            int width = _virtualScreen.Right - _virtualScreen.Left, height = _virtualScreen.Bottom - _virtualScreen.Top;
            var inputs = new[]
            {
                MouseEvent(0x0001 | 0x8000 | 0x4000,
                    (int)Math.Round((point.X - _virtualScreen.Left) * 65535d / (width - 1)),
                    (int)Math.Round((point.Y - _virtualScreen.Top) * 65535d / (height - 1))),
                MouseEvent(0x0002), MouseEvent(0x0004),
            };
            _attempted = true;
            _sentAt = Stopwatch.GetTimestamp();
            _sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
            int error = Marshal.GetLastWin32Error();
            trace($"B1_SENDINPUT_RETURN: requested=3; inserted={_sent}; lastError={error}; order=move/down/up; no input retry.");
            Assert.Equal(3U, _sent);
        }

        private void WaitForOwnedHit(Window window, TextBox box, Popup popup, UIElement placement,
            IntPtr owner, IntPtr target, NativePoint point, NativeRect popupBounds, uint dpi)
        {
            HwndSource ownerSource = Assert.IsType<HwndSource>(_ownerSource);
            HwndSource popupSource = Assert.IsType<HwndSource>(_popupSource);
            int observations = 0, renderCallbacks = 0;
            double startedMs = clock.Elapsed.TotalMilliseconds;
            double? firstOwnedHitMs = null;
            object? initial = null;
            bool conditionObserved = false;
            EventHandler rendering = (_, _) => renderCallbacks++;

            object Observe()
            {
                IntPtr hit = WindowFromPoint(point);
                observations++;
                if (hit == target && firstOwnedHitMs is null) firstOwnedHitMs = clock.Elapsed.TotalMilliseconds;
                return new
                {
                    observedMs = clock.Elapsed.TotalMilliseconds,
                    hitEqualsExactPopup = hit == target,
                    hit = DescribeNativeWindow(hit, owner, target),
                    owner = DescribeNativeWindow(owner, owner, target),
                    popup = DescribeNativeWindow(target, owner, target),
                    foreground = GetForegroundWindow().ToInt64(),
                    point = new { point.X, point.Y },
                    targetOpacity = box.Opacity,
                    targetHitTestVisible = box.IsHitTestVisible,
                    rootOpacity = (_popupSource?.RootVisual as UIElement)?.Opacity,
                    rootHitTestVisible = (_popupSource?.RootVisual as UIElement)?.IsHitTestVisible,
                };
            }

            void RequireUnchangedOwnerAndGeometry()
            {
                Assert.False(ownerSource.IsDisposed || popupSource.IsDisposed);
                Assert.Equal(owner, new WindowInteropHelper(window).Handle);
                Assert.Equal(owner, ownerSource.Handle);
                Assert.Equal(target, popupSource.Handle);
                Assert.Same(ownerSource, PresentationSource.FromVisual(placement));
                Assert.Same(popupSource, PresentationSource.FromVisual(box));
                UIElement child = Assert.IsAssignableFrom<UIElement>(popup.Child);
                Assert.Same(popupSource, PresentationSource.FromVisual(child));
                Assert.Same(placement, popup.PlacementTarget);
                Assert.True(popup.IsOpen && !popup.StaysOpen && popup.AllowsTransparency);
                Assert.True(window.IsVisible && window.IsEnabled && box.IsVisible && box.IsEnabled);
                Assert.True(child.IsVisible && child.IsAncestorOf(box));
                Assert.Equal(owner, GetForegroundWindow());
                Assert.Equal(owner, GetWindow(target, 4));
                Assert.Equal(0x08000000, GetWindowLong(target, -20) & 0x08000000);
                Assert.True(IsWindowVisible(owner) && IsWindowEnabled(owner));
                Assert.True(IsWindowVisible(target) && IsWindowEnabled(target));
                uint thread = GetCurrentThreadId();
                Assert.Equal(thread, GetWindowThreadProcessId(owner, out uint ownerProcess));
                Assert.Equal((uint)Environment.ProcessId, ownerProcess);
                Assert.Equal(thread, GetWindowThreadProcessId(target, out uint targetProcess));
                Assert.Equal(ownerProcess, targetProcess);
                Assert.Equal(dpi, GetDpiForWindow(owner));
                Assert.Equal(dpi, GetDpiForWindow(target));
                System.Windows.DpiScale currentDpi = System.Windows.Media.VisualTreeHelper.GetDpi(box);
                Assert.Equal(dpi / 96d, currentDpi.DpiScaleX);
                Assert.Equal(dpi / 96d, currentDpi.DpiScaleY);
                Assert.True(GetWindowRect(target, out NativeRect currentBounds));
                Assert.Equal(popupBounds, currentBounds);
                Assert.Equal(_virtualScreen, VirtualScreen());
                Point center = box.PointToScreen(new Point(box.ActualWidth / 2, box.ActualHeight / 2));
                Assert.True(double.IsFinite(center.X) && double.IsFinite(center.Y));
                Assert.Equal(point.X, checked((int)Math.Round(center.X)));
                Assert.Equal(point.Y, checked((int)Math.Round(center.Y)));
                Assert.True(Contains(currentBounds, point) && Contains(_virtualScreen, point));
                Point local = box.PointFromScreen(new Point(point.X, point.Y));
                Assert.True(local.X > 1 && local.X < box.ActualWidth - 1
                    && local.Y > 1 && local.Y < box.ActualHeight - 1);
                AssertNeutralModifiers();
                AssertNeutralMouseButtons();
                Assert.Equal(0, GetSystemMetrics(23));
                Assert.True(IsOwnedRestingCapture());
            }

            System.Windows.Media.CompositionTarget.Rendering += rendering;
            try
            {
                initial = Observe();
                // Same existing three-second dispatcher pump. Rendering callbacks are observations,
                // not proof of display presentation. Only the unchanged exact native hit permits input.
                Pump(window, () =>
                {
                    RequireUnchangedOwnerAndGeometry();
                    IntPtr hit = WindowFromPoint(point);
                    observations++;
                    if (hit == target && firstOwnedHitMs is null)
                        firstOwnedHitMs = clock.Elapsed.TotalMilliseconds;
                    conditionObserved = hit == target;
                    return conditionObserved;
                });
                RequireUnchangedOwnerAndGeometry();
            }
            finally
            {
                System.Windows.Media.CompositionTarget.Rendering -= rendering;
                object final = Observe();
                trace("B1_HIT_READINESS " + JsonSerializer.Serialize(new
                {
                    startedMs, finishedMs = clock.Elapsed.TotalMilliseconds,
                    deadlineMilliseconds = 3000, observations, renderCallbacks,
                    firstOwnedHitMs, conditionObserved, initial, final,
                    windowTitles = "NOT_READ", pixels = "NOT_READ",
                    observationScope = "Only this bounded pre-input wait; no focus/style/input changes",
                }));
            }
        }

        private static object DescribeNativeWindow(IntPtr handle, IntPtr owner, IntPtr popup)
        {
            uint thread = GetWindowThreadProcessId(handle, out uint process);
            var className = new System.Text.StringBuilder(256);
            Marshal.SetLastPInvokeError(0);
            int classLength = GetClassName(handle, className, className.Capacity);
            int classError = classLength == 0 ? Marshal.GetLastPInvokeError() : 0;
            bool rectangleObserved = GetWindowRect(handle, out NativeRect rect);
            int style = GetWindowLong(handle, -16), extendedStyle = GetWindowLong(handle, -20);
            return new
            {
                hwnd = handle.ToInt64(), equalsOwner = handle == owner, equalsPopup = handle == popup,
                threadId = thread, processId = process, identityObserved = thread != 0,
                className = classLength > 0 ? className.ToString() : null, classError,
                style, extendedStyle, layered = (extendedStyle & 0x00080000) != 0,
                transparent = (extendedStyle & 0x00000020) != 0,
                nativeOwner = GetWindow(handle, 4).ToInt64(), visible = IsWindowVisible(handle),
                enabled = IsWindowEnabled(handle), rectangleObserved,
                rectangle = rectangleObserved ? new[] { rect.Left, rect.Top, rect.Right, rect.Bottom } : null,
            };
        }

        internal void RequireMouseUpProcessed(Window window, TextBox box, Action? afterDispatch = null)
        {
            Pump(window, () => box.IsKeyboardFocused && HasMessage(0x0202)
                && MouseButtonsNeutral() && IsOwnedRestingCapture(), afterDispatch: afterDispatch);
            Assert.True(HasMessage(0x0201) && HasMessage(0x0202), "The owned Popup did not observe the complete native down/up pair; no T sent.");
            Assert.True(box.IsKeyboardFocused);
            AssertNeutralMouseButtons();
            Assert.True(IsOwnedRestingCapture(), "TextBox/foreign mouse capture remains; no T sent.");
            Assert.Equal(new WindowInteropHelper(window).Handle, GetForegroundWindow());
            FlushMessages();
            Snapshot(box, "after-b1-mouse-up-pump", trace);
        }

        internal void Restore(Window window)
        {
            var errors = new List<Exception>();
            void Attempt(string stage, Action action)
            {
                try { action(); }
                catch (Exception error) { errors.Add(error); trace($"B1_CLEANUP_FAIL {stage}: {error}"); }
            }
            Attempt("owned-left-up", () =>
            {
                // SendInput inserts in order. A returned prefix of two includes our DOWN
                // without UP. One compensating UP is cleanup, never a click or T retry.
                if (_sent == 2 && !_releaseAttempted)
                {
                    _releaseAttempted = true;
                    Input[] release = [MouseEvent(0x0004)];
                    uint released = SendInput(1, release, Marshal.SizeOf<Input>());
                    trace($"B1_COMPENSATING_LEFTUP: inserted={released}; lastError={Marshal.GetLastWin32Error()}; original failure retained.");
                    Assert.Equal(1U, released);
                    Pump(window, () => HasMessage(0x0202) && MouseButtonsNeutral() && IsOwnedRestingCapture());
                    Assert.True(HasMessage(0x0202) && MouseButtonsNeutral() && IsOwnedRestingCapture());
                }
            });
            Attempt("cursor-restore", () =>
            {
                if (_cursorSaved && _sent > 0)
                {
                    // Drain the submitted MOVE before restoring it. Do not take the cursor
                    // back if another user/window has acquired its current physical point.
                    Pump(window, CursorIsInOwnedPopup);
                    Assert.True(CursorIsInOwnedPopup(), "Cursor left the owned Popup; preserve its current position and report incomplete restoration.");
                    Assert.Equal(_virtualScreen, VirtualScreen());
                    AssertNeutralMouseButtons();
                    Assert.True(SetCursorPos(_savedCursor.X, _savedCursor.Y));
                    Assert.True(GetCursorPos(out NativePoint restored));
                    Assert.Equal(_savedCursor.X, restored.X);
                    Assert.Equal(_savedCursor.Y, restored.Y);
                    trace($"B1_CURSOR_RESTORED: {restored.X},{restored.Y}.");
                }
            });
            Attempt("owner-hook-remove", () => { if (_ownerHooked) { _ownerSource!.RemoveHook(Hook); _ownerHooked = false; } });
            Attempt("popup-hook-remove", () => { if (_popupHooked) { _popupSource!.RemoveHook(Hook); _popupHooked = false; } });
            Attempt("message-observation", FlushMessages);
            trace($"B1_INPUT_ACCOUNT: clickBatches={(_attempted ? 1 : 0)}; inserted={_sent}; compensatingLeftUp={_releaseAttempted}; droppedMessages={_dropped}.");
            if (errors.Count != 0) throw new AggregateException("B1 mouse cleanup failures.", errors);
        }

        private bool CursorIsInOwnedPopup() => _popupSource is { IsDisposed: false }
            && GetCursorPos(out NativePoint position) && WindowFromPoint(position) == _popupSource.Handle;

        private bool IsOwnedRestingCapture()
        {
            if (_popupSource is null || _popupSource.IsDisposed) return false;
            IntPtr captured = GetCapture();
            // Popup.cs(v10.0.11):1031-1069/1101-1122 maintains/reclaims SubTree capture
            // for StaysOpen=false. It is not an unreleased TextBox selection drag.
            return Mouse.Captured is null ? captured == IntPtr.Zero
                : ReferenceEquals(Mouse.Captured, _popupSource.RootVisual) && captured == _popupSource.Handle;
        }

        private bool HasMessage(int message) => _messages.Take(_messageCount).Any(value =>
            value.Message == message && value.Hwnd == _popupSource?.Handle && value.Timestamp >= _sentAt);

        private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message is 0x0021 or 0x0006 or 0x0007 or 0x0008 or 0x0281 or 0x010D
                or 0x0200 or 0x0201 or 0x0202 or 0x0215)
            {
                if (_messageCount < _messages.Length)
                    _messages[_messageCount++] = new(Stopwatch.GetTimestamp(), clock.Elapsed.TotalMilliseconds,
                        hwnd, message, wParam, lParam);
                else _dropped++;
            }
            handled = false;
            return IntPtr.Zero;
        }

        private void FlushMessages()
        {
            while (_flushed < _messageCount)
            {
                NativeClickMessage value = _messages[_flushed++];
                trace($"B1_HWND_MESSAGE: observedMs={value.ElapsedMs:F3}; ticks={value.Timestamp}; hwnd=0x{value.Hwnd.ToInt64():X}; msg=0x{value.Message:X}; wParam=0x{value.WParam.ToInt64():X}; lParam=0x{value.LParam.ToInt64():X}.");
            }
            Assert.Equal(0, _dropped);
        }

        private static bool Contains(NativeRect rect, NativePoint point) => point.X >= rect.Left && point.X < rect.Right
            && point.Y >= rect.Top && point.Y < rect.Bottom;
        private static NativeRect VirtualScreen()
        {
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
            Assert.True(width > 1 && height > 1);
            return new NativeRect { Left = left, Top = top, Right = checked(left + width), Bottom = checked(top + height) };
        }
        private static Input MouseEvent(uint flags, int x = 0, int y = 0) => new()
        {
            Type = 0, Data = new InputUnion { Mouse = new MouseInput { X = x, Y = y, Flags = flags } },
        };
        private readonly record struct NativeClickMessage(long Timestamp, double ElapsedMs,
            IntPtr Hwnd, int Message, IntPtr WParam, IntPtr LParam);
    }

    private static bool MouseButtonsNeutral() => new[] { 0x01, 0x02, 0x04, 0x05, 0x06 }
        .All(key => (GetAsyncKeyState(key) & 0x8000) == 0);
    private static void AssertNeutralMouseButtons() => Assert.True(MouseButtonsNeutral(),
        "A mouse button is held; no click/key retry is permitted.");

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder className, int maximum);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);


    private static void AssertNeutralModifiers() => Assert.False(
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0),
        "A modifier key is held. No key was sent.");

    private static string CodePoints(string text) => string.Join(" ", text.Take(16).Select(value => $"U+{(int)value:X4}"));

    private static void Pump(Window window, Func<bool> done, TimeSpan? timeout = null, Action? afterDispatch = null)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.Elapsed < (timeout ?? TimeSpan.FromSeconds(3)))
        {
            window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            afterDispatch?.Invoke();
            Thread.Sleep(15);
        }
    }

    private static IntPtr[] LoadedLayouts()
    {
        int count = GetKeyboardLayoutList(0, null);
        var layouts = new IntPtr[Math.Max(0, count)];
        if (count > 0) GetKeyboardLayoutList(count, layouts);
        return layouts;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadKeyboardLayout(string id, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr ActivateKeyboardLayout(IntPtr layout, uint flags);
    [DllImport("user32.dll")] private static extern bool UnloadKeyboardLayout(IntPtr layout);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[]? layouts);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int CaretLeft, CaretTop, CaretRight, CaretBottom;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public IntPtr ExtraInfo; }
}

internal sealed class NativePopupFocusObservationFactAttribute : FactAttribute
{
    public NativePopupFocusObservationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_IME_NATIVE_FOCUS_OBSERVE") is null)
            Skip = "Opt-in input-free native Popup focus observation: set MYCAPTURE_IME_NATIVE_FOCUS_OBSERVE=1 and MYCAPTURE_REAL_IME_TEST=1 on an idle desktop.";
        else if (Environment.GetEnvironmentVariable("MYCAPTURE_REAL_IME_TEST") != "1")
            Skip = "Native Popup focus observation changes the test thread keyboard layout; MYCAPTURE_REAL_IME_TEST=1 is required.";
    }
}

internal sealed class OnScreenPlainPopupDiagnosticFactAttribute : FactAttribute
{
    public OnScreenPlainPopupDiagnosticFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_IME_ONSCREEN_D0") is null)
            Skip = "Opt-in on-screen owner D0: set MYCAPTURE_IME_ONSCREEN_D0=1 and MYCAPTURE_REAL_IME_TEST=1 on an idle desktop.";
        else if (Environment.GetEnvironmentVariable("MYCAPTURE_REAL_IME_TEST") != "1")
            Skip = "On-screen owner D0 changes the keyboard layout and sends one T; MYCAPTURE_REAL_IME_TEST=1 is required.";
    }
}

internal sealed class NativeClickPlainPopupDiagnosticFactAttribute : FactAttribute
{
    public NativeClickPlainPopupDiagnosticFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_IME_ONSCREEN_NATIVE_CLICK") is null)
            Skip = "Opt-in B1: set MYCAPTURE_IME_ONSCREEN_NATIVE_CLICK=1 and MYCAPTURE_REAL_IME_TEST=1 on an idle interactive desktop.";
        else if (Environment.GetEnvironmentVariable("MYCAPTURE_REAL_IME_TEST") != "1")
            Skip = "B1 sends one owned native click and one T; MYCAPTURE_REAL_IME_TEST=1 is required.";
    }
}

internal sealed class NativeClickPlainWindowDiagnosticFactAttribute : FactAttribute
{
    public NativeClickPlainWindowDiagnosticFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_IME_ONSCREEN_WINDOW_CLICK") is null)
            Skip = "Opt-in Window D0: set MYCAPTURE_IME_ONSCREEN_WINDOW_CLICK=1 and MYCAPTURE_REAL_IME_TEST=1 on an idle interactive desktop.";
        else if (Environment.GetEnvironmentVariable("MYCAPTURE_REAL_IME_TEST") != "1")
            Skip = "Window D0 sends one owned native click and one T; MYCAPTURE_REAL_IME_TEST=1 is required.";
    }
}

internal sealed class NativeClickPopupBoundaryDiagnosticFactAttribute : FactAttribute
{
    public NativeClickPopupBoundaryDiagnosticFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_IME_ONSCREEN_POPUP_B2") is null)
            Skip = "Opt-in B2: set MYCAPTURE_IME_ONSCREEN_POPUP_B2=1 and MYCAPTURE_REAL_IME_TEST=1 on an idle interactive desktop.";
        else if (Environment.GetEnvironmentVariable("MYCAPTURE_REAL_IME_TEST") != "1")
            Skip = "B2 observes existing Popup focus boundaries, sends one owned native click and one T; MYCAPTURE_REAL_IME_TEST=1 is required.";
    }
}

internal sealed class NativeProductColorOpenerDiagnosticFactAttribute : FactAttribute
{
    public NativeProductColorOpenerDiagnosticFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_IME_PRODUCT_OPENER") is null)
            Skip = "Opt-in product opener: set MYCAPTURE_IME_PRODUCT_OPENER=1 and MYCAPTURE_REAL_IME_TEST=1 on an idle interactive desktop.";
        else if (Environment.GetEnvironmentVariable("MYCAPTURE_REAL_IME_TEST") != "1")
            Skip = "The product opener sends one owned native click and one T; MYCAPTURE_REAL_IME_TEST=1 is required.";
    }
}

internal sealed class InlineProductHostObservationFactAttribute : FactAttribute
{
    public InlineProductHostObservationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_IME_INLINE_HOST_OBSERVE") is null)
            Skip = "Opt-in current inline host observation: MYCAPTURE_IME_INLINE_HOST_OBSERVE=1 and MYCAPTURE_REAL_IME_TEST=1.";
        else if (Environment.GetEnvironmentVariable("MYCAPTURE_REAL_IME_TEST") != "1")
            Skip = "The owned UI observation requires MYCAPTURE_REAL_IME_TEST=1 on an idle desktop.";
    }
}
