using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Posts left-button input to one HWND back to back. WPF's mouse provider accepts these only
/// from the HWND under the cursor or holding capture, so each gesture holds native capture as
/// a real press would; WPF's own capture replaces it, and a gesture nothing captured gives it
/// back. The thread's key state is that of a plain left click: the left button follows each
/// press and release, as for real input, and the other buttons and the modifier keys are up.
/// </summary>
/// <remarks>
/// As with a real mouse, the pointer arrives before the press: each gesture starts with a
/// buttonless hover one pixel away, then one on the first point. The first input activates
/// WPF's mouse, and the hit test that follows activation asks the desktop for the window
/// under the point (WindowFromPoint). Test windows of every process share the same off-screen
/// spot, so another process's window can answer, and a press with no hover before it then
/// reaches no element. The second hover moves the pointer, so WPF hit-tests this window's
/// own visual tree instead.
/// </remarks>
internal sealed class QueuedMouse : IDisposable
{
    private const int MouseMove = 0x0200, LeftDown = 0x0201, LeftUp = 0x0202, LeftButtonFlag = 0x0001;
    // Right, middle and X buttons; Shift, Ctrl, Alt (either side) and the Windows keys.
    private static readonly byte[] OtherButtonsAndModifiers =
        [0x02, 0x04, 0x05, 0x06, 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];
    private readonly HwndSource _source;
    private readonly HwndSourceHook _hook;
    private readonly Control? _observed;
    private int _pending;
    private int _unaccepted;

    public QueuedMouse(Window window, Control? observed = null)
    {
        _source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(window));
        _observed = observed;
        _hook = Hook;
        _source.AddHook(_hook);
    }

    public nint Handle => _source.Handle;

    /// <summary>Whether the observed box's text view had a valid layout when the last release arrived.</summary>
    public bool? ViewValidAtRelease { get; private set; }

    /// <summary>
    /// Whether the HWND still held native capture once the last gesture was processed, read
    /// before this helper gives back the capture it took for a gesture nothing captured.
    /// </summary>
    public bool NativeCaptureAfterGesture { get; private set; }

    /// <summary>Clicks <paramref name="element"/> at <paramref name="at"/> (its own coordinates), or its center.</summary>
    public void Click(FrameworkElement element, Point? at = null) => Post(element, (LeftDown, at), (LeftUp, at));

    public void Drag(FrameworkElement element, Point from, Point to) => Post(element, (LeftDown, from),
        (MouseMove, new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2)), (MouseMove, to), (LeftUp, to));

    public void Drain()
    {
        _source.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        _source.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    public void Dispose()
    {
        _source.RemoveHook(_hook);
        SetLeftButton(false);
        Mouse.Capture(null);
        if (GetCapture() == Handle) _ = ReleaseCapture();
    }

    private void Post(FrameworkElement element, params (int Message, Point? At)[] steps)
    {
        Drain();
        Assert.True(element.IsVisible && element.ActualWidth > 4 && element.ActualHeight > 4);
        Assert.Equal(0, _pending);
        // A capture left from an earlier gesture is not cleared here: the gesture then goes
        // where WPF sends it, as the user's next click would.
        Point first = DevicePoint(element, steps[0].At);
        (int Message, nint Buttons, nint Position)[] posts =
        [
            (MouseMove, 0, Pack(new Point(first.X >= 1 ? first.X - 1 : first.X + 1, first.Y))),
            (MouseMove, 0, Pack(first)),
            .. steps.Select(step => (step.Message, step.Message == LeftUp ? (nint)0 : LeftButtonFlag,
                Pack(DevicePoint(element, step.At)))),
        ];
        _ = SetCapture(Handle);
        _pending = posts.Length;
        _unaccepted = 0;
        foreach ((int message, nint buttons, nint position) in posts)
        {
            Assert.True(PostMessage(Handle, message, buttons, position));
        }
        Drain();
        Assert.Equal(0, _pending);
        Assert.Equal(0, _unaccepted);
        NativeCaptureAfterGesture = GetCapture() == Handle;
        if (Mouse.Captured is null && NativeCaptureAfterGesture) _ = ReleaseCapture();
    }

    private Point DevicePoint(FrameworkElement element, Point? at)
    {
        Point local = at ?? new Point(element.ActualWidth / 2, element.ActualHeight / 2);
        return _source.CompositionTarget.TransformToDevice.Transform(
            element.TranslatePoint(local, (UIElement)_source.RootVisual));
    }

    private static nint Pack(Point device) =>
        ((int)Math.Round(device.Y) << 16) | ((int)Math.Round(device.X) & 0xFFFF);

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_pending > 0 && message is MouseMove or LeftDown or LeftUp)
        {
            if (GetCapture() != hwnd) _unaccepted++;
            // A move carries its button state, so a hover has the left button up and a drag has it down.
            SetLeftButton(message == LeftDown || (message == MouseMove && (wParam & LeftButtonFlag) != 0));
            if (message == LeftUp && _observed is { } box) ViewValidAtRelease = IsViewValid(box);
            _pending--;
        }
        return 0;
    }

    private static void SetLeftButton(bool pressed)
    {
        var keys = new byte[256];
        Assert.True(GetKeyboardState(keys));
        keys[0x01] = pressed ? (byte)0x80 : (byte)0;
        foreach (byte key in OtherButtonsAndModifiers) keys[key] = 0;
        Assert.True(SetKeyboardState(keys));
    }

    internal static bool IsViewValid(Control box)
    {
        var host = Assert.IsType<ScrollViewer>(box.Template.FindName("PART_ContentHost", box));
        var view = Assert.IsAssignableFrom<UIElement>(host.Content);
        return view.IsMeasureValid && view.IsArrangeValid;
    }

    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetKeyboardState(byte[] keys);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetKeyboardState(byte[] keys);
}
