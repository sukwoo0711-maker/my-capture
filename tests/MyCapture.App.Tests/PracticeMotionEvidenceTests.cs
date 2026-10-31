using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using MyCapture.Platform.Display;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Opt-in direct WPF renders; never reads the desktop or the real clipboard.</summary>
public sealed class PracticeMotionEvidenceTests
{
    [Fact]
    public Task StaticScenesAndMeasuredMotionFilmstrips() => SettingsThemePreviewTests.RunIsolated(
        nameof(StaticScenesAndMeasuredMotionFilmstrips), Render, typeof(PracticeMotionEvidenceTests));

    private static void Render()
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_TUTORIAL_MOTION_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string root = DiagnosticOutputPaths.Create(requested);
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_TUTORIAL_MOTION_PHASE") ?? "after";
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string dictionary in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{dictionary}.xaml") });
        bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        var measurements = new List<object>();
        try
        {
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            foreach (string locale in new[] { "ko-KR", "en-US" })
            {
                using var language = UiText.UseLanguage(locale);
                UiText.Configure(locale[..2]);
                foreach ((string name, AppTheme theme) in new[] { ("dark", AppTheme.Glass), ("light", AppTheme.GlassLight) })
                {
                    ThemeService.Apply(theme);
                    var monitor = Laptop();
                    WelcomeWindow guide = Create(monitor, true);
                    guide.LimitToWorkArea(monitor);
                    using var tracking = new WindowTrackSizeScope(guide, new Size(1366, 768));
                    try
                    {
                        guide.Show();
                        Drain(guide);
                        Assert.Equal(640, guide.ActualWidth);
                        Assert.Equal(720, guide.ActualHeight);
                        SaveScene("A1");
                        PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.A4);
                        SaveScene("A4-waiting");
                        PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.B1);
                        Assert.True(guide.SuccessShown);
                        SaveScene("B1-success");
                        guide.NotifyHotkeyFired(GlobalHotkeyCommand.PasteToScreen);
                        Assert.True(guide.KeyPressed);
                        SaveScene("B1-key-pressed");
                        PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.B2);
                        SaveScene("B2-waiting");
                        PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.C2);
                        SaveScene("C2");
                        PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.Results);
                        SaveScene("Results");
                        var body = Assert.IsAssignableFrom<FrameworkElement>(guide.Scroller.Content);
                        measurements.Add(new
                        {
                            phase, locale, theme = name, width = guide.ActualWidth, height = guide.ActualHeight,
                            contentHeight = body.ActualHeight, contentDesiredHeight = body.DesiredSize.Height,
                            extentHeight = guide.Scroller.ExtentHeight, viewportHeight = guide.Scroller.ViewportHeight,
                            contentMargin = new { body.Margin.Left, body.Margin.Top, body.Margin.Right, body.Margin.Bottom },
                        });
                        void SaveScene(string scene)
                        {
                            Drain(guide);
                            Save(RenderWindow(guide), DiagnosticOutputPaths.Child(root, $"{phase}-{locale[..2]}-{name}-{scene}.png"));
                        }
                    }
                    finally { guide.Close(); }
                }
            }
            File.WriteAllText(DiagnosticOutputPaths.Child(root, $"measurements-{phase}.json"), JsonSerializer.Serialize(measurements,
                new JsonSerializerOptions { WriteIndented = true }));
            if (phase != "before") RenderFilmstrips(root);
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = previousMotion;
            application.Shutdown();
        }
    }

    private static MonitorInfo Laptop() => new("motion-evidence", new RectD(0, 0, 1366, 768), new RectD(0, 0, 1366, 728), 96, true);

    private static WelcomeWindow Create(MonitorInfo monitor, bool reducedMotion) => OnboardingGuideTests.Create(new(),
        new WelcomeOptions { ReducedMotion = reducedMotion, TextScale = 1, MonitorProvider = () => monitor, HintDelay = TimeSpan.FromMinutes(5) });

    private static void RenderFilmstrips(string root)
    {
        using var language = UiText.UseLanguage("ko-KR");
        UiText.Configure("ko");
        ThemeService.Apply(AppTheme.Glass);
        FluidMotion.AnimationsEnabledOverrideForTest = true;
        var timings = new List<object>();
        Filmstrip("forward-B0-B1", [0, 40, 83, 110, 140, 180, 250, 340],
            guide => guide.Session.StartGroup(PracticeStep.B0), guide => PracticeJourneyDriver.Complete(guide.Session));
        Filmstrip("back-B1-B0", [0, 40, 83, 110, 140, 180, 250, 340],
            guide => PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.B1), guide => guide.Session.Back());
        Filmstrip("chip-appear", [0, 60, 120, 167, 260],
            guide => guide.Session.StartGroup(PracticeStep.B0), guide => PracticeJourneyDriver.Complete(guide.Session));
        Filmstrip("segment-fill", [0, 60, 125, 190, 250],
            guide => guide.Session.StartGroup(PracticeStep.B0), guide => PracticeJourneyDriver.Complete(guide.Session));
        Filmstrip("keycap-press", [0, 40, 83, 160, 250],
            guide => PracticeJourneyDriver.AdvanceTo(guide.Session, PracticeStep.B1),
            guide => guide.NotifyHotkeyFired(GlobalHotkeyCommand.PasteToScreen));
        File.WriteAllText(DiagnosticOutputPaths.Child(root, "filmstrip-timings.json"), JsonSerializer.Serialize(timings,
            new JsonSerializerOptions { WriteIndented = true }));

        void Filmstrip(string name, int[] requestedMs, Action<WelcomeWindow> prepare, Action<WelcomeWindow> trigger)
        {
            var monitor = Laptop();
            WelcomeWindow guide = Create(monitor, false);
            guide.LimitToWorkArea(monitor);
            using var tracking = new WindowTrackSizeScope(guide, new Size(1366, 768));
            try
            {
                prepare(guide);
                guide.Show();
                Drain(guide);
                PumpUntil(() => MotionAtRest(guide), TimeSpan.FromSeconds(4));
                var frames = new List<BitmapSource>();
                var elapsed = new List<double>();
                var watch = Stopwatch.StartNew();
                trigger(guide);
                foreach (int target in requestedMs)
                {
                    PumpUntil(() => watch.Elapsed.TotalMilliseconds >= target, TimeSpan.FromSeconds(3));
                    guide.UpdateLayout();
                    elapsed.Add(Math.Round(watch.Elapsed.TotalMilliseconds, 2));
                    frames.Add(RenderWindow(guide));
                }
                SaveStrip(frames, elapsed, DiagnosticOutputPaths.Child(root, $"filmstrip-{name}.png"));
                timings.Add(new { motion = name, requestedMs, measuredMs = elapsed });
                PumpUntil(() => MotionAtRest(guide), TimeSpan.FromSeconds(4));
            }
            finally { guide.Close(); }
        }
    }

    private static bool MotionAtRest(WelcomeWindow guide)
    {
        // The shared window entrance leaves a stopped opacity clock attached. Its visible
        // endpoint must be settled, but only this bundle's clocks are the cleanup contract.
        var surface = (FrameworkElement)guide.Content;
        if (surface.Opacity != 1 || !surface.RenderTransform.Value.IsIdentity || guide.TransitionOverlay is not null)
            return false;
        if (!guide.MotionParts.Concat<FrameworkElement>(guide.ProgressFills).Append(guide.SuccessChip).All(AtRest))
            return false;
        // Logical children include the newly built caps, their badge and recap. They do not
        // include unrelated control-template clocks from the existing shared theme.
        if (guide.MotionParts.OfType<ContentControl>().SelectMany(FirstRunWelcomeTests.Descendants)
            .OfType<FrameworkElement>().Any(element => !AtRest(element))) return false;
        if (FirstRunWelcomeTests.Descendants(guide.SuccessChip).OfType<SymbolIcon>()
            .Any(icon => icon.HasAnimatedProperties || StrokeDrawMotion.IsDrawing(icon))) return false;
        ContentPresenter? label = VisualDescendants(guide.TryNowButton).OfType<ContentPresenter>().FirstOrDefault();
        return label is null || AtRest(label);
    }

    private static bool AtRest(FrameworkElement element) => element.Opacity == 1
        && element.RenderTransform.Value.IsIdentity && !element.HasAnimatedProperties
        && !TransformHasAnimation(element.RenderTransform);

    private static bool TransformHasAnimation(Transform transform) => transform.HasAnimatedProperties
        || transform is TransformGroup group && group.Children.Any(TransformHasAnimation);

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in VisualDescendants(child)) yield return descendant;
        }
    }

    private static void PumpUntil(Func<bool> complete, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!complete() && watch.Elapsed < timeout)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        Assert.True(complete(), "The offscreen render did not reach its sampling condition within the generous timeout.");
    }

    private static void Drain(Window window) => window.Dispatcher.Invoke(window.UpdateLayout, DispatcherPriority.ApplicationIdle);

    private static BitmapSource RenderWindow(Window window)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        bitmap.Freeze();
        return bitmap;
    }

    private static void SaveStrip(IReadOnlyList<BitmapSource> frames, IReadOnlyList<double> times, string path)
    {
        const int labelHeight = 34;
        int width = frames[0].PixelWidth, height = frames[0].PixelHeight;
        var visual = new DrawingVisual();
        using (DrawingContext draw = visual.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, width * frames.Count, height + labelHeight));
            for (int i = 0; i < frames.Count; i++)
            {
                draw.DrawImage(frames[i], new Rect(width * i, 0, width, height));
                var label = new FormattedText($"{times[i]:0.00} ms", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 16, Brushes.Black, 1);
                draw.DrawText(label, new Point(width * i + 12, height + 6));
            }
        }
        var bitmap = new RenderTargetBitmap(width * frames.Count, height + labelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Save(bitmap, path);
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
