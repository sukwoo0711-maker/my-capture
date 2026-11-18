using System.IO;
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
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Core.Themes;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    /// <summary>
    /// Library cards with a web, document and app-only source in the dark and light themes, Korean and
    /// English: each card shows its source badge, the badge menu leads with Open source and the card
    /// menu offers it. With MYCAPTURE_SOURCE_OPEN_EVIDENCE set to a folder it also keeps PNGs of the
    /// cards, the badge menu, the card menu and the details panel's source section.
    /// </summary>
    [Fact]
    public Task SourceOpenEvidence_CardsBadgeMenuCardMenuAndDetailsShowEachSource() =>
        RunIsolated(nameof(SourceOpenEvidence_CardsBadgeMenuCardMenuAndDetailsShowEachSource), () =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            application.Resources["PinLabel"] = new PinLabelConverter();
            application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
            try
            {
                foreach (AppTheme theme in new[] { AppTheme.Midnight, AppTheme.Daylight })
                foreach (string language in new[] { "ko-KR", "en-US" })
                {
                    using var scope = UiText.UseLanguage(language);
                    VerifySourceOpenEvidence(theme, language);
                }
            }
            finally
            {
                ThemeService.Apply(AppTheme.Midnight);
                application.Shutdown();
            }
        });

    private static void VerifySourceOpenEvidence(AppTheme theme, string language)
    {
        ThemeService.Apply(theme);
        string root = TestRecycleBin.CreateTempSubdirectory("mc-source-open-").FullName;
        var paths = AppPaths.CreateForRoot(root);
        paths.EnsureCreated();
        var settings = new AppSettings();
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        BitmapSource sample = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 3 * 4], 4 * 4);
        byte[] png = ImageCodec.EncodePng(sample);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        // Real executables of this PC give the badge an icon to show; the document is a fixture name.
        var sources = new (string Kind, string Title, string Url, CaptureSourceDetails Source)[]
        {
            ("web", "Source open sample — Microsoft Edge", "https://example.com/docs/source-open",
                new("Microsoft Edge", Path.Combine(programs, @"Microsoft\Edge\Application\msedge.exe"))),
            ("document", "report.txt - 메모장", "",
                new("메모장", Path.Combine(windows, @"System32\notepad.exe"), @"C:\Work\report.txt")),
            ("app", "Untitled - Notepad", "", new("메모장", Path.Combine(windows, @"System32\notepad.exe"))),
        };
        var ids = new Dictionary<string, Guid>();
        DateTimeOffset now = DateTimeOffset.Now;
        for (int index = 0; index < sources.Length; index++)
        {
            var record = new CaptureRecord
            {
                Width = sample.PixelWidth, Height = sample.PixelHeight, CreatedAt = now.AddMinutes(-index),
                SourceWindowTitle = sources[index].Title, SourcePageUrl = sources[index].Url,
                OcrText = "Source fixture", OcrContentRevision = 0,
            };
            record.SetSourceDetails(sources[index].Source);
            queue.Add(record);
            string directory = queue.GetDirectory(record);
            Directory.CreateDirectory(directory);
            foreach (string file in new[] { CaptureFileNames.Thumbnail, CaptureFileNames.Rendered, CaptureFileNames.Original })
                File.WriteAllBytes(Path.Combine(directory, file), png);
            ids[sources[index].Kind] = record.Id;
        }

        var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance);
        var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue,
            NullLogger<CapturePersistenceService>.Instance);
        var commit = new CaptureCommitService(persistence, () => settings, () => paths,
            NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
        var ocr = new GalleryCompactOcr();
        using var presenter = new OcrResultPresenter(ocr, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
        var indexing = new OcrIndexingService(controller, ocr, queue.GetDirectory, () => settings.Ocr,
            NullLogger<OcrIndexingService>.Instance, Dispatcher.CurrentDispatcher);
        var vm = new GalleryViewModel(controller, record => queue.GetFilePath(record, CaptureFileNames.Thumbnail), 96);
        var window = new GalleryWindow(vm, controller,
            new GalleryReeditLoader(queue, NullLogger<GalleryReeditLoader>.Instance), commit, queue,
            new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance), paths,
            NullLoggerFactory.Instance, presenter, () => settings.Ocr, indexing,
            new GalleryMarkupPrivacy(), NullLogger.Instance)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, Width = 1100, Height = 760,
        };
        string label = $"{(theme == AppTheme.Midnight ? "dark" : "light")}-{language}";
        try
        {
            FluidMotion.SetWindowEntrance(window, false);
            window.Show();
            window.SetDetailsPaneOpen(true);
            SourceOpenSettle(window);
            // Icons, when the build has them, are prepared off the UI thread; give them time to arrive.
            for (int pass = 0; pass < 20; pass++) SourceOpenSettle(window);
            Button[] badges = Descendants(window).OfType<Button>()
                .Where(button => AutomationProperties.GetAutomationId(button) == "LibrarySourceBadge" && button.IsVisible).ToArray();
            Assert.Equal(sources.Length, badges.Length);
            SaveSourceOpenEvidence(window, $"cards-{label}.png");
            foreach (Button badge in badges)
            {
                var tile = (GalleryItemViewModel)badge.DataContext;
                string kind = sources.Single(source => ids[source.Kind] == tile.Id).Kind;
                if (FindSourceOpenAncestor<ListBoxItem>(badge) is { } card)
                    SaveSourceOpenEvidence(card, $"card-{kind}-{label}.png", 3);
            }

            Button documentBadge = badges.Single(badge => ((GalleryItemViewModel)badge.DataContext).Id == ids["document"]);
            ContextMenu badgeMenu = Assert.IsType<ContextMenu>(GallerySourceBadge.EnsureMenu(documentBadge));
            badgeMenu.PlacementTarget = documentBadge;
            badgeMenu.Placement = PlacementMode.Bottom;
            badgeMenu.IsOpen = true;
            SourceOpenSettle(window);
            for (int pass = 0; pass < 10; pass++) SourceOpenSettle(window);
            Assert.Equal(UiText.Get("Source.Open"), Assert.IsType<MenuItem>(badgeMenu.Items[0]).Header);
            SaveSourceOpenEvidence(badgeMenu, $"badge-menu-document-{label}.png", 2);
            badgeMenu.IsOpen = false;

            var documentCard = Assert.IsType<ListBoxItem>(FindSourceOpenAncestor<ListBoxItem>(documentBadge));
            window.OpenTileMenu(documentCard, PlacementMode.Bottom);
            SourceOpenSettle(window);
            for (int pass = 0; pass < 10; pass++) SourceOpenSettle(window);
            ContextMenu cardMenu = window.TileMenu;
            Assert.Contains(cardMenu.Items.OfType<MenuItem>(), item => item.Tag as string == "OpenSource" && item.IsVisible);
            SaveSourceOpenEvidence(cardMenu, $"card-menu-document-{label}.png", 2);
            cardMenu.IsOpen = false;

            foreach ((string kind, _, _, _) in sources)
            {
                vm.Select(ids[kind]);
                SourceOpenSettle(window);
                FrameworkElement section = Descendants(window).OfType<FrameworkElement>()
                    .Single(element => AutomationProperties.GetAutomationId(element) == "LibrarySourceSection");
                section.BringIntoView();
                for (int pass = 0; pass < 5; pass++) SourceOpenSettle(window);
                SaveSourceOpenEvidence(section, $"details-{kind}-{label}.png", 2);
            }
        }
        finally
        {
            window.CloseForExit();
        }
    }

    private static T? FindSourceOpenAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private static void SourceOpenSettle(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Thread.Sleep(15);
    }

    private static void SaveSourceOpenEvidence(Visual visual, string name, double zoom = 1)
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_SOURCE_OPEN_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string directory = DiagnosticOutputPaths.Create(requested);
        Visual target = visual is Window window ? (Visual)VisualTreeHelper.GetChild(window, 0) : visual;
        var element = (FrameworkElement)target;
        double width = element.ActualWidth, height = element.ActualHeight;
        if (width < 1 || height < 1) return;
        // A part of a window is cut from the whole window's rendering, so descendants drawn outside
        // the part's own box (which shift a VisualBrush) cannot move it.
        // A menu belongs to its window logically but draws in its own popup, outside the window's tree.
        var root = Window.GetWindow(element) is { } owner && VisualTreeHelper.GetChild(owner, 0) is FrameworkElement top
            && !ReferenceEquals(element, top) && top.IsAncestorOf(element) ? top : element;
        Rect area = ReferenceEquals(root, element) ? new Rect(0, 0, width, height)
            : element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, width, height));
        double rootWidth = root.ActualWidth, rootHeight = root.ActualHeight;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(rootWidth * zoom), (int)Math.Ceiling(rootHeight * zoom),
            96 * zoom, 96 * zoom, PixelFormats.Pbgra32);
        var content = new DrawingVisual();
        using (DrawingContext context = content.RenderOpen())
        {
            Brush background = Application.Current.TryFindResource("Surface.Window") as Brush
                ?? Application.Current.TryFindResource("Surface.Base") as Brush ?? Brushes.Gray;
            context.DrawRectangle(background, null, new Rect(0, 0, rootWidth, rootHeight));
            context.DrawRectangle(new VisualBrush(root)
            {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, rootWidth, rootHeight), Stretch = Stretch.Fill,
            }, null, new Rect(0, 0, rootWidth, rootHeight));
        }
        bitmap.Render(content);
        var pixels = new Int32Rect((int)Math.Floor(area.X * zoom), (int)Math.Floor(area.Y * zoom),
            (int)Math.Ceiling(area.Width * zoom), (int)Math.Ceiling(area.Height * zoom));
        pixels.Width = Math.Min(pixels.Width, bitmap.PixelWidth - pixels.X);
        pixels.Height = Math.Min(pixels.Height, bitmap.PixelHeight - pixels.Y);
        if (pixels.X < 0 || pixels.Y < 0 || pixels.Width < 1 || pixels.Height < 1) return;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(bitmap, pixels)));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }
}
