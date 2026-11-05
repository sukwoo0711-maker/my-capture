using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.Platform.Display;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class TestHostDpiCanaryTests(ITestOutputHelper output)
{
    [PerMonitorTestHostFact]
    public void LateTemplatesAndWrappedTextStayAt96ThenNativeFrameUsesMonitorDpi() => StaTestHost.Run(() =>
    {
        Assert.Equal("PerMonitorV2", Environment.GetEnvironmentVariable("MYCAPTURE_TESTHOST_DPI_MODE"));
        Assert.Equal(Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            Environment.GetEnvironmentVariable("MYCAPTURE_TESTHOST_DPI_INITIALIZED_PID"));
        Assert.Equal("testhost", Assembly.GetEntryAssembly()?.GetName().Name);
        Assert.True(AreDpiAwarenessContextsEqual(GetDpiAwarenessContextForProcess(GetCurrentProcess()), -4));
        Assert.True(AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), -4));

        // First establish the per-monitor process's real WPF HWND path, before any
        // synthetic scope. This also reproduces a preceding native-window test.
        VerifyNativeWindow("native before 96");
        VerifyLate96DpiContent();
        VerifyNativeWindow("native after 96");
        Assert.True(AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), -4));
    });

    private void VerifyLate96DpiContent()
    {
        using var context = TestDpiContext.At96Dpi();
        var panel = new Grid();
        var window = new Window
        {
            Width = 620, Height = 300, MinWidth = 1, MinHeight = 1,
            Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            UseLayoutRounding = true, Content = panel,
        };
        try
        {
            TestDpiContext.Initialize96DpiWindow(window);
            window.Show();
            Settle(window);
            TestDpiContext.Assert96Dpi(window);

            // These template and drawing visuals did not exist at SourceInitialized.
            var text = new TextBox
            {
                Text = string.Join(' ', Enumerable.Repeat("alpha beta gamma delta", 16)),
                TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                FontSize = 16, Padding = new Thickness(8),
            };
            panel.Children.Add(text);
            Settle(window);
            Assert.NotNull(text.Template);
            Assert.NotEmpty(Visuals(text).OfType<Grid>());
            Assert.Contains(Visuals(text), visual => visual.GetType().Name == "TextBoxLineDrawingVisual");
            TestDpiContext.Assert96Dpi(window);
            int wideLines = text.LineCount;
            Assert.True(wideLines > 1);

            window.Width = 280;
            Settle(window);
            Assert.Equal(280, window.ActualWidth);
            Assert.True(text.LineCount > wideLines, $"Expected wrapping: wide={wideLines}, narrow={text.LineCount}.");
            TestDpiContext.Assert96Dpi(window);
            text.AppendText("\nA newly formatted line after the resize.");
            Settle(window);
            TestDpiContext.Assert96Dpi(window);
            output.WriteLine($"pid={Environment.ProcessId}; 96 DPI late-template/wrap: lines={wideLines}->{text.LineCount}; " +
                $"visuals={Visuals(window).Count()}; full visual gate passed.");
        }
        finally { window.Close(); }
    }

    private void VerifyNativeWindow(string phase)
    {
        using var context = TestDpiContext.AtPerMonitorDpi();
        MonitorInfo monitor = MonitorEnumerator.GetPrimary();
        string? calibratedDpi = Environment.GetEnvironmentVariable("MYCAPTURE_TESTHOST_DPI_EXPECTED_MONITOR");
        if (calibratedDpi is not null)
            Assert.Equal(uint.Parse(calibratedDpi, CultureInfo.InvariantCulture), monitor.Dpi);
        double scale = monitor.ScaleFactor;
        var window = new Window
        {
            Width = 640, Height = 320, WindowStyle = WindowStyle.SingleBorderWindow, ResizeMode = ResizeMode.CanResize,
            Left = (monitor.WorkArea.X + 40) / scale, Top = (monitor.WorkArea.Y + 40) / scale,
            ShowActivated = false, ShowInTaskbar = false, UseLayoutRounding = true,
            Content = new Grid { Children = { new TextBlock { Text = "Native DPI canary", Margin = new Thickness(16) } } },
        };
        try
        {
            TestDpiContext.InitializeMonitorDpiWindow(window, monitor.Dpi);
            window.Show();
            Settle(window);
            TestDpiContext.AssertMonitorDpi(window, monitor.Dpi);
            nint hwnd = new WindowInteropHelper(window).Handle;
            Check(GetWindowRect(hwnd, out NativeRect outer));
            Check(GetClientRect(hwnd, out NativeRect client));
            var predicted = client;
            uint style = unchecked((uint)GetWindowLong(hwnd, -16));
            uint extendedStyle = unchecked((uint)GetWindowLong(hwnd, -20));
            Check(AdjustWindowRectExForDpi(ref predicted, style, false, extendedStyle, monitor.Dpi));
            Assert.Equal(predicted.Right - predicted.Left, outer.Right - outer.Left);
            Assert.Equal(predicted.Bottom - predicted.Top, outer.Bottom - outer.Top);
            Assert.Equal((int)Math.Round(window.Width * scale), outer.Right - outer.Left);
            Assert.Equal((int)Math.Round(window.Height * scale), outer.Bottom - outer.Top);
            output.WriteLine($"pid={Environment.ProcessId}; {phase}: DPI={monitor.Dpi}; " +
                $"outer={outer.Right - outer.Left}x{outer.Bottom - outer.Top}; " +
                $"client={client.Right}x{client.Bottom}; native frame and full visual gate passed.");
        }
        finally { window.Close(); }
    }

    private static IEnumerable<Visual> Visuals(Visual root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (VisualTreeHelper.GetChild(root, index) is Visual child)
                foreach (Visual visual in Visuals(child)) yield return visual;
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(window.UpdateLayout));
        window.UpdateLayout();
    }

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static nint GetWindowLong(nint hwnd, int index) => IntPtr.Size == 8
        ? GetWindowLongPtrW(hwnd, index) : GetWindowLongW(hwnd, index);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetDpiAwarenessContextForProcess(nint process);
    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustWindowRectExForDpi(ref NativeRect rect, uint style,
        [MarshalAs(UnmanagedType.Bool)] bool menu, uint extendedStyle, uint dpi);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLongW(nint hwnd, int index);
}

internal sealed class PerMonitorTestHostFactAttribute : FactAttribute
{
    public PerMonitorTestHostFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_TESTHOST_DPI_MODE") is null)
            Skip = "Opt in with the TestHostDpiStartupHook runsettings to verify the PMv2 testhost.";
    }
}
