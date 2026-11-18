using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Platform.Interop;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class KeyboardFocusCueNativeIngressTests
{
    [Theory]
    [InlineData(NativeMethods.NIN_SELECT, false)]
    [InlineData(NativeMethods.NIN_KEYSELECT, true)]
    [InlineData(NativeMethods.WM_LBUTTONUP, false)]
    [InlineData(NativeMethods.WM_LBUTTONDBLCLK, false)]
    public void TrayReportsItsInputOriginInsideTheExistingQueuedAction(int notification, bool keyboard) => StaTestHost.Run(() =>
    {
        using var window = new NativeMessageWindow();
        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        using var tray = new TrayIconService(window,
            new TrayIconAssets(Path.Combine(assets, "tray-idle.ico"), Path.Combine(assets, "tray-capturing.ico"),
                Path.Combine(assets, "tray-busy.ico"), Path.Combine(assets, "tray-error.ico")),
            NullLogger<TrayIconService>.Instance);
        var events = new List<string>();
        tray.InputOriginChanged = value => events.Add(value ? "keyboard" : "pointer");
        tray.GalleryRequested += (_, _) => events.Add("gallery");
        window.MessageReceived += (_, args) =>
        {
            if (args.Message == NativeMethods.NOTIFYICON_CALLBACK_MESSAGE) Assert.Empty(events);
        };
        Assert.True(window.Post(NativeMethods.NOTIFYICON_CALLBACK_MESSAGE, 0, notification));
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Assert.Equal(new[] { keyboard ? "keyboard" : "pointer", "gallery" }, events);
    });

    [Theory]
    [InlineData(NativeMethods.WM_CONTEXTMENU, -1, true)]
    [InlineData(NativeMethods.WM_CONTEXTMENU, 200, false)]
    [InlineData(NativeMethods.WM_RBUTTONUP, -1, false)]
    public void ContextMenuUsesTheShellKeyboardSentinelOnly(int notification, int packedPoint, bool keyboard) =>
        Assert.Equal(keyboard, TrayIconService.IsKeyboardActivation(notification, packedPoint));
}
