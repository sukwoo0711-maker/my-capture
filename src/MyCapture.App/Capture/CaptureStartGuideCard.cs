using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MyCapture.App.Themes;

namespace MyCapture.App.Capture;

/// <summary>
/// Short start guide shown in the middle of the selector's monitor for a user's first captures and
/// whenever the mode bar's help button is pressed: two small pictures, a window with the overlay's
/// accent frame and a click on it, and a dashed region dragged from one corner to the other, each
/// named underneath with the words the overlay's own window tab uses, and the Esc hint below them.
/// Without window detection only the drag picture remains. The bar's buttons explain themselves
/// with names and tooltips, so the card does not repeat them.
/// </summary>
/// <remarks>
/// The pictures are vector shapes painted from the selector's guide palette, so they follow all six
/// themes, the guide colour override and system high contrast, and scale with the card. The accent
/// marks only what the overlay itself highlights: the window frame and its tab, and the selection.
/// The card never takes input: clicks and drags go through to the selector, and the overlay view
/// decides when it fades away. UI Automation sees one group named "how to capture" whose help text
/// describes both actions, with the captions as its text children.
/// </remarks>
internal sealed class CaptureStartGuideCard : Border
{
    /// <summary>Size of each picture (and the width of its caption), in DIPs before display scaling.</summary>
    internal const double PictureWidth = 152;

    internal const double PictureHeight = 88;

    /// <summary>Space between the click and the drag column.</summary>
    internal const double ColumnGap = 20;

    private const double PointerArm = 8;

    private readonly FrameworkElement _clickColumn;
    private readonly FrameworkElement _dragColumn;
    private readonly TextBlock _clickCaption;
    private readonly TextBlock _dragCaption;
    private readonly TextBlock _escape;

    internal CaptureStartGuideCard()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = true;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(20, 14, 20, 14);
        SetResourceReference(BackgroundProperty, CaptureModeBar.SurfaceKey);
        SetResourceReference(BorderBrushProperty, CaptureModeBar.BorderKey);

        _clickCaption = Caption();
        _dragCaption = Caption();
        _clickColumn = Column(ClickPicture(), _clickCaption);
        _dragColumn = Column(DragPicture(), _dragCaption);
        var columns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        columns.Children.Add(_clickColumn);
        columns.Children.Add(_dragColumn);

        _escape = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        _escape.SetResourceReference(TextBlock.ForegroundProperty, CaptureModeBar.MutedKey);

        var content = new StackPanel();
        content.Children.Add(columns);
        content.Children.Add(_escape);
        Child = content;
        Configure(detectWindows: true);
    }

    /// <summary>The automation help text: both actions (only the drag without window detection) and Esc.</summary>
    internal string HelpText => AutomationProperties.GetHelpText(this);

    /// <summary>The captions under the pictures that are shown, without the joiners that keep Korean words whole.</summary>
    internal IReadOnlyList<string> ActionTexts =>
        new[] { (_clickColumn, _clickCaption), (_dragColumn, _dragCaption) }
            .Where(column => column.Item1.Visibility == Visibility.Visible)
            .Select(column => AutomationProperties.GetName(column.Item2))
            .ToArray();

    /// <summary>The Esc line, without word joiners.</summary>
    internal string EscapeText => AutomationProperties.GetName(_escape);

    /// <summary>What a click and a drag do (only a drag without window detection).</summary>
    internal void Configure(bool detectWindows)
    {
        _clickColumn.Visibility = detectWindows ? Visibility.Visible : Visibility.Collapsed;
        _dragColumn.Margin = new Thickness(detectWindows ? ColumnGap : 0, 0, 0, 0);
        KoreanWordWrap.SetText(_clickCaption, UiText.Get("CaptureGuide2_ClickWindow"));
        KoreanWordWrap.SetText(_dragCaption, UiText.Get("CaptureGuide2_DragCapture"));
        KoreanWordWrap.SetText(_escape, UiText.Get("CaptureGuide2_Esc"));
        AutomationProperties.SetName(this, UiText.Get("CaptureGuide2_IntroTitle"));
        AutomationProperties.SetHelpText(this,
            UiText.Get(detectWindows ? "CaptureGuide2_IntroBody" : "CaptureGuide2_IntroBodyRegionOnly"));
    }

    /// <summary>Same brushes as the mode bar.</summary>
    internal void ApplyPalette(IReadOnlyDictionary<string, Brush> palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        Resources[CaptureModeBar.SurfaceKey] = palette["CaptureGuide.ChipSurface"];
        Resources[CaptureModeBar.BorderKey] = palette["CaptureGuide.ChipBorder"];
        Resources[CaptureModeBar.TextKey] = palette["CaptureGuide.Text"];
        Resources[CaptureModeBar.MutedKey] = palette["CaptureGuide.MutedText"];
        Resources[CaptureModeBar.AccentKey] = palette["CaptureGuide.SelectionBorder"];
        Resources[CaptureModeBar.AccentTextKey] = palette["CaptureGuide.AccentText"];
        Resources[CaptureModeBar.HoverKey] = palette["CaptureGuide.InnerGlow"];
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CardAutomationPeer(this);

    private static TextBlock Caption()
    {
        var caption = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
            MaxWidth = PictureWidth,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, CaptureModeBar.TextKey);
        return caption;
    }

    private static FrameworkElement Column(UIElement picture, TextBlock caption)
    {
        var column = new StackPanel { Width = PictureWidth };
        column.Children.Add(picture);
        column.Children.Add(caption);
        return column;
    }

    /// <summary>
    /// A window with the overlay's accent frame and the tab above its top-left corner, and the
    /// selector's crosshair on it with three short marks for the click.
    /// </summary>
    private static Canvas ClickPicture()
    {
        var window = new Rect(22, 18, 108, 62);
        const double band = 2.5;
        var center = new Point(100, 58);
        var picture = Picture();
        // Title bar and a few lines of content, quieter than the frame around them.
        var titleBar = new LineGeometry(new Point(window.Left, window.Top + 12), new Point(window.Right, window.Top + 12));
        picture.Children.Add(Stroke(Frozen(titleBar), CaptureModeBar.MutedKey, 1.25, opacity: 0.7));
        picture.Children.Add(Stroke(Frozen(Geometry.Parse("M32,42 L84,42 M32,52 L104,52 M32,62 L90,62 M32,72 L70,72")),
            CaptureModeBar.MutedKey, 3, opacity: 0.45));
        // The accent band lies inside the window's bounds, as it does on screen.
        var inner = new Rect(window.Left + (band / 2), window.Top + (band / 2), window.Width - band, window.Height - band);
        picture.Children.Add(Stroke(Frozen(new RectangleGeometry(inner, 2, 2)), CaptureModeBar.AccentKey, band));
        picture.Children.Add(Fill(Frozen(TopTab(new Rect(window.Left, window.Top - 11, 42, 11), 3)), CaptureModeBar.AccentKey));
        picture.Children.Add(Stroke(Frozen(new LineGeometry(new Point(window.Left + 6, window.Top - 5.5),
            new Point(window.Left + 30, window.Top - 5.5))), CaptureModeBar.AccentTextKey, 2));
        AddPointer(picture, center);
        var ticks = new StreamGeometry();
        using (StreamGeometryContext g = ticks.Open())
        {
            foreach (double degrees in new[] { -12.0, -45.0, -78.0 })
            {
                double radians = degrees * Math.PI / 180;
                var direction = new Vector(Math.Cos(radians), Math.Sin(radians));
                g.BeginFigure(center + (direction * 12), isFilled: false, isClosed: false);
                g.LineTo(center + (direction * 16.5), isStroked: true, isSmoothJoin: false);
            }
        }
        picture.Children.Add(Stroke(Frozen(ticks), CaptureModeBar.TextKey, 1.6));
        return picture;
    }

    /// <summary>
    /// A dashed accent selection with the start point on its top-left corner, an arrow along the drag
    /// and the selector's crosshair on the corner where the drag ends.
    /// </summary>
    private static Canvas DragPicture()
    {
        var region = new Rect(26, 13, 98, 60);
        var picture = Picture();
        var selection = Stroke(Frozen(new RectangleGeometry(region)), CaptureModeBar.AccentKey, 1.5);
        selection.StrokeDashArray = [3, 2];
        selection.StrokeDashCap = PenLineCap.Flat;
        selection.StrokeStartLineCap = selection.StrokeEndLineCap = PenLineCap.Flat;
        selection.SetResourceReference(Shape.FillProperty, CaptureModeBar.HoverKey);
        picture.Children.Add(selection);

        Vector along = region.BottomRight - region.TopLeft;
        double length = along.Length;
        along.Normalize();
        Point from = region.TopLeft + (along * 13);
        Point to = region.TopLeft + (along * (length - 28));
        var arrow = new StreamGeometry();
        using (StreamGeometryContext g = arrow.Open())
        {
            g.BeginFigure(from, isFilled: false, isClosed: false);
            g.LineTo(to, isStroked: true, isSmoothJoin: false);
            var head = new Matrix();
            head.Rotate(150);
            Vector left = head.Transform(along) * 7;
            head.Rotate(60);
            Vector right = head.Transform(along) * 7;
            g.BeginFigure(to + left, isFilled: false, isClosed: false);
            g.LineTo(to, isStroked: true, isSmoothJoin: true);
            g.LineTo(to + right, isStroked: true, isSmoothJoin: true);
        }
        picture.Children.Add(Stroke(Frozen(arrow), CaptureModeBar.TextKey, 1.6));

        var start = Stroke(Frozen(new EllipseGeometry(region.TopLeft, 3.5, 3.5)), CaptureModeBar.TextKey, 1.5);
        start.SetResourceReference(Shape.FillProperty, CaptureModeBar.SurfaceKey);
        picture.Children.Add(start);
        AddPointer(picture, region.BottomRight);
        return picture;
    }

    /// <summary>The selector's crosshair, on a surface-coloured halo that keeps it clear of what is under it.</summary>
    private static void AddPointer(Canvas picture, Point center)
    {
        var cross = new GeometryGroup();
        cross.Children.Add(new LineGeometry(new Point(center.X - PointerArm, center.Y), new Point(center.X + PointerArm, center.Y)));
        cross.Children.Add(new LineGeometry(new Point(center.X, center.Y - PointerArm), new Point(center.X, center.Y + PointerArm)));
        Geometry frozen = Frozen(cross);
        picture.Children.Add(Stroke(frozen, CaptureModeBar.SurfaceKey, 4.5));
        picture.Children.Add(Stroke(frozen, CaptureModeBar.TextKey, 1.6));
    }

    private static Canvas Picture() => new()
    {
        Width = PictureWidth,
        Height = PictureHeight,
        ClipToBounds = true,
        IsHitTestVisible = false,
    };

    private static Path Stroke(Geometry data, string brushKey, double thickness, double opacity = 1)
    {
        var path = new Path
        {
            Data = data,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Opacity = opacity,
        };
        path.SetResourceReference(Shape.StrokeProperty, brushKey);
        return path;
    }

    private static Path Fill(Geometry data, string brushKey)
    {
        var path = new Path { Data = data };
        path.SetResourceReference(Shape.FillProperty, brushKey);
        return path;
    }

    /// <summary>A tab rounded on its free top edge and square where it meets the frame below.</summary>
    private static StreamGeometry TopTab(Rect r, double radius)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext g = geometry.Open();
        g.BeginFigure(new Point(r.Left, r.Bottom), isFilled: true, isClosed: true);
        g.LineTo(new Point(r.Left, r.Top + radius), false, false);
        g.ArcTo(new Point(r.Left + radius, r.Top), new Size(radius, radius), 0, false, SweepDirection.Clockwise, false, false);
        g.LineTo(new Point(r.Right - radius, r.Top), false, false);
        g.ArcTo(new Point(r.Right, r.Top + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, false, false);
        g.LineTo(new Point(r.Right, r.Bottom), false, false);
        return geometry;
    }

    private static Geometry Frozen(Geometry geometry)
    {
        if (geometry.CanFreeze) geometry.Freeze();
        return geometry;
    }

    /// <summary>One group with the card's name and help text; the captions are its text children.</summary>
    private sealed class CardAutomationPeer(CaptureStartGuideCard owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(CaptureStartGuideCard);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
    }
}
