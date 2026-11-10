using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace MyCapture.App.Pinning;

/// <summary>Per-view geometry, text and drawing resources, independent of realised DataGrid cells.</summary>
internal sealed class PinTableCellLayout
{
    private readonly Dictionary<(string Value, int Width), FormattedText> _text = new();
    private readonly Dictionary<double, (Pen Line, Pen Border)> _pens = new();
    private double _zoom = 1;
    private Transform _textScale = Frozen(new ScaleTransform(1, 1));
    internal int[] Widths { get; }
    internal int RowCount { get; }

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

    internal double Zoom
    {
        get => _zoom;
        set
        {
            if (_zoom == value) return;
            _zoom = value;
            _textScale = Frozen(new ScaleTransform(value, value));
        }
    }

    /// <summary>The frozen zoom transform the cells draw their text in.</summary>
    internal Transform TextScale => _textScale;

    /// <summary>The current cell's focus ring width in DIPs (the Focus.RingWidth token).</summary>
    internal double FocusWidth { get; set; } = 2;

    /// <summary>Set when a cell rendered after the last <see cref="Place"/>, so the view checks the positions again.</summary>
    internal bool Pending { get; set; }

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

    /// <summary>Frozen one-device-pixel grid pens for a device <paramref name="scale"/>.</summary>
    internal (Pen Line, Pen Border) Pens(double scale)
    {
        if (!_pens.TryGetValue(scale, out (Pen Line, Pen Border) pens))
        {
            pens = (ClipboardTextRenderer.DevicePixelPen(ClipboardTextRenderer.GridLine, scale),
                ClipboardTextRenderer.DevicePixelPen(ClipboardTextRenderer.GridBorder, scale));
            _pens.Add(scale, pens);
        }
        return pens;
    }

    // A cell snaps its lines and fills to device pixels from its position in the root. WPF renders
    // an element inside its own arrange, before the arrange of its ancestors has set their offsets
    // (the first open, a newly realised or recycled row), and does not render it again when a scroll
    // only moves it. The view therefore places every cell once a layout pass has settled.

    /// <summary>
    /// Gives every realised cell under <paramref name="grid"/> its position in the root: one walk
    /// down from the grid's own <paramref name="origin"/> adds up the visual offsets, so no cell
    /// computes a transform. A cell redraws only when it waited for its position or drew for another
    /// device-pixel phase or DPI.
    /// </summary>
    internal void Place(Visual grid, Point origin)
    {
        Pending = false;
        Walk(grid, origin);

        static void Walk(Visual parent, Point at)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int index = 0; index < count; index++)
            {
                if (VisualTreeHelper.GetChild(parent, index) is not Visual child || child is ScrollBar) continue;
                if (VisualTreeHelper.GetTransform(child) is not null)
                {
                    PlaceTransformed(child);
                    continue;
                }
                Point position = at + VisualTreeHelper.GetOffset(child);
                if (child is PinTableCell cell) cell.Place(position);
                else Walk(child, position);
            }
        }
    }

    // The DataGrid's own templates have no transforms; a transformed subtree places each cell
    // from its own transform to the root.
    private static void PlaceTransformed(Visual parent)
    {
        if (parent is PinTableCell cell)
        {
            cell.Place(PinTableCell.RootOrigin(cell, out _));
            return;
        }
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            if (VisualTreeHelper.GetChild(parent, index) is Visual child) PlaceTransformed(child);
        }
    }

    private static Transform Frozen(Transform transform)
    {
        transform.Freeze();
        return transform;
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
    private Point _placedOrigin;
    private bool _placed;
    private Point _drawnOrigin;
    private DpiScale _drawnDpi;
    // Drew content for _drawnOrigin, or skipped drawing until the view places the cell.
    private bool _drawn;
    private bool _waiting;
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
        // The row's value arrives, or a recycled row gives its cells another value and position.
        DataContextChanged += (_, _) =>
        {
            NameCell();
            Unplace();
        };
        // The current cell (the one with keyboard focus) draws its focus ring.
        cell.IsKeyboardFocusWithinChanged += (_, _) => InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Unplace();
    }

    // DataGridCell names itself from its content's plain text, which a drawn cell does not have, and
    // a LabeledBy label resolves only to a peer some client has already created. The cell's own
    // name gives the DataGrid's cell item peer, and so Narrator, the value as soon as it asks.
    private void NameCell()
    {
        string value = Value;
        if (value.Length > 0) AutomationProperties.SetName(_cell, value);
        else _cell.ClearValue(AutomationProperties.NameProperty);
    }

    private void Unplace()
    {
        _placed = false;
        InvalidateVisual();
    }

    /// <summary>The position of <paramref name="visual"/> in its root and the DPI it renders at.</summary>
    internal static Point RootOrigin(Visual visual, out DpiScale dpi)
    {
        dpi = VisualTreeHelper.GetDpi(visual);
        var origin = new Point();
        Visual current = visual;
        while (VisualTreeHelper.GetParent(current) is Visual parent)
        {
            if (VisualTreeHelper.GetTransform(current) is not null)
            {
                Visual root = parent;
                while (VisualTreeHelper.GetParent(root) is Visual next) root = next;
                return visual.TransformToAncestor(root).Transform(new Point());
            }
            origin += VisualTreeHelper.GetOffset(current);
            current = parent;
        }
        return origin;
    }

    /// <summary>
    /// Records the cell's position in the root; redraws it once when it waited for the position or
    /// drew for another device-pixel phase or DPI. A redraw draws at exactly this position, so
    /// placing it again at the same position asks for nothing.
    /// </summary>
    internal void Place(Point origin)
    {
        _placedOrigin = origin;
        _placed = true;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        if (_waiting || (_drawn && (!SameDpi(dpi, _drawnDpi) || !SamePhase(origin.X, _drawnOrigin.X, dpi.DpiScaleX)
            || !SamePhase(origin.Y, _drawnOrigin.Y, dpi.DpiScaleY))))
        {
            _waiting = false;
            InvalidateVisual();
        }
    }

    private static bool SameDpi(DpiScale a, DpiScale b) => a.DpiScaleX == b.DpiScaleX && a.DpiScaleY == b.DpiScaleY;

    // Moving by whole device pixels keeps every snapped line on its pixel.
    private static bool SamePhase(double position, double drawnAt, double scale)
    {
        double shift = (position - drawnAt) * scale;
        return Math.Abs(shift - Math.Round(shift)) < 1e-6;
    }

    private static bool Whole(double value) => Math.Abs(value - Math.Round(value)) < 1e-6;

    // Whole device pixels for a DIP length, at least one.
    private static double DevicePixels(double length, double scale) => Math.Max(1, Math.Round(length * scale)) / scale;

    protected override void OnRender(DrawingContext drawing)
    {
        _drawn = false;
        _waiting = false;
        var row = DataGridRow.GetRowContainingElement(_cell);
        if (row is null) return;
        int index = row.GetIndex();
        double zoom = _layout.Zoom;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        // Every render asks the view to check the placement after the pass, as a render can follow
        // a move in the same pass.
        _layout.Pending = true;
        Point origin;
        if (_placed) origin = _placedOrigin;
        else
        {
            // Unplaced, the cell's ancestors may not have their offsets yet. At whole device scales
            // the offsets nearly always keep the cell on the same device-pixel phase, so it draws
            // at once and the placement after the pass redraws it only if it moved off that phase;
            // at fractional scales the cell waits for its position rather than drawing twice.
            if (!Whole(dpi.DpiScaleX * zoom) || !Whole(dpi.DpiScaleY * zoom))
            {
                _waiting = true;
                return;
            }
            origin = RootOrigin(this, out _);
        }
        _drawn = true;
        _drawnOrigin = origin;
        _drawnDpi = dpi;
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        double width = _layout.Widths[_column] * zoom, height = ClipboardTextRenderer.TableRowHeight * zoom;
        bool lastColumn = _column == _layout.Widths.Length - 1, lastRow = index == _layout.RowCount - 1;
        double x = ClipboardTextRenderer.SnapLine(origin.X, sx) - origin.X;
        double y = ClipboardTextRenderer.SnapLine(origin.Y, sy) - origin.Y;
        double right = ClipboardTextRenderer.SnapLine(origin.X + width, sx) - origin.X - 1 / sx;
        double bottom = ClipboardTextRenderer.SnapLine(origin.Y + height, sy) - origin.Y - 1 / sy;
        double leftEdge = x - 0.5 / sx, topEdge = y - 0.5 / sy;
        double rightEdge = right + 0.5 / sx, bottomEdge = bottom + 0.5 / sy;
        // RenderTable's white foundation is inset half a DIP. Odd stripes cover its
        // entire row, including that inset; retain the thin card-colour edge at high DPI.
        bool stripe = (index & 1) == 1;
        double insetLeft = !stripe && _column == 0 ? 0.5 * zoom : leftEdge;
        double insetTop = !stripe && index == 0 ? 0.5 * zoom : topEdge;
        double insetRight = !stripe && lastColumn ? width - 0.5 * zoom : rightEdge;
        double insetBottom = !stripe && lastRow ? height - 0.5 * zoom : bottomEdge;
        drawing.DrawRectangle(stripe ? ClipboardTextRenderer.AlternateRowBackground : ClipboardTextRenderer.TableBackground,
            null, new Rect(insetLeft, insetTop, insetRight - insetLeft, insetBottom - insetTop));
        if (SelectedBackground is { } selected) drawing.DrawRectangle(selected, null, new Rect(0, 0, width, height));
        Brush ink = Ink ?? ClipboardTextRenderer.TextForeground;
        if (Value.Length > 0)
        {
            FormattedText text = _layout.Text(Value, _column);
            text.SetForegroundBrush(ink);
            drawing.PushTransform(_layout.TextScale);
            drawing.DrawText(text, new Point(ClipboardTextRenderer.TableHorizontalPadding,
                (ClipboardTextRenderer.TableRowHeight - text.Height) / 2));
            drawing.Pop();
        }

        // Snap in the containing visual's coordinate space. Snapping local widths instead
        // accumulates half-pixel errors at fractional monitor scales and zooms.
        (Pen line, Pen border) = _layout.Pens(sx);
        (Pen lineY, Pen borderY) = sy == sx ? (line, border) : _layout.Pens(sy);
        Pen vertical = _column == 0 ? border : line;
        Pen horizontal = index == 0 ? borderY : lineY;
        if (_column > 0) drawing.DrawLine(vertical, new Point(x, topEdge), new Point(x, bottomEdge));
        if (index > 0) drawing.DrawLine(horizontal, new Point(leftEdge, y), new Point(rightEdge, y));
        if (_column == 0) drawing.DrawLine(vertical, new Point(x, topEdge), new Point(x, bottomEdge));
        if (index == 0) drawing.DrawLine(horizontal, new Point(leftEdge, y), new Point(rightEdge, y));
        if (lastColumn) drawing.DrawLine(border, new Point(right, topEdge), new Point(right, bottomEdge));
        if (lastRow) drawing.DrawLine(borderY, new Point(leftEdge, bottom), new Point(rightEdge, bottom));

        if (_cell.IsKeyboardFocusWithin) DrawFocusRing(drawing, ink, origin, x, y, right, bottom, width, height, lastColumn, lastRow, sx, sy);
    }

    // The current cell (the one with keyboard focus, which the arrows and Shift+arrows move) is
    // outlined inside its grid lines in its own text colour: the system highlight text colour on a
    // selected cell, a pair every Windows and contrast theme keeps legible, and the picture's ink
    // otherwise. The width is the theme's focus ring width in whole device pixels; it does not
    // zoom with the table.
    private void DrawFocusRing(DrawingContext drawing, Brush ink, Point origin, double x, double y, double right, double bottom,
        double width, double height, bool lastColumn, bool lastRow, double sx, double sy)
    {
        double left = x + 0.5 / sx, top = y + 0.5 / sy;
        double end = (lastColumn ? right : ClipboardTextRenderer.SnapLine(origin.X + width, sx) - origin.X) - 0.5 / sx;
        double foot = (lastRow ? bottom : ClipboardTextRenderer.SnapLine(origin.Y + height, sy) - origin.Y) - 0.5 / sy;
        double ringX = Math.Min(DevicePixels(_layout.FocusWidth, sx), Math.Max(0, (end - left) / 2));
        double ringY = Math.Min(DevicePixels(_layout.FocusWidth, sy), Math.Max(0, (foot - top) / 2));
        double across = Math.Max(0, end - left), side = Math.Max(0, foot - top - (2 * ringY));
        drawing.DrawRectangle(ink, null, new Rect(left, top, across, ringY));
        drawing.DrawRectangle(ink, null, new Rect(left, foot - ringY, across, ringY));
        drawing.DrawRectangle(ink, null, new Rect(left, top + ringY, ringX, side));
        drawing.DrawRectangle(ink, null, new Rect(end - ringX, top + ringY, ringX, side));
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
        // Resource references follow Windows colour and contrast theme changes while the view is open.
        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(SystemColors.HighlightBrushKey)));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(SystemColors.HighlightTextBrushKey)));
        style.Triggers.Add(selected);
        var inactive = new MultiTrigger();
        inactive.Conditions.Add(new System.Windows.Condition(DataGridCell.IsSelectedProperty, true));
        inactive.Conditions.Add(new System.Windows.Condition(Selector.IsSelectionActiveProperty, false));
        inactive.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(SystemColors.InactiveSelectionHighlightBrushKey)));
        inactive.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(SystemColors.InactiveSelectionHighlightTextBrushKey)));
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
