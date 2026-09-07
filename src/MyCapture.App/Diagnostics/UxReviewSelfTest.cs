using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Pinning;
using MyCapture.App.Recording;
using MyCapture.App.Settings;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Recording;

namespace MyCapture.App.Diagnostics;

/// <summary>Isolated real-WPF review fixtures; never loads the user's queue or settings.</summary>
internal static class UxReviewSelfTest
{
    internal const string CommandLineSwitch = "--selftest-ux-review";

    internal static int Run()
    {
        DirectoryInfo output = UxReviewOutputDirectory.Create();
        Console.WriteLine($"UX_REVIEW_OUTPUT={output.FullName}");
        System.Diagnostics.Trace.TraceInformation("UX review output: {0}", output.FullName);
        try
        {
            return RunGenerated(output.FullName);
        }
        catch (Exception ex)
        {
            // A failure report belongs to this generated directory too; no command-line
            // output path is passed to the shell's generic fallback writer.
            File.WriteAllText(Path.Combine(output.FullName, "ux-review-selftest-report.txt"),
                $"RESULT: FAIL (unhandled exception)\n\n{ex}");
            return 2;
        }
    }

    private static int RunGenerated(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        // A fresh child on every invocation prevents recovery/retention work on earlier fixtures.
        AppPaths paths = AppPaths.CreateForRoot(Path.Combine(outputDirectory, "fixtures-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(paths.DataRoot);
        Directory.CreateDirectory(paths.CapturesRoot);
        var report = new StringBuilder("MyCapture real WPF UX review\n");
        report.AppendLine($"UTC: {DateTimeOffset.UtcNow:O}");
        report.AppendLine("Fixtures only. No screen capture, user queue, OS settings, registry, or clipboard writes.");
        IReadOnlyList<MonitorInfo> monitors = MonitorEnumerator.GetAll();
        foreach (MonitorInfo monitor in monitors)
            report.AppendLine($"Monitor: {monitor.DeviceName}; physical={monitor.Bounds}; DPI={monitor.Dpi}; primary={monitor.IsPrimary}");
        bool mixed = monitors.Count > 1 && monitors.Select(monitor => monitor.Dpi).Distinct().Count() > 1;
        report.AppendLine($"Native mixed-DPI topology available: {mixed}");
        report.AppendLine("PNG density 96/144/192 is offscreen raster density, not an OS monitor DPI switch. Native pointer crossing remains a separate interactive check.");

        bool? oldMotion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        report.AppendLine($"Reduced-motion test override: animations enabled={FluidMotion.AnimationsEnabled}");
        var windows = new List<(string Name, Window Window)>();
        using ILoggerFactory logs = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        var ocr = new FixtureOcr();
        using var presenter = new OcrResultPresenter(ocr, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
        var settings = new AppSettings();
        var queue = new CaptureQueue(paths, settings.Queue, logs.CreateLogger<CaptureQueue>());
        BitmapSource sample = CreateFixtureBitmap();
        var controller = new GalleryController(queue, logs.CreateLogger<GalleryController>());
        var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue, logs.CreateLogger<CapturePersistenceService>());
        var commit = new CaptureCommitService(persistence, () => settings, () => paths, logs.CreateLogger<CaptureCommitService>(), _ => Task.FromResult(true));
        var videoLibrary = new VideoLibraryService(queue, paths, logs.CreateLogger<VideoLibraryService>());
        int failures = 0;
        try
        {
            for (int index = 0; index < 6; index++)
            {
                var record = new CaptureRecord
                {
                    Width = sample.PixelWidth, Height = sample.PixelHeight,
                    Title = index == 0 ? "검토용 캡처 · 여러 줄 코드와 긴 한국어 제목 정렬 확인" : $"UI review sample {index + 1}",
                    CreatedAt = DateTimeOffset.Now.AddHours(-index * 7),
                    IsPinned = index == 1,
                    OcrText = "Synthetic local fixture", OcrContentRevision = 0,
                };
                queue.Add(record);
                string directory = queue.GetDirectory(record);
                Directory.CreateDirectory(directory);
                byte[] png = ImageCodec.EncodePng(sample);
                File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Thumbnail), png);
                File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Rendered), png);
                File.WriteAllBytes(Path.Combine(directory, CaptureFileNames.Original), png);
            }
            var viewModel = new GalleryViewModel(controller,
                record => Path.Combine(queue.GetDirectory(record), CaptureFileNames.Thumbnail), 320);
            var indexing = new OcrIndexingService(controller, ocr, queue.GetDirectory, () => settings.Ocr,
                logs.CreateLogger<OcrIndexingService>(), Dispatcher.CurrentDispatcher);
            windows.Add(("gallery", new GalleryWindow(viewModel, controller,
                new GalleryReeditLoader(queue, logs.CreateLogger<GalleryReeditLoader>()), commit, queue,
                videoLibrary, paths, logs, presenter, () => settings.Ocr, indexing,
                new FixturePrivacy(), NullLogger.Instance)));
            windows.Add(("settings", new SettingsWindow(() => settings,
                _ => new SettingsApplyResult(true, true, true, false, []), NullLogger.Instance)));
            var region = new RectD(0, 0, sample.PixelWidth, sample.PixelHeight);
            windows.Add(("annotation", new AnnotationEditorWindow(new FrozenFrame(sample, region, null, 0), region, sample)));
            RecordingResult recording = CreateFixtureVideo(Path.Combine(paths.DataRoot, "synthetic.mp4"), sample);
            var document = VideoEditDocument.CreateFor(recording.Width, recording.Height, recording.DurationMs);
            document.TextOverlays.Add(new TimedTextOverlay { Text = "텍스트 표시 구간 · 드래그로 조절", StartMs = 300, EndMs = 2200 });
            windows.Add(("video", new VideoEditorWindow(recording, paths, logs, document)));
            const string source = "public void Capture()\r\n{\r\n\tSave();\r\n}";
            BitmapSource code = ClipboardCodeRenderer.TryRender(source,
                "<div style=\"color: #d4d4d4; background-color: #1e1e1e; white-space: pre;\"><div><span style=\"color: #569cd6;\">public void</span> Capture()</div><div>{</div><div>    Save();</div><div>}</div></div>")
                ?? ClipboardTextRenderer.Render(source, false);
            var pin = new PinWindow(PinContent.FromText(code, source, false), new PinViewState(code.PixelWidth, code.PixelHeight, 1, 1, 0.1), 40, 40, () => settings.Pin)
            {
                Title = "MyCapture UX Pin fixture",
                ShowInTaskbar = true,
            };
            windows.Add(("pin-code", pin));
            bool imageCopy = false;
            string inputLog = Path.Combine(outputDirectory, "pin-input-events.txt");
            pin.PreviewMouseLeftButtonDown += (_, args) =>
            {
                Point local = args.GetPosition(pin);
                var cursor = WindowStyleFacade.GetCursorPosition();
                File.AppendAllText(inputLog, $"{DateTimeOffset.UtcNow:O} POINTER DOWN local=[{local.X:0.##},{local.Y:0.##}]; cursor=[{cursor.X},{cursor.Y}]\n");
            };
            pin.LocationChanged += (_, _) =>
            {
                var bounds = WindowStyleFacade.GetWindowBounds(new WindowInteropHelper(pin).Handle);
                File.AppendAllText(inputLog, $"{DateTimeOffset.UtcNow:O} POSITION physical=[{bounds.Left},{bounds.Top}]\n");
            };
            pin.CopyRequested += (_, image) =>
            {
                imageCopy = ReferenceEquals(code, image);
                File.AppendAllText(inputLog, $"{DateTimeOffset.UtcNow:O} IMAGE identity={imageCopy}; modifiers={Keyboard.Modifiers}\n");
            };
            pin.OriginalTextCopyRequested += (_, text) =>
                File.AppendAllText(inputLog, $"{DateTimeOffset.UtcNow:O} TEXT exact={text == source}; modifiers={Keyboard.Modifiers}\n");
            pin.OcrRequested += (_, _) =>
                File.AppendAllText(inputLog, $"{DateTimeOffset.UtcNow:O} UNEXPECTED OCR on source-text fixture\n");
            // Invoke the real Ctrl+C-labelled command without touching the system clipboard.
            MenuItem copy = pin.ContextMenu.Items.OfType<MenuItem>().Single(item => item.InputGestureText == "Ctrl+C");
            copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            report.AppendLine($"Pin Ctrl+C image command returns rendered bitmap: {imageCopy}");
            if (!imageCopy) failures++;

            foreach ((string name, Window window) in windows)
            {
                try
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = 30;
                    window.Top = 30;
                    window.ShowActivated = false;
                    window.Show();
                    Pump(TimeSpan.FromMilliseconds(name == "video" ? 1800 : 180));
                    if (window is PinWindow floating)
                    {
                        IntPtr handle = new WindowInteropHelper(floating).Handle;
                        (int initialLeft, int initialTop, int initialRight, int initialBottom) = WindowStyleFacade.GetWindowBounds(handle);
                        int width = initialRight - initialLeft;
                        int height = initialBottom - initialTop;
                        foreach (MonitorInfo monitor in monitors)
                        {
                            foreach ((double x, double y) in new[]
                            {
                                (monitor.Bounds.Left, monitor.Bounds.Top),
                                (monitor.Bounds.Right - width, monitor.Bounds.Bottom - height),
                            })
                            {
                                floating.MovePhysicalForTest(x, y);
                                Pump(TimeSpan.FromMilliseconds(50));
                                (int actualLeft, int actualTop, _, _) = WindowStyleFacade.GetWindowBounds(handle);
                                bool reached = Math.Abs(actualLeft - x) <= 1 && Math.Abs(actualTop - y) <= 1;
                                report.AppendLine($"Native pin endpoint {monitor.DeviceName}: requested=[{x:0},{y:0}], actual=[{actualLeft},{actualTop}], reached={reached}");
                                if (!reached) failures++;
                            }
                        }
                        floating.MovePhysicalForTest(initialLeft, initialTop);
                        report.AppendLine("Native endpoint test calls the production physical-move path and reads HWND bounds; pointer-gesture validation is not claimed.");
                    }
                    if (window is VideoEditorWindow video)
                        report.AppendLine($"Video media ready={video.IsMediaReadyForTest}; failed={video.HasMediaFailedForTest}");
                    double normalWidth = window.Width;
                    double normalHeight = window.Height;
                    foreach (bool compact in new[] { false, true })
                    {
                        window.Width = compact ? Math.Max(window.MinWidth, name == "pin-code" ? normalWidth : 780) : normalWidth;
                        window.Height = compact ? Math.Max(window.MinHeight, name == "pin-code" ? normalHeight : 560) : normalHeight;
                        window.UpdateLayout();
                        Pump(TimeSpan.FromMilliseconds(100));
                        string label = name + (compact ? "-compact" : "-normal");
                        foreach (int dpi in new[] { 96, 144, 192 })
                            Render(window, Path.Combine(outputDirectory, $"{label}-{dpi}dpi.png"), dpi);
                        WriteLayoutInventory(window, Path.Combine(outputDirectory, label + "-layout.json"));
                        report.AppendLine($"Rendered {label}: {window.ActualWidth:0}x{window.ActualHeight:0} DIP; native scale={VisualTreeHelper.GetDpi(window).DpiScaleX:0.##}");
                    }
                    if (window is SettingsWindow)
                    {
                        TabControl? tabs = Descendants(window).OfType<TabControl>().FirstOrDefault();
                        if (tabs is not null)
                            for (int index = 0; index < tabs.Items.Count; index++)
                            {
                                tabs.SelectedIndex = index;
                                if (tabs.ItemContainerGenerator.ContainerFromIndex(index) is TabItem tab) tab.BringIntoView();
                                window.UpdateLayout();
                                Pump(TimeSpan.FromMilliseconds(80));
                                Render(window, Path.Combine(outputDirectory, $"settings-page-{index + 1}-compact-144dpi.png"), 144);
                                WriteLayoutInventory(window, Path.Combine(outputDirectory, $"settings-page-{index + 1}-layout.json"));
                            }
                    }
                    bool focusMoved = window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                    report.AppendLine($"Focus {name}: moved={focusMoved}; target={Keyboard.FocusedElement?.GetType().Name ?? "none"}");
                    window.Hide();
                }
                catch (Exception ex)
                {
                    failures++;
                    report.AppendLine($"FAIL {name}: {ex}");
                }
            }

            if (int.TryParse(Environment.GetEnvironmentVariable("MYCAPTURE_UX_REVIEW_HOLD_SECONDS"), out int seconds) && seconds > 0)
            {
                report.AppendLine("Interactive fixture windows held open; no automatic keyboard/pointer input sent.");
                foreach (var fixture in windows) fixture.Window.Show();
                MonitorInfo primary = monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
                IntPtr pinHandle = new WindowInteropHelper(pin).Handle;
                (int pinLeft, _, int pinRight, _) = WindowStyleFacade.GetWindowBounds(pinHandle);
                pin.MovePhysicalForTest(primary.Bounds.Right - (pinRight - pinLeft), primary.Bounds.Top + 100);
                report.AppendLine("Interactive pin fixture starts at the primary monitor's right boundary for bounded cross-monitor drag checks; taskbar/title metadata is diagnostic-only.");
                File.WriteAllText(Path.Combine(outputDirectory, "ux-review-selftest-report.txt"), report.ToString());
                for (int elapsed = 0; elapsed < Math.Min(seconds, 600); elapsed++) Pump(TimeSpan.FromSeconds(1));
            }
        }
        finally
        {
            foreach (var fixture in windows)
            {
                if (fixture.Window is GalleryWindow gallery) gallery.CloseForExit();
                else if (fixture.Window is SettingsWindow preferences) preferences.CloseForExit();
                else fixture.Window.Close();
            }
            FluidMotion.AnimationsEnabledOverrideForTest = oldMotion;
        }
        report.AppendLine("Layout JSON records visible text, font metrics, wrapping/trimming, and control automation/focus metadata; candidates require visual review, not automatic pass claims.");
        report.AppendLine(failures == 0 ? "RESULT: PASS (fixture rendering and command checks; manual visual review required)" : $"RESULT: FAIL ({failures})");
        File.WriteAllText(Path.Combine(outputDirectory, "ux-review-selftest-report.txt"), report.ToString(), new UTF8Encoding(false));
        return failures == 0 ? 0 : 1;
    }

    private static BitmapSource CreateFixtureBitmap()
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 29, 46)), null, new Rect(0, 0, 640, 360));
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(29, 47, 68)), null, new Rect(28, 28, 584, 304), 12, 12);
            var title = new FormattedText("MyCapture · 검토용 캡처", CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), 26, Brushes.White, 1);
            drawing.DrawText(title, new Point(54, 58));
            var code = new FormattedText("public void Capture()\n{\n    Save();\n}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), 23, Brushes.LightSkyBlue, 1);
            drawing.DrawText(code, new Point(54, 120));
            drawing.DrawRoundedRectangle(Brushes.CornflowerBlue, null, new Rect(448, 273, 128, 34), 6, 6);
        }
        var bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static RecordingResult CreateFixtureVideo(string path, BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        converted.CopyPixels(pixels, image.PixelWidth * 4, 0);
        using var encoder = new MediaFoundationVideoEncoder(new VideoEncoderOptions(path, image.PixelWidth, image.PixelHeight, 15, 1_000_000), NullLogger<MediaFoundationVideoEncoder>.Instance);
        for (int frame = 0; frame < 45; frame++) encoder.WriteFrame(new EncoderFrame(pixels, image.PixelWidth, image.PixelHeight, image.PixelWidth * 4, frame * (1000.0 / 15)));
        encoder.Complete();
        return new RecordingResult(path, 3000, 15, 45, image.PixelWidth, image.PixelHeight);
    }

    private static void Render(Window window, string path, int dpi)
    {
        window.UpdateLayout();
        double scale = dpi / 96.0;
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(window.ActualWidth * scale)),
            Math.Max(1, (int)Math.Ceiling(window.ActualHeight * scale)), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(parent);
        while (pending.Count > 0)
        {
            DependencyObject item = pending.Pop();
            yield return item;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(item); index++) pending.Push(VisualTreeHelper.GetChild(item, index));
        }
    }

    private static void WriteLayoutInventory(Window window, string path)
    {
        var entries = new List<object>();
        foreach (FrameworkElement element in Descendants(window).OfType<FrameworkElement>().Where(element => element.IsVisible))
        {
            if (element is TextBlock text && !string.IsNullOrWhiteSpace(text.Text))
            {
                entries.Add(new { Kind = "Text", text.Text, text.FontSize, Font = text.FontFamily.Source,
                    Width = text.ActualWidth, Height = text.ActualHeight, Wrapping = text.TextWrapping.ToString(), Trimming = text.TextTrimming.ToString(),
                    ClipCandidate = text.DesiredSize.Width > text.ActualWidth + 1 || text.DesiredSize.Height > text.ActualHeight + 1 });
            }
            else if (element is ButtonBase or TextBox or ComboBox or Slider)
            {
                AutomationPeer? peer = UIElementAutomationPeer.CreatePeerForElement(element);
                entries.Add(new { Kind = element.GetType().Name, Name = peer?.GetName() ?? AutomationProperties.GetName(element),
                    Width = element.ActualWidth, Height = element.ActualHeight, element.Focusable, element.IsEnabled, element.IsKeyboardFocused });
            }
        }
        File.WriteAllText(path, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed class FixtureOcr : IOcrService
    {
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["en", "ko"];
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(OcrResult.Success("Synthetic local fixture", "en", [], TimeSpan.Zero));
    }

    private sealed class FixturePrivacy : IPrivacyRedactionService
    {
        public bool IsAvailable => true;
        public Task<PrivacyRedactionResult> FindAsync(BitmapSource bitmap, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PrivacyRedactionResult(PrivacyRedactionStatus.NoMatches, []));
    }
}
