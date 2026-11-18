using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Esc during a scroll capture: the claim is a WM_HOTKEY id on the resident message window, so the
/// route is exercised with posted messages and fake registration, never with the real key.
/// </summary>
public sealed class ScrollCaptureEscapeTests
{
    private const uint WmHotkey = 0x0312;

    [Fact]
    public void EscCancelsOnceAndReleasesTheKeyBeforeCancelling() => StaTestHost.Run(() =>
    {
        using var window = new NativeMessageWindow();
        var registered = new List<IntPtr>();
        var released = new List<IntPtr>();
        int cancels = 0;
        bool releasedBeforeCancel = false;
        using ScrollCaptureEscape escape = Assert.IsType<ScrollCaptureEscape>(ScrollCaptureEscape.TryClaim(window,
            () => { cancels++; releasedBeforeCancel = released.Count == 1; },
            handle => { registered.Add(handle); return true; }, released.Add));

        Assert.Equal([window.Handle], registered);
        Assert.True(escape.IsClaimed);
        Assert.True(window.Post(WmHotkey, new IntPtr(ScrollCaptureEscape.HotkeyId), IntPtr.Zero));
        Assert.True(window.Post(WmHotkey, new IntPtr(ScrollCaptureEscape.HotkeyId), IntPtr.Zero));
        Pump();

        Assert.Equal(1, cancels);
        Assert.True(releasedBeforeCancel);
        Assert.Equal([window.Handle], released);
        Assert.False(escape.IsClaimed);
    });

    [Fact]
    public void OtherHotkeysOnTheMessageWindowDoNotCancel() => StaTestHost.Run(() =>
    {
        using var window = new NativeMessageWindow();
        int cancels = 0;
        using ScrollCaptureEscape escape = ScrollCaptureEscape.TryClaim(window, () => cancels++,
            _ => true, _ => { })!;

        // 0x4201 is a global capture command id; 0x7102 is the quick card's Alt+Q.
        Assert.True(window.Post(WmHotkey, new IntPtr(0x4201), IntPtr.Zero));
        Assert.True(window.Post(WmHotkey, new IntPtr(0x7102), IntPtr.Zero));
        Assert.True(window.Post(0x0400, new IntPtr(ScrollCaptureEscape.HotkeyId), IntPtr.Zero));
        Pump();

        Assert.Equal(0, cancels);
        Assert.True(escape.IsClaimed);
    });

    [Fact]
    public void TheEndOfTheCaptureReleasesTheKeyOnceAndLaterEscIsIgnored() => StaTestHost.Run(() =>
    {
        using var window = new NativeMessageWindow();
        int releases = 0;
        int cancels = 0;
        ScrollCaptureEscape escape = ScrollCaptureEscape.TryClaim(window, () => cancels++,
            _ => true, _ => releases++)!;

        escape.Dispose();
        escape.Dispose();
        Assert.True(window.Post(WmHotkey, new IntPtr(ScrollCaptureEscape.HotkeyId), IntPtr.Zero));
        Pump();

        Assert.Equal(1, releases);
        Assert.Equal(0, cancels);
        Assert.False(escape.IsClaimed);
    });

    [Fact]
    public void AnEscOwnedElsewhereOrNoMessageWindowLeavesOnlyTheTrayCommand() => StaTestHost.Run(() =>
    {
        using var window = new NativeMessageWindow();
        int releases = 0;

        Assert.Null(ScrollCaptureEscape.TryClaim(window, () => { }, _ => false, _ => releases++));
        Assert.Null(ScrollCaptureEscape.TryClaim(null, () => { }, _ => true, _ => releases++));
        Assert.Equal(0, releases);
    });

    [Fact]
    public void TheTestAssemblyNeverClaimsTheUsersEscKey() => StaTestHost.Run(() =>
    {
        // SystemEscapeHotkeyGuard suppresses the default RegisterHotKey seam for this assembly.
        using var window = new NativeMessageWindow();
        Assert.True(NativeCaptureOverlay.SuppressSystemEscapeHotkey);
        Assert.Null(ScrollCaptureEscape.TryClaim(window, () => { }));
    });

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
