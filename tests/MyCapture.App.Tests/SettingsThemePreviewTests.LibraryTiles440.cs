using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Diagnostics;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Themes;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryTiles440_EvidenceMixedMediaSelectionAndHover() =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYCAPTURE_LIBRARY_UX_EVIDENCE"))
            ? Task.CompletedTask
            : RunIsolated(nameof(LibraryTiles440_EvidenceMixedMediaSelectionAndHover), () =>
            {
                var application = CreateLibraryMotionApplication();
                bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
                FluidMotion.AnimationsEnabledOverrideForTest = false;
                try
                {
                    using var language = UiText.UseLanguage("ko-KR");
                    foreach (AppTheme theme in new[] { AppTheme.Glass, AppTheme.Midnight, AppTheme.Daylight })
                    {
                        ThemeService.Apply(theme);
                        LibraryUxFixture fixture = CreateLibraryTiles440Fixture();
                        GalleryWindow window = fixture.Window;
                        window.Height = 980;
                        try
                        {
                            window.Show();
                            var rows = (ListBox)window.FindName("RowsList");
                            PumpLibraryUntil(window, () => Descendants(rows).OfType<ListBoxItem>()
                                .Count(item => item.DataContext is GalleryItemViewModel) >= 3,
                                "mixed media capture tiles");
                            fixture.ViewModel.Select(fixture.Records[0].Id);
                            SettleGalleryLayout(window);
                            ListBoxItem hovered = Descendants(rows).OfType<ListBoxItem>()
                                .Single(item => item.DataContext is GalleryItemViewModel tile && tile.Id == fixture.Records[1].Id);
                            SetLibraryTileState440(hovered, "IsMouseOver", true);
                            SettleGalleryLayout(window);
                            SaveLibraryUxEvidence(window, theme, $"library-tiles440-{AppThemeNames.ToSetting(theme)}.png");
                        }
                        finally { window.CloseForExit(); }
                    }
                }
                finally
                {
                    FluidMotion.AnimationsEnabledOverrideForTest = previous;
                    ThemeService.Apply(AppTheme.Midnight);
                    application.Shutdown();
                }
            });

    [Fact]
    public Task LibraryTiles440_ActionsRevealOnHoverAndFocusWhileSelectionStaysQuietWithAccessibleNames() =>
        RunIsolated(nameof(LibraryTiles440_ActionsRevealOnHoverAndFocusWhileSelectionStaysQuietWithAccessibleNames), () =>
        {
            var application = CreateLibraryMotionApplication();
            bool? previous = FluidMotion.AnimationsEnabledOverrideForTest;
            FluidMotion.AnimationsEnabledOverrideForTest = false;
            try
            {
                foreach (string language in new[] { "ko-KR", "en-US" })
                {
                    using var languageScope = UiText.UseLanguage(language);
                    foreach (AppTheme theme in Enum.GetValues<AppTheme>())
                    foreach (double scale in new[] { 1d, 2d })
                    {
                        ThemeService.Apply(theme);
                        LibraryUxFixture fixture = CreateLibraryTiles440Fixture();
                        GalleryWindow window = fixture.Window;
                        try
                        {
                            window.Show();
                            VisualTreeHelper.SetRootDpi(window, new DpiScale(scale, scale));
                            foreach (UIElement element in Descendants(window).Prepend(window).OfType<UIElement>())
                            {
                                element.InvalidateMeasure();
                                element.InvalidateArrange();
                            }
                            var rows = (ListBox)window.FindName("RowsList");
                            PumpLibraryUntil(window, () => Descendants(rows).OfType<ListBoxItem>()
                                .Count(item => item.DataContext is GalleryItemViewModel) >= 3, "tile actions");
                            VerifyLibraryTileActions440(window, fixture, fixture.Records[0].Id, compact: false);
                            VerifyLibraryTileActions440(window, fixture, fixture.Records[1].Id, compact: false);
                            VerifyLibraryTileActions440(window, fixture, fixture.Records[2].Id, compact: false);
                            fixture.ViewModel.ClearSelection();
                            fixture.ViewModel.NavigateTo(CaptureFolderScope.ForFolder(fixture.Design.Id));
                            SettleGalleryLayout(window);
                            VerifyLibraryTileActions440(window, fixture, fixture.Records[4].Id, compact: true);
                        }
                        finally { window.CloseForExit(); }
                    }
                }
            }
            finally
            {
                FluidMotion.AnimationsEnabledOverrideForTest = previous;
                ThemeService.Apply(AppTheme.Midnight);
                application.Shutdown();
            }
        });

    private static void VerifyLibraryTileActions440(GalleryWindow window, LibraryUxFixture fixture, Guid id, bool compact)
    {
        fixture.ViewModel.ClearSelection();
        var rows = (ListBox)window.FindName("RowsList");
        ListBoxItem item = PumpLibraryUntil(window,
            () => Descendants(rows).OfType<ListBoxItem>()
                .FirstOrDefault(candidate => candidate.DataContext is GalleryItemViewModel tile && tile.Id == id),
            candidate => candidate is not null, "realized capture tile");
        var vm = (GalleryItemViewModel)item.DataContext;
        Border actions = Descendants(item).OfType<Border>().Single(border => border.Name == "TileActions");
        Assert.False(vm.IsSelected);
        Assert.Equal(0, actions.Opacity);
        Assert.False(actions.IsHitTestVisible);
        Assert.Equal(Visibility.Visible, actions.Visibility);

        Button[] buttons = Descendants(actions).OfType<Button>().Where(button => button.IsVisible).ToArray();
        Assert.Equal(vm.IsImage ? 4 : 2, buttons.Length);
        string[] names = buttons.Select(AutomationProperties.GetName).ToArray();
        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.DoesNotContain(UiText.Get("Text_89D4979D7596"), names);
        Assert.Contains(UiText.Get("Text_1003CFC78E66"), names);
        Assert.Contains(UiText.Get("Text_5E8959D18390"), names);
        if (vm.IsImage)
        {
            Assert.Contains(UiText.Get("Gallery.Float"), names);
            Assert.Contains(UiText.Get("Text_42A667BC71B0"), names);
        }
        foreach (Button button in buttons)
        {
            Assert.True(button.Focusable);
            Assert.True(button.IsTabStop);
            Assert.True(button.ActualWidth >= 24 && button.ActualHeight >= 24);
            Assert.NotNull(button.ToolTip);
        }
        Button edit = buttons.Single(button => AutomationProperties.GetName(button) == UiText.Get("Text_1003CFC78E66"));
        Assert.Equal(vm.IsImage ? UiText.Get("Text_0400CF56D074") : UiText.Get("Gallery.EditVideoHint"), edit.ToolTip);

        SetLibraryTileState440(item, "IsMouseOver", true);
        SettleGalleryLayout(window);
        Assert.Equal(1, actions.Opacity);
        Assert.True(actions.IsHitTestVisible);
        SetLibraryTileState440(item, "IsMouseOver", false);
        SettleGalleryLayout(window);
        Assert.Equal(0, actions.Opacity);
        Assert.False(actions.IsHitTestVisible);

        SetLibraryTileState440(item, "IsKeyboardFocusWithin", true);
        SettleGalleryLayout(window);
        Assert.Equal(1, actions.Opacity);
        Assert.True(actions.IsHitTestVisible);
        var focus = Assert.IsType<Border>(item.Template.FindName("FocusRing", item));
        Assert.Same(window.FindResource("Text.Primary"), focus.BorderBrush);
        Assert.Equal(1, focus.Opacity);
        SetLibraryTileState440(item, "IsKeyboardFocusWithin", false);
        SettleGalleryLayout(window);
        Assert.Equal(0, actions.Opacity);
        Assert.False(actions.IsHitTestVisible);

        fixture.ViewModel.Select(id);
        SettleGalleryLayout(window);
        // Selection retains its outline without obscuring the preview with idle commands.
        Assert.Equal(0, actions.Opacity);
        Assert.False(actions.IsHitTestVisible);
        var selected = Assert.IsType<Border>(item.Template.FindName("SelectedLayer", item));
        Assert.Equal(new Thickness(2), selected.BorderThickness);
        Assert.Same(window.FindResource("Text.Primary"), selected.BorderBrush);
        SetLibraryTileState440(item, "IsMouseOver", true);
        SettleGalleryLayout(window);
        Assert.Equal(1, actions.Opacity);
        Assert.True(actions.IsHitTestVisible);
        SetLibraryTileState440(item, "IsMouseOver", false);
        SettleGalleryLayout(window);
        Assert.Equal(0, actions.Opacity);
        Assert.False(actions.IsHitTestVisible);
        SetLibraryTileState440(item, "IsKeyboardFocusWithin", true);
        SettleGalleryLayout(window);
        Assert.Equal(1, actions.Opacity);
        Assert.True(actions.IsHitTestVisible);
        Assert.Equal(1, focus.Opacity);
        Assert.Same(window.FindResource("Text.Primary"), selected.BorderBrush);
        SetLibraryTileState440(item, "IsKeyboardFocusWithin", false);
        SettleGalleryLayout(window);
        Assert.Equal(0, actions.Opacity);
        Assert.False(actions.IsHitTestVisible);

        FrameworkElement preview = Descendants(item).OfType<FrameworkElement>().Single(element => element.Name == "TilePreview");
        Assert.InRange(Math.Abs(preview.ActualWidth * 10d / 16 - preview.ActualHeight), 0, 1);
        Image thumbnail = Descendants(preview).OfType<Image>().Single();
        Assert.Equal(Stretch.Uniform, thumbnail.Stretch);
        TextBlock title = Descendants(item).OfType<TextBlock>().Single(text => text.Name == "TileTitle");
        Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
        Assert.Equal(TextWrapping.NoWrap, title.TextWrapping);
        Assert.Equal(vm.Caption, title.Text);
        Assert.Null(item.ToolTip);
        var captionTip = Assert.IsType<ToolTip>(title.ToolTip);
        captionTip.PlacementTarget = title;
        try
        {
            captionTip.IsOpen = true;
            SettleGalleryLayout(window);
            TextBlock expanded = Assert.Single(Descendants(captionTip).OfType<TextBlock>(), text => text.Text == vm.Caption);
            Assert.Equal(TextWrapping.Wrap, expanded.TextWrapping);
            Assert.Equal(TextTrimming.None, expanded.TextTrimming);
            Assert.Equal(420, captionTip.MaxWidth);
            Assert.Equal(300, Assert.Single(Descendants(captionTip).OfType<ScrollViewer>()).MaxHeight);
        }
        finally { captionTip.IsOpen = false; }
        Assert.True(title.FontSize >= 12);
        Assert.DoesNotContain(Descendants(item).OfType<TextBlock>(),
            text => text.IsVisible && text.Text == UiText.Get("Text_302BAE127938"));
        FrameworkElement videoBadge = Descendants(item).OfType<FrameworkElement>().Single(element => element.Name == "VideoBadge");
        Assert.Equal(vm.IsVideo, videoBadge.IsVisible);
        if (vm.IsVideo)
            Assert.Contains(Descendants(videoBadge).OfType<TextBlock>(), text => text.Text == vm.DurationCaption);
        Border keepBadge = Descendants(item).OfType<Border>().Single(border => border.Name == "KeepBadge");
        Assert.Equal(vm.IsPinned, keepBadge.IsVisible);
        Assert.Same(window.FindResource("Surface.Sunken"), keepBadge.Background);
        Assert.Equal(compact, Descendants(item).OfType<Border>().Any(border => border.Name == "CompactTileCard"));

        Button more = buttons.Single(button => AutomationProperties.GetName(button) == UiText.Get("Text_5E8959D18390"));
        ContextMenu menu = Assert.IsType<ContextMenu>(more.ContextMenu);
        menu.PlacementTarget = more;
        menu.DataContext = vm;
        MenuItem editMenu = menu.Items.OfType<MenuItem>()
            .Single(entry => AutomationProperties.GetName(entry) == UiText.Get("Text_1003CFC78E66"));
        Assert.Equal(vm.IsImage ? "Enter" : string.Empty, editMenu.InputGestureText);
        Assert.Contains(menu.Items.OfType<MenuItem>(), entry => entry.InputGestureText == "Delete"
            && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(entry)));
        Assert.NotNull(item.ContextMenu);
        fixture.ViewModel.ClearSelection();
        SettleGalleryLayout(window);
        Assert.Equal(0, actions.Opacity);
    }

    [Fact]
    public Task LibraryTiles440_ThousandItemsKeepRowsAndThumbnailLoadingVirtualized() =>
        RunIsolated(nameof(LibraryTiles440_ThousandItemsKeepRowsAndThumbnailLoadingVirtualized), () =>
        {
            var application = CreateLibraryMotionApplication();
            ThemeService.Apply(AppTheme.Glass);
            EvidenceLibrary fixture = CreateEvidenceLibrary(1_000, "mc-tiles440-perf-");
            try
            {
                var timer = Stopwatch.StartNew();
                fixture.Window.Show();
                SettleGalleryLayout(fixture.Window);
                double openedMs = timer.Elapsed.TotalMilliseconds;
                var rows = (ListBox)fixture.Window.FindName("RowsList");
                Assert.True(VirtualizingPanel.GetIsVirtualizing(rows));
                Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(rows));
                Assert.True(ScrollViewer.GetCanContentScroll(rows));
                Assert.Null(rows.ItemContainerGenerator.ContainerFromIndex(rows.Items.Count - 1));
                GalleryItemViewModel[] all = fixture.ViewModel.Groups.SelectMany(group => group.Items).ToArray();
                Assert.Equal(1_000, all.Length);
                int initialRealized = Descendants(rows).OfType<ListBoxItem>()
                    .Count(item => item.DataContext is GalleryItemViewModel);
                Assert.InRange(initialRealized, 1, 99);
                Assert.Equal(0, all[^1].CachedThumbnailBytes);

                var scroll = Descendants(rows).OfType<ScrollViewer>().First();
                timer.Restart();
                scroll.ScrollToEnd();
                SettleGalleryLayout(fixture.Window);
                double scrolledMs = timer.Elapsed.TotalMilliseconds;
                Assert.NotNull(rows.ItemContainerGenerator.ContainerFromIndex(rows.Items.Count - 1));
                Assert.Null(rows.ItemContainerGenerator.ContainerFromIndex(0));
                int endRealized = Descendants(rows).OfType<ListBoxItem>()
                    .Count(item => item.DataContext is GalleryItemViewModel);
                Assert.InRange(endRealized, 1, 99);

                string? output = Environment.GetEnvironmentVariable("MYCAPTURE_LIBRARY_UX_EVIDENCE");
                if (!string.IsNullOrWhiteSpace(output))
                    File.WriteAllText(DiagnosticOutputPaths.Child(DiagnosticOutputPaths.Create(output), "library-tiles440-performance.txt"),
                        $"1000 synthetic captures; window show and layout {openedMs:F2} ms; scroll to end and layout {scrolledMs:F2} ms; "
                        + $"realized tiles {initialRealized} at start, {endRealized} at end.\n");
            }
            finally
            {
                fixture.Window.CloseForExit();
                application.Shutdown();
            }
        });

    // Exercise the shipped read-only state bindings without sending input or changing desktop focus.
    // The same isolated WPF technique is used by CapturePixelFillTests and AccentColorThemeTests.
    private static void SetLibraryTileState440(UIElement element, string property, bool value)
    {
        var key = Assert.IsType<DependencyPropertyKey>(typeof(UIElement)
            .GetField(property + "PropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        element.SetValue(key, value);
    }

    private static LibraryUxFixture CreateLibraryTiles440Fixture()
    {
        LibraryUxFixture fixture = CreateLibraryUxFixture("mc-tiles440-");
        fixture.Records[0].Title = "Capture review - toolbar and keyboard navigation";
        fixture.Records[1].Title = "Settings and storage layout";
        fixture.Records[2].Title = "Screen recording - reproduction steps";
        fixture.Records[2].MediaKind = CaptureMediaKind.Video;
        fixture.Records[2].DurationMs = 92_000;
        fixture.Records[2].FrameRate = 30;
        fixture.Records[2].FrameCount = 2_760;
        fixture.ViewModel.Refresh();
        return fixture;
    }
}
