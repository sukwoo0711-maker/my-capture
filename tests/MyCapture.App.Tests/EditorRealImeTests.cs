using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using Xunit;
using Xunit.Abstractions;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in: types T, R, Q and W into the image editor through the real Windows Korean IME in
/// Hangul mode and checks that the shortcuts run, then checks that Hangul still composes in the
/// editor's text box.
/// </summary>
/// <remarks>
/// <para>
/// Never runs by default. It switches this test thread's keyboard layout to Korean (00000412),
/// turns Hangul mode on, makes an off-screen editor window the foreground window and sends
/// real key presses with SendInput, so it must run on an idle desktop: set
/// <c>MYCAPTURE_REAL_IME_TEST=1</c> and keep hands off the keyboard and mouse for about ten
/// seconds. Every key press is sent only after checking that the editor is still the
/// foreground window; if focus moves away the test stops without sending more keys.
/// </para>
/// <para>
/// Afterwards it restores the IME mode, reactivates the previous layout, and unloads the
/// Korean layout if the test loaded it.
/// </para>
/// </remarks>
public sealed class EditorRealImeTests(ITestOutputHelper output)
{
    [RealKoreanImeFact]
    public void HangulModeLettersRunEditorShortcutsAndTextStillComposes() => StaTestHost.Run(() =>
    {
        var clock = Stopwatch.StartNew();
        void Trace(string message) => output.WriteLine($"{clock.Elapsed.TotalMilliseconds:F3} ms: {message}");
        AssertNeutralModifiers();
        IntPtr previousLayout = GetKeyboardLayout(0);
        IntPtr[] loadedBefore = LoadedLayouts();
        IntPtr korean = IntPtr.Zero;
        EditorWindowHost? host = null;
        TextBox? color = null;
        InputMethodState previousState = InputMethodState.DoNotCare;
        ImeConversionModeValues previousMode = ImeConversionModeValues.DoNotCare;
        bool modeCaptured = false;
        try
        {
            korean = LoadKeyboardLayout("00000412", KlfActivate);
            Assert.True(korean != IntPtr.Zero && (korean.ToInt64() & 0xFFFF) == 0x0412, "The Korean keyboard layout could not be loaded.");
            bool rendered = false;
            host = EditorWindowHost.Create(beforeShow: candidate =>
            {
                candidate.ContentRendered += (_, _) =>
                {
                    rendered = true;
                    Trace("ContentRendered completed; focused=" + FocusName());
                };
                ObserveInput(candidate, "window", Trace);
            });
            Window window = host.Window;
            AnnotationEditorControl editor = host.Editor;
            AnnotationEditorController controller = Controller(editor);
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            // The product's first ContentRendered handler activates and focuses the canvas.
            // Let that handler finish before opening/focusing the separate popup HWND.
            Pump(window, () => rendered);
            Assert.True(rendered, "The editor did not finish its first ContentRendered event. No keys were sent.");
            window.Activate();
            SetForegroundWindow(hwnd);
            Pump(window, () => GetForegroundWindow() == hwnd);
            Assert.True(GetForegroundWindow() == hwnd,
                "The editor could not take the foreground (run from a focused console on an idle desktop). No keys were sent.");
            Assert.NotEqual(IntPtr.Zero, ActivateKeyboardLayout(korean, 0));
            Assert.Equal(korean, GetKeyboardLayout(0));

            // Hangul mode is switched in the colour box, where the IME is enabled.
            color = ColorInput(editor);
            ObserveInput(color, "color", Trace);
            Assert.True(color.Focus());
            Pump(window, () => color.IsKeyboardFocused);
            AssertOwnedFocus(window, color, Trace);
            DrainInitialization(window);
            previousState = InputMethod.Current.ImeState;
            previousMode = InputMethod.Current.ImeConversionMode;
            modeCaptured = true;
            Trace($"Captured IME state={previousState}, mode={previousMode}, layout=0x{korean.ToInt64():X}.");
            InputMethod.Current.ImeState = InputMethodState.On;
            InputMethod.Current.ImeConversionMode = ImeConversionModeValues.Native;
            Pump(window, () => InputMethod.Current.ImeState == InputMethodState.On
                && InputMethod.Current.ImeConversionMode.HasFlag(ImeConversionModeValues.Native));
            Assert.Equal(InputMethodState.On, InputMethod.Current.ImeState);
            Assert.True(InputMethod.Current.ImeConversionMode.HasFlag(ImeConversionModeValues.Native), "Hangul mode could not be turned on.");

            // Proof that the IME is live: T in the colour box composes ㅅ.
            string original = color.Text;
            color.SelectAll();
            Assert.True(editor.IsCustomColorFlyoutOpenForTest);
            Type(window, color, korean, Key.T, Trace);
            Pump(window, () => color.Text.Contains('ㅅ'));
            Trace($"Color proof: text={CodePoints(color.Text)}, tool={controller.Tool}, popup={editor.IsCustomColorFlyoutOpenForTest}; focused={FocusName()}.");
            Assert.Contains("ㅅ", color.Text);

            // The canvas: letters are shortcuts even in Hangul mode.
            Assert.True(editor.Focus());
            Pump(window, () => editor.IsKeyboardFocused);
            color.Text = original;
            Type(window, editor, korean, Key.T, Trace);
            Pump(window, () => controller.Tool == EditorTool.Text);
            Assert.Equal(EditorTool.Text, controller.Tool);
            Type(window, editor, korean, Key.R, Trace);
            Pump(window, () => controller.Tool == EditorTool.Rectangle);
            Assert.Equal(EditorTool.Rectangle, controller.Tool);
            Type(window, editor, korean, Key.Q, Trace);
            Pump(window, () => controller.RotationTurns == 3);
            Assert.Equal(3, controller.RotationTurns);
            Type(window, editor, korean, Key.W, Trace);
            Pump(window, () => controller.RotationTurns == 0);
            Assert.Equal(0, controller.RotationTurns);
            Assert.DoesNotContain(controller.Document.Items.OfType<TextAnnotation>(), text => text.Text.Contains('ㅅ'));

            // The live text box still takes Hangul: T then K compose 사.
            Invoke(editor, "PlaceTextBox", new PointD(40, 40));
            TextBox live = Field<TextBox>(editor, "_activeTextBox");
            ObserveInput(live, "live", Trace);
            Pump(window, () => live.IsKeyboardFocused);
            DrainInitialization(window);
            Type(window, live, korean, Key.T, Trace);
            Type(window, live, korean, Key.K, Trace);
            Pump(window, () => live.Text == "사");
            Trace("Live proof: text=" + CodePoints(live.Text));
            Assert.Equal("사", live.Text);
        }
        catch (Exception ex)
        {
            Trace($"Body failed: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            try
            {
                if (modeCaptured && host is not null && color is not null)
                {
                    // Do not reclaim the desktop if another application became foreground.
                    // A failed mode restore still reaches the layout/unload/close finally blocks.
                    Assert.Equal(new WindowInteropHelper(host.Window).Handle, GetForegroundWindow());
                    _ = ColorInput(host.Editor);
                    Assert.True(color.Focus());
                    Pump(host.Window, () => color.IsKeyboardFocused);
                    AssertOwnedFocus(host.Window, color, Trace);
                    if (previousMode != ImeConversionModeValues.DoNotCare)
                        InputMethod.Current.ImeConversionMode = previousMode;
                    if (previousState != InputMethodState.DoNotCare)
                        InputMethod.Current.ImeState = previousState;
                    Pump(host.Window, () => (previousMode == ImeConversionModeValues.DoNotCare
                            || InputMethod.Current.ImeConversionMode == previousMode)
                        && (previousState == InputMethodState.DoNotCare || InputMethod.Current.ImeState == previousState));
                    Trace($"Restored IME state={InputMethod.Current.ImeState}, mode={InputMethod.Current.ImeConversionMode}; "
                        + $"expected state={previousState}, mode={previousMode}.");
                    if (previousMode != ImeConversionModeValues.DoNotCare)
                        Assert.Equal(previousMode, InputMethod.Current.ImeConversionMode);
                    if (previousState != InputMethodState.DoNotCare)
                        Assert.Equal(previousState, InputMethod.Current.ImeState);
                }
            }
            finally
            {
                try
                {
                    Assert.NotEqual(IntPtr.Zero, ActivateKeyboardLayout(previousLayout, 0));
                    Trace($"Restored layout=0x{GetKeyboardLayout(0).ToInt64():X}; expected=0x{previousLayout.ToInt64():X}.");
                    Assert.Equal(previousLayout, GetKeyboardLayout(0));
                }
                finally
                {
                    try
                    {
                        if (korean != IntPtr.Zero && !loadedBefore.Contains(korean))
                        {
                            Assert.True(UnloadKeyboardLayout(korean), "The Korean layout loaded by this test could not be unloaded.");
                            Assert.DoesNotContain(korean, LoadedLayouts());
                            Trace("Unloaded only the Korean layout introduced by this test.");
                        }
                    }
                    finally { host?.Dispose(); }
                }
            }
        }
    });

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

    private static void Type(Window window, UIElement target, IntPtr layout, Key key, Action<string> trace)
    {
        // Never type into another application: stop if the editor lost the foreground.
        AssertOwnedFocus(window, target, trace);
        AssertNeutralModifiers();
        Assert.Equal(layout, GetKeyboardLayout(0));
        if (target is TextBox)
        {
            Assert.True(InputMethod.GetIsInputMethodEnabled(target));
            Assert.Equal(InputMethodState.On, InputMethod.Current.ImeState);
            Assert.True(InputMethod.Current.ImeConversionMode.HasFlag(ImeConversionModeValues.Native));
        }
        trace($"Send {key}; IME state={InputMethod.Current.ImeState}, mode={InputMethod.Current.ImeConversionMode}.");
        ushort vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        ushort scan = (ushort)MapVirtualKey(vk, 0);
        var inputs = new[]
        {
            new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk, ScanCode = scan } } },
            new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk, ScanCode = scan, Flags = KeyUp } } },
        };
        Assert.Equal((uint)inputs.Length, SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()));
        Pump(window, () => false, TimeSpan.FromMilliseconds(150));
    }

    private static void AssertOwnedFocus(Window window, UIElement target, Action<string> trace)
    {
        IntPtr owner = new WindowInteropHelper(window).Handle;
        Assert.NotEqual(IntPtr.Zero, owner);
        Assert.True(GetForegroundWindow() == owner, "The editor lost the foreground; the remaining keys were not sent.");
        Assert.Same(target, Keyboard.FocusedElement);
        Assert.True(target.IsKeyboardFocused);
        Assert.True(target.IsVisible);
        Assert.True(target.IsEnabled);
        HwndSource source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(target));
        Assert.False(source.IsDisposed);
        IntPtr targetHwnd = source.Handle;
        Assert.NotEqual(IntPtr.Zero, targetHwnd);
        uint thread = GetCurrentThreadId();
        Assert.Equal(thread, GetWindowThreadProcessId(owner, out uint ownerProcess));
        Assert.Equal((uint)Environment.ProcessId, ownerProcess);
        Assert.Equal(thread, GetWindowThreadProcessId(targetHwnd, out uint targetProcess));
        Assert.Equal(ownerProcess, targetProcess);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        Assert.True(GetGUIThreadInfo(thread, ref info), "Could not inspect the test thread's native focus. No keys were sent.");
        trace($"Focus managed={FocusName()}, target HWND=0x{targetHwnd.ToInt64():X}, "
            + $"native HWND=0x{info.Focus.ToInt64():X}, foreground=0x{owner.ToInt64():X}, thread={thread}.");
        Assert.NotEqual(IntPtr.Zero, info.Focus);
        Assert.Equal(thread, GetWindowThreadProcessId(info.Focus, out uint focusProcess));
        Assert.Equal(ownerProcess, focusProcess);

        AnnotationEditorControl editor = Assert.IsType<AnnotationEditorWindow>(window).Editor;
        if (ReferenceEquals(target, editor.CustomColorInputForTest))
        {
            FrameworkElement flyout = editor.CustomColorFlyoutContentForTest;
            trace($"Color inline host open={editor.IsCustomColorFlyoutOpenForTest}, HWND=0x{targetHwnd.ToInt64():X}, owner=0x{owner.ToInt64():X}.");
            Assert.True(editor.IsCustomColorFlyoutOpenForTest);
            Assert.True(flyout.IsVisible && flyout.IsEnabled);
            Assert.True(editor.IsAncestorOf(flyout) && flyout.IsAncestorOf(target));
            Assert.Same(source, PresentationSource.FromVisual(flyout));
            Assert.Same(source, PresentationSource.FromVisual(editor.CustomColorButtonForTest));
            Assert.Same(source, PresentationSource.FromVisual(window));
            Assert.True(IsWindowVisible(targetHwnd) && IsWindowEnabled(targetHwnd));
            Assert.Equal(owner, targetHwnd);
            Assert.Equal(owner, info.Focus);
        }
        else
        {
            // Canvas and live editor text input must share the owned window HWND.
            Assert.Equal(owner, targetHwnd);
            Assert.Equal(targetHwnd, info.Focus);
        }
    }

    private static void AssertNeutralModifiers() => Assert.False(
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0),
        "A modifier key is held; no keys were sent.");

    private static string FocusName() => Keyboard.FocusedElement?.GetType().Name ?? "none";
    private static string CodePoints(string text) => string.Join(" ", text.Take(16).Select(value => $"U+{(int)value:X4}"));

    private static void ObserveInput(UIElement target, string name, Action<string> trace)
    {
        target.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) =>
            trace(name + " GotKeyboardFocus; focused=" + FocusName())), handledEventsToo: true);
        target.AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, _) =>
            trace(name + " LostKeyboardFocus; focused=" + FocusName())), handledEventsToo: true);
        target.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
            trace($"{name} PreviewKeyDown key={e.Key}, ime={e.ImeProcessedKey}, handled={e.Handled}; focused={FocusName()}")), handledEventsToo: true);
        foreach (RoutedEvent routed in new[] { TextCompositionManager.PreviewTextInputStartEvent,
            TextCompositionManager.PreviewTextInputUpdateEvent, TextCompositionManager.PreviewTextInputEvent })
            target.AddHandler(routed, new TextCompositionEventHandler((_, e) =>
                trace($"{name} {e.RoutedEvent.Name} handled={e.Handled}, text={CodePoints(e.TextComposition.Text)}, "
                    + $"composition={CodePoints(e.TextComposition.CompositionText)}")), handledEventsToo: true);
    }

    private static void Pump(Window window, Func<bool> done, TimeSpan? timeout = null)
    {
        var clock = Stopwatch.StartNew();
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(3);
        while (!done() && clock.Elapsed < limit)
        {
            window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
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

    private const uint KlfActivate = 0x00000001;
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadKeyboardLayout(string id, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr ActivateKeyboardLayout(IntPtr layout, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnloadKeyboardLayout(IntPtr layout);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[]? layouts);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int CaretLeft, CaretTop, CaretRight, CaretBottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint Data;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}

/// <summary>Skips unless <c>MYCAPTURE_REAL_IME_TEST=1</c>: the test drives the real keyboard.</summary>
internal sealed class RealKoreanImeFactAttribute : FactAttribute
{
    public RealKoreanImeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_REAL_IME_TEST") != "1")
            Skip = "Opt-in real Korean IME test: set MYCAPTURE_REAL_IME_TEST=1 on an idle desktop (it switches the keyboard layout and sends key presses).";
    }
}
