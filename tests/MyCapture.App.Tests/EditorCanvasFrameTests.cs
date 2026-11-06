using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

/// <summary>
/// The editor's canvas frame draws the rounded corners it declares.
/// </summary>
public sealed class EditorCanvasFrameTests
{
    [Fact]
    public void CanvasIsClippedToTheFramesRoundedInsideEdgeAtEverySize() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(beforeShow: UseShippedTheme);
        var viewport = Field<Grid>(host.Editor, "_viewport");
        var frame = Assert.IsType<Border>(VisualTreeHelper.GetParent(viewport));
        Assert.Equal(new CornerRadius(8), frame.CornerRadius);

        foreach (double width in new[] { 1000d, 760d, 1240d })
        {
            host.Window.Width = width;
            host.Window.UpdateLayout();
            var clip = Assert.IsType<RectangleGeometry>(viewport.Clip);
            Assert.Equal(new Rect(viewport.RenderSize), clip.Rect);
            // The outline's centre line turns at radius 8; its inside edge half a DIP tighter.
            Assert.Equal(7.5, clip.RadiusX);
            Assert.Equal(7.5, clip.RadiusY);
        }
    });

    [Fact]
    public void FrameCornersShowTheWindowBehindTheFrameNotTheCanvas() => StaTestHost.Run(() =>
    {
        using EditorWindowHost host = EditorWindowHost.Create(beforeShow: UseShippedTheme);
        // Crop mode adds the scrim over the whole canvas: the corners stay rounded under it too.
        foreach (bool crop in new[] { false, true })
        {
            if (crop) Assert.True(host.Editor.HandleShortcut(Key.X, ModifierKeys.None));
            host.Window.UpdateLayout();
            var frame = Assert.IsType<Border>(VisualTreeHelper.GetParent(Field<Grid>(host.Editor, "_viewport")));
            Point origin = frame.TranslatePoint(new Point(0, 0), host.Window);
            int left = (int)Math.Round(origin.X), top = (int)Math.Round(origin.Y);
            int right = left + (int)Math.Round(frame.ActualWidth) - 1, bottom = top + (int)Math.Round(frame.ActualHeight) - 1;
            Pixels pixels = Pixels.Render(host.Window);

            // The body's own surface, just outside the frame's left edge.
            Color behind = pixels[left - 4, top + 40];
            // One pixel in from each corner of the frame's box lies well outside its rounded
            // outline (about 1.4 DIP), where the body shows through.
            foreach ((int x, int y) in new[] { (left + 1, top + 1), (right - 1, top + 1), (left + 1, bottom - 1), (right - 1, bottom - 1) })
            {
                AssertClose(behind, pixels[x, y], $"crop={crop} corner ({x},{y})");
            }

            // The canvas itself still fills the frame away from the corners.
            Assert.NotEqual(behind, pixels[left + 4, top + 40]);
        }
    });

    private static void UseShippedTheme(Window window)
    {
        window.Resources = SystemHighContrastThemeTests.LoadTheme();
        window.SetResourceReference(Control.FontFamilyProperty, "Font.Ui");
        MyCapture.App.Themes.ThemeFonts.ApplyText(window);
    }

    private static void AssertClose(Color expected, Color actual, string where)
    {
        int difference = Math.Max(Math.Abs(expected.R - actual.R), Math.Max(Math.Abs(expected.G - actual.G), Math.Abs(expected.B - actual.B)));
        Assert.True(difference <= 2, $"{where}: expected {expected}, found {actual}");
    }

    /// <summary>The window's client content rendered offscreen at 96 DPI.</summary>
    private sealed class Pixels
    {
        private readonly byte[] _bgra;
        private readonly int _width;

        private Pixels(byte[] bgra, int width)
        {
            _bgra = bgra;
            _width = width;
        }

        public Color this[int x, int y]
        {
            get
            {
                int offset = (y * _width + x) * 4;
                return Color.FromRgb(_bgra[offset + 2], _bgra[offset + 1], _bgra[offset]);
            }
        }

        public static Pixels Render(Window window)
        {
            int width = (int)Math.Ceiling(window.ActualWidth), height = (int)Math.Ceiling(window.ActualHeight);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            byte[] bgra = new byte[width * height * 4];
            bitmap.CopyPixels(bgra, width * 4, 0);
            return new Pixels(bgra, width);
        }
    }
}
