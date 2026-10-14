using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MyCapture.App.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CaptureWindowVisibilityTests
{
    [Fact]
    public void HidesNativeWindowAndCanBeShownAndHiddenAgain() => StaTestHost.Run(() =>
    {
        var window = new Window
        {
            Width = 100, Height = 100, Left = -10000, Top = -10000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false, ShowInTaskbar = false,
        };
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                window.Show();
                IntPtr handle = new WindowInteropHelper(window).Handle;
                Assert.True(IsWindowVisible(handle));
                Task? pending = null;
                _ = window.Dispatcher.InvokeAsync(() => pending = CaptureWindowVisibility.HideAndConfirmAsync(window));
                var elapsed = Stopwatch.StartNew();
                while (pending is null || !pending.IsCompleted)
                {
                    Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
                    var frame = new DispatcherFrame();
                    _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                }
                pending.GetAwaiter().GetResult();
                Assert.False(window.IsVisible);
                Assert.False(IsWindowVisible(handle));
            }
        }
        finally { window.Close(); }
    });

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
