using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.Platform.Interop;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ResidentIdleReleaseWindowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreRunsBeforeNativeVisibilityIncludingHiddenWindowReshow(bool keepAlive) => StaTestHost.Run(() =>
    {
        RenderMode original = RenderOptions.ProcessRenderMode;
        using var shell = new NativeMessageWindow(separateRenderKeepAlive: keepAlive);
        var window = new Window { Width = 40, Height = 40, ShowActivated = false, ShowInTaskbar = false };
        IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
        TimeSpan now = TimeSpan.Zero;
        List<bool> visibleAtRestore = [];
        var controller = new ResidentRenderIdleController(TimeSpan.FromSeconds(1), () => now,
            () =>
            {
                if (keepAlive) shell.ReleaseRenderKeepAlive();
                else RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            },
            () =>
            {
                visibleAtRestore.Add(NativeMethods.IsWindowVisible(handle));
                if (keepAlive) shell.RestoreRenderKeepAlive();
                else RenderOptions.ProcessRenderMode = original;
            });
        using var observer = new ResidentWindowActivity(controller.BeforeUse);
        try
        {
            for (int cycle = 1; cycle <= 2; cycle++)
            {
                now = TimeSpan.FromSeconds(cycle);
                controller.Poll(false, TimeSpan.FromSeconds(1));
                Assert.True(controller.IsReleased);
                window.Show();
                Assert.True(window.IsVisible);
                Assert.False(controller.IsReleased);
                Assert.Equal(cycle, controller.RestoreCount);
                window.Hide();
            }
            Assert.Equal(new[] { false, false }, visibleAtRestore);
            Assert.Null(observer.CallbackFailure);
            Assert.Null(shell.CallbackFailure);
        }
        finally
        {
            window.Close();
            RenderOptions.ProcessRenderMode = original;
        }
    });

    [Fact]
    public void KeepAliveReleasePreservesNamedTopLevelWindowAndQueuedTrayActivation() => StaTestHost.Run(() =>
    {
        using var window = new NativeMessageWindow(separateRenderKeepAlive: true);
        IntPtr handle = window.Handle;
        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        using var tray = new TrayIconService(window,
            new TrayIconAssets(Path.Combine(assets, "tray-idle.ico"), Path.Combine(assets, "tray-capturing.ico"),
                Path.Combine(assets, "tray-busy.ico"), Path.Combine(assets, "tray-error.ico")),
            NullLogger<TrayIconService>.Instance);
        int activated = 0;
        bool returnedBeforeActivation = false;
        tray.GalleryRequested += (_, _) => activated++;
        window.MessageReceived += (_, message) =>
        {
            if (message.Message == NativeMethods.NOTIFYICON_CALLBACK_MESSAGE)
                returnedBeforeActivation = activated == 0;
        };
        Assert.True(window.Post(NativeMethods.NOTIFYICON_CALLBACK_MESSAGE, IntPtr.Zero, new IntPtr(NativeMethods.NIN_SELECT)));
        window.ReleaseRenderKeepAlive();
        Assert.False(window.IsRenderKeepAlivePresent);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Pump();
        Assert.Equal(1, activated);
        Assert.True(returnedBeforeActivation);
        Assert.Equal(handle, window.Handle);
        var title = new StringBuilder(128);
        NativeMethods.GetWindowText(handle, title, title.Capacity);
        Assert.Equal("MyCapture.NativeMessageWindow", title.ToString());
        var className = new StringBuilder(128);
        NativeMethods.GetClassName(handle, className, className.Capacity);
        Assert.Equal(handle, NativeMethods.FindWindow(className.ToString(), title.ToString()));
        bool enumerated = false;
        NativeMethods.EnumWindows((candidate, _) => { enumerated |= candidate == handle; return true; }, IntPtr.Zero);
        Assert.True(enumerated);
        Assert.False(NativeMethods.IsWindowVisible(handle));
        Assert.Null(HwndSource.FromHwnd(handle));

        bool restarted = false;
        window.MessageReceived += (_, message) => restarted |= message.Message == window.TaskbarCreatedMessage;
        Assert.True(window.Post(window.TaskbarCreatedMessage, IntPtr.Zero, IntPtr.Zero));
        uint broadcast = NativeMethods.RegisterWindowMessage("MyCapture.Test.IdleBroadcast." + Guid.NewGuid());
        bool broadcastReceived = false;
        window.MessageReceived += (_, message) => broadcastReceived |= message.Message == broadcast;
        // A private broadcast tests the top-level route without telling other apps Explorer restarted.
        Assert.True(NativeMethods.PostMessage(new IntPtr(0xffff), broadcast, IntPtr.Zero, IntPtr.Zero));
        Pump();
        Assert.True(restarted);
        Assert.True(broadcastReceived);
        window.RestoreRenderKeepAlive();
        Assert.True(window.IsRenderKeepAlivePresent);
        Assert.Equal(handle, window.Handle);
        Assert.True(window.Post(NativeMethods.NOTIFYICON_CALLBACK_MESSAGE, IntPtr.Zero, new IntPtr(NativeMethods.NIN_KEYSELECT)));
        Pump();
        Assert.Equal(2, activated);
        Assert.Null(window.CallbackFailure);
    });

    [Fact]
    public void DefaultShellStillUsesOriginalWpfSource() => StaTestHost.Run(() =>
    {
        using var window = new NativeMessageWindow();
        Assert.False(window.HasSeparateRenderKeepAlive);
        Assert.NotNull(HwndSource.FromHwnd(window.Handle));
        Assert.Throws<InvalidOperationException>(window.ReleaseRenderKeepAlive);
    });

    [Fact]
    public void NativeSubscriberFailureIsLatchedWithoutEscapingUser32() => StaTestHost.Run(() =>
    {
        using var window = new NativeMessageWindow(separateRenderKeepAlive: true);
        uint message = NativeMethods.RegisterWindowMessage("MyCapture.Test.CallbackFailure." + Guid.NewGuid());
        window.MessageReceived += (_, args) =>
        {
            if (args.Message == message) throw new InvalidOperationException("subscriber failure");
        };
        Assert.True(window.Post(message, IntPtr.Zero, IntPtr.Zero));
        Pump();
        Assert.IsType<InvalidOperationException>(window.CallbackFailure);
    });

    [Fact]
    public void NativePresentationFailureIsLatchedWithoutEscapingUser32() => StaTestHost.Run(() =>
    {
        using var observer = new ResidentWindowActivity(() => throw new InvalidOperationException("restore failure"));
        var window = new Window { Width = 40, Height = 40, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            Assert.IsType<InvalidOperationException>(observer.CallbackFailure);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void NestedNativeShowCannotBypassTheTransitionFailureLatch() => StaTestHost.Run(() =>
    {
        var outer = new Window { Width = 40, Height = 40, ShowActivated = false, ShowInTaskbar = false };
        var nested = new Window { Width = 40, Height = 40, ShowActivated = false, ShowInTaskbar = false };
        bool attempted = false;
        using var observer = new ResidentWindowActivity(() =>
        {
            if (attempted) return;
            attempted = true;
            nested.Show();
        });
        try
        {
            outer.Show();
            Assert.True(attempted);
            Assert.IsType<InvalidOperationException>(observer.CallbackFailure);
        }
        finally
        {
            nested.Close();
            outer.Close();
        }
    });

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
