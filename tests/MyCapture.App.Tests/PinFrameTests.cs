using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A pin shows one continuous frame: the frozen image is clipped to the same rounded rectangle
/// the outline follows, and the outline is drawn over the image edge, so the four corners close
/// at 100% and 150% display scale, at rest and on hover.
/// </summary>
public sealed class PinFrameTests
{
    [Fact]
    public void PinAndNotificationCorners_FollowTheCurrentHost()
    {
        double radius = WindowsFeatureSupport.WindowCornerRadiusDip(Environment.OSVersion.Version.Build);
        Assert.Equal(radius, PinWindow.FrameCornerRadiusDip);
        Assert.Equal(new CornerRadius(radius), MyCapture.App.ThemedShellPresenter.CardCorner);
    }

    private const string CodeSource = "public void Capture()\r\n{\r\n\tSave();\r\n}";

    private const string CodeHtml =
        "<div style=\"color: #d4d4d4; background-color: #1e1e1e; white-space: pre;\"><div><span style=\"color: #569cd6;\">public void</span> Capture()</div><div>{</div><div>    Save();</div><div>}</div></div>";

    private static readonly Color LightDesk = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color DarkDesk = Color.FromRgb(0x1F, 0x1F, 0x1F);
    private static readonly Color ImageRed = Color.FromRgb(0xE0, 0x20, 0x20);

    [Theory]
    [InlineData(1.0, false, 1.0)]
    [InlineData(1.0, true, 1.0)]
    [InlineData(1.5, false, 1.0)]
    [InlineData(1.5, true, 1.0)]
    [InlineData(1.25, false, 1.0)]
    [InlineData(1.5, true, 0.93)]
    [InlineData(1.75, false, 1.1)]
    public void FrameIsOneContinuousOutlineThatClosesAtEveryCorner(double scale, bool hover, double zoom) => RunWithoutMotion(() =>
    {
        BitmapSource frame = RenderPin(PinContent.FromImage(Solid(201, 121, ImageRed)), scale, hover, LightDesk, zoom);
        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        byte[] pixels = Pixels(frame);

        // The outline is drawn over the image edge, so all four sides show the same colour.
        Color outline = At(pixels, width, width / 2, 0);
        Assert.True(Distance(outline, ImageRed) > 120, $"top edge shows the image, not the outline: {outline}");
        foreach ((int x, int y) in new[] { (width / 2, height - 1), (0, height / 2), (width - 1, height / 2) })
        {
            Assert.True(Distance(At(pixels, width, x, y), outline) < 12, $"edge ({x},{y}) differs from the top outline");
        }

        // In each corner the outline separates the desk from the image, and its pixels connect
        // the horizontal edge to the vertical edge: the image is clipped to the rounded corner
        // and the stroke closes the arc instead of stopping where the straight edges end.
        int size = (int)Math.Ceiling(12 * scale);
        foreach ((int x0, int y0, int dx, int dy) in new[]
        {
            (0, 0, 1, 1), (width - 1, 0, -1, 1), (0, height - 1, 1, -1), (width - 1, height - 1, -1, -1),
        })
        {
            var kind = new int[size, size];
            for (int i = 0; i < size; i++)
            {
                for (int j = 0; j < size; j++)
                {
                    (double desk, double line, double image) =
                        Unmix(At(pixels, width, x0 + (dx * i), y0 + (dy * j)), LightDesk, outline, ImageRed);
                    kind[i, j] = line >= desk && line >= image ? Outline : image > desk ? Picture : Desk;
                }
            }

            for (int i = 0; i < size; i++)
            {
                for (int j = 0; j < size; j++)
                {
                    if (kind[i, j] != Picture) continue;
                    foreach ((int ni, int nj) in new[] { (i - 1, j), (i + 1, j), (i, j - 1), (i, j + 1) })
                    {
                        bool inside = ni >= 0 && nj >= 0 && ni < size && nj < size;
                        Assert.False(inside && kind[ni, nj] == Desk, $"image meets the desk at corner ({x0},{y0}), local ({i},{j})");
                    }
                }
            }

            Assert.True(OutlineConnectsEdges(kind, size), $"the outline does not close at corner ({x0},{y0})");
        }
    });

    private const int Desk = 0;
    private const int Outline = 1;
    private const int Picture = 2;

    /// <summary>Whether outline pixels join the first row to the first column (8-connected).</summary>
    private static bool OutlineConnectsEdges(int[,] kind, int size)
    {
        var seen = new bool[size, size];
        var pending = new Queue<(int I, int J)>();
        for (int i = 0; i < size; i++)
        {
            if (kind[i, 0] == Outline)
            {
                seen[i, 0] = true;
                pending.Enqueue((i, 0));
            }
        }

        while (pending.Count > 0)
        {
            (int i, int j) = pending.Dequeue();
            if (i == 0) return true;
            for (int di = -1; di <= 1; di++)
            {
                for (int dj = -1; dj <= 1; dj++)
                {
                    int ni = i + di, nj = j + dj;
                    if (ni < 0 || nj < 0 || ni >= size || nj >= size || seen[ni, nj] || kind[ni, nj] != Outline) continue;
                    seen[ni, nj] = true;
                    pending.Enqueue((ni, nj));
                }
            }
        }

        return false;
    }

    [Fact]
    public void ImageFillsThePinOneToOneInsideTheOutline() => RunWithoutMotion(() =>
    {
        BitmapSource checker = Checker(64, 40);
        BitmapSource frame = RenderPin(PinContent.FromImage(checker), 1.0, hover: false, LightDesk);
        Assert.Equal(64, frame.PixelWidth);
        Assert.Equal(40, frame.PixelHeight);
        byte[] source = Pixels(checker);
        byte[] shown = Pixels(frame);

        // Away from the one-pixel outline and the rounded corners every pin pixel is the
        // source pixel: the image is not shrunk into the frame and resampled.
        for (int y = 9; y < 31; y++)
        {
            for (int x = 1; x < 63; x++)
            {
                Assert.Equal(At(source, 64, x, y), At(shown, 64, x, y));
            }
        }

        for (int x = 9; x < 55; x++)
        {
            Assert.Equal(At(source, 64, x, 1), At(shown, 64, x, 1));
            Assert.Equal(At(source, 64, x, 38), At(shown, 64, x, 38));
        }
    });

    [Fact]
    public void ImageClipFollowsTheOutlineCorner()
    {
        Rect clip = PinWindow.ImageClipBounds(new Size(200, 120));

        Assert.Equal(new Rect(0.5, 0.5, 199, 119), clip);
        Assert.Equal(new Rect(0.5, 0.5, 0, 0), PinWindow.ImageClipBounds(new Size(0.4, 0.2)));
    }

    /// <summary>
    /// Opt-in evidence: set <c>MYCAPTURE_PIN_FRAME_EVIDENCE</c> to a folder to write the code pin
    /// at 100% and 150% on a light and a dark desktop, with 8x corner close-ups.
    /// </summary>
    [Fact]
    public void CodePinFrame_WritesEvidenceWhenRequested()
    {
        string? directory = Environment.GetEnvironmentVariable("MYCAPTURE_PIN_FRAME_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        RunWithoutMotion(() =>
        {
            Directory.CreateDirectory(directory);
            BitmapSource code = ClipboardCodeRenderer.TryRender(CodeSource, CodeHtml)
                ?? throw new InvalidOperationException("Code fixture did not render.");
            foreach (double scale in new[] { 1.0, 1.5 })
            {
                foreach (bool hover in new[] { false, true })
                {
                    foreach ((string name, Color desk) in new[] { ("light", LightDesk), ("dark", DarkDesk) })
                    {
                        BitmapSource frame = RenderPin(PinContent.FromText(code, CodeSource, false), scale, hover, desk);
                        string label = $"pin-code-{(int)(scale * 100)}-{(hover ? "hover" : "rest")}-{name}";
                        Save(frame, Path.Combine(directory, label + ".png"));
                        Save(CornerCloseUp(frame, (int)Math.Round(24 * scale), 8), Path.Combine(directory, label + "-corners.png"));
                    }
                }
            }
        });
    }

    private static void RunWithoutMotion(Action body) => StaTestHost.Run(() =>
    {
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        try
        {
            body();
        }
        finally
        {
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    /// <summary>
    /// Renders the pin's real chrome as a root visual at <paramref name="scale"/>, so layout
    /// rounding follows that display scale the way it does on a monitor of that DPI.
    /// </summary>
    private static BitmapSource RenderPin(PinContent content, double scale, bool hover, Color desk, double zoom = 1.0)
    {
        var pin = new PinWindow(
            content,
            new PinViewState(content.Image.PixelWidth, content.Image.PixelHeight, zoom, 1, 0.1),
            -10000,
            -10000,
            () => new PinSettings());
        try
        {
            if (hover)
            {
                pin.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            }

            var chrome = (UIElement)pin.Content;
            pin.Content = null;
            // The window's inherited layout rounding and pixel snapping travel with the chrome.
            var host = new Border
            {
                Background = new SolidColorBrush(desk),
                Child = chrome,
                UseLayoutRounding = pin.UseLayoutRounding,
                SnapsToDevicePixels = pin.SnapsToDevicePixels,
            };
            VisualTreeHelper.SetRootDpi(host, new DpiScale(scale, scale));
            // Like a window, the client area is a whole number of display pixels.
            int pixelWidth = (int)Math.Round(pin.Width * scale);
            int pixelHeight = (int)Math.Round(pin.Height * scale);
            var size = new Size(pixelWidth / scale, pixelHeight / scale);
            host.Measure(size);
            host.Arrange(new Rect(size));
            host.UpdateLayout();
            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(host);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            pin.Close();
        }
    }

    /// <summary>The four corners, each <paramref name="size"/> pixels, enlarged without smoothing.</summary>
    private static BitmapSource CornerCloseUp(BitmapSource frame, int size, int zoom)
    {
        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        byte[] pixels = Pixels(frame);
        const int gap = 4;
        int tile = size * zoom;
        int outWidth = (tile * 2) + gap;
        int outHeight = (tile * 2) + gap;
        byte[] output = new byte[outWidth * outHeight * 4];
        Array.Fill(output, (byte)0x80);
        (int X, int Y, int OutX, int OutY)[] corners =
        [
            (0, 0, 0, 0),
            (width - size, 0, tile + gap, 0),
            (0, height - size, 0, tile + gap),
            (width - size, height - size, tile + gap, tile + gap),
        ];
        foreach ((int x0, int y0, int outX, int outY) in corners)
        {
            for (int y = 0; y < tile; y++)
            {
                for (int x = 0; x < tile; x++)
                {
                    int source = (((y0 + (y / zoom)) * width) + x0 + (x / zoom)) * 4;
                    int target = (((outY + y) * outWidth) + outX + x) * 4;
                    Array.Copy(pixels, source, output, target, 4);
                }
            }
        }

        BitmapSource result = BitmapSource.Create(outWidth, outHeight, 96, 96, PixelFormats.Bgra32, null, output, outWidth * 4);
        result.Freeze();
        return result;
    }

    private static BitmapSource Solid(int width, int height, Color color)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = color.B;
            pixels[index + 1] = color.G;
            pixels[index + 2] = color.R;
            pixels[index + 3] = 0xFF;
        }

        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static BitmapSource Checker(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = ((y * width) + x) * 4;
                byte value = ((x + y) % 2) == 0 ? (byte)0x10 : (byte)0xF0;
                pixels[index] = value;
                pixels[index + 1] = (byte)(0xFF - value);
                pixels[index + 2] = value;
                pixels[index + 3] = 0xFF;
            }
        }

        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static Color At(byte[] pixels, int width, int x, int y)
    {
        int index = ((y * width) + x) * 4;
        return Color.FromRgb(pixels[index + 2], pixels[index + 1], pixels[index]);
    }

    /// <summary>
    /// Splits an anti-aliased pixel into the shares of three known colours by least squares,
    /// so coverage of the outline can be measured however it blends with desk and image.
    /// </summary>
    private static (double A, double B, double C) Unmix(Color pixel, Color a, Color b, Color c)
    {
        double[] u = [a.R - c.R, a.G - c.G, a.B - c.B];
        double[] v = [b.R - c.R, b.G - c.G, b.B - c.B];
        double[] w = [pixel.R - c.R, pixel.G - c.G, pixel.B - c.B];
        double uu = Dot(u, u), uv = Dot(u, v), vv = Dot(v, v), uw = Dot(u, w), vw = Dot(v, w);
        double determinant = (uu * vv) - (uv * uv);
        double shareA = ((uw * vv) - (vw * uv)) / determinant;
        double shareB = ((vw * uu) - (uw * uv)) / determinant;
        return (shareA, shareB, 1 - shareA - shareB);

        static double Dot(double[] x, double[] y) => (x[0] * y[0]) + (x[1] * y[1]) + (x[2] * y[2]);
    }

    private static double Distance(Color a, Color b) =>
        Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    private static byte[] Pixels(BitmapSource source)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
