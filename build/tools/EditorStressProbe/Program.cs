using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;

internal static class Bootstrap
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length is not (5 or 6 or 7)) { Console.Error.WriteLine("EditorStress <payload> <output-directory> <width 3840|7680> <items 1000|10000> <ink-points-per-stroke> [solid|entropy] [product|independent]"); return 2; }
        string payload = Path.GetFullPath(args[0]);
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", payload + Path.DirectorySeparatorChar);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(payload, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        try
        {
            var product = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(payload, "MyCapture.dll"));
            return (int)typeof(Bootstrap).Assembly.GetType("StressCase", true)!.GetMethod("Start")!.Invoke(null, [product, args])!;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 3; }
    }
}

internal static class StressCase
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static string _phase = "startup";
    private static string _root = "";
    private static readonly List<object> Phases = [];
    private static readonly List<double> Heartbeats = [];
    private static readonly List<object> EncoderSamples = [];
    private static long _peakPrivate;

    public static int Start(Assembly product, string[] args)
    {
        int width = int.Parse(args[2]), count = int.Parse(args[3]), inkPoints = int.Parse(args[4]);
        string pattern = args.Length >= 6 ? args[5] : "solid";
        string encoderMode = args.Length == 7 ? args[6] : "product";
        if (pattern is not ("solid" or "entropy")) return 4;
        if (encoderMode is not ("product" or "independent")) return 4;
        if (width is not (3840 or 7680) || count is not (1000 or 10000) || inkPoints is < 2 or > 10000) return 4;
        _root = Path.GetFullPath(args[1]);
        if (Directory.Exists(_root)) throw new IOException("Use a new output directory; preserve previous evidence.");
        Directory.CreateDirectory(_root);
        var clock = Stopwatch.StartNew();
        using var guard = new System.Threading.Timer(_ =>
        {
            using var process = Process.GetCurrentProcess();
            long memory = process.PrivateMemorySize64;
            Interlocked.Exchange(ref _peakPrivate, Math.Max(Interlocked.Read(ref _peakPrivate), memory));
            if (clock.Elapsed < TimeSpan.FromSeconds(60) && memory < 2L * 1024 * 1024 * 1024) return;
            File.WriteAllText(Path.Combine(_root, "guard.json"), JsonSerializer.Serialize(new
                { Status = "GUARD_ABORT", Phase = _phase, ElapsedMs = clock.Elapsed.TotalMilliseconds, PrivateBytes = memory, Pid = Environment.ProcessId }));
            Environment.Exit(124); // This harness's own process only; all data and partial output retained.
        }, null, 0, 250);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string dictionary in new[] { "Tokens", "Symbols", "Controls" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MyCapture;component/Themes/" + dictionary + ".xaml") });
        foreach (var (key, type) in new[] { ("BoolToVisibility", "BoolToVisibilityConverter"), ("PinLabel", "PinLabelConverter"), ("IsHeaderRow", "IsHeaderRowConverter") })
            app.Resources.Add(key, Activator.CreateInstance(product.GetType("MyCapture.App.Gallery." + type, true)!));
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(app, product, width, count, inkPoints, pattern, encoderMode); app.Shutdown(0); }
            catch (Exception error) { File.WriteAllText(Path.Combine(_root, "failure.txt"), error.ToString()); app.Shutdown(5); }
        };
        return app.Run();
    }

    private static T Measure<T>(string phase, Func<T> operation)
    {
        _phase = phase;
        long before = GC.GetTotalAllocatedBytes(true);
        var clock = Stopwatch.StartNew();
        T value = operation();
        Phases.Add(new { Phase = phase, Ms = clock.Elapsed.TotalMilliseconds, AllocatedBytes = GC.GetTotalAllocatedBytes(true) - before });
        File.WriteAllText(Path.Combine(_root, "progress.json"), JsonSerializer.Serialize(Phases));
        return value;
    }

    private static async Task RunAsync(Application app, Assembly product, int width, int count, int inkPoints, string pattern, string encoderMode)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        int height = width * 9 / 16;
        string sourcePixelSha = "";
        var bitmap = Measure("synthetic-bitmap", () =>
        {
            byte[] pixels = new byte[width * height * 4];
            uint random = 0x4D435354;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pattern == "entropy")
                {
                    random ^= random << 13; random ^= random >> 17; random ^= random << 5;
                    pixels[i] = (byte)random; pixels[i + 1] = (byte)(random >> 8); pixels[i + 2] = (byte)(random >> 16);
                }
                else { pixels[i] = 230; pixels[i + 1] = 245; pixels[i + 2] = 250; }
                pixels[i + 3] = 255;
            }
            sourcePixelSha = Convert.ToHexString(SHA256.HashData(pixels));
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); image.Freeze(); return image;
        });
        var document = Measure("synthetic-document", () =>
        {
            var doc = AnnotationDocument.CreateFor(width, height);
            for (int i = 0; i < count - 20; i++)
                doc.Items.Add(new RectangleAnnotation { ZIndex = i, Rect = new RectD((i * 37) % (width - 120), (i * 29) % (height - 80), 120, 80) });
            for (int i = 0; i < 20; i++) doc.Items.Add(new PenAnnotation
            {
                ZIndex = count - 20 + i,
                Points = Enumerable.Range(0, inkPoints).Select(p => new PointD((p * 3.0) % width, (i * 80 + Math.Sin(p * .015) * 50 + height / 2) % height)).ToList()
            });
            return doc;
        });
        string originalDoc = document.ToJson();
        Guid[] originalIds = document.Items.Select(item => item.Id).ToArray();
        File.WriteAllText(Path.Combine(_root, "document-before.json"), originalDoc);
        var editorType = product.GetType("MyCapture.App.Editing.AnnotationEditorControl", true)!;
        if (encoderMode == "independent")
        {
            // Diagnostic boundary only: install the owned synthetic image's cache entry.
            // Lazy runs inside the product's actual background STA encoder, never on UI.
            var cache = (System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, Lazy<byte[]>>)
                editorType.GetField("DraftPngCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            cache.Add(bitmap, new Lazy<byte[]>(() => EncodeIndependentBitmap(bitmap)));
        }
        var ctor = editorType.GetConstructors(Members).Single(c => c.GetParameters().Length == 7);
        var region = new RectD(0, 0, width, height);
        object CreateEditor() => ctor.Invoke([new FrozenFrame(bitmap, region, null, 0), region, bitmap, document, null, null, "Synthetic stress"]);
        object editor = Measure("editor-open", CreateEditor);
        var surface = (FrameworkElement)editorType.GetField("_surface", Members)!.GetValue(editor)!;
        var controller = editorType.GetField("_controller", Members)!.GetValue(editor)!;
        var controllerType = controller.GetType();
        var actualDocument = (AnnotationDocument)controllerType.GetProperty("Document", Members)!.GetValue(controller)!;
        var undo = controllerType.GetProperty("Undo", Members)!.GetValue(controller)!;
        RenderTargetBitmap Render(double scale)
        {
            surface.Measure(new Size(1280 * scale, 720 * scale)); surface.Arrange(new Rect(0, 0, 1280 * scale, 720 * scale)); surface.InvalidateVisual(); surface.UpdateLayout();
            var target = new RenderTargetBitmap((int)(1280 * scale), (int)(720 * scale), 96, 96, PixelFormats.Pbgra32);
            target.Render(surface); target.Freeze();
            return target;
        }
        double last = Stopwatch.GetTimestamp();
        var heartbeat = new DispatcherTimer(DispatcherPriority.Normal, app.Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        heartbeat.Tick += (_, _) => { double now = Stopwatch.GetTimestamp(); Heartbeats.Add((now - last) * 1000 / Stopwatch.Frequency); last = now; };
        heartbeat.Start();
        await Task.Delay(100);
        string initialViewportSha = "";
        for (int pass = 0; pass < 3; pass++)
        {
            var rendered = Measure("render-viewport-" + pass, () => Render(1));
            if (pass == 0)
            {
                heartbeat.Stop(); // Diagnostic PNG/hash IO is outside product heartbeat measurements.
                byte[] viewportPixels = new byte[1280 * 720 * 4]; rendered.CopyPixels(viewportPixels, 1280 * 4, 0);
                initialViewportSha = Convert.ToHexString(SHA256.HashData(viewportPixels));
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered));
                using var stream = File.Create(Path.Combine(_root, "initial-viewport.png")); encoder.Save(stream);
                last = Stopwatch.GetTimestamp();
                heartbeat.Start();
            }
            await Task.Delay(20);
        }
        Measure("render-fit-scale-150", () => { Render(1.5); return true; });
        var sessionType = product.GetType("MyCapture.App.Editing.EditDraftSession", true)!;
        var sessionCtor = sessionType.GetConstructors(Members).Single();
        var snapshot = editorType.GetMethod("CaptureDraftAsync", Members)!.CreateDelegate(sessionCtor.GetParameters()[2].ParameterType, editor);
        var failures = new List<string>();
        var store = new EditDraftStore(Path.Combine(_root, "drafts")); // production: retired drafts are deleted
        var session = sessionCtor.Invoke([store, app.Dispatcher, snapshot, (Action<Exception>)(error => failures.Add(error.ToString())), null, 0L]);
        var changed = sessionType.GetMethod("Changed", Members)!;
        editorType.GetEvent("DraftChanged", Members)!.GetAddMethod(true)!.Invoke(editor, [(EventHandler)((_, _) => changed.Invoke(session, null))]);
        var flushMethod = sessionType.GetMethod("FlushAsync", Members)!;
        Task Flush() => (Task)flushMethod.Invoke(session, null)!;
        var tool = controllerType.GetProperty("Tool", Members)!;
        tool.SetValue(controller, Enum.Parse(tool.PropertyType, "Rectangle"));
        _phase = "first-draft-and-edit";
        changed.Invoke(session, null);
        Task firstDraft = Flush();
        for (int i = 0; i < 10; i++)
        {
            Measure("add-undo-redo-" + i, () =>
            {
                PointD from = new(30 + i * 30, 30), to = new(100 + i * 30, 100);
                controllerType.GetMethod("PointerDown", Members)!.Invoke(controller, [from]); controllerType.GetMethod("PointerMove", Members)!.Invoke(controller, [to]); controllerType.GetMethod("PointerUp", Members)!.Invoke(controller, [to]);
                undo.GetType().GetMethod("Undo")!.Invoke(undo, null); undo.GetType().GetMethod("Redo")!.Invoke(undo, null);
                Render(1); return true;
            });
            await Task.Delay(25);
        }
        await firstDraft;
        _phase = "latest-draft-flush";
        var draftClock = Stopwatch.StartNew(); await Flush();
        Phases.Add(new { Phase = _phase, Ms = draftClock.Elapsed.TotalMilliseconds });
        // Movement uses the real selection gesture; no input injection or OS clipboard.
        tool.SetValue(controller, Enum.Parse(tool.PropertyType, "Select"));
        Measure("selection-move", () =>
        {
            controllerType.GetMethod("PointerDown", Members)!.Invoke(controller, [new PointD(310, 65)]);
            controllerType.GetMethod("PointerMove", Members)!.Invoke(controller, [new PointD(330, 85)]);
            controllerType.GetMethod("PointerUp", Members)!.Invoke(controller, [new PointD(330, 85)]); Render(1); return true;
        });
        await Flush(); await Task.Delay(100); heartbeat.Stop();
        int finalCount = actualDocument.Items.Count;
        File.WriteAllText(Path.Combine(_root, "document-after.json"), actualDocument.ToJson());
        if (finalCount != count + 10 || !actualDocument.Items.Take(count).Select(item => item.Id).SequenceEqual(originalIds)
            || actualDocument.Items.OfType<PenAnnotation>().Sum(pen => pen.Points.Count) != 20L * inkPoints)
            throw new IOException("Annotation/ink preservation failed.");
        var drafts = await Task.Run(store.ReadAvailable);
        if (drafts.Count != 1) throw new IOException("Expected one recoverable draft.");
        using var recoveredDocument = JsonDocument.Parse(drafts[0].Draft.Document);
        if (recoveredDocument.RootElement.GetProperty("items").GetArrayLength() != finalCount || failures.Count != 0)
            throw new IOException("Latest recoverable draft completeness failed.");
        string recoveredSourceSha;
        using (var originalStream = File.OpenRead(store.ResolveFile(drafts[0].Draft, "original.png")))
        {
            var decoded = BitmapDecoder.Create(originalStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            if (decoded.PixelWidth != width || decoded.PixelHeight != height) throw new IOException("Recovered source dimensions changed.");
            var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[width * height * 4]; converted.CopyPixels(pixels, width * 4, 0);
            recoveredSourceSha = Convert.ToHexString(SHA256.HashData(pixels));
        }
        if (recoveredSourceSha != sourcePixelSha) throw new IOException("Recovered source pixels changed.");
        var weak = new List<WeakReference>();
        var windowType = product.GetType("MyCapture.App.Editing.AnnotationEditorWindow", true)!;
        var windowCtor = windowType.GetConstructors(Members).Single(c => c.GetParameters().Length == 8);
        for (int i = 0; i < 3; i++)
        {
            weak.Add(Measure("repeat-window-open-close-" + i, () => CreateClosedWindowReference(() =>
                (Window)windowCtor.Invoke([new FrozenFrame(bitmap, region, null, 0), region, bitmap, null, document, null, null, "Synthetic stress"]))));
        }
        // Diagnostic forced GC only for retained-object observation, outside editing measurements.
        var weakReferenceObservations = new List<int>();
        for (int pass = 0; pass < 2; pass++)
        {
            await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            weakReferenceObservations.Add(weak.Count(reference => reference.IsAlive));
        }
        double[] gaps = Heartbeats.Order().ToArray();
        var blobs = Directory.GetFiles(Path.Combine(_root, "drafts"), "*.blob", SearchOption.AllDirectories).Select(path => new FileInfo(path)).ToArray();
        string dll = product.Location;
        File.WriteAllText(Path.Combine(_root, "result.json"), JsonSerializer.Serialize(new
        {
            Status = "MEASURED", ProductDll = dll, ProductSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))), ProductVersion = FileVersionInfo.GetVersionInfo(dll).ProductVersion,
            FrameworkDescription = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, CoreRuntimeVersion = Environment.Version.ToString(),
            PresentationFrameworkDll = typeof(Application).Assembly.Location, PresentationFrameworkFileVersion = FileVersionInfo.GetVersionInfo(typeof(Application).Assembly.Location).FileVersion,
            Width = width, Height = height, PixelPattern = pattern, SourcePixelSha256 = sourcePixelSha, RecoveredSourcePixelSha256 = recoveredSourceSha,
            SourcePixelFormat = bitmap.Format.ToString(), SourceDpiX = bitmap.DpiX, SourceDpiY = bitmap.DpiY,
            EncoderMode = encoderMode, IndependentEncoderSamples = EncoderSamples,
            RecoveredFiles = drafts[0].Draft.Files,
            InitialAnnotations = count, FinalAnnotations = finalCount, InkPoints = 20L * inkPoints,
            Phases, InitialViewportSha256 = initialViewportSha, HeartbeatSamples = gaps.Length, MaxGapMs = gaps.Length == 0 ? 0 : gaps[^1], P95GapMs = gaps.Length == 0 ? 0 : gaps[(int)Math.Ceiling(gaps.Length * .95) - 1],
            BlobCount = blobs.Length, BlobBytes = blobs.Sum(file => file.Length), RecoveryRevision = drafts[0].Draft.Revision,
            PeakPrivateBytes = Interlocked.Read(ref _peakPrivate), RepeatedEditorWeakReferencesAlive = weak.Count(reference => reference.IsAlive),
            RepeatedEditorWeakReferenceObservations = weakReferenceObservations,
            Limits = "Actual product control/surface/controller/renderer/session/store; detached viewport rendering, fit-scale not product zoom UI. "
                + (pattern == "entropy" ? "Deterministic high-entropy synthetic RGB pixels. " : "Synthetic compressible pixels. ")
                + (encoderMode == "independent" ? "Owned bitmap PNG cache entry uses the diagnostic independent-pixel encoder. " : "")
                + "Actual Window construction/Close without Show/HWND presentation. Explicit forced GC outside timed phases. No screen/clipboard/registry/process interference; 60s/2GB guard aborts only this process; all evidence retained."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("RESULT=" + Path.Combine(_root, "result.json"));
        GC.KeepAlive(editor);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CreateClosedWindowReference(Func<Window> create)
    {
        Window window = create();
        // Imported unchanged document: normal Close needs no prompt. No Show or HWND.
        window.Close();
        return new WeakReference(window);
    }

    private static byte[] EncodeIndependentBitmap(BitmapSource source)
    {
        if (Application.Current.Dispatcher.CheckAccess()) throw new InvalidOperationException("Independent PNG preparation must run off UI.");
        var clock = Stopwatch.StartNew();
        // Retain the properties of the frame the product encoder would have used.
        BitmapFrame reference = BitmapFrame.Create(source);
        int stride = checked((source.PixelWidth * source.Format.BitsPerPixel + 7) / 8);
        byte[] pixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(pixels, stride, 0);
        BitmapSource independent = BitmapSource.Create(source.PixelWidth, source.PixelHeight,
            source.DpiX, source.DpiY, source.Format, source.Palette, pixels, stride);
        independent.Freeze();
        double copyMs = clock.Elapsed.TotalMilliseconds;
        var frame = BitmapFrame.Create(independent, reference.Thumbnail,
            reference.Metadata as BitmapMetadata, reference.ColorContexts);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(frame);
        using var stream = new MemoryStream(); encoder.Save(stream);
        byte[] result = stream.ToArray();
        EncoderSamples.Add(new { CopyMs = copyMs, TotalMs = clock.Elapsed.TotalMilliseconds,
            PixelBytes = pixels.Length, PngBytes = result.Length, Thread = Environment.CurrentManagedThreadId,
            Apartment = Thread.CurrentThread.GetApartmentState().ToString(), OffDispatcher = true });
        return result;
    }
}
