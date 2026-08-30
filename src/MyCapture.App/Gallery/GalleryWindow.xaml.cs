using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using MyCapture.App.Editing;
using MyCapture.App.Ocr;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
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
    private readonly GalleryDragExportService _dragExport;
    private readonly OcrResultPresenter _ocrPresenter;
    private readonly Func<OcrSettings> _ocrSettings;
    private readonly ILogger _log;

    private bool _allowClose;
    private Point _dragStart;
    private GalleryItemViewModel? _dragTile;
    private bool _dragArmed;

    internal GalleryWindow(
        GalleryViewModel viewModel,
        GalleryController controller,
        GalleryReeditLoader reeditLoader,
        CaptureCommitService commitService,
        CaptureQueue queue,
        OcrResultPresenter ocrPresenter,
        Func<OcrSettings> ocrSettings,
        ILogger log)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _reeditLoader = reeditLoader ?? throw new ArgumentNullException(nameof(reeditLoader));
        _commitService = commitService ?? throw new ArgumentNullException(nameof(commitService));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _dragExport = new GalleryDragExportService(_queue);
        _ocrPresenter = ocrPresenter ?? throw new ArgumentNullException(nameof(ocrPresenter));
        _ocrSettings = ocrSettings ?? throw new ArgumentNullException(nameof(ocrSettings));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        InitializeComponent();
        DataContext = _viewModel;

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
            Hide();
            return;
        }

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
        // Selection is used only for keyboard focus/target resolution; nothing to do here,
        // but keeping the handler lets the container light up its selected visual.
    }

    private void OnTileMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ResetDragGesture();
        if (IsInsideButton(e.OriginalSource)
            || ResolveTileFromTree(e.OriginalSource) is not GalleryItemViewModel tile)
        {
            return;
        }

        _dragStart = e.GetPosition(this);
        _dragTile = tile;
        _dragArmed = true;
    }

    private void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed)
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

        GalleryItemViewModel? tile = _dragTile;
        ResetDragGesture();
        CaptureRecord? record = tile is null ? null : _controller.Find(tile.Id);
        if (record is null)
        {
            return;
        }

        try
        {
            DependencyObject source = sender as DependencyObject ?? this;
            _ = _dragExport.BeginDrag(source, record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not stage capture {Id} for shell drag export", record.Id);
            ShowStatus("이미지 파일을 준비할 수 없습니다. 잠시 후 다시 시도해 주세요.");
        }

        e.Handled = true;
    }

    private void OnTileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResolveTile(e.OriginalSource) is GalleryItemViewModel tile)
        {
            OpenReedit(tile);
            e.Handled = true;
        }
    }

    private void OnTileKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox list || list.SelectedItem is not GalleryItemViewModel tile)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                OpenReedit(tile);
                e.Handled = true;
                break;
            case Key.Delete:
                ConfirmAndDelete(tile);
                e.Handled = true;
                break;
            case Key.Space:
            case Key.P:
                TogglePin(tile);
                e.Handled = true;
                break;
            case Key.C when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                CopyRendered(tile);
                e.Handled = true;
                break;
            case Key.T:
                RecognizeText(tile);
                e.Handled = true;
                break;
        }
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

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTile(sender) is GalleryItemViewModel tile)
        {
            CopyRendered(tile);
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

    private void OnDeleteMenuClick(object sender, RoutedEventArgs e)
    {
        if (ResolveTileFromCommand(sender) is GalleryItemViewModel tile)
        {
            ConfirmAndDelete(tile);
        }
    }

    // ---- Actions -------------------------------------------------------------------

    private void TogglePin(GalleryItemViewModel tile)
    {
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
        MessageBoxResult answer = MessageBox.Show(
            this,
            $"이 캡처를 삭제할까요?\n\n{tile.ContextLabel}\n삭제하면 되돌릴 수 없습니다.",
            "캡처 삭제",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        if (_controller.Delete(tile.Id))
        {
            _viewModel.Refresh();
            RaiseCaptureChanged();
        }
    }

    private void CopyRendered(GalleryItemViewModel tile)
    {
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
            ShowStatus("이미지를 복사할 수 없습니다. 파일이 없거나 손상되었습니다.");
            return;
        }

        if (!ClipboardImageService.CopyImage(rendered))
        {
            ShowStatus("클립보드에 복사하지 못했습니다. 잠시 후 다시 시도해 주세요.");
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
        CaptureRecord? record = _controller.Find(tile.Id);
        if (record is null)
        {
            return;
        }

        OcrSettings settings = _ocrSettings();
        string renderedPath = _queue.GetFilePath(record, CaptureFileNames.Rendered);
        Guid id = record.Id;
        string context = tile.ContextLabel;

        OcrRequest RequestFactory() => OcrRequest.FromFile(
            renderedPath, settings.UpscaleFactor, settings.PreferredLanguages);

        void OnFresh(OcrResult result)
        {
            // Cache and re-filter only when the setting allows and there is text to store.
            if (settings.CacheResults && result.Status == OcrStatus.Success)
            {
                _controller.CacheOcr(id, result.Text, result.LanguageTag);
                tile.RaiseMetaChanged();
                _viewModel.Refresh();
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

    private void OpenReedit(GalleryItemViewModel tile)
    {
        CaptureRecord? record = _controller.Find(tile.Id);
        if (record is null)
        {
            return;
        }

        GalleryReeditContext? context = _reeditLoader.TryLoad(record, out GalleryReeditLoader.LoadFailure failure);
        if (context is null)
        {
            ShowStatus(failure switch
            {
                GalleryReeditLoader.LoadFailure.MissingOriginal => "원본 이미지를 찾을 수 없어 편집할 수 없습니다.",
                GalleryReeditLoader.LoadFailure.UndecodableOriginal => "원본 이미지가 손상되어 편집할 수 없습니다.",
                _ => "이 캡처를 편집할 수 없습니다.",
            });
            return;
        }

        var editor = new GalleryEditorWindow(context) { Owner = this };
        editor.CommitRequested = result => CommitReedit(record, result);
        editor.Committed += (_, _) => OnReeditCommitted(tile.Id);
        _ = editor.ShowDialog();
    }

    private bool CommitReedit(CaptureRecord record, AnnotationEditingResult result)
    {
        try
        {
            // Commit against the SAME record: the flattened rendered.png, the layer document
            // and any sidecars are rewritten in place, never a new capture.
            return _commitService.Commit(record, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Re-edit commit failed for {Id}", record.Id);
            ShowStatus("저장에 실패했습니다. 다시 시도해 주세요.");
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

    // ---- Helpers -------------------------------------------------------------------

    private void ResetDragGesture()
    {
        _dragArmed = false;
        _dragTile = null;
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
