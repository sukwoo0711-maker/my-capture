using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Pinning;
using MyCapture.App.Recording;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Annotations;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Ocr;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// Renders the Microsoft Store listing screenshots from the real windows, filled with
/// synthetic but realistic captures. Like the UX review it never reads the user's library,
/// settings, screen or clipboard; it only writes PNGs into the output directory.
/// </summary>
/// <remarks>
/// Usage: <c>MyCapture.exe --selftest-store-shots [outputDirectory] [ko|en]</c>. Each scene is
/// rendered at 2x and composed onto a 2560x1440 canvas, above the Store's 1366x768 minimum.
/// </remarks>
internal static class StoreScreenshotSelfTest
{
    internal const string CommandLineSwitch = "--selftest-store-shots";

    private const double CanvasWidth = 2560;
    private const double CanvasHeight = 1440;
    private const double RenderScale = 2;

    private static readonly Typeface Ui = new(new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface UiBold = new(new FontFamily("Segoe UI Variable Display, Segoe UI, Malgun Gothic"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface Mono = new(new FontFamily("Cascadia Code, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    internal static int Run(string outputDirectory, string? language)
    {
        string lang = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ko";
        // The CLI path is resolved once to a local directory; every artifact is a checked child of it.
        string root = DiagnosticOutputPaths.Create(outputDirectory);
        var report = new StringBuilder("MyCapture Store screenshots\n");
        report.AppendLine($"Language: {lang}; canvas {CanvasWidth}x{CanvasHeight}; window scale {RenderScale}x");
        bool? oldMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        int failures = 0;
        try
        {
            using (UiText.UseLanguage(lang == "en" ? "en-US" : "ko-KR"))
            {
                failures = RenderScenes(root, lang == "en", report);
            }
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = oldMotion;
        }

        report.AppendLine(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures})");
        File.WriteAllText(DiagnosticOutputPaths.Child(root, "store-screenshots-report.txt"), report.ToString());
        return failures == 0 ? 0 : 1;
    }

    private static int RenderScenes(string output, bool english, StringBuilder report)
    {
        AppPaths paths = AppPaths.CreateForRoot(DiagnosticOutputPaths.Child(output, "fixtures-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(paths.DataRoot);
        Directory.CreateDirectory(paths.CapturesRoot);
        using ILoggerFactory logs = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        var settings = new AppSettings();
        Samples samples = Samples.Create(english);
        int failures = 0;

        void Scene(string name, Func<BitmapSource> render)
        {
            try
            {
                BitmapSource image = render();
                string path = DiagnosticOutputPaths.Child(output, name + ".png");
                Save(image, path);
                report.AppendLine($"PASS {name}: {image.PixelWidth}x{image.PixelHeight}");
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine($"FAIL {name}: {ex}");
            }
        }

        Scene("01-library", () => LibraryScene(AppTheme.Midnight, paths, logs, settings, samples, english, light: false));
        Scene("02-annotation", () => AnnotationScene(samples, english));
        Scene("03-quick-redact", () => RedactionScene(samples, english));
        Scene("04-after-capture", () => QuickAccessScene(samples));
        Scene("05-pins", () => PinScene(settings, samples, english));
        Scene("06-ocr", () => OcrScene(samples, english));
        RecordingResult recording = UxReviewSelfTest.CreateFixtureVideo(DiagnosticOutputPaths.Child(paths.DataRoot, "store-demo.mp4"), samples.Dashboard720);
        BitmapSource? editor = null;
        Scene("07-video-editor", () => editor = VideoEditorScene(recording, paths, logs, english));
        Scene("08-gif-export", () => GifExportScene(recording, logs, editor, english));
        Scene("09-settings", () => SettingsScene(settings));
        Scene("10-library-light", () => LibraryScene(AppTheme.Daylight, paths, logs, settings, samples, english, light: true));
        return failures;
    }

    // ----- Scenes -----

    private static BitmapSource LibraryScene(AppTheme theme, AppPaths paths, ILoggerFactory logs, AppSettings settings,
        Samples samples, bool english, bool light)
    {
        ThemeService.Apply(theme);
        AppPaths libraryPaths = AppPaths.CreateForRoot(DiagnosticOutputPaths.Child(paths.DataRoot, "library-" + theme));
        Directory.CreateDirectory(libraryPaths.CapturesRoot);
        var queue = new CaptureQueue(libraryPaths, settings.Queue, logs.CreateLogger<CaptureQueue>());
        DateTimeOffset now = new(2026, 10, 8, 16, 40, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 8, 16, 40, 0)));
        (BitmapSource Image, string Title, string Source, string Ocr)[] items = samples.LibraryItems;
        for (int index = 0; index < 24; index++)
        {
            var item = items[index % items.Length];
            byte[] png = ImageCodec.EncodePng(item.Image);
            var record = new CaptureRecord
            {
                Width = item.Image.PixelWidth,
                Height = item.Image.PixelHeight,
                Title = item.Title,
                SourceWindowTitle = item.Source,
                CreatedAt = now.AddMinutes(-(index * 53 + index * index * 7)),
                IsPinned = index == 2,
                OcrText = item.Ocr,
                OcrContentRevision = 0,
                TotalBytes = png.LongLength * 3,
            };
            queue.Add(record);
            string directory = queue.GetDirectory(record);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Thumbnail), png);
            File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Rendered), png);
            File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Original), png);
        }

        var controller = new GalleryController(queue, logs.CreateLogger<GalleryController>());
        var persistence = new CapturePersistenceService(queue, libraryPaths, () => settings.Queue, logs.CreateLogger<CapturePersistenceService>());
        var commit = new CaptureCommitService(persistence, () => settings, () => libraryPaths, logs.CreateLogger<CaptureCommitService>(), (_, _) => Task.FromResult(true));
        var videoLibrary = new VideoLibraryService(queue, libraryPaths, logs.CreateLogger<VideoLibraryService>());
        var ocr = new UxReviewSelfTest.FixtureOcr();
        using var presenter = new OcrResultPresenter(ocr, System.Windows.Threading.Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
        var viewModel = new GalleryViewModel(controller,
            record => Path.Combine(queue.GetDirectory(record), CaptureFileNames.Thumbnail), 480, clock: () => now);
        var indexing = new OcrIndexingService(controller, ocr, queue.GetDirectory, () => settings.Ocr,
            logs.CreateLogger<OcrIndexingService>(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        var window = new GalleryWindow(viewModel, controller,
            new GalleryReeditLoader(queue, logs.CreateLogger<GalleryReeditLoader>()), commit, queue,
            videoLibrary, libraryPaths, logs, presenter, () => settings.Ocr, indexing,
            new UxReviewSelfTest.FixturePrivacy(), NullLogger.Instance)
        {
            Width = 1240,
            Height = 760,
        };
        viewModel.Select(viewModel.Groups.SelectMany(group => group.Items).First().Id);
        BitmapSource shot = Snapshot(window, settleMs: 700);
        return Compose(light, new Layer(shot, Center: true, Width: 2000));
    }

    private static BitmapSource AnnotationScene(Samples samples, bool english)
    {
        ThemeService.Apply(AppTheme.Midnight);
        BitmapSource image = samples.Dashboard;
        var document = AnnotationDocument.CreateFor(image.PixelWidth, image.PixelHeight);
        var iris = ColorRgba.FromRgb(124, 108, 255);
        var coral = ColorRgba.FromRgb(255, 92, 102);
        document.Add(Rectangle(new RectD(248, 128, 592, 164), coral, 5));
        document.Add(Step(new RectD(214, 96, 56, 56), "1", iris));
        document.Add(Label(new RectD(1104, 318, 214, 56), english ? " Q3 peak" : " 3분기 최고", coral));
        document.Add(Arrow(new PointD(1150, 378), new PointD(1052, 410), coral, 6));
        document.Add(Highlight(new RectD(276, 756, 560, 44)));
        document.Add(Step(new RectD(214, 750, 56, 56), "2", iris));
        var region = new RectD(0, 0, image.PixelWidth, image.PixelHeight);
        var window = new AnnotationEditorWindow(new FrozenFrame(image, region, null, 0), region, image, initialDocument: document)
        {
            Width = 1240,
            Height = 780,
        };
        return Compose(false, new Layer(Snapshot(window, settleMs: 500), Center: true, Width: 2000));
    }

    private static BitmapSource RedactionScene(Samples samples, bool english)
    {
        ThemeService.Apply(AppTheme.Glass);
        BitmapSource image = samples.Inquiry;
        var document = AnnotationDocument.CreateFor(image.PixelWidth, image.PixelHeight);
        foreach (RectD cover in samples.InquirySensitiveRegions)
        {
            document.Add(Cover(cover));
        }

        var region = new RectD(0, 0, image.PixelWidth, image.PixelHeight);
        var window = new AnnotationEditorWindow(new FrozenFrame(image, region, null, 0), region, image, initialDocument: document)
        {
            Width = 1240,
            Height = 780,
        };
        return Compose(false, new Layer(Snapshot(window, settleMs: 500), Center: true, Width: 2000));
    }

    private static BitmapSource QuickAccessScene(Samples samples)
    {
        ThemeService.Apply(AppTheme.Glass);
        var card = new QuickAccessWindow(samples.Chart, () => Task.FromResult(true), () => { },
            () => Task.CompletedTask, () => Task.FromResult(true), () => { }, () => Task.FromResult("chart.png"))
        {
            IsKept = true,
        };
        BitmapSource shot = TrimBottom(Snapshot(card, settleMs: 400));
        double cardWidth = shot.PixelWidth / RenderScale * 2.8;
        double cardHeight = cardWidth * shot.PixelHeight / shot.PixelWidth;
        return Compose(false,
            new Layer(samples.Article, X: 150, Y: 110, Width: 1760),
            new Layer(shot, X: CanvasWidth - cardWidth - 150, Y: CanvasHeight - cardHeight - 130, Width: cardWidth));
    }

    private static BitmapSource PinScene(AppSettings settings, Samples samples, bool english)
    {
        ThemeService.Apply(AppTheme.Midnight);
        string code = english
            ? "public async Task SaveAsync(Capture capture)\r\n{\r\n    await _library.AddAsync(capture);\r\n    _clipboard.Copy(capture.Image);\r\n}"
            : "public async Task SaveAsync(Capture capture)\r\n{\r\n    // 라이브러리에 먼저 저장\r\n    await _library.AddAsync(capture);\r\n    _clipboard.Copy(capture.Image);\r\n}";
        BitmapSource codeImage = ClipboardTextRenderer.Render(code, false);
        string table = english
            ? "Item\tQ3\tQ4\r\nServer\t1,240\t1,310\r\nDesign\t860\t920\r\nMarketing\t1,980\t2,150"
            : "항목\t3분기\t4분기\r\n서버\t1,240\t1,310\r\n디자인\t860\t920\r\n마케팅\t1,980\t2,150";
        BitmapSource tableImage = ClipboardTextRenderer.Render(table, true);
        var codePin = new PinWindow(PinContent.FromText(codeImage, code, false),
            new PinViewState(codeImage.PixelWidth, codeImage.PixelHeight, 1, 1, 0.1), 40, 40, () => settings.Pin) { ShowInTaskbar = false };
        var tablePin = new PinWindow(PinContent.FromText(tableImage, table, true),
            new PinViewState(tableImage.PixelWidth, tableImage.PixelHeight, 1, 1, 0.1), 40, 40, () => settings.Pin) { ShowInTaskbar = false };
        var chartPin = new PinWindow(PinContent.FromImage(samples.Chart),
            new PinViewState(samples.Chart.PixelWidth, samples.Chart.PixelHeight, 1, 1, 0.1), 40, 40, () => settings.Pin) { ShowInTaskbar = false };
        BitmapSource codeShot = Snapshot(codePin, settleMs: 250);
        BitmapSource tableShot = Snapshot(tablePin, settleMs: 250);
        BitmapSource chartShot = Snapshot(chartPin, settleMs: 250);
        return Compose(false,
            new Layer(samples.Spreadsheet, X: 140, Y: 110, Width: 1760),
            new Layer(chartShot, X: 1560, Y: 170, Width: 860),
            new Layer(codeShot, X: 330, Y: 780, Width: Math.Min(1120, codeShot.PixelWidth / RenderScale * 2.0)),
            new Layer(tableShot, X: 1640, Y: 820, Width: Math.Min(720, tableShot.PixelWidth / RenderScale * 2.0)));
    }

    private static BitmapSource OcrScene(Samples samples, bool english)
    {
        ThemeService.Apply(AppTheme.Midnight);
        var window = new OcrResultWindow { Width = 640, Height = 600 };
        OcrLine[] lines = samples.ArticleText.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select((line, index) => new OcrLine(line, new OcrRect(0, index * 40, 900, 32), []))
            .ToArray();
        window.ShowResult(OcrResult.Success(samples.ArticleText, english ? "en" : "ko", lines, TimeSpan.FromMilliseconds(380)),
            samples.ArticleSource);
        foreach (TextBox box in UxReviewSelfTest.Descendants(window).OfType<TextBox>())
        {
            box.Select(0, 0);
        }

        BitmapSource shot = Snapshot(window, settleMs: 300);
        return Compose(false,
            new Layer(samples.Article, X: 120, Y: 150, Width: 1500),
            new Layer(shot, X: 1500, Y: 250, Width: 940));
    }

    private static BitmapSource VideoEditorScene(RecordingResult recording, AppPaths paths, ILoggerFactory logs, bool english)
    {
        ThemeService.Apply(AppTheme.Midnight);
        var document = VideoEditDocument.CreateFor(recording.Width, recording.Height, recording.DurationMs);
        document.TextOverlays.Add(new TimedTextOverlay { Text = english ? "Monthly sales trend" : "월별 매출 추이", StartMs = 200, EndMs = 1700 });
        document.TextOverlays.Add(new TimedTextOverlay { Text = english ? "Q3 peak" : "3분기 최고점", StartMs = 1800, EndMs = 2900 });
        var window = new VideoEditorWindow(recording, paths, logs, document) { Width = 1240, Height = 800 };
        window.Show();
        UxReviewSelfTest.Pump(TimeSpan.FromMilliseconds(1800));
        UxReviewSelfTest.Descendants(window).OfType<ListBox>().First().SelectedIndex = 0;
        BitmapSource shot = Snapshot(window, settleMs: 400);
        return Compose(false, new Layer(shot, Center: true, Width: 2060));
    }

    private static BitmapSource GifExportScene(RecordingResult recording, ILoggerFactory logs, BitmapSource? editor, bool english)
    {
        ThemeService.Apply(AppTheme.Midnight);
        var document = VideoEditDocument.CreateFor(recording.Width, recording.Height, recording.DurationMs);
        var dialog = new VideoExportDialog(recording, document, logs, gif: true) { Height = 1060 };
        // Let the size estimate finish so the dialog shows its result instead of progress,
        // then show the form from the top.
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = 30;
        dialog.Top = 30;
        dialog.ShowActivated = false;
        dialog.Show();
        UxReviewSelfTest.Pump(TimeSpan.FromMilliseconds(6000));
        foreach (ScrollViewer scroll in UxReviewSelfTest.Descendants(dialog).OfType<ScrollViewer>())
        {
            scroll.ScrollToTop();
        }

        BitmapSource shot = Snapshot(dialog, settleMs: 300);
        var layers = new List<Layer>();
        if (editor is not null)
        {
            layers.Add(new Layer(editor, X: 0, Y: 0, Width: CanvasWidth, Shadow: false, Radius: 0));
            layers.Add(new Layer(Dim(editor.PixelWidth, editor.PixelHeight, 0.45), X: 0, Y: 0, Width: CanvasWidth, Shadow: false, Radius: 0));
        }

        layers.Add(new Layer(shot, Center: true, Width: FitWidth(shot, 1500, 1320)));
        return Compose(false, [.. layers]);
    }

    private static BitmapSource SettingsScene(AppSettings settings)
    {
        ThemeService.Apply(AppTheme.Glass);
        var window = new SettingsWindow(() => settings, _ => new SettingsApplyResult(true, true, true, false, []),
            NullLogger.Instance, settingsStore: null)
        {
            Width = 1100,
            Height = 760,
        };
        BitmapSource shot = Snapshot(window, settleMs: 400, close: w => ((SettingsWindow)w).CloseForExit());
        return Compose(false, new Layer(shot, Center: true, Width: 1800));
    }

    // ----- Annotation helpers -----

    private static RectangleAnnotation Rectangle(RectD rect, ColorRgba stroke, double thickness) => new()
    {
        Rect = rect,
        Stroke = stroke,
        StrokeThickness = thickness,
        Fill = new ColorRgba(0, stroke.R, stroke.G, stroke.B),
    };

    private static RectangleAnnotation Highlight(RectD rect) => new()
    {
        Rect = rect,
        Stroke = new ColorRgba(0, 255, 214, 10),
        StrokeThickness = 1,
        Fill = new ColorRgba(90, 255, 214, 10),
    };

    private static RectangleAnnotation Cover(RectD rect) => new()
    {
        Rect = rect,
        Stroke = ColorRgba.FromRgb(20, 22, 30),
        StrokeThickness = 2,
        Fill = ColorRgba.FromRgb(20, 22, 30),
    };

    private static PolylineAnnotation Arrow(PointD from, PointD to, ColorRgba stroke, double thickness) => new()
    {
        Points = [from, to],
        Stroke = stroke,
        StrokeThickness = thickness,
        HeadAtEnd = true,
    };

    private static TextAnnotation Label(RectD rect, string text, ColorRgba background) => new()
    {
        Rect = rect,
        Text = text,
        Foreground = ColorRgba.FromRgb(255, 255, 255),
        Background = background,
        FontSize = 34,
    };

    private static TextAnnotation Step(RectD rect, string text, ColorRgba background) => new()
    {
        Rect = rect,
        Text = text,
        Foreground = ColorRgba.FromRgb(255, 255, 255),
        Background = background,
        FontSize = 30,
        IsStepNumber = true,
    };

    // ----- Rendering and composition -----

    private sealed record Layer(BitmapSource Image, double X = 0, double Y = 0, double Width = 0, bool Center = false,
        bool Shadow = true, double Radius = 14);

    private static BitmapSource Snapshot(Window window, int settleMs, Action<Window>? close = null)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 30;
        window.Top = 30;
        window.ShowActivated = false;
        if (!window.IsVisible)
        {
            window.Show();
        }

        UxReviewSelfTest.Pump(TimeSpan.FromMilliseconds(settleMs));
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(window.ActualWidth * RenderScale)),
            Math.Max(1, (int)Math.Ceiling(window.ActualHeight * RenderScale)),
            96 * RenderScale, 96 * RenderScale, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (DrawingContext drawing = backdrop.RenderOpen())
        {
            drawing.DrawRectangle(window.TryFindResource("Surface.Base") as Brush ?? Brushes.Black, null,
                new Rect(0, 0, window.ActualWidth, window.ActualHeight));
        }

        bitmap.Render(backdrop);
        bitmap.Render(window);
        bitmap.Freeze();
        try
        {
            if (close is not null) close(window);
            else window.Close();
        }
        catch (InvalidOperationException)
        {
        }

        return bitmap;
    }

    private static double FitWidth(BitmapSource image, double maxWidth, double maxHeight) =>
        Math.Min(maxWidth, maxHeight * image.PixelWidth / image.PixelHeight);

    private static BitmapSource Compose(bool light, params Layer[] layers)
    {
        var root = new Canvas { Width = CanvasWidth, Height = CanvasHeight, Background = Background(light) };
        foreach (Layer layer in layers)
        {
            double width = layer.Width > 0 ? layer.Width : layer.Image.PixelWidth;
            double height = width * layer.Image.PixelHeight / layer.Image.PixelWidth;
            var border = new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(layer.Radius),
                Background = new ImageBrush(layer.Image) { Stretch = Stretch.Fill },
            };
            RenderOptions.SetBitmapScalingMode(border, BitmapScalingMode.HighQuality);
            if (layer.Shadow)
            {
                border.Effect = new DropShadowEffect
                {
                    BlurRadius = 70,
                    ShadowDepth = 18,
                    Direction = 270,
                    Opacity = light ? 0.22 : 0.55,
                    Color = Colors.Black,
                };
            }

            Canvas.SetLeft(border, layer.Center ? (CanvasWidth - width) / 2 : layer.X);
            Canvas.SetTop(border, layer.Center ? (CanvasHeight - height) / 2 : layer.Y);
            root.Children.Add(border);
        }

        root.Measure(new Size(CanvasWidth, CanvasHeight));
        root.Arrange(new Rect(0, 0, CanvasWidth, CanvasHeight));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)CanvasWidth, (int)CanvasHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    private static Brush Background(bool light)
    {
        var group = new DrawingGroup();
        using (DrawingContext drawing = group.Open())
        {
            var rect = new Rect(0, 0, CanvasWidth, CanvasHeight);
            drawing.DrawRectangle(new LinearGradientBrush(
                light ? Color.FromRgb(244, 246, 251) : Color.FromRgb(11, 15, 23),
                light ? Color.FromRgb(226, 230, 244) : Color.FromRgb(24, 22, 48), 35), null, rect);
            drawing.DrawRectangle(new RadialGradientBrush(
                Color.FromArgb(light ? (byte)70 : (byte)110, 124, 108, 255), Color.FromArgb(0, 124, 108, 255))
            {
                Center = new Point(0.82, 0.12),
                GradientOrigin = new Point(0.82, 0.12),
                RadiusX = 0.6,
                RadiusY = 0.8,
            }, null, rect);
        }

        return new DrawingBrush(group) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
    }

    private static BitmapSource Dim(int width, int height, double opacity)
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), 8, 10, 18)), null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Drops trailing rows that only repeat the bottom background colour.</summary>
    private static BitmapSource TrimBottom(BitmapSource image)
    {
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);
        int probe = (height - 1) * stride + Math.Min(24, width - 1) * 4;
        int bottom = height - 1;
        while (bottom > height / 2)
        {
            bool uniform = true;
            for (int x = 24; x < width - 24 && uniform; x++)
            {
                int offset = bottom * stride + x * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    if (Math.Abs(pixels[offset + channel] - pixels[probe + channel]) > 6)
                    {
                        uniform = false;
                        break;
                    }
                }
            }

            if (!uniform) break;
            bottom--;
        }

        int keep = Math.Min(height, bottom + 1 + (int)(14 * RenderScale));
        var cropped = new CroppedBitmap(image, new Int32Rect(0, 0, width, keep));
        cropped.Freeze();
        return cropped;
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }

    // ----- Synthetic captures -----

    private sealed class Samples
    {
        public required BitmapSource Dashboard { get; init; }
        public required BitmapSource Dashboard720 { get; init; }
        public required BitmapSource Code { get; init; }
        public required BitmapSource Article { get; init; }
        public required BitmapSource Inquiry { get; init; }
        public required BitmapSource Spreadsheet { get; init; }
        public required BitmapSource Chart { get; init; }
        public required RectD[] InquirySensitiveRegions { get; init; }
        public required string ArticleText { get; init; }
        public required string ArticleSource { get; init; }
        public required (BitmapSource Image, string Title, string Source, string Ocr)[] LibraryItems { get; init; }

        public static Samples Create(bool english)
        {
            BitmapSource dashboard = DrawDashboard(english, 1440, 900);
            BitmapSource code = DrawCode(english);
            (BitmapSource article, string articleText) = DrawArticle(english);
            (BitmapSource inquiry, RectD[] sensitive) = DrawInquiry(english);
            BitmapSource sheet = DrawSpreadsheet(english);
            BitmapSource chart = DrawChartCard(english);
            string source = english ? "5 ways to capture your screen faster - Microsoft Edge" : "화면을 빠르게 캡처하는 5가지 방법 - Microsoft Edge";
            return new Samples
            {
                Dashboard = dashboard,
                Dashboard720 = DrawDashboard(english, 1280, 720),
                Code = code,
                Article = article,
                Inquiry = inquiry,
                Spreadsheet = sheet,
                Chart = chart,
                InquirySensitiveRegions = sensitive,
                ArticleText = articleText,
                ArticleSource = source,
                LibraryItems =
                [
                    (dashboard, english ? "Monthly sales dashboard" : "월간 매출 대시보드", "Northwind Analytics - Microsoft Edge", english ? "Monthly sales 124.8M" : "월 매출 1억 2,480만"),
                    (code, english ? "CaptureService.cs review" : "CaptureService.cs 리뷰", "CaptureService.cs - Visual Studio Code", "public async Task SaveAsync"),
                    (article, english ? "Screen capture tips" : "화면 캡처 방법 정리", source, articleText),
                    (sheet, english ? "2026 budget" : "2026 예산안", english ? "2026 budget.xlsx - Excel" : "2026 예산안.xlsx - Excel", english ? "Item Q1 Q2 Q3 Q4" : "항목 1분기 2분기 3분기 4분기"),
                    (chart, english ? "Weekly active users" : "주간 활성 사용자", "Northwind Analytics - Microsoft Edge", english ? "Weekly active users" : "주간 활성 사용자"),
                    (inquiry, english ? "Customer inquiry #2481" : "고객 문의 #2481", english ? "Support console - Microsoft Edge" : "상담 관리 - Microsoft Edge", english ? "Customer inquiry" : "고객 문의 상세"),
                ],
            };
        }
    }

    private static FormattedText Text(string text, Typeface face, double size, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, 1.0);

    private static BitmapSource Rasterize(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            draw(drawing);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static SolidColorBrush Rgb(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private static void BrowserChrome(DrawingContext d, int width, string url)
    {
        d.DrawRectangle(Rgb(232, 235, 241), null, new Rect(0, 0, width, 56));
        d.DrawRoundedRectangle(Brushes.White, null, new Rect(150, 12, width - 300, 32), 16, 16);
        d.DrawText(Text(url, Ui, 15, Rgb(70, 76, 90)), new Point(172, 18));
        foreach (int x in new[] { 22, 46, 70 })
            d.DrawEllipse(Rgb(196, 200, 210), null, new Point(x, 28), 7, 7);
    }

    private static BitmapSource DrawDashboard(bool english, int width, int height)
    {
        double s = width / 1440.0;
        return Rasterize(width, height, d =>
        {
            d.PushTransform(new ScaleTransform(s, s));
            d.DrawRectangle(Rgb(246, 247, 251), null, new Rect(0, 0, 1440, 900 / s * s));
            BrowserChrome(d, 1440, "analytics.northwind.example/sales");
            d.DrawRectangle(Rgb(26, 30, 46), null, new Rect(0, 56, 220, 900));
            d.DrawText(Text("Northwind", UiBold, 22, Brushes.White), new Point(28, 82));
            string[] nav = english ? ["Overview", "Sales", "Customers", "Products", "Reports"] : ["개요", "매출", "고객", "상품", "보고서"];
            for (int i = 0; i < nav.Length; i++)
            {
                if (i == 1) d.DrawRoundedRectangle(Rgb(124, 108, 255), null, new Rect(16, 136 + i * 46, 188, 38), 8, 8);
                d.DrawText(Text(nav[i], Ui, 16, i == 1 ? Brushes.White : Rgb(170, 176, 196)), new Point(34, 145 + i * 46));
            }

            d.DrawText(Text(english ? "Sales overview · Q3 2026" : "매출 개요 · 2026년 3분기", UiBold, 28, Rgb(24, 28, 40)), new Point(260, 82));
            (string Label, string Value, string Delta)[] kpis = english
                ? [("Monthly sales", "$93.6K", "+12.4%"), ("Orders", "3,482", "+8.1%"), ("Conversion", "4.8%", "+0.6p"), ("Returning", "61%", "+3.2p")]
                : [("월 매출", "1억 2,480만", "+12.4%"), ("주문 수", "3,482건", "+8.1%"), ("전환율", "4.8%", "+0.6p"), ("재방문 고객", "61%", "+3.2p")];
            for (int i = 0; i < kpis.Length; i++)
            {
                var card = new Rect(260 + i * 290, 140, 268, 140);
                d.DrawRoundedRectangle(Brushes.White, new Pen(Rgb(228, 231, 240), 1), card, 12, 12);
                d.DrawText(Text(kpis[i].Label, Ui, 16, Rgb(110, 116, 132)), new Point(card.X + 22, card.Y + 22));
                d.DrawText(Text(kpis[i].Value, UiBold, 30, Rgb(24, 28, 40)), new Point(card.X + 22, card.Y + 54));
                d.DrawText(Text(kpis[i].Delta, UiBold, 15, Rgb(22, 160, 110)), new Point(card.X + 22, card.Y + 102));
            }

            var chart = new Rect(260, 310, 1130, 360);
            d.DrawRoundedRectangle(Brushes.White, new Pen(Rgb(228, 231, 240), 1), chart, 12, 12);
            d.DrawText(Text(english ? "Monthly sales" : "월별 매출", UiBold, 18, Rgb(24, 28, 40)), new Point(chart.X + 24, chart.Y + 20));
            double[] values = [52, 58, 55, 63, 70, 68, 76, 84, 92, 81, 74, 79];
            for (int i = 0; i < values.Length; i++)
            {
                double h = values[i] * 2.6;
                var bar = new Rect(chart.X + 50 + i * 88, chart.Bottom - 40 - h, 46, h);
                d.DrawRoundedRectangle(i == 8 ? Rgb(124, 108, 255) : Rgb(196, 190, 255), null, bar, 6, 6);
                d.DrawText(Text((i + 1) + (english ? "" : "월"), Ui, 13, Rgb(120, 126, 140)), new Point(bar.X + 6, chart.Bottom - 30));
            }

            var table = new Rect(260, 690, 1130, 190);
            d.DrawRoundedRectangle(Brushes.White, new Pen(Rgb(228, 231, 240), 1), table, 12, 12);
            string[] rows = english
                ? ["Top products        Units     Sales", "Wireless keyboard     1,240     $31.2K", "4K monitor 27\"        412     $28.9K"]
                : ["인기 상품                판매량      매출", "무선 키보드              1,240     3,720만", "27형 4K 모니터          412       3,090만"];
            for (int i = 0; i < rows.Length; i++)
                d.DrawText(Text(rows[i], i == 0 ? UiBold : Ui, 17, Rgb(40, 44, 58)), new Point(table.X + 24, table.Y + 22 + i * 50));
            d.Pop();
        });
    }

    private static BitmapSource DrawCode(bool english) => Rasterize(1440, 900, d =>
    {
        d.DrawRectangle(Rgb(30, 30, 30), null, new Rect(0, 0, 1440, 900));
        d.DrawRectangle(Rgb(37, 37, 38), null, new Rect(0, 0, 1440, 44));
        d.DrawRectangle(Rgb(30, 30, 30), null, new Rect(56, 0, 230, 44));
        d.DrawText(Text("CaptureService.cs", Ui, 15, Rgb(230, 230, 230)), new Point(78, 12));
        d.DrawRectangle(Rgb(51, 51, 51), null, new Rect(0, 0, 56, 900));
        (string Text, Color Color)[][] lines =
        [
            [("using", Color.FromRgb(86, 156, 214)), (" MyCapture.Core;", Color.FromRgb(212, 212, 212))],
            [],
            [("public sealed class ", Color.FromRgb(86, 156, 214)), ("CaptureService", Color.FromRgb(78, 201, 176))],
            [("{", Color.FromRgb(212, 212, 212))],
            [("    private readonly ", Color.FromRgb(86, 156, 214)), ("ILibrary", Color.FromRgb(78, 201, 176)), (" _library;", Color.FromRgb(212, 212, 212))],
            [],
            [(english ? "    // Save to the library before the editor opens" : "    // 편집기를 열기 전에 라이브러리에 먼저 저장", Color.FromRgb(106, 153, 85))],
            [("    public async ", Color.FromRgb(86, 156, 214)), ("Task", Color.FromRgb(78, 201, 176)), (" SaveAsync(", Color.FromRgb(212, 212, 212)), ("Capture", Color.FromRgb(78, 201, 176)), (" capture)", Color.FromRgb(212, 212, 212))],
            [("    {", Color.FromRgb(212, 212, 212))],
            [("        await ", Color.FromRgb(197, 134, 192)), ("_library.AddAsync(capture);", Color.FromRgb(212, 212, 212))],
            [("        _clipboard.Copy(capture.Image);", Color.FromRgb(212, 212, 212))],
            [("        _log.Information(", Color.FromRgb(212, 212, 212)), ("\"Saved {Id}\"", Color.FromRgb(206, 145, 120)), (", capture.Id);", Color.FromRgb(212, 212, 212))],
            [("    }", Color.FromRgb(212, 212, 212))],
            [("}", Color.FromRgb(212, 212, 212))],
        ];
        for (int i = 0; i < lines.Length; i++)
        {
            double y = 70 + i * 34;
            d.DrawText(Text((i + 1).ToString(CultureInfo.InvariantCulture), Mono, 17, Rgb(110, 118, 129)), new Point(14, y));
            double x = 80;
            foreach ((string text, Color color) in lines[i])
            {
                FormattedText run = Text(text, Mono, 19, new SolidColorBrush(color));
                d.DrawText(run, new Point(x, y));
                x += run.WidthIncludingTrailingWhitespace;
            }
        }
    });

    private static (BitmapSource, string) DrawArticle(bool english)
    {
        string title = english ? "5 ways to capture your screen faster" : "화면을 빠르게 캡처하는 5가지 방법";
        string[] body = english
            ?
            [
                "Region capture is the fastest way to share exactly what matters.",
                "1. Press Ctrl+Shift+C and drag across the area you need.",
                "2. Keep a reference visible with a pin on top of other windows.",
                "3. Hide email addresses and phone numbers before you share.",
                "4. Record a short clip and export it as a GIF.",
                "5. Find old captures by searching the text inside them.",
            ]
            :
            [
                "영역 캡처는 필요한 부분만 정확하게 공유하는 가장 빠른 방법입니다.",
                "1. Ctrl+Shift+C를 누르고 필요한 영역을 드래그합니다.",
                "2. 참고할 내용은 화면 고정으로 다른 창 위에 띄워 둡니다.",
                "3. 공유하기 전에 이메일과 전화번호를 가립니다.",
                "4. 짧은 화면을 녹화해 GIF로 내보냅니다.",
                "5. 이미지 속 글자로 예전 캡처를 검색합니다.",
            ];
        BitmapSource image = Rasterize(1440, 900, d =>
        {
            d.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1440, 900));
            BrowserChrome(d, 1440, english ? "blog.example.com/capture-tips" : "blog.example.com/capture-tips");
            d.DrawText(Text(english ? "Productivity · 5 min read" : "업무 팁 · 5분 읽기", Ui, 16, Rgb(124, 108, 255)), new Point(220, 110));
            d.DrawText(Text(title, UiBold, 40, Rgb(20, 24, 34)), new Point(220, 140));
            for (int i = 0; i < body.Length; i++)
                d.DrawText(Text(body[i], Ui, 22, Rgb(52, 56, 70)), new Point(220, 230 + i * 52));
            d.DrawRoundedRectangle(Rgb(238, 240, 247), null, new Rect(220, 560, 1000, 280), 14, 14);
            d.DrawRoundedRectangle(Rgb(124, 108, 255), null, new Rect(560, 620, 320, 160), 12, 12);
            d.DrawText(Text("Ctrl + Shift + C", UiBold, 30, Brushes.White), new Point(605, 680));
        });
        return (image, title + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, body));
    }

    private static (BitmapSource, RectD[]) DrawInquiry(bool english)
    {
        var regions = new List<RectD>();
        BitmapSource image = Rasterize(1440, 900, d =>
        {
            d.DrawRectangle(Rgb(246, 247, 251), null, new Rect(0, 0, 1440, 900));
            BrowserChrome(d, 1440, "support.example.com/tickets/2481");
            d.DrawText(Text(english ? "Customer inquiry #2481" : "고객 문의 #2481", UiBold, 30, Rgb(24, 28, 40)), new Point(120, 90));
            var card = new Rect(120, 150, 1200, 700);
            d.DrawRoundedRectangle(Brushes.White, new Pen(Rgb(228, 231, 240), 1), card, 14, 14);
            (string Label, string Value, bool Sensitive)[] fields = english
                ?
                [
                    ("Name", "Jordan Lee", false),
                    ("Email", "jordan.lee@example.com", true),
                    ("Phone", "+1 555-0134-2286", true),
                    ("Card", "4321 5678 9012 3456", true),
                    ("Order", "NW-20261008-0412", false),
                ]
                :
                [
                    ("이름", "홍길동", false),
                    ("이메일", "gildong.hong@example.com", true),
                    ("연락처", "010-2345-6789", true),
                    ("결제 카드", "4321-5678-9012-3456", true),
                    ("주문 번호", "NW-20261008-0412", false),
                ];
            for (int i = 0; i < fields.Length; i++)
            {
                double y = card.Y + 40 + i * 72;
                d.DrawText(Text(fields[i].Label, Ui, 20, Rgb(110, 116, 132)), new Point(card.X + 40, y));
                FormattedText value = Text(fields[i].Value, Ui, 24, Rgb(24, 28, 40));
                d.DrawText(value, new Point(card.X + 260, y - 3));
                if (fields[i].Sensitive)
                    regions.Add(new RectD(card.X + 252, y - 8, value.Width + 18, value.Height + 12));
                d.DrawLine(new Pen(Rgb(236, 238, 244), 1), new Point(card.X + 40, y + 50), new Point(card.Right - 40, y + 50));
            }

            d.DrawText(Text(english ? "Message" : "문의 내용", Ui, 20, Rgb(110, 116, 132)), new Point(card.X + 40, card.Y + 420));
            d.DrawText(Text(english
                    ? "The receipt email did not arrive after the payment.\nPlease resend it to the address above."
                    : "결제 후 영수증 메일을 받지 못했습니다.\n위 주소로 다시 보내 주시면 좋겠습니다.",
                Ui, 22, Rgb(52, 56, 70)), new Point(card.X + 260, card.Y + 418));
        });
        return (image, [.. regions]);
    }

    private static BitmapSource DrawSpreadsheet(bool english) => Rasterize(1440, 900, d =>
    {
        d.DrawRectangle(Brushes.White, null, new Rect(0, 0, 1440, 900));
        d.DrawRectangle(Rgb(16, 124, 65), null, new Rect(0, 0, 1440, 48));
        d.DrawText(Text(english ? "2026 budget.xlsx" : "2026 예산안.xlsx", UiBold, 18, Brushes.White), new Point(24, 12));
        string[] header = english ? ["Item", "Q1", "Q2", "Q3", "Q4", "Total"] : ["항목", "1분기", "2분기", "3분기", "4분기", "합계"];
        string[][] rows = english
            ?
            [
                ["Servers", "1,180", "1,210", "1,240", "1,310", "4,940"],
                ["Design", "820", "840", "860", "920", "3,440"],
                ["Marketing", "1,720", "1,860", "1,980", "2,150", "7,710"],
                ["Support", "640", "660", "690", "720", "2,710"],
                ["Training", "210", "240", "260", "300", "1,010"],
            ]
            :
            [
                ["서버", "1,180", "1,210", "1,240", "1,310", "4,940"],
                ["디자인", "820", "840", "860", "920", "3,440"],
                ["마케팅", "1,720", "1,860", "1,980", "2,150", "7,710"],
                ["고객 지원", "640", "660", "690", "720", "2,710"],
                ["교육", "210", "240", "260", "300", "1,010"],
            ];
        var grid = new Pen(Rgb(218, 220, 224), 1);
        for (int r = 0; r <= 18; r++)
            d.DrawLine(grid, new Point(0, 96 + r * 44), new Point(1440, 96 + r * 44));
        for (int c = 0; c <= 8; c++)
            d.DrawLine(grid, new Point(60 + c * 190, 52), new Point(60 + c * 190, 900));
        d.DrawRectangle(Rgb(226, 239, 218), null, new Rect(61, 97, 190 * 6 - 1, 43));
        for (int c = 0; c < header.Length; c++)
            d.DrawText(Text(header[c], UiBold, 18, Rgb(30, 34, 40)), new Point(76 + c * 190, 106));
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < rows[r].Length; c++)
                d.DrawText(Text(rows[r][c], Ui, 18, Rgb(40, 44, 52)), new Point(76 + c * 190, 150 + r * 44));
        for (int r = 0; r < 18; r++)
            d.DrawText(Text((r + 1).ToString(CultureInfo.InvariantCulture), Ui, 14, Rgb(120, 124, 130)), new Point(22, 108 + r * 44));
    });

    private static BitmapSource DrawChartCard(bool english) => Rasterize(720, 420, d =>
    {
        d.DrawRoundedRectangle(Brushes.White, null, new Rect(0, 0, 720, 420), 16, 16);
        d.DrawText(Text(english ? "Weekly active users" : "주간 활성 사용자", UiBold, 24, Rgb(24, 28, 40)), new Point(30, 26));
        d.DrawText(Text("18,240", UiBold, 40, Rgb(24, 28, 40)), new Point(30, 66));
        d.DrawText(Text("+9.6%", UiBold, 18, Rgb(22, 160, 110)), new Point(190, 84));
        double[] values = [40, 46, 44, 52, 58, 55, 63, 70, 68, 76, 82, 88];
        var points = values.Select((v, i) => new Point(40 + i * 58, 380 - v * 2.6)).ToArray();
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(new Point(points[0].X, 390), true, true);
            foreach (Point p in points) g.LineTo(p, true, true);
            g.LineTo(new Point(points[^1].X, 390), true, true);
        }

        d.DrawGeometry(new LinearGradientBrush(Color.FromArgb(90, 124, 108, 255), Color.FromArgb(0, 124, 108, 255), 90), null, geometry);
        var line = new StreamGeometry();
        using (StreamGeometryContext g = line.Open())
        {
            g.BeginFigure(points[0], false, false);
            foreach (Point p in points.Skip(1)) g.LineTo(p, true, true);
        }

        d.DrawGeometry(null, new Pen(Rgb(124, 108, 255), 4) { LineJoin = PenLineJoin.Round }, line);
    });
}
