using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MyCapture.App.Themes;
using MyCapture.Core.Capture;
using MyCapture.Core.Settings;

namespace MyCapture.App.Capture;

/// <summary>
/// One retained, bounded overlay for the magnifier grid and its footer: a swatch of the centre
/// pixel's colour, its value and coordinates, or, in the colour mode, the value in the copy format
/// and the colour's name. A hairline divides the footer from the grid, and the centre pixel carries
/// an accent ring outside a white one so it reads on any colour.
/// </summary>
/// <remarks>
/// On Windows 11 DWM rounds the magnifier's window (an 8-DIP radius) and paints its edge, and the
/// image's top corners follow that radius inside the 3-DIP inset. A square window (Windows 10)
/// gets a one-pixel chip-border edge and square image corners instead.
/// </remarks>
internal sealed class NativeSelectorMagnifier : Adorner
{
    /// <summary>The value's size: Latin hex digits and colour values, so the Micro size.</summary>
    internal const double ColorFontSize = TypeScale.Micro;
    internal const double CoordinateFontSize = TypeScale.Micro;
    /// <summary>The colour's name, which can be Korean: never below the Caption size.</summary>
    internal const double NameFontSize = TypeScale.Caption;
    /// <summary>The image's top-corner radius in a rounded window: the window's 8 DIPs less the 3-DIP inset.</summary>
    internal const double ImageRadius = 5;
    /// <summary>The colour swatch's side, DIPs.</summary>
    internal const double SwatchSize = 10;
    /// <summary>Space between the window's edge and the footer's content, DIPs.</summary>
    internal const double FooterPadding = 9;
    /// <summary>Space between the swatch and the value, DIPs.</summary>
    private const double SwatchGap = 5;
    /// <summary>The smallest footer content band below the divider, DIPs.</summary>
    private const double MinimumFooterContent = 32;
    private readonly Image _image;
    private readonly Brush _textBrush, _mutedBrush, _gridBrush, _hairlineBrush, _reticleBrush;
    private readonly Brush? _quarterGridBrush;
    private readonly Brush _cornerBrush;
    private readonly Pen _outlinePen, _gridPen, _swatchPen, _reticlePen, _reticleInnerPen;
    private readonly Typeface _typeface, _coordinateTypeface, _uiTypeface;
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
    private FormattedText? _colorText, _coordinateText, _nameText, _englishText;
    private SolidColorBrush? _swatch;
    private bool _colorMode, _layoutPending, _windowRounded;
    private ColorFormat _format;
    private readonly bool _korean = UiText.Culture.TwoLetterISOLanguageName == "ko";

    internal NativeSelectorMagnifier(Image image, IReadOnlyDictionary<string, Brush> palette) : base(image)
    {
        _image = image;
        IsHitTestVisible = false;
        _textBrush = palette["CaptureGuide.Text"];
        _mutedBrush = palette["CaptureGuide.MutedText"];
        _gridBrush = palette["CaptureGuide.MagnifierGrid"];
        _hairlineBrush = palette["CaptureGuide.ChipHairline"];
        _quarterGridBrush = CaptureOverlayView.CoverageBrush(_gridBrush, 0.25f);
        _cornerBrush = OpaquePanelBrush(palette["CaptureGuide.ChipSurface"]);
        _outlinePen = new Pen(palette["CaptureGuide.ChipBorder"], 1);
        _gridPen = new Pen(_gridBrush, 0.5);
        _swatchPen = new Pen(_hairlineBrush, 1);
        // The app's accent (a system colour in high contrast), outside a white ring.
        _reticleBrush = Application.Current?.TryFindResource("Accent.Default") as Brush ?? palette["CaptureGuide.MagnifierBorder"];
        _reticlePen = new Pen(_reticleBrush, 1);
        _reticleInnerPen = new Pen(Brushes.White, 1);
        FontFamily family = Application.Current?.TryFindResource("Font.Mono") as FontFamily ?? new FontFamily("Consolas");
        _typeface = new Typeface(family, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
        _coordinateTypeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FontFamily ui = Application.Current?.TryFindResource("Font.Ui") as FontFamily ?? new FontFamily("Segoe UI");
        _uiTypeface = new Typeface(ui, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    }

    /// <summary>
    /// The colour mode's footer: the value in the copy format, then the colour's name (in Korean
    /// with the CSS name below it). The next <see cref="Update"/> reports the changed size.
    /// </summary>
    internal bool ColorMode
    {
        get => _colorMode;
        set
        {
            if (_colorMode == value) return;
            _colorMode = value;
            _layoutPending = true;
            _colorText = _coordinateText = _nameText = _englishText = null;
            _dirty = true;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>True while DWM rounds and outlines the magnifier's window.</summary>
    internal bool WindowRounded
    {
        get => _windowRounded;
        set { if (_windowRounded == value) return; _windowRounded = value; _dirty = true; InvalidateVisual(); }
    }

    /// <summary>The copy format the colour mode shows; read when the mode is entered.</summary>
    internal ColorFormat Format
    {
        get => _format;
        set { if (_format == value) return; _format = value; _colorText = null; _dirty = true; InvalidateVisual(); }
    }

    /// <summary>The value and name the colour mode shows, as drawn; null outside the colour mode.</summary>
    internal string? ColorReadoutForTest { get; private set; }

    private double Footer => _colorMode ? _profile.ColorFooter : _profile.Footer;

    internal DrawingGroup DrawingForTest { get { RenderPending(); return _drawing; } }
    internal Size ImageSizeForTest => new(_profile.Image, _profile.Image);
    internal Point ImageOffsetForTest => new(_profile.Inset, _profile.Inset);
    internal Rect ColorBoundsForTest { get; private set; }
    internal Rect CoordinateBoundsForTest { get; private set; }
    internal Rect SwatchBoundsForTest { get; private set; }
    internal Rect DividerBoundsForTest { get; private set; }
    internal Brush ReticleBrushForTest => _reticleBrush;
    internal Rect CellBoundsForTest => new(_cellX * (_profile.Image / _columns), _cellY * (_profile.Image / _rows),
        _profile.Image / _columns, _profile.Image / _rows);
    internal IReadOnlyList<LineGeometry> GridLinesForTest => _gridLines;
    internal double FooterHeightForTest => Footer;
    internal int GridFillCount { get; private set; }
    internal int LabelLayoutCountForTest { get; private set; }
    internal int RenderCountForTest { get; private set; }
    internal Size LogicalSize => new(_profile.Image + 6, _profile.Image + Footer + 6);

    internal void Bind(int width, int height, IReadOnlyList<CaptureGuideArea> areas, double fallbackScale)
    {
        var profiles = new List<LayoutProfile>();
        Add(fallbackScale);
        for (int index = 0; index < areas.Count; index++) Add(areas[index].Scale);
        _profiles = profiles.ToArray();
        _profile = default;
        _colorText = _coordinateText = _nameText = _englishText = null;
        _drawnX = _drawnY = -1;
        _drawnColor = uint.MaxValue;
        _dirty = true;

        void Add(double scale)
        {
            if (!(scale > 0) || profiles.Any(item => item.Scale == scale)) return;
            double inset = Math.Ceiling(3 * scale) / scale, pixel = 1 / scale;
            FormattedText color = Text("#FFFFFF", ColorFontSize, _textBrush, scale);
            FormattedText coordinates = Text($"{Math.Max(0, width - 1)}, {Math.Max(0, height - 1)}", CoordinateFontSize,
                _mutedBrush, scale, _coordinateTypeface);
            // Window width less the footer padding on both sides, and the swatch before the value.
            double lead = SwatchSize + SwatchGap, sides = 2 * FooterPadding - 6;
            bool oneLine = sides + lead + color.Width + 8 + coordinates.Width <= 150 / scale;
            double cellPixels = oneLine ? 10
                : Math.Max(10, Math.Ceiling((sides + lead + Math.Max(color.Width, coordinates.Width)) * scale / 15));
            double content = oneLine ? MinimumFooterContent
                : Math.Max(MinimumFooterContent, 6 + color.Height + 2 + coordinates.Height + 6);
            // The colour mode: value, name and, in Korean, the CSS name on a third line.
            FormattedText name = new("Ag", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _uiTypeface,
                NameFontSize, _textBrush, scale);
            FormattedText english = Text("Ag", CoordinateFontSize, _mutedBrush, scale, _coordinateTypeface);
            double colorContent = Math.Max(MinimumFooterContent,
                6 + color.Height + 2 + name.Height + (_korean ? 1 + english.Height : 0) + 6);
            profiles.Add(new LayoutProfile(scale, cellPixels * 15 / scale, FooterFor(content), oneLine, color.Height,
                inset, TopCorners(cellPixels * 15 / scale), FooterFor(colorContent), name.Height, coordinates.Height));

            // Below the image: the inset, the divider, then the content band, less the panel's bottom band.
            double FooterFor(double band) => Math.Ceiling((2 * inset + pixel + band - 6) * scale - 0.0001) / scale;
        }
    }

    internal bool Update(double scale, int x, int y, int cropX, int cropY, int columns, int rows, byte[] sample)
    {
        bool layoutChanged = _profile.Scale != scale || _layoutPending;
        _layoutPending = false;
        if (layoutChanged)
        {
            for (int index = 0; index < _profiles.Length; index++)
                if (_profiles[index].Scale == scale) { _profile = _profiles[index]; break; }
            if (!(_profile.Scale > 0)) _profile = _profiles[0];
            _image.Width = _image.Height = _profile.Image;
            double pixel = 1 / _profile.Scale;
            _outlinePen.Thickness = _swatchPen.Thickness = _reticlePen.Thickness = _reticleInnerPen.Thickness = pixel;
            _gridPen.Thickness = 0.5 / _profile.Scale;
            _colorText = _coordinateText = _nameText = _englishText = null;
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
        _colorText = _coordinateText = _nameText = _englishText = null;
        ColorReadoutForTest = null;
        _dirty = true;
        _x = _y = 0;
        _color = 0;
    }

    protected override Size MeasureOverride(Size constraint) => new(_profile.Image, _profile.Image + Footer);

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
            byte red = (byte)(_color >> 16), green = (byte)(_color >> 8), blue = (byte)_color;
            if (_colorMode)
            {
                // Named once per new colour, here and not on the mouse message; 139 small distances.
                ColorNameMatch match = ColorNames.Nearest(red, green, blue);
                string value = ColorSampleFormatter.Format(red, green, blue, _format);
                string name = ColorNames.PrimaryName(match, _korean);
                _colorText = Bounded(Text(value, ColorFontSize, _textBrush, _profile.Scale));
                _nameText = Bounded(new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    _uiTypeface, NameFontSize, _textBrush, _profile.Scale));
                _englishText = _korean
                    ? Bounded(Text(match.Color.Name, CoordinateFontSize, _mutedBrush, _profile.Scale, _coordinateTypeface)) : null;
                ColorReadoutForTest = value + " " + ColorNames.Display(match, _korean);
            }
            else _colorText = Text($"#{_color:X6}", ColorFontSize, _textBrush, _profile.Scale);
            _swatch = new SolidColorBrush(Color.FromRgb(red, green, blue));
            _swatch.Freeze();
            _drawnColor = _color;
            LabelLayoutCountForTest++;
        }
        if (_coordinateText is null || _drawnX != _x || _drawnY != _y)
        {
            _coordinateText = Text($"{_x}, {_y}", CoordinateFontSize, _mutedBrush, _profile.Scale, _coordinateTypeface);
            _drawnX = _x; _drawnY = _y;
            LabelLayoutCountForTest++;
        }
        double pixel = 1 / _profile.Scale, inset = _profile.Inset, image = _profile.Image;
        // The panel spans the window: from -inset to the image's far side plus the rest of the 6-DIP band.
        double panelLeft = -inset, panelRight = image + 6 - inset, panelBottom = image + Footer + 6 - inset;
        using DrawingContext dc = _drawing.Open();
        if (_windowRounded)
        {
            // Paint the panel over the image's two top corners. A non-rectangular Image.Clip would
            // introduce a WPF intermediate surface; this bounded, frozen geometry needs none.
            dc.DrawGeometry(_cornerBrush, null, _profile.Corners);
        }
        else
        {
            dc.DrawRectangle(null, _outlinePen, new Rect(panelLeft + pixel / 2, -inset + pixel / 2,
                panelRight - panelLeft - pixel, panelBottom + inset - pixel));
        }
        bool integralCells = Math.Abs(image * _profile.Scale / _columns - Math.Round(image * _profile.Scale / _columns)) < 0.000001
            && Math.Abs(image * _profile.Scale / _rows - Math.Round(image * _profile.Scale / _rows)) < 0.000001;
        if (_quarterGridBrush is { } quarter && integralCells)
        {
            for (int index = 1; index < _columns; index++)
                dc.DrawRectangle(quarter, null, new Rect(index * image / _columns - pixel, 0, 2 * pixel, image));
            for (int index = 1; index < _rows; index++)
                dc.DrawRectangle(quarter, null, new Rect(0, index * image / _rows - pixel, image, 2 * pixel));
            GridFillCount++;
        }
        else
        {
            for (int index = 0; index < _gridLines.Length; index++) dc.DrawGeometry(null, _gridPen, _gridLines[index]);
        }
        // The centre pixel: one physical pixel of accent just outside it, one of white just inside.
        Rect cell = CellBoundsForTest;
        dc.DrawRectangle(null, _reticlePen, new Rect(cell.X - pixel / 2, cell.Y - pixel / 2, cell.Width + pixel, cell.Height + pixel));
        dc.DrawRectangle(null, _reticleInnerPen, new Rect(cell.X + pixel / 2, cell.Y + pixel / 2,
            Math.Max(0, cell.Width - pixel), Math.Max(0, cell.Height - pixel)));

        // A hairline across the window, the inset below the image, then the footer's content band.
        DividerBoundsForTest = new Rect(panelLeft, image + inset, panelRight - panelLeft, pixel);
        dc.DrawRectangle(_hairlineBrush, null, DividerBoundsForTest);
        double bandTop = DividerBoundsForTest.Bottom, band = panelBottom - bandTop;
        double left = Snap(panelLeft + FooterPadding), right = Snap(panelRight - FooterPadding);
        double valueX = left + SwatchSize + SwatchGap;
        if (_colorMode && _nameText is not null)
        {
            double block = _profile.ColorHeight + 2 + _profile.NameHeight + (_englishText is not null ? 1 + _profile.CoordinateHeight : 0);
            var colorAt = new Point(valueX, Snap(bandTop + (band - block) / 2));
            DrawSwatch(colorAt.Y + _profile.ColorHeight / 2);
            dc.DrawText(_colorText, colorAt);
            var nameAt = new Point(valueX, colorAt.Y + _profile.ColorHeight + 2);
            dc.DrawText(_nameText, nameAt);
            if (_englishText is not null) dc.DrawText(_englishText, new Point(valueX, nameAt.Y + _profile.NameHeight + 1));
            ColorBoundsForTest = new Rect(colorAt, new Size(_colorText.Width, _colorText.Height));
            CoordinateBoundsForTest = Rect.Empty;
            return;
        }
        Point valueAt, coordinateAt;
        if (_profile.OneLine)
        {
            valueAt = new Point(valueX, Snap(bandTop + (band - _profile.ColorHeight) / 2));
            coordinateAt = new Point(right - _coordinateText.Width, Snap(bandTop + (band - _profile.CoordinateHeight) / 2));
        }
        else
        {
            double block = _profile.ColorHeight + 2 + _profile.CoordinateHeight;
            valueAt = new Point(valueX, Snap(bandTop + (band - block) / 2));
            coordinateAt = new Point(valueX, valueAt.Y + _profile.ColorHeight + 2);
        }
        DrawSwatch(valueAt.Y + _profile.ColorHeight / 2);
        dc.DrawText(_colorText, valueAt);
        dc.DrawText(_coordinateText, coordinateAt);
        ColorBoundsForTest = new Rect(valueAt, new Size(_colorText.Width, _colorText.Height));
        CoordinateBoundsForTest = new Rect(coordinateAt, new Size(_coordinateText.Width, _coordinateText.Height));

        // A 10-DIP swatch with a 2-DIP radius and a hairline edge, centred on the value's line.
        void DrawSwatch(double middle)
        {
            SwatchBoundsForTest = new Rect(left, Snap(middle - SwatchSize / 2), SwatchSize, SwatchSize);
            Rect edge = new(SwatchBoundsForTest.X + pixel / 2, SwatchBoundsForTest.Y + pixel / 2,
                SwatchSize - pixel, SwatchSize - pixel);
            dc.DrawRoundedRectangle(_swatch, _swatchPen, edge, 2 - pixel / 2, 2 - pixel / 2);
        }
    }

    private double Snap(double value) => Math.Round(value * _profile.Scale) / _profile.Scale;

    private FormattedText Text(string value, double size, Brush brush, double scale, Typeface? typeface = null) =>
        new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface ?? _typeface, size, brush, scale);

    // A long value or name ends in an ellipsis inside the footer instead of running past the panel.
    private FormattedText Bounded(FormattedText text)
    {
        text.MaxTextWidth = Math.Max(1, _profile.Image + 6 - 2 * FooterPadding - SwatchSize - SwatchGap);
        text.MaxLineCount = 1;
        text.Trimming = TextTrimming.CharacterEllipsis;
        return text;
    }

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
        double ColorHeight, double Inset, Geometry Corners, double ColorFooter, double NameHeight, double CoordinateHeight);
}
