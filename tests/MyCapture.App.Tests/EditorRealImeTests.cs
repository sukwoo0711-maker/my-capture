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
public sealed class EditorRealImeTests
{
    [RealKoreanImeFact]
    public void HangulModeLettersRunEditorShortcutsAndTextStillComposes() => StaTestHost.Run(() =>
    {
        IntPtr previousLayout = GetKeyboardLayout(0);
        bool koreanWasLoaded = LoadedLayouts().Any(layout => (layout.ToInt64() & 0xFFFF) == 0x0412);
        IntPtr korean = LoadKeyboardLayout("00000412", KlfActivate);
        Assert.True(korean != IntPtr.Zero && (korean.ToInt64() & 0xFFFF) == 0x0412, "The Korean keyboard layout could not be loaded.");

        using EditorWindowHost host = EditorWindowHost.Create();
        Window window = host.Window;
        AnnotationEditorControl editor = host.Editor;
        AnnotationEditorController controller = Controller(editor);
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        InputMethodState previousState = InputMethodState.DoNotCare;
        ImeConversionModeValues previousMode = ImeConversionModeValues.DoNotCare;
        TextBox color = ColorInput(editor);
        try
        {
            window.Activate();
            SetForegroundWindow(hwnd);
            Pump(window, () => GetForegroundWindow() == hwnd);
            Assert.True(GetForegroundWindow() == hwnd,
                "The editor could not take the foreground (run from a focused console on an idle desktop). No keys were sent.");
            Assert.NotEqual(IntPtr.Zero, ActivateKeyboardLayout(korean, 0));

            // Hangul mode is switched in the colour box, where the IME is enabled.
            Assert.True(color.Focus());
            Pump(window, () => color.IsKeyboardFocused);
            previousState = InputMethod.Current.ImeState;
            previousMode = InputMethod.Current.ImeConversionMode;
            InputMethod.Current.ImeState = InputMethodState.On;
            InputMethod.Current.ImeConversionMode = ImeConversionModeValues.Native;
            Pump(window, () => InputMethod.Current.ImeConversionMode.HasFlag(ImeConversionModeValues.Native));
            Assert.True(InputMethod.Current.ImeConversionMode.HasFlag(ImeConversionModeValues.Native), "Hangul mode could not be turned on.");

            // Proof that the IME is live: T in the colour box composes ㅅ.
            string original = color.Text;
            color.SelectAll();
            Type(window, hwnd, Key.T);
            Pump(window, () => color.Text.Contains('ㅅ'));
            Assert.Contains("ㅅ", color.Text);

            // The canvas: letters are shortcuts even in Hangul mode.
            Assert.True(editor.Focus());
            Pump(window, () => editor.IsKeyboardFocused);
            color.Text = original;
            Type(window, hwnd, Key.T);
            Pump(window, () => controller.Tool == EditorTool.Text);
            Assert.Equal(EditorTool.Text, controller.Tool);
            Type(window, hwnd, Key.R);
            Pump(window, () => controller.Tool == EditorTool.Rectangle);
            Assert.Equal(EditorTool.Rectangle, controller.Tool);
            Type(window, hwnd, Key.Q);
            Pump(window, () => controller.RotationTurns == 3);
            Assert.Equal(3, controller.RotationTurns);
            Type(window, hwnd, Key.W);
            Pump(window, () => controller.RotationTurns == 0);
            Assert.Equal(0, controller.RotationTurns);
            Assert.DoesNotContain(controller.Document.Items.OfType<TextAnnotation>(), text => text.Text.Contains('ㅅ'));

            // The live text box still takes Hangul: T then K compose 사.
            Invoke(editor, "PlaceTextBox", new PointD(40, 40));
            TextBox live = Field<TextBox>(editor, "_activeTextBox");
            Pump(window, () => live.IsKeyboardFocused);
            Type(window, hwnd, Key.T);
            Type(window, hwnd, Key.K);
            Pump(window, () => live.Text == "사");
            Assert.Equal("사", live.Text);
        }
        finally
        {
            if (previousMode != ImeConversionModeValues.DoNotCare)
            {
                color.Focus();
                InputMethod.Current.ImeConversionMode = previousMode;
                InputMethod.Current.ImeState = previousState;
            }

            ActivateKeyboardLayout(previousLayout, 0);
            if (!koreanWasLoaded) UnloadKeyboardLayout(korean);
        }
    });

    private static void Type(Window window, IntPtr hwnd, Key key)
    {
        // Never type into another application: stop if the editor lost the foreground.
        Assert.True(GetForegroundWindow() == hwnd, "The editor lost the foreground; the remaining keys were not sent.");
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

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
