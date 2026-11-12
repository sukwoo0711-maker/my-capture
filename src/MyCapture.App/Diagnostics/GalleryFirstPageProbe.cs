using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Gallery;
using MyCapture.Platform.Display;

namespace MyCapture.App.Diagnostics;

/// <summary>Shared first-page predicate and opt-in timestamp observations; no product rendering changes.</summary>
internal static class GalleryFirstPageProbe
{
    internal static bool Ready(GalleryWindow window, GalleryViewModel viewModel, int queueCount, out int visible)
    {
        visible = 0;
        if (!window.IsVisible || !window.IsLoaded || !window.IsEnabled || window.WindowState == WindowState.Minimized
            || !window.IsMeasureValid || !window.IsArrangeValid || window.ActualWidth <= 0 || window.ActualHeight <= 0)
            return false;
        // Screen coordinates are physical pixels, just like MonitorInfo.WorkArea.
        MonitorInfo monitor = MonitorEnumerator.GetPrimary();
        Point topLeft = window.PointToScreen(new Point());
        Point bottomRight = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
        Rect work = new(monitor.WorkArea.X, monitor.WorkArea.Y, monitor.WorkArea.Width, monitor.WorkArea.Height);
        if (!work.Contains(new Rect(topLeft, bottomRight)))
            throw new InvalidOperationException("The library client bounds are not fully inside the primary monitor work area.");
        var rows = (ListBox)window.FindName("RowsList");
        GalleryThumbnailImage[] images = LibraryVisualDescendants(rows).OfType<GalleryThumbnailImage>()
            // Source=null may leave Image itself at 0x0. The template's fixed preview slot,
            // not the bitmap's current size, determines whether a pending image is on page.
            .Where(image => LibraryVisibleArea(LibraryPreviewSlot(image), window, requireScrollViewport: true)).ToArray();
        visible = images.Length;
        if (queueCount == 0)
            return viewModel!.QueueIsEmpty && viewModel.IsEmpty && viewModel.VisibleCount == 0
                && visible == 0 && LibraryVisibleArea((FrameworkElement)window.FindName("EmptyStateLabel"), window, false);
        return !viewModel!.IsEmpty && visible > 0
            && images.All(image => image.IsVisible && image.ActualWidth > 0 && image.ActualHeight > 0
                && image.Source is not null && !image.IsFading && image.Opacity == 1);
    }

    internal static FrameworkElement LibraryPreviewSlot(GalleryThumbnailImage image)
    {
        for (DependencyObject? node = VisualTreeHelper.GetParent(image); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement { Name: "TilePreview" } preview) return preview;
        throw new InvalidOperationException("The gallery thumbnail has no TilePreview layout slot.");
    }

    internal static bool LibraryVisibleArea(FrameworkElement element, Window window, bool requireScrollViewport)
    {
        if (!element.IsVisible || !element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        Rect visible = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        visible.Intersect(new Rect(window.RenderSize));
        bool hasScrollViewport = false;
        for (DependencyObject? node = element; node is not null && node != window; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not UIElement ancestor) continue;
            // Opacity is readiness, not geometry: a transparent arrival fade still occupies
            // the first page and must not disappear from the set that is checked below.
            if (!ancestor.IsVisible) return false;
            GeneralTransform transform = ancestor.TransformToAncestor(window);
            if (ancestor is ScrollContentPresenter) hasScrollViewport = true;
            if (ancestor.ClipToBounds || ancestor is ScrollContentPresenter)
                visible.Intersect(transform.TransformBounds(new Rect(ancestor.RenderSize)));
            if (ancestor.Clip is { } clip) visible.Intersect(transform.TransformBounds(clip.Bounds));
            if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return false;
        }
        return (!requireScrollViewport || hasScrollViewport) && !visible.IsEmpty && visible.Width > 0 && visible.Height > 0;
    }

    internal static IEnumerable<DependencyObject> LibraryVisualDescendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject descendant in LibraryVisualDescendants(child)) yield return descendant;
        }
    }

    internal const string Scenario = "library-first-page";
    internal const string CountSwitch = "--library-first-page-count";
    internal const string InputShaSwitch = "--library-first-page-input-sha256";
    internal const string Revision = "library-first-page-v1";

    internal static bool Requested(IReadOnlyList<string> scenarios) =>
        scenarios.Contains(Scenario, StringComparer.OrdinalIgnoreCase);

    internal static int? ValidateOptions(IReadOnlyList<string> scenarios, bool quick, bool audit,
        IReadOnlySet<string> variants, bool noIndexing, bool software, string[] arguments)
    {
        if (!Requested(scenarios)) return null;
        if (scenarios.Count != 1 || quick || audit || variants.Count != 0 || noIndexing || software)
            throw new ArgumentException("Library first-page requires one scenario and production options.");
        string count = ReadSingle(arguments, CountSwitch);
        if (count is not ("0" or "1000")) throw new ArgumentException("Library count must be exactly 0 or 1000.");
        string digest = ReadSingle(arguments, InputShaSwitch);
        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit)) throw new ArgumentException("An input manifest SHA256 is required.");
        return int.Parse(count, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static string ReadSingle(string[] arguments, string name)
    {
        int[] indices = arguments.Select((value, index) => (value, index))
            .Where(pair => pair.value.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.index).ToArray();
        if (indices.Length != 1 || indices[0] + 1 >= arguments.Length || arguments[indices[0] + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Exactly one value is required for " + name);
        return arguments[indices[0] + 1];
    }

    internal sealed record Beat(long Posted, long Ran);
    internal sealed record Measurement(long Started, long HandlerReturned, long Ready, long Input, long Rendering,
        int VisibleImages, int ReadinessChecks, long ReadinessTicks, int Width, int Height,
        double DpiX, double DpiY, int RenderingTier, bool HighResolutionTimer, int TimerFallbacks, Beat[] Beats);

    // Driver lives on a worker; every UI observation is an ordinary Input-priority callback.
    // There is no nested DispatcherFrame, synthetic input, explicit layout, render flush or GC.
    internal static Task<Measurement> MeasureAsync(Dispatcher dispatcher, Func<GalleryWindow?> window,
        Func<int> count, Action open) => Task.Run(() => Measure(dispatcher, window, count, open));

    private static Measurement Measure(Dispatcher dispatcher, Func<GalleryWindow?> getWindow, Func<int> count, Action open)
    {
        using var heartbeat = new Heartbeat(dispatcher);
        using var delay = new Delay();
        long started = 0, returned = 0, ready = 0, input = 0, rendering = 0, checkTicks = 0;
        int visible = 0, checks = 0;
        long readRender() => Interlocked.Read(ref rendering);
        bool Check()
        {
            long before = Stopwatch.GetTimestamp();
            try
            {
                checks++;
                return getWindow() is { DataContext: GalleryViewModel viewModel } current && Ready(current, viewModel, count(), out visible);
            }
            finally { checkTicks += Stopwatch.GetTimestamp() - before; }
        }
        EventHandler rendered = (_, _) =>
        {
            if (readRender() == 0 && Check()) Interlocked.Exchange(ref rendering, Stopwatch.GetTimestamp());
        };
        try
        {
            dispatcher.Invoke(() =>
            {
                if (getWindow() is not null) throw new InvalidOperationException("The gallery was constructed before its cold measurement.");
                started = Stopwatch.GetTimestamp();
                open();
                returned = Stopwatch.GetTimestamp();
            }, DispatcherPriority.Input);
            long deadline = Stopwatch.GetTimestamp();
            while (ready == 0 && Stopwatch.GetElapsedTime(deadline) < TimeSpan.FromSeconds(30))
            {
                dispatcher.Invoke(() => { if (Check()) ready = Stopwatch.GetTimestamp(); }, DispatcherPriority.Input);
                if (ready == 0) delay.Wait(16);
            }
            if (ready == 0) throw new TimeoutException("The library first page did not become ready.");
            dispatcher.Invoke(() =>
            {
                if (!Check()) throw new InvalidOperationException("The first page lost readiness before its input callback.");
                input = Stopwatch.GetTimestamp();
                CompositionTarget.Rendering += rendered;
            }, DispatcherPriority.Input);
            deadline = Stopwatch.GetTimestamp();
            while (readRender() == 0 && Stopwatch.GetElapsedTime(deadline) < TimeSpan.FromSeconds(30)) delay.Wait(16);
            if (readRender() == 0) throw new TimeoutException("No ready rendering callback was observed after input.");
            heartbeat.Stop();
            return dispatcher.Invoke(() =>
            {
                GalleryWindow current = getWindow() ?? throw new InvalidOperationException("Gallery disappeared.");
                DpiScale dpi = VisualTreeHelper.GetDpi(current);
                return new Measurement(started, returned, ready, input, readRender(), visible, checks, checkTicks,
                    (int)Math.Round(current.ActualWidth * dpi.DpiScaleX), (int)Math.Round(current.ActualHeight * dpi.DpiScaleY),
                    dpi.DpiScaleX, dpi.DpiScaleY, RenderCapability.Tier >> 16,
                    heartbeat.HighResolution, heartbeat.Fallbacks + delay.Fallbacks, heartbeat.Samples());
            }, DispatcherPriority.Input);
        }
        finally { dispatcher.Invoke(() => CompositionTarget.Rendering -= rendered, DispatcherPriority.Input); }
    }

    private sealed class Heartbeat : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _started = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<Beat> _beats = new();
        private volatile bool _stop;
        private Exception? _failure;
        internal bool HighResolution { get; private set; }
        internal int Fallbacks { get; private set; }

        internal Heartbeat(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Library first-page heartbeat", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            if (!_started.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Heartbeat did not start.");
            if (_failure is { } failure) throw new InvalidOperationException("Heartbeat initialization failed.", failure);
        }

        private void Loop()
        {
            try
            {
                using var delay = new Delay();
                HighResolution = delay.HighResolution;
                _started.Set();
                while (!_stop)
                {
                    delay.Wait(8);
                    long posted = Stopwatch.GetTimestamp();
                    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    {
                        _beats.Enqueue(new Beat(posted, Stopwatch.GetTimestamp()));
                        done.TrySetResult();
                    }));
                    while (!done.Task.Wait(10))
                        if (_stop && Stopwatch.GetElapsedTime(posted) > TimeSpan.FromSeconds(5))
                            throw new TimeoutException("The final heartbeat was not acknowledged.");
                }
                Fallbacks = delay.Fallbacks;
            }
            catch (Exception error) { _failure = error; _started.Set(); }
        }

        internal void Stop()
        {
            _stop = true;
            if (!_thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Heartbeat thread did not stop.");
            if (_failure is { } failure) throw new InvalidOperationException("Heartbeat failed.", failure);
        }
        internal Beat[] Samples() => _beats.ToArray();
        public void Dispose() => Stop();
    }

    private sealed class Delay : IDisposable
    {
        private nint _timer = CreateWaitableTimerExW(0, null, 0x00000002, 0x001F0003);
        internal bool HighResolution => _timer != 0;
        internal int Fallbacks { get; private set; }
        internal void Wait(double milliseconds)
        {
            long due = -(long)(milliseconds * 10_000);
            if (_timer == 0 || !SetWaitableTimer(_timer, ref due, 0, 0, 0, false))
            {
                Fallbacks++;
                Thread.Sleep((int)Math.Ceiling(milliseconds));
                return;
            }
            if (WaitForSingleObject(_timer, uint.MaxValue) != 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() { if (_timer != 0) { CloseHandle(_timer); _timer = 0; } }
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(nint timer, ref long due, int period, nint completion, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    }
}
