using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Queued input reaches its own window's element even when another window covers that window.</summary>
public sealed class QueuedMouseTests
{
    // A spot no other test uses: the cover would otherwise hide a concurrent test's window.
    private const double Left = -26000, Top = -16000, Width = 240, Height = 120;

    [Fact]
    public void ClicksReachTheButtonUnderAnotherThreadsWindow() => StaTestHost.Run(() =>
    {
        int clicks = 0;
        var target = new Button { Content = "Target" };
        target.Click += (_, _) => clicks++;
        Window window = CreateWindow(topmost: false);
        window.Content = target;
        window.Show();
        try
        {
            using var cover = Cover.Show();
            using var mouse = new QueuedMouse(window);
            mouse.Drain();
            // WPF's hit test after mouse activation asks the desktop for the window under the
            // point; here the cover, a window of another dispatcher, answers.
            Point center = target.PointToScreen(new Point(target.ActualWidth / 2, target.ActualHeight / 2));
            Assert.Equal(cover.Handle, WindowFromPoint(new NativePoint((int)center.X, (int)center.Y)));

            mouse.Click(target, new Point(target.ActualWidth / 4, target.ActualHeight / 2));
            Assert.Equal(1, clicks);
            Assert.Null(Mouse.Captured);
            // WPF's mouse deactivated after the first gesture, so the second activates it again.
            mouse.Click(target, new Point(target.ActualWidth * 3 / 4, target.ActualHeight / 2));
            Assert.Equal(2, clicks);
            Assert.Null(Mouse.Captured);
        }
        finally
        {
            window.Close();
        }
    });

    private static Window CreateWindow(bool topmost) => new()
    {
        WindowStartupLocation = WindowStartupLocation.Manual, Left = Left, Top = Top, Width = Width, Height = Height,
        WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = topmost,
        ShowActivated = false, ShowInTaskbar = false,
    };

    /// <summary>A topmost window over the test window, shown by its own WPF thread.</summary>
    private sealed class Cover : IDisposable
    {
        private readonly Thread _thread;
        private readonly Dispatcher _dispatcher;

        private Cover(Thread thread, Dispatcher dispatcher, nint handle)
        {
            _thread = thread;
            _dispatcher = dispatcher;
            Handle = handle;
        }

        public nint Handle { get; }

        public static Cover Show()
        {
            var shown = new TaskCompletionSource<(Dispatcher Dispatcher, nint Handle)>(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread thread = WpfTestThread.Create(() =>
            {
                try
                {
                    Window cover = CreateWindow(topmost: true);
                    cover.Show();
                    shown.SetResult((cover.Dispatcher, new WindowInteropHelper(cover).Handle));
                }
                catch (Exception ex)
                {
                    shown.SetException(ex);
                    return;
                }

                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(shown.Task.Wait(TimeSpan.FromSeconds(30)), "The cover window did not show.");
            (Dispatcher dispatcher, nint handle) = shown.Task.Result;
            return new Cover(thread, dispatcher, handle);
        }

        public void Dispose()
        {
            _dispatcher.InvokeShutdown();
            _thread.Join();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
}
