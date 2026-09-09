using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.Platform.Interop;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class NativeShellLifecycleTests
{
    [Fact]
    public void MessageWindow_IsHiddenTopLevel_AndReceivesBroadcast() => StaTestHost.Run(() =>
    {
        using var window = new NativeMessageWindow();
        bool enumerated = false;
        NativeMethods.EnumWindows((handle, _) =>
        {
            enumerated |= handle == window.Handle;
            return true;
        }, IntPtr.Zero);
        Assert.True(enumerated);
        Assert.False(NativeMethods.IsWindowVisible(window.Handle));
        uint message = NativeMethods.RegisterWindowMessage("MyCapture.Test." + Guid.NewGuid());
        bool received = false;
        window.MessageReceived += (_, args) => received |= args.Message == message;
        Assert.True(NativeMethods.PostMessage(new IntPtr(0xffff), message, IntPtr.Zero, IntPtr.Zero));
        Pump();
        Assert.True(received);
    });

    [Theory]
    [InlineData(NativeMethods.NIN_SELECT)]
    [InlineData(NativeMethods.WM_LBUTTONDBLCLK)]
    public void TrayActivation_ReturnsFromNativeCallbackBeforeOpeningGallery(int notification) => StaTestHost.Run(() =>
    {
        using var window = new NativeMessageWindow();
        string root = Path.Combine(AppContext.BaseDirectory, "Assets");
        using var tray = new TrayIconService(window,
            new TrayIconAssets(Path.Combine(root, "tray-idle.ico"), Path.Combine(root, "tray-capturing.ico"),
                Path.Combine(root, "tray-busy.ico"), Path.Combine(root, "tray-error.ico")),
            NullLogger<TrayIconService>.Instance);
        bool activated = false;
        bool returnedBeforeActivation = false;
        tray.GalleryRequested += (_, _) => activated = true;
        window.MessageReceived += (_, args) =>
        {
            if (args.Message == NativeMethods.NOTIFYICON_CALLBACK_MESSAGE)
            {
                returnedBeforeActivation = !activated;
            }
        };
        Assert.True(window.Post(NativeMethods.NOTIFYICON_CALLBACK_MESSAGE, IntPtr.Zero,
            new IntPtr(notification)));
        Pump();
        Assert.True(returnedBeforeActivation);
        Assert.True(activated);
    });

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
