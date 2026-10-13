using System.Diagnostics;
using System.IO;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3 || File.Exists(args[1])) return 2;
        var results = new List<object>();
        string? failure = null;
        Application? app = null;
        try
        {
            string target = Path.GetFullPath(args[0]);
            string folder = Path.GetDirectoryName(target)!;
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                string path = Path.Combine(folder, name.Name + ".dll");
                return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
            };
            Assembly product = AssemblyLoadContext.Default.LoadFromAssemblyPath(target);
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string resource in new[] { "Tokens", "Symbols", "Controls" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{resource}.xaml") });
            Type theme = product.GetType("MyCapture.App.Themes.ThemeService", true)!;
            MethodInfo apply = theme.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic)!;
            apply.Invoke(null, [Enum.Parse(apply.GetParameters()[0].ParameterType, "Midnight")]);
            Type type = product.GetType("MyCapture.App.Capture.CaptureOverlayView", true)!;
            ConstructorInfo constructor = type.GetConstructors(Instance).Single(c => c.GetParameters().Length == 3);
            Type frameType = constructor.GetParameters()[0].ParameterType;
            ConstructorInfo frameConstructor = frameType.GetConstructors().Single(c => c.GetParameters().Length == 4);
            Type rectType = frameConstructor.GetParameters()[1].ParameterType;
            Type pointType = type.GetField("_cursorPixel", Instance)!.FieldType;
            MethodInfo move = type.GetMethod("OnMouseMove", Instance)!;
            MethodInfo renderLayers = type.GetMethod("RenderLayers", Instance)!;
            PropertyInfo readCursor = type.GetProperty("ReadCursor", Instance)!;
            if (Environment.GetEnvironmentVariable("MYCAPTURE_OVERLAY_NATIVE") == "1")
            {
                results.Add(NativeRun(product, type, frameType, frameConstructor, rectType, pointType, move, readCursor));
            }
            else
            foreach (int width in Environment.GetEnvironmentVariable("MYCAPTURE_OVERLAY_ACTUAL") == "1" ? new[] { int.Parse(Environment.GetEnvironmentVariable("STRESS_WIDTH") ?? "4208") } : new[] { 1920, 3840 })
            foreach (bool magnifier in new[] { false, true })
            foreach (bool drag in new[] { false, true })
            {
                bool snapshot = Environment.GetEnvironmentVariable("MYCAPTURE_OVERLAY_SNAPSHOT") == "1";
                if (snapshot && (!magnifier || !drag)) continue;
                int height = width == 4208 ? 1440 : width * 9 / 16;
                byte[] pixels = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int offset = (y * width + x) * 4;
                    pixels[offset] = (byte)(x % 256); pixels[offset + 1] = (byte)(y % 256);
                    pixels[offset + 2] = (byte)((x + y) % 256); pixels[offset + 3] = 255;
                }
                PixelFormat frameFormat = Environment.GetEnvironmentVariable("MYCAPTURE_OVERLAY_BGR32") == "1" ? PixelFormats.Bgr32 : PixelFormats.Bgra32;
                BitmapSource source = BitmapSource.Create(width, height, 96, 96, frameFormat, null, pixels, width * 4);
                source.Freeze();
                object bounds = Activator.CreateInstance(rectType, 0d, 0d, (double)width, (double)height)!;
                object frame = frameConstructor.Invoke([source, bounds, null, 0d]);
                string disabled = Environment.GetEnvironmentVariable("MYCAPTURE_OVERLAY_DISABLE") ?? "";
                var view = (FrameworkElement)constructor.Invoke([frame, magnifier, disabled != "grid"]);
                // Current production selector shows the mode bar and start guide; old has neither.
                type.GetProperty("ShowModeBar", Instance)?.SetValue(view, disabled != "bar");
                type.GetProperty("ShowStartGuide", Instance)?.SetValue(view, disabled != "guide");
                type.GetProperty("DetectWindows", Instance)?.SetValue(view, disabled != "detect");
                if (type.GetProperty("WindowCandidates", Instance) is { } candidatesProperty)
                {
                    Type candidate = frameType.Assembly.GetType("MyCapture.Platform.Capture.WindowCandidate", true)!;
                    ConstructorInfo cc = candidate.GetConstructors().Single(c => c.GetParameters()[0].ParameterType == typeof(IntPtr));
                    Array candidates = Array.CreateInstance(candidate, 3);
                    for (int index = 0; index < 3; index++)
                    {
                        object?[] values = cc.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
                        values[0] = new IntPtr(index + 11);
                        values[1] = Activator.CreateInstance(rectType, index * width / 3d, 0d, width / 3d, (double)height);
                        values[2] = "Synthetic window";
                        candidates.SetValue(cc.Invoke(values), index);
                    }
                    candidatesProperty.SetValue(view, candidates);
                }
                object cursor = Activator.CreateInstance(pointType, 120d, 100d)!;
                Func<object> read = () => cursor;
                Delegate cursorDelegate = Expression.Lambda(readCursor.PropertyType,
                    Expression.Convert(Expression.Invoke(Expression.Constant(read)), pointType)).Compile();
                readCursor.SetValue(view, cursorDelegate);
                try
                {
                    // No native host: independently render real product visuals at their full physical size.
                    view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
                    SetMouseOver(view);
                    type.GetField("_pointerInitialized", Instance)!.SetValue(view, true);
                    if (drag)
                    {
                        FieldInfo interaction = type.GetField("_interaction", Instance)!;
                        interaction.SetValue(view, Enum.Parse(interaction.FieldType, "Create"));
                        type.GetField("_dragAnchor", Instance)!.SetValue(view, Activator.CreateInstance(pointType, 100d, 100d));
                    }
                    var moveArgs = new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent };
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    // Warm text/WPF command machinery only; measured loops always change the pointer.
                    move.Invoke(view, [moveArgs]); renderLayers.Invoke(view, null); bitmap.Render(view);
                    if (disabled is "dimmer" or "reveal")
                        ((DrawingVisual)type.GetField(disabled == "dimmer" ? "_dimmerVisual" : "_revealVisual", Instance)!.GetValue(view)!).Opacity = 0;
                    var inputMs = new List<double>(); var commandMs = new List<double>();
                    var rasterMs = new List<double>();
                    var clock = new Stopwatch();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < (snapshot ? 1 : 200); i++)
                    {
                        cursor = Activator.CreateInstance(pointType, snapshot ? width * 0.7 : (double)(150 + i * (width - 300) / 200),
                            snapshot ? height * 0.7 : (double)(150 + i * (height - 300) / 200))!;
                        clock.Restart(); move.Invoke(view, [moveArgs]); inputMs.Add(clock.Elapsed.TotalMilliseconds);
                        clock.Restart(); renderLayers.Invoke(view, null); commandMs.Add(clock.Elapsed.TotalMilliseconds);
                        if (i % 20 == 0)
                        { clock.Restart(); bitmap.Clear(); bitmap.Render(view); rasterMs.Add(clock.Elapsed.TotalMilliseconds); }
                    }
                    long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (snapshot)
                    {
                        bitmap.Clear(); bitmap.Render(view);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var png = new FileStream(Path.GetFullPath(args[1]) + ".png", FileMode.CreateNew);
                        encoder.Save(png);
                    }
                    results.Add(new { Width = width, Height = height, Format = frameFormat.ToString(), Magnifier = magnifier, Drag = drag,
                        InputMs = inputMs, CommandMs = commandMs, RasterMs = rasterMs, AllocationBytes = allocation,
                        StaticRenderCount = type.GetProperty("StaticRenderCount", Instance)!.GetValue(view),
                        FeedbackRenderCount = type.GetProperty("FeedbackRenderCount", Instance)!.GetValue(view),
                        MagnifierUpdateCount = type.GetProperty("MagnifierUpdateCount", Instance)!.GetValue(view) });
                }
                finally { type.GetMethod("ReleaseResources", Instance)!.Invoke(view, null); }
            }
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally { app?.Shutdown(); }
        using var stream = new FileStream(Path.GetFullPath(args[1]), FileMode.CreateNew);
        JsonSerializer.Serialize(stream, new { Label = args[2], Target = args[0], Framework = RuntimeInformation.FrameworkDescription,
            Wpf = typeof(Window).Assembly.Location, Results = results, Failure = failure,
            Limits = Environment.GetEnvironmentVariable("MYCAPTURE_OVERLAY_NATIVE") == "1"
                ? "Actual product overlay HWND with synthetic frozen desktop, owned window lifecycle; no product App startup, user profile, queue, persistence, save, clipboard, screen acquisition or native cursor movement. Producer field identifies callback-driven or independent dispatcher pointer delivery; callback intervals are not DWM presentation or OS input-to-photon latency. No private images/metadata collected."
                : "Offscreen synthetic opaque frozen desktop; actual product View OnMouseMove/RenderLayers, same reflection overhead; warm per-point command costs plus software RenderTargetBitmap raster costs, not HWND/DWM latency. No App/Window.Show/native cursor/input/clipboard/profile/queue/persistence/save/cleanup. Native pointer fallback overridden. No desktop images or metadata collected." },
            new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"RESULT={(failure is null ? "PASS" : "FAIL")} CASES={results.Count}");
        if (failure is not null) Console.Error.WriteLine(failure);
        int expected = Environment.GetEnvironmentVariable("MYCAPTURE_OVERLAY_NATIVE") == "1" || Environment.GetEnvironmentVariable("MYCAPTURE_OVERLAY_SNAPSHOT") == "1" ? 1
            : Environment.GetEnvironmentVariable("MYCAPTURE_OVERLAY_ACTUAL") == "1" ? 4 : 8;
        return failure is null && results.Count == expected ? 0 : 1;
    }
    private static object NativeRun(Assembly product, Type type, Type frameType, ConstructorInfo frameConstructor,
        Type rectType, Type pointType, MethodInfo move, PropertyInfo readCursor)
    {
        const int width = 4208, height = 1440;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        { int offset = (y * width + x) * 4; pixels[offset] = (byte)(x % 256); pixels[offset + 1] = (byte)(y % 256); pixels[offset + 2] = (byte)((x + y) % 256); pixels[offset + 3] = 255; }
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        source.Freeze();
        object bounds = Activator.CreateInstance(rectType, 0d, 0d, (double)width, (double)height)!;
        object frame = frameConstructor.Invoke([source, bounds, null, 0d]);
        Type windowType = product.GetType("MyCapture.App.Capture.CaptureOverlayWindow", true)!;
        ConstructorInfo wc = windowType.GetConstructors(Instance).Single(c => c.GetParameters()[0].ParameterType == frameType);
        object?[] values = wc.GetParameters().Select(p => p.DefaultValue).ToArray(); values[0] = frame; values[1] = false;
        var window = (Window)wc.Invoke(values);
        var view = (FrameworkElement)window.Content;
        object cursor = Activator.CreateInstance(pointType, 150d, 180d)!;
        Func<object> read = () => cursor;
        readCursor.SetValue(view, Expression.Lambda(readCursor.PropertyType,
            Expression.Convert(Expression.Invoke(Expression.Constant(read)), pointType)).Compile());
        windowType.GetProperty("ShowModeBar", Instance)?.SetValue(window, true);
        windowType.GetProperty("ShowStartGuide", Instance)?.SetValue(window, true);
        windowType.GetProperty("DetectWindows", Instance)?.SetValue(window, true);
        if (type.GetProperty("WindowCandidates", Instance) is { } cp)
        {
            Type candidate = frameType.Assembly.GetType("MyCapture.Platform.Capture.WindowCandidate", true)!;
            ConstructorInfo cc = candidate.GetConstructors().Single(c => c.GetParameters()[0].ParameterType == typeof(IntPtr));
            int candidateCount = Math.Clamp(int.Parse(Environment.GetEnvironmentVariable("STRESS_CANDIDATES") ?? "3"), 3, 10000);
            Array candidates = Array.CreateInstance(candidate, candidateCount);
            for (int i = 0; i < candidateCount; i++)
            { object?[] v = cc.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray(); v[0] = new IntPtr(i + 11); v[1] = i < candidateCount - 3 ? Activator.CreateInstance(rectType, 0d, 0d, 2d, 2d) : Activator.CreateInstance(rectType, (i - (candidateCount - 3)) * width / 3d, 0d, width / 3d, (double)height); v[2] = "Synthetic window"; candidates.SetValue(cc.Invoke(v), i); }
            cp.SetValue(view, candidates);
        }
        var output = new List<object>();
        var moves = new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent };
        try
        {
            window.Show();
            PumpUntil(() => view.IsLoaded && (int)type.GetProperty("StaticRenderCount", Instance)!.GetValue(view)! > 0, 5000);
            SetMouseOver(view);
            foreach (bool drag in new[] { false, true })
            {
                if (drag)
                {
                    type.GetMethod("DismissStartGuide", Instance)?.Invoke(view, null);
                    FieldInfo interaction = type.GetField("_interaction", Instance)!;
                    interaction.SetValue(view, Enum.Parse(interaction.FieldType, "Create"));
                    type.GetField("_dragAnchor", Instance)!.SetValue(view, Activator.CreateInstance(pointType, 100d, 100d));
                }
                var intervals = new List<double>(); var inputTimes = new List<double>(); var feedbackCounts = new List<int>();
                string initialInteraction = type.GetField("_interaction", Instance)!.GetValue(view)!.ToString()!;
                int burst = Math.Clamp(int.Parse(Environment.GetEnvironmentVariable("STRESS_BURST") ?? "1"), 1, 128);
                var renderingTimes = new List<double>(); int duplicateCallbacks = 0; TimeSpan? lastRenderingTime = null;
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                TimeSpan cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
                var clock = Stopwatch.StartNew(); double prior = -1; int sequence = 0;
                bool asynchronousInput = Environment.GetEnvironmentVariable("STRESS_ASYNC_INPUT") == "1";
                var queueDelayMs = new List<double>();
                int inputBatch = 0;
                void DeliverInput()
                {
                    for (int b = 0; b < burst; b++)
                    {
                        int inputSequence = inputBatch * burst + b;
                        cursor = Activator.CreateInstance(pointType, (double)(150 + inputSequence * 19 % (width - 300)),
                            (double)(180 + inputSequence * 11 % (height - 360)))!;
                        var cost = Stopwatch.StartNew(); move.Invoke(view, [moves]); inputTimes.Add(cost.Elapsed.TotalMilliseconds);
                    }
                    inputBatch++;
                }
                EventHandler tick = (_, eventArgs) =>
                {
                    if (!asynchronousInput && sequence >= 120) return;
                    TimeSpan renderingTime = ((RenderingEventArgs)eventArgs).RenderingTime;
                    if (lastRenderingTime == renderingTime) { duplicateCallbacks++; return; }
                    lastRenderingTime = renderingTime; renderingTimes.Add(renderingTime.TotalMilliseconds);
                    double now = clock.Elapsed.TotalMilliseconds;
                    if (prior >= 0) intervals.Add(now - prior);
                    prior = now;
                    if (!asynchronousInput) DeliverInput();
                    feedbackCounts.Add((int)type.GetProperty("FeedbackRenderCount", Instance)!.GetValue(view)!);
                    sequence++;
                };
                CompositionTarget.Rendering += tick;
                try
                {
                    if (asynchronousInput)
                    {
                        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(18));
                        Task producer = Task.Run(async () =>
                        {
                            for (int i = 0; i < 240; i++)
                            {
                                await Task.Delay(8, cancel.Token);
                                double enqueued = clock.Elapsed.TotalMilliseconds;
                                await view.Dispatcher.InvokeAsync(() =>
                                {
                                    queueDelayMs.Add(clock.Elapsed.TotalMilliseconds - enqueued);
                                    DeliverInput();
                                }, DispatcherPriority.Input, cancel.Token);
                            }
                        });
                        try
                        {
                            PumpUntil(() => producer.IsCompleted, 20000);
                            producer.GetAwaiter().GetResult();
                            PumpUntil(() => !(bool)type.GetProperty("HasPendingFeedback", Instance)!.GetValue(view)!, 3000);
                            if (!Equals(type.GetField("_drawnPointer", Instance)!.GetValue(view), cursor))
                                throw new InvalidOperationException("The final input position was not drawn.");
                        }
                        finally { cancel.Cancel(); }
                    }
                    else PumpUntil(() => sequence >= 120, 10000);
                }
                finally { CompositionTarget.Rendering -= tick; }
                string finalInteraction = type.GetField("_interaction", Instance)!.GetValue(view)!.ToString()!;
                bool valid = !drag || initialInteraction == "Create" && finalInteraction == "Create";
                output.Add(new { Drag = drag, InitialInteraction = initialInteraction, FinalInteraction = finalInteraction,
                    Valid = valid, Ended = type.GetField("_ended", Instance)!.GetValue(view),
                    FinalSelection = type.GetProperty("Selection", Instance)!.GetValue(view),
                    FinalCursor = cursor, IntervalsMs = intervals, InputMs = inputTimes, FeedbackCounts = feedbackCounts,
                    Producer = asynchronousInput ? "ThreadPool-8ms-with-dispatcher-backpressure" : "CompositionCallback",
                    QueueDelayMs = queueDelayMs, InputBatches = inputBatch,
                    ElapsedMs = clock.Elapsed.TotalMilliseconds, UniqueRenderingTimes = sequence, DuplicateCallbacks = duplicateCallbacks, RenderingTimesMs = renderingTimes, InputBurst = burst, InputCount = inputTimes.Count,
                    AllocationBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore, CpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds,
                    StaticRenderCount = type.GetProperty("StaticRenderCount", Instance)!.GetValue(view),
                    MagnifierUpdates = type.GetProperty("MagnifierUpdateCount", Instance)!.GetValue(view) });
                if (!valid) throw new InvalidOperationException("Native fixture lost its drag interaction state.");
            }
            return new { Native = true, ActualWidth = view.ActualWidth, ActualHeight = view.ActualHeight,
                Tier = RenderCapability.Tier >> 16, Dpi = VisualTreeHelper.GetDpi(view).DpiScaleX, Phases = output,
                Limits = "Actual product overlay HWND across synthetic4208x1440 Bgr32 desktop, actual scheduler; owned windows only. Producer field distinguishes callback-coupled input from separate ThreadPool/dispatcher input. RenderingTime is deduplicated but not a DWM presentation. QueueDelayMs measures dispatcher delivery only, not OS mouse-to-photon. No selector confirmation, persistence/cursor movement/input injection." };
        }
        finally { window.Close(); }
    }
    private static void PumpUntil(Func<bool> predicate, int timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate() && clock.ElapsedMilliseconds < timeout)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        if (!predicate()) throw new TimeoutException("Owned overlay lifecycle timed out.");
    }
    private static void SetMouseOver(FrameworkElement view)
    {
        var key = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        view.SetValue(key, true);
        MethodInfo flag = typeof(UIElement).GetMethod("WriteFlag", BindingFlags.Instance | BindingFlags.NonPublic)!;
        flag.Invoke(view, [Enum.Parse(flag.GetParameters()[0].ParameterType, "IsMouseOverCache"), true]);
    }
}
