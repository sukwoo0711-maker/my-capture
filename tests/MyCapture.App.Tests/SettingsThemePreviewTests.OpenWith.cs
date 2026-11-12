using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryOpenWith_IsLazyForParentAndDisabledForMultipleSelectionForBothMediaKinds() =>
        RunIsolated(nameof(LibraryOpenWith_IsLazyForParentAndDisabledForMultipleSelectionForBothMediaKinds), () =>
        {
            Application application = CreateLibraryMotionApplication();
            var fixture = CreateOpenWithFixture();
            GalleryWindow window = fixture.Window;
            try
            {
                fixture.Records[1].MediaKind = CaptureMediaKind.Video;
                fixture.ViewModel.Refresh();
                var source = new OpenWithTestSource(1); window.OpenWithSource = source;
                window.Show(); SettleGalleryLayout(window);
                var rows = (ListBox)window.FindName("RowsList");
                ContextMenu menu = window.TileMenu;
                var item = menu.Items.OfType<MenuItem>().Single(candidate => Equals(candidate.Tag, "OpenWith"));
                MenuItem compare = menu.Items.OfType<MenuItem>().Single(candidate => Equals(candidate.Tag, "Compare"));
                Assert.Equal(menu.Items.IndexOf(compare) + 1, menu.Items.IndexOf(item));
                Assert.IsType<Separator>(menu.Items[menu.Items.IndexOf(item) + 1]);
                GalleryItemViewModel image = fixture.ViewModel.FindTile(fixture.Records[0].Id)!;
                GalleryItemViewModel video = fixture.ViewModel.FindTile(fixture.Records[1].Id)!;
                window.OpenTileMenu(RealizedCard(rows, image.Id), PlacementMode.Bottom);
                Assert.True(menu.IsOpen);
                Assert.Equal(0, source.Queries);
                Assert.True(item.IsEnabled);
                Assert.Equal(Visibility.Visible, item.Visibility);
                item.Focus();
                var right = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(item), Environment.TickCount, Key.Right)
                    { RoutedEvent = Keyboard.KeyDownEvent };
                item.RaiseEvent(right);
                Assert.True(right.Handled);
                OpenWithTestSupport.PumpUntil(() => item.IsSubmenuOpen && source.Queries == 1);
                Assert.Equal(".png", source.Extensions.Last());
                Assert.Equal(source.Set.Handlers[0].Name, AutomationProperties.GetName((MenuItem)item.Items[0]));
                menu.IsOpen = false;
                OpenWithTestSupport.PumpUntil(() => menu.PlacementTarget is null);

                fixture.ViewModel.Select(image.Id);
                fixture.ViewModel.Select(video.Id, control: true);
                window.OpenTileMenu(RealizedCard(rows, image.Id), PlacementMode.Bottom);
                Assert.False(item.IsEnabled);
                Assert.Equal(1, source.Queries);
                menu.IsOpen = false;
                OpenWithTestSupport.PumpUntil(() => menu.PlacementTarget is null);
                fixture.ViewModel.ClearSelection();
                source.Set = new OpenWithTestSet([new OpenWithTestHandler("Video sample app", null)]);
                window.OpenTileMenu(RealizedCard(rows, video.Id), PlacementMode.Bottom);
                Assert.True(item.IsEnabled);
                Assert.Equal(Visibility.Visible, item.Visibility);
                Assert.Equal(1, source.Queries);
                item.IsSubmenuOpen = true;
                OpenWithTestSupport.PumpUntil(() => source.Queries == 2);
                Assert.Equal(".mp4", source.Extensions.Last());
            }
            finally { window.TileMenu.IsOpen = false; window.CloseForExit(); application.Shutdown(); }
        });

    [Fact]
    public Task LibraryOpenWith_CloseBeforeClickPassesCopyAndLaunchFailureUsesExistingErrorLine() =>
        RunIsolated(nameof(LibraryOpenWith_CloseBeforeClickPassesCopyAndLaunchFailureUsesExistingErrorLine), () =>
        {
            Application application = CreateLibraryMotionApplication();
            var fixture = CreateOpenWithFixture();
            GalleryWindow window = fixture.Window;
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                CaptureRecord record = fixture.Records[0];
                string stored = fixture.Queue.GetFilePath(record, CaptureFileNames.Rendered);
                byte[] bytes = File.ReadAllBytes(stored);
                DateTime timestamp = File.GetLastWriteTimeUtc(stored);
                var source = new OpenWithTestSource(1)
                {
                    Invoke = path =>
                    {
                        Assert.NotEqual(stored, path);
                        Assert.False(path.StartsWith(fixture.Queue.GetDirectory(record) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                        Assert.True(File.Exists(path));
                        File.WriteAllBytes(path, [12, 34, 56]);
                        throw new InvalidOperationException("synthetic launch failure");
                    },
                };
                window.OpenWithSource = source;
                GalleryItemViewModel tile = fixture.ViewModel.FindTile(record.Id)!;
                ContextMenu menu = window.TileMenu;
                menu.PlacementTarget = new Button { DataContext = tile };
                window.PrepareTileMenu(menu, tile);
                var openWith = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "OpenWith"));
                openWith.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, openWith));
                var command = (MenuItem)openWith.Items[0];
                menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
                command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                OpenWithTestSupport.PumpUntil(() => source.InvokedPaths.Count == 1);
                OpenWithTestSupport.PumpUntil(() => window.ActionStatusKindForTest == FeedbackKind.ActionRequired);
                Assert.Equal(bytes, File.ReadAllBytes(stored));
                Assert.Equal(timestamp, File.GetLastWriteTimeUtc(stored));
                Assert.True(window.IsActionStatusDismissibleForTest);
                Assert.Equal(UiText.Format("OpenWith.Failed", UiText.Get("OpenWith.Unavailable")),
                    KoreanWordWrap.Strip(((TextBlock)window.FindName("LibraryActionStatus")).Text));
                Assert.Single(source.InvokedPaths);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); window.CloseForExit(); application.Shutdown(); }
        });

    [Fact]
    public Task LibraryOpenWith_RendersLibraryAndCardMenusWithSyntheticProgramsAt100Percent() =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYCAPTURE_OPENWITH_EVIDENCE")) ? Task.CompletedTask :
        RunIsolated(nameof(LibraryOpenWith_RendersLibraryAndCardMenusWithSyntheticProgramsAt100Percent), () =>
        {
            Application application = CreateLibraryMotionApplication();
            using var language = UiText.UseLanguage("ko-KR");
            bool? previousMotion = FluidMotion.AnimationsEnabledOverrideForTest;
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            string evidence = TestDiagnosticArtifacts.NewDirectory(Path.Combine(
                Environment.GetEnvironmentVariable("MYCAPTURE_OPENWITH_EVIDENCE")!, "openwith-" + Guid.NewGuid().ToString("N")));
            var fixture = CreateOpenWithFixture();
            GalleryWindow window = fixture.Window;
            QuickAccessWindow? card = null;
            using var router = new ReceiptKeyboardRouter(_ => new QuickAccessContextMenuTests.NoHotkeys());
            try
            {
                var source = new OpenWithTestSource(2);
                source.Set = new OpenWithTestSet([
                    new OpenWithTestHandler("사진 편집 예시", OpenWithTestSupport.Image()),
                    new OpenWithTestHandler("Sample_Viewer", null)]);
                window.OpenWithSource = source;
                GalleryItemViewModel tile = fixture.ViewModel.FindTile(fixture.Records[0].Id)!;
                ContextMenu menu = window.TileMenu;
                menu.Resources = window.Resources;
                menu.PlacementTarget = new Button { DataContext = tile };
                window.PrepareTileMenu(menu, tile);
                menu.Width = 300;
                SetEvidenceDpi(menu);
                LayoutEvidence(menu);

                string? baselinePath = Environment.GetEnvironmentVariable("MYCAPTURE_OPENWITH_BASELINE_XAML");
                if (!string.IsNullOrWhiteSpace(baselinePath))
                {
                    ContextMenu baseline = ReadOriginalTileMenu(TestDiagnosticArtifacts.ExistingFile(baselinePath));
                    baseline.Resources = window.Resources; baseline.Width = 300;
                    baseline.PlacementTarget = new Button { DataContext = tile };
                    // Properties is added by the existing TileMenu getter in both versions.
                    baseline.Items.Add(new MenuItem { Tag = "MediaInfo", Header = UiText.Get("Library.Properties"), InputGestureText = "Alt+Enter" });
                    window.PrepareTileMenu(baseline, tile);
                    SetEvidenceDpi(baseline); LayoutEvidence(baseline);
                    SaveOpenWithSheet(evidence, "library-as-is-100.png", [(baseline, new Point(16, 16))]);
                }

                var openWith = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "OpenWith"));
                openWith.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, openWith));
                LayoutEvidence(menu); openWith.ApplyTemplate();
                var popup = (Popup)openWith.Template.FindName("PART_Popup", openWith);
                FrameworkElement body = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
                SetEvidenceDpi(body); LayoutEvidence(body);
                Assert.Equal(1, VisualTreeHelper.GetDpi(menu).DpiScaleX);
                Assert.Equal(1, VisualTreeHelper.GetDpi(body).DpiScaleX);
                Assert.Equal(4, openWith.Items.Count);
                double rowY = openWith.TranslatePoint(new Point(), menu).Y;
                SaveOpenWithSheet(evidence, "library-to-be-100.png", [(menu, new Point(16, 16)), (body, new Point(320, rowY + 16))]);

                card = QuickAccessContextMenuTests.CreateCard(router: router);
                card.OpenWithSource = source;
                Border cardBody = QuickAccessDetachedFixture.Layout(card, 1);
                SaveOpenWithSheet(evidence, "card-as-is-100.png", [(cardBody, new Point(16, 16))]);
                ContextMenu cardMenu = card.CardMenu;
                cardMenu.Width = 260;
                SetEvidenceDpi(cardMenu); LayoutEvidence(cardMenu);
                var cardOpenWith = (MenuItem)cardMenu.Items[6];
                cardOpenWith.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, cardOpenWith));
                LayoutEvidence(cardMenu); cardOpenWith.ApplyTemplate();
                var cardPopup = (Popup)cardOpenWith.Template.FindName("PART_Popup", cardOpenWith);
                FrameworkElement cardSubmenu = Assert.IsAssignableFrom<FrameworkElement>(cardPopup.Child);
                SetEvidenceDpi(cardSubmenu); LayoutEvidence(cardSubmenu);
                double menuX = cardBody.ActualWidth + 30;
                SaveOpenWithSheet(evidence, "card-to-be-100.png", [(cardBody, new Point(16, 16)),
                    (cardMenu, new Point(menuX, 16)), (cardSubmenu, new Point(menuX + 270, cardOpenWith.TranslatePoint(new Point(), cardMenu).Y + 16))]);
                TestDiagnosticArtifacts.WriteJson(evidence, "evidence.json", new
                {
                    Dpi = 96, Content = "Synthetic only", Mode = "Actual detached WPF controls and popup bodies",
                    BaselineLibrary = baselinePath is null ? "Not supplied" : "Original dfc497d4 ContextMenu markup; event/command wiring removed for detached rendering",
                    BaselineCard = "Unchanged actual card content; the baseline had no context menu",
                    Programs = new[] { "사진 편집 예시", "Sample_Viewer" },
                });
            }
            finally { card?.Close(); window.CloseForExit(); FluidMotion.AnimationsEnabledOverrideForTest = previousMotion; application.Shutdown(); }
        });

    private sealed record OpenWithFixture(GalleryWindow Window, GalleryViewModel ViewModel, CaptureQueue Queue, CaptureRecord[] Records);

    private static OpenWithFixture CreateOpenWithFixture()
    {
        string root = OwnedTestDirectory.Create("mc-openwith-library-");
        var paths = AppPaths.CreateForRoot(root); paths.EnsureCreated();
        var settings = new AppSettings();
        var queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
        DateTimeOffset now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        CaptureRecord[] records = [AddLibraryFolderCapture(queue, now, "", Colors.SteelBlue),
            AddLibraryFolderCapture(queue, now.AddMinutes(-1), "", Colors.DarkSeaGreen)];
        var controller = new GalleryController(queue, NullLogger<GalleryController>.Instance);
        var persistence = new CapturePersistenceService(queue, paths, () => settings.Queue, NullLogger<CapturePersistenceService>.Instance);
        var commit = new CaptureCommitService(persistence, () => settings, () => paths, NullLogger<CaptureCommitService>.Instance,
            (_, _) => Task.FromResult(true));
        var ocr = new GalleryCompactOcr();
        var presenter = new OcrResultPresenter(ocr, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
        var indexing = new OcrIndexingService(controller, ocr, queue.GetDirectory, () => settings.Ocr,
            NullLogger<OcrIndexingService>.Instance, Dispatcher.CurrentDispatcher);
        var vm = new GalleryViewModel(controller, record => queue.GetFilePath(record, CaptureFileNames.Thumbnail), 96, clock: () => now);
        var window = new GalleryWindow(vm, controller, new GalleryReeditLoader(queue, NullLogger<GalleryReeditLoader>.Instance),
            commit, queue, new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance), paths, NullLoggerFactory.Instance,
            presenter, () => settings.Ocr, indexing, new GalleryMarkupPrivacy(), NullLogger.Instance)
        { WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        window.Closed += (_, _) => presenter.Dispose();
        FluidMotion.SetWindowEntrance(window, false);
        return new(window, vm, queue, records);
    }

    private static ContextMenu ReadOriginalTileMenu(string path)
    {
        XElement window = XDocument.Load(path).Root!;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement menu = new(window.Descendants().Single(element => element.Name.LocalName == "ContextMenu"
            && (string?)element.Attribute(x + "Key") == "Gallery.TileContextMenu"));
        foreach (XAttribute declaration in window.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
        {
            string value = declaration.Value;
            if (value.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal) && !value.Contains(";assembly=", StringComparison.Ordinal))
                value += ";assembly=MyCapture";
            menu.SetAttributeValue(declaration.Name, value);
        }
        menu.Attribute(x + "Key")?.Remove();
        foreach (XElement element in menu.DescendantsAndSelf())
        {
            string ns = element.Name.NamespaceName;
            if (ns.StartsWith("clr-namespace:MyCapture.", StringComparison.Ordinal) && !ns.Contains(";assembly=", StringComparison.Ordinal))
                element.Name = XName.Get(element.Name.LocalName, ns + ";assembly=MyCapture");
        }
        foreach (XAttribute attribute in menu.DescendantsAndSelf().Attributes().Where(attribute =>
                     attribute.Name.LocalName is "Click" or "Opened" or "Command" or "CommandTarget").ToArray()) attribute.Remove();
        return (ContextMenu)XamlReader.Parse(menu.ToString(SaveOptions.DisableFormatting));
    }

    private static void SetEvidenceDpi(FrameworkElement element)
    {
        VisualTreeHelper.SetRootDpi(element, new DpiScale(1, 1));
        element.InvalidateMeasure(); element.InvalidateArrange();
    }

    private static void SaveOpenWithSheet(string directory, string name, (FrameworkElement Element, Point Origin)[] parts)
    {
        var bitmaps = parts.Select(part =>
        {
            Size size = PopupSnapshot.ExtentOf(part.Element);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(part.Element);
            return (Image: bitmap, Bounds: PopupSnapshot.DrawBounds(part.Element, part.Origin, bitmap.PixelWidth, bitmap.PixelHeight));
        }).ToArray();
        double width = bitmaps.Max(part => part.Bounds.Right) + 16, height = bitmaps.Max(part => part.Bounds.Bottom) + 16;
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x25)), null, new Rect(0, 0, width, height));
            foreach (var part in bitmaps) context.DrawImage(part.Image, part.Bounds);
        }
        var sheet = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        sheet.Render(drawing);
        TestDiagnosticArtifacts.SavePng(directory, name, sheet);
    }
}
