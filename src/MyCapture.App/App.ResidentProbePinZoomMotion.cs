using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Display;

namespace MyCapture.App;

public partial class App
{
    // pin-zoom-motion: what the screen shows while a pin zooms by the wheel. The pin rests on an opaque
    // green window the probe opens (a known colour around the pin), and a sampler thread reads
    // one screen row and one screen column through the zoom anchor with BitBlt, as fast as it can, together
    // with the pin's window rectangle. A picture pin shows a picture whose colours encode their own pixel
    // coordinates, so the pixel under the anchor tells which picture point is shown there. Only the edges,
    // the decoded points and the rectangles are written; no screen image leaves the process.
    // Pure green: off the coordinate picture's colours (bar its one pixel at x 0, y 255) and off the brand hues.
    private const uint MotionBacking = 0x00FF00;

    private sealed record MotionTrial(string Name, double StartZoom, int[] Notches, int IntervalMs, bool Animations = true);

    private async Task ProbePinZoomMotionAsync(ResidentProbeOptions options, ResidentProbeRecorder recorder, string scenario)
    {
        if (_pins is null || _settings is null) throw new InvalidOperationException("Pins unavailable.");
        bool native = options.Has("pin-light");
        bool small = options.Has("pin-motion-small");
        bool text = scenario == "pin-text";
        int repeats = options.Has("pin-motion-once") ? 1 : 3;
        // Duration candidates for the comparison; the product uses PinZoomMotionTiming.DefaultDurationMs.
        PinZoomMotionTiming.DurationMs = options.Has("pin-motion-83") ? 83
            : options.Has("pin-motion-167") ? 167 : PinZoomMotionTiming.DefaultDurationMs;
        (int width, int height) = text ? (0, 0) : small ? (400, 300) : scenario switch
        {
            "pin-1600" => (1600, 1000), "pin-1080" => (1920, 1080), "pin-4k" => (3840, 2160),
            _ => throw new ArgumentException("Unknown zoom motion scenario"),
        };
        // The primary monitor, so every run measures on the same screen wherever the pointer is.
        MonitorInfo monitor = MonitorEnumerator.GetPrimary();
        RectD work = monitor.WorkArea;
        double scale = monitor.ScaleFactor <= 0 ? 1 : monitor.ScaleFactor;
        var backing = new Window
        {
            Title = "MyCapture zoom motion backing", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, ShowActivated = false, AllowsTransparency = false,
            Background = BackingBrush(),
            Left = work.Left / scale, Top = work.Top / scale, Width = work.Width / scale, Height = work.Height / scale,
            WindowStartupLocation = WindowStartupLocation.Manual,
            // The app's window style paints its own background; the backing colour is the content's.
            Content = new System.Windows.Controls.Border { Background = BackingBrush() },
        };
        PinWindow? pin = null;
        bool lightweight = _settings.Pin.LightweightImagePins;
        var results = new List<object>();
        bool? animationsBefore = FluidMotion.AnimationsEnabledOverrideForTest;
        try
        {
            backing.Show();
            IntPtr backingHandle = new System.Windows.Interop.WindowInteropHelper(backing).Handle;
            PhysicalWindowPositioner.PlaceTopmostWithoutActivation(backingHandle, work);
            await Task.Delay(300);
            _ = ZoomMotionSampler.GetWindowRectPublic(backingHandle, out int bl, out int bt, out int br, out int bb);
            var backingRect = new { Left = bl, Top = bt, Right = br, Bottom = bb, Visible = backing.IsVisible };

            _settings.Pin.LightweightImagePins = native;
            _pins.BeforeShowForTest = created =>
            {
                pin = created;
                created.ResidentProbeInitialSize();
            };
            string? sourceText = null;
            if (text)
            {
                sourceText = PinProbeText(1, table: false);
                _pins.PinRenderedText(ClipboardTextRenderer.Render(sourceText, false, scale), sourceText, false);
            }
            else
            {
                _pins.PinImageAtRegion(CoordinateImage(width, height), CaptureProvenance.None,
                    new RectD(work.Left + 120, work.Top + 120, width, height));
            }
            await ProbeWaitAsync(() => pin is { IsShown: true } && !pin.ResidentProbeLandingActive, 10_000, "motion pin landed");
            if (pin is null) throw new InvalidOperationException("No pin was created.");
            if (native && !pin.IsNativeHosted) throw new InvalidOperationException("The pin did not use the lightweight host.");
            _pins.BeforeShowForTest = null;
            await Task.Delay(1500);

            // The pin at 100% with its top-left 120 px inside the work area; the anchor sits at about a third of
            // the picture, on a point whose coordinates lie half way between two multiples of 256.
            pin.MovePhysicalForTest(work.Left + 120, work.Top + 120);
            await Task.Delay(400);
            (int left0, int top0, int right0, int bottom0) = pin.PhysicalBounds;
            int surfaceWidth = right0 - left0, surfaceHeight = bottom0 - top0;
            double imageWidth = text ? surfaceWidth / scale : width, imageHeight = text ? surfaceHeight / scale : height;
            static int Middle(double at) => Math.Max(128, (int)(Math.Round((at - 128) / 256) * 256) + 128);
            int ux = text ? (int)(surfaceWidth * 0.35) : Middle(width * 0.35);
            int uy = text ? (int)(surfaceHeight * 0.4) : Middle(height * 0.4);
            var anchor = new Point(left0 + ux, top0 + uy);
            // The row and column reach as far as the largest zoom of the trials can take the pin from the anchor.
            double reach = 1.25;
            int rowLeft = (int)Math.Max(work.Left, anchor.X - (ux * reach) - 40);
            int rowRight = (int)Math.Min(work.Right, anchor.X + ((surfaceWidth - ux) * reach) + 40);
            int columnTop = (int)Math.Max(work.Top, anchor.Y - (uy * reach) - 40);
            int columnBottom = (int)Math.Min(work.Bottom, anchor.Y + ((surfaceHeight - uy) * reach) + 40);

            MotionTrial[] trials =
            [
                new("one-in", 1, [1], 0),
                new("one-out", 1.1, [-1], 0),
                new("five-in", 0.5, [1, 1, 1, 1, 1], 30),
                new("five-out", 0.5 * Math.Pow(1.1, 5), [-1, -1, -1, -1, -1], 30),
                new("stop-100", 0.95, [1], 0),
                new("off-in", 1, [1], 0, Animations: false),
            ];
            // pin-motion-trace: who places the pin window, with a short stack, during each trial.
            var traces = new List<object>();
            bool tracing = false;
            System.Windows.Interop.HwndSource? traceSource = options.Has("pin-motion-trace") && !native
                ? System.Windows.Interop.HwndSource.FromHwnd(pin.Handle) : null;
            System.Windows.Interop.HwndSourceHook traceHook = (IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (tracing && msg == 0x0047)
                {
                    (int tl, int tt, int tr, int tb) = pin.PhysicalBounds;
                    string stack = string.Join(" < ", new StackTrace(1, false).GetFrames()
                        .Select(f => f.GetMethod()).Where(m => m?.DeclaringType?.Namespace?.StartsWith("MyCapture") == true
                            || m?.Name is "SetWindowPos" or "UpdateHwndPositionOnTopLeftChange" or "UpdateHwndSizeOnWidthHeightChange")
                        .Take(8).Select(m => $"{m!.DeclaringType?.Name}.{m.Name}"));
                    traces.Add(new { Ms = 0.0, Rect = new[] { tl, tt, tr, tb }, pin.Left, pin.Top, pin.Width, pin.Height, Stack = stack });
                }
                return IntPtr.Zero;
            };
            traceSource?.AddHook(traceHook);
            using var sampler = new ZoomMotionSampler(pin.Handle, (int)anchor.X, (int)anchor.Y,
                rowLeft, rowRight, columnTop, columnBottom);
            for (int round = 1; round <= repeats; round++)
            {
                foreach (MotionTrial trial in trials)
                {
                    FluidMotion.AnimationsEnabledOverrideForTest = animationsBefore;
                    pin.ResidentProbeSetZoomAt(trial.StartZoom, anchor);
                    await ProbeWaitAsync(() => !pin.IsLiveZoomingForTest && !pin.ResidentProbeZoomMotionActive, 15_000, trial.Name + " start settled");
                    await Task.Delay(700);
                    FluidMotion.AnimationsEnabledOverrideForTest = trial.Animations ? animationsBefore : false;
                    traces.Clear();
                    tracing = traceSource is not null;
                    sampler.Start();
                    await Task.Delay(120);
                    var inputs = new List<object>();
                    double expectedZoom = trial.StartZoom;
                    foreach (int notch in trial.Notches)
                    {
                        double at = sampler.Now;
                        pin.ResidentProbeWheelAt(notch * 120, anchor);
                        inputs.Add(new { At = at, Returned = sampler.Now, notch, Zoom = pin.State.Zoom });
                        if (trial.IntervalMs > 0) await Task.Delay(trial.IntervalMs);
                    }
                    await Task.Delay(900);
                    List<ZoomMotionSampler.Sample> samples = sampler.Stop();
                    tracing = false;
                    FluidMotion.AnimationsEnabledOverrideForTest = animationsBefore;
                    results.Add(new
                    {
                        Trial = trial.Name, Round = round, trial.StartZoom, trial.IntervalMs, trial.Animations,
                        TargetZoom = pin.State.Zoom, Inputs = inputs, Rects = sampler.Rects.ToArray(), Traces = traces.ToArray(),
                        Samples = samples.Select(s => new[]
                        {
                            Math.Round(s.Time, 3), s.Left, s.Right, s.Top, s.Bottom, s.AnchorX, s.AnchorY,
                            s.BeforeX, s.AfterX, s.BeforeY, s.AfterY,
                            s.WindowLeft, s.WindowTop, s.WindowRight, s.WindowBottom,
                        }).ToArray(),
                    });
                }
            }
            double refresh = RefreshRate();
            string path = DiagnosticOutputPaths.Child(options.Output, "zoom-motion.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                Version = 1, Scenario = scenario, Host = native ? "native" : "wpf", Small = small, Text = text,
                ImageWidth = imageWidth, ImageHeight = imageHeight, Scale = scale, RefreshHz = refresh,
                Anchor = new { anchor.X, anchor.Y, ux, uy }, Row = new { Left = rowLeft, Right = rowRight },
                Column = new { Top = columnTop, Bottom = columnBottom }, SurfaceWidth = surfaceWidth, SurfaceHeight = surfaceHeight,
                Encoding = "R = x & 255, G = y & 255, B = (x >> 8) << 4 | (y >> 8); -1 = not decodable",
                SampleColumns = new[] { "ms", "left", "right", "top", "bottom", "anchorX", "anchorY", "beforeX", "afterX", "beforeY", "afterY",
                    "windowLeft", "windowTop", "windowRight", "windowBottom" },
                ProbeSpan = ZoomMotionSampler.Span, FirstRowBacking = sampler.FirstRowBacking, FirstRowColors = sampler.FirstRowColors, RowWidth = rowRight - rowLeft,
                BackingRect = backingRect, WorkArea = new { work.Left, work.Top, work.Width, work.Height },
                Results = results,
            }));
            recorder.Note($"{scenario}: zoom motion written to {path}");
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = animationsBefore;
            PinZoomMotionTiming.DurationMs = PinZoomMotionTiming.DefaultDurationMs;
            _pins.BeforeShowForTest = null;
            _settings.Pin.LightweightImagePins = lightweight;
            _pins.CloseAll();
            await ProbeWaitAsync(() => !Windows.OfType<PinWindow>().Any(), 15_000, "motion pins closed");
            backing.Close();
        }
    }

    private static SolidColorBrush BackingBrush() =>
        new(Color.FromRgb((byte)((MotionBacking >> 16) & 0xFF), (byte)((MotionBacking >> 8) & 0xFF), (byte)(MotionBacking & 0xFF)));

    private static double RefreshRate()
    {
        var mode = new ZoomMotionSampler.DevMode { Size = (short)Marshal.SizeOf<ZoomMotionSampler.DevMode>() };
        return ZoomMotionSampler.EnumDisplaySettingsW(null, -1, ref mode) ? mode.DisplayFrequency : 0;
    }

    /// <summary>A picture whose pixel at (x, y) has R = x &amp; 255, G = y &amp; 255, B = (x &gt;&gt; 8) &lt;&lt; 4 | (y &gt;&gt; 8).</summary>
    internal static BitmapSource CoordinateImage(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = ((y * width) + x) * 4;
                pixels[at] = (byte)((((x >> 8) & 15) << 4) | ((y >> 8) & 15));
                pixels[at + 1] = (byte)(y & 255);
                pixels[at + 2] = (byte)(x & 255);
                pixels[at + 3] = 255;
            }
        }
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    /// <summary>Reads one screen row and one screen column through a point on its own thread.</summary>
    private sealed unsafe class ZoomMotionSampler : IDisposable
    {
        // The decoded points this far before and after the anchor give the picture's scale on the screen.
        internal const int Span = 24;
        private readonly IntPtr _window;
        private readonly int _anchorX, _anchorY, _rowLeft, _rowWidth, _columnTop, _columnHeight;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();
        private Thread? _thread, _rectThread;
        private volatile bool _running;
        private List<Sample> _samples = [];
        private List<double[]> _rects = [];

        /// <summary>Every change of the window rectangle seen by a fast poll: [ms, left, top, right, bottom].</summary>
        internal List<double[]> Rects => _rects;

        internal readonly record struct Sample(double Time, int Left, int Right, int Top, int Bottom,
            int AnchorX, int AnchorY, int BeforeX, int AfterX, int BeforeY, int AfterY,
            int WindowLeft, int WindowTop, int WindowRight, int WindowBottom);

        internal ZoomMotionSampler(IntPtr window, int anchorX, int anchorY, int rowLeft, int rowRight, int columnTop, int columnBottom)
        {
            _window = window;
            _anchorX = anchorX; _anchorY = anchorY;
            _rowLeft = rowLeft; _rowWidth = Math.Max(1, rowRight - rowLeft);
            _columnTop = columnTop; _columnHeight = Math.Max(1, columnBottom - columnTop);
        }

        internal double Now => _clock.Elapsed.TotalMilliseconds;

        internal void Start()
        {
            lock (_gate)
            {
                if (_running) throw new InvalidOperationException("The sampler is already running.");
                _samples = new List<Sample>(4096);
                _rects = new List<double[]>(512);
                _running = true;
                _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "zoom motion sampler" };
                _thread.Start();
                _rectThread = new Thread(PollRect) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "zoom motion rect" };
                _rectThread.Start();
            }
        }

        internal List<Sample> Stop()
        {
            _running = false;
            _thread?.Join();
            _thread = null;
            _rectThread?.Join();
            _rectThread = null;
            return _samples;
        }

        public void Dispose() => Stop();

        // GetWindowRect is cheap: a sub-millisecond poll sees each placement the host makes.
        internal static bool GetWindowRectPublic(IntPtr window, out int left, out int top, out int right, out int bottom)
        {
            bool ok = GetWindowRect(window, out Rect rect);
            (left, top, right, bottom) = (rect.Left, rect.Top, rect.Right, rect.Bottom);
            return ok;
        }

        private void PollRect()
        {
            Rect last = default;
            bool any = false;
            while (_running)
            {
                double time = Now;
                if (GetWindowRect(_window, out Rect rect) && (!any || !rect.Equals(last)))
                {
                    _rects.Add([Math.Round(time, 3), rect.Left, rect.Top, rect.Right, rect.Bottom]);
                    last = rect;
                    any = true;
                }
                Thread.SpinWait(2000);
            }
        }

        private void Run()
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr memory = CreateCompatibleDC(screen);
            IntPtr rowBitmap = Section(memory, _rowWidth, 1, out IntPtr rowBits);
            IntPtr columnBitmap = Section(memory, 1, _columnHeight, out IntPtr columnBits);
            try
            {
                while (_running)
                {
                    double time = Now;
                    _ = GetWindowRect(_window, out Rect rect);
                    IntPtr old = SelectObject(memory, rowBitmap);
                    _ = BitBlt(memory, 0, 0, _rowWidth, 1, screen, _rowLeft, _anchorY, 0x00CC0020 | 0x40000000);
                    _ = SelectObject(memory, columnBitmap);
                    _ = BitBlt(memory, 0, 0, 1, _columnHeight, screen, _anchorX, _columnTop, 0x00CC0020 | 0x40000000);
                    _ = SelectObject(memory, old);
                    uint* row = (uint*)rowBits;
                    // A bottom-up one-pixel-wide DIB keeps its rows in reverse order, four bytes each.
                    uint* column = (uint*)columnBits;
                    int ax = _anchorX - _rowLeft, ay = _anchorY - _columnTop;
                    if (FirstRowBacking < 0)
                    {
                        FirstRowBacking = CountBacking(row, _rowWidth);
                        var counts = new Dictionary<uint, int>();
                        for (int i = 0; i < _rowWidth; i++)
                        {
                            uint pixel = row[i] & 0xFFFFFF;
                            counts[pixel] = counts.TryGetValue(pixel, out int n) ? n + 1 : 1;
                        }
                        FirstRowColors = string.Join(" ", counts.OrderByDescending(c => c.Value).Take(4).Select(c => $"{c.Key:X6}x{c.Value}"));
                    }
                    (int left, int right) = Edges(row, _rowWidth, reversed: false, ax);
                    (int top, int bottom) = Edges(column, _columnHeight, reversed: true, ay);
                    _samples.Add(new Sample(time,
                        left < 0 ? -1 : left + _rowLeft, right < 0 ? -1 : right + _rowLeft,
                        top < 0 ? -1 : top + _columnTop, bottom < 0 ? -1 : bottom + _columnTop,
                        DecodeX(At(row, _rowWidth, ax, false)), DecodeY(At(row, _rowWidth, ax, false)),
                        DecodeX(At(row, _rowWidth, ax - Span, false)), DecodeX(At(row, _rowWidth, ax + Span, false)),
                        DecodeY(At(column, _columnHeight, ay - Span, true)), DecodeY(At(column, _columnHeight, ay + Span, true)),
                        rect.Left, rect.Top, rect.Right, rect.Bottom));
                }
            }
            finally
            {
                DeleteObject(rowBitmap);
                DeleteObject(columnBitmap);
                DeleteDC(memory);
                _ = ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private static uint At(uint* line, int length, int index, bool reversed) =>
            index < 0 || index >= length ? MotionBacking : line[reversed ? length - 1 - index : index];

        // The pin's run of the line: outward from the anchor to the first backing pixel on each side.
        private static (int First, int Last) Edges(uint* line, int length, bool reversed, int anchor)
        {
            if (anchor < 0 || anchor >= length || IsBacking(At(line, length, anchor, reversed))) return (-1, -1);
            int first = anchor, last = anchor;
            while (first > 0 && !IsBacking(At(line, length, first - 1, reversed))) first--;
            while (last < length - 1 && !IsBacking(At(line, length, last + 1, reversed))) last++;
            // A pin that reaches past the line's ends has no edge there.
            if (first == 0) first = -1;
            if (last == length - 1) last = -1;
            return (first, last);
        }

        private static bool IsBacking(uint pixel) => (pixel & 0xFFFFFF) == MotionBacking;

        private static int CountBacking(uint* line, int length)
        {
            int count = 0;
            for (int i = 0; i < length; i++) if (IsBacking(line[i])) count++;
            return count;
        }

        /// <summary>Backing pixels in the first row read (diagnostics for a backing that did not show).</summary>
        internal int FirstRowBacking { get; private set; } = -1;

        /// <summary>The most frequent colours of the first row read, as RRGGBBxCount.</summary>
        internal string FirstRowColors { get; private set; } = "";

        private static int DecodeX(uint pixel) => (pixel & 0xFFFFFF) == MotionBacking ? -1
            : (int)((((pixel & 0xFF) >> 4) << 8) | ((pixel >> 16) & 0xFF));

        private static int DecodeY(uint pixel) => (pixel & 0xFFFFFF) == MotionBacking ? -1
            : (int)(((pixel & 0x0F) << 8) | ((pixel >> 8) & 0xFF));

        private static IntPtr Section(IntPtr dc, int width, int height, out IntPtr bits)
        {
            var info = new BitmapInfo
            {
                Size = Marshal.SizeOf<BitmapInfo>(), Width = width, Height = height, Planes = 1, BitCount = 32,
            };
            IntPtr bitmap = CreateDIBSection(dc, ref info, 0, out bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed.");
            return bitmap;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            internal int Size, Width, Height;
            internal short Planes, BitCount;
            internal int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
            internal int Colors;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { internal int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
            internal short SpecVersion, DriverVersion, Size, DriverExtra;
            internal int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
            internal short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string FormName;
            internal short LogPixels;
            internal int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplaySettingsW(string? device, int mode, ref DevMode devMode);

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr gdiObject);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    }
}
