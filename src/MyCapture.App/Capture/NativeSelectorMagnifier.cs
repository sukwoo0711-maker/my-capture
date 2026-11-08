using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MyCapture.App.Themes;

namespace MyCapture.App.Capture;

/// <summary>One retained, bounded overlay for the magnifier grid and its two footer values.</summary>
internal sealed class NativeSelectorMagnifier : Adorner
{
    internal const double ColorFontSize = TypeScale.Caption;
    internal const double CoordinateFontSize = TypeScale.Micro;
    internal const double ImageRadius = 6;
    private readonly Image _image;
    private readonly Brush _textBrush, _mutedBrush, _gridBrush;
    private readonly Brush? _quarterGridBrush;
    private readonly Brush _cornerBrush;
    private readonly Pen _borderPen, _gridPen;
    private readonly Typeface _typeface;
    private readonly DrawingGroup _drawing = new();
    private readonly LineGeometry[] _gridLines = Enumerable.Range(0, 28).Select(_ => new LineGeometry()).ToArray();
    private LayoutProfile[] _profiles = [];
    private LayoutProfile _profile;
    private AdornerLayer? _layer;
    private bool _dirty = true;
    private int _x, _y, _cellX, _cellY, _columns = 15, _rows = 15;
    private uint _color;
    private int _drawnX = -1, _drawnY = -1;
    private uint _drawnColor = uint.MaxValue;
    private FormattedText? _colorText, _coordinateText;

    internal NativeSelectorMagnifier(Image image, IReadOnlyDictionary<string, Brush> palette) : base(image)
    {
        _image = image;
        IsHitTestVisible = false;
        _textBrush = palette["CaptureGuide.Text"];
        _mutedBrush = palette["CaptureGuide.MutedText"];
        _gridBrush = palette["CaptureGuide.MagnifierGrid"];
        _quarterGridBrush = CaptureOverlayView.CoverageBrush(_gridBrush, 0.25f);
        _cornerBrush = OpaquePanelBrush(palette["CaptureGuide.ChipSurface"]);
        _borderPen = new Pen(palette["CaptureGuide.MagnifierBorder"], 1);
        _gridPen = new Pen(_gridBrush, 0.5);
        FontFamily family = Application.Current?.TryFindResource("Font.Mono") as FontFamily ?? new FontFamily("Consolas");
        _typeface = new Typeface(family, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    }

    internal DrawingGroup DrawingForTest { get { RenderPending(); return _drawing; } }
    internal Size ImageSizeForTest => new(_profile.Image, _profile.Image);
    internal Point ImageOffsetForTest => new(_profile.Inset, _profile.Inset);
    internal Rect ColorBoundsForTest { get; private set; }
    internal Rect CoordinateBoundsForTest { get; private set; }
    internal Rect CellBoundsForTest => new(_cellX * (_profile.Image / _columns), _cellY * (_profile.Image / _rows),
        _profile.Image / _columns, _profile.Image / _rows);
    internal IReadOnlyList<LineGeometry> GridLinesForTest => _gridLines;
    internal double FooterHeightForTest => _profile.Footer;
    internal int GridFillCount { get; private set; }
    internal int LabelLayoutCountForTest { get; private set; }
    internal int RenderCountForTest { get; private set; }
    internal Size LogicalSize => new(_profile.Image + 6, _profile.Image + _profile.Footer + 6);

    internal void Bind(int width, int height, IReadOnlyList<CaptureGuideArea> areas, double fallbackScale)
    {
        var profiles = new List<LayoutProfile>();
        Add(fallbackScale);
        for (int index = 0; index < areas.Count; index++) Add(areas[index].Scale);
        _profiles = profiles.ToArray();
        _profile = default;
        _colorText = _coordinateText = null;
        _drawnX = _drawnY = -1;
        _drawnColor = uint.MaxValue;
        _dirty = true;

        void Add(double scale)
        {
            if (!(scale > 0) || profiles.Any(item => item.Scale == scale)) return;
            FormattedText color = Text("#FFFFFF", ColorFontSize, _textBrush, scale);
            FormattedText coordinates = Text($"{Math.Max(0, width - 1)}, {Math.Max(0, height - 1)}", CoordinateFontSize, _mutedBrush, scale);
            bool oneLine = 8 + color.Width + 8 + coordinates.Width + 8 <= 150 / scale;
            double cellPixels = oneLine ? 10 : Math.Max(10, Math.Ceiling((16 + Math.Max(color.Width, coordinates.Width)) * scale / 15));
            double footerText = oneLine ? 5 + color.Height + 5 : 5 + color.Height + 1 + coordinates.Height + 6;
            profiles.Add(new LayoutProfile(scale, cellPixels * 15 / scale,
                Math.Max(38, Math.Ceiling(footerText * scale)) / scale, oneLine, color.Height,
                Math.Ceiling(3 * scale) / scale, TopCorners(cellPixels * 15 / scale)));
        }
    }

    internal bool Update(double scale, int x, int y, int cropX, int cropY, int columns, int rows, byte[] sample)
    {
        bool layoutChanged = _profile.Scale != scale;
        if (layoutChanged)
        {
            for (int index = 0; index < _profiles.Length; index++)
                if (_profiles[index].Scale == scale) { _profile = _profiles[index]; break; }
            if (!(_profile.Scale > 0)) _profile = _profiles[0];
            _image.Width = _image.Height = _profile.Image;
            _borderPen.Thickness = 1 / _profile.Scale;
            _gridPen.Thickness = 0.5 / _profile.Scale;
            _colorText = _coordinateText = null;
            InvalidateMeasure();
        }
        int cellX = Math.Clamp(x - cropX, 0, columns - 1), cellY = Math.Clamp(y - cropY, 0, rows - 1);
        int offset = (cellY * 15 + cellX) * 4;
        uint color = (uint)((sample[offset + 2] << 16) | (sample[offset + 1] << 8) | sample[offset]);
        if (!layoutChanged && _x == x && _y == y && _color == color && _cellX == cellX && _cellY == cellY
            && _columns == columns && _rows == rows) return false;
        bool gridChanged = layoutChanged || _columns != columns || _rows != rows;
        _x = x; _y = y; _color = color; _cellX = cellX; _cellY = cellY; _columns = columns; _rows = rows;
        if (gridChanged)
        {
            for (int index = 1; index < 15; index++)
            {
                double vertical = index * (_profile.Image / columns), horizontal = index * (_profile.Image / rows);
                _gridLines[index - 1].StartPoint = index < columns ? new Point(vertical, 0) : default;
                _gridLines[index - 1].EndPoint = index < columns ? new Point(vertical, _profile.Image) : default;
                _gridLines[index + 13].StartPoint = index < rows ? new Point(0, horizontal) : default;
                _gridLines[index + 13].EndPoint = index < rows ? new Point(_profile.Image, horizontal) : default;
            }
        }
        _dirty = true;
        // WPF coalesces invalidation. Numeric strings and text layout are built only by OnRender,
        // never here on a raw mouse message, and repeated values reuse their existing layout.
        InvalidateVisual();
        return layoutChanged;
    }

    internal void Attach()
    {
        AdornerLayer? layer = AdornerLayer.GetAdornerLayer(_image);
        if (ReferenceEquals(layer, _layer)) return;
        Detach();
        if (layer is null) return;
        _layer = layer;
        layer.Add(this);
    }

    internal void Detach()
    {
        _layer?.Remove(this);
        _layer = null;
    }

    internal void Clear()
    {
        Detach();
        _drawing.Children.Clear();
        _colorText = _coordinateText = null;
        _dirty = true;
        _x = _y = 0;
        _color = 0;
    }

    protected override Size MeasureOverride(Size constraint) => new(_profile.Image, _profile.Image + _profile.Footer);

    protected override void OnRender(DrawingContext drawingContext)
    {
        RenderPending();
        drawingContext.DrawDrawing(_drawing);
    }

    private void RenderPending()
    {
        if (!_dirty || !(_profile.Scale > 0)) return;
        _dirty = false;
        RenderCountForTest++;
        if (_colorText is null || _drawnColor != _color)
        {
            _colorText = Text($"#{_color:X6}", ColorFontSize, _textBrush, _profile.Scale);
            _drawnColor = _color;
            LabelLayoutCountForTest++;
        }
        if (_coordinateText is null || _drawnX != _x || _drawnY != _y)
        {
            _coordinateText = Text($"{_x}, {_y}", CoordinateFontSize, _mutedBrush, _profile.Scale);
            _drawnX = _x; _drawnY = _y;
            LabelLayoutCountForTest++;
        }
        using DrawingContext dc = _drawing.Open();
        dc.DrawRoundedRectangle(null, _borderPen,
            new Rect(-_profile.Inset, -_profile.Inset, _profile.Image + 6, _profile.Image + _profile.Footer + 6), 9, 9);
        // Paint the panel over the image's two top corners. A non-rectangular Image.Clip would
        // introduce a WPF intermediate surface; this bounded, frozen geometry needs none.
        dc.DrawGeometry(_cornerBrush, null, _profile.Corners);
        double pixel = 1 / _profile.Scale;
        bool integralCells = Math.Abs(_profile.Image * _profile.Scale / _columns - Math.Round(_profile.Image * _profile.Scale / _columns)) < 0.000001
            && Math.Abs(_profile.Image * _profile.Scale / _rows - Math.Round(_profile.Image * _profile.Scale / _rows)) < 0.000001;
        if (_quarterGridBrush is { } quarter && integralCells)
        {
            for (int index = 1; index < _columns; index++)
                dc.DrawRectangle(quarter, null, new Rect(index * _profile.Image / _columns - pixel, 0, 2 * pixel, _profile.Image));
            for (int index = 1; index < _rows; index++)
                dc.DrawRectangle(quarter, null, new Rect(0, index * _profile.Image / _rows - pixel, _profile.Image, 2 * pixel));
            GridFillCount++;
        }
        else
        {
            for (int index = 0; index < _gridLines.Length; index++) dc.DrawGeometry(null, _gridPen, _gridLines[index]);
        }
        dc.DrawRectangle(null, _borderPen, CellBoundsForTest);
        var colorAt = new Point(8, _profile.Image + 5);
        Point coordinateAt = _profile.OneLine
            ? new Point(_profile.Image - _coordinateText.Width - 8, _profile.Image + 6)
            : new Point(8, _profile.Image + 5 + _profile.ColorHeight + 1);
        dc.DrawText(_colorText, colorAt);
        dc.DrawText(_coordinateText, coordinateAt);
        ColorBoundsForTest = new Rect(colorAt, new Size(_colorText.Width, _colorText.Height));
        CoordinateBoundsForTest = new Rect(coordinateAt, new Size(_coordinateText.Width, _coordinateText.Height));
    }

    private FormattedText Text(string value, double size, Brush brush, double scale) =>
        new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _typeface, size, brush, scale);

    private static Geometry TopCorners(double width)
    {
        var corners = new StreamGeometry();
        using (StreamGeometryContext context = corners.Open())
        {
            context.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
            context.LineTo(new Point(ImageRadius, 0), isStroked: false, isSmoothJoin: false);
            context.ArcTo(new Point(0, ImageRadius), new Size(ImageRadius, ImageRadius), 0,
                isLargeArc: false, SweepDirection.Counterclockwise, isStroked: false, isSmoothJoin: false);
            context.BeginFigure(new Point(width, 0), isFilled: true, isClosed: true);
            context.LineTo(new Point(width - ImageRadius, 0), isStroked: false, isSmoothJoin: false);
            context.ArcTo(new Point(width, ImageRadius), new Size(ImageRadius, ImageRadius), 0,
                isLargeArc: false, SweepDirection.Clockwise, isStroked: false, isSmoothJoin: false);
        }
        corners.Freeze();
        return corners;
    }

    private static Brush OpaquePanelBrush(Brush panel)
    {
        Color floor = (CaptureWindowExclusion.OpaqueBackground() as SolidColorBrush)?.Color ?? Colors.Black;
        Color foreground = (panel as SolidColorBrush)?.Color ?? floor;
        double alpha = foreground.A / 255d * panel.Opacity;
        byte Blend(byte value, byte background) => (byte)Math.Round(value * alpha + background * (1 - alpha));
        var brush = new SolidColorBrush(Color.FromRgb(Blend(foreground.R, floor.R),
            Blend(foreground.G, floor.G), Blend(foreground.B, floor.B)));
        brush.Freeze();
        return brush;
    }

    private readonly record struct LayoutProfile(double Scale, double Image, double Footer, bool OneLine,
        double ColorHeight, double Inset, Geometry Corners);
}
