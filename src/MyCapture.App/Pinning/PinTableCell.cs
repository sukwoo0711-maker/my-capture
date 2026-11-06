using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace MyCapture.App.Pinning;

/// <summary>Per-view geometry and text caches, independent of realised DataGrid cells and zoom.</summary>
internal sealed class PinTableCellLayout
{
    private readonly Dictionary<(string Value, int Width), FormattedText> _text = new();
    internal int[] Widths { get; }
    internal int RowCount { get; }
    internal double Zoom { get; set; } = 1;

    internal PinTableCellLayout(string original, PinTableSelection table)
    {
        var measured = new Dictionary<string, int>(StringComparer.Ordinal);
        ClipboardTextRenderer.TablePreview preview = ClipboardTextRenderer.ParseTablePreview(original);
        int[] pictureWidths = ClipboardTextRenderer.MeasureColumnWidths(preview.Rows, preview.ColumnCount, true, measured);
        Widths = new int[table.ColumnCount];
        Array.Copy(pictureWidths, Widths, Math.Min(Widths.Length, pictureWidths.Length));
        if (Widths.Length > pictureWidths.Length)
        {
            // Only the extra columns are measured here; the first twelve retain FitColumns.
            IReadOnlyList<IReadOnlyList<string>> extra = table.Rows.Take(ClipboardTextRenderer.MaxTableRows)
                .Select(row => (IReadOnlyList<string>)row.Skip(pictureWidths.Length).ToArray()).ToArray();
            int[] extraWidths = ClipboardTextRenderer.MeasureColumnWidths(extra, Widths.Length - pictureWidths.Length, false, measured);
            Array.Copy(extraWidths, 0, Widths, pictureWidths.Length, extraWidths.Length);
        }
        RowCount = table.RowCount;
    }

    internal FormattedText Text(string value, int column)
    {
        var key = (value, Widths[column]);
        if (!_text.TryGetValue(key, out FormattedText? text))
        {
            text = ClipboardTextRenderer.CreateTableCellText(value, key.Item2);
            _text.Add(key, text);
        }
        return text;
    }

    // A cell snaps its lines and fills to device pixels from its root position while it renders.
    // WPF renders an element inside its own arrange, before the arrange of its ancestors has set
    // their offsets (the first open, a newly realised row or column), and does not render it again
    // when a scroll only moves it. Resnap redraws those cells once a layout pass has settled.
    private readonly HashSet<PinTableCell> _drawn = [];
    private bool _drawnSinceResnap;

    internal void Drawn(PinTableCell cell)
    {
        _drawn.Add(cell);
        _drawnSinceResnap = true;
    }

    /// <summary>
    /// Redraws the cells under <paramref name="scope"/> whose position moved by a fraction of a
    /// device pixel since they were drawn. <paramref name="moved"/> says whether the grid itself
    /// moved or scrolled; otherwise only a pass that drew cells can have left them unsnapped.
    /// </summary>
    internal void Resnap(Visual scope, bool moved)
    {
        if (!moved && !_drawnSinceResnap) return;
        _drawnSinceResnap = false;
        List<PinTableCell>? detached = null;
        foreach (PinTableCell cell in _drawn)
        {
            if (!cell.IsDescendantOf(scope)) (detached ??= []).Add(cell);
            else if (cell.SnapChanged()) cell.InvalidateVisual();
        }
        if (detached is not null)
        {
            foreach (PinTableCell cell in detached) _drawn.Remove(cell);
        }
    }
}

internal sealed class PinTableColumn(PinTableCellLayout layout, int column) : DataGridColumn
{
    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem) => new PinTableCell(layout, column, cell);
    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem) => GenerateElement(cell, dataItem);
}

/// <summary>One visual per cell; the DataGrid still owns input, focus, selection and recycling.</summary>
internal sealed class PinTableCell : FrameworkElement
{
    private readonly PinTableCellLayout _layout;
    private readonly int _column;
    private readonly DataGridCell _cell;
    private Point _snapOrigin;
    private DpiScale _snapDpi;
    private static readonly DependencyProperty SelectedBackgroundProperty = DependencyProperty.Register(
        nameof(SelectedBackground), typeof(Brush), typeof(PinTableCell), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty InkProperty = DependencyProperty.Register(
        nameof(Ink), typeof(Brush), typeof(PinTableCell), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush? SelectedBackground { get => (Brush?)GetValue(SelectedBackgroundProperty); set => SetValue(SelectedBackgroundProperty, value); }
    public Brush? Ink { get => (Brush?)GetValue(InkProperty); set => SetValue(InkProperty, value); }
    internal string Value => DataContext is string[] row && _column < row.Length ? row[_column] : string.Empty;

    internal PinTableCell(PinTableCellLayout layout, int column, DataGridCell cell)
    {
        _layout = layout;
        _column = column;
        _cell = cell;
        UseLayoutRounding = false;
        SnapsToDevicePixels = false;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        SetBinding(SelectedBackgroundProperty, new Binding(nameof(DataGridCell.Background)) { Source = cell });
        SetBinding(InkProperty, new Binding(nameof(DataGridCell.Foreground)) { Source = cell });
        DataContextChanged += (_, _) => InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    /// <summary>The position of <paramref name="visual"/> in its root and the DPI it renders at.</summary>
    internal static Point RootOrigin(Visual visual, out DpiScale dpi)
    {
        dpi = VisualTreeHelper.GetDpi(visual);
        Visual root = visual;
        while (VisualTreeHelper.GetParent(root) is Visual parent) root = parent;
        return ReferenceEquals(root, visual) ? new Point() : visual.TransformToAncestor(root).Transform(new Point());
    }

    /// <summary>Whether the cell now sits at another device-pixel phase or DPI than it was drawn for.</summary>
    internal bool SnapChanged()
    {
        Point origin = RootOrigin(this, out DpiScale dpi);
        return dpi.DpiScaleX != _snapDpi.DpiScaleX || dpi.DpiScaleY != _snapDpi.DpiScaleY
            || !SamePhase(origin.X, _snapOrigin.X, dpi.DpiScaleX) || !SamePhase(origin.Y, _snapOrigin.Y, dpi.DpiScaleY);
    }

    // Moving by whole device pixels keeps every snapped line on its pixel.
    private static bool SamePhase(double position, double drawnAt, double scale)
    {
        double shift = (position - drawnAt) * scale;
        return Math.Abs(shift - Math.Round(shift)) < 1e-6;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        var row = DataGridRow.GetRowContainingElement(_cell);
        if (row is null) return;
        int index = row.GetIndex();
        double zoom = _layout.Zoom;
        double width = _layout.Widths[_column] * zoom, height = ClipboardTextRenderer.TableRowHeight * zoom;
        Point origin = RootOrigin(this, out DpiScale dpi);
        _snapOrigin = origin;
        _snapDpi = dpi;
        _layout.Drawn(this);
        double x = ClipboardTextRenderer.SnapLine(origin.X, dpi.DpiScaleX) - origin.X;
        double y = ClipboardTextRenderer.SnapLine(origin.Y, dpi.DpiScaleY) - origin.Y;
        double right = ClipboardTextRenderer.SnapLine(origin.X + width, dpi.DpiScaleX) - origin.X - 1 / dpi.DpiScaleX;
        double bottom = ClipboardTextRenderer.SnapLine(origin.Y + height, dpi.DpiScaleY) - origin.Y - 1 / dpi.DpiScaleY;
        double leftEdge = x - 0.5 / dpi.DpiScaleX, topEdge = y - 0.5 / dpi.DpiScaleY;
        double rightEdge = right + 0.5 / dpi.DpiScaleX, bottomEdge = bottom + 0.5 / dpi.DpiScaleY;
        // RenderTable's white foundation is inset half a DIP. Odd stripes cover its
        // entire row, including that inset; retain the thin card-colour edge at high DPI.
        bool stripe = (index & 1) == 1;
        double insetLeft = !stripe && _column == 0 ? 0.5 * zoom : leftEdge;
        double insetTop = !stripe && index == 0 ? 0.5 * zoom : topEdge;
        double insetRight = !stripe && _column == _layout.Widths.Length - 1 ? width - 0.5 * zoom : rightEdge;
        double insetBottom = !stripe && index == _layout.RowCount - 1 ? height - 0.5 * zoom : bottomEdge;
        drawing.DrawRectangle(stripe ? ClipboardTextRenderer.AlternateRowBackground : ClipboardTextRenderer.TableBackground,
            null, new Rect(insetLeft, insetTop, insetRight - insetLeft, insetBottom - insetTop));
        if (SelectedBackground is { } selected) drawing.DrawRectangle(selected, null, new Rect(0, 0, width, height));
        if (Value.Length > 0)
        {
            FormattedText text = _layout.Text(Value, _column);
            text.SetForegroundBrush(Ink ?? ClipboardTextRenderer.TextForeground);
            drawing.PushTransform(new ScaleTransform(zoom, zoom));
            drawing.DrawText(text, new Point(ClipboardTextRenderer.TableHorizontalPadding,
                (ClipboardTextRenderer.TableRowHeight - text.Height) / 2));
            drawing.Pop();
        }

        // Snap in the containing visual's coordinate space. Snapping local widths instead
        // accumulates half-pixel errors at fractional monitor scales and zooms.
        Pen vertical = ClipboardTextRenderer.DevicePixelPen(_column == 0 ? ClipboardTextRenderer.GridBorder : ClipboardTextRenderer.GridLine, dpi.DpiScaleX);
        Pen horizontal = ClipboardTextRenderer.DevicePixelPen(index == 0 ? ClipboardTextRenderer.GridBorder : ClipboardTextRenderer.GridLine, dpi.DpiScaleY);
        if (_column > 0) drawing.DrawLine(vertical, new Point(x, topEdge), new Point(x, bottomEdge));
        if (index > 0) drawing.DrawLine(horizontal, new Point(leftEdge, y), new Point(rightEdge, y));
        if (_column == 0) drawing.DrawLine(vertical, new Point(x, topEdge), new Point(x, bottomEdge));
        if (index == 0) drawing.DrawLine(horizontal, new Point(leftEdge, y), new Point(rightEdge, y));
        if (_column == _layout.Widths.Length - 1)
            drawing.DrawLine(ClipboardTextRenderer.DevicePixelPen(ClipboardTextRenderer.GridBorder, dpi.DpiScaleX), new Point(right, topEdge), new Point(right, bottomEdge));
        if (index == _layout.RowCount - 1)
            drawing.DrawLine(ClipboardTextRenderer.DevicePixelPen(ClipboardTextRenderer.GridBorder, dpi.DpiScaleY), new Point(leftEdge, bottom), new Point(rightEdge, bottom));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CellPeer(this);

    private sealed class CellPeer(PinTableCell owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.Value;
        protected override string GetClassNameCore() => nameof(PinTableCell);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    }

    internal static Style CreateCellStyle()
    {
        var style = new Style(typeof(DataGridCell));
        style.Setters.Add(new Setter(UseLayoutRoundingProperty, false));
        style.Setters.Add(new Setter(SnapsToDevicePixelsProperty, false));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, ClipboardTextRenderer.TextForeground));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(DataGridCell))
        {
            VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)),
        }));
        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, SystemColors.HighlightBrush));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.HighlightTextBrush));
        style.Triggers.Add(selected);
        var inactive = new MultiTrigger();
        inactive.Conditions.Add(new Condition(DataGridCell.IsSelectedProperty, true));
        inactive.Conditions.Add(new Condition(Selector.IsSelectionActiveProperty, false));
        inactive.Setters.Add(new Setter(Control.BackgroundProperty, SystemColors.InactiveSelectionHighlightBrush));
        inactive.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.InactiveSelectionHighlightTextBrush));
        style.Triggers.Add(inactive);
        return style;
    }

    internal static Style CreateRowStyle()
    {
        var style = new Style(typeof(DataGridRow));
        style.Setters.Add(new Setter(UseLayoutRoundingProperty, false));
        style.Setters.Add(new Setter(SnapsToDevicePixelsProperty, false));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        return style;
    }
}
