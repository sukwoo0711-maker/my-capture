using MyCapture.App.Themes;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
using MyCapture.App.Recording;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Gallery;

/// <summary>
/// The dedicated capture gallery: a grouped, searchable, virtualized grid over the queue.
/// </summary>
/// <remarks>
/// <para>
/// One reusable instance. Opened from the tray and from a second-launch activation; a normal
/// close hides it back to the tray rather than destroying it (the process is
/// <c>OnExplicitShutdown</c>), so the queue and any decoded thumbnails survive and reopening
/// is instant. An explicit application exit closes it for real.
/// </para>
/// <para>
/// The window is a thin shell: the queue rules live in <see cref="GalleryController"/>, the
/// presentation state in <see cref="GalleryViewModel"/>, and re-edit loading in
/// <see cref="GalleryReeditLoader"/>. The code-behind only wires input (search focus, per-card
/// buttons, keyboard) to those collaborators and runs commit on the same record.
/// </para>
/// </remarks>
internal sealed partial class GalleryWindow : Window
{
    public static RoutedCommand CompareCommand { get; } = new();
    private readonly GalleryViewModel _viewModel;
    private readonly GalleryController _controller;
    private readonly GalleryReeditLoader _reeditLoader;
    private readonly CaptureCommitService _commitService;
    private readonly CaptureQueue _queue;
    private readonly VideoLibraryService _videoLibrary;
    private readonly AppPaths _paths;
    private readonly ILoggerFactory _loggerFactory;
    private readonly GalleryDragExportService _dragExport;
    private readonly OcrResultPresenter _ocrPresenter;
    private readonly Func<OcrSettings> _ocrSettings;
    private readonly MyCapture.App.Ocr.OcrIndexingService _ocrIndexing;
    private readonly IPrivacyRedactionService _privacyRedactionService;
    private readonly ILogger _log;

    private bool _allowClose;
    // Header capture buttons hide the library without closing it, so the shown folder stays
    // the destination of the capture they start.
    private bool _hiddenForCapture;
    private int _visibilityGeneration;
    private bool _imagePreparationInProgress;
    private Point _dragStart;
    private GalleryItemViewModel? _dragTile;
    private bool _dragArmed;
    private bool _collapseOnRelease;
    private bool _dragPreparing;
    private bool _deleteInProgress;
    private readonly GalleryDragGesture _dragGesture = new();
    private CancellationTokenSource? _dragCancellation;
    private bool _ocrIndexingRunning;
    private CancellationTokenSource? _ocrIndexingCts;
    private readonly HashSet<Guid> _openEditors = [];
    private GalleryVideoPlayerWindow? _videoPlayer;

    internal GalleryWindow(
        GalleryViewModel viewModel,
        GalleryController controller,
        GalleryReeditLoader reeditLoader,
        CaptureCommitService commitService,
        CaptureQueue queue,
        VideoLibraryService videoLibrary,
        AppPaths paths,
        ILoggerFactory loggerFactory,
        OcrResultPresenter ocrPresenter,
        Func<OcrSettings> ocrSettings,
        MyCapture.App.Ocr.OcrIndexingService ocrIndexing,
        IPrivacyRedactionService privacyRedactionService,
        ILogger log)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _reeditLoader = reeditLoader ?? throw new ArgumentNullException(nameof(reeditLoader));
        _commitService = commitService ?? throw new ArgumentNullException(nameof(commitService));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _videoLibrary = videoLibrary ?? throw new ArgumentNullException(nameof(videoLibrary));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _dragExport = new GalleryDragExportService(_queue);
        _ocrPresenter = ocrPresenter ?? throw new ArgumentNullException(nameof(ocrPresenter));
        _ocrSettings = ocrSettings ?? throw new ArgumentNullException(nameof(ocrSettings));
        _ocrIndexing = ocrIndexing ?? throw new ArgumentNullException(nameof(ocrIndexing));
        _privacyRedactionService = privacyRedactionService
            ?? throw new ArgumentNullException(nameof(privacyRedactionService));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        InitializeComponent();
        CommandBindings.Add(new CommandBinding(CompareCommand, OnCompareExecuted, (_, e) =>
        {
            e.CanExecute = !_imagePreparationInProgress && _viewModel.CanCompareSelected;
            e.Handled = true;
        }));
        Loaded += (_, _) => UpdateCommandShortcuts();
        MyCapture.App.Themes.WorkspaceTheme.Attach(this, MyCapture.App.Themes.WorkspaceRole.Gallery, resourcesLoaded: true);
        _viewModel.EnableAsyncThumbnailLoading(Dispatcher);
        _viewModel.SetThumbnailLoadingEnabled(false);
        DataContext = _viewModel;
        InitializeFolderActions();
        PreviewMouseLeftButtonUp += OnDragButtonUp;
        PreviewMouseMove += OnTileMouseMove;
        LostMouseCapture += (_, _) => { if (Mouse.Captured != this) ResetDragGesture(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) ResetDragGesture(); };
        // Bubbling, so a tile's open caption hint or a menu consumes Escape first.
        KeyDown += OnLibraryKeyDown;
        InitializeActionStatus();
        InitializeRecovery();
        InitializeEmptyLibrary();
        IsVisibleChanged += (_, _) =>
        {
            _visibilityGeneration++;
            if (!IsVisible) { ResetDragGesture(); _importCancellation?.Cancel(); }
            else _hiddenForCapture = false;
            _viewModel.SetThumbnailLoadingEnabled(IsVisible);
            _viewModel.SetLibraryOpen(IsVisible || _hiddenForCapture);
        };

        // F2 edits the selected capture's searchable title/tags without altering pixels.
        InputBindings.Add(new KeyBinding(new RelayCommand(RenameSelectedFolderOrCapture),
            new KeyGesture(Key.F2)));
        // Ctrl+F focuses the search box from anywhere in the window.
        InputBindings.Add(new KeyBinding(
            new RelayCommand(FocusSearch),
            new KeyGesture(Key.F, ModifierKeys.Control)));
    }

    /// <summary>Raised when a re-edit commit finalises a capture, so the tray count can update.</summary>
    internal event EventHandler? CaptureChanged;

    /// <summary>Asks the shell to float a rendered library image as a pin window.</summary>
    internal event EventHandler<BitmapSource>? FloatRequested;

    /// <summary>Header toolbar commands, wired by the shell: region capture and region recording.</summary>
    internal Action? CaptureRegionRequested { get; set; }
    internal Action? RecordRegionRequested { get; set; }
    internal Func<HotkeySettings> ReadHotkeys { get; set; } = () => new HotkeySettings();

    internal void OpenCapture(Guid id, bool edit)
    {
        ShowGallery();
        // "Show in library" must find the capture even when another folder is open, and a filed
        // capture is listed only in its own folder while browsing.
        if (_controller.Find(id) is { } record && !_viewModel.ListsLocation(record))
            ShowScope(record.FolderId is { } folderId ? CaptureFolderScope.ForFolder(folderId) : CaptureFolderScope.All);
        _viewModel.Select(id);
        if (edit && _viewModel.FindTile(id) is { } tile) OpenReedit(tile);
    }

    /// <summary>
    /// Shows the window and brings it forward, rebuilding the view from the current queue so a
    /// capture taken while it was hidden appears.
    /// </summary>
    internal void ShowGallery()
    {
        UpdateCommandShortcuts();
        _viewModel.Refresh();
        RefreshOcrCoverageBanner();

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        _ = Activate();
        Topmost = true;
        Topmost = false;
        _ = Focus();
        ShowPendingRetentionNotice();
    }

    /// <summary>Refreshes an already-open gallery without stealing focus from the video editor.</summary>
    internal void RefreshFromQueue(Guid? changedRecordId = null)
    {
        if (changedRecordId is Guid id)
        {
            // A recording can open its editor before the user opens the gallery. If the gallery
            // already exists, its cached tile must discard the old first-frame bitmap when that
            // external editor commits a new rendered generation.
            _viewModel.FindTile(id)?.RefreshThumbnail();
        }

        _viewModel.Refresh();
        RefreshOcrCoverageBanner();
        ShowPendingRetentionNotice();
    }

    /// <summary>Closes the window for real, used only on an explicit application exit.</summary>
    internal void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // A normal close returns to the tray; only an explicit exit is allowed through.
        if (!_allowClose)
        {
            e.Cancel = true;
            CloseInlinePlayer();
            // Closing ends the folder session: new captures return to the date library unless
            // the saved keep-after-close option names a folder, and reopening starts at All.
            _hiddenForCapture = false;
            ShowScope(CaptureFolderScope.All);
            Hide();
            return;
        }

        CloseInlinePlayer();

        base.OnClosing(e);
    }

    // ---- Search --------------------------------------------------------------------

    private void FocusSearch()
    {
        _ = SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) =>
        _viewModel.SearchQuery = SearchBox.Text;

    private void OnRemoveSearchConditionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GallerySearchCondition condition }) return;
        _viewModel.RemoveSearchCondition(condition.Index);
        SearchBox.Text = _viewModel.SearchQuery;
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    private void OnClearSearchConditionsClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    // ---- OCR full-text search coverage -------------------------------------------------

    /// <summary>
    /// Updates the coverage banner: hidden when the whole library is already searchable and
    /// OCR is available; a "no language pack" advisory when the OS engine is missing; otherwise
    /// an "N captures not yet searchable ??index now" prompt. This is what turns full-text
    /// search from a per-capture manual action into a property of the whole history.
    /// </summary>
    private void RefreshOcrCoverageBanner()
    {
        if (_ocrIndexingRunning)
        {
            return; // The running pass owns the banner text until it finishes.
        }

        if (!_ocrIndexing.IsAvailable)
        {
            // No OS OCR language pack: show explicit guidance instead of a silent no-op,
            // and hide the index button since indexing cannot run.
            OcrAvailability unavailable = OcrAvailability.Describe(false, System.Array.Empty<string>());
            OcrCoverageHeadline.Text = unavailable.Headline;
            OcrCoverageDetail.Text = unavailable.Detail;
            OcrIndexButton.Visibility = Visibility.Collapsed;
            OcrCoverageBanner.Visibility = Visibility.Visible;
            return;
        }

        OcrCoverage coverage = _ocrIndexing.Coverage;
        if (coverage.IsComplete)
        {
            OcrCoverageBanner.Visibility = Visibility.Collapsed;
            return;
        }

        OcrCoverageHeadline.Text = UiText.Format("Text_BE08A8230E51", coverage.Missing);
        OcrCoverageDetail.Text = UiText.Format("Text_21AAACA9783A", coverage.Total, coverage.WithOcrText);
        OcrIndexButton.Visibility = Visibility.Visible;
        OcrIndexButton.IsEnabled = true;
        OcrCoverageBanner.Visibility = Visibility.Visible;
    }

    private async void OnOcrIndexClick(object sender, RoutedEventArgs e)
    {
        if (_ocrIndexingRunning)
        {
            // Second click cancels an in-progress pass.
            _ocrIndexingCts?.Cancel();
            return;
        }

        if (_queue.Records.Any(record => _commitService.IsRecordBusy(record.Id)))
        {
            ShowStatus(UiText.Get("Text_EC81AD6B5A75"));
            return;
        }

        _ocrIndexingRunning = true;
        _ocrIndexingCts = new System.Threading.CancellationTokenSource();
        OcrIndexButton.Content = UiText.Get("Text_92CDEF3C5BC0");
        OcrCoverageBanner.Visibility = Visibility.Visible;

        var progress = new System.Progress<MyCapture.App.Ocr.OcrIndexingProgress>(p =>
        {
            OcrCoverageHeadline.Text = UiText.Format("Text_C51C3FDBF019", p.Processed, p.Total);
            OcrCoverageDetail.Text = UiText.Format("Text_AAED78A6EA00", p.Indexed);
        });

        MyCapture.App.Ocr.OcrIndexingOutcome outcome;
        try
        {
            outcome = await _ocrIndexing.IndexMissingAsync(progress, _ocrIndexingCts.Token);
        }
        catch (System.Exception ex)
        {
            _log.LogWarning(ex, "OCR indexing pass failed");
            outcome = MyCapture.App.Ocr.OcrIndexingOutcome.Cancelled;
        }
        finally
        {
            _ocrIndexingCts.Dispose();
            _ocrIndexingCts = null;
            _ocrIndexingRunning = false;
            OcrIndexButton.Content = UiText.Get("Text_C5364F392040");
        }

        _log.LogInformation("OCR indexing pass ended: {Outcome}", outcome);

        // Newly indexed text is now searchable; refresh the grid and the banner.
        _viewModel.Refresh();
        RefreshOcrCoverageBanner();
    }

    // ---- Responsive columns --------------------------------------------------------

    private void OnRowsListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged)
        {
            return;
        }

        // The view model only rebuilds rows when the resulting column count actually changes,
        // so this fires cheaply on every resize.
        _ = _viewModel.SetColumnCountForWidth(RowsContentWidth(e.NewSize.Width));
    }

    // ---- Tile selection / keyboard -------------------------------------------------

    private void OnTileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.OfType<GalleryItemViewModel>().FirstOrDefault() is not { } tile)
        {
            return;
        }

        if (tile.IsVideo)
        {
            PrepareVideoPlayer(tile, autoPlay: false);
        }
        else
        {
            CloseInlinePlayer();
        }
    }

    private void OnTileMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_dragPreparing) { e.Handled = true; return; }
        ResetDragGesture();
        if (IsInsideButton(e.OriginalSource)
            || ResolveTileFromTree(e.OriginalSource) is not GalleryItemViewModel tile)
        {
            return;
        }

        _dragStart = e.GetPosition(this);
        _dragTile = tile;
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _collapseOnRelease = !control && !shift && tile.IsSelected;
        if (!_collapseOnRelease) _viewModel.Select(tile.Id, control, shift);
        if (sender is ListBox list && list.ItemContainerGenerator.ContainerFromItem(tile) is ListBoxItem item) item.Focus();
        e.Handled = true; // Inner single-row ListBox must never collapse the shared selection.
        if (!tile.IsSelected) return;
        if (e.ClickCount == 2)
        {
            _collapseOnRelease = false;
            if (tile.IsVideo) PrepareVideoPlayer(tile, autoPlay: true); else OpenReedit(tile);
            return;
        }
        if (!Mouse.Capture(this, CaptureMode.SubTree)) { ResetDragGesture(); return; }
        _dragGesture.Arm();
        _dragArmed = true;
    }

    private async void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || _dragPreparing)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ResetDragGesture();
            return;
        }

        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        CaptureRecord[] records = _viewModel.SelectedTiles.Select(t => t.Record).ToArray();
        long[] revisions = records.Select(r => r.ContentRevision).ToArray();
        if (records.Length == 0 || records.Any(r => !EnsureRecordReady(r.Id))) { ResetDragGesture(); return; }
        _collapseOnRelease = false;
        _dragPreparing = true;
        int generation = _dragGesture.Generation;
        var cancellation = new CancellationTokenSource();
        _dragCancellation = cancellation;
        e.Handled = true;
        try
        {
            using GalleryDragExportService.PreparedDrag prepared = await _dragExport.PrepareBatchAsync(records, cancellation.Token);
            if (!_dragGesture.CanStart(generation, Mouse.LeftButton == MouseButtonState.Pressed, IsVisible)
                || records.Where((r, i) => _controller.Find(r.Id) != r || r.ContentRevision != revisions[i]
                    || !EnsureRecordReady(r.Id)).Any()) return;
            // Release WPF capture before OLE takes ownership. Leases span the complete OLE loop.
            _dragArmed = false;
            if (Mouse.Captured == this) Mouse.Capture(null);
            prepared.MarkPublished();
            GalleryFolderDragPayload payload = CreateCaptureDragPayload(records.Select(record => record.Id));
            DataObject data = CreateCaptureDragData(prepared.Paths, payload);
            // Folders that can take the captures stand out for the whole drag.
            _viewModel.BeginCaptureDrag(payload.Captures);
            DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not stage selected captures for shell drag export");
            ShowStatus(UiText.Get("Text_51F5EF959148"));
        }
        finally
        {
            _dragPreparing = false;
            if (ReferenceEquals(_dragCancellation, cancellation)) _dragCancellation = null;
            cancellation.Dispose();
            ResetDragGesture();
            ClearDropFeedback();
        }
    }

    private void OnDragButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragArmed && !_dragPreparing && _dragTile is { } tile)
        {
            if (_collapseOnRelease) _viewModel.Select(tile.Id);
            if (_viewModel.SingleSelectedTile?.IsVideo == true) PrepareVideoPlayer(tile, autoPlay: false);
            else CloseInlinePlayer();
        }
        ResetDragGesture();
    }

    private void OnDayClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GalleryHeaderRow row })
            _viewModel.SelectDay(row.Heading, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        CloseInlinePlayer();
        e.Handled = true;
    }

    private void OnTileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResolveTile(e.OriginalSource) is GalleryItemViewModel tile)
        {
            if (tile.IsVideo)
            {
                PrepareVideoPlayer(tile, autoPlay: true);
            }
            else
            {
                OpenReedit(tile);
            }
            e.Handled = true;
        }
    }

    private async void OnTileKeyDown(object sender, KeyEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource)) return;
        GalleryItemViewModel? tile = ResolveTileFromTree(e.OriginalSource) ?? _viewModel.SingleSelectedTile;
        if (tile is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.A when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                _viewModel.SelectAllVisible();
                e.Handled = true;
                break;
            // Plain Up/Down can leave the tiles for the folder cards above them; range
            // selection with Ctrl or Shift stays on tiles.
            case Key.Up or Key.Down when Keyboard.Modifiers == ModifierKeys.None
                && GallerySelectionNavigation.FindVertical(_viewModel.Rows, tile,
                    e.Key == Key.Up ? -1 : 1) is GalleryFolderCardViewModel card:
                FocusFolderCard(card);
                e.Handled = true;
                break;
            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
                GalleryItemViewModel? next = GallerySelectionNavigation.FindNeighbor(_viewModel.Rows, tile,
                    e.Key is Key.Up or Key.Down, e.Key is Key.Left or Key.Up ? -1 : 1);
                if (next is not null)
                {
                    _viewModel.Select(next.Id, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                    GalleryTileRow? row = _viewModel.Rows.OfType<GalleryTileRow>().FirstOrDefault(r => r.Tiles.Contains(next));
                    if (row is not null)
                    {
                        RowsList.ScrollIntoView(row);
                        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => FocusTile(RowsList, next)));
                    }
                }
                e.Handled = true;
                break;
            case Key.Enter:
                if (tile.IsVideo)
                {
                    PrepareVideoPlayer(tile, autoPlay: true);
                }
                else
                {
                    OpenReedit(tile);
                }
                e.Handled = true;
                break;
            case Key.M when Keyboard.Modifiers == ModifierKeys.None:
                OnMoveCapturesClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Delete:
                ConfirmDeleteMany(_viewModel.Selection.SelectedIds, false);
                e.Handled = true;
                break;
            case Key.Space:
                _viewModel.Select(tile.Id, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
                break;
            case Key.P:
                TogglePin(tile);
                e.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                if (tile.IsImage)
                {
                    e.Handled = true;
                    await CopyRenderedAsync(tile);
                }
                break;
            case Key.T when tile.IsImage:
                RecognizeText(tile);
                e.Handled = true;
                break;
            case Key.G when tile.IsVideo:
                OpenVideoEditor(tile, exportGifWhenReady: true);
                e.Handled = true;
                break;
        }
    }

    // ---- Floating video player ------------------------------------------------------

    /// <summary>
    /// Opens (or refocuses) the floating video player for a video tile. Playback lives in
    /// its own window so it never duplicates the card's play affordance or overlaps the
    /// details panel.
    /// </summary>
    private void PrepareVideoPlayer(GalleryItemViewModel tile, bool autoPlay)
    {
        CaptureRecord? record = _controller.Find(tile.Id);
        if (record is null || !record.IsVideo || !EnsureRecordReady(record.Id))
        {
            return;
        }

        try
        {
            string path = Path.GetFullPath(_videoLibrary.CurrentVideoPath(record));
            if (_videoPlayer is not null && _videoPlayer.VideoId == tile.Id)
            {
                // Same video: just bring the player forward and honour autoplay.
                if (autoPlay && _videoPlayer.IsLoaded) { }
                _videoPlayer.Show();
                _videoPlayer.Activate();
                return;
            }

            CloseInlinePlayer();
            _videoPlayer = new GalleryVideoPlayerWindow();
            _videoPlayer.Closed += (_, _) => _videoPlayer = null;
            if (!_videoPlayer.Open(tile.Id, tile.ContextLabel, path, autoPlay))
            {
                ShowStatus(UiText.Get("Text_EC7FF66E22C9"));
                return;
            }

            // Anchor near the details panel's top-right, clamped to the work area.
            Rect work = SystemParameters.WorkArea;
            _videoPlayer.Left = Math.Max(work.Left, work.Right - _videoPlayer.Width - 24);
            _videoPlayer.Top = Math.Max(work.Top, work.Top + 90);
            _videoPlayer.Show();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open the video player for {Id}", record.Id);
            MyCapture.App.Themes.ThemedDialog.Error(this, UiText.Get("Dialog.AppFailure"), ex);
        }
    }

    private void CloseInlinePlayer()
    {
        _videoPlayer?.Close();
        _videoPlayer = null;
    }

    /// <summary>UX-review hook: selects a video tile the same way a user click would.</summary>
    internal void SelectVideoForReview(GalleryItemViewModel tile) => PrepareVideoPlayer(tile, autoPlay: false);

    // ---- Per-card buttons ----------------------------------------------------------

    /// <summary>
    /// A navigation pane view (all items, images, videos, kept) shows the whole library, so it
    /// also leaves an open folder or date, exactly like picking another item in a navigation view.
    /// </summary>
    private async void OnLibraryFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not RadioButton { Tag: string filter }) return;
        GalleryFilter next = Enum.Parse<GalleryFilter>(filter);
        if (_viewModel.Filter == next && _viewModel.SelectedScope.IsAll && _viewModel.SelectedTag is null) return;
        await ShowLibraryViewAsync(next);
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTile(sender) is GalleryItemViewModel tile)
        {
            OpenReedit(tile);
        }
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTile(sender) is GalleryItemViewModel tile)
        {
            TogglePin(tile);
        }
    }

    private void OnFloatClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTile(sender) is not GalleryItemViewModel tile || !tile.IsImage)
        {
            return;
        }

        e.Handled = true;
        if (!EnsureRecordReady(tile.Id))
        {
            return;
        }

        CaptureRecord? record = _controller.Find(tile.Id);
        if (record is null)
        {
            return;
        }

        string renderedPath = _queue.GetFilePath(record, CaptureFileNames.Rendered);
        BitmapSource? rendered = ImageCodec.TryLoad(renderedPath);
        if (rendered is null)
        {
            ShowStatus(UiText.Get("Gallery.FloatFailed"));
            return;
        }

        FloatRequested?.Invoke(this, rendered);
    }

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTile(sender) is GalleryItemViewModel tile)
        {
            e.Handled = true;
            await CopyRenderedAsync(tile);
        }
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        // The More button opens its own context menu on a normal left click. The menu's
        // DataContext is bound to the button's tile via PlacementTarget, and every item passes
        // the tile as CommandParameter, so the subsequent action resolves the correct record.
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void OnOcrMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTileFromCommand(sender) is GalleryItemViewModel tile)
        {
            RecognizeText(tile);
        }
    }

    private void OnGifMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTileFromCommand(sender) is GalleryItemViewModel tile)
        {
            OpenVideoEditor(tile, exportGifWhenReady: true);
        }
    }

    private void OnDeleteMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTileFromCommand(sender) is GalleryItemViewModel tile)
        {
            ConfirmAndDelete(tile);
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ResolveTile(sender) is GalleryItemViewModel tile)
        {
            ConfirmAndDelete(tile);
        }
    }

    private void OnPinMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTileFromCommand(sender) is GalleryItemViewModel tile)
        {
            TogglePin(tile);
        }
    }

    // ---- Actions -------------------------------------------------------------------

    private void TogglePin(GalleryItemViewModel tile)
    {
        if (!EnsureRecordReady(tile.Id))
        {
            return;
        }

        bool? pinned = _controller.TogglePin(tile.Id);
        if (pinned is null)
        {
            return;
        }

        tile.RaiseMetaChanged();
        _viewModel.Refresh();
        RaiseCaptureChanged();
    }

    private void ConfirmAndDelete(GalleryItemViewModel tile)
    {
        ConfirmDeleteMany([tile.Id], false);
    }

    private void OnDeleteSelectedClick(object sender, RoutedEventArgs e) => ConfirmDeleteMany(_viewModel.Selection.SelectedIds, false);
    private void OnClearLibraryClick(object sender, RoutedEventArgs e) => ConfirmDeleteMany(_controller.Records.Select(r => r.Id).ToArray(), true);

    /// <summary>Command bar shortcuts live in tooltips, read from the current hotkey settings.</summary>
    private void UpdateCommandShortcuts()
    {
        HotkeySettings hotkeys = ReadHotkeys();
        string capture = UiText.Format("LibraryUx_CommandShortcut", UiText.Get("Text_2C20ADDA2018").Trim(), hotkeys.Capture.ToString());
        string record = UiText.Format("LibraryUx_CommandShortcut", UiText.Get("Text_4F5AF20FAA6E").Trim(), hotkeys.RecordRegion.ToString());
        CaptureRegionButton.ToolTip = capture;
        CaptureRegionIconButton.ToolTip = capture;
        RecordRegionButton.ToolTip = record;
        RecordRegionIconButton.ToolTip = record;
    }

    private void OnClearSelectionClick(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearSelection();
        CloseInlinePlayer();
    }

    private void OnLibraryKeyDown(object sender, KeyEventArgs e)
    {
        // Escape clears the selection, which returns the command bar to the library commands.
        if (e.Handled || e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None
            || !_viewModel.HasSelection || e.OriginalSource is TextBox) return;
        _viewModel.ClearSelection();
        CloseInlinePlayer();
        e.Handled = true;
    }

    private async void OnMoreCommandsClick(object sender, RoutedEventArgs e)
    {
        ContactSheetMenu.IsEnabled = _viewModel.HasSelection;
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
            e.Handled = true;
            await PopulateDeletedCapturesAsync();
        }
    }

    // ---- Action status ---------------------------------------------------------------

    private static readonly System.ComponentModel.DependencyPropertyDescriptor StatusTextDescriptor =
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
    private MyCapture.App.Themes.FeedbackCountdown? _statusCountdown;
    private Button? _statusDismiss;
    private MyCapture.App.Themes.FeedbackKind _statusKind;

    // Status lines the command handlers write that the user has to act on; an export failure
    // starts with Folder410.ExportFailed and adds the cause.
    private static readonly string[] ActionRequiredStatusKeys =
    [
        "Folder410.OperationFailed", "Folder410.DeleteChanged", "Library.Archive.Failed",
        "Library.SourcePage.OpenFailed", "Library.SourcePage.CopyFailed",
        "Library.Source.OpenFolderFailed", "Library.Source.CopyPathFailed",
        "LibrarySelection_DeleteFailed",
    ];

    // Plain confirmations; every other line carries a count, a hint or a cause to read.
    private static readonly string[] ConfirmationStatusKeys = ["Library.SourcePage.Copied", "Library.Source.PathCopied"];

    // Export lines that the outcome replaces.
    private static readonly string[] ProgressStatusKeys =
    [
        "Library.Archive.Preparing", "Library.Archive.Progress", "Folder410.ExportPreparing", "Folder410.ExportProgress",
        "LibraryUndo_Restoring",
    ];

    private void InitializeActionStatus()
    {
        // Pointing at the status line or focusing its close button pauses the countdown.
        _statusCountdown = new MyCapture.App.Themes.FeedbackCountdown(Dispatcher,
            () => LibraryStatusBar.IsMouseOver || LibraryStatusBar.IsKeyboardFocusWithin, ExpireActionStatus);
        // A failure stays until closed, so it gets a close button beside it (column 2, which the
        // archive Cancel button only uses while an export is still running).
        var glyph = new SymbolIcon
        {
            Size = 16,
            Data = TryFindResource("Icon.Close") as Geometry ?? Geometry.Parse("M5,5 L15,15 M15,5 L5,15"),
            };
        glyph.SetResourceReference(SymbolIcon.StrokeProperty, "Text.Secondary");
        _statusDismiss = new Button
        {
            Content = glyph, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = UiText.Get("Feedback.Dismiss"),
        };
        _statusDismiss.SetResourceReference(StyleProperty, "Button.GhostCompact");
        System.Windows.Automation.AutomationProperties.SetName(_statusDismiss, UiText.Get("Feedback.Dismiss"));
        _statusDismiss.Click += (_, _) => DismissActionStatus();
        Grid.SetColumn(_statusDismiss, 3);
        LibraryStatusBar.Children.Add(_statusDismiss);
        // After the selection handler, so Esc still clears a selection first.
        KeyDown += (_, e) =>
        {
            if (e.Handled || e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None || e.OriginalSource is TextBox) return;
            e.Handled = DismissActionStatus();
        };
        StatusTextDescriptor.AddValueChanged(LibraryActionStatus, OnActionStatusChanged);
        Closed += (_, _) =>
        {
            StatusTextDescriptor.RemoveValueChanged(LibraryActionStatus, OnActionStatusChanged);
            _statusCountdown.Stop();
        };
    }

    /// <summary>Which <see cref="MyCapture.App.Themes.FeedbackTiming"/> kind a status line follows.</summary>
    internal static MyCapture.App.Themes.FeedbackKind ClassifyActionStatus(string text)
    {
        if (text == UiText.Get("LibraryRecovery_Failed") || ActionRequiredStatusKeys.Any(key => text == UiText.Get(key))
            || text.StartsWith(UiText.Get("Folder410.ExportFailed"), StringComparison.Ordinal)
            || MatchesFormat(text, "LibraryUndo_PartlyRestored"))
        {
            return MyCapture.App.Themes.FeedbackKind.ActionRequired;
        }

        if (ProgressStatusKeys.Any(key => MatchesFormat(text, key))) return MyCapture.App.Themes.FeedbackKind.Progress;
        return ConfirmationStatusKeys.Any(key => text == UiText.Get(key))
            ? MyCapture.App.Themes.FeedbackKind.Confirmation
            : MyCapture.App.Themes.FeedbackKind.Result;
    }

    private static bool MatchesFormat(string text, string key) => System.Text.RegularExpressions.Regex.IsMatch(text,
        "^" + string.Join(".+", System.Text.RegularExpressions.Regex.Split(UiText.Get(key), @"\{\d+[^}]*\}")
            .Select(System.Text.RegularExpressions.Regex.Escape)) + "$",
        System.Text.RegularExpressions.RegexOptions.Singleline);

    internal MyCapture.App.Themes.FeedbackKind ActionStatusKindForTest => _statusKind;

    internal TimeSpan? ActionStatusRemainingForTest => _statusCountdown?.Remaining;

    internal bool IsActionStatusDismissibleForTest => _statusDismiss?.Visibility == Visibility.Visible;

    /// <summary>Spends status display time as the countdown's clock would.</summary>
    internal void AdvanceActionStatusForTest(TimeSpan elapsed) => _statusCountdown?.Advance(elapsed);

    // Every result restarts the clock, so the latest one stays readable for its full time.
    private void OnActionStatusChanged(object? sender, EventArgs e)
    {
        string text = LibraryActionStatus.Text;
        SyncStatusAction();
        MyCapture.App.Themes.FeedbackKind previous = _statusKind;
        _statusKind = string.IsNullOrEmpty(text) ? MyCapture.App.Themes.FeedbackKind.Echo : ClassifyActionStatus(text);
        bool dismissible = !string.IsNullOrEmpty(text) && MyCapture.App.Themes.FeedbackTiming.StaysUntilDismissed(_statusKind);
        LibraryActionStatus.SetResourceReference(TextBlock.ForegroundProperty, dismissible ? "State.Danger" : "Text.Secondary");
        if (_statusDismiss is not null) _statusDismiss.Visibility = dismissible ? Visibility.Visible : Visibility.Collapsed;
        if (string.IsNullOrEmpty(text))
        {
            _statusCountdown?.Stop();
            return;
        }

        _statusCountdown?.Start(MyCapture.App.Themes.FeedbackTiming.DisplayTime(_statusKind));
        // Export counts change quickly; only the start of the export and its outcome are read out.
        if (_statusKind != MyCapture.App.Themes.FeedbackKind.Progress || previous != MyCapture.App.Themes.FeedbackKind.Progress)
        {
            MyCapture.App.Themes.FeedbackTiming.Announce(LibraryActionStatus, text, _statusKind, "MyCapture.Library.Status");
        }
    }

    /// <summary>Clears a finished action's result; a running export keeps its progress line.</summary>
    internal void ExpireActionStatus()
    {
        if (_archiveCancellation is not null)
        {
            _statusCountdown?.Start(MyCapture.App.Themes.FeedbackTiming.Result);
            return;
        }

        _statusCountdown?.Stop();
        LibraryActionStatus.Text = string.Empty;
    }

    /// <summary>Closes a failure line that stays until dismissed; false when none is showing.</summary>
    private bool DismissActionStatus()
    {
        if (string.IsNullOrEmpty(LibraryActionStatus.Text)
            || !MyCapture.App.Themes.FeedbackTiming.StaysUntilDismissed(_statusKind)) return false;
        bool refocus = _statusDismiss?.IsKeyboardFocused == true;
        LibraryActionStatus.Text = string.Empty;
        if (refocus) MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        return true;
    }

    /// <summary>Header toolbar: start a region capture, mirroring the tray command.</summary>
    private void OnCaptureRegionClick(object sender, RoutedEventArgs e)
    {
        _hiddenForCapture = true;
        Window.GetWindow(this)?.Hide();
        CaptureRegionRequested?.Invoke();
    }

    /// <summary>Header toolbar: start (or stop) a region recording, mirroring the tray command.</summary>
    private void OnRecordRegionClick(object sender, RoutedEventArgs e)
    {
        _hiddenForCapture = true;
        Window.GetWindow(this)?.Hide();
        RecordRegionRequested?.Invoke();
    }

    private async Task CopyRenderedAsync(GalleryItemViewModel tile)
    {
        if (!tile.IsImage)
        {
            return;
        }

        if (!EnsureRecordReady(tile.Id))
        {
            return;
        }

        CaptureRecord? record = _controller.Find(tile.Id);
        if (record is null)
        {
            return;
        }

        // Copy the flattened rendered.png so the clipboard carries the annotated result. This
        // does not modify the capture, matching the editor's Ctrl+C intent.
        string renderedPath = _queue.GetFilePath(record, CaptureFileNames.Rendered);
        BitmapSource? rendered = ImageCodec.TryLoad(renderedPath);
        if (rendered is null)
        {
            ShowStatus(UiText.Get("Text_3BF4FEA88C51"));
            return;
        }

        try
        {
            if (!await ClipboardImageService.CopyImageAsync(rendered, record.SourcePageUrl))
            {
                ShowStatus(UiText.Get("Text_75425A6D9BE8"));
            }
        }
        catch (Exception ex)
        {
            // async-void event handlers must never let a dispatcher shutdown or unexpected
            // clipboard provider failure escape into WPF's message pump.
            _log.LogWarning(ex, "Could not copy gallery capture {Id} to the clipboard", record.Id);
            ShowStatus(UiText.Get("Text_75425A6D9BE8"));
        }
    }

    /// <summary>
    /// Recognises text in the capture's rendered.png through the shared OCR presenter. A cached
    /// result opens instantly with an option to rerun; otherwise recognition runs asynchronously
    /// and, on success, is cached on the record (when the setting allows) which also refreshes
    /// the searchable haystack.
    /// </summary>
    private void RecognizeText(GalleryItemViewModel tile)
    {
        if (!tile.IsImage)
        {
            return;
        }

        if (!EnsureRecordReady(tile.Id))
        {
            return;
        }

        CaptureRecord? record = _controller.Find(tile.Id);
        if (record is null)
        {
            return;
        }

        OcrSettings settings = _ocrSettings();
        string renderedPath = _queue.GetFilePath(record, CaptureFileNames.Rendered);
        Guid id = record.Id;
        long requestedContentRevision = record.ContentRevision;
        string context = tile.ContextLabel;

        OcrRequest RequestFactory() => OcrRequestFactory.FromFile(renderedPath, settings);

        void OnFresh(OcrResult result)
        {
            // Cache and re-filter only when the setting allows and there is text to store.
            if (settings.CacheResults && result.Status == OcrStatus.Success)
            {
                if (_controller.CacheOcr(
                        id,
                        result.Text,
                        result.LanguageTag,
                        requestedContentRevision))
                {
                    tile.RaiseMetaChanged();
                    _viewModel.Refresh();
                }
            }
        }

        // A cached result opens immediately; the rerun button re-runs recognition and re-caches.
        if (record.HasOcrText)
        {
            var cached = OcrResult.Success(
                record.OcrText!,
                record.OcrLanguage ?? string.Empty,
                lines: [],
                elapsed: TimeSpan.Zero);
            _ocrPresenter.ShowCached(cached, context, RequestFactory, OnFresh);
            return;
        }

        _ocrPresenter.ShowRecognized(RequestFactory, context, OnFresh);
    }

    private async void OnCompareExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        e.Handled = true;
        GalleryItemViewModel[] selected = _viewModel.SelectedTiles.ToArray();
        if (_imagePreparationInProgress || selected.Length != 2 || selected.Any(tile => !tile.IsImage)) return;
        CaptureRecord? first = _controller.Find(selected[0].Id);
        CaptureRecord? second = _controller.Find(selected[1].Id);
        if (first is null || second is null || !EnsureRecordReady(first.Id) || !EnsureRecordReady(second.Id)) return;
        _imagePreparationInProgress = true;
        CommandManager.InvalidateRequerySuggested();
        int generation = _visibilityGeneration;
        long firstRevision = first.ContentRevision;
        long secondRevision = second.ContentRevision;
        string firstTitle = selected[0].ContextLabel;
        string secondTitle = selected[1].ContextLabel;
        try
        {
            string firstPath = _queue.GetFilePath(first, CaptureFileNames.Rendered);
            string secondPath = _queue.GetFilePath(second, CaptureFileNames.Rendered);
            var images = await Task.Run(() => (First: ImageCodec.TryLoad(firstPath), Second: ImageCodec.TryLoad(secondPath)));
            if (!IsVisible || generation != _visibilityGeneration) return;
            if (!ReferenceEquals(_controller.Find(first.Id), first) || !ReferenceEquals(_controller.Find(second.Id), second)
                || first.ContentRevision != firstRevision || second.ContentRevision != secondRevision
                || _commitService.IsRecordBeingWritten(first.Id) || _commitService.IsRecordBeingWritten(second.Id))
            {
                ShowStatus(UiText.Get("GalleryImageChanged"));
                return;
            }
            if (images.First is null || images.Second is null)
            {
                ShowStatus(UiText.Get("Gallery.Compare.LoadFailed"));
                return;
            }
            new GalleryCompareWindow(images.First, images.Second, firstTitle, first.CreatedAt, secondTitle, second.CreatedAt)
                { Owner = this }.Show();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not prepare capture comparison");
            if (IsVisible && generation == _visibilityGeneration) ShowStatus(UiText.Get("Gallery.Compare.LoadFailed"));
        }
        finally
        {
            _imagePreparationInProgress = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async void OnReducedExportClick(object sender, RoutedEventArgs e)
    {
        GalleryItemViewModel? tile = ResolveTileFromCommand(sender);
        CaptureRecord? record = tile is null ? null : _controller.Find(tile.Id);
        if (_imagePreparationInProgress || record is null || !record.IsImage || !EnsureRecordReady(record.Id)
            || !_openEditors.Add(record.Id)) return;
        _imagePreparationInProgress = true;
        int visibilityGeneration = _visibilityGeneration;
        try
        {
            using CaptureEditSession session = _commitService.BeginEditSession(record);
            string path = _queue.GetFilePath(record, CaptureFileNames.Rendered);
            BitmapSource? image = await Task.Run(() => ImageCodec.TryLoad(path));
            if (!IsVisible || visibilityGeneration != _visibilityGeneration) return;
            if (!ReferenceEquals(_controller.Find(record.Id), record) || _commitService.IsRecordBeingWritten(record.Id)
                || record.ContentRevision != session.ExpectedContentRevision)
            {
                ShowStatus(UiText.Get("GalleryImageChanged"));
                return;
            }
            if (image is null)
            {
                ShowStatus(UiText.Get("Text_BCA2A41F7456"));
                return;
            }
            _ = ImageReductionExportDialog.Show(this, image,
                Path.Combine(_paths.QuickSaveRoot, GalleryDragExportService.BuildBaseFileName(DateTimeOffset.Now)),
                record.SourcePageUrl, _commitService.LastExportOptions, _commitService.RememberExportOptions);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not prepare reduced export for {Id}", record.Id);
            if (IsVisible && visibilityGeneration == _visibilityGeneration)
                MyCapture.App.Themes.ThemedDialog.Error(this, UiText.Get("Dialog.AppFailure"), ex);
        }
        finally { _imagePreparationInProgress = false; _openEditors.Remove(record.Id); }
    }

    private async void OpenReedit(GalleryItemViewModel tile)
    {
        if (_imagePreparationInProgress) return;
        if (!EnsureRecordReady(tile.Id))
        {
            return;
        }

        CaptureRecord? record = _controller.Find(tile.Id);
        if (record is null)
        {
            return;
        }

        if (record.IsVideo)
        {
            OpenVideoEditor(tile, exportGifWhenReady: false);
            return;
        }

        if (!_openEditors.Add(record.Id))
        {
            ShowStatus(UiText.Get("Text_63F240FA482C"));
            return;
        }

        _imagePreparationInProgress = true;
        int visibilityGeneration = _visibilityGeneration;
        try
        {
            using CaptureEditSession editSession = _commitService.BeginEditSession(record);
            var loaded = await Task.Run(() =>
            {
                GalleryReeditContext? result = _reeditLoader.TryLoad(record, out GalleryReeditLoader.LoadFailure loadFailure);
                return (Context: result, Failure: loadFailure);
            });
            if (!IsVisible || visibilityGeneration != _visibilityGeneration) return;
            if (!ReferenceEquals(_controller.Find(record.Id), record) || _commitService.IsRecordBeingWritten(record.Id)
                || record.ContentRevision != editSession.ExpectedContentRevision)
            {
                ShowStatus(UiText.Get("GalleryImageChanged"));
                return;
            }
            GalleryReeditContext? context = loaded.Context;
            GalleryReeditLoader.LoadFailure failure = loaded.Failure;
            if (context is null)
            {
                ShowStatus(failure switch
                {
                    GalleryReeditLoader.LoadFailure.MissingOriginal => UiText.Get("Text_2618DA07C831"),
                    GalleryReeditLoader.LoadFailure.UndecodableOriginal => UiText.Get("Text_7C821231A673"),
                    _ => UiText.Get("Text_BCA2A41F7456"),
                });
                return;
            }

            var editor = new GalleryEditorWindow(context, _privacyRedactionService, record, () => _viewModel.FindTile(record.Id)?.RetentionHours() ?? CaptureRetention.DefaultImageRetentionHours) { Owner = this };
            editor.CommitRequested = result => CommitReeditAsync(record, result, editSession);
            editor.DraftRecordSource = () => (record.Id, editSession.ExpectedContentRevision);
            editor.Committed += (_, _) => OnReeditCommitted(tile.Id);
            _ = editor.ShowDialog();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open image editor for {Id}", record.Id);
            if (IsVisible && visibilityGeneration == _visibilityGeneration)
            {
                // The generic message alone gives a user nothing to report back; include
                // the concrete fault so a support conversation can start from evidence.
                MyCapture.App.Themes.ThemedDialog.Error(this, UiText.Get("Dialog.AppFailure"), ex);
            }
        }
        finally
        {
            _imagePreparationInProgress = false;
            _openEditors.Remove(record.Id);
        }
    }

    private async Task<bool> CommitReeditAsync(
        CaptureRecord record,
        AnnotationEditingResult result,
        CaptureEditSession editSession)
    {
        try
        {
            // Commit against the SAME record: the flattened rendered.png, the layer document
            // and any sidecars are rewritten in place, never a new capture.
            return await _commitService.CommitAsync(record, result, editSession);
        }
        catch (CaptureGenerationConflictException ex)
        {
            _log.LogWarning(ex, "Re-edit rejected because capture {Id} changed", record.Id);
            ShowStatus(UiText.Get("Text_884F73809F89"));
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Re-edit commit failed for {Id}", record.Id);
            ShowStatus(UiText.Get("Text_5F310F33AB12"));
            return false;
        }
    }

    private void OnReeditCommitted(Guid id)
    {
        // Refresh the affected tile's thumbnail/meta and the summary after files changed.
        GalleryItemViewModel? tile = _viewModel.FindTile(id);
        tile?.RefreshThumbnail();
        _viewModel.Refresh();
        RaiseCaptureChanged();
    }

    private void OpenVideoEditor(GalleryItemViewModel tile, bool exportGifWhenReady)
    {
        if (!EnsureRecordReady(tile.Id))
        {
            return;
        }

        CaptureRecord? record = _controller.Find(tile.Id);
        if (record is null || !record.IsVideo)
        {
            return;
        }

        CloseInlinePlayer();

        if (!_openEditors.Add(record.Id))
        {
            ShowStatus(UiText.Get("Text_733011719B1C"));
            return;
        }

        VideoEditSession? editSession = null;
        try
        {
            VideoEditSession activeSession = _videoLibrary.BeginEdit(record);
            editSession = activeSession;
            VideoLibraryItem item = _videoLibrary.Load(record);
            var editor = new VideoEditorWindow(item.Recording, _paths, _loggerFactory, item.EditDocument)
            {
                Owner = this,
                PrivacyRedactionService = _privacyRedactionService,
                ExportGifWhenReady = exportGifWhenReady,
                RenderStagingPathFactory = () => _videoLibrary.CreateRenderStagingPath(record),
                VideoCommitHandler = (document, stage, cancellationToken) =>
                    _videoLibrary.CommitEditAsync(
                        record,
                        activeSession,
                        document,
                        stage,
                        cancellationToken),
            };
            editor.VideoCommitted += (_, _) => OnReeditCommitted(record.Id);
            _ = editor.ShowDialog();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not open video editor for {Id}", record.Id);
            MyCapture.App.Themes.ThemedDialog.Error(this, UiText.Get("Dialog.AppFailure"), ex);
        }
        finally
        {
            editSession?.Dispose();
            _openEditors.Remove(record.Id);
        }
    }

    // ---- Helpers -------------------------------------------------------------------

    private bool EnsureRecordReady(Guid recordId)
    {
        if (!_commitService.IsRecordBusy(recordId) && !_videoLibrary.IsBusy(recordId))
        {
            return true;
        }

        ShowStatus(UiText.Get("Text_E20E28D53DCE"));
        return false;
    }

    private void ResetDragGesture()
    {
        _dragArmed = false;
        _dragTile = null;
        _collapseOnRelease = false;
        _dragGesture.Cancel();
        _dragCancellation?.Cancel();
        if (Mouse.Captured == this) Mouse.Capture(null);
    }

    private static bool IsInsideButton(object? source)
    {
        for (DependencyObject? current = source as DependencyObject;
             current is not null;
             current = GetParent(current))
        {
            if (current is ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    private static bool FocusTile(DependencyObject parent, GalleryItemViewModel tile)
    {
        if (parent is ListBoxItem { DataContext: GalleryItemViewModel candidate } item && candidate == tile)
            return item.Focus();
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FocusTile(VisualTreeHelper.GetChild(parent, i), tile)) return true;
        return false;
    }

    private static GalleryItemViewModel? ResolveTileFromTree(object? source)
    {
        for (DependencyObject? current = source as DependencyObject;
             current is not null;
             current = GetParent(current))
        {
            if (current is FrameworkElement { DataContext: GalleryItemViewModel tile })
            {
                return tile;
            }
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            return VisualTreeHelper.GetParent(child);
        }

        return LogicalTreeHelper.GetParent(child);
    }

    private static GalleryItemViewModel? ResolveTile(object? source) => source switch
    {
        FrameworkElement { DataContext: GalleryItemViewModel tile } => tile,
        _ => null,
    };

    // Overflow menu items live outside the tile's visual tree, so their DataContext resolution
    // is unreliable. Each item carries the tile as CommandParameter (the robust route); fall
    // back to the item's inherited DataContext only if that is somehow absent.
    private static GalleryItemViewModel? ResolveTileFromCommand(object? source) => source switch
    {
        MenuItem { CommandParameter: GalleryItemViewModel tile } => tile,
        FrameworkElement { DataContext: GalleryItemViewModel tile } => tile,
        _ => null,
    };

    private void ShowStatus(string message) =>
        MyCapture.App.Themes.ThemedDialog.Show(this, message, "MyCapture", MessageBoxButton.OK, MessageBoxImage.Information);

    private void RaiseCaptureChanged() => CaptureChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Minimal <see cref="ICommand"/> for the window's Ctrl+F input binding.
/// </summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    internal RelayCommand(Action execute) => _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
