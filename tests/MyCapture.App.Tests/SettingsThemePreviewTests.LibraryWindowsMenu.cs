using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Gallery;
using MyCapture.App.Sources;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryWindowsMenu_BadgeAndMenuOpenSourcesAndKeepFallbackAndCardInput() =>
        RunIsolated(nameof(LibraryWindowsMenu_BadgeAndMenuOpenSourcesAndKeepFallbackAndCardInput), () =>
        {
            var application = CreateLibraryMotionApplication();
            using var language = UiText.UseLanguage("ko-KR");
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            LibraryUxFixture fixture = CreateLibraryUxFixture("mc-windows-menu-actions-");
            GalleryWindow window = fixture.Window;
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                GalleryItemViewModel tile = fixture.ViewModel.FindTile(fixture.Records[0].Id)!;
                GalleryItemViewModel other = fixture.ViewModel.FindTile(fixture.Records[1].Id)!;
                var badge = new Button { DataContext = tile };
                var card = new ListBoxItem
                {
                    DataContext = tile, Content = badge, ContentTemplate = null, Style = null,
                    Template = new ControlTemplate(typeof(ListBoxItem))
                    { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
                };
                var rows = (ListBox)window.FindName("RowsList");
                rows.ItemsSource = null;
                rows.Items.Add(card);
                var client = (FrameworkElement)window.Content;
                client.Measure(new Size(1280, 760));
                client.Arrange(new Rect(0, 0, 1280, 760));
                client.UpdateLayout();
                var launched = new List<ProcessStartInfo>();
                bool missing = false;
                var actions = new CaptureSourceActions(launched.Add, _ => !missing, _ => false,
                    _ => throw new InvalidOperationException("No clipboard action is expected."), @"C:\Windows");
                var statuses = new List<string>();
                CaptureProvenance[] sources =
                [
                    CaptureProvenance.Create(url: "https://github.com/example/project?private=value"),
                    CaptureProvenance.Create(documentPath: @"C:\Work\report.txt", appExecutablePath: @"C:\Windows\System32\notepad.exe"),
                    CaptureProvenance.Create(appName: "Notepad", appExecutablePath: @"C:\Windows\System32\notepad.exe"),
                ];
                foreach (CaptureProvenance source in sources)
                {
                    GallerySourceBadge.SetProvenance(badge, source);
                    GallerySourceBadge.Configure(badge, source, statuses.Add, null, actions);
                    fixture.ViewModel.Select(other.Id);
                    window.SetDetailsPaneOpen(false);
                    int count = launched.Count, statusCount = statuses.Count;
                    badge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpLibraryUntil(window, () => statuses.Count > statusCount, "source opens through the injected action");
                    Assert.Equal(count + 1, launched.Count);
                    Assert.Equal(string.Empty, statuses.Last());
                    Assert.False(window.IsDetailsPaneOpen);
                    Assert.Equal(other.Id, Assert.Single(fixture.ViewModel.Selection.SelectedIds));
                    Assert.Equal(UiText.Format("Source.BadgeOpen", source.BadgeText), AutomationProperties.GetName(badge));
                    Assert.NotNull(GallerySourceBadge.EnsureMenu(badge));
                }
                Assert.Equal("출처 열기: Notepad", AutomationProperties.GetName(badge));
                Assert.Equal("출처 열기: Notepad", ((TextBlock)((ToolTip)badge.ToolTip).Content).Text);

                // Enter/Space remain the standard Button activation path: neither the badge's
                // preview handler nor the library's card shortcut handler consumes these keys.
                // Invoke is the same Button.OnClick route and requires no real keyboard input.
                GallerySourceBadge.SetProvenance(badge, sources[0]);
                GallerySourceBadge.Configure(badge, sources[0], statuses.Add, null, actions);
                LayoutEvidence(card);
                Assert.Same(window, Window.GetWindow(badge));
                Assert.Same(card, ItemsControl.ContainerFromElement(rows, badge));
                int cardCommands = 0;
                window.KeyCommandHookForTest = _ => { cardCommands++; return true; };
                var cardEnter = new KeyEventArgs(Keyboard.PrimaryDevice, new MenuTestPresentationSource(card), 0, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = card };
                typeof(GalleryWindow).GetMethod("OnLibraryItemsKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [rows, cardEnter]);
                Assert.True(cardEnter.Handled);
                Assert.Equal(1, cardCommands);
                cardCommands = 0;
                foreach (Key key in new[] { Key.Enter, Key.Space })
                {
                    var args = new KeyEventArgs(Keyboard.PrimaryDevice, new MenuTestPresentationSource(card), 0, key)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = badge.Content };
                    badge.RaiseEvent(args);
                    typeof(GalleryWindow).GetMethod("OnLibraryItemsKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(window, [rows, args]);
                    Assert.False(args.Handled);
                    Assert.Equal(0, cardCommands);
                    int count = launched.Count;
                    ((IInvokeProvider)new ButtonAutomationPeer(badge).GetPattern(PatternInterface.Invoke)!).Invoke();
                    PumpLibraryUntil(window, () => launched.Count > count, "button keyboard activation route");
                    Assert.Equal(count + 1, launched.Count);
                }

                // A source without an executable retains the existing library details fallback.
                CaptureProvenance unavailable = CaptureProvenance.Create(appName: "Notepad");
                GallerySourceBadge.SetProvenance(badge, unavailable);
                GallerySourceBadge.Configure(badge, unavailable, statuses.Add, null, actions);
                int before = launched.Count;
                badge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(before, launched.Count);
                Assert.True(window.IsDetailsPaneOpen);
                Assert.Equal(tile.Id, Assert.Single(fixture.ViewModel.Selection.SelectedIds));
                Assert.Equal("출처 상세: Notepad", AutomationProperties.GetName(badge));

                // A missing document reports the failure and reveals details, without launching a file.
                window.SetDetailsPaneOpen(false);
                missing = true;
                GallerySourceBadge.SetProvenance(badge, sources[1]);
                GallerySourceBadge.Configure(badge, sources[1], statuses.Add, null, actions);
                int previousStatuses = statuses.Count;
                badge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpLibraryUntil(window, () => statuses.Count > previousStatuses && window.IsDetailsPaneOpen, "document fallback");
                Assert.Equal(before, launched.Count);
                Assert.Equal(UiText.Get("Library.Source.FileMissing"), statuses.Last());
                missing = false;

                // The glyph's own menu remains independent from the shared card menu.
                Assert.NotSame(window.TileMenu, GallerySourceBadge.EnsureMenu(badge));

                // A double-click on the actual glyph is inside the badge. Neither the preview
                // handler nor the card's handler may begin a re-edit.
                var glyph = Assert.IsType<TextBlock>(badge.Content);
                badge.ApplyTemplate();
                LayoutEvidence(card);
                Assert.Same(card, ItemsControl.ContainerFromElement(rows, glyph));
                Assert.True((bool)typeof(GalleryWindow).GetMethod("IsInsideButton", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [glyph])!);
                var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = Control.MouseDoubleClickEvent, Source = glyph };
                typeof(GalleryWindow).GetMethod("OnTileDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [card, doubleClick]);
                Assert.False(doubleClick.Handled);
                Assert.Empty((HashSet<Guid>)typeof(GalleryWindow).GetField("_openEditors", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
                var previewDoubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                    { RoutedEvent = Control.PreviewMouseDoubleClickEvent, Source = glyph };
                badge.RaiseEvent(previewDoubleClick);
                Assert.True(previewDoubleClick.Handled);

                // The shared menu must resolve each new placement target, including a reused menu.
                window.SourceActions = actions;
                fixture.Records[0].SourcePageUrl = "https://first.example/page";
                fixture.Records[1].SourcePageUrl = "https://second.example/page";
                tile.RaiseMetaChanged(); other.RaiseMetaChanged();
                ContextMenu menu = window.TileMenu;
                MenuItem open = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "OpenSource"));
                foreach (GalleryItemViewModel target in new[] { tile, other })
                {
                    menu.PlacementTarget = new Button { DataContext = target };
                    window.PrepareTileMenu(menu, target);
                    LayoutEvidence(menu);
                    Assert.Equal(Visibility.Visible, open.Visibility);
                    Assert.Same(target, open.CommandParameter);
                    before = launched.Count;
                    open.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Assert.Equal(before + 1, launched.Count);
                    Assert.Equal(target.SourceProvenance.Url, launched.Last().FileName);
                }
                fixture.Records[1].SourcePageUrl = string.Empty;
                fixture.Records[1].SourceAppName = "Name only";
                other.RaiseMetaChanged();
                window.PrepareTileMenu(menu, other);
                Assert.Equal(Visibility.Collapsed, open.Visibility);
                Assert.Equal(Visibility.Visible, menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "SameSource")).Visibility);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                FluidMotion.AnimationsEnabledOverrideForTest = null;
                window.CloseForExit(); application.Shutdown();
            }
        });

    private sealed class MenuTestPresentationSource(Visual root) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = root;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    [Fact]
    public Task LibraryWindowsMenu_RendersCardAndMenuWithoutShowingAWindow() =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYCAPTURE_LIBRARY_MENU_EVIDENCE"))
            ? Task.CompletedTask
            : RunIsolated(nameof(LibraryWindowsMenu_RendersCardAndMenuWithoutShowingAWindow), () =>
        {
            var application = CreateLibraryMotionApplication();
            using var language = UiText.UseLanguage("ko-KR");
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            LibraryUxFixture fixture = CreateLibraryUxFixture("mc-windows-menu-evidence-");
            GalleryWindow window = fixture.Window;
            try
            {
                fixture.Records[0].SourcePageUrl = "https://github.com/owner/project";
                fixture.Records[0].SourceAppName = "Microsoft Edge";
                fixture.Records[0].SourceWindowTitle = "라이브러리 메뉴 검토";
                fixture.ViewModel.Refresh();
                GalleryItemViewModel tile = fixture.ViewModel.FindTile(fixture.Records[0].Id)!;
                var target = new Button { DataContext = tile };
                ContextMenu menu = window.TileMenu;
                menu.Resources = window.Resources;
                menu.PlacementTarget = target;
                window.PrepareTileMenu(menu, tile);
                menu.Width = 300;
                LayoutEvidence(menu);
                Assert.False(menu.IsOpen);
                Assert.True(menu.ActualHeight > 200);
                SaveWindowsMenuEvidence(menu, "menu");

                var template = (DataTemplate)window.FindResource("Gallery.TileTemplate");
                var card = (FrameworkElement)template.LoadContent();
                card.DataContext = tile;
                var frame = new Border
                {
                    Resources = window.Resources, Padding = new Thickness(16), Width = 340,
                    Background = (Brush)window.FindResource("Surface.Base"), Child = card,
                };
                LayoutEvidence(frame);
                Button badge = CardPart<Button>(card, "SourceBadge");
                Assert.Equal(Visibility.Visible, badge.Visibility);
                Assert.True(badge.ActualWidth > 0);
                SaveWindowsMenuEvidence(frame, "card");
                var tooltip = Assert.IsType<ToolTip>(badge.ToolTip);
                tooltip.Resources = window.Resources;
                LayoutEvidence(tooltip);
                SaveWindowsMenuEvidence(tooltip, "badge-tooltip");
                Assert.False(window.IsVisible);
            }
            finally
            {
                FluidMotion.AnimationsEnabledOverrideForTest = null;
                window.CloseForExit();
                application.Shutdown();
            }
        });

    private static void LayoutEvidence(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
    }

    private static void SaveWindowsMenuEvidence(FrameworkElement element, string name)
    {
        string root = Environment.GetEnvironmentVariable("MYCAPTURE_LIBRARY_MENU_EVIDENCE")!;
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_LIBRARY_MENU_PHASE") ?? "after";
        Directory.CreateDirectory(root);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * 2),
            (int)Math.Ceiling(element.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(root, $"{phase}-{name}.png"));
        encoder.Save(output);
    }
}
