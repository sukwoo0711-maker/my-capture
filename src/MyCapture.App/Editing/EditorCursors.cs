using MyCapture.App.Themes;
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

    /// <summary>
    /// The colours of a cursor that shows the tool's ink: the ink itself, opaque so a faint or
    /// see-through colour still shows, inside whichever of a white and a near-black edge contrasts
    /// with it more (WCAG contrast ratio, at least 4.3:1 for any ink), so the pointer reads on
    /// light and dark captures alike.
    /// </summary>
    internal static EditorCursorStyle ForInk(ColorRgba ink)
    {
        var body = Color.FromRgb(ink.R, ink.G, ink.B);
        double light = ContrastRatio(body, Colors.White), dark = ContrastRatio(body, Ink);
        return new EditorCursorStyle(body, light > dark ? Colors.White : Ink);
    }

    /// <summary>The WCAG 2 contrast ratio of two opaque colours, 1 to 21.</summary>
    internal static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
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
/// tool shapes are drawn at run time. The pen and the highlighter click at the centre of the
/// footprint the surface draws at the true width, and the eraser at the centre of the ring its
/// cursor draws for its reach; their small icon sits beside the footprint or ring with its tip
/// touching the upper-right edge.
/// </summary>
/// <remarks>
/// <para>
/// No cursor file ships with the app (the repository adds no binary assets). Each tool cursor
/// is rendered from vector geometry at the exact pixel size the pointer uses on the editor's
/// monitor (the user's pointer size times the monitor scale), packed as an in-memory 32-bit
/// .cur and loaded at that size. The hot spot is placed on a pixel centre at every size: the
/// footprint's centre for the brush-like tools, the inner crop corner, the I-beam's middle, or
/// the crosshair centre.
/// </para>
/// <para>
/// The drawn glyphs stay in the size class of the Windows pointers they stand beside, so the
/// pointer never hides the pixels it is about to change: in a 32-pixel frame the shape crosshair
/// spans about 19 pixels like the Windows precision cross, a tool badge adds a 9-pixel tag below
/// and to the right of it, and the pen, highlighter and eraser icons are about 14 pixels, smaller
/// than the arrow. Only the footprint, which the surface draws, grows with the brush width.
/// </para>
/// <para>
/// A brush cursor is larger than the pointer size by the offset its icon sits from the footprint
/// (see <see cref="BrushOffset"/>): the icon is drawn as it is at offset 0, moved right by the
/// offset, and the hot spot moves down by the same amount, so the tip lands up and to the right
/// of the click point. Which offsets are in use depends on the width and the zoom, so only the
/// last few brush cursors are kept; cursors that show a colour are kept the same way.
/// </para>
/// <para>
/// Cursors of tools that draw in a colour show that colour (<see cref="EditorCursorPolicy.ShowsInk"/>)
/// inside a contrasting edge (<see cref="EditorCursorStyle.ForInk"/>). The other tool cursors take
/// the fill and outline of the user's pointer style (white with a black outline by default). Under
/// a high-contrast theme the editor keeps system cursors only.
/// </para>
/// </remarks>
internal static class EditorCursors
{
    /// <summary>Pointer size at 100% scale with the default Windows pointer size.</summary>
    internal const int DefaultBaseSize = 32;

    // Glyphs are authored in a 32x32 space and scaled to the requested size.
    private const double Design = 32;
    private static readonly object Gate = new();
    private static readonly Dictionary<CursorKey, Cursor> Cache = [];

    // Brush cursors in the order they were last used, oldest first; at most BrushCursorLimit stay.
    private static readonly List<CursorKey> BrushOrder = [];

    // The other cursors that depend on the ink or the step number, kept the same way.
    private static readonly List<CursorKey> InkOrder = [];

    private readonly record struct CursorKey(EditorCursorKind Kind, int Size, int Number, ColorRgba Color, EditorCursorStyle Style, int BrushOffset);

    /// <summary>How many pen, highlighter and eraser cursors are kept; the least recently used one is dropped beyond that.</summary>
    /// <remarks>The pen and highlighter vary with the footprint and the ink; the eraser with the monitor scale.</remarks>
    internal const int BrushCursorLimit = 8;

    /// <summary>How many crosshair, I-beam and step cursors with their colour or number are kept.</summary>
    internal const int InkCursorLimit = 16;

    /// <summary>How many brush cursors are kept now, for tests.</summary>
    internal static int BrushCursorCount
    {
        get { lock (Gate) return BrushOrder.Count; }
    }

    /// <summary>How many colour or number cursors are kept now, for tests.</summary>
    internal static int InkCursorCount
    {
        get { lock (Gate) return InkOrder.Count; }
    }

    private static readonly Color Ink = EditorCursorStyle.Ink;

    /// <summary>The pointer edge length in pixels for a monitor scale and Windows pointer size.</summary>
    internal static int PixelSize(int baseSize, double dpiScale)
    {
        double scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        return Math.Clamp((int)Math.Round(Math.Clamp(baseSize, DefaultBaseSize, 256) * scale), 16, 256);
    }

    /// <summary>True for the cursors whose icon sits beside a footprint the surface draws.</summary>
    internal static bool IsBrush(EditorCursorKind kind) => kind is EditorCursorKind.Pen or EditorCursorKind.Highlighter;

    /// <summary>
    /// The eraser's reach drawn by its cursor, in pixels, when the caller gives none: the reach in
    /// DIP at the scale the pointer size implies for the default Windows pointer.
    /// </summary>
    private static double DefaultEraserRadius(int size) => AnnotationEditorController.EraserRadiusDip * size / DefaultBaseSize;

    /// <summary>
    /// How many pixels the icon of a brush cursor is moved right (and the click point down) so
    /// the tip sits outside the footprint, one pixel clear of its edge at 45 degrees.
    /// </summary>
    /// <param name="pixelSize">Edge length from <see cref="PixelSize"/>.</param>
    /// <param name="footprintRadius">The footprint's radius in screen pixels. A value that is not a
    /// number, or is negative, means no footprint and gives 0.</param>
    /// <returns>The offset, at most as large as keeps the cursor within 256 pixels.</returns>
    internal static int BrushOffset(int pixelSize, double footprintRadius)
    {
        if (!double.IsFinite(footprintRadius) || footprintRadius < 0) return 0;
        double room = 256 - Math.Clamp(pixelSize, 16, 256);
        return (int)Math.Clamp(Math.Ceiling((footprintRadius + 1) / Math.Sqrt(2)), 0, room);
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
    /// <param name="ink">The colour the tool draws with: the step badge's colour, the pen's ink, and so on.
    /// It is part of what makes a cursor distinct only for <see cref="EditorCursorPolicy.ShowsInk"/> kinds.</param>
    /// <param name="highContrast">True to keep system cursors only.</param>
    /// <param name="style">The user's pointer colours.</param>
    /// <param name="brushOffset">For the pen and the highlighter, the offset from <see cref="BrushOffset"/>; it is part of
    /// what makes a cursor distinct for those kinds only.</param>
    /// <param name="eraserRadius">For the eraser, the radius of its reach in screen pixels; not a number means
    /// <see cref="DefaultEraserRadius"/>.</param>
    internal static Cursor Get(EditorCursorKind kind, int pixelSize, int stepNumber, ColorRgba ink, bool highContrast, EditorCursorStyle style,
        int brushOffset = 0, double eraserRadius = double.NaN)
    {
        if (SystemCursor(kind, highContrast) is { } system) return system;

        bool numbered = kind == EditorCursorKind.StepNumber;
        bool inked = EditorCursorPolicy.ShowsInk(kind);
        bool eraser = kind == EditorCursorKind.Eraser;
        bool brush = IsBrush(kind) || eraser;
        double reach = eraser ? EraserRadius(pixelSize, eraserRadius) : 0;
        // The eraser's reach takes the offset's place in its identity, in quarter pixels.
        int offset = IsBrush(kind) ? ClampBrushOffset(pixelSize, brushOffset) : eraser ? (int)Math.Round(reach * 4) : 0;
        var key = new CursorKey(kind, pixelSize, numbered ? stepNumber : 0, inked ? Opaque(ink) : default, style, offset);
        List<CursorKey>? order = brush ? BrushOrder : inked || numbered ? InkOrder : null;
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out Cursor? cached))
            {
                if (order is not null) Use(order, key);
                return cached;
            }
        }

        Cursor cursor;
        try
        {
            EditorCursorImage image = eraser
                ? RenderEraser(pixelSize, reach, style)
                : Render(kind, pixelSize, stepNumber, ink, style, offset);
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
            if (Cache.TryGetValue(key, out Cursor? raced))
            {
                if (order is not null) Use(order, key);
                return raced;
            }

            Cache[key] = cursor;
            if (order is not null)
            {
                order.Add(key);
                int limit = brush ? BrushCursorLimit : InkCursorLimit;
                // Dropped from the cache only: the evicted cursor may still be on the viewport, and
                // its handle is released with the object once nothing shows it.
                while (order.Count > limit)
                {
                    Cache.Remove(order[0]);
                    order.RemoveAt(0);
                }
            }
        }

        return cursor;
    }

    // Callers hold Gate.
    private static void Use(List<CursorKey> order, CursorKey key)
    {
        order.Remove(key);
        order.Add(key);
    }

    private static ColorRgba Opaque(ColorRgba color) => new(byte.MaxValue, color.R, color.G, color.B);

    private static double EraserRadius(int size, double radius) =>
        Math.Clamp(double.IsFinite(radius) && radius > 0 ? radius : DefaultEraserRadius(Math.Clamp(size, 16, 256)), 1, 96);

    private static int ClampBrushOffset(int pixelSize, int offset) => Math.Clamp(offset, 0, 256 - Math.Clamp(pixelSize, 16, 256));

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
        EditorCursorKind.Unavailable => Cursors.No,
        EditorCursorKind.Pen or EditorCursorKind.Highlighter when highContrast => Cursors.Pen,
        EditorCursorKind.TextInk when highContrast => Cursors.IBeam,
        _ when highContrast => Cursors.Cross,
        _ => null,
    };

    /// <summary>Draws one tool cursor at <paramref name="size"/> pixels.</summary>
    /// <param name="ink">The colour the tool draws with; shown by <see cref="EditorCursorPolicy.ShowsInk"/> kinds only.</param>
    /// <param name="brushOffset">For the brush-like kinds, see <see cref="BrushOffset"/>. The image is then that
    /// many pixels larger each way: the icon moves right and the hot spot down by the offset, so the tip lands at the
    /// hot spot plus (offset, -offset). 0 draws the icon with its tip on the hot spot. Ignored for other kinds.</param>
    internal static EditorCursorImage Render(EditorCursorKind kind, int size, int stepNumber, ColorRgba ink, EditorCursorStyle style, int brushOffset = 0)
    {
        if (!EditorCursorPolicy.IsCustom(kind)) throw new ArgumentOutOfRangeException(nameof(kind), kind, "System cursors are not drawn.");
        if (kind == EditorCursorKind.Eraser) return RenderEraser(size, double.NaN, style);
        size = Math.Clamp(size, 16, 256);
        int offset = IsBrush(kind) ? ClampBrushOffset(size, brushOffset) : 0;
        double k = size / Design;
        Point anchor = Anchor(kind);
        int hotX = Math.Clamp((int)Math.Floor(anchor.X * k), 0, size - 1);
        int hotY = Math.Clamp((int)Math.Floor(anchor.Y * k), 0, size - 1);
        // The step cursor shows its colour on the badge; its crosshair keeps the pointer style.
        EditorCursorStyle look = EditorCursorPolicy.ShowsInk(kind) && kind != EditorCursorKind.StepNumber
            ? EditorCursorStyle.ForInk(ink) : style;

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            // Lands the anchor exactly on the hot spot pixel's centre at every size, then moves the
            // whole icon right by the brush offset.
            dc.PushTransform(new MatrixTransform(k, 0, 0, k, offset + hotX + 0.5 - (anchor.X * k), hotY + 0.5 - (anchor.Y * k)));
            switch (kind)
            {
                case EditorCursorKind.Pen:
                    // The shared pen icon, placed by its nib.
                    DrawOutline(dc, IconGeometry("Icon.Pen", PenFallback), new Point(4.1, 15.9), anchor, BrushIconScale, look);
                    break;
                case EditorCursorKind.Highlighter:
                    // The shared edit icon, placed by its pencil nib so the stroke starts there.
                    DrawOutline(dc, IconGeometry("Icon.Edit", EditFallback), new Point(3.75, 16.25), anchor, BrushIconScale, look);
                    break;
                case EditorCursorKind.Crop:
                    DrawOutline(dc, IconGeometry("Icon.Crop", CropFallback), new Point(5, 5), anchor, 1.0, look);
                    break;
                case EditorCursorKind.TextInk:
                    DrawTextBeam(dc, anchor, look);
                    break;
                default:
                    DrawCrosshair(dc, anchor, look);
                    if (kind == EditorCursorKind.StepNumber) DrawStepBadge(dc, stepNumber, ink);
                    else if (kind != EditorCursorKind.Crosshair) DrawToolBadge(dc, kind, look);
                    break;
            }

            dc.Pop();
        }

        var target = new RenderTargetBitmap(size + offset, size + offset, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var straight = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
        straight.Freeze();
        return new EditorCursorImage(straight, hotX, hotY + offset);
    }

    /// <summary>
    /// Draws the eraser cursor: a ring as large as the eraser's reach around the click point, in a
    /// dark and a light line like the pen's footprint, with the small eraser icon beside it, its
    /// rubber edge one pixel clear of the ring at 45 degrees up and to the right.
    /// </summary>
    /// <param name="size">Edge length from <see cref="PixelSize"/>; it sets the icon's size.</param>
    /// <param name="radius">The reach in screen pixels; not a number means <see cref="DefaultEraserRadius"/>.</param>
    internal static EditorCursorImage RenderEraser(int size, double radius, EditorCursorStyle style)
    {
        size = Math.Clamp(size, 16, 256);
        double k = size / Design;
        double reach = EraserRadius(size, radius);
        // Room for the outer ring on every side of the click point.
        double ring = reach + 1.5 + 1;
        int offset = (int)Math.Ceiling((reach + 1.5 + 1) / Math.Sqrt(2));
        // The icon rises about 13 design units up and to the right of its rubber edge, outline included.
        int icon = (int)Math.Ceiling(14 * k);
        int hot = Math.Max((int)Math.Ceiling(ring), offset + icon);
        // Square, like every other cursor image the editor makes.
        int width = Math.Min(256, Math.Max(hot + (int)Math.Ceiling(ring) + 1, hot + offset + icon + 1));
        int height = width;
        var center = new Point(hot + 0.5, hot + 0.5);
        double line = Math.Max(1, k);

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0xE6, Ink.R, Ink.G, Ink.B)), line), center, reach + (line / 2), reach + (line / 2));
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), line), center, reach + (line * 1.5), reach + (line * 1.5));
            var tip = new Point(center.X + offset, center.Y - offset);
            dc.PushTransform(new MatrixTransform(k, 0, 0, k, tip.X, tip.Y));
            DrawOutline(dc, IconGeometry("Icon.Eraser", EraserFallback), EraserTip, default, BrushIconScale, style);
            dc.Pop();
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var straight = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
        straight.Freeze();
        return new EditorCursorImage(straight, Math.Min(hot, width - 1), Math.Min(hot, height - 1));
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

    /// <summary>How large the pen, highlighter and eraser icons are drawn against their 20-unit grid.</summary>
    internal const double BrushIconScale = 0.8;

    // The pen icon on its 20x20 grid; the editor's shared Icon.Pen resource wins when loaded.
    private const string PenFallback =
        "M4.1,15.9 L5.1,12.1 L12.35,4.85 A1.8,1.8 0 0 1 14.9,4.85 L15.15,5.1 A1.8,1.8 0 0 1 15.15,7.65 L7.9,14.9 Z M11.25,5.95 L14.05,8.75 M4.1,15.9 L7.9,14.9";

    private const string CropFallback = "M5,2 V14 A1,1 0 0 0 6,15 H18 M2,5 H14 A1,1 0 0 1 15,6 V18";

    // The edit icon on its 20x20 grid, its pencil nib at 3.75,16.25; the shared Icon.Edit
    // resource wins when loaded.
    private const string EditFallback =
        "M3.75,16.25 L4.45,12.65 L12.25,4.85 A1.65,1.65 0 0 1 14.6,7.2 L6.8,15 Z M11.1,6 L13.45,8.35 M3.75,17 L16.25,17";

    /// <summary>The eraser icon on its 20x20 grid; the shared Icon.Eraser resource wins when loaded.</summary>
    internal const string EraserFallback =
        "M8.25,16.25 L3,11 L11,3 L16.25,8.25 Z M6.2,7.8 L11.45,13.05 M8.25,16.25 L16.75,16.25";

    // The middle of the eraser's rubber edge, from 8.25,16.25 to 3,11.
    private static readonly Point EraserTip = new(5.625, 13.625);

    private static Point Anchor(EditorCursorKind kind) => kind switch
    {
        // Low on the left: the icon rises to the upper right of its tip, all inside the box.
        EditorCursorKind.Pen or EditorCursorKind.Highlighter or EditorCursorKind.Eraser => new Point(3.5, 27.5),
        EditorCursorKind.Crop => new Point(6.5, 6.5),
        EditorCursorKind.TextInk => new Point(6.5, 10.5),
        _ => new Point(9.5, 9.5),
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

    /// <summary>An outline-only icon drawn in the body colour on an outline-coloured edge.</summary>
    private static void DrawOutline(DrawingContext dc, Geometry icon, Point iconAnchor, Point anchor, double scale, EditorCursorStyle style)
    {
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, anchor.X - (iconAnchor.X * scale), anchor.Y - (iconAnchor.Y * scale)));
        dc.DrawGeometry(null, RoundPen(style.Outline, OutlineWidth / scale), icon);
        dc.DrawGeometry(null, RoundPen(style.Body, BodyWidth / scale), icon);
        dc.Pop();
    }

    // Edge and body line widths of every drawn glyph, in design units.
    private const double OutlineWidth = 3.4;
    private const double BodyWidth = 1.4;

    // Half the crosshair's span and the gap at its centre, in design units: about the Windows
    // precision cross at every pointer size.
    private const double CrossArm = 7.5;
    private const double CrossGap = 2.5;

    private static void DrawCrosshair(DrawingContext dc, Point c, EditorCursorStyle style)
    {
        var lines = new GeometryGroup();
        foreach ((double dx, double dy) in new[] { (0.0, -1.0), (0.0, 1.0), (-1.0, 0.0), (1.0, 0.0) })
        {
            lines.Children.Add(new LineGeometry(new Point(c.X + (dx * CrossGap), c.Y + (dy * CrossGap)), new Point(c.X + (dx * CrossArm), c.Y + (dy * CrossArm))));
        }

        dc.DrawGeometry(null, RoundPen(style.Outline, OutlineWidth), lines);
        dc.DrawGeometry(new SolidColorBrush(style.Outline), null, new EllipseGeometry(c, 1.8, 1.8));
        dc.DrawGeometry(null, RoundPen(style.Body, BodyWidth), lines);
        dc.DrawGeometry(new SolidColorBrush(style.Body), null, new EllipseGeometry(c, 0.9, 0.9));
    }

    /// <summary>An I-beam about as large as the Windows one, centred on the click point.</summary>
    private static void DrawTextBeam(DrawingContext dc, Point c, EditorCursorStyle style)
    {
        const double half = 8, serif = 2.5;
        var beam = new GeometryGroup();
        beam.Children.Add(new LineGeometry(new Point(c.X, c.Y - half), new Point(c.X, c.Y + half)));
        beam.Children.Add(new LineGeometry(new Point(c.X - serif, c.Y - half), new Point(c.X + serif, c.Y - half)));
        beam.Children.Add(new LineGeometry(new Point(c.X - serif, c.Y + half), new Point(c.X + serif, c.Y + half)));
        dc.DrawGeometry(null, RoundPen(style.Outline, OutlineWidth), beam);
        dc.DrawGeometry(null, RoundPen(style.Body, BodyWidth), beam);
    }

    // The tag below and to the right of the crosshair that names the tool.
    private static readonly Rect BadgePlate = new(13.5, 13.5, 9, 9);

    // The step badge, a little larger so its digits stay legible.
    private static readonly Rect StepPlate = new(13, 13, 11, 11);

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
        dc.DrawRoundedRectangle(new SolidColorBrush(style.Body), new Pen(new SolidColorBrush(style.Outline), 1), BadgePlate, 2, 2);
        const double scale = 0.4;
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, BadgePlate.X + (BadgePlate.Width / 2) - (10 * scale), BadgePlate.Y + (BadgePlate.Height / 2) - (10 * scale)));
        dc.DrawGeometry(null, RoundPen(style.Outline, 1.1 / scale), IconGeometry(key, fallback));
        dc.Pop();
    }

    private static void DrawStepBadge(DrawingContext dc, int number, ColorRgba color)
    {
        // Drawn on the badge's own 15-unit plate with the cursor type sizes, then fitted to the
        // smaller plate, so the digits keep their proportions.
        var legacy = new Rect(15.5, 15.5, 15, 15);
        double fit = StepPlate.Width / legacy.Width;
        dc.PushTransform(new MatrixTransform(fit, 0, 0, fit, StepPlate.X - (legacy.X * fit), StepPlate.Y - (legacy.Y * fit)));
        // The badge previews the annotation itself, so it keeps the annotation's colours.
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Ink), 2.6), legacy, 3, 3);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)), new Pen(Brushes.White, 1.1), legacy, 3, 3);
        string text = Math.Max(1, number) > 999 ? "…" : Math.Max(1, number).ToString(CultureInfo.InvariantCulture);
        double fontSize = text.Length switch { 1 => TypeScale.Content.CursorSingleDigit, 2 => TypeScale.Content.CursorTwoDigits, _ => TypeScale.Content.CursorManyDigits };
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), fontSize, Brushes.White, 1.0);
        var origin = new Point(legacy.X + ((legacy.Width - formatted.Width) / 2), legacy.Y + ((legacy.Height - formatted.Height) / 2));
        Geometry glyphs = formatted.BuildGeometry(origin);
        // The placed badge draws white digits with a dark outline; the preview matches it.
        dc.DrawGeometry(null, RoundPen(Ink, 1.6), glyphs);
        dc.DrawGeometry(Brushes.White, null, glyphs);
        dc.Pop();
    }

    private static Pen RoundPen(Color color, double thickness) => new(new SolidColorBrush(color), thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };
}
