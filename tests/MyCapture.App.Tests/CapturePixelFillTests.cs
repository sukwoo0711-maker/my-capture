using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CapturePixelFillTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    public void GridFills_MatchTheHalfPixelStrokes_WhereverTheMagnifierLands(double scale) => StaTestHost.Run(() =>
    {
        // The token grid (black or white at alpha 50) and a high-contrast window-text colour.
        Color[] colors = [Color.FromArgb(50, 0, 0, 0), Color.FromArgb(50, 255, 255, 255), Color.FromArgb(255, 0x12, 0x34, 0x56)];
        // Below-right of the pointer, flipped at the right and bottom edges, and near each corner.
        PointD[] cursors = [new(120, 100), new(639, 359), new(0, 0), new(5, 350), new(630, 4), new(333, 207)];
        foreach (Color color in colors)
        {
            foreach (PointD cursor in cursors)
            {
                var fills = new CaptureOverlayView(NoiseFrame(), showSelectionGrid: false);
                var strokes = new CaptureOverlayView(NoiseFrame(), showSelectionGrid: false) { MagnifierGridFills = false, CrosshairFills = false };
                try
                {
                    foreach (CaptureOverlayView view in new[] { fills, strokes })
                    {
                        UseGridColor(view, color);
                        view.ReadCursor = () => cursor;
                        SetMouseOver(view, true);
                        view.InitializePointer();
                        Render(view, scale);
                    }
                    byte[] expected = Pixels(Render(strokes, scale));
                    byte[] actual = Pixels(Render(fills, scale));
                    Assert.True(fills.MagnifierGridFillCount > 0, $"The fills were not used at scale {scale}, cursor {cursor}.");
                    Assert.Equal(0, strokes.MagnifierGridFillCount);
                    // The fills replace all 28 strokes; the strokes recipe really drew lines.
                    Assert.Equal(0, Lines(Visual(fills, "_magnifierVisual").Drawing));
                    Assert.Equal(28, Lines(Visual(strokes, "_magnifierVisual").Drawing));
                    AssertSamePixels(expected, actual, $"colour {color}, scale {scale}, cursor {cursor}");
                }
                finally { fills.ReleaseResources(); strokes.ReleaseResources(); }
            }
        }
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    [InlineData(2.5)]
    [InlineData(3.0)]
    public void MagnifierColourAndCoordinatesNeverOverlapAndStayInsideItsPanel(double scale) => StaTestHost.Run(() =>
    {
        // The image is 150 frame pixels and the values under it are DIP text, so at 200% the
        // 12 DIP colour and the 10 DIP coordinates overlapped inside a 75 DIP footer.
        foreach (PointD cursor in new PointD[] { new(120, 100), new(639, 359), new(333, 207) })
        {
            var view = new CaptureOverlayView(NoiseFrame(), showSelectionGrid: false);
            try
            {
                view.ReadCursor = () => cursor;
                SetMouseOver(view, true);
                view.InitializePointer();
                Render(view, scale);
                Drawing[] leaves = Leaves(Visual(view, "_magnifierVisual").Drawing).ToArray();
                GlyphRunDrawing[] texts = leaves.OfType<GlyphRunDrawing>().ToArray();
                Assert.Equal(2, texts.Length);
                Rect colour = texts[0].Bounds, location = texts[1].Bounds;
                string what = $"scale {scale}, cursor {cursor}: colour {colour}, coordinates {location}";
                Assert.False(colour.IntersectsWith(location), what);
                Rect panel = leaves.OfType<GeometryDrawing>().Select(drawing => drawing.Geometry).OfType<RectangleGeometry>()
                    .Single(geometry => geometry.RadiusX == 9).Rect;
                Assert.True(panel.Contains(colour) && panel.Contains(location), $"{what}, panel {panel}");
                // 100% keeps the values side by side, as before; 200% puts the coordinates under the colour.
                if (scale == 1.0) Assert.True(location.Top < colour.Bottom && location.Left > colour.Right, what);
                if (scale == 2.0) Assert.True(location.Top >= colour.Bottom, what);
                // Wider cells for the text are still whole device pixels, so the grid keeps its fills.
                Assert.True(view.MagnifierGridFillCount > 0, what);
            }
            finally { view.ReleaseResources(); }
        }
    });

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(2.0, true)]
    [InlineData(1.25, false)]
    [InlineData(1.5, false)]
    public void CrosshairFills_MatchTheStrokes_AndOnlyWhereTheyEndOnDevicePixels(double scale, bool fills) => StaTestHost.Run(() =>
    {
        // Theme crosshairs: the accent over a black or white keyline, and high-contrast colours.
        (Color Line, Color Shadow)[] colors = [(Color.FromRgb(0x97, 0x89, 0xFF), Colors.White), (Color.FromRgb(0x20, 0xD0, 0x60), Colors.Black),
            (Color.FromRgb(0xFF, 0xFF, 0x00), Color.FromRgb(0x10, 0x10, 0x10))];
        PointD[] cursors = [new(120, 100), new(0, 0), new(639, 359), new(5, 350), new(333, 207)];
        foreach ((Color line, Color shadow) in colors)
        {
            foreach (PointD cursor in cursors)
            {
                // The magnifier is off so the comparison sees the crosshair over the frozen desktop.
                var actual = new CaptureOverlayView(NoiseFrame(), showMagnifier: false, showSelectionGrid: false);
                var expected = new CaptureOverlayView(NoiseFrame(), showMagnifier: false, showSelectionGrid: false) { CrosshairFills = false };
                try
                {
                    foreach (CaptureOverlayView view in new[] { actual, expected })
                    {
                        UsePalette(view, ("CaptureGuide.Crosshair", line), ("CaptureGuide.CrosshairShadow", shadow));
                        view.ReadCursor = () => cursor;
                        SetMouseOver(view, true);
                        view.InitializePointer();
                        Render(view, scale);
                    }
                    byte[] reference = Pixels(Render(expected, scale));
                    byte[] obtained = Pixels(Render(actual, scale));
                    Assert.Equal(fills, actual.CrosshairFillCount > 0);
                    Assert.Equal(0, expected.CrosshairFillCount);
                    Assert.Equal(fills ? 0 : 4, Lines(Visual(actual, "_pointerVisual").Drawing));
                    Assert.Equal(4, Lines(Visual(expected, "_pointerVisual").Drawing));
                    AssertSamePixels(reference, obtained, $"crosshair {line}/{shadow}, scale {scale}, cursor {cursor}");
                }
                finally { actual.ReleaseResources(); expected.ReleaseResources(); }
            }
        }
    });

    [Fact]
    public void OffPixelPlacementsAndOtherColours_KeepTheStrokes() => StaTestHost.Run(() =>
    {
        // A simulated sub-pixel pointer places the magnifier between device pixels.
        PointD cursor = new(120.5, 100.25);
        var view = new CaptureOverlayView(NoiseFrame(), showSelectionGrid: false);
        try
        {
            view.ReadCursor = () => cursor;
            SetMouseOver(view, true);
            view.InitializePointer();
            Render(view, 1);
            Assert.Equal(0, view.MagnifierGridFillCount);
            Assert.Equal(28, Lines(Visual(view, "_magnifierVisual").Drawing));
            // Byte premultiplication rounds a translucent mid colour differently from a quarter alpha.
            UseGridColor(view, Color.FromArgb(77, 200, 100, 30));
            cursor = new PointD(130, 110);
            view.UpdatePointer(false, false);
            Render(view, 1);
            Assert.Equal(0, view.MagnifierGridFillCount);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void CoverageBrush_OnlyForSolidColoursWhoseScaledAlphaRoundsExactly()
    {
        StaTestHost.Run(() =>
        {
            Assert.Equal(Color.FromArgb(50, 0, 0, 0).ScA * 0.25f,
                ((SolidColorBrush)CaptureOverlayView.CoverageBrush(Solid(Color.FromArgb(50, 0, 0, 0)), 0.25f)!).Color.ScA);
            Assert.Equal(Color.FromArgb(255, 0x12, 0x34, 0x56).ScA * 0.5f,
                ((SolidColorBrush)CaptureOverlayView.CoverageBrush(Solid(Color.FromArgb(255, 0x12, 0x34, 0x56)), 0.5f)!).Color.ScA);
            Assert.NotNull(CaptureOverlayView.CoverageBrush(Solid(Color.FromArgb(50, 255, 255, 255)), 0.25f));
            Assert.Null(CaptureOverlayView.CoverageBrush(Solid(Color.FromArgb(77, 200, 100, 30)), 0.25f));
            Assert.Null(CaptureOverlayView.CoverageBrush(new SolidColorBrush(Colors.Black) { Opacity = 0.5 }, 0.5f));
            Assert.Null(CaptureOverlayView.CoverageBrush(new LinearGradientBrush(Colors.Black, Colors.White, 0), 0.5f));
            Assert.Throws<ArgumentOutOfRangeException>(() => CaptureOverlayView.CoverageBrush(Solid(Colors.Black), 0.3f));
        });
    }

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static void UseGridColor(CaptureOverlayView view, Color color) => UsePalette(view, ("CaptureGuide.MagnifierGrid", color));

    private static void UsePalette(CaptureOverlayView view, params (string Key, Color Color)[] colors)
    {
        var palette = new Dictionary<string, Brush>(view.GuidePalette);
        foreach ((string key, Color color) in colors) palette[key] = Solid(color);
        typeof(CaptureOverlayView).GetField("_guidePalette", Instance)!.SetValue(view, palette);
        typeof(CaptureOverlayView).GetField("_staticDirty", Instance)!.SetValue(view, true);
    }

    private static int Lines(Drawing? drawing) => drawing switch
    {
        DrawingGroup group => group.Children.Sum(Lines),
        GeometryDrawing { Geometry: LineGeometry } => 1,
        _ => 0,
    };

    private static IEnumerable<Drawing> Leaves(Drawing? drawing) => drawing switch
    {
        DrawingGroup group => group.Children.SelectMany(Leaves),
        null => [],
        _ => [drawing],
    };

    private static void AssertSamePixels(byte[] expected, byte[] actual, string what)
    {
        int changed = 0, first = -1;
        for (int i = 0; i < actual.Length; i += 4)
        {
            if (actual.AsSpan(i, 4).SequenceEqual(expected.AsSpan(i, 4))) continue;
            if (first < 0) first = i / 4;
            changed++;
        }
        Assert.True(changed == 0, $"{changed} changed pixels for {what}; first ({first % 640}, {first / 640}).");
    }

    private static DrawingVisual Visual(CaptureOverlayView view, string field) =>
        (DrawingVisual)typeof(CaptureOverlayView).GetField(field, Instance)!.GetValue(view)!;

    private static void SetMouseOver(CaptureOverlayView view, bool value)
    {
        var key = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        view.SetValue(key, value);
        MethodInfo writeFlag = typeof(UIElement).GetMethod("WriteFlag", Instance)!;
        writeFlag.Invoke(view, [Enum.Parse(writeFlag.GetParameters()[0].ParameterType, "IsMouseOverCache"), value]);
    }

    private static RenderTargetBitmap Render(CaptureOverlayView view, double scale)
    {
        if (Math.Abs(view.GuidePixelSizeX - 1 / scale) > 0.000001)
            typeof(CaptureOverlayView).GetMethod("OnDpiChanged", Instance)!.Invoke(view,
                [new DpiScale(1 / view.GuidePixelSizeX, 1 / view.GuidePixelSizeY), new DpiScale(scale, scale)]);
        // Offscreen roots report 96 DPI; keep the simulated scale's DIP sizes unrounded.
        view.UseLayoutRounding = false;
        view.InvalidateVisual();
        view.Measure(new Size(640 / scale, 360 / scale));
        view.Arrange(new Rect(0, 0, 640 / scale, 360 / scale));
        view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(640, 360, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(view);
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static FrozenFrame NoiseFrame()
    {
        byte[] pixels = new byte[640 * 360 * 4];
        var random = new Random(11);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)random.Next(256);
            pixels[i + 1] = (byte)random.Next(256);
            pixels[i + 2] = (byte)random.Next(256);
            pixels[i + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgr32, null, pixels, 640 * 4);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, new RectD(0, 0, 640, 360), null, 0);
    }
}
