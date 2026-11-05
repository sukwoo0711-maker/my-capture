using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using MyCapture.App.Gallery;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task LibraryRows_ResizeKeepsSourceViewSelectionAndFocusReentry() =>
        RunIsolated(nameof(LibraryRows_ResizeKeepsSourceViewSelectionAndFocusReentry), () =>
        {
            Application application = CreateLibraryMotionApplication();
            using var language = UiText.UseLanguage("ko-KR");
            LibraryUxFixture f = CreateLibraryUxFixture("mc-row-reset-");
            GalleryWindow window = f.Window;
            SynchronizationContext? previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
            try
            {
                // Keep the eager first projection and test the real width-change handler.
                Assert.NotEmpty(f.ViewModel.Rows);
                window.Width = 2000;
                // CI's real desktop (about 1024x768) clamps 2000 to near MinWidth 980, so the
                // column count cannot change. Admit the requested width at any DPI up to 300%.
                using var tracking = new WindowTrackSizeScope(window, new Size(2000 * 3 + 100, 0));
                window.Show(); window.Activate(); SettleGalleryLayout(window);
                Assert.True(window.IsActive, "This native focus regression requires the active test window.");
                var rows = (ListBox)window.FindName("RowsList");
                var search = (TextBox)window.FindName("SearchBox");
                var source = rows.ItemsSource;
                var view = CollectionViewSource.GetDefaultView(source);
                int columns = f.ViewModel.ColumnCount;
                Guid rememberedId = f.Records[2].Id;
                Guid selectedId = f.Records[3].Id;
                GalleryItemViewModel remembered = f.ViewModel.FindTile(rememberedId)!;
                GalleryItemViewModel selected = f.ViewModel.FindTile(selectedId)!;
                f.ViewModel.Select(selectedId);
                Assert.True(RealizedCard(rows, rememberedId).Focus());
                Assert.True(search.Focus());
                Assert.Same(search, Keyboard.FocusedElement);

                window.Width = window.MinWidth;
                SettleGalleryLayout(window);
                Assert.True(f.ViewModel.ColumnCount < columns);
                Assert.Same(source, rows.ItemsSource);
                Assert.Same(f.ViewModel.Rows, source);
                Assert.Same(view, CollectionViewSource.GetDefaultView(rows.ItemsSource));
                Assert.Equal(f.ViewModel.Rows.ToArray(), view.Cast<GalleryRow>().ToArray());
                Assert.Equal(selectedId, Assert.Single(f.ViewModel.Selection.SelectedIds));
                Assert.Same(selected, Assert.Single(f.ViewModel.SelectedTiles));
                Assert.Same(remembered, f.ViewModel.FindTile(rememberedId));
                Assert.Same(search, Keyboard.FocusedElement);

                // The old row containers were replaced. Re-entry must still find the
                // remembered capture by identity, ahead of the different selected tile.
                Assert.True(window.FocusGridEntry());
                PumpLibraryUntil(window, () => Keyboard.FocusedElement is ListBoxItem
                    { DataContext: GalleryItemViewModel tile } && tile.Id == rememberedId,
                    "row-layout reset retains the remembered capture for grid re-entry");
                Assert.Equal(selectedId, Assert.Single(f.ViewModel.Selection.SelectedIds));

                // Resize while a nonfirst card actually owns keyboard focus in a scrolled viewport.
                // Keep the Search-focus/re-entry checks above unchanged. No refocus or ScrollIntoView
                // is performed after this second resize, so a detached/stale focused card cannot pass.
                Assert.True(search.Focus());
                window.Height = window.MinHeight;
                SettleGalleryLayout(window);
                var scroll = Descendants(rows).OfType<ScrollViewer>().First();
                var viewport = Descendants(scroll).OfType<ScrollContentPresenter>().First();
                Assert.True(scroll.ScrollableHeight > 0, "The fixture must contain vertically offscreen cards.");
                scroll.ScrollToEnd();
                SettleGalleryLayout(window);
                Assert.True(scroll.VerticalOffset > 0, "The active-card resize starts after actual scrolling.");
                Guid activeId = f.ViewModel.Rows.OfType<GalleryTileRow>().Last().Tiles.Last().Id;
                GalleryItemViewModel activeTile = f.ViewModel.FindTile(activeId)!;
                Assert.NotEqual(f.ViewModel.Rows.OfType<GalleryTileRow>().First().Tiles[0].Id, activeId);
                Assert.True(RealizedCard(rows, activeId).Focus());
                PumpLibraryUntil(window, ActiveCardIsFocusedAndVisible, "scrolled active card before row reset");
                int narrowColumns = f.ViewModel.ColumnCount;

                window.Width = 2000;
                SettleGalleryLayout(window);
                Assert.True(f.ViewModel.ColumnCount > narrowColumns);
                PumpLibraryUntil(window, ActiveCardIsFocusedAndVisible,
                    "row reset keeps the actual active card focused and visible without refocusing");
                Assert.Same(source, rows.ItemsSource);
                Assert.Same(view, CollectionViewSource.GetDefaultView(rows.ItemsSource));
                Assert.Equal(f.ViewModel.Rows.ToArray(), view.Cast<GalleryRow>().ToArray());
                Assert.Same(activeTile, f.ViewModel.FindTile(activeId));
                Assert.Equal(selectedId, Assert.Single(f.ViewModel.Selection.SelectedIds));
                Assert.InRange(scroll.VerticalOffset, 0, scroll.ScrollableHeight);

                // XAML attaches the product handler to this same RowsList before this handler.
                int wideColumns = f.ViewModel.ColumnCount;
                bool observedPending = false, movedToSearch = false;
                SizeChangedEventHandler moveFocusToSearch = (_, args) =>
                {
                    if (!args.WidthChanged) return;
                    observedPending |= window.GridResizeFocusRestorePendingForTest;
                    movedToSearch |= search.Focus();
                };
                rows.SizeChanged += moveFocusToSearch;
                try
                {
                    window.Width = window.MinWidth;
                    SettleGalleryLayout(window);
                    DrainFocusCallbacks();
                    Assert.True(f.ViewModel.ColumnCount < wideColumns);
                    Assert.True(observedPending, "The product must have queued a restoration before the focus move.");
                    Assert.True(movedToSearch);
                    Assert.Same(search, Keyboard.FocusedElement);
                }
                finally { rows.SizeChanged -= moveFocusToSearch; }

                // Prove the remembered folder route defers before testing cancellation.
                GalleryFolderRow folderRow = f.ViewModel.Rows.OfType<GalleryFolderRow>().First();
                rows.ScrollIntoView(folderRow);
                SettleGalleryLayout(window);
                Button folder = FolderCardButtons(rows).First();
                var folderModel = Assert.IsType<GalleryFolderCardViewModel>(folder.DataContext);
                Assert.True(folder.Focus());
                Assert.Same(folder, Keyboard.FocusedElement);
                Assert.True(search.Focus());
                bool stillWanted = true;
                Assert.True(window.FocusGridEntry(() => stillWanted));
                Assert.Same(search, Keyboard.FocusedElement);
                DrainFocusCallbacks();
                Assert.Same(folderModel, Assert.IsType<Button>(Keyboard.FocusedElement).DataContext);

                Assert.True(search.Focus());
                Assert.True(window.FocusGridEntry(() => stillWanted));
                Assert.Same(search, Keyboard.FocusedElement);
                stillWanted = false;
                DrainFocusCallbacks();
                Assert.Same(search, Keyboard.FocusedElement);

                // Exercise the product input hook while the actual resize restore is pending.
                window.Width = window.MinWidth;
                SettleGalleryLayout(window);
                scroll.ScrollToEnd();
                SettleGalleryLayout(window);
                Assert.True(RealizedCard(rows, activeId).Focus());
                PumpLibraryUntil(window, ActiveCardIsFocusedAndVisible, "card before input-cancelled resize");
                narrowColumns = f.ViewModel.ColumnCount;
                bool inputSawPending = false, inputCancelledPending = false;
                SizeChangedEventHandler cancelWithInput = (_, args) =>
                {
                    if (!args.WidthChanged || !window.GridResizeFocusRestorePendingForTest) return;
                    inputSawPending = true;
                    _ = PressLibraryKey(window, Key.A);
                    inputCancelledPending = !window.GridResizeFocusRestorePendingForTest;
                };
                rows.SizeChanged += cancelWithInput;
                try
                {
                    window.Width = 2000;
                    SettleGalleryLayout(window);
                    DrainFocusCallbacks();
                    Assert.True(f.ViewModel.ColumnCount > narrowColumns);
                    Assert.True(inputSawPending);
                    Assert.True(inputCancelledPending);
                    Assert.False(ActiveCardIsFocusedAndVisible());
                }
                finally { rows.SizeChanged -= cancelWithInput; }

                // Hide at Send priority after the resize callback has queued its Loaded work.
                Assert.True(search.Focus());
                window.Width = window.MinWidth;
                SettleGalleryLayout(window);
                scroll.ScrollToEnd();
                SettleGalleryLayout(window);
                Assert.True(RealizedCard(rows, activeId).Focus());
                PumpLibraryUntil(window, ActiveCardIsFocusedAndVisible, "card before hidden resize");
                bool hideSawPending = false, hideCancelledPending = false;
                SizeChangedEventHandler hideBeforeRestore = (_, args) =>
                {
                    if (!args.WidthChanged || !window.GridResizeFocusRestorePendingForTest) return;
                    hideSawPending = true;
                    _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                    {
                        window.Hide();
                        hideCancelledPending = !window.GridResizeFocusRestorePendingForTest;
                    }));
                };
                rows.SizeChanged += hideBeforeRestore;
                try
                {
                    window.Width = 2000;
                    SettleGalleryLayout(window);
                    DrainFocusCallbacks();
                    Assert.True(hideSawPending);
                    Assert.True(hideCancelledPending);
                    Assert.False(window.IsVisible);
                    Assert.False(window.GridResizeFocusRestorePendingForTest);
                }
                finally { rows.SizeChanged -= hideBeforeRestore; }
                window.Show(); window.Activate(); SettleGalleryLayout(window);
                Assert.True(window.IsActive);

                // Put enough synthetic cards beyond the viewport/cache to require tile realization.
                Assert.True(search.Focus());
                for (int index = 0; index < 48; index++)
                    _ = AddLibraryFolderCapture(f.Queue, DateTimeOffset.UtcNow.AddMinutes(-index),
                        $"Focus virtualization {index}", System.Windows.Media.Colors.SlateGray);
                f.ViewModel.Refresh();
                window.Width = window.MinWidth;
                scroll.ScrollToTop();
                SettleGalleryLayout(window);
                Guid deferredId = f.ViewModel.Rows.OfType<GalleryTileRow>().First().Tiles[0].Id;
                Assert.True(RealizedCard(rows, deferredId).Focus());
                Assert.True(search.Focus());
                scroll.ScrollToEnd();
                SettleGalleryLayout(window);
                Assert.False(DeferredCardIsRealized());
                stillWanted = true;
                Assert.True(window.FocusGridEntry(() => stillWanted));
                Assert.Same(search, Keyboard.FocusedElement);
                DrainFocusCallbacks();
                Assert.Equal(deferredId, Assert.IsType<GalleryItemViewModel>(
                    Assert.IsType<ListBoxItem>(Keyboard.FocusedElement).DataContext).Id);

                Assert.True(search.Focus());
                scroll.ScrollToEnd();
                SettleGalleryLayout(window);
                Assert.False(DeferredCardIsRealized());
                Assert.True(window.FocusGridEntry(() => stillWanted));
                Assert.Same(search, Keyboard.FocusedElement);
                stillWanted = false;
                DrainFocusCallbacks();
                Assert.Same(search, Keyboard.FocusedElement);

                bool DeferredCardIsRealized() => Descendants(rows).OfType<ListBoxItem>().Any(item =>
                    item.DataContext is GalleryItemViewModel tile && tile.Id == deferredId);

                void DrainFocusCallbacks()
                {
                    for (int pass = 0; pass < 2; pass++)
                    {
                        var frame = new DispatcherFrame();
                        _ = window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                            new Action(() => frame.Continue = false));
                        Dispatcher.PushFrame(frame);
                    }
                }

                bool ActiveCardIsFocusedAndVisible()
                {
                    ListBoxItem? card = Descendants(rows).OfType<ListBoxItem>().FirstOrDefault(item =>
                        ReferenceEquals(item, Keyboard.FocusedElement)
                        && item.DataContext is GalleryItemViewModel tile && tile.Id == activeId);
                    if (card is null || PresentationSource.FromVisual(card) is null
                        || !rows.IsKeyboardFocusWithin || !card.IsVisible
                        || card.ActualWidth <= 0 || card.ActualHeight <= 0
                        || viewport.ActualWidth <= 0 || viewport.ActualHeight <= 0) return false;
                    Point center = card.TranslatePoint(new Point(card.ActualWidth / 2, card.ActualHeight / 2), viewport);
                    return new Rect(viewport.RenderSize).Contains(center);
                }

            }
            finally
            {
                window.CloseForExit();
                SynchronizationContext.SetSynchronizationContext(previous);
                application.Shutdown();
            }
        });
}
