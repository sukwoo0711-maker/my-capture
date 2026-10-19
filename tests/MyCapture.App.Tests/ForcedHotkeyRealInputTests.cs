using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using MyCapture.Core.Settings;
using MyCapture.Platform.Interop;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in: proves that a chord claimed by <see cref="LowLevelKeyboardHotkeyOverride"/> reaches
/// MyCapture's window even though another window already owns it through <c>RegisterHotKey</c>,
/// and that the other owner gets it back once the claim is released.
/// </summary>
/// <remarks>
/// Never runs by default. It sends real key presses with SendInput, so it must run on an idle,
/// unlocked desktop: set <c>MYCAPTURE_REAL_HOTKEY_TEST=1</c> and keep hands off the keyboard for
/// about three seconds. The chord is Ctrl+Shift+F24, which no keyboard has, so a key that leaks
/// to the foreground window does nothing there. No keys are sent while the user holds a modifier.
/// </remarks>
public sealed class ForcedHotkeyRealInputTests
{
    private static readonly Hotkey Chord = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, VkF24);

    [RealHotkeyInputFact]
    public void ClaimedChordArrivesAheadOfAnotherProgramsRegistration() => StaTestHost.Run(() =>
    {
        Assert.False(AnyModifierDown(), "A modifier key is held; no keys were sent.");
        using var other = new NativeMessageWindow();
        using var ours = new NativeMessageWindow();
        var otherHits = new List<IntPtr>();
        var ourHits = new List<IntPtr>();
        other.MessageReceived += (_, e) => { if (e.Message == NativeMethods.WM_HOTKEY) otherHits.Add(e.WParam); };
        ours.MessageReceived += (_, e) => { if (e.Message == NativeMethods.WM_HOTKEY) ourHits.Add(e.WParam); };

        const int otherId = 0x7101;
        const int ourId = 0x7102;
        Assert.True(NativeMethods.RegisterHotKey(other.Handle, otherId,
            (uint)Chord.Modifiers | NativeMethods.MOD_NOREPEAT, Chord.VirtualKey),
            "Ctrl+Shift+F24 is already registered on this desktop; the test cannot stand in for the other program.");
        try
        {
            // The other owner receives the chord while nothing claims it.
            SendChord(repeatDown: false);
            Pump(() => otherHits.Count == 1);
            Assert.Equal(new[] { new IntPtr(otherId) }, otherHits);
            Assert.Empty(ourHits);

            using var hotkeyOverride = new LowLevelKeyboardHotkeyOverride(ours);
            Assert.True(hotkeyOverride.TryClaim(ourId, Chord));

            // A claimed chord, held long enough to auto-repeat, fires once and only for us.
            SendChord(repeatDown: true);
            Pump(() => ourHits.Count == 1);
            Pump(() => false, TimeSpan.FromMilliseconds(300));
            Assert.Equal(new[] { new IntPtr(ourId) }, ourHits);
            Assert.Single(otherHits);

            // Released, the chord goes back to the program that registered it.
            hotkeyOverride.Release(ourId);
            SendChord(repeatDown: false);
            Pump(() => otherHits.Count == 2);
            Assert.Equal(2, otherHits.Count);
            Assert.Single(ourHits);
        }
        finally
        {
            NativeMethods.UnregisterHotKey(other.Handle, otherId);
        }
    });

    private const uint VkF24 = 0x87;
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;

    private static void SendChord(bool repeatDown)
    {
        Assert.False(AnyModifierDown(), "A modifier key is held; the remaining keys were not sent.");
        var keys = new List<(ushort Key, bool Up)> { (VkControl, false), (VkShift, false), ((ushort)VkF24, false) };
        if (repeatDown) keys.Add(((ushort)VkF24, false));
        keys.AddRange([((ushort)VkF24, true), (VkShift, true), (VkControl, true)]);
        Input[] inputs = keys.ConvertAll(k => new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = k.Key, Flags = k.Up ? KeyUp : 0 } },
        }).ToArray();
        Assert.Equal((uint)inputs.Length, SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()));
    }

    private static bool AnyModifierDown() =>
        new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    private static void Pump(Func<bool> done, TimeSpan? timeout = null)
    {
        var clock = Stopwatch.StartNew();
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(3);
        while (!done() && clock.Elapsed < limit)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(15);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

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

/// <summary>Skips unless <c>MYCAPTURE_REAL_HOTKEY_TEST=1</c>: the test drives the real keyboard.</summary>
internal sealed class RealHotkeyInputFactAttribute : FactAttribute
{
    public RealHotkeyInputFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_REAL_HOTKEY_TEST") != "1")
            Skip = "Opt-in real hotkey test: set MYCAPTURE_REAL_HOTKEY_TEST=1 on an idle, unlocked desktop (it sends Ctrl+Shift+F24 key presses).";
    }
}
