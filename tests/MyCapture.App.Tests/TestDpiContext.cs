using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Aligns a fixture's thread, HWND and WPF coordinates without changing process or display settings.</summary>
internal sealed class TestDpiContext : IDisposable
{
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private nint _previous;

    private TestDpiContext(nint context)
    {
        // This changes only the current test thread. Each HWND retains the context in
        // which it was created; the process and the user's display settings are untouched.
        _previous = SetThreadDpiAwarenessContext(context);
        if (_previous == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static TestDpiContext At96Dpi() => new(-1 /* DPI_AWARENESS_CONTEXT_UNAWARE */);

    internal static TestDpiContext AtPerMonitorDpi() => new(-4 /* DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 */);

    internal static void Initialize96DpiWindow(Window window) => InitializeWindow(window, 96);

    internal static void InitializeMonitorDpiWindow(Window window, uint monitorDpi) => InitializeWindow(window, monitorDpi);

    private static void InitializeWindow(Window window, uint expectedDpi)
    {
        Assert.Equal(nint.Zero, new WindowInteropHelper(window).Handle);
        Assert.True(expectedDpi > 0);
        double scale = expectedDpi / 96d;
        // Window.Content and button/template content can still be separate visual roots.
        // Each root needs its own old/new DPI notification before it joins the HWND tree;
        // changing only Window does not reach these constructor-created subtrees.
        foreach (Visual root in FixtureNodes(window).OfType<Visual>().Reverse())
            if (!ReferenceEquals(root, window) && VisualTreeHelper.GetParent(root) is null)
                VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        // WPF's default visual DPI may already have been initialized by another test.
        // Set this root before placement/measurement, rather than repairing its layout
        // after Show has already exercised the product's SourceInitialized sizing.
        VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
        window.SourceInitialized += OnSourceInitialized;

        void OnSourceInitialized(object? sender, EventArgs args)
        {
            window.SourceInitialized -= OnSourceInitialized;
            // This event is raised by base.OnSourceInitialized, before an editor's sizing
            // override resumes. Some WPF hosts propagate their cached DPI at HWND attachment.
            // Always propagate to the now-attached visuals, even when Window itself already
            // has this DPI. The detached roots above received their cache notifications first.
            VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
            AssertDpi(window, expectedDpi);
        }
    }

    internal static void Assert96Dpi(Window window) => AssertDpi(window, 96);

    internal static void AssertMonitorDpi(Window window, uint monitorDpi) => AssertDpi(window, monitorDpi);

    private static void AssertDpi(Window window, uint expectedDpi)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        Assert.NotEqual(nint.Zero, hwnd);
        Assert.Equal(expectedDpi, GetDpiForWindow(hwnd));
        double scale = expectedDpi / 96d;
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        Assert.Equal(scale, dpi.DpiScaleX);
        Assert.Equal(scale, dpi.DpiScaleY);
        HwndSource source = HwndSource.FromHwnd(hwnd)!;
        // A visual flag alone is insufficient for native-frame and exact-pixel tests.
        Assert.Equal(new Matrix(scale, 0, 0, scale, 0, 0), source.CompositionTarget.TransformToDevice);
        Assert.Equal(new Matrix(1 / scale, 0, 0, 1 / scale, 0, 0), source.CompositionTarget.TransformFromDevice);
        AssertVisualDpi(window, scale);
    }

    private static void AssertVisualDpi(Visual visual, double scale)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(visual);
        Assert.True(dpi.DpiScaleX == scale && dpi.DpiScaleY == scale,
            $"{visual.GetType().FullName} has DPI {dpi.DpiScaleX:R}/{dpi.DpiScaleY:R}; expected {scale:R}.");
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(visual); index++)
            if (VisualTreeHelper.GetChild(visual, index) is Visual child) AssertVisualDpi(child, scale);
    }

    private static IEnumerable<DependencyObject> FixtureNodes(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(root);
        while (pending.TryPop(out DependencyObject? current))
        {
            if (current is null || !visited.Add(current)) continue;
            yield return current;
            foreach (object child in LogicalTreeHelper.GetChildren(current))
                if (child is DependencyObject dependency) pending.Push(dependency);
            if (current is Visual)
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                    pending.Push(VisualTreeHelper.GetChild(current, index));
        }
    }

    public void Dispose()
    {
        if (_previous == 0) return;
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("The DPI context must be restored on its owning test thread.");
        nint previous = _previous;
        if (SetThreadDpiAwarenessContext(previous) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        _previous = 0;
        Assert.True(AreDpiAwarenessContextsEqual(previous, GetThreadDpiAwarenessContext()),
            "The original test thread DPI context was not restored.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
