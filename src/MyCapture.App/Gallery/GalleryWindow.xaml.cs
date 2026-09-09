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
    private readonly DispatcherTimer _inlinePlaybackTimer;
    private Guid? _inlineVideoId;
    private string? _inlineVideoPath;
    private bool _inlineMediaReady;
    private bool _inlinePlaying;
    private bool _inlineSeekUpdating;
    private bool _inlineAutoPlayPending;

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
        _viewModel.EnableAsyncThumbnailLoading(Dispatcher);
        _viewModel.SetThumbnailLoadingEnabled(false);
        DataContext = _viewModel;
        PreviewMouseLeftButtonUp += OnDragButtonUp;
        PreviewMouseMove += OnTileMouseMove;
        LostMouseCapture += (_, _) => { if (Mouse.Captured != this) ResetDragGesture(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) ResetDragGesture(); };
        IsVisibleChanged += (_, _) =>
        {
            _visibilityGeneration++;
            if (!IsVisible) ResetDragGesture();
            _viewModel.SetThumbnailLoadingEnabled(IsVisible);
        };
        _inlinePlaybackTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _inlinePlaybackTimer.Tick += OnInlinePlaybackTick;

        // Ctrl+F focuses the search box from anywhere in the window.
        InputBindings.Add(new KeyBinding(
            new RelayCommand(FocusSearch),
            new KeyGesture(Key.F, ModifierKeys.Control)));
    }

    /// <summary>Raised when a re-edit commit finalises a capture, so the tray count can update.</summary>
    internal event EventHandler? CaptureChanged;

    /// <summary>
    /// Shows the window and brings it forward, rebuilding the view from the current queue so a
    /// capture taken while it was hidden appears.
    /// </summary>
    internal void ShowGallery()
    {
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
            Hide();
            return;
        }

        CloseInlinePlayer();
        _inlinePlaybackTimer.Tick -= OnInlinePlaybackTick;

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

    // ---- OCR full-text search coverage -------------------------------------------------

    /// <summary>
    /// Updates the coverage banner: hidden when the whole library is already searchable and
    /// OCR is available; a "no language pack" advisory when the OS engine is missing; otherwise
    /// an "N captures not yet searchable — index now" prompt. This is what turns full-text
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

        // Subtract the vertical scrollbar's width so a row's tiles never overflow into the
        // scrollbar track. The view model only rebuilds rows when the resulting column count
        // actually changes, so this fires cheaply on every resize.
        double available = e.NewSize.Width - SystemParameters.VerticalScrollBarWidth;
        _ = _viewModel.SetColumnCountForWidth(available);
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
            PrepareInlineVideo(tile, autoPlay: false);
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
            if (tile.IsVideo) PrepareInlineVideo(tile, autoPlay: true); else OpenReedit(tile);
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
            DragDrop.DoDragDrop(this, GalleryDragExportService.CreateFileDropData(prepared.Paths), DragDropEffects.Copy);
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
        }
    }

    private void OnDragButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragArmed && !_dragPreparing && _dragTile is { } tile)
        {
            if (_collapseOnRelease) _viewModel.Select(tile.Id);
            if (_viewModel.SingleSelectedTile?.IsVideo == true) PrepareInlineVideo(tile, autoPlay: false);
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
                PrepareInlineVideo(tile, autoPlay: true);
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
                _viewModel.Selection.SelectGroup(_viewModel.Groups.SelectMany(g => g.Items).Select(t => t.Id), false);
                _viewModel.Refresh();
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
                    PrepareInlineVideo(tile, autoPlay: true);
                }
                else
                {
                    OpenReedit(tile);
                }
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

    // ---- In-library video playback ------------------------------------------------

    private void PrepareInlineVideo(GalleryItemViewModel tile, bool autoPlay)
    {
        CaptureRecord? record = _controller.Find(tile.Id);
        if (record is null || !record.IsVideo || !EnsureRecordReady(record.Id))
        {
            return;
        }

        try
        {
            string path = Path.GetFullPath(_videoLibrary.CurrentVideoPath(record));
            if (!File.Exists(path))
            {
                ShowStatus(UiText.Get("Text_EC7FF66E22C9"));
                return;
            }

            InlineVideoPanel.Visibility = Visibility.Visible;
            InlineVideoTitle.Text = tile.ContextLabel;
            if (_inlineVideoId == tile.Id
                && string.Equals(_inlineVideoPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _inlineAutoPlayPending |= autoPlay;
                if (autoPlay && _inlineMediaReady)
                {
                    SetInlinePlayback(playing: true);
                }

                return;
            }

            _inlineAutoPlayPending = autoPlay;
            InlineVideo.Stop();
            InlineVideo.Source = new Uri(path, UriKind.Absolute);
            _inlineVideoId = tile.Id;
            _inlineVideoPath = path;
            _inlineMediaReady = false;
            _inlinePlaying = false;
            InlinePlayButton.Content = UiText.Get("Text_D43A776C5E28");
            InlinePlaybackStatus.Text = UiText.Get("Text_AE614DABA128");
            InlineSeekSlider.Maximum = Math.Max(1, record.DurationMs);
            SetInlineSliderValue(0);
            InlineTimeLabel.Text = $"00:00 / {FormatPlaybackTime(record.DurationMs)}";

            // Manual MediaElement playback needs one Play call to begin opening on every
            // Windows media stack. MediaOpened decides whether to remain playing or pause.
            InlineVideo.Play();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open inline video {Id}", record.Id);
            InlinePlaybackStatus.Text = UiText.Get("Text_B6AEB06C1E3E");
            ShowStatus(UiText.Get("Text_EBC9F14582AD") + ex.Message);
        }
    }

    private void OnInlineMediaOpened(object sender, RoutedEventArgs e)
    {
        _inlineMediaReady = true;
        double durationMs = InlineVideo.NaturalDuration.HasTimeSpan
            ? InlineVideo.NaturalDuration.TimeSpan.TotalMilliseconds
            : Math.Max(1, InlineSeekSlider.Maximum);
        InlineSeekSlider.Maximum = Math.Max(1, durationMs);
        InlinePlaybackStatus.Text = UiText.Get("Text_6E86D1A85620");
        if (_inlineAutoPlayPending)
        {
            SetInlinePlayback(playing: true);
        }
        else
        {
            InlineVideo.Pause();
            InlineVideo.Position = TimeSpan.Zero;
            SetInlineSliderValue(0);
            UpdateInlineTimeLabel();
        }

        _inlineAutoPlayPending = false;
    }

    private void OnInlineMediaEnded(object sender, RoutedEventArgs e)
    {
        InlineVideo.Pause();
        InlineVideo.Position = TimeSpan.Zero;
        _inlinePlaying = false;
        _inlinePlaybackTimer.Stop();
        InlinePlayButton.Content = UiText.Get("Text_D43A776C5E28");
        InlinePlaybackStatus.Text = UiText.Get("Text_2223CE0FF051");
        SetInlineSliderValue(0);
        UpdateInlineTimeLabel();
    }

    private void OnInlineMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _inlineMediaReady = false;
        _inlinePlaying = false;
        _inlineAutoPlayPending = false;
        _inlinePlaybackTimer.Stop();
        InlinePlayButton.Content = UiText.Get("Text_D43A776C5E28");
        InlinePlaybackStatus.Text = UiText.Get("Text_875917AFD6EF");
        _log.LogWarning(e.ErrorException, "Inline gallery playback failed for {Path}", _inlineVideoPath);
    }

    private void OnInlinePlayClick(object sender, RoutedEventArgs e)
    {
        if (_inlineMediaReady)
        {
            SetInlinePlayback(!_inlinePlaying);
        }
    }

    private void SetInlinePlayback(bool playing)
    {
        if (!_inlineMediaReady)
        {
            return;
        }

        if (playing)
        {
            if (InlineVideo.Position.TotalMilliseconds >= InlineSeekSlider.Maximum - 1)
            {
                InlineVideo.Position = TimeSpan.Zero;
            }

            InlineVideo.Play();
            _inlinePlaying = true;
            _inlinePlaybackTimer.Start();
            InlinePlayButton.Content = UiText.Get("Text_4F51C0C8ADA8");
            InlinePlaybackStatus.Text = UiText.Get("Text_8B5C97A7F917");
        }
        else
        {
            InlineVideo.Pause();
            _inlinePlaying = false;
            _inlinePlaybackTimer.Stop();
            InlinePlayButton.Content = UiText.Get("Text_D43A776C5E28");
            InlinePlaybackStatus.Text = UiText.Get("Text_4F51C0C8ADA8");
            UpdateInlinePlaybackPosition();
        }
    }

    private void OnInlinePlaybackTick(object? sender, EventArgs e) =>
        UpdateInlinePlaybackPosition();

    private void UpdateInlinePlaybackPosition()
    {
        if (!_inlineMediaReady)
        {
            return;
        }

        SetInlineSliderValue(Math.Clamp(
            InlineVideo.Position.TotalMilliseconds,
            0,
            InlineSeekSlider.Maximum));
        UpdateInlineTimeLabel();
    }

    private void OnInlineSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_inlineSeekUpdating || !_inlineMediaReady)
        {
            return;
        }

        InlineVideo.Position = TimeSpan.FromMilliseconds(Math.Clamp(
            e.NewValue,
            0,
            InlineSeekSlider.Maximum));
        UpdateInlineTimeLabel();
    }

    private void SetInlineSliderValue(double value)
    {
        _inlineSeekUpdating = true;
        try
        {
            InlineSeekSlider.Value = value;
        }
        finally
        {
            _inlineSeekUpdating = false;
        }
    }

    private void UpdateInlineTimeLabel() =>
        InlineTimeLabel.Text = $"{FormatPlaybackTime(InlineVideo.Position.TotalMilliseconds)} / {FormatPlaybackTime(InlineSeekSlider.Maximum)}";

    private static string FormatPlaybackTime(double milliseconds)
    {
        TimeSpan value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1
            ? value.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnInlineCloseClick(object sender, RoutedEventArgs e) => CloseInlinePlayer();

    private void CloseInlinePlayer()
    {
        _inlinePlaybackTimer.Stop();
        _inlinePlaying = false;
        _inlineMediaReady = false;
        _inlineAutoPlayPending = false;
        _inlineVideoId = null;
        _inlineVideoPath = null;
        try
        {
            InlineVideo.Close();
            InlineVideo.Source = null;
        }
        catch (InvalidOperationException)
        {
        }

        InlineVideoPanel.Visibility = Visibility.Collapsed;
        InlinePlayButton.Content = UiText.Get("Text_D43A776C5E28");
        InlinePlaybackStatus.Text = UiText.Get("Text_6E86D1A85620");
        SetInlineSliderValue(0);
    }

    // ---- Per-card buttons ----------------------------------------------------------

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

    private async void ConfirmDeleteMany(IEnumerable<Guid> ids, bool entireLibrary)
    {
        if (_deleteInProgress) return;
        Guid[] targets = ids.Distinct().ToArray();
        if (targets.Length == 0) return;
        _deleteInProgress = true;
        try
        {
            string prompt = UiText.Format(entireLibrary ? "LibrarySelection_ClearConfirm" : "LibrarySelection_DeleteConfirm", targets.Length);
            if (MessageBox.Show(this, prompt, UiText.Get("LibrarySelection_Delete"), MessageBoxButton.OKCancel,
                MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            CloseInlinePlayer(); // MediaElement must release its source before directory cleanup.
            foreach (Guid id in targets) _viewModel.FindTile(id)?.SetThumbnailLoadingEnabled(false);
            CaptureBatchRemovalResult result = await _queue.RemoveManyAsync(targets,
                id => !_commitService.IsRecordBusy(id) && !_videoLibrary.IsBusy(id));
            string status = UiText.Format("LibrarySelection_Removed", result.RemovedCount, targets.Length - result.RemovedCount);
            if (result.CleanupPendingCount > 0) status += "\n" + UiText.Format("LibrarySelection_CleanupPending", result.CleanupPendingCount);
            ShowStatus(status);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not durably remove selected library entries");
            ShowStatus(UiText.Get("LibrarySelection_DeleteFailed"));
        }
        finally
        {
            _deleteInProgress = false;
            _viewModel.Refresh();
            _viewModel.SetThumbnailLoadingEnabled(IsVisible);
            RaiseCaptureChanged();
        }
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
            if (!await ClipboardImageService.CopyImageAsync(rendered))
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

        OcrRequest RequestFactory() => OcrRequest.FromFile(
            renderedPath, settings.UpscaleFactor, settings.PreferredLanguages);

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
            if (_controller.Find(record.Id) is null || _commitService.IsRecordBusy(record.Id)
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
                Path.Combine(_paths.QuickSaveRoot, GalleryDragExportService.BuildBaseFileName(DateTimeOffset.Now)));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not prepare reduced export for {Id}", record.Id);
            if (IsVisible && visibilityGeneration == _visibilityGeneration)
                ShowStatus(UiText.Format("ExportReduction_Error", ex.Message));
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
            if (_controller.Find(record.Id) is null || _commitService.IsRecordBusy(record.Id)
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

            var editor = new GalleryEditorWindow(context, _privacyRedactionService) { Owner = this };
            editor.CommitRequested = result => CommitReeditAsync(record, result, editSession);
            editor.Committed += (_, _) => OnReeditCommitted(tile.Id);
            _ = editor.ShowDialog();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open image editor for {Id}", record.Id);
            if (IsVisible && visibilityGeneration == _visibilityGeneration)
                ShowStatus(UiText.Get("Text_BCA2A41F7456"));
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
            ShowStatus(UiText.Get("Text_FB0A09B025D3") + ex.Message);
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
        MessageBox.Show(this, message, "MyCapture", MessageBoxButton.OK, MessageBoxImage.Information);

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
