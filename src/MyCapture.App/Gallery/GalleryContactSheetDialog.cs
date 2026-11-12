using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using MyCapture.App.Themes;
using MyCapture.Core.Export;
using MyCapture.Core.Queue;

namespace MyCapture.App.Gallery;

internal sealed class GalleryContactSheetDialog : Window
{
    private const int ThumbnailSize = 96;
    private static readonly GalleryThumbnailLoader ThumbnailLoader = new();
    private readonly IReadOnlyList<GalleryContactSheetItem> _items;
    private readonly Func<bool> _isCurrent;
    private readonly CultureInfo _culture = UiText.Culture;
    private readonly ComboBox _pageSize = new();
    private readonly ComboBox _layout = new();
    private readonly ComboBox _caption = new();
    private readonly CheckBox _source = new() { Margin = new Thickness(0, 12, 0, 12) };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, Margin = new Thickness(16) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 40 };
    private readonly ProgressBar _progress = new() { Height = 5, Minimum = 0, Visibility = Visibility.Collapsed };
    private readonly Button _save;
    private readonly Button _cancel;
    private readonly StackPanel _settings = new();
    private readonly ContactSheetOrder _order;
    private readonly ObservableCollection<GalleryContactSheetOrderRow> _rows = [];
    private readonly List<string> _sortKeys = [];
    private readonly CancellationTokenSource _thumbnailCancellation = new();
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _exportCancellation;
    private Task _pendingPreview = Task.CompletedTask;
    private bool _closed;
    private bool _syncingSort;
    private readonly LibraryActivityTracker? _activities;
    private int _generation;
    private GalleryContactSheetOrderRow? _dragRow;
    private Point _dragStart;
    private bool _dragging;

    internal GalleryContactSheetDialog(IReadOnlyList<GalleryContactSheetItem> items, int skippedVideos, Func<bool> isCurrent,
        LibraryActivityTracker? activities = null)
    {
        _items = items;
        _activities = activities;
        _isCurrent = isCurrent;
        _order = new ContactSheetOrder(items.Count);
        bool ordering = items.Count >= 2;
        StandardWindowTheme.Apply(this);
        Title = UiText.Get("ContactSheet.Action");
        // The order column sits between the options and the preview only when there is an order to choose.
        Width = ordering ? 1120 : 860; Height = 680; MinWidth = ordering ? 900 : 620; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Surface.Base");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        var root = new DockPanel { Margin = new Thickness(20) };
        _save = ThemedDialog.CreateActionButton(UiText.Get("ContactSheet.Save"), primary: true);
        _save.IsDefault = true;
        _save.IsEnabled = false;
        _cancel = ThemedDialog.CreateActionButton(UiText.Get("Library.Action.Cancel"), primary: false);
        _cancel.IsCancel = true;
        var footer = new StackPanel();
        _progress.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
        _progress.SetResourceReference(Control.BackgroundProperty, "Surface.Sunken");
        footer.Children.Add(_progress);
        footer.Children.Add(_status);
        footer.Children.Add(ThemedDialog.CreateActionRow(_save, _cancel));
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        if (ordering) body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        AddChoice(_settings, "ContactSheet.PageSize", _pageSize, ["ContactSheet.A4Portrait", "ContactSheet.A4Landscape", "ContactSheet.LetterPortrait"], 0);
        AddChoice(_settings, "ContactSheet.Layout", _layout, ["ContactSheet.OneByOne", "ContactSheet.OneByTwo", "ContactSheet.TwoByTwo", "ContactSheet.TwoByThree", "ContactSheet.ThreeByFour"], 2);
        AddChoice(_settings, "ContactSheet.Caption", _caption, ["ContactSheet.Title", "ContactSheet.CaptureTime", "ContactSheet.TitleAndCaptureTime", "ContactSheet.None"], 0);
        _source.Content = UiText.Get("ContactSheet.Source");
        AutomationProperties.SetName(_source, UiText.Get("ContactSheet.Source"));
        _settings.Children.Add(_source);
        if (skippedVideos > 0) _settings.Children.Add(KoreanWordWrap.Show(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
        }, UiText.Format("ContactSheet.SkippedVideos", skippedVideos)));
        body.Children.Add(new ScrollViewer { Content = _settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (ordering)
        {
            OrderPanel = CreateOrderPanel();
            Grid.SetColumn(OrderPanel, 1);
            body.Children.Add(OrderPanel);
        }
        var previewPanel = new DockPanel();
        var previewLabel = new TextBlock { Text = UiText.Get("ContactSheet.Preview"), Margin = new Thickness(16, 0, 0, 0) };
        DockPanel.SetDock(previewLabel, Dock.Top);
        previewPanel.Children.Add(previewLabel);
        previewPanel.Children.Add(_preview);
        Grid.SetColumn(previewPanel, ordering ? 2 : 1);
        body.Children.Add(previewPanel);
        root.Children.Add(body);
        Content = root;
        AutomationProperties.SetName(_preview, UiText.Get("ContactSheet.Preview"));
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_progress, UiText.Get("ContactSheet.Saving"));
        _pageSize.SelectionChanged += (_, _) => RequestPreview();
        _layout.SelectionChanged += (_, _) => RequestPreview();
        _caption.SelectionChanged += (_, _) => RequestPreview();
        _source.Checked += (_, _) => RequestPreview();
        _source.Unchecked += (_, _) => RequestPreview();
        Loaded += (_, _) => { RequestPreview(); _pageSize.Focus(); };
        _save.Click += async (_, _) => await SaveAsync();
        _cancel.Click += (_, _) => _exportCancellation?.Cancel();
        Closing += (_, e) =>
        {
            if (_exportCancellation is not null) { _exportCancellation.Cancel(); e.Cancel = true; }
        };
        Closed += (_, _) =>
        {
            _closed = true; _generation++; _previewCancellation?.Cancel(); _preview.Source = null;
            _thumbnailCancellation.Cancel();
        };
    }

    internal Task PendingPreview => _pendingPreview;

    internal ImageSource? PreviewImage => _preview.Source;

    /// <summary>The order column; absent when there is a single image.</summary>
    internal FrameworkElement? OrderPanel { get; }

    internal ListBox OrderList { get; } = new();

    internal ComboBox SortChoice { get; } = new();

    internal Button MoveUpButton { get; } = new();

    internal Button MoveDownButton { get; } = new();

    internal IReadOnlyList<GalleryContactSheetOrderRow> OrderRows => _rows;

    internal IReadOnlyList<string> SortKeys => _sortKeys;

    /// <summary>The images the PDF will hold, in page order.</summary>
    internal IReadOnlyList<GalleryContactSheetItem> OrderedItems() =>
        _order.IncludedItems().Select(item => _items[item]).ToArray();

    private FrameworkElement CreateOrderPanel()
    {
        var panel = new DockPanel { Margin = new Thickness(16, 0, 0, 0) };
        var top = new StackPanel();
        _sortKeys.AddRange(["ContactSheet.SortLibrary", "ContactSheet.SortOldest", "ContactSheet.SortNewest"]);
        // Title order is offered only when titles can tell the images apart.
        if (_items.Select(item => item.Title.Trim()).Where(title => title.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).Skip(1).Any())
            _sortKeys.Add("ContactSheet.SortTitle");
        _sortKeys.Add("ContactSheet.SortCustom");
        AddChoice(top, "ContactSheet.Sort", SortChoice, [.. _sortKeys], 0);
        var orderLabel = new Label { Content = UiText.Get("ContactSheet.Order"), Target = OrderList, Padding = new Thickness(0, 10, 0, 5) };
        orderLabel.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
        top.Children.Add(orderLabel);
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);

        Configure(MoveUpButton, "ContactSheet.MoveUp", "Alt+↑", "Alt+Up");
        Configure(MoveDownButton, "ContactSheet.MoveDown", "Alt+↓", "Alt+Down");
        MoveDownButton.Margin = new Thickness(8, 0, 0, 0);
        var moves = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        moves.Children.Add(MoveUpButton);
        moves.Children.Add(MoveDownButton);
        DockPanel.SetDock(moves, Dock.Bottom);
        panel.Children.Add(moves);

        for (int item = 0; item < _items.Count; item++)
        {
            GalleryContactSheetItem source = _items[item];
            string time = source.CreatedAt.ToLocalTime().ToString("g", _culture);
            string title = source.Title.Trim();
            _rows.Add(new GalleryContactSheetOrderRow(item, title.Length > 0 ? title : time, title.Length > 0 ? time : string.Empty,
                token => ThumbnailLoader.LoadAsync(() => ThumbnailPath(source), ThumbnailSize, token),
                _thumbnailCancellation.Token, OnInclusionChanged));
        }
        Renumber();
        OrderList.ItemsSource = _rows;
        OrderList.ItemTemplate = CreateRowTemplate();
        OrderList.SelectionMode = SelectionMode.Single;
        OrderList.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(OrderList, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(OrderList, true);
        VirtualizingPanel.SetVirtualizationMode(OrderList, VirtualizationMode.Recycling);
        AutomationProperties.SetName(OrderList, UiText.Get("ContactSheet.Order"));
        var itemStyle = new Style(typeof(ListBoxItem), TryFindResource(typeof(ListBoxItem)) as Style);
        itemStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new System.Windows.Data.Binding(nameof(GalleryContactSheetOrderRow.AutomationName))));
        OrderList.ItemContainerStyle = itemStyle;
        OrderList.SelectedIndex = 0;
        OrderList.SelectionChanged += (_, _) => UpdateMoveButtons();
        OrderList.PreviewKeyDown += OnOrderKeyDown;
        OrderList.PreviewMouseLeftButtonDown += OnOrderMouseDown;
        OrderList.PreviewMouseMove += OnOrderMouseMove;
        OrderList.PreviewMouseLeftButtonUp += (_, _) => EndDrag();
        OrderList.LostMouseCapture += (_, _) => EndDrag();
        var frame = new Border { Child = OrderList, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(2) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        panel.Children.Add(frame);

        MoveUpButton.Click += (_, _) => MoveSelected(-1);
        MoveDownButton.Click += (_, _) => MoveSelected(1);
        SortChoice.SelectionChanged += (_, _) => ApplySort();
        UpdateMoveButtons();
        return panel;

        void Configure(Button button, string key, string gesture, string acceleratorKey)
        {
            button.Content = UiText.Get(key);
            button.ToolTip = $"{UiText.Get(key)} ({gesture})";
            button.MinWidth = 88;
            button.MinHeight = 32;
            button.SetResourceReference(StyleProperty, "Button.Secondary");
            AutomationProperties.SetName(button, UiText.Get(key));
            AutomationProperties.SetAcceleratorKey(button, acceleratorKey);
        }
    }

    private static string ThumbnailPath(GalleryContactSheetItem item) =>
        item.ThumbnailPath is { Length: > 0 } thumbnail && File.Exists(thumbnail) ? thumbnail : item.ImagePath;

    // Templates belong to the thread that parsed them; each dialog parses its own.
    private static DataTemplate CreateRowTemplate() => (DataTemplate)XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Grid>
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="Auto" />
              <ColumnDefinition Width="Auto" />
              <ColumnDefinition Width="Auto" />
              <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            <CheckBox IsChecked="{Binding IsIncluded}" Focusable="False" VerticalAlignment="Center" Margin="0,0,6,0"
                      AutomationProperties.Name="{Binding IncludeName}" />
            <TextBlock Grid.Column="1" Text="{Binding Number}" MinWidth="24" TextAlignment="Right"
                       VerticalAlignment="Center" Margin="0,0,10,0" />
            <Border Grid.Column="2" Width="64" Height="40" Margin="0,0,10,0" Background="{DynamicResource Surface.Sunken}"
                    CornerRadius="3">
              <Image Source="{Binding Thumbnail}" Stretch="Uniform" />
            </Border>
            <StackPanel Grid.Column="3" VerticalAlignment="Center">
              <TextBlock Text="{Binding Title}" TextTrimming="CharacterEllipsis" />
              <TextBlock Text="{Binding Detail}" TextTrimming="CharacterEllipsis" Foreground="{DynamicResource Text.Secondary}">
                <TextBlock.Style>
                  <Style TargetType="TextBlock">
                    <Style.Triggers>
                      <DataTrigger Binding="{Binding Detail}" Value="">
                        <Setter Property="Visibility" Value="Collapsed" />
                      </DataTrigger>
                    </Style.Triggers>
                  </Style>
                </TextBlock.Style>
              </TextBlock>
            </StackPanel>
          </Grid>
        </DataTemplate>
        """);

    private static void AddChoice(Panel parent, string labelKey, ComboBox choice, string[] keys, int selected)
    {
        var label = new Label { Content = UiText.Get(labelKey), Target = choice, Padding = new Thickness(0, 10, 0, 5) };
        label.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
        parent.Children.Add(label);
        foreach (string key in keys) choice.Items.Add(UiText.Get(key));
        choice.SelectedIndex = selected;
        choice.MinHeight = 32;
        AutomationProperties.SetName(choice, UiText.Get(labelKey));
        parent.Children.Add(choice);
    }

    private ContactSheetOptions Options => new((ContactSheetPageSize)_pageSize.SelectedIndex,
        (ContactSheetGrid)_layout.SelectedIndex, (ContactSheetCaption)_caption.SelectedIndex, _source.IsChecked == true);

    /// <summary>Moves the row at <paramref name="from"/> to <paramref name="to"/> and keeps it selected.</summary>
    internal bool MoveRow(int from, int to)
    {
        if (_exportCancellation is not null || !_order.Move(from, to)) return false;
        bool focused = OrderList.IsKeyboardFocusWithin;
        GalleryContactSheetOrderRow row = _rows[from];
        _rows.Move(from, to);
        Renumber();
        OrderList.SelectedItem = row;
        OrderList.ScrollIntoView(row);
        if (focused) FocusRow(row);
        SyncSortChoice();
        UpdateMoveButtons();
        RequestPreview();
        return true;
    }

    private void MoveSelected(int offset)
    {
        int position = OrderList.SelectedIndex;
        if (position < 0) return;
        MoveRow(position, position + offset);
        // A button that the move disabled at either end hands focus back to the moved row.
        if (Keyboard.FocusedElement is Button { IsEnabled: false } && OrderList.SelectedItem is GalleryContactSheetOrderRow row)
            FocusRow(row);
    }

    private void FocusRow(GalleryContactSheetOrderRow row)
    {
        OrderList.UpdateLayout();
        if (OrderList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem container) container.Focus();
    }

    private void OnOrderKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleOrderKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers, e.OriginalSource)) e.Handled = true;
    }

    /// <summary>Alt+Up and Alt+Down move the selected row; Space on a row includes or leaves it out.</summary>
    internal bool HandleOrderKey(Key key, ModifierKeys modifiers, object? source)
    {
        if (modifiers == ModifierKeys.Alt && key is Key.Up or Key.Down)
        {
            MoveSelected(key == Key.Up ? -1 : 1);
            return true;
        }
        if (modifiers == ModifierKeys.None && key == Key.Space && source is ListBoxItem
            && OrderList.SelectedItem is GalleryContactSheetOrderRow row)
        {
            row.IsIncluded = !row.IsIncluded;
            return true;
        }
        return false;
    }

    private void OnOrderMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragRow = null;
        if (_exportCancellation is not null || e.OriginalSource is not DependencyObject source) return;
        // The include box keeps its own click; only the rest of the row picks the row up.
        for (DependencyObject? node = source; node is not null && node != OrderList; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ToggleButton) return;
            if (node is ListBoxItem { DataContext: GalleryContactSheetOrderRow row })
            {
                _dragRow = row;
                _dragStart = e.GetPosition(OrderList);
                return;
            }
        }
    }

    private void OnOrderMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragRow is null || e.LeftButton != MouseButtonState.Pressed) return;
        Point point = e.GetPosition(OrderList);
        if (!_dragging)
        {
            if (Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragging = true;
            OrderList.CaptureMouse();
        }
        // Near either edge the list scrolls so a long order can be crossed in one drag.
        if (FindScrollViewer(OrderList) is { } scroller)
        {
            if (point.Y < 16) scroller.LineUp();
            else if (point.Y > OrderList.ActualHeight - 16) scroller.LineDown();
        }
        DragRowTo(_dragRow, point);
        e.Handled = true;
    }

    private void EndDrag()
    {
        bool dragging = _dragging;
        _dragging = false;
        _dragRow = null;
        if (dragging && OrderList.IsMouseCaptured) OrderList.ReleaseMouseCapture();
    }

    /// <summary>Moves a dragged row to the row under <paramref name="point"/> (list coordinates).</summary>
    internal bool DragRowTo(GalleryContactSheetOrderRow row, Point point)
    {
        int target = DropIndex(point);
        int from = _rows.IndexOf(row);
        return target >= 0 && from >= 0 && MoveRow(from, target);
    }

    /// <summary>The position of the shown row under a point, or the nearest shown row above or below it.</summary>
    internal int DropIndex(Point point)
    {
        int nearest = -1;
        double nearestDistance = double.PositiveInfinity;
        for (int position = 0; position < _rows.Count; position++)
        {
            if (OrderList.ItemContainerGenerator.ContainerFromIndex(position) is not ListBoxItem { IsVisible: true } container) continue;
            Rect bounds = container.TransformToAncestor(OrderList).TransformBounds(new Rect(container.RenderSize));
            if (point.Y >= bounds.Top && point.Y < bounds.Bottom) return position;
            double distance = point.Y < bounds.Top ? bounds.Top - point.Y : point.Y - bounds.Bottom;
            if (distance < nearestDistance) { nearestDistance = distance; nearest = position; }
        }
        return nearest;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private void ApplySort()
    {
        if (_syncingSort || SortChoice.SelectedIndex < 0) return;
        var titles = new NaturalTitleComparer(_culture);
        switch (_sortKeys[SortChoice.SelectedIndex])
        {
            case "ContactSheet.SortLibrary": _order.Reset(); break;
            case "ContactSheet.SortOldest": _order.SortBy(item => _items[item].CreatedAt); break;
            case "ContactSheet.SortNewest": _order.SortBy(item => _items[item].CreatedAt, descending: true); break;
            // Untitled images follow the titled ones instead of leading the list.
            case "ContactSheet.SortTitle":
                _order.SortBy(item => _items[item].Title.Trim(), TitleComparer(titles));
                break;
            default: return; // Custom names the order the rows already have.
        }
        GalleryContactSheetOrderRow? selected = OrderList.SelectedItem as GalleryContactSheetOrderRow;
        for (int position = 0; position < _order.Count; position++)
        {
            int current = IndexOfItem(_order.Items[position], position);
            if (current != position) _rows.Move(current, position);
        }
        Renumber();
        if (selected is not null) { OrderList.SelectedItem = selected; OrderList.ScrollIntoView(selected); }
        SyncSortChoice();
        UpdateMoveButtons();
        RequestPreview();
    }

    private int IndexOfItem(int item, int start)
    {
        for (int index = start; index < _rows.Count; index++)
            if (_rows[index].Item == item) return index;
        throw new InvalidOperationException("The order list lost a row.");
    }

    /// <summary>Shows Library order when moves return to it, and Custom after any other hand move.</summary>
    private void SyncSortChoice()
    {
        string current = _sortKeys[Math.Max(0, SortChoice.SelectedIndex)];
        // A chosen sort stays named while the rows still follow it, even when it equals the library order.
        string wanted = SortStillHolds(current) ? current
            : _order.IsOriginalOrder ? "ContactSheet.SortLibrary" : "ContactSheet.SortCustom";
        _syncingSort = true;
        try { SortChoice.SelectedIndex = _sortKeys.IndexOf(wanted); }
        finally { _syncingSort = false; }
    }

    private bool SortStillHolds(string key)
    {
        if (key == "ContactSheet.SortLibrary") return _order.IsOriginalOrder;
        var probe = new ContactSheetOrder(_items.Count);
        var titles = new NaturalTitleComparer(_culture);
        switch (key)
        {
            case "ContactSheet.SortOldest": probe.SortBy(item => _items[item].CreatedAt); break;
            case "ContactSheet.SortNewest": probe.SortBy(item => _items[item].CreatedAt, descending: true); break;
            case "ContactSheet.SortTitle": probe.SortBy(item => _items[item].Title.Trim(), TitleComparer(titles)); break;
            default: return false;
        }
        return probe.Items.SequenceEqual(_order.Items);
    }

    private static IComparer<string> TitleComparer(NaturalTitleComparer titles) => Comparer<string>.Create((left, right) =>
        left.Length == 0 && right.Length == 0 ? 0 : left.Length == 0 ? 1 : right.Length == 0 ? -1 : titles.Compare(left, right));

    private void OnInclusionChanged(GalleryContactSheetOrderRow row)
    {
        _order.SetIncluded(row.Item, row.IsIncluded);
        Renumber();
        RequestPreview();
    }

    private void Renumber()
    {
        int number = 0;
        for (int position = 0; position < _rows.Count; position++)
        {
            GalleryContactSheetOrderRow row = _rows[position];
            row.SetPlace(position + 1, row.IsIncluded ? ++number : 0);
        }
    }

    private void UpdateMoveButtons()
    {
        int position = OrderList.SelectedIndex;
        bool idle = _exportCancellation is null;
        MoveUpButton.IsEnabled = idle && position > 0;
        MoveDownButton.IsEnabled = idle && position >= 0 && position < _rows.Count - 1;
    }

    private void RequestPreview()
    {
        if (!IsLoaded || _closed || _exportCancellation is not null) return;
        _previewCancellation?.Cancel();
        _pendingPreview = Task.WhenAll(_pendingPreview, UpdatePreviewAsync(++_generation, Options, OrderedItems()));
    }

    private async Task UpdatePreviewAsync(int generation, ContactSheetOptions options, IReadOnlyList<GalleryContactSheetItem> items)
    {
        using var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        _save.IsEnabled = false;
        _preview.Source = null;
        try
        {
            if (items.Count == 0 && _items.Count > 0)
            {
                KoreanWordWrap.Show(_status, UiText.Get("ContactSheet.NoneIncluded"));
                return;
            }
            int pages = new ContactSheetLayout(options).PageCount(items.Count);
            KoreanWordWrap.Show(_status, UiText.Get("ContactSheet.Preparing"));
            await Task.Delay(150, cancellation.Token);
            var preview = await GalleryContactSheetRenderer.RunAsync(
                () => GalleryContactSheetRenderer.Preview(items, options, _culture, cancellation.Token), cancellation.Token);
            if (_closed || generation != _generation) return;
            _preview.Source = preview;
            int excluded = _items.Count - items.Count;
            KoreanWordWrap.Show(_status, excluded > 0
                ? UiText.Format("ContactSheet.PageCountExcluded", items.Count, pages, excluded)
                : UiText.Format("ContactSheet.PageCount", items.Count, pages));
            _save.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (ArgumentOutOfRangeException)
        {
            if (!_closed && generation == _generation) KoreanWordWrap.Show(_status, UiText.Get("ContactSheet.Limit"));
        }
        catch (Exception ex) when (IsExportError(ex))
        {
            if (!_closed && generation == _generation) KoreanWordWrap.Show(_status, UiText.Get("ContactSheet.PreviewFailed"));
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
        }
    }

    /// <summary>Writes the PDF for <paramref name="items"/> in their given order. Runs on an STA thread.</summary>
    internal static void WritePdf(Stream output, IReadOnlyList<GalleryContactSheetItem> items, ContactSheetOptions options,
        CultureInfo culture, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var layout = new ContactSheetLayout(options);
        ContactSheetPdfWriter.Write(output, options, items.Count,
            index => GalleryContactSheetRenderer.EncodeCell(items[index], options,
                layout.Cell(index % layout.CellsPerPage), culture, cancellationToken), progress, cancellationToken);
    }

    private async Task SaveAsync()
    {
        if (_exportCancellation is not null || !_save.IsEnabled) return;
        var picker = new SaveFileDialog
        {
            Title = UiText.Get("ContactSheet.Save"), Filter = UiText.Get("ContactSheet.Filter"),
            DefaultExt = ".pdf", AddExtension = true, CheckPathExists = true, OverwritePrompt = false,
            FileName = $"MyCapture-{DateTime.Now:yyyyMMdd-HHmmss}.pdf",
        };
        if (picker.ShowDialog(this) != true) return;
        if (File.Exists(picker.FileName)) { KoreanWordWrap.Show(_status, UiText.Get("ContactSheet.Exists")); return; }
        ContactSheetOptions options = Options;
        IReadOnlyList<GalleryContactSheetItem> items = OrderedItems();
        if (items.Count == 0) return;
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        using var activity = _activities?.Start(LibraryActivityKind.Pdf, cancellation.Cancel, items.Count);
        _settings.IsEnabled = _save.IsEnabled = false;
        if (OrderPanel is not null) OrderPanel.IsEnabled = false;
        _progress.Maximum = items.Count;
        _progress.Value = 0;
        _progress.Visibility = Visibility.Visible;
        KoreanWordWrap.Show(_status, UiText.Get("ContactSheet.Saving"));
        string stage = Path.Combine(Path.GetDirectoryName(picker.FileName)!, $".MyCapture-{Guid.NewGuid():N}.pdf.partial");
        bool created = false;
        bool acceptingProgress = true;
        try
        {
            if (!_isCurrent()) throw new InvalidOperationException("A capture changed before export.");
            var progress = new Progress<int>(done =>
            {
                if (!acceptingProgress || !ReferenceEquals(_exportCancellation, cancellation)) return;
                activity?.Report(done, items.Count);
                _progress.Value = done;
                KoreanWordWrap.Show(_status, UiText.Format("ContactSheet.Progress", done, items.Count));
            });
            await GalleryContactSheetRenderer.RunAsync(() =>
            {
                using var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created = true;
                WritePdf(output, items, options, _culture, progress, cancellation.Token);
                output.Flush(flushToDisk: true);
                return true;
            }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_isCurrent()) throw new InvalidOperationException("A capture changed during export.");
            // Claim the destination only after a complete PDF exists; an existing file always survives.
            File.Move(stage, picker.FileName, overwrite: false);
            created = false;
            KoreanWordWrap.Show(_status, UiText.Get("ContactSheet.Saved"));
        }
        catch (OperationCanceledException) { KoreanWordWrap.Show(_status, UiText.Get("ContactSheet.Cancelled")); }
        catch (Exception ex) when (IsExportError(ex)) { KoreanWordWrap.Show(_status, UiText.Get("ContactSheet.Failed")); }
        finally
        {
            acceptingProgress = false;
            if (created)
            {
                try { await Task.Run(() => global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(stage)); }
                catch (Exception ex) when (IsExportError(ex))
                { KoreanWordWrap.Show(_status, UiText.Format("ContactSheet.StageRetained", stage)); }
            }
            _exportCancellation = null;
            _settings.IsEnabled = _save.IsEnabled = true;
            if (OrderPanel is not null) OrderPanel.IsEnabled = true;
            UpdateMoveButtons();
            _progress.Visibility = Visibility.Collapsed;
        }
    }

    private static bool IsExportError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or NotSupportedException or System.Runtime.InteropServices.COMException;
}
