using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MyCapture.Core.Primitives;

namespace MyCapture.App.Editing;

/// <summary>One rendered cursor image and the pixel that clicks.</summary>
internal readonly record struct EditorCursorImage(BitmapSource Bitmap, int HotSpotX, int HotSpotY);

/// <summary>
/// The fill and outline of the user's Windows pointer (Accessibility, Mouse pointer, Style),
/// so tool cursors look like the arrow they replace.
/// </summary>
internal readonly record struct EditorCursorStyle(Color Body, Color Outline)
{
    internal static readonly Color Ink = Color.FromRgb(0x0B, 0x0F, 0x17);

    /// <summary>The default Windows pointer: white with a black outline.</summary>
    internal static EditorCursorStyle White => new(Colors.White, Ink);

    /// <summary>Maps the stored pointer style: 0 white, 1 black, 2 inverted, 6 custom colour.</summary>
    /// <param name="cursorType">HKCU\Software\Microsoft\Accessibility CursorType.</param>
    /// <param name="cursorColor">CursorColor as a COLORREF (0x00BBGGRR).</param>
    internal static EditorCursorStyle FromSettings(int? cursorType, int? cursorColor)
    {
        switch (cursorType)
        {
            case 1:
                return new EditorCursorStyle(Ink, Colors.White);
            case 6 when cursorColor is int value:
                var body = Color.FromRgb((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));
                // Windows outlines coloured pointers in black; a very dark colour needs a light edge instead.
                double luminance = ((0.2126 * body.R) + (0.7152 * body.G) + (0.0722 * body.B)) / 255;
                return new EditorCursorStyle(body, luminance < 0.2 ? Colors.White : Ink);
            default:
                // Inverted pointers have no fixed colour; the white style reads on every capture.
                return White;
        }
    }

    /// <summary>Reads the current pointer style, or the white style when it cannot be read.</summary>
    internal static EditorCursorStyle Read()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            return FromSettings(key?.GetValue("CursorType") as int?, key?.GetValue("CursorColor") as int?);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return White;
        }
    }
}

/// <summary>
/// Turns <see cref="EditorCursorKind"/> into a WPF cursor. System shapes come from Windows;
/// tool shapes are drawn at run time. Brush locators mark the exact stroke centre while
/// the surface independently draws the effective brush width.
/// </summary>
/// <remarks>
/// <para>
/// No cursor file ships with the app (the repository adds no binary assets). Each tool cursor
/// is rendered from vector geometry at the exact pixel size the pointer uses on the editor's
/// monitor (the user's pointer size times the monitor scale), packed as an in-memory 32-bit
/// .cur and loaded at that size. The hot spot is placed on a pixel centre at every size: the
/// brush centre, the inner crop corner, or the crosshair centre.
/// </para>
/// <para>
/// Tool cursors take the fill and outline of the user's pointer style (white with a black
/// outline by default), so they read on light and dark captures and match the arrow. Under a
/// high-contrast theme the editor keeps system cursors only.
/// </para>
/// </remarks>
internal static class EditorCursors
{
    /// <summary>Pointer size at 100% scale with the default Windows pointer size.</summary>
    internal const int DefaultBaseSize = 32;

    // Glyphs are authored in a 32x32 space and scaled to the requested size.
    private const double Design = 32;
    private static readonly object Gate = new();
    private static readonly Dictionary<(EditorCursorKind Kind, int Size, int Number, ColorRgba Color, EditorCursorStyle Style), Cursor> Cache = [];

    private static readonly Color Ink = EditorCursorStyle.Ink;
    private static readonly Color Marker = Color.FromRgb(0xFF, 0xD6, 0x0A);

    /// <summary>The pointer edge length in pixels for a monitor scale and Windows pointer size.</summary>
    internal static int PixelSize(int baseSize, double dpiScale)
    {
        double scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        return Math.Clamp((int)Math.Round(Math.Clamp(baseSize, DefaultBaseSize, 256) * scale), 16, 256);
    }

    /// <summary>Reads the Windows pointer size (Accessibility, Mouse pointer, Size).</summary>
    internal static int ReadBaseSize()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors");
            if (key?.GetValue("CursorBaseSize") is int value) return Math.Clamp(value, DefaultBaseSize, 256);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
        }

        return DefaultBaseSize;
    }

    /// <summary>Returns the cursor for <paramref name="kind"/>.</summary>
    /// <param name="pixelSize">Edge length from <see cref="PixelSize"/>.</param>
    /// <param name="stepNumber">The number the step tool adds next.</param>
    /// <param name="stepColor">The badge colour the step tool uses.</param>
    /// <param name="highContrast">True to keep system cursors only.</param>
    /// <param name="style">The user's pointer colours.</param>
    internal static Cursor Get(EditorCursorKind kind, int pixelSize, int stepNumber, ColorRgba stepColor, bool highContrast, EditorCursorStyle style)
    {
        if (SystemCursor(kind, highContrast) is { } system) return system;

        bool numbered = kind == EditorCursorKind.StepNumber;
        var key = (kind, pixelSize, numbered ? stepNumber : 0, numbered ? stepColor : default, style);
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out Cursor? cached)) return cached;
        }

        Cursor cursor;
        try
        {
            EditorCursorImage image = Render(kind, pixelSize, stepNumber, stepColor, style);
            using var stream = new MemoryStream(BuildCursorFile(image));
            cursor = new Cursor(stream, scaleWithDpi: false);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceWarning("Editor cursor {0} could not be created: {1}", kind, ex.Message);
            return Cursors.Cross;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out Cursor? raced)) return raced;
            Cache[key] = cursor;
        }

        return cursor;
    }

    /// <summary>The Windows cursor for system kinds, or for every kind under high contrast.</summary>
    internal static Cursor? SystemCursor(EditorCursorKind kind, bool highContrast) => kind switch
    {
        EditorCursorKind.Arrow => Cursors.Arrow,
        EditorCursorKind.Move => Cursors.SizeAll,
        EditorCursorKind.ResizeNorthwestSoutheast => Cursors.SizeNWSE,
        EditorCursorKind.ResizeNortheastSouthwest => Cursors.SizeNESW,
        EditorCursorKind.ResizeNorthSouth => Cursors.SizeNS,
        EditorCursorKind.ResizeWestEast => Cursors.SizeWE,
        EditorCursorKind.TextBeam => Cursors.IBeam,
        EditorCursorKind.Cross => Cursors.Cross,
        EditorCursorKind.Pen or EditorCursorKind.Highlighter when highContrast => Cursors.Pen,
        _ when highContrast => Cursors.Cross,
        _ => null,
    };

    /// <summary>Draws one tool cursor at <paramref name="size"/> pixels.</summary>
    internal static EditorCursorImage Render(EditorCursorKind kind, int size, int stepNumber, ColorRgba stepColor, EditorCursorStyle style)
    {
        if (!EditorCursorPolicy.IsCustom(kind)) throw new ArgumentOutOfRangeException(nameof(kind), kind, "System cursors are not drawn.");
        size = Math.Clamp(size, 16, 256);
        double k = size / Design;
        Point anchor = Anchor(kind);
        int hotX = Math.Clamp((int)Math.Floor(anchor.X * k), 0, size - 1);
        int hotY = Math.Clamp((int)Math.Floor(anchor.Y * k), 0, size - 1);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            // Lands the anchor exactly on the hot spot pixel's centre at every size.
            dc.PushTransform(new MatrixTransform(k, 0, 0, k, hotX + 0.5 - (anchor.X * k), hotY + 0.5 - (anchor.Y * k)));
            switch (kind)
            {
                case EditorCursorKind.Pen:
                case EditorCursorKind.Highlighter:
                    DrawBrushLocator(dc, anchor, style);
                    break;
                case EditorCursorKind.Crop:
                    DrawOutline(dc, IconGeometry("Icon.Crop", CropFallback), new Point(5, 5), anchor, 1.3, style);
                    break;
                default:
                    DrawCrosshair(dc, anchor, style);
                    if (kind == EditorCursorKind.StepNumber) DrawStepBadge(dc, stepNumber, stepColor);
                    else DrawToolBadge(dc, kind, style);
                    break;
            }

            dc.Pop();
        }

        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var straight = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
        straight.Freeze();
        return new EditorCursorImage(straight, hotX, hotY);
    }

    /// <summary>Packs one 32-bit image as a Windows .cur file.</summary>
    internal static byte[] BuildCursorFile(EditorCursorImage image)
    {
        BitmapSource bitmap = image.Bitmap.Format == PixelFormats.Bgra32
            ? image.Bitmap
            : new FormatConvertedBitmap(image.Bitmap, PixelFormats.Bgra32, null, 0);
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        int maskStride = ((width + 31) / 32) * 4;
        int imageBytes = 40 + pixels.Length + (maskStride * height);

        using var memory = new MemoryStream(6 + 16 + imageBytes);
        using (var writer = new BinaryWriter(memory, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            // ICONDIR: reserved, type 2 (cursor), one image.
            writer.Write((ushort)0);
            writer.Write((ushort)2);
            writer.Write((ushort)1);
            // ICONDIRENTRY: 256 is stored as 0; a cursor keeps its hot spot where an icon keeps
            // its plane and bit counts.
            writer.Write((byte)(width >= 256 ? 0 : width));
            writer.Write((byte)(height >= 256 ? 0 : height));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)image.HotSpotX);
            writer.Write((ushort)image.HotSpotY);
            writer.Write(imageBytes);
            writer.Write(6 + 16);
            // BITMAPINFOHEADER: the height covers the colour rows and the AND mask rows.
            writer.Write(40);
            writer.Write(width);
            writer.Write(height * 2);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(0);
            writer.Write(pixels.Length + (maskStride * height));
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            // Bottom-up BGRA rows, then the 1-bit AND mask (set where fully transparent).
            for (int row = height - 1; row >= 0; row--) writer.Write(pixels, row * stride, stride);
            byte[] mask = new byte[maskStride];
            for (int row = height - 1; row >= 0; row--)
            {
                Array.Clear(mask);
                for (int x = 0; x < width; x++)
                {
                    if (pixels[(row * stride) + (x * 4) + 3] == 0) mask[x / 8] |= (byte)(0x80 >> (x % 8));
                }

                writer.Write(mask);
            }
        }

        return memory.ToArray();
    }

    // The pen icon on its 20x20 grid; the editor's shared Icon.Pen resource wins when loaded.
    private const string PenFallback =
        "M4.1,15.9 L5.1,12.1 L12.35,4.85 A1.8,1.8 0 0 1 14.9,4.85 L15.15,5.1 A1.8,1.8 0 0 1 15.15,7.65 L7.9,14.9 Z M11.25,5.95 L14.05,8.75 M4.1,15.9 L7.9,14.9";

    private const string CropFallback = "M5,2 V14 A1,1 0 0 0 6,15 H18 M2,5 H14 A1,1 0 0 1 15,6 V18";

    // The highlighter rail icon's marker body, and the inked band at its chisel tip.
    private const string HighlighterBody = "M4,14 L13,3 L17,7 L8,18 Z";
    private const string HighlighterTip = "M4,14 L5.4,12.3 L9.4,16.3 L8,18 Z";

    private static Point Anchor(EditorCursorKind kind) => kind switch
    {
        EditorCursorKind.Pen or EditorCursorKind.Highlighter => new Point(15.5, 15.5),
        EditorCursorKind.Crop => new Point(8.5, 8.5),
        _ => new Point(10.5, 10.5),
    };

    private static Geometry IconGeometry(string key, string fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is Geometry shared) return shared;
        }
        catch (ResourceReferenceKeyNotFoundException)
        {
        }

        return Geometry.Parse(fallback);
    }

    /// <summary>A filled tool silhouette in the pointer colours, with an optional coloured tip.</summary>
    private static void DrawTool(DrawingContext dc, Geometry icon, Point iconTip, Point anchor, double scale, Geometry? marker, EditorCursorStyle style)
    {
        var place = new MatrixTransform(scale, 0, 0, scale, anchor.X - (iconTip.X * scale), anchor.Y - (iconTip.Y * scale));
        dc.PushTransform(place);
        double stroke = 1.5 / scale;
        dc.DrawGeometry(new SolidColorBrush(style.Body), RoundPen(style.Outline, stroke), icon);
        if (marker is not null) dc.DrawGeometry(new SolidColorBrush(Marker), RoundPen(style.Outline, stroke), marker);
        dc.Pop();
    }

    /// <summary>An outline-only icon drawn in the pointer colour on an outline-coloured edge.</summary>
    private static void DrawOutline(DrawingContext dc, Geometry icon, Point iconAnchor, Point anchor, double scale, EditorCursorStyle style)
    {
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, anchor.X - (iconAnchor.X * scale), anchor.Y - (iconAnchor.Y * scale)));
        dc.DrawGeometry(null, RoundPen(style.Outline, 4.2 / scale), icon);
        dc.DrawGeometry(null, RoundPen(style.Body, 1.8 / scale), icon);
        dc.Pop();
    }

    private static void DrawCrosshair(DrawingContext dc, Point c, EditorCursorStyle style)
    {
        var lines = new GeometryGroup();
        foreach ((double dx, double dy) in new[] { (0.0, -1.0), (0.0, 1.0), (-1.0, 0.0), (1.0, 0.0) })
        {
            lines.Children.Add(new LineGeometry(new Point(c.X + (dx * 2.5), c.Y + (dy * 2.5)), new Point(c.X + (dx * 9.5), c.Y + (dy * 9.5))));
        }

        dc.DrawGeometry(null, RoundPen(style.Outline, 3.4), lines);
        dc.DrawGeometry(new SolidColorBrush(style.Outline), null, new EllipseGeometry(c, 1.8, 1.8));
        dc.DrawGeometry(null, RoundPen(style.Body, 1.3), lines);
        dc.DrawGeometry(new SolidColorBrush(style.Body), null, new EllipseGeometry(c, 0.9, 0.9));
    }

    private static void DrawBrushLocator(DrawingContext dc, Point center, EditorCursorStyle style)
    {
        var lines = new GeometryGroup();
        lines.Children.Add(new LineGeometry(new Point(center.X - 4, center.Y), new Point(center.X + 4, center.Y)));
        lines.Children.Add(new LineGeometry(new Point(center.X, center.Y - 4), new Point(center.X, center.Y + 4)));
        dc.DrawGeometry(null, RoundPen(style.Outline, 3.2), lines);
        dc.DrawGeometry(null, RoundPen(style.Body, 1.2), lines);
    }

    private static readonly Rect BadgePlate = new(15.5, 15.5, 15, 15);

    private static void DrawToolBadge(DrawingContext dc, EditorCursorKind kind, EditorCursorStyle style)
    {
        (string key, string fallback) = kind switch
        {
            EditorCursorKind.Mosaic => ("Icon.Mosaic",
                "M4,3.75 L16,3.75 A1.25,1.25 0 0 1 17.25,5 L17.25,15 A1.25,1.25 0 0 1 16,16.25 L4,16.25 A1.25,1.25 0 0 1 2.75,15 L2.75,5 A1.25,1.25 0 0 1 4,3.75 Z M10,3.75 L10,10 M2.75,10 L17.25,10 M10,10 L10,16.25"),
            EditorCursorKind.Blur => ("Icon.Blur",
                "M10,2.75 C10,2.75 4.75,8.6 4.75,12 A5.25,5.25 0 0 0 15.25,12 C15.25,8.6 10,2.75 10,2.75 Z M7.6,12.4 A2.4,2.4 0 0 0 10,14.8"),
            _ => ("Icon.Image",
                "M4,3.75 L16,3.75 A1.25,1.25 0 0 1 17.25,5 L17.25,15 A1.25,1.25 0 0 1 16,16.25 L4,16.25 A1.25,1.25 0 0 1 2.75,15 L2.75,5 A1.25,1.25 0 0 1 4,3.75 Z M3,13.5 L7.4,9.25 L10.4,12.1 L12.6,10.1 L17,14 M13.6,7.2 A1.15,1.15 0 1 0 13.59,7.2"),
        };
        dc.DrawRoundedRectangle(new SolidColorBrush(style.Body), new Pen(new SolidColorBrush(style.Outline), 1), BadgePlate, 3, 3);
        const double scale = 0.66;
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, BadgePlate.X + (BadgePlate.Width / 2) - (10 * scale), BadgePlate.Y + (BadgePlate.Height / 2) - (10 * scale)));
        dc.DrawGeometry(null, RoundPen(style.Outline, 1.25 / scale), IconGeometry(key, fallback));
        dc.Pop();
    }

    private static void DrawStepBadge(DrawingContext dc, int number, ColorRgba color)
    {
        // The badge previews the annotation itself, so it keeps the annotation's colours.
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Ink), 2.6), BadgePlate, 3, 3);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)), new Pen(Brushes.White, 1.1), BadgePlate, 3, 3);
        string text = Math.Max(1, number) > 999 ? "…" : Math.Max(1, number).ToString(CultureInfo.InvariantCulture);
        double fontSize = text.Length switch { 1 => 11.5, 2 => 10, _ => 7.5 };
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), fontSize, Brushes.White, 1.0);
        var origin = new Point(BadgePlate.X + ((BadgePlate.Width - formatted.Width) / 2), BadgePlate.Y + ((BadgePlate.Height - formatted.Height) / 2));
        Geometry glyphs = formatted.BuildGeometry(origin);
        // The placed badge draws white digits with a dark outline; the preview matches it.
        dc.DrawGeometry(null, RoundPen(Ink, 1.6), glyphs);
        dc.DrawGeometry(Brushes.White, null, glyphs);
    }

    private static Pen RoundPen(Color color, double thickness) => new(new SolidColorBrush(color), thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };
}
