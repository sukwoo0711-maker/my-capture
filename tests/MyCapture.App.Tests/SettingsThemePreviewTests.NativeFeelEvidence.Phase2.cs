using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    private const string Phase2RenderOutput = "MYCAPTURE_PHASE2_EDITOR_EVIDENCE";
    private const BindingFlags Phase2Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly int[] Phase2OpeningTargets = [0, 83, 167, 250];
    private static readonly string[] Phase2Languages = ["ko-KR", "en-US"];

    // The same partial compiles on the phase-0 baseline. Optional product entrance/icon
    // members are discovered, never substituted into the baseline editor's visual tree.
    [NativeFeelSceneGroup(62)]
    [NativeFeelScene("phase2-editor-ko-workspace", "image-editor")]
    [NativeFeelScene("phase2-editor-ko-alt", "image-editor")]
    [NativeFeelScene("phase2-editor-ko-narrow", "image-editor")]
    [NativeFeelScene("phase2-editor-ko-icons", "image-editor-icon-comparison")]
    [NativeFeelScene("phase2-editor-ko-opening", "image-editor")]
    [NativeFeelScene("phase2-editor-ko-select", "image-editor")]
    [NativeFeelScene("phase2-editor-ko-rectangle", "image-editor")]
    [NativeFeelScene("phase2-editor-ko-arrow", "image-editor")]
    [NativeFeelScene("phase2-editor-ko-pen", "image-editor")]
    [NativeFeelScene("phase2-editor-ko-text", "image-editor")]
    [NativeFeelScene("phase2-editor-ko-image", "image-editor")]
    [NativeFeelScene("phase2-editor-ko-mosaic", "image-editor")]
    [NativeFeelScene("phase2-editor-ko-crop", "image-editor")]
    [NativeFeelScene("phase2-editor-ko-ellipse", "image-editor")]
    [NativeFeelScene("phase2-editor-ko-highlighter", "image-editor")]
    [NativeFeelScene("phase2-editor-ko-stepnumber", "image-editor")]
    [NativeFeelScene("phase2-editor-ko-blur", "image-editor")]
    [NativeFeelScene("phase2-editor-ko-eraser", "image-editor")]
    [NativeFeelScene("phase2-editor-en-workspace", "image-editor")]
    [NativeFeelScene("phase2-editor-en-alt", "image-editor")]
    [NativeFeelScene("phase2-editor-en-narrow", "image-editor")]
    [NativeFeelScene("phase2-editor-en-icons", "image-editor-icon-comparison")]
    [NativeFeelScene("phase2-editor-en-opening", "image-editor")]
    [NativeFeelScene("phase2-editor-en-select", "image-editor")]
    [NativeFeelScene("phase2-editor-en-rectangle", "image-editor")]
    [NativeFeelScene("phase2-editor-en-arrow", "image-editor")]
    [NativeFeelScene("phase2-editor-en-pen", "image-editor")]
    [NativeFeelScene("phase2-editor-en-text", "image-editor")]
    [NativeFeelScene("phase2-editor-en-image", "image-editor")]
    [NativeFeelScene("phase2-editor-en-mosaic", "image-editor")]
    [NativeFeelScene("phase2-editor-en-crop", "image-editor")]
    [NativeFeelScene("phase2-editor-en-ellipse", "image-editor")]
    [NativeFeelScene("phase2-editor-en-highlighter", "image-editor")]
    [NativeFeelScene("phase2-editor-en-stepnumber", "image-editor")]
    [NativeFeelScene("phase2-editor-en-blur", "image-editor")]
    [NativeFeelScene("phase2-editor-en-eraser", "image-editor")]
    private static void NativeFeelPhase2Editor(NativeFeelSceneContext context)
    {
        NativeFeelDesktopCapture desktop = context.Desktop;
        BitmapSource image = Phase2Source();
        Func<AnnotationDefaults>? oldRead = AnnotationEditorPreferences.Read;
        Func<bool>? oldHints = ShortcutPresentation.ReadEnabled;
        AnnotationEditorPreferences.Read = () => new AnnotationDefaults { LastTool = nameof(EditorTool.Select) };
        ShortcutPresentation.ReadEnabled = () => false;
        try
        {
            foreach (string language in Phase2Languages)
            {
                using var culture = UiText.UseLanguage(language);
                string prefix = "phase2-editor-" + (language == "ko-KR" ? "ko-" : "en-");
                var window = Phase2CreateWindow(image, desktop.Monitor);
                try
                {
                    desktop.Place(window, 1000, 640);
                    _ = new WindowInteropHelper(window).EnsureHandle();
                    desktop.Place(window, 1000, 640);
                    int firstFrame = desktop.Frames.Count;
                    desktop.Sequence(prefix + "opening", "phase2-editor-opening", desktop.Bounds(window), window.Show,
                        "Actual editor Show sequence; synthetic 540x240 source; source pixels and host motion preference retained. "
                        + "Nominal 0/83/167/250 ms targets are mapped to measured composed frames, never relabelled as exact samples.");
                    Phase2MarkOpeningFrames(desktop, firstFrame);
                    Assert.Equal(1000, window.ActualWidth, 3);
                    Assert.Equal(640, window.ActualHeight, 3);
                    Assert.Equal(1 / desktop.Scale, Phase2Surface(window).DipPerPixel, 6);
                    desktop.Still(prefix + "workspace", "phase2-editor-workspace", window,
                        "1000x640 DIP, 540x240 original physical pixels; language=" + language);
                    ShortcutPresentation.SetAltRevealed(window, true);
                    desktop.Still(prefix + "alt", "phase2-editor-alt", window, "Actual Alt-caption attached state; no native input.");
                    ShortcutPresentation.SetAltRevealed(window, false);
                    foreach (EditorTool tool in Enum.GetValues<EditorTool>())
                    {
                        window.Editor.ShortcutActions[tool.ToString()]();
                        desktop.Still(prefix + tool.ToString().ToLowerInvariant(), "phase2-editor-properties", window,
                            "Actual tool selection and property row: " + tool);
                    }
                    window.Editor.ShortcutActions[nameof(EditorTool.Select)]();
                    desktop.Place(window, 680, 460);
                    desktop.Still(prefix + "narrow", "phase2-editor-narrow", window, "680x460 DIP; unchanged original source.");
                    Assert.Equal(680, window.ActualWidth, 3);
                    Assert.Equal(460, window.ActualHeight, 3);
                }
                finally { ShortcutPresentation.SetAltRevealed(window, false); window.Editor.CancelPendingMosaics(closing: true); window.Close(); }

                var comparison = new Window
                {
                    Title = language == "ko-KR" ? "개인정보 가리기 아이콘 비교" : "Privacy redaction icon comparison",
                    Content = Phase2IconComparison(language), ShowActivated = false, ShowInTaskbar = false,
                };
                try
                {
                    desktop.Place(comparison, 600, 230);
                    comparison.Show();
                    desktop.Still(prefix + "icons", "phase2-editor-icons", comparison,
                        "Diagnostic comparison only: existing lock and masking candidate, both SymbolIcon 20-unit grid / 1.6 stroke at 20 and 64 DIP. No product UI is added.");
                }
                finally { comparison.Close(); }
            }
        }
        finally { AnnotationEditorPreferences.Read = oldRead; ShortcutPresentation.ReadEnabled = oldHints; }
    }

    private static void Phase2MarkOpeningFrames(NativeFeelDesktopCapture desktop, int firstFrame)
    {
        int[] candidates = Enumerable.Range(firstFrame, desktop.Frames.Count - firstFrame)
            .Where(index => desktop.Frames[index].BeginMs >= 0).ToArray();
        Assert.NotEmpty(candidates);
        foreach (int target in Phase2OpeningTargets)
        {
            int index = candidates.MinBy(index => Math.Abs(desktop.Frames[index].BeginMs - target));
            NativeFeelFrame frame = desktop.Frames[index];
            desktop.Frames[index] = frame with
            {
                Note = frame.Note + FormattableString.Invariant($" Target {target} ms: measured sample {frame.BeginMs:F3}..{frame.EndMs:F3} ms."),
            };
        }
    }

    /// <summary>
    /// Supporting WPF visual evidence for environments without desktop capture. The same
    /// source and actual editor controls are rendered before/after; this is not DWM evidence.
    /// Run alone with a new absolute MYCAPTURE_PHASE2_EDITOR_EVIDENCE output directory.
    /// </summary>
    [Fact]
    public Task Phase2_RenderEditorEvidence()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Phase2RenderOutput))) return Task.CompletedTask;
        return RunIsolated(nameof(Phase2_RenderEditorEvidence), Phase2RenderMatrix, hungAfter: TimeSpan.FromMinutes(5));
    }

    private static void Phase2RenderMatrix()
    {
        string output = TestDiagnosticArtifacts.NewDirectory(Environment.GetEnvironmentVariable(Phase2RenderOutput)!);
        Assert.Null(Application.Current);
        Assert.Null(AnnotationEditorPreferences.Read);
        Assert.Null(AnnotationEditorPreferences.Write);
        Assert.Null(AnnotationEditorPreferences.StampFolder);
        Application application = CreateLibraryApplication();
        bool? oldMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        DpiScale? oldSizing = AnnotationEditorWindow.SizingDpiOverrideForTest;
        Func<bool>? oldHints = ShortcutPresentation.ReadEnabled;
        var frames = new List<object>();
        string? failure = null;
        try
        {
            AnnotationEditorPreferences.Read = () => new AnnotationDefaults { LastTool = nameof(EditorTool.Select) };
            AnnotationEditorPreferences.Write = _ => { };
            AnnotationEditorPreferences.StampFolder = () => output;
            ShortcutPresentation.ReadEnabled = () => false;
            BitmapSource source = Phase2Source();
            TestDiagnosticArtifacts.SavePng(output, "source.png", source);
            foreach (double scale in new[] { 1d, 1.5 })
            foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.GlassLight })
            foreach (string language in Phase2Languages)
            {
                using var culture = UiText.UseLanguage(language);
                ThemeService.Apply(theme);
                FluidMotion.AnimationsEnabledOverrideForTest = false;
                AnnotationEditorWindow.SizingDpiOverrideForTest = new DpiScale(scale, scale);
                string combo = $"{(theme == AppTheme.Glass ? "dark" : "light")}-scale{scale * 100:0}-{(language == "ko-KR" ? "ko" : "en")}";
                using var host = new Phase2RenderHost(source, scale);
                void Save(string scene, double? target = null, double? begin = null, double? end = null)
                {
                    string file = $"{scene}-{combo}.png";
                    TestDiagnosticArtifacts.SavePng(output, file, Phase2Render(host.Window, scale));
                    frames.Add(new
                    {
                        scene, file, language, theme = theme.ToString(), scale, width = host.Window.ActualWidth,
                        height = host.Window.ActualHeight, source = Phase2ImageGeometry(host.Window),
                        inspectorHeight = Phase2Field<Border>(host.Window.Editor, "_inspectorPanel").ActualHeight,
                        targetMs = target, beginMs = begin, endMs = end,
                    });
                }
                Save("workspace");
                ShortcutPresentation.SetAltRevealed(host.Window, true);
                Phase2Settle(host.Window);
                Save("alt");
                ShortcutPresentation.SetAltRevealed(host.Window, false);
                foreach (EditorTool tool in Enum.GetValues<EditorTool>())
                {
                    host.Window.Editor.ShortcutActions[tool.ToString()]();
                    Phase2Settle(host.Window);
                    Save("tool-" + tool.ToString().ToLowerInvariant());
                }
                host.Window.Editor.ShortcutActions[nameof(EditorTool.Select)]();
                host.Resize(680, 460);
                Save("narrow");
                host.Resize(1000, 640);
                Grid icons = Phase2IconComparison(language);
                VisualTreeHelper.SetRootDpi(icons, new DpiScale(scale, scale));
                icons.Measure(new Size(600, 200)); icons.Arrange(new Rect(0, 0, 600, 200)); icons.UpdateLayout();
                TestDiagnosticArtifacts.SavePng(output, "privacy-icons-" + combo + ".png", Phase2Render(icons, scale));

                MethodInfo? entrance = typeof(AnnotationEditorControl).GetMethod("BeginWorkspaceEntrance", Phase2Members);
                FluidMotion.AnimationsEnabledOverrideForTest = oldMotion;
                Phase2ImagePosition geometry = Phase2ImageGeometry(host.Window);
                typeof(AnnotationEditorControl).GetMethod("PrepareWorkspaceEntrance", Phase2Members)?.Invoke(host.Window.Editor, null);
                var clock = Stopwatch.StartNew();
                entrance?.Invoke(host.Window.Editor, null);
                foreach (int target in Phase2OpeningTargets)
                {
                    NativeFeelDesktopCapture.Pump(Math.Max(0, target - clock.Elapsed.TotalMilliseconds));
                    double begin = clock.Elapsed.TotalMilliseconds;
                    Phase2AssertImageGeometry(geometry, Phase2ImageGeometry(host.Window));
                    string file = $"opening-target{target:000}-{combo}.png";
                    BitmapSource bitmap = Phase2Render(host.Window, scale);
                    double end = clock.Elapsed.TotalMilliseconds;
                    TestDiagnosticArtifacts.SavePng(output, file, bitmap);
                    frames.Add(new
                    {
                        scene = "opening", file, language, theme = theme.ToString(), scale, targetMs = target,
                        beginMs = begin, endMs = end, entranceAvailable = entrance is not null,
                        motionEnabled = FluidMotion.AnimationsEnabled, source = Phase2ImageGeometry(host.Window),
                        note = "Measured WPF render after the real chrome entrance entry point with source layout already settled; not desktop Show timing.",
                    });
                }
            }
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            TestDiagnosticArtifacts.WriteJson(output, "manifest.json", new
            {
                schema = 1, capture = "RenderTargetBitmap of the real WPF editor; no desktop pixels or fabricated native caption",
                syntheticSource = "540x240 pixels, identical bilingual order-table source for all states and both revisions",
                scales = new[] { 1d, 1.5 }, dimensions = new[] { "1000x640", "680x460" },
                motion = "Target times are requests; beginMs/endMs are measured. Baseline lacks the phase-2 entrance entry point. Host motion preference is retained.",
                iconComparison = "Existing lock and masking candidate, same 20-unit grid and 1.6-unit stroke; diagnostic board only",
                frames, failure,
            });
            FluidMotion.AnimationsEnabledOverrideForTest = oldMotion;
            AnnotationEditorWindow.SizingDpiOverrideForTest = oldSizing;
            ShortcutPresentation.ReadEnabled = oldHints;
            AnnotationEditorPreferences.Read = null;
            AnnotationEditorPreferences.Write = null;
            AnnotationEditorPreferences.StampFolder = null;
            application.Shutdown();
        }
    }

    private sealed class Phase2RenderHost : IDisposable
    {
        internal AnnotationEditorWindow Window { get; }
        private readonly WindowTrackSizeScope _tracking;
        internal Phase2RenderHost(BitmapSource image, double scale)
        {
            var monitor = new MonitorInfo(@"\\.\DISPLAY_PHASE2_FIXTURE", new RectD(0, 0, 4096, 2160), new RectD(0, 0, 4096, 2160), 96, true);
            Window = Phase2CreateWindow(image, monitor);
            Window.Left = Window.Top = -6000;
            Window.Width = 1000; Window.Height = 640;
            Window.WindowStartupLocation = WindowStartupLocation.Manual;
            EventHandler activate = (EventHandler)typeof(AnnotationEditorWindow).GetMethod("OnContentRendered", Phase2Members,
                [typeof(object), typeof(EventArgs)])!.CreateDelegate(typeof(EventHandler), Window);
            Window.ContentRendered -= activate;
            _tracking = new WindowTrackSizeScope(Window, new Size(4096, 2160));
            try
            {
                Window.Show();
                Resize(1000, 640);
                AnnotationEditorSurface surface = Phase2Surface(Window);
                DpiScale old = VisualTreeHelper.GetDpi(surface);
                VisualTreeHelper.SetRootDpi(Window, new DpiScale(scale, scale));
                typeof(AnnotationEditorSurface).GetMethod("OnDpiChanged", Phase2Members)!.Invoke(surface, [old, new DpiScale(scale, scale)]);
                Phase2Settle(Window);
                Window.Editor.ShortcutActions[nameof(EditorTool.Select)]();
                typeof(AnnotationEditorControl).GetMethod("CompleteWorkspaceEntrance", Phase2Members)?.Invoke(Window.Editor, null);
                Phase2Settle(Window);
                Assert.Equal(scale, VisualTreeHelper.GetDpi(surface).DpiScaleX, 6);
                Assert.Equal(1 / scale, surface.DipPerPixel, 6);
            }
            catch { Window.Close(); _tracking.Dispose(); throw; }
        }
        internal void Resize(double width, double height)
        {
            Window.Width = width; Window.Height = height;
            Window.Left = Window.Top = -6000;
            Phase2Settle(Window);
            Assert.Equal(width, Window.ActualWidth, 3);
            Assert.Equal(height, Window.ActualHeight, 3);
        }
        public void Dispose()
        {
            ShortcutPresentation.SetAltRevealed(Window, false);
            Window.Editor.CancelPendingMosaics(closing: true);
            Window.Close();
            _tracking.Dispose();
        }
    }

    private static AnnotationEditorWindow Phase2CreateWindow(BitmapSource image, MonitorInfo monitor)
    {
        var region = new RectD(0, 0, image.PixelWidth, image.PixelHeight);
        var window = new AnnotationEditorWindow(new FrozenFrame(image, region, monitor, 0), region, image, pointerMonitor: monitor)
        {
            ShowActivated = false, ShowInTaskbar = false, ConfirmClose = () => EditorCloseChoice.DontSave,
        };
        window.Editor.PickStampImagePath = () => null;
        return window;
    }

    private static Grid Phase2IconComparison(string language)
    {
        var grid = new Grid { Width = 600, Height = 200 };
        grid.SetResourceReference(Panel.BackgroundProperty, "Surface.Base");
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        Geometry mask = Phase2MaskGeometry();
        Geometry locked = (Geometry)Application.Current.FindResource("Icon.Lock");
        for (int column = 0; column < 2; column++)
        {
            var stack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24) };
            var label = new TextBlock
            {
                Text = column == 0 ? (language == "ko-KR" ? "기존 자물쇠" : "Existing lock") : (language == "ko-KR" ? "개인정보 가림" : "Privacy mask"),
                FontSize = TypeScale.Body, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
            stack.Children.Add(label);
            foreach (double size in new[] { 20d, 64d })
            {
                var icon = SymbolIcon.Create(column == 0 ? locked : mask, size);
                icon.SetResourceReference(SymbolIcon.ForegroundProperty, "Text.Primary");
                icon.Margin = new Thickness(0, 0, 0, 12);
                stack.Children.Add(icon);
            }
            Grid.SetColumn(stack, column); grid.Children.Add(stack);
        }
        return grid;
    }

    private static Geometry Phase2MaskGeometry()
    {
        // The candidate is shown on both revisions; only the current editor uses it.
        PropertyInfo? property = typeof(AnnotationEditorControl).GetProperty("PrivacyMaskGeometry", Phase2Members);
        if (property?.GetValue(null) is Geometry geometry) return geometry;
        FieldInfo? field = typeof(AnnotationEditorControl).GetField("PrivacyMaskGeometry", Phase2Members);
        if (field?.GetValue(null) is Geometry fieldGeometry) return fieldGeometry;
        return Geometry.Parse("M4,3.5 H16 V16.5 H4 Z M7,6.5 H11 M6.5,9.25 H13.5 V11.75 H6.5 Z M7,14 H10");
    }

    private sealed record Phase2ImagePosition(double X, double Y, double Width, double Height, double DipPerPixel);
    private static Phase2ImagePosition Phase2ImageGeometry(AnnotationEditorWindow window)
    {
        AnnotationEditorSurface surface = Phase2Surface(window);
        Rect bounds = surface.TransformToAncestor(window).TransformBounds(surface.FrameRectDip);
        return new(bounds.X, bounds.Y, bounds.Width, bounds.Height, surface.DipPerPixel);
    }
    private static void Phase2AssertImageGeometry(Phase2ImagePosition expected, Phase2ImagePosition actual)
    {
        Assert.Equal(expected.X, actual.X, 6); Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Width, actual.Width, 6); Assert.Equal(expected.Height, actual.Height, 6);
        Assert.Equal(expected.DipPerPixel, actual.DipPerPixel, 6);
    }
    private static AnnotationEditorSurface Phase2Surface(AnnotationEditorWindow window) => Phase2Field<AnnotationEditorSurface>(window.Editor, "_surface");
    private static T Phase2Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Phase2Members)!.GetValue(owner)!;
    private static void Phase2Settle(Window window)
    {
        for (int i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        window.UpdateLayout();
    }
    private static BitmapSource Phase2Render(FrameworkElement element, double scale)
    {
        double width = element.ActualWidth, height = element.ActualHeight;
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
        {
            context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(element)
            {
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height),
            }, null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(drawing); bitmap.Freeze(); return bitmap;
    }
    private static BitmapSource Phase2Source()
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(247, 248, 250)), null, new Rect(0, 0, 540, 240));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(31, 111, 235)), null, new Rect(0, 0, 540, 46));
            void Text(string value, double x, double y, double size, Brush brush) => context.DrawText(
                new FormattedText(value, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight,
                    new Typeface("Malgun Gothic"), size, brush, 1), new Point(x, y));
            Text("주문 응답 / Order response", 20, 10, 18, Brushes.White);
            string[] keys = ["order_id", "amount", "status", "contact"];
            string[] values = ["AB2406180042", "12,000 KRW", "PAID / 결제 완료", "review@example.test"];
            for (int row = 0; row < keys.Length; row++)
            {
                double y = 62 + row * 42;
                context.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(208, 213, 221)), 1), new Point(20, y + 30), new Point(520, y + 30));
                Text(keys[row], 20, y, 16, Brushes.Black);
                Text(values[row], 182, y, 16, Brushes.Black);
            }
        }
        var bitmap = new RenderTargetBitmap(540, 240, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
}
