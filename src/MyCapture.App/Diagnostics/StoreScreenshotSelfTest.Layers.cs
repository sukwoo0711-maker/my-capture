using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
using MyCapture.Core.Privacy;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Core.Undo;
using MyCapture.Ocr;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Recording;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Diagnostics;

internal static partial class StoreScreenshotSelfTest
{
    internal const string LayersCommandLineSwitch = "--selftest-store-layers";

    internal static (string Output, string Language) ParseLayersArguments(string[] args, int index)
    {
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1])
            || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("--selftest-store-layers requires an output directory and optional ko|en.");
        string language = index + 2 < args.Length ? args[index + 2].ToLowerInvariant() : "ko";
        if (language is not ("ko" or "en") || args.Length > index + 3)
            throw new ArgumentException("Store layer language must be ko or en, with no extra arguments.");
        return (Path.GetFullPath(args[index + 1]), language);
    }

    internal static int RunLayers(string outputDirectory, string? language)
    {
        var export = new LayerExport(DiagnosticOutputPaths.Create(outputDirectory), language == "en");
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        bool? contrast = ThemeService.SystemHighContrastOverrideForTest;
        ThemeService.SystemHighContrastOverrideForTest = false;
        Func<bool>? hints = ShortcutPresentation.ReadEnabled;
        Func<HotkeySettings> hotkeys = ShortcutHelpWindow.ReadHotkeys;
        ShortcutHelpWindow.ReadHotkeys = () => NewInstallDefaults.Create().Hotkeys;
        ShortcutPresentation.ReadEnabled = () => false;
        ShortcutPresentation.Refresh();
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        UiText.Configure(language == "en" ? "en" : "ko");
        InputManager.Current.PreProcessInput += BlockLayerInput;
        try
        {
            using (UiText.UseLanguage(language == "en" ? "en-US" : "ko-KR")) export.Run();
        }
        catch (Exception ex) { export.Fail("run", ex); }
        finally
        {
            try { export.Finish(); }
            finally
            {
                InputManager.Current.PreProcessInput -= BlockLayerInput;
                FluidMotion.AnimationsEnabledOverrideForTest = motion;
                ThemeService.SystemHighContrastOverrideForTest = contrast;
                ShortcutPresentation.ReadEnabled = hints;
                ShortcutHelpWindow.ReadHotkeys = hotkeys;
                ShortcutPresentation.Refresh();
                UiText.Configure(null);
            }
        }
        return export.Failures == 0 ? 0 : 1;
    }

    private static void BlockLayerInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is KeyEventArgs or TextCompositionEventArgs or MouseButtonEventArgs or MouseWheelEventArgs or KeyboardFocusChangedEventArgs)
            e.Cancel();
    }

    // The production handler activates the editor. The diagnostic omits that event entirely.
    private sealed class LayerEditor(FrozenFrame frame, RectD region, BitmapSource image,
        AnnotationDocument document, IPrivacyRedactionService? privacy = null, string? title = null)
        : AnnotationEditorWindow(frame, region, image, initialDocument: document,
            privacyRedactionService: privacy, title: title)
    {
        protected override void OnContentRendered(EventArgs e) { }
    }

    private sealed class LayerOcr(OcrResult result) : IOcrService
    {
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["ko", "en"];
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed partial class LayerExport(string root, bool english)
    {
        private const double Scale = 3;
        private static double Offscreen => Math.Min(-24000,
            Math.Min(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop) - 10000);
        private readonly string _root = root;
        private readonly bool _english = english;
        private readonly string _fixtures = DiagnosticOutputPaths.Child(root, "_fixtures-" + Guid.NewGuid().ToString("N"));
        private readonly StringBuilder _report = new("MyCapture Store layers\n");
        private readonly List<object> _files = [];
        private readonly List<Window> _windows = [];
        internal int Failures { get; private set; }

        internal void Fail(string scene, Exception ex)
        {
            Failures++;
            _report.AppendLine($"FAIL {scene}: {ex}");
        }

        private void Scene(string name, Action action)
        {
            try { action(); }
            catch (Exception ex) { Fail(name, ex); }
        }

        internal void Run()
        {
            ThemeService.ApplyFromSettings(AppThemeNames.Glass, accentColor: "Red");
            Directory.CreateDirectory(_fixtures);
            // Product code such as the GIF preview writes work files under Path.GetTempPath();
            // pointing this process's TMP/TEMP at the fixture folder keeps every write under the output directory.
            _previousTmp = Environment.GetEnvironmentVariable("TMP");
            _previousTemp = Environment.GetEnvironmentVariable("TEMP");
            Environment.SetEnvironmentVariable("TMP", _fixtures);
            Environment.SetEnvironmentVariable("TEMP", _fixtures);
            _report.AppendLine($"Language: {(_english ? "en" : "ko")}; theme: Glass/Dark; scale: 3x (code sample: 2x)");
            _report.AppendLine("OCR fixture geometry is measured from synthetic text, with no OCR model or user capture. Redaction uses PrivacyRedactionService, PrivacyDetector and the real editor action.");
            Scene("code", () => Emit("samples/code.png", CodeSample(2), 1440, 900, 2));
            Scene("redaction", ExportRedaction);
            Scene("annotation", ExportAnnotation);
            Scene("pin", ExportPin);
            Scene("quick card", ExportQuickCard);
            Scene("OCR", ExportOcr);
            Scene("library", ExportLibrary);
            Scene("video", ExportVideo);
            ExportPinLayers();
        }

        private string? _previousTmp;
        private string? _previousTemp;

        internal void Finish()
        {
            Environment.SetEnvironmentVariable("TMP", _previousTmp);
            Environment.SetEnvironmentVariable("TEMP", _previousTemp);
            foreach (Window window in _windows.ToArray())
            {
                try { CloseLayerWindow(window); }
                catch (Exception ex) { Fail("close window", ex); }
            }
            // The fixtures are synthetic files this process created under the output directory.
            // A file still held by a closing window only leaves the folder behind; it is not a failure.
            try
            {
                if (Directory.Exists(_fixtures)) global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteDirectory(_fixtures, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _report.AppendLine("Fixtures left in place: " + _fixtures + " (" + ex.Message + ")");
            }
            var manifest = new
            {
                generator = new
                {
                    name = "MyCapture --selftest-store-layers",
                    appVersion = typeof(App).Assembly.GetName().Version?.ToString(),
                    language = _english ? "en" : "ko",
                    theme = "Dark",
                    fixtureOcr = "Measured synthetic text; real privacy detector and editor redaction",
                },
                files = _files,
            };
            File.WriteAllText(DiagnosticOutputPaths.Child(_root, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            if (_files.Count != ExpectedLayerCount) { Failures++; _report.AppendLine($"FAIL expected {ExpectedLayerCount} layers, got {_files.Count}"); }
            _report.AppendLine(Failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({Failures})");
            File.WriteAllText(DiagnosticOutputPaths.Child(_root, "store-layers-report.txt"), _report.ToString(), new UTF8Encoding(false));
        }

        private void Emit(string relative, BitmapSource image, double logicalWidth, double logicalHeight, double scale = Scale,
            double windowOpacity = 1)
        {
            if (image.PixelWidth != (int)Math.Round(logicalWidth * scale) || image.PixelHeight != (int)Math.Round(logicalHeight * scale))
                throw new InvalidDataException($"Unexpected layer size for {relative}: {image.PixelWidth}x{image.PixelHeight}");
            string[] parts = relative.Split('/');
            string directory = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(_root, parts[0]));
            Save(image, DiagnosticOutputPaths.Child(directory, parts[1]));
            _files.Add(new { path = relative, width = image.PixelWidth, height = image.PixelHeight,
                renderScale = scale, logicalWidth, logicalHeight, windowOpacity, theme = "Dark",
                background = relative.StartsWith("samples/", StringComparison.Ordinal)
                    || relative is "film/editor-before-paste.png" or "film/sheet.png" ? "opaque" : "transparent" });
            _report.AppendLine($"PASS {relative}: {image.PixelWidth}x{image.PixelHeight}, {scale}x");
        }

        private void OpenLayerWindow(Window window, double width, double height, int settleMs)
        {
            _windows.Add(window);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = Offscreen;
            window.Top = Offscreen;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.PreviewGotKeyboardFocus += (_, e) => e.Handled = true;
            window.SizeToContent = SizeToContent.Manual;
            window.Width = width;
            window.Height = height;
            window.Show();
            Pump(80);
            // Native captions are not part of WPF's client visual. Fit the client itself to the contract.
            for (int pass = 0; pass < 5; pass++)
            {
                window.UpdateLayout();
                FrameworkElement client = LayerClient(window);
                double dx = width - client.ActualWidth, dy = height - client.ActualHeight;
                if (Math.Abs(dx) < .1 && Math.Abs(dy) < .1) break;
                window.MinWidth = Math.Min(window.MinWidth, width);
                window.MinHeight = Math.Min(window.MinHeight, height);
                window.Width = window.ActualWidth + dx;
                window.Height = window.ActualHeight + dy;
                Pump(60);
            }
            Pump(settleMs);
            window.UpdateLayout();
            if (window.Left > -10000 || window.Top > -10000)
                throw new InvalidOperationException("Diagnostic window did not remain offscreen.");
        }

        private static FrameworkElement LayerClient(Window window) =>
            VisualTreeHelper.GetChildrenCount(window) > 0 && VisualTreeHelper.GetChild(window, 0) is FrameworkElement element ? element : window;

        private static BitmapSource RenderLayerWindow(Window window)
        {
            window.UpdateLayout();
            FrameworkElement client = LayerClient(window);
            var bitmap = new RenderTargetBitmap((int)Math.Round(client.ActualWidth * Scale),
                (int)Math.Round(client.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            bitmap.Render(client);
            bitmap.Freeze();
            return bitmap;
        }

        private void CloseLayerWindow(Window window)
        {
            switch (window)
            {
                case GalleryWindow library: library.CloseForExit(); break;
                case OcrResultWindow ocr: ocr.CloseForExit(); break;
                case AnnotationEditorWindow editor: editor.ConfirmClose = () => EditorCloseChoice.DontSave; editor.Close(); break;
                default: window.Close(); break;
            }
            _windows.Remove(window);
            Pump(40);
        }

        private static void Pump(int milliseconds) => UxReviewSelfTest.Pump(TimeSpan.FromMilliseconds(milliseconds));

        private void ExportRedaction()
        {
            var lines = new List<OcrLine>();
            BitmapSource source = ErrorSample(lines);
            OcrResult result = OcrResult.Success(string.Join(Environment.NewLine, lines.Select(x => x.Text)),
                _english ? "en" : "ko", lines, TimeSpan.Zero);
            var privacy = new PrivacyRedactionService(new LayerOcr(result), new PrivacyDetector(), () => new OcrSettings());
            var region = new RectD(0, 0, source.PixelWidth, source.PixelHeight);
            var window = new LayerEditor(new FrozenFrame(source, region, null, 0), region, source,
                AnnotationDocument.CreateFor(source.PixelWidth, source.PixelHeight), privacy,
                _english ? "Log upload failure" : "로그 업로드 오류");
            try
            {
                OpenLayerWindow(window, 1240, 780, 300);
                var editor = (AnnotationEditorControl)window.Content;
                ClearLayerSelection(editor);
                Emit("redact/00-before.png", RenderLayerWindow(window), 1240, 780);
                Task action = editor.ApplyPrivacyRedactionsAsync();
                while (!action.IsCompleted) Pump(20);
                action.GetAwaiter().GetResult();
                PrivacyRedactionResult found = privacy.FindAsync(source).GetAwaiter().GetResult();
                if (found.Status != PrivacyRedactionStatus.Success || found.Regions.Count != 3)
                    throw new InvalidDataException($"Expected email, token and card matches; got {found.Regions.Count}.");
                ClearLayerSelection(editor);
                Pump(100);
                Emit("redact/03.png", RenderLayerWindow(window), 1240, 780);
                _report.AppendLine("Redaction: 3 real detector matches; word bounds from FormattedText at fixture origins plus production 3px padding; selection cleared before both renders.");
                Scene("annotated redaction", () => ExportRedactionAnnotated(window));
            }
            finally { CloseLayerWindow(window); }
        }

        private static void ClearLayerSelection(AnnotationEditorControl editor)
        {
            var controller = (AnnotationEditorController)(typeof(AnnotationEditorControl)
                .GetField("_controller", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(editor)
                ?? throw new MissingFieldException(nameof(AnnotationEditorControl), "_controller"));
            controller.SetSelected(null);
            editor.UpdateLayout();
        }

        private void ExportAnnotation()
        {
            BitmapSource source = TerminalSample(false);
            var document = AnnotationDocument.CreateFor(source.PixelWidth, source.PixelHeight);
            var defaults = new AnnotationDefaults();
            // The terminal sample uses 31 px text, so the marks use the editor's larger user-selectable
            // sizes (font 30, stroke 6) to stay readable once the window is scaled into the Store image.
            const double MarkFontSize = 30;
            var controller = new AnnotationEditorController(document, new UndoStack()) { DefaultFontSize = MarkFontSize };
            document.Add(Arrow(new PointD(850, 390), new PointD(380, 478), defaults.StrokeColor, 6));
            controller.AddStepNumber(new PointD(18, 334));
            controller.AddStepNumber(new PointD(18, 484));
            document.Add(new TextAnnotation
            {
                Rect = new RectD(870, 360, 400, 44),
                Text = _english ? "After retry" : "재시도 후 발생",
                Foreground = defaults.TextColor,
                Background = defaults.TextBackground ? ColorRgba.Black : ColorRgba.Transparent,
                FontSize = MarkFontSize,
                FontFamily = defaults.FontFamily,
                Bold = true,
                AutoSize = true,
                HasOutline = defaults.TextOutline,
                OutlineStyle = TextOutlineStyle.Consistent,
            });
            var region = new RectD(0, 0, source.PixelWidth, source.PixelHeight);
            var window = new LayerEditor(new FrozenFrame(source, region, null, 0), region, source, document);
            try { OpenLayerWindow(window, 1240, 780, 300); Emit("annotation/06-step2.png", RenderLayerWindow(window), 1240, 780); }
            finally { CloseLayerWindow(window); }
        }

        private void ExportPin()
        {
            BitmapSource reference = DrawSample(436, 184, 1, dc =>
            {
                SampleText(dc, "CaptureService.cs · API", 18, 14, 18, UiBold);
                SampleText(dc, "Task<Capture> SaveAsync(Capture capture)", 18, 55, 16, Mono);
                SampleText(dc, _english ? "Returns the saved capture." : "저장한 캡처를 반환합니다.", 18, 94, 17);
                SampleText(dc, _english ? "Throws on timeout." : "시간 초과 시 예외가 발생합니다.", 18, 129, 16);
            });
            var settings = new AppSettings();
            var window = new PinWindow(PinContent.FromImage(reference), new PinViewState(436, 184, 1, 1, .1),
                Offscreen, Offscreen, () => settings.Pin);
            try
            {
                OpenLayerWindow(window, 436, 184, 250);
                window.SetChromeActiveForTest(true);
                Emit("pins/code.png", RenderLayerWindow(window), 436, 184);
            }
            finally { CloseLayerWindow(window); }
        }

        private void ExportQuickCard()
        {
            var area = new RectD(Offscreen, Offscreen, 1920, 1080);
            var monitor = new MonitorInfo("StoreLayersFixture", area, area, 96, false);
            BitmapSource capture = DrawSample(310, 50, 1, dc =>
            {
                SampleText(dc, "CaptureService.cs", 8, 3, 14, UiBold);
                SampleText(dc, "await library.AddAsync(capture);", 8, 25, 14, Mono);
            });
            var window = new QuickAccessWindow(capture, () => Task.FromResult(true), () => { }, () => Task.CompletedTask,
                () => Task.FromResult(true), () => { }, () => Task.FromResult("CaptureService.cs.png"), targetMonitor: monitor)
            { IsKept = true };
            try
            {
                OpenLayerWindow(window, 344, 215, 250);
                Emit("quick-access/dashboard.png", RenderLayerWindow(window), 344, 215);
                _report.AppendLine("Quick card: 344x215 DIP at 3x, kept and not activated; C/E/P/S/T live in tooltips and automation names, so no key captions are drawn.");
            }
            finally { CloseLayerWindow(window); }
        }

        private void ExportOcr()
        {
            var lines = new List<OcrLine>();
            BitmapSource source = TerminalSample(false, lines);
            var result = OcrResult.Success(string.Join(Environment.NewLine, lines.Select(x => x.Text)), _english ? "en" : "ko", lines, TimeSpan.Zero);
            var window = new OcrResultWindow();
            try
            {
                window.SetSourceImage(source);
                MethodInfo method = typeof(OcrResultWindow).GetMethod("ApplyResult", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(nameof(OcrResultWindow), "ApplyResult");
                method.Invoke(window, [result, "dotnet test · CaptureService.Tests"]);
                OpenLayerWindow(window, 1100, 640, 300);
                foreach (TextBox box in UxReviewSelfTest.Descendants(window).OfType<TextBox>()) box.Select(0, 0);
                Emit("ocr/result-source.png", RenderLayerWindow(window), 1100, 640);
            }
            finally { CloseLayerWindow(window); }
        }

        private void ExportLibrary()
        {
            AppPaths paths = AppPaths.CreateForRoot(DiagnosticOutputPaths.Child(_fixtures, "library"));
            Directory.CreateDirectory(paths.CapturesRoot);
            var settings = new AppSettings();
            using ILoggerFactory logs = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            var queue = new CaptureQueue(paths, settings.Queue, logs.CreateLogger<CaptureQueue>());
            DateTimeOffset now = new(2026, 10, 19, 15, 30, 0, TimeSpan.Zero);
            string[] titles = ["CaptureService.cs — timeout", "UploadTests.cs", "RetryTests.cs — timeout"];
            string[] ocrTexts = ["SaveAsync(capture)", "Request timeout", "Request timeout after retry"];
            for (int i = 0; i < 3; i++)
            {
                BitmapSource image = i == 0 ? CodeSample() : TerminalSample(false);
                byte[] png = ImageCodec.EncodePng(image);
                var record = new CaptureRecord { Width = image.PixelWidth, Height = image.PixelHeight,
                    Title = titles[i], SourceWindowTitle = titles[i], OcrText = ocrTexts[i], OcrContentRevision = 0,
                    CreatedAt = now.AddMinutes(-i), TotalBytes = png.LongLength * 3 };
                queue.Add(record);
                string directory = queue.GetDirectory(record);
                Directory.CreateDirectory(directory);
                foreach (string name in new[] { CaptureFileNames.Thumbnail, CaptureFileNames.Rendered, CaptureFileNames.Original })
                    File.WriteAllBytes(DiagnosticOutputPaths.Child(directory, name), png);
            }
            var controller = new GalleryController(queue, logs.CreateLogger<GalleryController>());
            var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue, logs.CreateLogger<CapturePersistenceService>());
            var commit = new CaptureCommitService(persistence, () => settings, () => paths, logs.CreateLogger<CaptureCommitService>(), (_, _) => Task.FromResult(true));
            var video = new VideoLibraryService(queue, paths, logs.CreateLogger<VideoLibraryService>());
            var ocr = new UxReviewSelfTest.FixtureOcr();
            using var presenter = new OcrResultPresenter(ocr, System.Windows.Threading.Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
            var viewModel = new GalleryViewModel(controller, record => Path.Combine(queue.GetDirectory(record), CaptureFileNames.Thumbnail), 480, clock: () => now);
            var indexing = new OcrIndexingService(controller, ocr, queue.GetDirectory, () => settings.Ocr,
                logs.CreateLogger<OcrIndexingService>(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
            var window = new GalleryWindow(viewModel, controller, new GalleryReeditLoader(queue, logs.CreateLogger<GalleryReeditLoader>()),
                commit, queue, video, paths, logs, presenter, () => settings.Ocr, indexing, new UxReviewSelfTest.FixturePrivacy(), NullLogger.Instance);
            try
            {
                OpenLayerWindow(window, 1240, 760, 400);
                window.SearchBox.Text = "timeout";
                Pump(600);
                if (viewModel.VisibleCount != 3) throw new InvalidDataException($"Expected 3 timeout hits, got {viewModel.VisibleCount}.");
                // Thumbnails decode in the background; render only after every hit shows its image.
                GalleryItemViewModel[] hits = viewModel.Groups.SelectMany(group => group.Items).ToArray();
                DateTime thumbnailDeadline = DateTime.UtcNow.AddSeconds(10);
                while (hits.Any(hit => hit.Thumbnail is null && !hit.IsBroken) && DateTime.UtcNow < thumbnailDeadline) Pump(100);
                Pump(200);
                if (hits.Any(hit => hit.Thumbnail is null || hit.IsBroken))
                    throw new InvalidDataException("A library thumbnail did not load within 10 seconds.");
                Emit("library/midnight-search.png", RenderLayerWindow(window), 1240, 760);
            }
            finally { CloseLayerWindow(window); }
        }

        private void ExportVideo()
        {
            using ILoggerFactory logs = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
            AppPaths paths = AppPaths.CreateForRoot(_fixtures);
            RecordingResult recording = CreateDeveloperRecording();
            Scene("video editor", () => ExportVideoEditor(recording, paths, logs));
            Scene("GIF export", () => ExportGifDialog(recording, logs));
        }

        private RecordingResult CreateDeveloperRecording()
        {
            BitmapSource failed = TerminalSample(passed: false);
            BitmapSource passed = TerminalSample(passed: true);
            if (failed.PixelWidth != 1440 || failed.PixelHeight != 900
                || passed.PixelWidth != failed.PixelWidth || passed.PixelHeight != failed.PixelHeight)
            {
                throw new InvalidOperationException("The terminal video requires two 1440x900 fixture frames.");
            }

            byte[] failedPixels = VideoFramePixels(failed);
            byte[] passedPixels = VideoFramePixels(passed);
            if (failedPixels.AsSpan().SequenceEqual(passedPixels))
                throw new InvalidOperationException("The terminal video requires different failure and success frames.");
            DiagnosticOutputPaths.Create(_fixtures);
            string path = DiagnosticOutputPaths.Child(_fixtures, "developer-terminal.mp4");
            const int fps = 15;
            const int frameCount = 45;
            using (var encoder = new MediaFoundationVideoEncoder(
                new VideoEncoderOptions(path, failed.PixelWidth, failed.PixelHeight, fps, 2_000_000),
                NullLogger<MediaFoundationVideoEncoder>.Instance))
            {
                // All pixels are generated fixtures. No capture or recording device is opened.
                // The success result begins at frame 24 (1.6 s), with the second timed note.
                for (int index = 0; index < frameCount; index++)
                {
                    byte[] pixels = index < 24 ? failedPixels : passedPixels;
                    encoder.WriteFrame(new EncoderFrame(pixels, failed.PixelWidth, failed.PixelHeight,
                        failed.PixelWidth * 4, index * (1000.0 / fps)));
                }
                encoder.Complete();
            }

            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0)
                throw new IOException("The synthetic terminal video encoder produced no output.");

            _report.AppendLine($"Video fixture: {path}; 1440x900; {fps} fps; {frameCount} frames; 3000 ms; {file.Length} bytes; generated terminal failure then success.");
            return new RecordingResult(path, 3000, fps, frameCount, failed.PixelWidth, failed.PixelHeight);
        }

        private static byte[] VideoFramePixels(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[checked(source.PixelWidth * source.PixelHeight * 4)];
            converted.CopyPixels(pixels, source.PixelWidth * 4, 0);
            return pixels;
        }

        private VideoEditDocument DeveloperVideoDocument(RecordingResult recording)
        {
            var document = VideoEditDocument.CreateFor(recording.Width, recording.Height, recording.DurationMs);
            document.TrimInMs = 400;
            document.TrimOutMs = 2600;
            document.TextOverlays.Add(new TimedTextOverlay
            {
                Id = Guid.Parse("6bafc301-e58e-44bc-88e2-aac368280701"),
                Text = _english ? "Reproduce the error" : "오류 재현",
                StartMs = 400,
                EndMs = 1500,
            });
            document.TextOverlays.Add(new TimedTextOverlay
            {
                Id = Guid.Parse("6bafc301-e58e-44bc-88e2-aac368280702"),
                Text = _english ? "Verify the fix" : "수정 후 확인",
                StartMs = 1600,
                EndMs = 2600,
            });
            return document;
        }

        private void ExportVideoEditor(RecordingResult recording, AppPaths paths, ILoggerFactory logs)
        {
            ThemeService.Apply(AppTheme.Glass);
            var document = DeveloperVideoDocument(recording);
            var window = new VideoEditorWindow(recording, paths, logs, document);
            try
            {
                OpenLayerWindow(window, 1240, 800, 200);
                WaitForLayerVideo(
                    () => window.HasMediaFailedForTest
                        || (window.IsMediaReadyForTest && window.IsSourceRangeKnownForTest),
                    "The synthetic terminal clip did not become ready in the video editor.", 15000);
                if (window.HasMediaFailedForTest)
                    throw new InvalidOperationException("The video editor could not open the synthetic clip: " + window.MediaFailureForTest);

                // This is the editor's own expanded detail state: source trim handles and the two
                // timed-text tracks remain part of its real layout rather than a painted overlay.
                window.SetDetailsExpandedForTest(true);
                window.UpdateLayout();
                ListBox layers = UxReviewSelfTest.Descendants(window).OfType<ListBox>()
                    .Single(list => list.Items.OfType<ListBoxItem>().Any(item => item.Tag is TimedTextOverlay));
                if (layers.Items.Count != 2)
                    throw new InvalidOperationException("The video editor must display both timed-text layers.");
                layers.SelectedIndex = 0;

                // Selecting the actual layer pauses and seeks to its start through the product path.
                PreviewSeekCoordinator coordinator = window.PreviewSeekCoordinatorForTest;
                WaitForLayerVideo(
                    () => coordinator.LastError is not null
                        || (coordinator.PresentedGeneration > 0
                            && !coordinator.IsInFlightForTest && !coordinator.HasPendingForTest
                            && Math.Abs(coordinator.PresentedPositionMs - 400) <= 1000.0 / recording.Fps),
                    "The video editor did not present the selected text layer's frame.", 15000);
                if (coordinator.LastError is { } error)
                    throw new InvalidOperationException("The video editor's real preview seek failed.", error);
                Pump(250);
                if (Math.Abs(window.TimelineForTest.InMs - 400) > 1
                    || Math.Abs(window.TimelineForTest.OutMs - 2600) > 1)
                    throw new InvalidOperationException("The video editor changed the required 0.4-2.6 s trim.");
                if (window.RenderCurrentFrameForTest() is null)
                    throw new InvalidOperationException("The video editor has no rendered preview frame.");

                Emit("video/editor.png", RenderLayerWindow(window), 1240, 800);
                _report.AppendLine($"Video editor: 1240x800 DIP at 3x; trim {window.TimelineForTest.InMs:0}-{window.TimelineForTest.OutMs:0} ms; kept {document.OutputDurationMs:0} ms; two timed text tracks; media ready; preview at {coordinator.PresentedPositionMs:0.###} ms.");
            }
            finally
            {
                CloseLayerWindow(window);
            }
        }

        private void ExportGifDialog(RecordingResult recording, ILoggerFactory logs)
        {
            ThemeService.Apply(AppTheme.Glass);
            var dialog = new VideoExportDialog(recording, DeveloperVideoDocument(recording), logs, gif: true)
            {
                // The product constructor caps its normal window at the current monitor. The Store
                // contract needs an offscreen 1021-DIP client regardless of that monitor's height.
                MaxHeight = double.PositiveInfinity,
            };
            try
            {
                // Before Show(), the ScrollViewer has no visual descendants. Read the existing
                // controls directly so their real selection events settle before sample generation.
                ComboBox quality = ReadLayerVideoField<ComboBox>(dialog, "_quality")
                    ?? throw new InvalidOperationException("The GIF quality control is missing.");
                ComboBox speed = ReadLayerVideoField<ComboBox>(dialog, "_speed")
                    ?? throw new InvalidOperationException("The GIF speed control is missing.");
                ComboBox target = ReadLayerVideoField<ComboBox>(dialog, "_shareTarget")
                    ?? throw new InvalidOperationException("The GIF target control is missing.");
                quality.SelectedIndex = GifSharePolicy.Presets.ToList().FindIndex(preset => preset.LongEdge == 960 && preset.FramesPerSecond == 10);
                speed.SelectedIndex = GifSharePolicy.Speeds.ToList().IndexOf(1d);
                target.SelectedIndex = GifSharePolicy.Targets.ToList().FindIndex(item => item.LabelKey == "GifPreview_Target10" && item.Bytes == 10_000_000);
                if (quality.SelectedIndex < 0 || speed.SelectedIndex < 0 || target.SelectedIndex < 0)
                    throw new InvalidOperationException("The Store GIF settings are absent from the product presets.");

                OpenLayerWindow(dialog, 624, 1021, 200);
                GifPreviewPanel panel = UxReviewSelfTest.Descendants(dialog).OfType<GifPreviewPanel>().Single();
                // The product owns the sample render and computes its displayed estimate. Do not
                // substitute a constant estimate, an independently encoded sample or a sample image.
                WaitForLayerVideo(
                    () => ReadLayerVideoField<Task>(dialog, "_sampleTask") is { IsCompleted: true },
                    "The product GIF preview calculation did not finish.", 45000);
                Task sampleTask = ReadLayerVideoField<Task>(dialog, "_sampleTask")
                    ?? throw new InvalidOperationException("The GIF preview task is missing.");
                sampleTask.GetAwaiter().GetResult();
                GifPreviewSample sample = ReadLayerVideoField<GifPreviewSample>(dialog, "_sample")
                    ?? throw new InvalidOperationException("The product could not render its GIF preview: " + panel.MessageText);
                GifRecommendation recommendation = ReadLayerVideoField<GifRecommendation>(dialog, "_recommendation")
                    ?? throw new InvalidOperationException("The GIF dialog did not calculate a recommendation.");
                TextBlock estimate = ReadLayerVideoField<TextBlock>(dialog, "_estimate")
                    ?? throw new InvalidOperationException("The GIF size label is missing.");
                TextBlock advice = ReadLayerVideoField<TextBlock>(dialog, "_advice")
                    ?? throw new InvalidOperationException("The GIF recommendation label is missing.");
                if (!panel.HasSample || panel.FrameCount == 0 || sample.Bytes <= 0
                    || !sample.Window.CoversWholeRange || !recommendation.Estimate.IsExact
                || recommendation.Estimate.ExpectedBytes != sample.Bytes
                || Math.Abs(sample.Window.StartMs - 400) > 1 || Math.Abs(sample.Window.EndMs - 2600) > 1
                    || sample.Quality.LongEdge != 960 || sample.Quality.FramesPerSecond != 10
                    || sample.Speed != 1 || recommendation.Kind != GifRecommendationKind.CurrentFits
                    || recommendation.Quality != GifExportQuality.Standard)
                    throw new InvalidOperationException("The GIF dialog did not reach the requested measured 960px/10fps recommended state.");
                if (string.IsNullOrWhiteSpace(estimate.Text) || string.IsNullOrWhiteSpace(advice.Text))
                    throw new InvalidOperationException("The GIF dialog has no computed size or recommendation text.");

                panel.Stop();
                foreach (ScrollViewer scroll in UxReviewSelfTest.Descendants(dialog).OfType<ScrollViewer>())
                    scroll.ScrollToTop();
                Pump(250);
                Emit("video/gif-export-dialog.png", RenderLayerWindow(dialog), 624, 1021);
                _report.AppendLine($"GIF dialog: 624x1021 DIP at 3x; 960px/10fps; 1.0x; GitHub 10000000-byte target; full kept range {sample.Window.StartMs:0}-{sample.Window.EndMs:0} ms; measured {sample.Bytes} bytes.");
                _report.AppendLine("GIF product size text: " + estimate.Text);
                _report.AppendLine("GIF product recommendation: " + advice.Text);
            }
            finally
            {
                CloseLayerWindow(dialog);
            }
        }

        private void WaitForLayerVideo(Func<bool> finished, string failure, int timeoutMs)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (!finished())
            {
                if (elapsed.ElapsedMilliseconds >= timeoutMs)
                    throw new TimeoutException(failure);
                Pump(40);
            }
        }

        // Read-only diagnostics for the existing export calculation. Fail explicitly if the
        // product's private state changes; do not guess successful estimates from visible numbers.
        private static T? ReadLayerVideoField<T>(object instance, string name) where T : class
        {
            System.Reflection.FieldInfo field = instance.GetType().GetField(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new MissingFieldException(instance.GetType().FullName, name);
            object? value = field.GetValue(instance);
            return value is null ? null : value as T
                ?? throw new InvalidOperationException($"Unexpected type for video diagnostic field {name}.");
        }


        private BitmapSource CodeSample(double scale = 1) => DrawSample(1440, 900, scale, dc =>
        {
            SampleText(dc, "CaptureService.cs", 72, 30, 28, UiBold);
            string[] code =
            [
                "using System;",
                "using System.Threading.Tasks;",
                "",
                "namespace MyCapture.Services;",
                "",
                "public sealed class CaptureService",
                "{",
                "    private readonly CaptureLibrary library;",
                "",
                "    public CaptureService(CaptureLibrary library)",
                "        => this.library = library;",
                "",
                "    public async Task<Capture> SaveAsync(Capture capture)",
                "    {",
                "        ArgumentNullException.ThrowIfNull(capture);",
                "        await library.AddAsync(capture);",
                "        return capture;",
                "    }",
                "}",
            ];
            for (int i = 0; i < code.Length; i++)
            {
                SampleText(dc, (i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(2), 72, 112 + i * 39, 26, Mono, true);
                SampleText(dc, code[i], 150, 112 + i * 39, 30, Mono);
            }
        });

        private BitmapSource ErrorSample(List<OcrLine> lines) => DrawSample(1440, 900, 1, dc =>
        {
            Brush ink = Rgb(31, 35, 40);
            Brush muted = Rgb(89, 99, 110);
            dc.DrawText(Text(_english ? "Log upload failure #482" : "로그 업로드 오류 #482", UiBold, 40, ink), new Point(72, 30));
            dc.DrawText(Text(_english ? "The upload failed after retry." : "재시도 후 로그 업로드에 실패했습니다.", Ui, 29, ink), new Point(72, 132));
            string[] labels = _english ? ["Reporter", "Upload token", "Test card"] : ["작성자 이메일", "업로드 토큰", "테스트 카드"];
            string[] values = ["user@example.com", "ghp_" + new string('A', 36), "4111 1111 1111 1111"];
            for (int i = 0; i < values.Length; i++)
            {
                double y = 240 + i * 80;
                dc.DrawText(Text(labels[i], Ui, 25, muted), new Point(72, y));
                AddSampleLine(dc, values[i], 340, y, 27, lines, ink);
                dc.DrawLine(new Pen(Rgb(216, 222, 228), 1), new Point(72, y + 57), new Point(1368, y + 57));
            }
            dc.DrawRectangle(Rgb(246, 248, 250), new Pen(Rgb(216, 222, 228), 1), new Rect(72, 520, 1296, 212));
            string[] log =
            [
                "15:42:08.103  POST /api/logs   HTTP 500",
                "15:42:08.106  request_id=req_demo_0482",
                "15:42:09.108  retry=1   upload failed",
            ];
            for (int i = 0; i < log.Length; i++)
                dc.DrawText(Text(log[i], Mono, 26, ink), new Point(100, 552 + i * 52));
        }, light: true);

        private BitmapSource TerminalSample(bool passed, List<OcrLine>? lines = null) => DrawSample(1440, 900, 1, dc =>
        {
            SampleText(dc, _english ? "Terminal" : "터미널", 72, 40, 26, UiBold);
            string[] content = ["dotnet test", "CaptureService.Tests", passed ? "Passed: 12    Failed: 0" : "Passed: 11    Failed: 1",
                passed ? "SaveAsync_ReturnsSuccess [PASS]" : "SaveAsync_ReturnsSuccess [FAIL]", "Expected: 200", passed ? "Actual: 200" : "Actual: 500",
                passed ? "Fix verified" : "Request timeout"];
            var measured = lines ?? [];
            for (int i = 0; i < content.Length; i++) AddSampleLine(dc, content[i], 100, 130 + i * 67, 31, measured);
        });

        private static void AddSampleLine(DrawingContext dc, string value, double x, double y, double size, List<OcrLine> lines, Brush? ink = null)
        {
            FormattedText text = Text(value, Mono, size, ink ?? Rgb(226, 230, 237));
            dc.DrawText(text, new Point(x, y));
            var bounds = new OcrRect(x, y, Math.Ceiling(text.WidthIncludingTrailingWhitespace), Math.Ceiling(text.Height));
            lines.Add(new OcrLine(value, bounds, [new OcrWord(value, bounds)]));
        }

        private static void SampleText(DrawingContext dc, string value, double x, double y, double size,
            Typeface? face = null, bool muted = false) =>
            dc.DrawText(Text(value, face ?? Ui, size, muted ? Rgb(151, 162, 180) : Rgb(226, 230, 237)), new Point(x, y));

        private static BitmapSource DrawSample(int width, int height, double scale, Action<DrawingContext> draw, bool light = false)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(light ? Brushes.White : Rgb(25, 29, 36), null, new Rect(0, 0, width, height));
                dc.DrawRectangle(light ? Rgb(246, 248, 250) : Rgb(34, 39, 48), null, new Rect(0, 0, width, 90));
                draw(dc);
            }
            var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
