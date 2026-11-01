using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryWindowsInputTests
{
    [Fact]
    public Task ControlArrowsMoveRealKeyboardFocusAcrossRowsWithoutChangingSelection() =>
        Run(nameof(ControlArrowsMoveRealKeyboardFocusAcrossRowsWithoutChangingSelection), fixture =>
        {
            fixture.Model.ShowView(GalleryFilter.Images);
            fixture.Window.WindowStartupLocation = WindowStartupLocation.Manual;
            fixture.Window.Left = -10000;
            fixture.Window.Top = -10000;
            fixture.Window.Width = 1280;
            fixture.Window.Height = 760;
            fixture.Window.ShowInTaskbar = false;
            fixture.Window.ShowActivated = false;
            // Keep the real scroll surface smaller than a card row, so its neighbour is
            // wholly clipped before Ctrl+Down instead of already peeking into the viewport.
            fixture.Rows.Height = 140;
            fixture.Rows.VerticalAlignment = VerticalAlignment.Top;
            fixture.Window.Show();
            fixture.Window.Activate();
            fixture.SettleLayout();

            Assert.Null(fixture.Window.TileFocusForTest);
            Assert.Same(fixture.Model.Rows, fixture.Rows.ItemsSource);
            GalleryTileRow[] rows = fixture.Model.Rows.OfType<GalleryTileRow>().ToArray();
            Assert.True(rows.Length > 5, "The fixture must have rows beyond the viewport.");
            GalleryItemViewModel[] first = rows[0].Tiles.ToArray();
            Assert.True(first.Length > 1);
            fixture.Model.Select(first[0].Id);
            fixture.Model.Select(first[1].Id, control: true);
            Guid[] selected = fixture.Model.Selection.SelectedIds.ToArray();
            Guid? anchor = fixture.Model.Selection.Anchor;

            ListBoxItem initial = fixture.RealizedCard(first[0]);
            Assert.IsType<ListBox>(ItemsControl.ItemsControlFromItemContainer(initial));
            Assert.NotSame(fixture.Rows, ItemsControl.ItemsControlFromItemContainer(initial));
            Assert.True(initial.Focus());
            fixture.WaitForFocus(first[0]);

            void Move(Key key, GalleryItemViewModel expected)
            {
                var focused = Assert.IsType<ListBoxItem>(Keyboard.FocusedElement);
                Assert.IsType<GalleryItemViewModel>(focused.DataContext);
                Assert.True(fixture.RoutedKey(focused, key, ModifierKeys.Control).Handled);
                fixture.WaitForFocus(expected);
                Assert.Same(expected, ((ListBoxItem)Keyboard.FocusedElement).DataContext);
                Assert.Equal(selected, fixture.Model.Selection.SelectedIds);
                Assert.Equal(anchor, fixture.Model.Selection.Anchor);
            }

            Move(Key.Right, first[1]);
            Move(Key.Left, first[0]);

            GalleryItemViewModel target = rows[1].Tiles[0];
            Assert.True(fixture.RowsScroller.ScrollableHeight > 0);
            Assert.True(initial.ActualHeight > fixture.RowsScroller.ViewportHeight);
            Assert.False(fixture.IsCardInViewport(target), "The next row must be wholly outside the viewport before Ctrl+Down.");
            double previousOffset = fixture.RowsScroller.VerticalOffset;
            Move(Key.Down, target);
            Assert.True(fixture.IsCardInViewport(target), "The focused destination must be brought into view.");
            Assert.True(fixture.RowsScroller.VerticalOffset > previousOffset);
        }, imageCount: 60);

    [Fact]
    public Task ControlArrowsMoveFocusOnlyAndSpaceAndShiftKeepTheirSelectionRules() =>
        Run(nameof(ControlArrowsMoveFocusOnlyAndSpaceAndShiftKeepTheirSelectionRules), fixture =>
        {
            GalleryItemViewModel[] tiles = fixture.Model.Rows.OfType<GalleryTileRow>().SelectMany(row => row.Tiles).ToArray();
            Dictionary<Guid, ListBoxItem> cards = fixture.AttachCards(tiles);
            GalleryItemViewModel focused = tiles[0];
            int focusMoves = 0;
            fixture.Window.TileFocusForTest = tile => { focused = tile; focusMoves++; };
            fixture.Model.Select(focused.Id);
            Guid? anchor = fixture.Model.Selection.Anchor;
            foreach ((Key key, int index) in new[] { (Key.Right, 1), (Key.Right, 2), (Key.Left, 1) })
            {
                KeyEventArgs args = fixture.Key("OnLibraryItemsKeyDown", cards[focused.Id], key, ModifierKeys.Control);
                Assert.True(args.Handled);
                Assert.Same(tiles[index], focused);
                Assert.Equal(new[] { tiles[0].Id }, fixture.Model.Selection.SelectedIds);
                Assert.Equal(anchor, fixture.Model.Selection.Anchor);
            }
            Assert.Equal(3, focusMoves);

            fixture.Key("OnLibraryItemsKeyDown", cards[focused.Id], Key.Space, ModifierKeys.Control);
            Assert.Equal(tiles.Take(2).Select(tile => tile.Id), fixture.Model.Selection.SelectedIds);
            Assert.Equal(focused.Id, fixture.Model.Selection.Anchor);
            fixture.Key("OnLibraryItemsKeyDown", cards[focused.Id], Key.Space, ModifierKeys.Control);
            Assert.Equal(new[] { tiles[0].Id }, fixture.Model.Selection.SelectedIds);

            fixture.Model.Select(tiles[0].Id);
            focused = tiles[1];
            fixture.Key("OnLibraryItemsKeyDown", cards[focused.Id], Key.Right, ModifierKeys.Shift);
            Assert.Same(tiles[2], focused);
            Assert.Equal(tiles.Take(3).Select(tile => tile.Id), fixture.Model.Selection.SelectedIds);
            Assert.Equal(tiles[0].Id, fixture.Model.Selection.Anchor);

            fixture.Model.Select(tiles[0].Id);
            fixture.Model.Select(tiles[2].Id, control: true);
            focused = tiles[2];
            fixture.Key("OnLibraryItemsKeyDown", cards[focused.Id], Key.Right, ModifierKeys.Control | ModifierKeys.Shift);
            Assert.Same(tiles[3], focused);
            Assert.Equal(new[] { tiles[0].Id, tiles[2].Id, tiles[3].Id }, fixture.Model.Selection.SelectedIds);
            Assert.Equal(tiles[2].Id, fixture.Model.Selection.Anchor);
        });

    [Fact]
    public Task BackForwardAndParentHandlersUseHistoryAndProtectSearchText() =>
        Run(nameof(BackForwardAndParentHandlersUseHistoryAndProtectSearchText), fixture =>
        {
            CaptureFolder parent = fixture.Wait(fixture.Queue.CreateFolderAsync("Parent"));
            CaptureFolder child = fixture.Wait(fixture.Queue.CreateFolderAsync("Child", parent.Id));
            CaptureFolder sibling = fixture.Wait(fixture.Queue.CreateFolderAsync("Sibling"));
            fixture.Model.NavigateTo(CaptureFolderScope.ForFolder(sibling.Id));
            fixture.Model.NavigateTo(CaptureFolderScope.ForFolder(child.Id));
            Assert.True(fixture.Key("OnNavigationKeyDown", fixture.Rows, Key.Left, ModifierKeys.Alt).Handled);
            Assert.Equal(sibling.Id, fixture.Model.SelectedScope.FolderId);
            Assert.True(fixture.Key("OnNavigationKeyDown", fixture.Rows, Key.Right, ModifierKeys.Alt).Handled);
            Assert.Equal(child.Id, fixture.Model.SelectedScope.FolderId);
            Assert.True(fixture.Key("OnNavigationKeyDown", fixture.Rows, Key.Up, ModifierKeys.Alt).Handled);
            Assert.Equal(parent.Id, fixture.Model.SelectedScope.FolderId);
            Assert.True(fixture.Key("OnNavigationKeyDown", fixture.Rows, Key.Back, ModifierKeys.None).Handled);
            Assert.Equal(child.Id, fixture.Model.SelectedScope.FolderId);

            var search = (TextBox)fixture.Window.FindName("SearchBox");
            search.Text = "draft";
            Assert.False(fixture.Key("OnNavigationKeyDown", search, Key.Back, ModifierKeys.None).Handled);
            Assert.Equal(child.Id, fixture.Model.SelectedScope.FolderId);
            Assert.Equal("draft", search.Text);
            search.Text = string.Empty;

            fixture.Model.ShowView(GalleryFilter.Images);
            fixture.Model.ShowView(GalleryFilter.Videos);
            Assert.False(fixture.Model.CanNavigateParent);
            Assert.True(fixture.MouseNavigation(MouseButton.XButton1).Handled);
            Assert.True(fixture.Model.IsImagesView);
            Assert.True(fixture.MouseNavigation(MouseButton.XButton2).Handled);
            Assert.True(fixture.Model.IsVideosView);
            Assert.True(fixture.Key("OnNavigationKeyDown", fixture.Rows, Key.Left, ModifierKeys.Alt).Handled);
            Assert.True(fixture.Model.IsImagesView);
            Assert.False(fixture.Key("OnNavigationKeyDown", fixture.Rows, Key.Up, ModifierKeys.Alt).Handled);
        });

    [Fact]
    public Task EnterDoubleClickAndPlayRestartAnOpenVideoNowOrWhenReady() =>
        Run(nameof(EnterDoubleClickAndPlayRestartAnOpenVideoNowOrWhenReady), fixture =>
        {
            GalleryItemViewModel tile = fixture.Model.FindTile(fixture.Video.Id)!;
            ListBoxItem card = fixture.AttachCards([tile])[tile.Id];
            foreach (string input in new[] { "Enter", "DoubleClick", "Play" })
            foreach (bool ready in new[] { false, true })
            {
                Invoke(fixture.Window, "CloseInlinePlayer");
                var player = new FakeVideoPlayer();
                int created = 0;
                fixture.Window.VideoPlayerFactory = () => { created++; return player; };
                fixture.Window.SelectVideoForReview(tile);
                Assert.Equal(1, player.OpenCount);
                Assert.False(player.Playing);
                if (ready) player.MarkReady();

                void Play()
                {
                    if (input == "Enter")
                    {
                        Assert.True(fixture.Key("OnLibraryItemsKeyDown", card, Key.Enter, ModifierKeys.None).Handled);
                    }
                    else if (input == "DoubleClick")
                    {
                        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                            { RoutedEvent = Control.MouseDoubleClickEvent, Source = card };
                        Invoke(fixture.Window, "OnTileDoubleClick", card, click);
                        Assert.True(click.Handled);
                    }
                    else
                    {
                        ContextMenu menu = fixture.Window.TileMenu;
                        menu.PlacementTarget = card;
                        fixture.Window.PrepareTileMenu(menu, tile);
                        menu.Measure(new Size(300, double.PositiveInfinity));
                        menu.Arrange(new Rect(menu.DesiredSize));
                        menu.UpdateLayout();
                        fixture.Drain();
                        MenuItem play = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "Play"));
                        Assert.Same(tile, play.CommandParameter);
                        play.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    }
                    fixture.Drain();
                }

                Play();
                Assert.Equal(1, player.PlayRequests);
                Assert.Equal(ready, player.Playing);
                Assert.Equal(!ready, player.Pending);
                if (!ready) player.MarkReady();
                Assert.True(player.Playing);
                player.Pause();
                Play();
                Assert.Equal(2, player.PlayRequests);
                Assert.True(player.Playing);
                Assert.Equal(1, created);
                Assert.Equal(1, player.OpenCount);
                Assert.Equal(2, player.ActivatedCount);
            }
        });

    [Fact]
    public Task RealPlayerRetainsARequestUntilMediaReadyAndThenStartsImmediately() =>
        Run(nameof(RealPlayerRetainsARequestUntilMediaReadyAndThenStartsImmediately), _ =>
        {
            var player = new GalleryVideoPlayerWindow();
            var playback = new List<bool>();
            player.PlaybackForTest = playback.Add;
            try
            {
                player.RequestPlay();
                Assert.Empty(playback);
                Assert.False(player.IsMediaReadyForDiagnostic);
                typeof(GalleryVideoPlayerWindow).GetField("_mediaReady", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(player, true);
                typeof(GalleryVideoPlayerWindow).GetMethod("ApplyPlaybackAfterOpening", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(player, null);
                Assert.Equal(new[] { true }, playback);
                Assert.True(player.IsPlayingForDiagnostic);
                player.RequestPlay();
                Assert.Equal(new[] { true, true }, playback);
            }
            finally { player.Close(); }
        });

    private static Task Run(string test, Action<Fixture> verify, int imageCount = 4) => SettingsThemePreviewTests.RunIsolated(test, () =>
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
        application.Resources["PinLabel"] = new PinLabelConverter();
        application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        using var fixture = new Fixture(imageCount);
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(fixture.Window.Dispatcher));
        try { verify(fixture); }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            FluidMotion.AnimationsEnabledOverrideForTest = null;
            application.Shutdown();
        }
    }, typeof(GalleryWindowsInputTests));

    private static void Invoke(GalleryWindow window, string name, params object[] args) =>
        typeof(GalleryWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    private sealed class TestSource(Visual root) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = root;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private sealed class FakeVideoPlayer : IGalleryVideoPlayer
    {
        public Guid VideoId { get; private set; }
        public event EventHandler? Closed;
        internal int OpenCount { get; private set; }
        internal int PlayRequests { get; private set; }
        internal int ActivatedCount { get; private set; }
        internal bool Playing { get; private set; }
        internal bool Pending { get; private set; }
        private bool _ready;
        public bool Open(Guid id, string title, string path, bool autoPlay)
        {
            VideoId = id;
            OpenCount++;
            if (autoPlay) RequestPlay();
            return true;
        }
        public void RequestPlay() { PlayRequests++; if (_ready) Playing = true; else Pending = true; }
        internal void MarkReady() { _ready = true; if (Pending) Playing = true; Pending = false; }
        internal void Pause() => Playing = false;
        public void ShowPlayer(bool activate) { if (activate) ActivatedCount++; }
        public void Close() => Closed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly OcrResultPresenter _presenter;
        internal GalleryWindow Window { get; }
        internal GalleryViewModel Model { get; }
        internal CaptureQueue Queue { get; }
        internal CaptureRecord Video { get; }
        internal ListBox Rows => (ListBox)Window.FindName("RowsList");

        internal Fixture(int imageCount)
        {
            string root = TestRecycleBin.CreateTempSubdirectory("mc-windows-input-").FullName;
            var paths = AppPaths.CreateForRoot(root);
            paths.EnsureCreated();
            var settings = new AppSettings();
            Queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            for (int index = 0; index < imageCount; index++)
                Queue.Add(new CaptureRecord { CreatedAt = DateTimeOffset.Now.AddMinutes(-index), RelativeDirectory = Guid.NewGuid().ToString("N") });
            Video = new CaptureRecord { MediaKind = CaptureMediaKind.Video, CreatedAt = DateTimeOffset.Now.AddDays(-1), RelativeDirectory = Guid.NewGuid().ToString("N") };
            Queue.Add(Video);
            var controller = new GalleryController(Queue, NullLogger<GalleryController>.Instance);
            Model = new GalleryViewModel(controller, record => Queue.GetFilePath(record, CaptureFileNames.Thumbnail), 96);
            var persistence = new CapturePersistenceService(Queue, paths, () => settings.Queue, NullLogger<CapturePersistenceService>.Instance);
            var commit = new CaptureCommitService(persistence, () => settings, () => paths,
                NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
            var ocr = new NoOcr();
            _presenter = new OcrResultPresenter(ocr, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
            var indexing = new OcrIndexingService(controller, ocr, Queue.GetDirectory, () => settings.Ocr,
                NullLogger<OcrIndexingService>.Instance, Dispatcher.CurrentDispatcher);
            Window = new GalleryWindow(Model, controller, new GalleryReeditLoader(Queue, NullLogger<GalleryReeditLoader>.Instance),
                commit, Queue, new VideoLibraryService(Queue, paths, NullLogger<VideoLibraryService>.Instance), paths,
                NullLoggerFactory.Instance, _presenter, () => settings.Ocr, indexing, new NoPrivacy(), NullLogger.Instance);
        }

        internal Dictionary<Guid, ListBoxItem> AttachCards(IEnumerable<GalleryItemViewModel> tiles)
        {
            Rows.ItemsSource = null;
            Rows.Items.Clear();
            var cards = tiles.ToDictionary(tile => tile.Id, tile => new ListBoxItem
            {
                DataContext = tile, Content = new Border { Width = 80, Height = 60 }, ContentTemplate = null, Style = null,
                Template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
            });
            foreach (ListBoxItem card in cards.Values) Rows.Items.Add(card);
            var client = (FrameworkElement)Window.Content;
            client.Measure(new Size(1280, 760)); client.Arrange(new Rect(0, 0, 1280, 760)); client.UpdateLayout();
            Assert.All(cards.Values, card => Assert.Same(Window, System.Windows.Window.GetWindow(card)));
            return cards;
        }

        internal KeyEventArgs Key(string handler, FrameworkElement source, Key key, ModifierKeys modifiers)
        {
            Window.KeyModifiersForTest = modifiers;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, new TestSource(Rows), 0, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = source };
            Invoke(Window, handler, source, args);
            Drain();
            return args;
        }

        internal ScrollViewer RowsScroller => Descendants(Rows).OfType<ScrollViewer>().First();

        internal ListBoxItem RealizedCard(GalleryItemViewModel tile) =>
            Descendants(Rows).OfType<ListBoxItem>().Single(card => ReferenceEquals(card.DataContext, tile));

        internal bool IsCardInViewport(GalleryItemViewModel tile)
        {
            ListBoxItem? card = Descendants(Rows).OfType<ListBoxItem>()
                .SingleOrDefault(candidate => ReferenceEquals(candidate.DataContext, tile));
            if (card is null || !card.IsVisible) return false;
            ScrollContentPresenter viewport = Descendants(RowsScroller).OfType<ScrollContentPresenter>().First();
            Rect bounds = card.TransformToAncestor(viewport).TransformBounds(new Rect(card.RenderSize));
            Rect visible = Rect.Intersect(bounds, new Rect(viewport.RenderSize));
            return !visible.IsEmpty && visible.Width > 0 && visible.Height > 0;
        }

        internal KeyEventArgs RoutedKey(ListBoxItem source, Key key, ModifierKeys modifiers)
        {
            Window.KeyModifiersForTest = modifiers;
            PresentationSource presentation = PresentationSource.FromVisual(Window)
                ?? throw new InvalidOperationException("The isolated window must have a real presentation source.");
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, presentation, 0, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            source.RaiseEvent(args);
            return args;
        }

        internal void SettleLayout()
        {
            for (int pass = 0; pass < 3; pass++) { Window.UpdateLayout(); Drain(); }
        }

        internal void WaitForFocus(GalleryItemViewModel tile)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(5);
            do
            {
                SettleLayout();
                if (Keyboard.FocusedElement is ListBoxItem focused && ReferenceEquals(focused.DataContext, tile)) return;
            } while (DateTime.UtcNow < until);
            Assert.Fail($"Keyboard focus did not reach card {tile.Id} through the product focus path.");
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                yield return child;
                foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
            }
        }

        internal MouseButtonEventArgs MouseNavigation(MouseButton button)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button)
                { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = Rows };
            Invoke(Window, "OnNavigationMouseDown", Rows, args);
            Drain();
            return args;
        }

        internal void Drain()
        {
            var frame = new DispatcherFrame();
            Window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        /// <summary>Waits for a queue operation while the window's dispatcher keeps running its continuations.</summary>
        internal T Wait<T>(Task<T> task)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(15);
            while (!task.IsCompleted && DateTime.UtcNow < until) Drain();
            Assert.True(task.IsCompleted, "The queue operation did not finish in 15 seconds.");
            return task.GetAwaiter().GetResult();
        }

        public void Dispose() { Window.CloseForExit(); _presenter.Dispose(); }
    }

    private sealed class NoOcr : IOcrService
    {
        public bool IsAvailable => false;
        public IReadOnlyList<string> SupportedLanguages => [];
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Input tests do not run OCR.");
    }

    private sealed class NoPrivacy : IPrivacyRedactionService
    {
        public bool IsAvailable => false;
        public Task<PrivacyRedactionResult> FindAsync(BitmapSource bitmap, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Input tests do not run privacy recognition.");
    }
}
