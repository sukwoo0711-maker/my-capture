using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App.Tests;

/// <summary>Desktop pixels only. No PrintWindow, client rendering, pointer input or clipboard access.</summary>
internal sealed class NativeFeelDesktopCapture : IDisposable
{
    private readonly Window _backdrop;
    private readonly IntPtr _backdropHandle;
    internal MonitorInfo Monitor { get; }
    internal List<NativeFeelFrame> Frames { get; } = [];
    internal List<NativeFeelCase> Cases { get; } = [];
    private readonly string _output;
    private readonly string _theme;
    internal double Scale { get; }
    internal string ScaleTag => "scale" + (Scale * 100).ToString("0.##", CultureInfo.InvariantCulture);

    internal NativeFeelDesktopCapture(string output, string theme, double? requiredScale = null)
    {
        _output = output;
        _theme = theme;
        if (!Environment.UserInteractive || GetSystemMetrics(0) <= 0 || GetSystemMetrics(1) <= 0)
            throw new InvalidOperationException("An unlocked interactive Windows desktop is required.");
        Check(DwmIsCompositionEnabled(out bool enabled) == 0 && enabled, "Desktop composition is unavailable.");
        Monitor = MonitorEnumerator.GetAll().First(item => item.IsPrimary);
        _backdrop = CreateBackdrop(Monitor);
        try
        {
            _backdrop.Show();
            _backdropHandle = new WindowInteropHelper(_backdrop).Handle;
            int squareCorners = 1; // DWMWCP_DONOTROUND; no rounded holes at monitor corners.
            _ = DwmSetWindowAttribute(_backdropHandle, 33, ref squareCorners, sizeof(int));
            Check(SetWindowPos(_backdropHandle, new IntPtr(-1), (int)Monitor.Bounds.X, (int)Monitor.Bounds.Y,
                Monitor.PixelWidth, Monitor.PixelHeight, 0x0010), "Could not place the neutral backdrop.");
            Scale = GetDpiForWindow(_backdropHandle) / 96d;
            Check(Scale > 0, "Native monitor DPI is unavailable.");
            Check(requiredScale is null || Scale == requiredScale,
                $"The desktop is at native {Scale * 100:0.##}%; this run requires {requiredScale * 100:0.##}%. No display setting was changed.");
            Pump(100);
            AssertCovered(Monitor.Bounds);
        }
        catch { _backdrop.Close(); throw; }
    }

    internal static Window CreateBackdrop(MonitorInfo monitor)
    {
        var window = new Window
        {
            // A plain Window otherwise receives the product's implicit Window.Standard style.
            Style = null, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = false, Opacity = 1,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false,
            ShowActivated = false, Topmost = true, Background = new SolidColorBrush(Color.FromRgb(128, 128, 128)),
            Left = monitor.Bounds.Left / monitor.ScaleFactor, Top = monitor.Bounds.Top / monitor.ScaleFactor,
            Width = monitor.Bounds.Width / monitor.ScaleFactor, Height = monitor.Bounds.Height / monitor.ScaleFactor,
        };
        ModernWindowChrome.SetEnabled(window, false);
        FluidMotion.SetWindowEntrance(window, false);
        return window;
    }

    internal void Place(Window window, double? width = null, double? height = null)
    {
        window.Owner = _backdrop;
        window.ShowActivated = false;
        window.Topmost = true;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        double availableWidth = Math.Max(1, Monitor.WorkArea.Width / Scale - 100);
        double availableHeight = Math.Max(1, Monitor.WorkArea.Height / Scale - 100);
        // Preserve product minimum sizes. A screen too small is an explicit failed prerequisite.
        Check(window.MinWidth <= availableWidth && window.MinHeight <= availableHeight,
            "The monitor is too small for the product minimum size plus the shadow margin.");
        if (window.SizeToContent is SizeToContent.Manual or SizeToContent.Height)
            window.Width = Math.Clamp(width ?? (double.IsFinite(window.Width) ? window.Width : 600), Math.Max(1, window.MinWidth), availableWidth);
        if (window.SizeToContent is SizeToContent.Manual or SizeToContent.Width)
            window.Height = Math.Clamp(height ?? (double.IsFinite(window.Height) ? window.Height : 400), Math.Max(1, window.MinHeight), availableHeight);
        double w = double.IsFinite(window.Width) ? window.Width : 600;
        double h = double.IsFinite(window.Height) ? window.Height : 400;
        window.Left = Monitor.WorkArea.Left / Scale + (Monitor.WorkArea.Width / Scale - w) / 2;
        window.Top = Monitor.WorkArea.Top / Scale + (Monitor.WorkArea.Height / Scale - h) / 2;
    }

    internal RectD Bounds(Window window, double marginDip = 32)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        Check(handle != IntPtr.Zero && GetWindowRect(handle, out _), "The real window has no bounds.");
        Check(GetDpiForWindow(handle) / 96d == Scale, "The surface DPI differs from the backdrop monitor's native DPI.");
        DpiScale visualDpi = VisualTreeHelper.GetDpi(window);
        Matrix? device = HwndSource.FromHwnd(handle)?.CompositionTarget?.TransformToDevice;
        Check(visualDpi.DpiScaleX == Scale && visualDpi.DpiScaleY == Scale
            && device is { M11: var x, M22: var y } && x == Scale && y == Scale,
            "The WPF visual or composition scale differs from the native HWND DPI.");
        _ = GetWindowRect(handle, out NativeRect rect);
        RectD bounds = new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        Check(Contains(Monitor.Bounds, bounds), "The real window extends beyond the neutral backdrop.");
        RectD expanded = bounds.Inflate(marginDip * Scale);
        // A product card can sit against a monitor edge; only the on-screen part of its shadow exists.
        double left = Math.Max(expanded.Left, Monitor.Bounds.Left), top = Math.Max(expanded.Top, Monitor.Bounds.Top);
        return new RectD(left, top, Math.Min(expanded.Right, Monitor.Bounds.Right) - left,
            Math.Min(expanded.Bottom, Monitor.Bounds.Bottom) - top).ToPixelBounds();
    }

    internal void Still(string id, string group, Window window, string note = "")
    {
        Pump(350);
        Check(window.IsVisible, "The requested window did not remain visible for its still.");
        RectD area = Bounds(window);
        BitmapSource bitmap = Grab(area);
        RequireVisibleContent(bitmap);
        Save(id, group, "still", 0, 0, area, bitmap, note);
        Cases.Add(new(id, NativeFeelCoverage.SurfaceOf(id), id, _theme, Scale, "captured", note));
    }

    internal void AreaStill(string id, string group, RectD area, string note)
    {
        Pump(350);
        BitmapSource bitmap = Grab(area);
        RequireVisibleContent(bitmap);
        Save(id, group, "still", 0, 0, area, bitmap, note);
        Cases.Add(new(id, NativeFeelCoverage.SurfaceOf(id), id, _theme, Scale, "captured", note));
    }

    internal void Omit(string id, string reason) => Cases.Add(new(id, NativeFeelCoverage.SurfaceOf(id), id, _theme, Scale, "omitted", reason));

    internal void Sequence(string id, string group, RectD area, Action change, string note)
    {
        var samples = new List<(double Begin, double End, BitmapSource Image)>();
        var clock = Stopwatch.StartNew();
        double begin = clock.Elapsed.TotalMilliseconds;
        BitmapSource baseline = Grab(area);
        samples.Add((begin, clock.Elapsed.TotalMilliseconds, baseline));
        double trigger = clock.Elapsed.TotalMilliseconds;
        change();
        double triggerReturned = clock.Elapsed.TotalMilliseconds;
        // Keep sampling independent of PNG encoding. Missed deadlines stay missed; timestamps are actual.
        double deadline = trigger + 16;
        do
        {
            Pump(Math.Max(0, deadline - clock.Elapsed.TotalMilliseconds));
            begin = clock.Elapsed.TotalMilliseconds;
            BitmapSource bitmap = Grab(area);
            samples.Add((begin, clock.Elapsed.TotalMilliseconds, bitmap));
            // Advance the requested cadence from its previous deadline, not from capture completion.
            // An overdue slot pumps once and captures the next available composed frame immediately.
            deadline += 16;
        } while (clock.Elapsed.TotalMilliseconds - trigger < 500);
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            Save(id, group, "sequence", sample.Begin - trigger, sample.End - trigger, area, sample.Image,
                note, i, triggerReturned - trigger);
        }
        Cases.Add(new(id, NativeFeelCoverage.SurfaceOf(id), id, _theme, Scale, "captured", note));
    }

    private void Save(string id, string group, string kind, double begin, double end, RectD area,
        BitmapSource bitmap, string note, int? frame = null, double? triggerEndMs = null)
    {
        string file = $"{id}-{_theme}-{ScaleTag}" + (frame is null ? "" : $"-frame{frame:000}") + ".png";
        TestDiagnosticArtifacts.SavePng(_output, file, bitmap);
        Frames.Add(new(id, NativeFeelCoverage.SurfaceOf(id), id, group, _theme, kind, file, Scale, begin, end,
            triggerEndMs is null ? null : 0, triggerEndMs, DateTimeOffset.UtcNow,
            (int)area.Left, (int)area.Top, bitmap.PixelWidth, bitmap.PixelHeight, note));
    }

    private BitmapSource Grab(RectD area)
    {
        AssertCovered(area);
        Check(DwmFlush() == 0, "DWM did not finish composing.");
        IntPtr screen = GetDC(IntPtr.Zero);
        Check(screen != IntPtr.Zero, "Could not acquire the desktop DC.");
        IntPtr memory = IntPtr.Zero, bitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            Check(memory != IntPtr.Zero, "Could not create the capture DC.");
            bitmap = CreateCompatibleBitmap(screen, (int)area.Width, (int)area.Height);
            Check(bitmap != IntPtr.Zero, "Could not create the capture bitmap.");
            old = SelectObject(memory, bitmap);
            Check(old != IntPtr.Zero && old != new IntPtr(-1), "Could not select the capture bitmap.");
            // SRCCOPY | CAPTUREBLT includes composited layered windows; GDI does not draw the cursor.
            Check(BitBlt(memory, 0, 0, (int)area.Width, (int)area.Height, screen,
                (int)area.Left, (int)area.Top, 0x40CC0020), "Desktop pixel capture failed.");
            AssertCovered(area);
            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero,
                Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            int stride = source.PixelWidth * 4;
            var pixels = new byte[stride * source.PixelHeight];
            new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0).CopyPixels(pixels, stride, 0);
            BitmapSource result = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96 * Scale, 96 * Scale,
                PixelFormats.Bgr32, null, pixels, stride);
            result.Freeze();
            return result;
        }
        finally
        {
            if (old != IntPtr.Zero && old != new IntPtr(-1)) _ = SelectObject(memory, old);
            if (bitmap != IntPtr.Zero) _ = DeleteObject(bitmap);
            if (memory != IntPtr.Zero) _ = DeleteDC(memory);
            _ = ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private void AssertCovered(RectD area)
    {
        Check(IsCurrentInputDesktop(), "The test desktop is locked, inactive, or cannot be verified.");
        Check(_backdrop.Style is null && !ModernWindowChrome.GetEnabled(_backdrop)
            && !_backdrop.AllowsTransparency && _backdrop.Opacity == 1
            && _backdrop.Background is SolidColorBrush { Color: { A: 255, R: 128, G: 128, B: 128 } },
            "The neutral backdrop's opaque fill or style changed.");
        Check(Contains(Monitor.Bounds, area), "Capture rectangle extends outside the backdrop.");
        Check(IsWindowVisible(_backdropHandle) && !IsIconic(_backdropHandle), "The neutral backdrop is not visible.");
        Check(DwmGetWindowAttribute(_backdropHandle, 14, out int backdropCloaked, sizeof(int)) == 0 && backdropCloaked == 0,
            "The neutral backdrop is cloaked or cannot be verified.");
        Check(GetWindowRect(_backdropHandle, out NativeRect backdropBounds)
            && Contains(new RectD(backdropBounds.Left, backdropBounds.Top,
                backdropBounds.Right - backdropBounds.Left, backdropBounds.Bottom - backdropBounds.Top), area),
            "The actual neutral backdrop does not cover the capture rectangle.");
        bool backdropFound = false;
        string? error = null;
        _ = EnumWindows((handle, parameter) =>
        {
            if (handle == _backdropHandle) { backdropFound = true; return false; }
            if (!IsWindowVisible(handle) || IsIconic(handle)) return true;
            if (DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (!GetWindowRect(handle, out NativeRect rect)) { error = "Could not check a visible window."; return false; }
            RectD other = new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            if (!Intersects(area, other)) return true;
            _ = GetWindowThreadProcessId(handle, out uint process);
            if (process != Environment.ProcessId)
            {
                error = "Another application's window covers the evidence area. Close or move it and rerun.";
                return false;
            }
            if (!GetWindowDisplayAffinity(handle, out uint affinity) || affinity != 0)
            {
                error = "A test-owned surface excludes itself from screen capture; an existing opt-out is required.";
                return false;
            }
            return true;
        }, IntPtr.Zero);
        Check(error is null && backdropFound, error ?? "The neutral backdrop is not in the desktop window stack.");
    }

    private static bool IsCurrentInputDesktop()
    {
        IntPtr input = OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS, no switching or input rights.
        if (input == IntPtr.Zero) return false;
        try
        {
            IntPtr current = GetThreadDesktop(GetCurrentThreadId());
            var inputName = new StringBuilder(256);
            var currentName = new StringBuilder(256);
            return GetUserObjectInformation(input, 2, inputName, inputName.Capacity * sizeof(char), out _)
                && GetUserObjectInformation(current, 2, currentName, currentName.Capacity * sizeof(char), out _)
                && string.Equals(inputName.ToString(), currentName.ToString(), StringComparison.Ordinal);
        }
        finally { _ = CloseDesktop(input); }
    }

    private static void RequireVisibleContent(BitmapSource image)
    {
        int stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        bool different = false;
        for (int offset = 4; offset < pixels.Length; offset += 4)
            if (pixels[offset] != pixels[0] || pixels[offset + 1] != pixels[1] || pixels[offset + 2] != pixels[2])
            { different = true; break; }
        Check(different, "The desktop still is a uniform field, with no visible product content.");
    }

    internal static bool Contains(RectD outer, RectD inner) => !inner.IsEmpty && inner.Left >= outer.Left
        && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
    internal static bool Intersects(RectD first, RectD second) => first.Left < second.Right && first.Right > second.Left
        && first.Top < second.Bottom && first.Bottom > second.Top;
    internal static void Pump(double milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)) };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    internal static void ContactSheets(string output, IEnumerable<NativeFeelFrame> frames)
    {
        foreach (var group in frames.GroupBy(frame => (frame.Theme, frame.Group)))
        {
            // Include each still and five actual samples from each sequence, with their measured times.
            var selected = group.GroupBy(frame => frame.Id).SelectMany(scene => scene.First().Kind == "still" ? scene
                : scene.Where((_, index) => index == 0 || index == scene.Count() - 1
                    || index == scene.Count() / 4 || index == scene.Count() / 2 || index == scene.Count() * 3 / 4)).ToArray();
            const int columns = 3, cellWidth = 420, cellHeight = 320;
            int rows = (selected.Length + columns - 1) / columns;
            var visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, columns * cellWidth, rows * cellHeight));
                for (int index = 0; index < selected.Length; index++)
                {
                    NativeFeelFrame item = selected[index];
                    var image = new BitmapImage();
                    image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(TestDiagnosticArtifacts.Confined(System.IO.Path.Combine(output, item.File)));
                    image.EndInit(); image.Freeze();
                    double x = index % columns * cellWidth, y = index / columns * cellHeight;
                    double fit = Math.Min((cellWidth - 20d) / image.PixelWidth, (cellHeight - 62d) / image.PixelHeight);
                    drawing.DrawImage(image, new Rect(x + 10, y + 10, image.PixelWidth * fit, image.PixelHeight * fit));
                    var label = new FormattedText($"{item.Id}\n{item.Theme} / {item.Scale * 100:0.##}% / {item.BeginMs:0.0} ms",
                        CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.Black, 1)
                    { MaxTextWidth = cellWidth - 20, MaxTextHeight = 48 };
                    drawing.DrawText(label, new Point(x + 10, y + cellHeight - 48));
                }
            }
            var sheet = new RenderTargetBitmap(columns * cellWidth, rows * cellHeight, 96, 96, PixelFormats.Pbgra32);
            sheet.Render(visual);
            TestDiagnosticArtifacts.SavePng(output, $"sheet-{group.Key.Group}-{group.Key.Theme}.png", sheet);
        }
    }

    internal static IEnumerable<NativeFeelSequenceSummary> SequenceSummaries(IEnumerable<NativeFeelFrame> frames) =>
        frames.Where(frame => frame.Kind == "sequence").GroupBy(frame => (frame.Id, frame.Theme)).Select(group =>
        {
            NativeFeelFrame[] samples = group.OrderBy(frame => frame.BeginMs).ToArray();
            double[] intervals = samples.Skip(1).Zip(samples, (current, previous) => current.BeginMs - previous.BeginMs).Order().ToArray();
            double median = intervals.Length == 0 ? 0 : intervals.Length % 2 == 0
                ? (intervals[intervals.Length / 2 - 1] + intervals[intervals.Length / 2]) / 2
                : intervals[intervals.Length / 2];
            return new NativeFeelSequenceSummary(group.Key.Id, group.Key.Theme, samples.Length,
                samples[0].BeginMs, samples[^1].EndMs, median, intervals.LastOrDefault());
        });

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    public void Dispose() => _backdrop.Close();
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowProc(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(IntPtr handle, out uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(
        IntPtr handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr handle);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr handle, IntPtr dc);
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr handle, uint attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, uint attribute, ref int value, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr source);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);
}

internal sealed record NativeFeelCase(string Id, string Surface, string State, string Theme, double? Scale, string Status, string Note);
internal sealed record NativeFeelFrame(string Id, string Surface, string State, string Group, string Theme, string Kind, string File, double Scale,
    double BeginMs, double EndMs, double? TriggerBeginMs, double? TriggerEndMs, DateTimeOffset WrittenUtc,
    int X, int Y, int Width, int Height, string Note);
internal sealed record NativeFeelSequenceSummary(string Id, string Theme, int FrameCount,
    double FirstBeginMs, double LastEndMs, double MedianIntervalMs, double MaximumIntervalMs);
