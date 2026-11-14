using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.App.Gallery;
using MyCapture.App.Themes;
using MyCapture.Core.Queue;

namespace MyCapture.App.Editing;

/// <summary>
/// One library image the "insert image" picker offers. Paths are resolved lazily, on a worker,
/// because resolving a capture's directory checks the file system.
/// </summary>
internal sealed record LibraryImageEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Title,
    Func<string> ResolveThumbnailPath,
    Func<string?> ResolveImagePath);

/// <summary>The picker's outcome: library images to insert, or a request to browse for a file.</summary>
internal sealed record LibraryImagePick(IReadOnlyList<LibraryImageEntry> Entries, bool Browse);

/// <summary>Builds the picker's entries from the capture library.</summary>
internal static class LibraryImageSource
{
    /// <summary>
    /// The library's still images, newest first, without the capture being edited
    /// (<paramref name="editedOriginalPath"/> is that capture's original file) and without
    /// recordings. Cheap enough for the UI thread: only strings are touched here.
    /// </summary>
    internal static IReadOnlyList<LibraryImageEntry> Create(CaptureQueue queue, string? editedOriginalPath)
    {
        ArgumentNullException.ThrowIfNull(queue);
        string? editedDirectory = DirectoryOf(editedOriginalPath);
        var entries = new List<LibraryImageEntry>();
        foreach (CaptureRecord record in queue.Records.Where(record => record.IsImage)
            .OrderByDescending(record => record.CreatedAt).ThenBy(record => record.Id))
        {
            if (editedDirectory is not null && IsRecordDirectory(editedDirectory, record.RelativeDirectory)) continue;
            // Snapshot only what path resolution needs; never read a live mutable record on a worker.
            Guid id = record.Id;
            string relativeDirectory = record.RelativeDirectory;
            Func<CaptureRecord> snapshot = () => new CaptureRecord { Id = id, RelativeDirectory = relativeDirectory };
            entries.Add(new LibraryImageEntry(
                id,
                record.CreatedAt,
                record.Title,
                () => Path.Combine(queue.GetDirectory(snapshot()), CaptureFileNames.Thumbnail),
                () => FinalImagePath(queue.GetDirectory(snapshot()))));
        }

        return entries;
    }

    /// <summary>The picture as the library shows it: the edited result, else the original.</summary>
    internal static string? FinalImagePath(string captureDirectory)
    {
        string rendered = Path.Combine(captureDirectory, CaptureFileNames.Rendered);
        if (File.Exists(rendered)) return rendered;
        string original = Path.Combine(captureDirectory, CaptureFileNames.Original);
        return File.Exists(original) ? original : null;
    }

    private static string? DirectoryOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetDirectoryName(Path.GetFullPath(path))?.TrimEnd('\\', '/'); }
        catch (ArgumentException) { return null; }
    }

    private static bool IsRecordDirectory(string directory, string relativeDirectory)
    {
        string relative = relativeDirectory.Replace('/', '\\').Trim('\\');
        return relative.Length > 0
            && (directory.Equals(relative, StringComparison.OrdinalIgnoreCase)
                || directory.EndsWith("\\" + relative, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>One picker tile: its caption and the thumbnail, which exists only while the tile is realized.</summary>
internal sealed class LibraryImageTile : INotifyPropertyChanged
{
    /// <summary>Long edge of the decoded thumbnail, in pixels.</summary>
    internal const int ThumbnailLongEdge = 256;

    private ImageSource? _thumbnail;
    private CancellationTokenSource? _loading;

    internal LibraryImageTile(LibraryImageEntry entry)
    {
        Entry = entry;
        string date = entry.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Caption = date;
        AutomationName = string.IsNullOrWhiteSpace(entry.Title) ? date : $"{date}, {entry.Title.Trim()}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal LibraryImageEntry Entry { get; }

    public string Caption { get; }

    public string AutomationName { get; }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    /// <summary>Starts decoding the small thumbnail off the UI thread; a no-op while loaded or loading.</summary>
    internal async void BeginLoad(GalleryThumbnailLoader loader)
    {
        if (_loading is not null || _thumbnail is not null) return;
        var cts = _loading = new CancellationTokenSource();
        try
        {
            System.Windows.Media.Imaging.BitmapSource? bitmap =
                await loader.LoadAsync(Entry.ResolveThumbnailPath, ThumbnailLongEdge, cts.Token);
            if (!cts.IsCancellationRequested) Thumbnail = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            // A missing or damaged thumbnail leaves the placeholder; the picture can still be picked.
        }
    }

    /// <summary>Drops the thumbnail and stops a decode in flight, as the tile leaves the viewport.</summary>
    internal void EndLoad()
    {
        _loading?.Cancel();
        _loading = null;
        if (_thumbnail is not null) Thumbnail = null;
    }
}

/// <summary>
/// A scrolling wrap grid of fixed-size tiles that realizes only the rows in view (and one more
/// each side), so a large library opens with a handful of elements.
/// </summary>
internal sealed class TileGridPanel : VirtualizingPanel, IScrollInfo
{
    internal const double TileWidth = 148;
    internal const double TileHeight = 124;

    private Size _extent;
    private Size _viewport;
    private double _offset;
    private int _columns = 1;

    internal int Columns => _columns;

    internal int RealizedCount => InternalChildren.Count;

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        double width = double.IsInfinity(availableSize.Width) ? TileWidth * 4 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? TileHeight * 3 : availableSize.Height;
        _columns = Math.Max(1, (int)Math.Floor(width / TileWidth));
        int rows = (count + _columns - 1) / _columns;
        UpdateScrollInfo(new Size(width, height), new Size(width, rows * TileHeight));

        int firstRow = Math.Max(0, (int)Math.Floor(_offset / TileHeight) - 1);
        int lastRow = (int)Math.Floor((_offset + height - 1) / TileHeight) + 1;
        int first = Math.Min(count, firstRow * _columns);
        int last = Math.Min(count - 1, ((lastRow + 1) * _columns) - 1);

        // Touching the children creates the generator; it is null before.
        _ = InternalChildren;
        IItemContainerGenerator generator = ItemContainerGenerator;
        if (count > 0 && last >= first)
        {
            GeneratorPosition start = generator.GeneratorPositionFromIndex(first);
            int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
            {
                for (int index = first; index <= last; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out bool isNew);
                    if (isNew)
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }

                    child.Measure(new Size(TileWidth, TileHeight));
                }
            }
        }

        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            int index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < first || index > last)
            {
                generator.Remove(new GeneratorPosition(i, 0), 1);
                RemoveInternalChildRange(i, 1);
            }
        }

        return new Size(width, Math.Min(height, Math.Max(rows * TileHeight, 0)));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        IItemContainerGenerator generator = ItemContainerGenerator;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            int row = index / _columns;
            int column = index % _columns;
            InternalChildren[i].Arrange(new Rect(column * TileWidth, (row * TileHeight) - _offset, TileWidth, TileHeight));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        if (args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace
            or NotifyCollectionChangedAction.Move or NotifyCollectionChangedAction.Reset)
        {
            _offset = 0;
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                RemoveInternalChildRange(0, InternalChildren.Count);
            }
        }

        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        int row = Math.Max(0, index) / _columns;
        double top = row * TileHeight;
        if (top < _offset) SetVerticalOffset(top);
        else if (top + TileHeight > _offset + _viewport.Height) SetVerticalOffset(top + TileHeight - _viewport.Height);
    }

    private void UpdateScrollInfo(Size viewport, Size extent)
    {
        bool changed = viewport != _viewport || extent != _extent;
        _viewport = viewport;
        _extent = extent;
        double clamped = Math.Max(0, Math.Min(_offset, extent.Height - viewport.Height));
        if (clamped != _offset) { _offset = clamped; changed = true; }
        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    // ---- IScrollInfo ---------------------------------------------------------------

    public bool CanVerticallyScroll { get; set; } = true;

    public bool CanHorizontallyScroll { get; set; }

    public double ExtentWidth => _extent.Width;

    public double ExtentHeight => _extent.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => 0;

    public double VerticalOffset => _offset;

    public ScrollViewer? ScrollOwner { get; set; }

    public void SetVerticalOffset(double offset)
    {
        double next = Math.Max(0, Math.Min(offset, Math.Max(0, _extent.Height - _viewport.Height)));
        if (next == _offset) return;
        _offset = next;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void SetHorizontalOffset(double offset)
    {
    }

    public void LineUp() => SetVerticalOffset(_offset - 24);

    public void LineDown() => SetVerticalOffset(_offset + 24);

    public void LineLeft()
    {
    }

    public void LineRight()
    {
    }

    public void PageUp() => SetVerticalOffset(_offset - _viewport.Height);

    public void PageDown() => SetVerticalOffset(_offset + _viewport.Height);

    public void PageLeft()
    {
    }

    public void PageRight()
    {
    }

    public void MouseWheelUp() => SetVerticalOffset(_offset - TileHeight / 2);

    public void MouseWheelDown() => SetVerticalOffset(_offset + TileHeight / 2);

    public void MouseWheelLeft()
    {
    }

    public void MouseWheelRight()
    {
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            if (!ReferenceEquals(InternalChildren[i], visual) && !(visual is DependencyObject d && IsAncestor(InternalChildren[i], d)))
            {
                continue;
            }

            BringIndexIntoView(ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0)));
            break;
        }

        return rectangle;
    }

    private static bool IsAncestor(UIElement ancestor, DependencyObject node)
    {
        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }

        return false;
    }
}

/// <summary>
/// The "insert image" picker: the library's images as a thumbnail grid, newest first, with a
/// way out to the file dialog. The window only reports the choice; the editor inserts it.
/// </summary>
internal sealed class LibraryImagePickerDialog : Window
{
    private readonly ListBox _list = new();
    private readonly GalleryThumbnailLoader _loader;
    private readonly Button _insert;
    private int _anchor;

    internal LibraryImagePickerDialog(IReadOnlyList<LibraryImageEntry> entries, GalleryThumbnailLoader? loader = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _loader = loader ?? new GalleryThumbnailLoader();
        Tiles = [.. entries.Select(entry => new LibraryImageTile(entry))];
        Title = UiText.Get("ImagePicker_Title");
        AutomationProperties.SetName(this, Title);
        Width = 720;
        Height = 560;
        MinWidth = 520;
        MinHeight = 400;
        // Never larger than the work area, so the buttons stay reachable on a small screen.
        MaxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width - 40);
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 40);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(StyleProperty, "Window.Standard");
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");

        _insert = ThemedDialog.CreateActionButton(UiText.Get("ImagePicker_Insert"), primary: true);
        _insert.IsDefault = true;
        _insert.IsEnabled = false;
        _insert.Click += (_, _) => Accept();
        Button cancel = ThemedDialog.CreateActionButton(UiText.Get("Dialog.Cancel"), primary: false);
        cancel.IsCancel = true;
        BrowseButton = ThemedDialog.CreateActionButton(UiText.Get("ImagePicker_Browse"), primary: false);
        BrowseButton.Margin = new Thickness(0, 4, 0, 0);
        BrowseButton.Click += (_, _) =>
        {
            Result = new LibraryImagePick([], Browse: true);
            DialogResult = true;
        };
        CancelButton = cancel;
        InsertButton = _insert;

        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(BrowseButton, 0);
        footer.Children.Add(BrowseButton);
        WrapPanel actions = ThemedDialog.CreateActionRow(_insert, cancel);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);

        var content = new DockPanel { Margin = new Thickness(24) };
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(footer);
        content.Children.Add(Tiles.Count == 0 ? BuildEmptyState() : BuildList());
        Content = content;

        Loaded += (_, _) =>
        {
            if (Tiles.Count > 0 && _list.Items.Count > 0)
            {
                _list.Focus();
            }
            else
            {
                BrowseButton.Focus();
            }
        };
    }

    /// <summary>The tiles in display order.</summary>
    internal IReadOnlyList<LibraryImageTile> Tiles { get; }

    internal ListBox List => _list;

    internal Button InsertButton { get; }

    internal Button BrowseButton { get; }

    internal Button CancelButton { get; }

    internal LibraryImagePick? Result { get; private set; }

    /// <summary>Whether the empty-state message stands in for the grid.</summary>
    internal bool ShowsEmptyState => Tiles.Count == 0;

    private UIElement BuildEmptyState()
    {
        var message = new TextBlock
        {
            Text = UiText.Get("ImagePicker_Empty"),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = TypeScale.Body,
        };
        message.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        AutomationProperties.SetName(message, message.Text);
        return message;
    }

    private UIElement BuildList()
    {
        _list.SelectionMode = SelectionMode.Extended;
        _list.ItemsSource = Tiles;
        _list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(TileGridPanel)));
        _list.ItemTemplate = BuildTemplate();
        _list.ItemContainerStyle = BuildContainerStyle();
        _list.SetValue(ScrollViewer.CanContentScrollProperty, true);
        _list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _list.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(_list, UiText.Get("ImagePicker_ListName"));
        _list.SelectionChanged += OnSelectionChanged;
        _list.MouseDoubleClick += OnDoubleClick;
        _list.PreviewKeyDown += OnListKeyDown;
        return _list;
    }

    private Style BuildContainerStyle()
    {
        var style = new Style(typeof(ListBoxItem), TryFindResource(typeof(ListBoxItem)) as Style);
        style.Setters.Add(new Setter(AutomationProperties.NameProperty, new System.Windows.Data.Binding(nameof(LibraryImageTile.AutomationName))));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(2)));
        return style;
    }

    private DataTemplate BuildTemplate()
    {
        var root = new FrameworkElementFactory(typeof(StackPanel));

        var well = new FrameworkElementFactory(typeof(Border));
        well.SetValue(Border.HeightProperty, 84.0);
        well.SetResourceReference(Border.BackgroundProperty, "Surface.Sunken");
        well.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var image = new FrameworkElementFactory(typeof(Image));
        image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(LibraryImageTile.Thumbnail)));
        image.SetValue(Image.StretchProperty, Stretch.Uniform);
        image.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
        image.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            ((sender as FrameworkElement)?.DataContext as LibraryImageTile)?.BeginLoad(_loader)));
        image.AddHandler(FrameworkElement.UnloadedEvent, new RoutedEventHandler((sender, _) =>
            ((sender as FrameworkElement)?.DataContext as LibraryImageTile)?.EndLoad()));
        well.AppendChild(image);
        root.AppendChild(well);

        var caption = new FrameworkElementFactory(typeof(TextBlock));
        caption.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(LibraryImageTile.Caption)));
        caption.SetValue(TextBlock.FontSizeProperty, TypeScale.Caption);
        caption.SetValue(TextBlock.MarginProperty, new Thickness(0, 4, 0, 0));
        caption.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        root.AppendChild(caption);
        return new DataTemplate { VisualTree = root };
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(_list, source) is ListBoxItem)
        {
            Accept();
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _insert.IsEnabled = _list.SelectedItems.Count > 0;
        if (_keyboardSelecting || e.AddedItems.Count == 0) return;
        // A click or Ctrl-click moves the cursor and, unless it extends a range, the anchor.
        _cursor = _list.Items.IndexOf(e.AddedItems[^1]);
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _anchor = _cursor;
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        int count = _list.Items.Count;
        if (count == 0) return;
        int columns = Math.Max(1, FindPanel()?.Columns ?? 1);
        int current = _cursor >= 0 && _cursor < count ? _cursor : -1;
        int? target = e.Key switch
        {
            Key.Left => Math.Max(0, current - 1),
            Key.Right => Math.Min(count - 1, current + 1),
            Key.Up => current < 0 ? 0 : Math.Max(0, current - columns),
            Key.Down => current < 0 ? 0 : Math.Min(count - 1, current + columns),
            Key.Home => 0,
            Key.End => count - 1,
            _ => null,
        };
        if (target is not { } index)
        {
            if (e.Key == Key.Enter && _list.SelectedItems.Count > 0)
            {
                e.Handled = true;
                Accept();
            }

            return;
        }

        e.Handled = true;
        _keyboardSelecting = true;
        try
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                int from = Math.Max(0, Math.Min(_anchor, count - 1));
                _list.SelectedItems.Clear();
                for (int i = Math.Min(from, index); i <= Math.Max(from, index); i++) _list.SelectedItems.Add(_list.Items[i]);
            }
            else if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                _list.SelectedItems.Clear();
                _list.SelectedItems.Add(_list.Items[index]);
                _anchor = index;
            }
        }
        finally
        {
            _keyboardSelecting = false;
        }

        _cursor = index;
        _list.ScrollIntoView(_list.Items[index]);
        FocusTile(index);
        if (_list.ItemContainerGenerator.ContainerFromIndex(index) is null)
        {
            _list.Dispatcher.BeginInvoke(() => FocusTile(index), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void FocusTile(int index)
    {
        if (_list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem container) container.Focus();
    }

    private int _cursor = -1;
    private bool _keyboardSelecting;

    private TileGridPanel? FindPanel() => FindChild<TileGridPanel>(_list);

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }

        return null;
    }

    internal void Accept()
    {
        if (_list.SelectedItems.Count == 0) return;
        // Pick order is the library's order (newest first), whichever way the tiles were clicked.
        LibraryImageEntry[] picked = [.. Tiles.Where(tile => _list.SelectedItems.Contains(tile)).Select(tile => tile.Entry)];
        Result = new LibraryImagePick(picked, Browse: false);
        DialogResult = true;
    }
}
