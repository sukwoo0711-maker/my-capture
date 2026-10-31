using MyCapture.App.Themes;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;

namespace MyCapture.App.Pinning;

/// <summary>A selectable projection of a pin's retained text; copying stays with the pin.</summary>
internal sealed class PinTextSelectionView : Grid
{
    private readonly string _original;
    private readonly bool _isCode;
    private readonly int[] _characterStarts = [];
    private readonly int[] _characterEnds = [];
    private readonly PinTableSelection? _table;
    private readonly TextBlock? _tableLimit;
    private readonly DispatcherTimer? _dragScrollTimer;
    private int _anchorRow;
    private int _anchorColumn;
    private int _activeRow;
    private int _activeColumn;
    private bool _hasCellSelection;
    private bool _dragSelecting;
    private Point _dragPoint;
    private double _zoom = 1;
    private long _wheelDelta;

    internal PinTextSelectionView(PinContent content)
    {
        _original = content.OriginalText ?? throw new ArgumentException("The pin has no original text.", nameof(content));
        _isCode = content.TextStyle == PinTextStyle.Code;
        Color background = SampleBackground(content.Image);
        var backgroundBrush = new SolidColorBrush(background);
        backgroundBrush.Freeze();
        Brush foreground = Luminance(background) > 0.179 ? Brushes.Black : Brushes.White;
        Background = backgroundBrush;
        ClipToBounds = true;
        AutomationProperties.SetName(this, UiText.Get("Pin_SelectText"));
        CommandManager.AddPreviewCanExecuteHandler(this, OnPreviewCanExecute);
        CommandManager.AddPreviewExecutedHandler(this, OnPreviewExecuted);

        if (content.Kind == PinContentKind.Table)
        {
            _table = new PinTableSelection(_original);
            TableGrid = new DataGrid
            {
                Style = null,
                Background = backgroundBrush,
                Foreground = foreground,
                RowBackground = backgroundBrush,
                AlternatingRowBackground = backgroundBrush,
                BorderThickness = new Thickness(0),
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserReorderColumns = false,
                CanUserResizeRows = false,
                CanUserSortColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.None,
                SelectionUnit = DataGridSelectionUnit.Cell,
                SelectionMode = DataGridSelectionMode.Extended,
                ClipboardCopyMode = DataGridClipboardCopyMode.None,
                EnableRowVirtualization = true,
                EnableColumnVirtualization = true,
                FontFamily = new FontFamily("Segoe UI"),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            VirtualizingPanel.SetVirtualizationMode(TableGrid, VirtualizationMode.Recycling);
            AutomationProperties.SetName(TableGrid, UiText.Get("Pin_SelectText"));
            BindPinContextMenu(TableGrid);
            var rows = _table.Rows.Select(row => Enumerable.Range(0, _table.ColumnCount)
                .Select(column => column < row.Count ? row[column] : string.Empty).ToArray()).ToArray();
            for (int column = 0; column < _table.ColumnCount; column++)
            {
                TableGrid.Columns.Add(new DataGridTextColumn
                {
                    DisplayIndex = column,
                    Binding = new Binding($"[{column}]"),
                    Width = DataGridLength.SizeToCells,
                    IsReadOnly = true,
                });
            }
            TableGrid.ItemsSource = rows;
            TableGrid.PreviewMouseLeftButtonDown += OnTableMouseDown;
            TableGrid.PreviewMouseMove += OnTableMouseMove;
            TableGrid.PreviewMouseLeftButtonUp += OnTableMouseUp;
            TableGrid.LostMouseCapture += (_, _) => StopDragSelection();
            TableGrid.PreviewKeyDown += (_, e) =>
            {
                if (HandleTableNavigationKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
            };
            _dragScrollTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(80),
            };
            _dragScrollTimer.Tick += (_, _) => ExtendSelectionTo(_dragPoint);
            Unloaded += (_, _) => StopDragSelection();
            Children.Add(TableGrid);
            if (_table.IsTruncated)
            {
                RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _tableLimit = new TextBlock
                {
                    Text = UiText.Format("Pin_SelectTextTableLimit", PinTableSelection.MaxRows, PinTableSelection.MaxColumns),
                    Foreground = foreground,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                _tableLimit.ToolTip = _tableLimit.Text;
                AutomationProperties.SetHelpText(_tableLimit, _tableLimit.Text);
                SetRow(_tableLimit, 1);
                Children.Add(_tableLimit);
            }
        }
        else if (content.IsMarkdown)
        {
            MarkdownBox = new RichTextBox(ClipboardMarkdownRenderer.CreateDocument(_original, content.MermaidResults))
            {
                Style = null,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                IsDocumentEnabled = false,
                Background = Brushes.White,
                Foreground = Brushes.Black,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                SelectionBrush = SystemColors.HighlightBrush,
                SelectionTextBrush = SystemColors.HighlightTextBrush,
                SelectionOpacity = 1,
                IsUndoEnabled = false,
            };
            AutomationProperties.SetName(MarkdownBox, UiText.Get("Pin_SelectText"));
            BindPinContextMenu(MarkdownBox);
            Children.Add(MarkdownBox);
        }
        else
        {
            (string display, _characterStarts, _characterEnds) = CreateDisplayText(_original);
            TextBox = new TextBox
            {
                Style = null,
                Text = display,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                Background = backgroundBrush,
                Foreground = foreground,
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily(_isCode ? ClipboardCodeRenderer.FontFamilyName
                    : _original.IndexOfAny(['\r', '\n', '\t']) >= 0 ? "Consolas" : "Segoe UI"),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                SelectionBrush = SystemColors.HighlightBrush,
                SelectionTextBrush = SystemColors.HighlightTextBrush,
                SelectionOpacity = 1,
                IsUndoEnabled = false,
            };
            AutomationProperties.SetName(TextBox, UiText.Get("Pin_SelectText"));
            BindPinContextMenu(TextBox);
            Children.Add(TextBox);
        }
        SetZoom(1);
    }

    internal TextBox? TextBox { get; }
    internal RichTextBox? MarkdownBox { get; }
    internal DataGrid? TableGrid { get; }

    internal string? SelectedOriginalText
    {
        get
        {
            if (_table is not null) return _table.SelectedText;
            if (MarkdownBox is { } markdown)
            {
                if (markdown.Selection.IsEmpty) return null;
                // RichTextBox.SelectAll normalizes its endpoints to text insertion positions,
                // inside the first/last Paragraph rather than the outer document boundaries.
                var first = markdown.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
                var last = markdown.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
                if (markdown.Selection.Start.CompareTo(first) <= 0
                    && markdown.Selection.End.CompareTo(last) >= 0) return _original;
                return markdown.Selection.Text;
            }
            if (TextBox is not { SelectionLength: > 0 } textBox) return null;
            int start = Math.Min(textBox.SelectionStart, _characterStarts.Length);
            int end = Math.Min(textBox.SelectionStart + textBox.SelectionLength, _characterEnds.Length);
            if (start >= end) return null;
            return _original[_characterStarts[start].._characterEnds[end - 1]];
        }
    }

    internal void FocusSelection()
    {
        if (TextBox is not null) TextBox.Focus();
        else if (MarkdownBox is not null) MarkdownBox.Focus();
        else TableGrid?.Focus();
    }

    internal void SetZoom(double zoom)
    {
        _zoom = double.IsFinite(zoom) && zoom > 0 ? zoom : 1;
        if (MarkdownBox is not null) MarkdownBox.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        if (TextBox is not null)
        {
            double paddingX = _isCode ? ClipboardCodeRenderer.HorizontalPaddingDip : 24;
            double paddingY = _isCode ? ClipboardCodeRenderer.VerticalPaddingDip : 20;
            TextBox.FontSize = (_isCode ? ClipboardCodeRenderer.FontSizeDip : TypeScale.Content.PlainText) * _zoom;
            TextBox.Padding = new Thickness(paddingX * _zoom, paddingY * _zoom, paddingX * _zoom, paddingY * _zoom);
            TextBlock.SetLineHeight(TextBox, (_isCode ? ClipboardCodeRenderer.LineHeightDip : 24) * _zoom);
        }
        if (TableGrid is not null)
        {
            TableGrid.FontSize = TypeScale.Content.TableText * _zoom;
            TableGrid.Margin = new Thickness(16 * _zoom);
            TableGrid.RowHeight = 34 * _zoom;
            TableGrid.MinColumnWidth = 0;
            TableGrid.MaxColumnWidth = 320 * _zoom;
            TableGrid.MinColumnWidth = 72 * _zoom;
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(12 * _zoom, 7 * _zoom, 12 * _zoom, 7 * _zoom)));
            foreach (DataGridTextColumn column in TableGrid.Columns) column.ElementStyle = textStyle;
        }
        if (_tableLimit is not null)
        {
            _tableLimit.FontSize = TypeScale.Caption * _zoom;
            _tableLimit.Margin = new Thickness(4 * _zoom, 0, 4 * _zoom, 4 * _zoom);
        }
    }

    internal bool TryScroll(int delta)
    {
        if (delta == 0) return false;
        _wheelDelta += delta;
        long notches = _wheelDelta / 120;
        _wheelDelta %= 120;
        if (notches == 0) return false;
        ScrollViewer? scroll = FindDescendant<ScrollViewer>(TextBox as DependencyObject ?? (DependencyObject?)MarkdownBox ?? TableGrid);
        if (scroll is null) return false;
        double direction = -notches;
        if ((direction < 0 && scroll.VerticalOffset > 0)
            || (direction > 0 && scroll.VerticalOffset < scroll.ScrollableHeight))
        {
            double distance = scroll.CanContentScroll ? 3 : 72 * _zoom;
            scroll.ScrollToVerticalOffset(Math.Clamp(scroll.VerticalOffset + direction * distance, 0, scroll.ScrollableHeight));
            return true;
        }
        if ((direction < 0 && scroll.HorizontalOffset > 0)
            || (direction > 0 && scroll.HorizontalOffset < scroll.ScrollableWidth))
        {
            scroll.ScrollToHorizontalOffset(Math.Clamp(scroll.HorizontalOffset + direction * 72 * _zoom, 0, scroll.ScrollableWidth));
            return true;
        }
        return false;
    }

    internal void SelectAll()
    {
        if (_table is not null && TableGrid is not null)
        {
            SelectCells(0, 0, _table.Rows.Count - 1, _table.ColumnCount - 1);
            _table.SelectAll();
        }
        else if (MarkdownBox is not null) MarkdownBox.SelectAll();
        else TextBox?.SelectAll();
    }

    internal void SelectCells(int row, int column, int endRow, int endColumn)
    {
        if (_table is null || TableGrid is null || _table.Rows.Count == 0 || _table.ColumnCount == 0) return;
        _anchorRow = Math.Clamp(row, 0, _table.Rows.Count - 1);
        _anchorColumn = Math.Clamp(column, 0, _table.ColumnCount - 1);
        _activeRow = Math.Clamp(endRow, 0, _table.Rows.Count - 1);
        _activeColumn = Math.Clamp(endColumn, 0, _table.ColumnCount - 1);
        _hasCellSelection = true;
        _table.Select(_anchorRow, _anchorColumn, _activeRow, _activeColumn);
        TableGrid.CurrentCell = new DataGridCellInfo(TableGrid.Items[_activeRow], TableGrid.Columns[_activeColumn]);
        TableGrid.SelectedCells.Clear();
        for (int selectedRow = Math.Min(_anchorRow, _activeRow); selectedRow <= Math.Max(_anchorRow, _activeRow); selectedRow++)
        {
            for (int selectedColumn = Math.Min(_anchorColumn, _activeColumn); selectedColumn <= Math.Max(_anchorColumn, _activeColumn); selectedColumn++)
            {
                TableGrid.SelectedCells.Add(new DataGridCellInfo(TableGrid.Items[selectedRow], TableGrid.Columns[selectedColumn]));
            }
        }
        TableGrid.ScrollIntoView(TableGrid.Items[_activeRow], TableGrid.Columns[_activeColumn]);
    }

    internal void MoveCell(Key key, bool extend)
    {
        if (_table is null) return;
        int row = _activeRow;
        int column = _activeColumn;
        if (_hasCellSelection)
        {
            switch (key)
            {
                case Key.Left: column--; break;
                case Key.Right: column++; break;
                case Key.Up: row--; break;
                case Key.Down: row++; break;
                default: return;
            }
        }
        if (extend && _hasCellSelection) SelectCells(_anchorRow, _anchorColumn, row, column);
        else SelectCells(row, column, row, column);
    }

    internal bool HandleTableNavigationKey(Key key, ModifierKeys modifiers)
    {
        if (_table is null || TableGrid is null) return false;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        bool control = (modifiers & ModifierKeys.Control) != 0;
        int row = _activeRow;
        int column = _activeColumn;
        switch (key)
        {
            case Key.Home:
                column = 0;
                if (control) row = 0;
                break;
            case Key.End:
                column = _table.ColumnCount - 1;
                if (control) row = _table.Rows.Count - 1;
                break;
            case Key.PageUp:
            case Key.PageDown:
                int page = Math.Max(1, (int)(TableGrid.ActualHeight / TableGrid.RowHeight) - 1);
                row += key == Key.PageUp ? -page : page;
                break;
            case Key.Tab:
                int index = Math.Clamp((row * _table.ColumnCount) + column + (shift ? -1 : 1),
                    0, (_table.Rows.Count * _table.ColumnCount) - 1);
                row = index / _table.ColumnCount;
                column = index % _table.ColumnCount;
                shift = false;
                break;
            case Key.Enter:
                row += shift ? -1 : 1;
                shift = false;
                break;
            case Key.Space:
                break;
            default:
                return false;
        }
        if (shift && _hasCellSelection) SelectCells(_anchorRow, _anchorColumn, row, column);
        else SelectCells(row, column, row, column);
        return true;
    }

    private void OnTableMouseDown(object sender, MouseButtonEventArgs e)
    {
        TableGrid!.Focus();
        _dragPoint = e.GetPosition(TableGrid);
        if (!SelectCellAt(_dragPoint, (Keyboard.Modifiers & ModifierKeys.Shift) != 0)) return;
        _dragSelecting = TableGrid.CaptureMouse();
        if (_dragSelecting) _dragScrollTimer!.Start();
        e.Handled = true;
    }

    private void OnTableMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragSelecting || e.LeftButton != MouseButtonState.Pressed) return;
        _dragPoint = e.GetPosition(TableGrid!);
        ExtendSelectionTo(_dragPoint);
        e.Handled = true;
    }

    private void OnTableMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragSelecting) return;
        StopDragSelection();
        e.Handled = true;
    }

    private void StopDragSelection()
    {
        _dragSelecting = false;
        _dragScrollTimer?.Stop();
        if (TableGrid?.IsMouseCaptured == true) TableGrid.ReleaseMouseCapture();
    }

    internal bool SelectCellAt(Point point, bool extend)
    {
        if (TableGrid is null || !TryGetCell(point, out int row, out int column)) return false;
        if (extend && _hasCellSelection) SelectCells(_anchorRow, _anchorColumn, row, column);
        else SelectCells(row, column, row, column);
        return true;
    }

    internal bool ExtendSelectionTo(Point point)
    {
        if (!_hasCellSelection || TableGrid is null) return false;
        ScrollViewer? scroll = FindDescendant<ScrollViewer>(TableGrid);
        ScrollContentPresenter? viewport = FindDescendant<ScrollContentPresenter>(scroll);
        if (scroll is not null && viewport is { ActualWidth: > 2, ActualHeight: > 2 })
        {
            Rect bounds = viewport.TransformToAncestor(TableGrid)
                .TransformBounds(new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
            bool scrolled = false;
            if (point.Y < bounds.Top) { scroll.LineUp(); scrolled = true; }
            else if (point.Y >= bounds.Bottom) { scroll.LineDown(); scrolled = true; }
            if (point.X < bounds.Left) { scroll.LineLeft(); scrolled = true; }
            else if (point.X >= bounds.Right) { scroll.LineRight(); scrolled = true; }
            if (scrolled) TableGrid.UpdateLayout();
            point = new Point(Math.Clamp(point.X, bounds.Left + 1, bounds.Right - 1),
                Math.Clamp(point.Y, bounds.Top + 1, bounds.Bottom - 1));
        }
        if (!TryGetCell(point, out int row, out int column)) return false;
        if (row != _activeRow || column != _activeColumn)
            SelectCells(_anchorRow, _anchorColumn, row, column);
        return true;
    }

    private bool TryGetCell(Point point, out int row, out int column)
    {
        DependencyObject? source = TableGrid!.InputHitTest(point) as DependencyObject;
        while (source is not null && source is not DataGridCell)
            source = VisualTreeHelper.GetParent(source);
        if (source is DataGridCell cell)
        {
            row = TableGrid.Items.IndexOf(cell.DataContext);
            column = cell.Column.DisplayIndex;
            return row >= 0;
        }
        row = column = 0;
        return false;
    }

    private static void BindPinContextMenu(FrameworkElement control) =>
        control.SetBinding(ContextMenuProperty, new Binding(nameof(ContextMenu))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(PinTextSelectionView), 1),
        });

    private static bool IsClipboardCommand(ICommand command) =>
        command == ApplicationCommands.Copy || command == ApplicationCommands.Cut || command == ApplicationCommands.Paste;

    private static void OnPreviewCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (!IsClipboardCommand(e.Command)) return;
        e.CanExecute = false;
        e.Handled = true;
    }

    private static void OnPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (IsClipboardCommand(e.Command)) e.Handled = true;
    }

    private static (string Display, int[] Starts, int[] Ends) CreateDisplayText(string original)
    {
        var display = new StringBuilder(original.Length);
        var starts = new List<int>(original.Length);
        var ends = new List<int>(original.Length);
        for (int index = 0; index < original.Length; index++)
        {
            char character = original[index];
            if (character == '\t')
            {
                for (int space = 0; space < 4; space++) Append(' ', index, index + 1);
            }
            else if (character == '\r' && index + 1 < original.Length && original[index + 1] == '\n')
            {
                Append('\r', index, index + 1);
                index++;
                Append('\n', index, index + 1);
            }
            else if (character is '\r' or '\n')
            {
                Append('\r', index, index + 1);
                Append('\n', index, index + 1);
            }
            else Append(character, index, index + 1);
        }
        return (display.ToString(), starts.ToArray(), ends.ToArray());

        void Append(char character, int start, int end)
        {
            display.Append(character);
            starts.Add(start);
            ends.Add(end);
        }
    }

    private static Color SampleBackground(BitmapSource image)
    {
        BitmapSource source = image.Format == PixelFormats.Bgra32
            ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        source.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        // Rounded text cards have a transparent corner; sample inside the empty top padding.
        if (pixel[3] < 255)
            source.CopyPixels(new Int32Rect(Math.Min(12, source.PixelWidth - 1), Math.Min(12, source.PixelHeight - 1), 1, 1), pixel, 4, 0);
        return pixel[3] == 0 ? Colors.White : Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte value)
        {
            double normalized = value / 255.0;
            return normalized <= 0.04045 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }
        return (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));
    }

    private static T? FindDescendant<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) return found;
            if (FindDescendant<T>(child) is { } descendant) return descendant;
        }
        return null;
    }
}
