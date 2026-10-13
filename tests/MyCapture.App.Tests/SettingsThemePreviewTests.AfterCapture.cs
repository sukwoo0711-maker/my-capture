using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Capture;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Ocr;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    private const string AfterCaptureEvidence = "MYCAPTURE_AFTER_CAPTURE_EVIDENCE";
    private const string AfterCapturePhase = "MYCAPTURE_AFTER_CAPTURE_PHASE";
    private const string AfterCapturePerformance = "MYCAPTURE_AFTER_CAPTURE_PERF";

    /// <summary>
    /// Renders the after-capture surfaces (quick access card, editor command bar, text
    /// recognition window, pin hover bar) and times how long each takes to open.
    /// </summary>
    /// <remarks>
    /// Runs only when <c>MYCAPTURE_AFTER_CAPTURE_EVIDENCE</c> (PNGs) or
    /// <c>MYCAPTURE_AFTER_CAPTURE_PERF</c> (open times) names a directory;
    /// <c>MYCAPTURE_AFTER_CAPTURE_PHASE</c> labels the files, for example before and after.
    /// </remarks>
    [Fact]
    public Task AfterCaptureSurfaces_RenderAndOpenTimesForReview()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AfterCaptureEvidence))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AfterCapturePerformance)))
            return Task.CompletedTask;
        return RunIsolated(nameof(AfterCaptureSurfaces_RenderAndOpenTimesForReview), RenderAfterCaptureSurfaces);
    }

    private static void RenderAfterCaptureSurfaces()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml"),
            });
        string phase = Environment.GetEnvironmentVariable(AfterCapturePhase) is { Length: > 0 } label ? label : "after";
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        Func<Window, bool> exclusion = PinHoverBar.ApplyCaptureExclusion;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        PinHoverBar.ApplyCaptureExclusion = _ => true;
        // The editor's text command is enabled as in the app, where recognition is wired.
        TextRecognitionRoute.Show = (_, _) => { };
        TextRecognitionRoute.IsAvailable = () => true;
        try
        {
            ThemeService.Apply(AppTheme.Daylight);
            if (Environment.GetEnvironmentVariable(AfterCapturePerformance) is { Length: > 0 } perf)
                MeasureAfterCaptureOpenTimes(DiagnosticOutputPaths.Create(perf), phase);
            if (Environment.GetEnvironmentVariable(AfterCaptureEvidence) is { Length: > 0 } evidence)
            {
                string directory = DiagnosticOutputPaths.Create(evidence);
                foreach (string language in new[] { "ko-KR", "en" })
                {
                    using var scope = UiText.UseLanguage(language);
                    string tag = language[..2];
                    RenderQuickAccessStates(directory, tag, phase);
                    RenderOcrWindow(directory, tag, phase);
                    foreach (double width in new[] { 860.0, 980.0, 1180.0 }) RenderEditorBar(directory, tag, width, phase);
                }
                using var korean = UiText.UseLanguage("ko-KR");
                foreach (var (name, width, height) in new[] { ("narrow", 220.0, 140.0), ("wide", 640.0, 300.0) })
                    RenderPinWithHoverBar(directory, name, width, height, phase);
            }
        }
        finally
        {
            TextRecognitionRoute.Show = null;
            TextRecognitionRoute.IsAvailable = null;
            PinHoverBar.ApplyCaptureExclusion = exclusion;
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
            application.Shutdown();
        }
    }

    private static QuickAccessWindow AfterCaptureCard(BitmapSource image) =>
        new(image, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
            () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png")) { IsKept = true };

    private static void RenderQuickAccessStates(string directory, string language, string phase)
    {
        QuickAccessWindow card = AfterCaptureCard(FeedbackSample());
        try
        {
            card.Show();
            PumpFor(TimeSpan.FromMilliseconds(120));
            SaveAfterCapturePng(directory, $"quick-access-normal-{language}-{phase}.png", RenderOnDesktop(card, 1.5));
            card.RunAsync("Copy", () => Task.FromResult(false), "QuickAccess.Copying", "QuickAccess.Copied").GetAwaiter().GetResult();
            PumpFor(TimeSpan.FromMilliseconds(120));
            SaveAfterCapturePng(directory, $"quick-access-failed-{language}-{phase}.png", RenderOnDesktop(card, 1.5));
        }
        finally { card.Close(); }

        QuickAccessWindow textCard = new(FeedbackSample(), () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
            () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"),
            copyText: () => Task.FromResult(new TextCaptureOutcome(TextCaptureStatus.Failed, null, 0))) { IsKept = true };
        try
        {
            textCard.Show();
            PumpFor(TimeSpan.FromMilliseconds(120));
            textCard.ShortcutActions["CopyText"]();
            PumpFor(TimeSpan.FromMilliseconds(120));
            SaveAfterCapturePng(directory, $"quick-access-textfailed-{language}-{phase}.png", RenderOnDesktop(textCard, 1.5));
            textCard.IsKept = false;
            PumpFor(TimeSpan.FromMilliseconds(60));
            SaveAfterCapturePng(directory, $"quick-access-unkept-{language}-{phase}.png", RenderOnDesktop(textCard, 1.5));
        }
        finally { textCard.Close(); }
    }

    private static (BitmapSource Image, OcrResult Result) AfterCaptureOcrSample()
    {
        string text = "public void Capture()\n{\n    Save();\n}\n검토용 캡처 · 여러 줄 코드";
        return (EditorEvidenceBackground(560, 300), OcrResult.Success(text, "ko", lines: [], elapsed: TimeSpan.FromMilliseconds(80)));
    }

    private static OcrResultWindow OffscreenOcrWindow()
    {
        var window = new OcrResultWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = 1024, Height = 680, ShowActivated = false,
        };
        FluidMotion.SetWindowEntrance(window, false);
        return window;
    }

    private static void RenderOcrWindow(string directory, string language, string phase)
    {
        (BitmapSource image, OcrResult result) = AfterCaptureOcrSample();
        OcrResultWindow window = OffscreenOcrWindow();
        try
        {
            window.ShowResult(result, "Capture", image);
            Drain(window);
            SaveAfterCapturePng(directory, $"ocr-window-{language}-{phase}.png", RenderWindow(window));
        }
        finally { window.CloseForExit(); }
    }

    private static AnnotationEditorWindow OffscreenEditor(double width)
    {
        BitmapSource bitmap = EditorEvidenceBackground(720, 300);
        var region = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        var window = new AnnotationEditorWindow(new FrozenFrame(bitmap, region, null, 0), region, bitmap)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000, Top = -10000, Width = width, Height = 560,
            ShowActivated = false, ShowInTaskbar = false,
        };
        FluidMotion.SetWindowEntrance(window, false);
        return window;
    }

    private static void RenderEditorBar(string directory, string language, double width, string phase)
    {
        AnnotationEditorWindow window = OffscreenEditor(width);
        try
        {
            window.Show();
            Drain(window);
            SaveAfterCapturePng(directory, $"editor-{width:0}-{language}-{phase}.png", RenderWindow(window));
        }
        finally { window.Close(); }
    }

    private static void RenderPinWithHoverBar(string directory, string name, double width, double height, string phase)
    {
        BitmapSource image = EditorEvidenceBackground((int)width, (int)height);
        var pin = new PinWindow(PinContent.FromImage(image), new PinViewState(width, height, 1, 1, 0.1), -10000, -10000,
            () => new PinSettings())
        {
            ShowActivated = false,
            ReadWorkArea = _ => new RectD(-12000, -12000, 4000, 4000),
        };
        try
        {
            pin.Show();
            Drain(pin);
            pin.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            PinHoverBar bar = pin.HoverBarForTest!;
            Drain(bar);
            Drain(pin);
            // The bar is its own window; compose both at their desktop positions.
            Rect pinBounds = new(pin.Left, pin.Top, pin.ActualWidth, pin.ActualHeight);
            Rect barBounds = new(bar.Left, bar.Top, bar.ActualWidth, bar.ActualHeight);
            Rect all = Rect.Union(pinBounds, barBounds);
            all.Inflate(16, 16);
            var visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5B, 0x6B, 0x7E)), null, new Rect(all.Size));
                drawing.DrawRectangle(new VisualBrush(pin) { Stretch = Stretch.None },
                    null, new Rect(pinBounds.Left - all.Left, pinBounds.Top - all.Top, pinBounds.Width, pinBounds.Height));
                drawing.DrawRectangle(new VisualBrush(bar) { Stretch = Stretch.None },
                    null, new Rect(barBounds.Left - all.Left, barBounds.Top - all.Top, barBounds.Width, barBounds.Height));
            }
            const double scale = 1.5;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(all.Width * scale), (int)Math.Ceiling(all.Height * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            SaveAfterCapturePng(directory, $"pin-{name}-{phase}.png", bitmap);
        }
        finally { pin.Close(); }
    }

    private static BitmapSource RenderOnDesktop(Window window, double scale)
    {
        window.UpdateLayout();
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2F, 0x55, 0x7A), Color.FromRgb(0x7A, 0x9C, 0xB8), 35),
                null, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            drawing.DrawRectangle(new VisualBrush(window) { Stretch = Stretch.None }, null,
                new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * scale),
            (int)Math.Ceiling(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static void SaveAfterCapturePng(string directory, string name, BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream output = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(output);
    }

    /// <summary>
    /// Open time = construction through the window's first rendered frame, in milliseconds:
    /// one warm-up open, then the median of seven.
    /// </summary>
    private static void MeasureAfterCaptureOpenTimes(string directory, string phase)
    {
        using var language = UiText.UseLanguage("ko-KR");
        BitmapSource cardImage = FeedbackSample();
        (BitmapSource ocrImage, OcrResult ocrResult) = AfterCaptureOcrSample();
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"phase={phase}");

        void Record(string surface, Func<Window> create, Action<Window> show, Action<Window> close)
        {
            var samples = new List<double>();
            for (int run = 0; run < 8; run++)
            {
                long start = Stopwatch.GetTimestamp();
                Window window = create();
                var frame = new DispatcherFrame();
                window.ContentRendered += (_, _) => frame.Continue = false;
                var guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                guard.Tick += (_, _) => { guard.Stop(); frame.Continue = false; };
                guard.Start();
                show(window);
                Dispatcher.PushFrame(frame);
                guard.Stop();
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                close(window);
                PumpFor(TimeSpan.FromMilliseconds(60));
                if (run > 0) samples.Add(elapsed);
            }
            samples.Sort();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{surface}: median={samples[samples.Count / 2]:0.0} ms; samples={string.Join(", ", samples.Select(value => value.ToString("0.0", CultureInfo.InvariantCulture)))}");
        }

        Record("quick-access", () => AfterCaptureCard(cardImage), window => window.Show(), window => window.Close());
        Record("editor-980", () => OffscreenEditor(980), window => window.Show(), window => window.Close());
        Record("ocr-window", OffscreenOcrWindow,
            window => ((OcrResultWindow)window).ShowResult(ocrResult, "Capture", ocrImage),
            window => ((OcrResultWindow)window).CloseForExit());

        // The app reuses one recognition window: time a re-show of the hidden window too.
        OcrResultWindow reused = OffscreenOcrWindow();
        reused.ShowResult(ocrResult, "Capture", ocrImage);
        Drain(reused);
        var reshow = new List<double>();
        for (int run = 0; run < 8; run++)
        {
            reused.Hide();
            PumpFor(TimeSpan.FromMilliseconds(40));
            long start = Stopwatch.GetTimestamp();
            reused.ShowResult(ocrResult, "Capture", ocrImage);
            reused.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            if (run > 0) reshow.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
        reused.CloseForExit();
        reshow.Sort();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"ocr-window-reshow: median={reshow[reshow.Count / 2]:0.0} ms; samples={string.Join(", ", reshow.Select(value => value.ToString("0.0", CultureInfo.InvariantCulture)))}");
        File.WriteAllText(DiagnosticOutputPaths.Child(directory, $"open-times-{phase}.txt"), report.ToString());
    }
}
