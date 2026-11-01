using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Undo;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorPointerSurfaceEvidenceTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly ColorRgba Red = ColorRgba.FromRgb(230, 48, 60);

    [Fact]
    public void RenderCursorAndBrushEvidence() => StaTestHost.Run(() =>
    {
        string? root = Environment.GetEnvironmentVariable("MYCAPTURE_POINTER_EVIDENCE");
        if (string.IsNullOrWhiteSpace(root)) return;
        string phase = Environment.GetEnvironmentVariable("MYCAPTURE_POINTER_PHASE") ?? "after";
        root = DiagnosticOutputPaths.Create(root);
        var sheet = new DrawingVisual();
        using (DrawingContext dc = sheet.RenderOpen())
        {
            dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, 1000, 600));
            int i = 0;
            foreach (EditorCursorKind kind in Enum.GetValues<EditorCursorKind>())
            {
                EditorCursorImage image = EditorCursorPolicy.IsCustom(kind)
                    ? EditorCursors.Render(kind, 32, 12, Red, EditorCursorStyle.White) : RenderSystem(kind);
                int x = (i % 5) * 200, y = (i / 5) * 200;
                Label(dc, kind.ToString(), x + 8, y + 8, 11);
                dc.DrawImage(image.Bitmap, new Rect(x + 42, y + 36, 96, 96));
                double hx = x + 42 + (image.HotSpotX + .5) * 3;
                double hy = y + 36 + (image.HotSpotY + .5) * 3;
                dc.DrawLine(new Pen(Brushes.Red, 1), new Point(hx - 6, hy), new Point(hx + 6, hy));
                dc.DrawLine(new Pen(Brushes.Red, 1), new Point(hx, hy - 6), new Point(hx, hy + 6));
                Label(dc, $"Hotspot {image.HotSpotX}, {image.HotSpotY}", x + 8, y + 160, 12);
                i++;
            }
        }
        Save(Render(sheet, 1000, 600), DiagnosticOutputPaths.Child(root, $"cursor-sheet-{phase}.png"));
        foreach (double scale in new[] { .5, 1.0 })
        foreach (int width in new[] { 3, 24 })
        foreach (EditorTool tool in new[] { EditorTool.Pen, EditorTool.Highlighter })
        {
            var (surface, controller) = Surface(scale);
            controller.Tool = tool;
            controller.StrokeThickness = width;
            var point = new PointD(160, 120);
            typeof(AnnotationEditorSurface).GetMethod("UpdatePointerPreview", Any)?.Invoke(surface, [point, true]);
            surface.InvalidateVisual();
            surface.UpdateLayout();
            var scene = new DrawingVisual();
            using (DrawingContext dc = scene.RenderOpen())
            {
                dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, 400, 310));
                dc.DrawImage(Render(surface, (int)surface.ActualWidth, (int)surface.ActualHeight), new Rect(0, 40, surface.ActualWidth, surface.ActualHeight));
                Point center = surface.ToSurfacePoint(point);
                EditorCursorImage cursor = EditorCursors.Render(tool == EditorTool.Pen ? EditorCursorKind.Pen : EditorCursorKind.Highlighter,
                    32, 12, Red, EditorCursorStyle.White);
                dc.DrawImage(cursor.Bitmap, new Rect(center.X - cursor.HotSpotX - .5, 40 + center.Y - cursor.HotSpotY - .5, 32, 32));
                Label(dc, $"{tool}, {width}px, {scale:P0}", 12, 8, 16);
            }
            Save(Render(scene, 400, 310), DiagnosticOutputPaths.Child(root, $"brush-{tool}-{width}-{scale * 100:0}-{phase}.png"));
        }
        {
            var (surface, controller) = Surface(1);
            controller.Tool = EditorTool.StepNumber;
            var badge = new TextAnnotation { IsStepNumber = true, Text = "12", Rect = new RectD(140, 100, 36, 36),
                FontSize = 18, Background = Red, Foreground = ColorRgba.White, Bold = true, HasOutline = true };
            controller.Document.Add(badge);
            controller.SetSelected(badge);
            surface.InvalidateVisual();
            surface.UpdateLayout();
            Save(Render(surface, (int)surface.ActualWidth, (int)surface.ActualHeight), DiagnosticOutputPaths.Child(root, $"step-handles-{phase}.png"));
        }
    });

    internal static (AnnotationEditorSurface Surface, AnnotationEditorController Controller) Surface(double scale)
    {
        var bitmap = Grid(320, 240);
        var rect = new RectD(0, 0, 320, 240);
        var controller = new AnnotationEditorController(AnnotationDocument.CreateFor(320, 240), new UndoStack());
        var surface = new AnnotationEditorSurface(new FrozenFrame(bitmap, rect, null, 0), rect, controller,
            new AnnotationRenderer(new AnnotationImageStore()));
        var size = new Size(320 * scale + 32, 240 * scale + 32);
        surface.Measure(size);
        surface.Arrange(new Rect(size));
        surface.UpdateLayout();
        Assert.Equal(scale, surface.DipPerPixel, 6);
        return (surface, controller);
    }

    private static BitmapSource Grid(int width, int height)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (int x = 0; x < width; x += 20) dc.DrawLine(new Pen(Brushes.LightGray, 1), new Point(x, 0), new Point(x, height));
            for (int y = 0; y < height; y += 20) dc.DrawLine(new Pen(Brushes.LightGray, 1), new Point(0, y), new Point(width, y));
        }
        return Render(visual, width, height);
    }

    private static BitmapSource Render(Visual visual, int width, int height)
    {
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Label(DrawingContext dc, string text, double x, double y, double size) => dc.DrawText(
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.Black, 1), new Point(x, y));

    private static EditorCursorImage RenderSystem(EditorCursorKind kind)
    {
        int id = kind switch
        {
            EditorCursorKind.Arrow => 32512, EditorCursorKind.Move => 32646,
            EditorCursorKind.ResizeNorthwestSoutheast => 32642, EditorCursorKind.ResizeNortheastSouthwest => 32643,
            EditorCursorKind.ResizeNorthSouth => 32645, EditorCursorKind.ResizeWestEast => 32644,
            EditorCursorKind.TextBeam => 32513, EditorCursorKind.Cross => 32515,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        IntPtr cursor = LoadCursor(IntPtr.Zero, (IntPtr)id);
        Assert.NotEqual(IntPtr.Zero, cursor);
        Assert.True(GetIconInfo(cursor, out ICONINFO info));
        IntPtr dc = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            Assert.NotEqual(0, GetObject(info.hbmColor == IntPtr.Zero ? info.hbmMask : info.hbmColor, Marshal.SizeOf<BITMAP>(), out BITMAP native));
            int width = native.bmWidth, height = info.hbmColor == IntPtr.Zero ? native.bmHeight / 2 : native.bmHeight;
            var header = new BITMAPINFO { size = (uint)Marshal.SizeOf<BITMAPINFO>(), width = width, height = -height, planes = 1, bitCount = 32 };
            dc = CreateCompatibleDC(IntPtr.Zero);
            bitmap = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
            Assert.NotEqual(IntPtr.Zero, dc); Assert.NotEqual(IntPtr.Zero, bitmap);
            previous = SelectObject(dc, bitmap);
            byte[] pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            Assert.True(DrawIconEx(dc, 0, 0, cursor, width, height, 0, IntPtr.Zero, 3));
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            BitmapSource result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            result.Freeze();
            return new EditorCursorImage(result, (int)info.xHotspot, (int)info.yHotspot);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(dc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (dc != IntPtr.Zero) DeleteDC(dc);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public int fIcon; public uint xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFO { public uint size; public int width, height; public ushort planes, bitCount; public uint compression, sizeImage; public int xPelsPerMeter, yPelsPerMeter; public uint clrUsed, clrImportant; }
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
