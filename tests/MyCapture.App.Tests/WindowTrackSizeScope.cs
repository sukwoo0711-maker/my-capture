using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MyCapture.App.Tests;

/// <summary>Keeps offscreen window fixtures independent of the runner's real desktop size.</summary>
internal sealed class WindowTrackSizeScope : IDisposable
{
    internal const string SmallDesktopEnvironmentVariable = "MYCAPTURE_TEST_SMALL_DESKTOP";
    internal static readonly Size SmallDesktopMaximum = new(1044, 788);
    private readonly Window _window;
    private HwndSource? _source;
    private readonly Size _minimumMaximum;
    private readonly Size? _desktopMaximum;
    private readonly Size _requested;
    internal int MessageCount { get; private set; }

    // Sizes here are native physical pixels, including the window frame. Passing a zero
    // minimum retains the supplied desktop limit for tests of the real constrained layout.
    internal WindowTrackSizeScope(Window window, Size minimumMaximum, Size? desktopMaximum = null)
    {
        _window = window;
        _minimumMaximum = minimumMaximum;
        _desktopMaximum = desktopMaximum ??
            (Environment.GetEnvironmentVariable(SmallDesktopEnvironmentVariable) == "1" ? SmallDesktopMaximum : null);
        _requested = new Size(window.Width, window.Height);
        _window.SourceInitialized += Attach;
        _window.Closed += OnClosed;
    }

    private void Attach(object? sender, EventArgs args)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle)!;
        _source.AddHook(Filter);
        // HWND creation has already consulted the real desktop. Negotiate the original
        // request again with the fixture's bounds, before AnnotationEditorWindow performs
        // its SourceInitialized sizing. No guessed size or extra visible frame is involved.
        DpiScale dpi = VisualTreeHelper.GetDpi(_window);
        if (!SetWindowPos(_source.Handle, 0, 0, 0, (int)Math.Round(_requested.Width * dpi.DpiScaleX),
            (int)Math.Round(_requested.Height * dpi.DpiScaleY),
            0x0002 | 0x0004 | 0x0010 | 0x0020 /* NOMOVE | NOZORDER | NOACTIVATE | FRAMECHANGED */))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private nint Filter(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0024 /* WM_GETMINMAXINFO */)
        {
            MessageCount++;
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            // The optional 1024x768 desktop is injected before normalization, so running
            // the same suite with it enabled proves the simulated monitor wins on CI too.
            if (_desktopMaximum is { } desktop)
                info.MaxTrackSize = new NativePoint((int)desktop.Width, (int)desktop.Height);
            info.MaxTrackSize.X = Math.Max(info.MaxTrackSize.X, (int)Math.Ceiling(_minimumMaximum.Width));
            info.MaxTrackSize.Y = Math.Max(info.MaxTrackSize.Y, (int)Math.Ceiling(_minimumMaximum.Height));
            Marshal.StructureToPtr(info, lParam, false);
            // Let WPF consume these bounds and update its own tracking-size cache.
        }
        return 0;
    }

    public void Dispose()
    {
        _window.SourceInitialized -= Attach;
        _window.Closed -= OnClosed;
        _source?.RemoveHook(Filter);
        _source = null;
    }

    private void OnClosed(object? sender, EventArgs args) => Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y) { public int X = x; public int Y = y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

}
