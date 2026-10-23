using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in constructor/first-render diagnostics. Separate child processes keep the first window
/// and first standalone control cold. Component rebuilds are warm, isolated diagnostic samples,
/// not additive slices of a constructor. No timing value is a test assertion.
/// </summary>
public sealed class EditorConstructionProbeTests
{
    internal const string OutputVariable = "MYCAPTURE_EDITOR_CONSTRUCTION_PERF";
    private const string ChildMode = "MYCAPTURE_EDITOR_CONSTRUCTION_CHILD";
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly List<string> _rows = ["mode,scenario,stage,elapsed_ms,ui_thread_allocated_bytes,elements_after_stage"];

    [EditorConstructionPerfFact]
    public async Task MeasureConstructionAndFirstRender()
    {
        string output = DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable(OutputVariable)!);
        if (Environment.GetEnvironmentVariable(ChildMode) is { Length: > 0 } mode)
        {
            StaTestHost.Run(() => Run(output, mode));
            File.WriteAllText(DiagnosticOutputPaths.Child(output, mode + "-completed.txt"), "PASS");
            return;
        }

        // A unique run directory preserves earlier evidence and needs no receipt deletion.
        string run = DiagnosticOutputPaths.Create(Path.Combine(output,
            "construction-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")));
        string runtimeRoot = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent!.Parent!.Parent!.FullName;
        string dotnet = Path.Combine(runtimeRoot, "dotnet.exe");
        Assert.True(File.Exists(dotnet));
        foreach (string childMode in new[] { "windows", "controls", "components" })
        {
            var start = new ProcessStartInfo(dotnet)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(EditorConstructionProbeTests).Assembly.Location);
            start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + typeof(EditorConstructionProbeTests).FullName + "." + nameof(MeasureConstructionAndFirstRender));
            start.ArgumentList.Add("--Logger:console;verbosity=normal");
            start.Environment[OutputVariable] = run;
            start.Environment[ChildMode] = childMode;
            start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
            using Process child = Process.Start(start) ?? throw new IOException("Could not start the construction probe.");
            Task<string> stdout = child.StandardOutput.ReadToEndAsync();
            Task<string> stderr = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                await child.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
            string console = await stdout + Environment.NewLine + await stderr;
            File.WriteAllText(DiagnosticOutputPaths.Child(run, childMode + "-console.txt"), console);
            Assert.True(child.ExitCode == 0, console);
            string receipt = DiagnosticOutputPaths.Child(run, childMode + "-completed.txt");
            Assert.True(File.Exists(receipt), "Probe child did not complete. " + console);
            Assert.Equal("PASS", File.ReadAllText(receipt));
        }
    }

    private void Run(string output, string mode)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        ThemeService.ApplyFromSettings(new AppSettings().General);
        AnnotationEditorPreferences.Read = static () => new AnnotationDefaults();
        AnnotationEditorPreferences.ReadShowSourceWindowTitle = static () => false;
        AnnotationEditorPreferences.Write = null;
        BitmapSource image = BitmapSource.Create(1920, 1080, 96, 96, PixelFormats.Bgr32, null,
            new byte[1920 * 1080 * 4], 1920 * 4);
        image.Freeze();
        var region = new RectD(0, 0, image.PixelWidth, image.PixelHeight);
        var frame = new FrozenFrame(image, region, null, 0);
        MonitorInfo primary = MonitorEnumerator.GetPrimary();
        WriteEnvironment(output, mode, primary);
        try
        {
            if (mode == "windows")
            {
                // Same heavy annotation fixture as EditorResponsivenessProbeTests; no persistence
                // or draft wiring here, so its total open metric remains the end-to-end authority.
                MeasureWindow("open-first", frame, region, image, primary, null, null);
                var many = CreateManyItems(image.PixelWidth, image.PixelHeight);
                MeasureWindow("open-200-second", frame, region, image, primary, many.Document, many.Assets);
                MeasureWindow("open-empty-warm", frame, region, image, primary, null, null);
            }
            else if (mode == "controls")
            {
                for (int sample = 0; sample < 3; sample++)
                {
                    string scenario = sample == 0 ? "control-first" : "control-warm-" + sample;
                    AnnotationEditorControl editor = Measure(mode, scenario, "new-control", () =>
                        Create<AnnotationEditorControl>(frame, region, image, primary, null, null));
                    Measure(mode, scenario, "measure-arrange", () =>
                    {
                        editor.Measure(new Size(1000, 640));
                        editor.Arrange(new Rect(0, 0, 1000, 640));
                        return editor;
                    });
                    Measure(mode, scenario, "render-target-bitmap", () =>
                    {
                        var target = new RenderTargetBitmap(1000, 640, 96, 96, PixelFormats.Pbgra32);
                        target.Render(editor);
                        return editor;
                    });
                    GC.KeepAlive(editor);
                }
            }
            else if (mode == "components")
            {
                foreach (string method in new[] { "BuildInspector", "BuildCommandBar", "BuildToolRail", "BuildStatusBar" })
                for (int sample = 1; sample <= 3; sample++)
                {
                    // Build only once on each throwaway, initialized owner. Rebuilding mutates its
                    // private fields; the owner is never shown or used for an interaction afterward.
                    AnnotationEditorControl owner = Create<AnnotationEditorControl>(frame, region, image, primary, null, null);
                    MethodInfo builder = typeof(AnnotationEditorControl).GetMethod(method, InstanceMembers)!;
                    Assert.NotNull(builder);
                    FrameworkElement tree = Measure(mode, method + "-" + sample, "warm-rebuild", () =>
                        (FrameworkElement)builder.Invoke(owner, null)!);
                    Measure(mode, method + "-" + sample, "detached-measure-arrange", () =>
                    {
                        tree.Measure(new Size(1000, 640));
                        tree.Arrange(new Rect(new Point(), tree.DesiredSize));
                        return tree;
                    });
                    GC.KeepAlive(owner);
                }
                MeasureSourceChip(frame, region, image, primary);
            }
            else throw new ArgumentException("Unknown probe mode: " + mode);
        }
        finally
        {
            File.WriteAllLines(DiagnosticOutputPaths.Child(output, mode + ".csv"), _rows, Encoding.UTF8);
            AnnotationEditorPreferences.Read = null;
            AnnotationEditorPreferences.ReadShowSourceWindowTitle = null;
            AnnotationEditorPreferences.Write = null;
        }
    }

    // Kept identical to ManyItems.Create, except for the version-compatible pen setter.
    private static (AnnotationDocument Document, Dictionary<string, BitmapSource> Assets, RectD Target) CreateManyItems(int width, int height)
    {
        var random = new Random(200);
        var document = AnnotationDocument.CreateFor(width, height);
        var assets = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        ColorRgba[] colors =
        [
            ColorRgba.FromRgb(0xEF, 0x44, 0x44), ColorRgba.FromRgb(0x22, 0xC5, 0x5E), ColorRgba.FromRgb(0x3B, 0x82, 0xF6),
            ColorRgba.FromRgb(0xF5, 0x9E, 0x0B), ColorRgba.FromRgb(0xA8, 0x55, 0xF7), ColorRgba.White,
        ];
        RectD Box(double minW, double maxW, double minH, double maxH)
        {
            double w = width * (minW + random.NextDouble() * (maxW - minW));
            double h = height * (minH + random.NextDouble() * (maxH - minH));
            return new RectD(width * 0.02 + random.NextDouble() * (width * 0.96 - w), height * 0.02 + random.NextDouble() * (height * 0.96 - h), w, h);
        }
        PointD Point() => new(width * (0.03 + random.NextDouble() * 0.94), height * (0.03 + random.NextDouble() * 0.94));
        ColorRgba Color() => colors[random.Next(colors.Length)];

        for (int i = 0; i < 50; i++)
            document.Add(new RectangleAnnotation { Rect = Box(0.02, 0.12, 0.03, 0.15), Stroke = Color(), StrokeThickness = 4 });
        for (int i = 0; i < 25; i++)
            document.Add(new EllipseAnnotation { Rect = Box(0.02, 0.10, 0.03, 0.12), Stroke = Color(), StrokeThickness = 4 });
        for (int i = 0; i < 25; i++)
            document.Add(PolylineAnnotation.CreateArrow(Point(), Point(), Color(), 5));
        for (int i = 0; i < 40; i++)
        {
            PointD at = Point();
            var points = new List<PointD>(150);
            for (int p = 0; p < 150; p++)
            {
                at = new PointD(Math.Clamp(at.X + random.Next(-12, 13), 2, width - 2), Math.Clamp(at.Y + random.Next(-12, 13), 2, height - 2));
                points.Add(at);
            }

            var pen = new PenAnnotation { Stroke = Color(), StrokeThickness = 4 };
            // Pen.Points changed from List<PointD> to PenPointCollection after d30feb74.
            // Resolve only this setter reflectively; fixture points and seed stay identical.
            PropertyInfo pointsProperty = typeof(PenAnnotation).GetProperty("Points")!;
            object values = pointsProperty.PropertyType.IsInstanceOfType(points)
                ? points : Activator.CreateInstance(pointsProperty.PropertyType, new object[] { points })!;
            pointsProperty.SetValue(pen, values);
            document.Add(pen);
        }

        for (int i = 0; i < 40; i++)
        {
            PointD at = Point();
            document.Add(new TextAnnotation
            {
                Rect = new RectD(Math.Min(at.X, width * 0.8), at.Y, 320, 40),
                Text = i % 4 == 0 ? $"설명 {i}: 이 영역을 확인하십시오\n두 번째 줄 {i}" : $"메모 {i} — check this value",
                FontSize = 28,
                FontFamily = "Malgun Gothic",
                Foreground = Color(),
                HasOutline = true,
                OutlineStyle = TextOutlineStyle.Consistent,
                Background = i % 3 == 0 ? ColorRgba.Black : ColorRgba.Transparent,
                AutoSize = true,
            });
        }

        for (int i = 0; i < 10; i++)
            document.Add(new BlurAnnotation { Rect = Box(0.03, 0.10, 0.03, 0.10), Radius = BlurAnnotation.DefaultRadius });
        for (int i = 0; i < 9; i++)
        {
            string name = $"asset-{i + 1:D2}.png";
            byte[] patch = SyntheticImage.PhotoLikePixels(160, 120, 900 + i);
            BitmapSource bitmap = BitmapSource.Create(160, 120, 96, 96, PixelFormats.Bgr32, null, patch, 160 * 4);
            bitmap.Freeze();
            assets[name] = bitmap;
            RectD box = Box(0.04, 0.08, 0.05, 0.10);
            document.Add(new ImageAnnotation { AssetFileName = name, SourceWidth = 160, SourceHeight = 120, Rect = box });
        }

        var target = new RectD(width * 0.40, height * 0.10, width * 0.15, height * 0.15);
        document.Add(new RectangleAnnotation { Rect = target, Stroke = ColorRgba.FromRgb(0xEF, 0x44, 0x44), StrokeThickness = 6 });
        return (document, assets, target);
    }

    private void MeasureWindow(string scenario, FrozenFrame frame, RectD region, BitmapSource image,
        MonitorInfo monitor, AnnotationDocument? document, IReadOnlyDictionary<string, BitmapSource>? assets)
    {
        AnnotationEditorWindow window = Measure("windows", scenario, "new-window-includes-control", () =>
            Create<AnnotationEditorWindow>(frame, region, image, monitor, document, assets));
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -30000;
        window.Top = -30000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.ConfirmClose = static () => EditorCloseChoice.DontSave;
        bool rendered = false;
        var dispatcherFrame = new DispatcherFrame();
        long shownAt = 0;
        double firstRenderMs = 0;
        window.ContentRendered += (_, _) =>
        {
            if (rendered) return;
            firstRenderMs = Stopwatch.GetElapsedTime(shownAt).TotalMilliseconds;
            rendered = true;
            dispatcherFrame.Continue = false;
        };
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Send,
            (_, _) => dispatcherFrame.Continue = false, Dispatcher.CurrentDispatcher);
        try
        {
            shownAt = Stopwatch.GetTimestamp();
            Measure("windows", scenario, "show-synchronous", () => { window.Show(); return window; });
            if (!rendered) Dispatcher.PushFrame(dispatcherFrame);
            Assert.True(rendered, "ContentRendered was not observed; the desktop may be unavailable.");
            _rows.Add(Csv("windows", scenario, "show-to-content-rendered", firstRenderMs, null, CountElements(window)));
        }
        finally
        {
            timer.Stop();
            window.Close();
        }
    }

    private void MeasureSourceChip(FrozenFrame frame, RectD region, BitmapSource image, MonitorInfo monitor)
    {
        // Reflection keeps this file usable against d30feb74, which predates source provenance.
        FieldInfo? provenance = typeof(AnnotationEditorControl).GetField("_provenance", InstanceMembers);
        MethodInfo? create = provenance?.FieldType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
        if (create is null)
        {
            _rows.Add("components,BuildStatusBar-source,unavailable-on-this-revision,,,");
            return;
        }
        object?[] arguments = create.GetParameters().Select(parameter =>
            parameter.Name == "appName" ? (object?)"Construction probe" : parameter.DefaultValue).ToArray();
        object source = create.Invoke(null, arguments)!;
        for (int sample = 1; sample <= 3; sample++)
        {
            AnnotationEditorControl owner = Create<AnnotationEditorControl>(frame, region, image, monitor, null, null, source);
            MethodInfo builder = typeof(AnnotationEditorControl).GetMethod("BuildStatusBar", InstanceMembers)!;
            Measure("components", "BuildStatusBar-source-" + sample, "warm-rebuild", () =>
                (FrameworkElement)builder.Invoke(owner, null)!);
        }
    }

    private static T Create<T>(FrozenFrame frame, RectD region, BitmapSource image, MonitorInfo monitor,
        AnnotationDocument? document, IReadOnlyDictionary<string, BitmapSource>? assets, object? provenance = null)
    {
        // Both revisions expose these named parameters, with provenance added only after main.
        ConstructorInfo constructor = typeof(T).GetConstructors(InstanceMembers)
            .OrderByDescending(candidate => candidate.GetParameters().Length).First();
        object?[] arguments = constructor.GetParameters().Select(parameter => parameter.Name switch
        {
            "frame" or "sourceFrame" => (object)frame,
            "bitmapRegion" or "sourceRegion" => region,
            "selectedBitmap" => image,
            "initialDocument" => document,
            "initialAssets" => assets,
            "sourceWindowTitle" => "Construction probe",
            "pointerMonitor" => monitor,
            "provenance" => provenance,
            _ when parameter.HasDefaultValue => parameter.DefaultValue,
            _ => throw new InvalidOperationException("Unknown required constructor parameter: " + parameter.Name),
        }).ToArray();
        return (T)constructor.Invoke(arguments);
    }

    private T Measure<T>(string mode, string scenario, string stage, Func<T> action) where T : DependencyObject
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        T result = action();
        double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        // Counting is outside the timed region. Both trees are traversed, once per object, so
        // ContentControls without a template and already-realized visual children are included.
        _rows.Add(Csv(mode, scenario, stage, milliseconds, bytes, CountElements(result)));
        return result;
    }

    private static int CountElements(DependencyObject root)
    {
        var visited = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        void Visit(DependencyObject item)
        {
            if (!visited.Add(item)) return;
            foreach (object child in LogicalTreeHelper.GetChildren(item))
                if (child is DependencyObject dependency) Visit(dependency);
            if (item is Visual)
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) Visit(VisualTreeHelper.GetChild(item, i));
        }
        Visit(root);
        return visited.Count(item => item is FrameworkElement);
    }

    private static string Csv(string mode, string scenario, string stage, double milliseconds, long? bytes, int elements) =>
        string.Join(",", mode, scenario, stage, milliseconds.ToString("F3", CultureInfo.InvariantCulture),
            bytes?.ToString(CultureInfo.InvariantCulture) ?? "", elements.ToString(CultureInfo.InvariantCulture));

    private static void WriteEnvironment(string output, string mode, MonitorInfo monitor)
    {
        Assembly product = typeof(AnnotationEditorControl).Assembly;
        File.WriteAllLines(DiagnosticOutputPaths.Child(output, mode + "-environment.txt"),
        [
            "Product assembly: " + product.Location,
            "Product informational version: " + product.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            "Runtime: " + RuntimeInformation.FrameworkDescription,
            "OS: " + Environment.OSVersion,
            "Monitor: " + monitor.Bounds + "; scale=" + monitor.ScaleFactor.ToString(CultureInfo.InvariantCulture),
            "WPF rendering tier: " + (RenderCapability.Tier >> 16),
            "Mode: " + mode,
            "Image: synthetic blank frozen Bgr32 1920x1080; fixed default preferences; no profile, persistence, clipboard or app startup.",
            "Windows: first empty, second the responsiveness probe's ManyItems fixture (200), third empty. Independent from the controls process.",
            "Window construction includes its control. Control and window rows must not be added together.",
            "ContentRendered is WPF's first-content-render notification, not physical screen presentation. Windows are off screen, matching the responsiveness probe.",
            "Component rows: a private builder reinvoked on a separate initialized throwaway owner. Warm reconstruction, not original constructor attribution; not additive.",
            "BuildCommandBar includes both edit/crop modes and their shortcut/Alt face construction. Source sample uses synthetic app provenance when supported.",
            "Reflection constructor lookup/invocation is inside constructor rows, in both revisions; component MethodInfo lookup is outside its timed row.",
            "UI allocation excludes worker-thread allocations. Tree counts run outside the timer. No wall-time thresholds are asserted.",
        ], Encoding.UTF8);
    }
}

internal sealed class EditorConstructionPerfFactAttribute : FactAttribute
{
    public EditorConstructionPerfFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EditorConstructionProbeTests.OutputVariable)))
            Skip = "Opt-in construction probe: set MYCAPTURE_EDITOR_CONSTRUCTION_PERF to an output directory.";
    }
}
