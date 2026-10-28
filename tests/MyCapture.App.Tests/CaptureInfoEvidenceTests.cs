using System.IO;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Ocr;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in, app-only 96-dpi renders. MYCAPTURE_CAPINFO_EVIDENCE must be an absolute output folder;
/// MYCAPTURE_CAPINFO_EVIDENCE_PHASE is a filename tag (default: after). Nothing is created when unset.
/// New capinfo APIs are reflected so this one file also compiles at 353e2415. The old card menu
/// uses GalleryWindow.TileMenu/OpenTileMenu directly; unavailable new surfaces are reported as skipped.
/// </summary>
public sealed class CaptureInfoEvidenceTests
{
    private const string OutputVariable = "MYCAPTURE_CAPINFO_EVIDENCE";
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [Fact]
    public Task LibraryCommandsCardMenuAndLoadedTooltip() => Run(nameof(LibraryCommandsCardMenuAndLoadedTooltip), RenderLibrary);

    [Fact]
    public Task ImageEditorBottomAndCaptureMenu() => Run(nameof(ImageEditorBottomAndCaptureMenu), RenderImageEditor);

    [Fact]
    public Task VideoEditorCommandBarAndCaptureMenu() => Run(nameof(VideoEditorCommandBarAndCaptureMenu), RenderVideoEditor);

    [Fact]
    public Task ImageVideoAndSelectionDetailsInBothLanguages() => Run(nameof(ImageVideoAndSelectionDetailsInBothLanguages), RenderDetails);

    private static Task Run(string name, Action<string, string> render)
    {
        string? requested = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(requested)) return Task.CompletedTask;
        if (!Path.IsPathFullyQualified(requested)) throw new IOException("Evidence requires an absolute output directory.");
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_CAPINFO_EVIDENCE_PHASE") is { Length: > 0 } value ? value : "after";
        string output = DiagnosticOutputPaths.Create(requested);
        _ = DiagnosticOutputPaths.Child(output, $"capinfo-{phase}.png");
        return SettingsThemePreviewTests.RunIsolated(name, () => render(output, phase), typeof(CaptureInfoEvidenceTests));
    }

    private static Application CreateApplication()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
        application.Resources["PinLabel"] = new PinLabelConverter();
        application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        UiText.Configure("ko-KR");
        ThemeService.Apply(AppTheme.Daylight);
        return application;
    }

    private static void RenderLibrary(string output, string phase)
    {
        Application application = CreateApplication();
        string root = OwnedTestDirectory.Create("mc-capinfo-library-");
        GalleryWindow? window = null;
        OcrResultPresenter? presenter = null;
        try
        {
            using var language = UiText.UseLanguage("ko-KR");
            var paths = AppPaths.CreateForRoot(root);
            paths.EnsureCreated();
            var settings = new AppSettings();
            var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            BitmapSource image = SampleImage();
            CaptureRecord first = AddImage(queue, image, "화면 검토", new DateTimeOffset(DateTime.Today).AddHours(12));
            AddImage(queue, image, "편집 예시", first.CreatedAt.AddMinutes(-10));
            CaptureRecord video = AddImage(queue, image, "녹화 예시", first.CreatedAt.AddMinutes(-20));
            video.MediaKind = CaptureMediaKind.Video;
            video.DurationMs = 61567;
            // Only metadata is needed here; a genuine synthetic MP4 is created for the video scene.
            File.WriteAllBytes(queue.GetFilePath(video, CaptureFileNames.VideoRendered), VideoHeader(61567));
            var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance);
            var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue, NullLogger<CapturePersistenceService>.Instance);
            var commit = new CaptureCommitService(persistence, () => settings, () => paths,
                NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
            var ocr = new EvidenceOcr();
            presenter = new OcrResultPresenter(ocr, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
            var indexing = new OcrIndexingService(controller, ocr, queue.GetDirectory, () => settings.Ocr,
                NullLogger<OcrIndexingService>.Instance, Dispatcher.CurrentDispatcher);
            var vm = new GalleryViewModel(controller, record => queue.GetFilePath(record, CaptureFileNames.Thumbnail), 96,
                clock: () => first.CreatedAt);
            window = new GalleryWindow(vm, controller, new GalleryReeditLoader(queue, NullLogger<GalleryReeditLoader>.Instance),
                commit, queue, new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance), paths,
                NullLoggerFactory.Instance, presenter, () => settings.Ocr, indexing, new EvidencePrivacy(), NullLogger.Instance);
            Place(window, 1264, 753);
            window.Show();
            SetDpi(window);
            PumpUntil(() => Descendants(window).OfType<ListBoxItem>().Any(card => card.DataContext is GalleryItemViewModel tile && tile.Id == first.Id));
            Settle(window);
            BitmapSource bar = Top(Snapshot(window), 150);
            Save(output, $"library-command-bar-ko-KR-96dpi-{phase}.png", bar);
            FrameworkElement anchor = (FrameworkElement)window.FindName("CaptureRegionButton");
            RenderCaptureMenu(output, $"library-capture-menu-ko-KR-96dpi-{phase}.png", anchor, bar);

            ListBoxItem firstCard = Card(window, first.Id);
            window.OpenTileMenu(firstCard, PlacementMode.Bottom);
            ContextMenu menu = window.TileMenu;
            try
            {
                Settle(menu);
                Assert.NotEmpty(menu.Items);
                Save(output, $"card-context-menu-ko-KR-96dpi-{phase}.png", Compose(Snapshot(firstCard), Snapshot(menu)));
            }
            finally { menu.IsOpen = false; }

            RenderTooltip(output, phase, window, Card(window, video.Id));
        }
        finally
        {
            window?.CloseForExit();
            presenter?.Dispose();
            application.Shutdown();
            FluidMotion.AnimationsEnabledOverrideForTest = null;
            OwnedTestDirectory.Delete(root);
        }
    }

    private static void RenderTooltip(string output, string phase, GalleryWindow owner, ListBoxItem card)
    {
        MethodInfo? opening = typeof(GalleryWindow).GetMethod("OnMediaDetailsToolTipOpening", Members);
        if (opening is null) { Skipped("tooltip-details", "the metadata tooltip API is absent"); return; }
        card.BringIntoView();
        Settle(owner);
        Assert.True(card.IsVisible && card.Opacity > 0);
        PumpUntil(() => Descendants(card).OfType<Image>().Any(image => image.Source is not null));
        Assert.Contains(Descendants(card).OfType<TextBlock>(), text => text.IsVisible && text.Text == "녹화 예시");
        TextBlock target = Descendants(card).OfType<TextBlock>().Single(text => text.Name == "TileTime");
        // The actual async-void handler reads the synthetic card's files; it does not need its event args.
        opening.Invoke(owner, [target, null]);
        PumpUntil(() => target.ToolTip is ToolTip tip && tip.Content is string text && text.Contains("MP4", StringComparison.Ordinal));
        var tooltip = (ToolTip)target.ToolTip;
        string facts = Assert.IsType<string>(tooltip.Content);
        Assert.Contains("×", facts);
        Assert.Contains("1:01.5", facts);
        Assert.Contains(UiText.Get("MediaInfo.FileSize"), facts);
        tooltip.PlacementTarget = target;
        tooltip.Placement = PlacementMode.Bottom;
        tooltip.IsOpen = true;
        try
        {
            Settle(tooltip);
            // The gallery's scrolling/virtualizing ancestor can clip a detached VisualBrush.
            // Crop the real window render at the card's current bounds instead.
            Settle(owner);
            Rect bounds = card.TransformToAncestor(owner).TransformBounds(new Rect(card.RenderSize));
            BitmapSource window = Snapshot(owner);
            Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= window.PixelWidth && bounds.Bottom <= window.PixelHeight,
                $"Tooltip card must be inside the rendered owner: {bounds}");
            var ownerCard = new CroppedBitmap(window, new Int32Rect((int)Math.Floor(bounds.X), (int)Math.Floor(bounds.Y),
                (int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height)));
            AssertSyntheticPreviewVisible(ownerCard);
            Save(output, $"tooltip-details-ko-KR-96dpi-{phase}.png", Compose(ownerCard, Snapshot(tooltip)));
        }
        finally { tooltip.IsOpen = false; }
    }

    private static void RenderImageEditor(string output, string phase)
    {
        Application application = CreateApplication();
        string root = OwnedTestDirectory.Create("mc-capinfo-image-");
        try
        {
            using var language = UiText.UseLanguage("ko-KR");
            BitmapSource image = SampleImage();
            string source = Path.Combine(root, "sample.png");
            File.WriteAllBytes(source, ImageCodec.EncodePng(image));
            foreach ((int width, int height) in new[] { (1180, 640), (860, 600) })
            {
                var region = new RectD(0, 0, image.PixelWidth, image.PixelHeight);
                var editor = new AnnotationEditorWindow(new FrozenFrame(image, region, null, 0), region, image);
                Place(editor, width, height);
                typeof(AnnotationEditorControl).GetProperty("SourceFilePath", Members)?.SetValue(editor.Editor, source);
                try
                {
                    editor.Show();
                    SetDpi(editor);
                    Settle(editor);
                    BitmapSource bottom = Bottom(Snapshot(editor), 120);
                    Save(output, $"image-editor-bottom-{width}-ko-KR-96dpi-{phase}.png", bottom);
                    if (width == 1180)
                    {
                        Button? anchor = Descendants(editor).OfType<Button>().FirstOrDefault(button => button.Name == "NewCapture");
                        if (anchor is null) Skipped("image-editor-capture-menu", "the new capture button is absent");
                        else RenderCaptureMenu(output, $"image-editor-capture-menu-ko-KR-96dpi-{phase}.png", anchor, bottom);
                    }
                }
                finally { editor.Close(); }
            }
        }
        finally
        {
            application.Shutdown();
            FluidMotion.AnimationsEnabledOverrideForTest = null;
            OwnedTestDirectory.Delete(root);
        }
    }

    private static void RenderVideoEditor(string output, string phase)
    {
        Application application = CreateApplication();
        string root = OwnedTestDirectory.Create("mc-capinfo-video-");
        VideoEditorWindow? editor = null;
        try
        {
            using var language = UiText.UseLanguage("ko-KR");
            RecordingResult recording = UxReviewSelfTest.CreateFixtureVideo(Path.Combine(root, "sample.mp4"), SampleImage(640, 360));
            editor = new VideoEditorWindow(recording, AppPaths.CreateForRoot(root), NullLoggerFactory.Instance);
            Place(editor, 920, 680);
            editor.Show();
            SetDpi(editor);
            PumpUntil(() => editor.IsMediaReadyForTest || editor.HasMediaFailedForTest, TimeSpan.FromSeconds(20));
            Assert.True(editor.IsMediaReadyForTest, editor.MediaFailureForTest);
            Settle(editor);
            BitmapSource bar = Top(Snapshot(editor), 120);
            Save(output, $"video-editor-command-bar-920x680-ko-KR-96dpi-{phase}.png", bar);
            Button? anchor = Descendants(editor).OfType<Button>().FirstOrDefault(button => button.Name == "NewCapture");
            if (anchor is null) Skipped("video-editor-capture-menu", "the new capture button is absent");
            else RenderCaptureMenu(output, $"video-editor-capture-menu-ko-KR-96dpi-{phase}.png", anchor, bar);
        }
        finally
        {
            editor?.Close();
            application.Shutdown();
            FluidMotion.AnimationsEnabledOverrideForTest = null;
            OwnedTestDirectory.Delete(root);
        }
    }

    private static void RenderDetails(string output, string phase)
    {
        Type? sourceType = typeof(GalleryWindow).Assembly.GetType("MyCapture.App.Media.MediaDetailsSource");
        Type? readerType = typeof(GalleryWindow).Assembly.GetType("MyCapture.App.Media.MediaDetailsReader");
        Type? detailsType = typeof(GalleryWindow).Assembly.GetType("MyCapture.App.Media.MediaDetails");
        Type? windowType = typeof(GalleryWindow).Assembly.GetType("MyCapture.App.Media.MediaDetailsWindow");
        if (sourceType is null || readerType is null || detailsType is null || windowType is null)
        { Skipped("details-image/details-video/details-multi", "the media information window API is absent"); return; }
        Application application = CreateApplication();
        string root = OwnedTestDirectory.Create("mc-capinfo-details-");
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            string imagePath = Path.Combine(root, "sample-image.png"), videoPath = Path.Combine(root, "sample-video.mp4"),
                damagedPath = Path.Combine(root, "unreadable-video.mp4"), missingPath = Path.Combine(root, "missing-file.png");
            File.WriteAllBytes(imagePath, ImageCodec.EncodePng(SampleImage()));
            File.WriteAllBytes(videoPath, VideoHeader(61567));
            File.WriteAllBytes(damagedPath, [0, 0, 0]);
            object Read(string path, int width, int height, double? duration, int canvasWidth = 0, int canvasHeight = 0)
            {
                object source = Activator.CreateInstance(sourceType, Members, null,
                    [path, width, height, duration, canvasWidth, canvasHeight, null, false], null)!;
                MethodInfo method = readerType.GetMethod("Read", Members)!;
                return method.Invoke(null, [source, CancellationToken.None, true])!;
            }
            object image = Read(imagePath, 720, 300, null, 640, 280);
            object video = Read(videoPath, 1920, 1080, null);
            object damaged = Read(damagedPath, 1920, 1080, null);
            object missing = Read(missingPath, 0, 0, null);
            foreach (string language in new[] { "ko-KR", "en-US" })
            {
                using var culture = UiText.UseLanguage(language);
                UiText.Configure(language);
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                Show("details-image", [image]);
                Show("details-video", [video]);
                Show("details-video-unreadable", [damaged]);
                Show("details-multi", [image, video, missing]);
                void Show(string name, object[] values)
                {
                    Array details = Array.CreateInstance(detailsType, values.Length);
                    for (int index = 0; index < values.Length; index++) details.SetValue(values[index], index);
                    var dialog = (Window)Activator.CreateInstance(windowType, Members, null, [details], null)!;
                    Place(dialog);
                    try
                    {
                        dialog.Show();
                        SetDpi(dialog);
                        Settle(dialog);
                        Save(output, $"{name}-{language}-96dpi-{phase}.png", Snapshot(dialog));
                    }
                    finally { dialog.Close(); }
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            application.Shutdown();
            FluidMotion.AnimationsEnabledOverrideForTest = null;
            OwnedTestDirectory.Delete(root);
        }
    }

    private static void RenderCaptureMenu(string output, string filename, FrameworkElement anchor, BitmapSource owner)
    {
        Type? type = typeof(GalleryWindow).Assembly.GetType("MyCapture.App.Capture.CaptureCommandMenu");
        if (type is null) { Skipped(filename, "the new capture menu API is absent"); return; }
        // Assigned fixture shortcuts, not the user's settings; no menu command is invoked.
        var settings = new HotkeySettings
        {
            CaptureFullScreen = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x46),
            CaptureWindow = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x57),
        };
        Func<HotkeySettings> read = () => settings;
        type.GetMethod("Open", Members)!.Invoke(null, [anchor, read, null]);
        ContextMenu menu = Assert.IsType<ContextMenu>(anchor.ContextMenu);
        try
        {
            Settle(menu);
            MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(4, items.Length);
            Assert.All(items, item => Assert.False(string.IsNullOrWhiteSpace(item.InputGestureText)));
            Save(output, filename, Compose(owner, Snapshot(menu)));
        }
        finally { menu.IsOpen = false; }
    }

    private static CaptureRecord AddImage(CaptureQueue queue, BitmapSource image, string title, DateTimeOffset time)
    {
        var record = new CaptureRecord
        {
            Width = image.PixelWidth, Height = image.PixelHeight, Title = title, CreatedAt = time, UpdatedAt = time,
            OcrText = "Synthetic evidence", OcrContentRevision = 0,
        };
        queue.Add(record);
        string directory = queue.GetDirectory(record);
        Directory.CreateDirectory(directory);
        byte[] png = ImageCodec.EncodePng(image);
        foreach (string name in new[] { CaptureFileNames.Thumbnail, CaptureFileNames.Rendered, CaptureFileNames.Original })
            File.WriteAllBytes(Path.Combine(directory, name), png);
        return record;
    }

    private static ListBoxItem Card(Window window, Guid id) => Descendants(window).OfType<ListBoxItem>()
        .Single(card => card.DataContext is GalleryItemViewModel tile && tile.Id == id);

    private static void Place(Window window, double? width = null, double? height = null)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000; window.Top = -10000;
        window.ShowActivated = false; window.ShowInTaskbar = false;
        if (width is double w) window.Width = w;
        if (height is double h) window.Height = h;
        FluidMotion.SetWindowEntrance(window, false);
    }

    private static void SetDpi(Window window)
    {
        VisualTreeHelper.SetRootDpi(window, new DpiScale(1, 1));
        foreach (UIElement child in Descendants(window).Prepend(window).OfType<UIElement>())
        { child.InvalidateMeasure(); child.InvalidateArrange(); }
    }

    private static void Settle(FrameworkElement element)
    {
        element.ApplyTemplate();
        element.UpdateLayout();
        element.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(element.UpdateLayout));
    }

    private static void PumpUntil(Func<bool> ready, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!ready() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(25), DispatcherPriority.Background,
                (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
        Assert.True(ready(), "The synthetic evidence scene did not finish loading.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject nested in Descendants(child)) yield return nested;
        }
    }

    private static BitmapSource Snapshot(FrameworkElement element)
    {
        Settle(element);
        int width = (int)Math.Ceiling(element.ActualWidth), height = (int)Math.Ceiling(element.ActualHeight);
        Assert.True(width > 0 && height > 0, $"Empty render: {element.GetType().Name}");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
        {
            // Match the existing evidence renderer: native material/transparency receives the
            // actual theme's opaque base. Keep Window bounds, including its padding, intact.
            context.DrawRectangle((Brush)element.FindResource("Surface.Base"), null, new Rect(0, 0, width, height));
            if (element is not Window)
            {
                // A card/menu lives at an offset in another visual tree. A local-origin brush
                // preserves its real content without carrying the parent's offset into this PNG.
                context.DrawRectangle(new VisualBrush(element)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, 0, element.ActualWidth, element.ActualHeight),
                    Stretch = Stretch.Fill,
                }, null, new Rect(0, 0, width, height));
            }
        }
        bitmap.Render(drawing);
        if (element is Window) bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource Top(BitmapSource bitmap, int height) =>
        new CroppedBitmap(bitmap, new Int32Rect(0, 0, bitmap.PixelWidth, Math.Min(height, bitmap.PixelHeight)));

    private static BitmapSource Bottom(BitmapSource bitmap, int height) =>
        new CroppedBitmap(bitmap, new Int32Rect(0, Math.Max(0, bitmap.PixelHeight - height), bitmap.PixelWidth, Math.Min(height, bitmap.PixelHeight)));

    private static void AssertSyntheticPreviewVisible(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        int samplePixels = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
            if (pixels[offset] > pixels[offset + 2] + 35 && pixels[offset] > pixels[offset + 1] + 15) samplePixels++;
        Assert.True(samplePixels > 100, "The tooltip owner render must contain the synthetic blue preview, not a blank background.");
    }

    private static BitmapSource Compose(BitmapSource owner, BitmapSource popup)
    {
        const int gap = 16;
        int width = owner.PixelWidth + gap + popup.PixelWidth, height = Math.Max(owner.PixelHeight, popup.PixelHeight);
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
        {
            context.DrawRectangle((Brush)Application.Current.FindResource("Surface.Base"), null, new Rect(0, 0, width, height));
            context.DrawImage(owner, new Rect(0, 0, owner.PixelWidth, owner.PixelHeight));
            context.DrawImage(popup, new Rect(owner.PixelWidth + gap, 0, popup.PixelWidth, popup.PixelHeight));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Save(string output, string name, BitmapSource bitmap)
    {
        Assert.Equal(96, bitmap.DpiX, precision: 2);
        Assert.Equal(96, bitmap.DpiY, precision: 2);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(output, name));
        encoder.Save(stream);
        Console.WriteLine($"CAPINFO PNG {name} ({bitmap.PixelWidth} x {bitmap.PixelHeight}, 96 dpi)");
    }

    private static void Skipped(string scene, string reason) => Console.WriteLine($"CAPINFO SKIP {scene}: {reason}");

    private static BitmapSource SampleImage(int width = 720, int height = 300)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset] = (byte)(180 + 60 * x / width);
                pixels[offset + 1] = (byte)(90 + 100 * y / height);
                pixels[offset + 2] = (byte)(50 + 80 * x / width);
                pixels[offset + 3] = 255;
            }
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static byte[] VideoHeader(uint duration)
    {
        byte[] movie = new byte[20], track = new byte[24], handler = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(movie.AsSpan(12), 1000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(track.AsSpan(20), duration);
        System.Text.Encoding.ASCII.GetBytes("vide").CopyTo(handler, 8);
        return Box("moov", [.. Box("mvhd", movie), .. Box("trak", [.. Box("tkhd", track), .. Box("mdia", Box("hdlr", handler))])]);
        static byte[] Box(string type, byte[] payload)
        {
            byte[] bytes = new byte[payload.Length + 8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
            payload.CopyTo(bytes, 8);
            return bytes;
        }
    }

    private sealed class EvidenceOcr : IOcrService
    {
        public bool IsAvailable => false;
        public IReadOnlyList<string> SupportedLanguages => [];
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Evidence rendering must not run OCR.");
    }

    private sealed class EvidencePrivacy : IPrivacyRedactionService
    {
        public bool IsAvailable => false;
        public Task<PrivacyRedactionResult> FindAsync(BitmapSource bitmap, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Evidence rendering must not run privacy analysis.");
    }
}
