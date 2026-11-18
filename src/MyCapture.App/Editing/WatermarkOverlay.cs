using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Annotations;

namespace MyCapture.App.Editing;

/// <summary>
/// Turns a watermark picture into the grey overlay that prints it into the page under it
/// (see <see cref="WatermarkInk"/>), for <see cref="AnnotationRenderer"/>.
/// </summary>
/// <remarks>
/// <para>
/// The overlay is an ordinary premultiplied picture the size of the watermark, drawn where the
/// watermark would be drawn, so the editor, saving, copying and every export show the same
/// pixels through the one renderer. Only the page brightness under it is read, from the
/// unannotated capture.
/// </para>
/// <para>
/// The page is sampled on a grid in the watermark's own pixels, no farther apart than
/// <see cref="GridSpacing"/> capture pixels (and at most <see cref="MaxGridStep"/> watermark
/// pixels). A watermark over a page that is light (or dark) at every node gets the same all-dark
/// (or all-light) overlay wherever it moves, built once per picture. Only one that straddles light
/// and dark is built for its place, interpolating the grid, so a drag across the edge of a dark
/// sidebar stays within a frame.
/// </para>
/// </remarks>
internal static class WatermarkOverlay
{
    private static readonly ConditionalWeakTable<BitmapSource, Ink> Inks = [];
    private static readonly ConditionalWeakTable<BitmapSource, PageBrightness> Pages = [];

    /// <summary>The largest distance, in capture pixels, between two page samples under a watermark.</summary>
    private const double GridSpacing = 8;

    /// <summary>The largest distance, in watermark pixels, between two page samples.</summary>
    private const int MaxGridStep = 16;

    /// <summary>Rows of the capture read at a time when its brightness is first measured.</summary>
    private const int BandRows = 32;

    /// <summary>
    /// The overlay for <paramref name="picture"/> drawn into <paramref name="box"/>, rotated by
    /// <paramref name="rotationDegrees"/> about its centre, over <paramref name="page"/>.
    /// Without a page the paper is taken as light.
    /// </summary>
    internal static BitmapSource For(BitmapSource picture, Rect box, double rotationDegrees, BitmapSource? page)
    {
        Ink ink = Inks.GetValue(picture, static source => Ink.From(source));
        if (page is null || box.IsEmpty || box.Width <= 0 || box.Height <= 0) return ink.Dark;

        PageBrightness brightness = BrightnessOf(page);
        int width = ink.Width;
        int height = ink.Height;

        // Capture pixels per watermark pixel decide how many watermark pixels one spacing spans.
        double scale = Math.Max(box.Width / width, box.Height / height);
        int step = Math.Clamp((int)Math.Floor(GridSpacing / scale), 1, MaxGridStep);
        int columns = WatermarkInk.GridCount(width, step);
        int rows = WatermarkInk.GridCount(height, step);

        double radians = rotationDegrees * Math.PI / 180;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double cx = box.Left + (box.Width / 2);
        double cy = box.Top + (box.Height / 2);
        var shares = new float[columns * rows];
        bool anyDark = false;
        bool anyLight = false;
        bool mixed = false;
        for (int gy = 0; gy < rows; gy++)
        {
            double dy = (((WatermarkInk.GridNode(gy, height, step) + 0.5) / height) - 0.5) * box.Height;
            for (int gx = 0; gx < columns; gx++)
            {
                double dx = (((WatermarkInk.GridNode(gx, width, step) + 0.5) / width) - 0.5) * box.Width;
                double share = WatermarkInk.DarkShare(brightness.At(cx + (dx * cos) - (dy * sin), cy + (dx * sin) + (dy * cos)));
                shares[(gy * columns) + gx] = (float)share;
                if (share >= 1) anyDark = true;
                else if (share <= 0) anyLight = true;
                else mixed = true;
            }
        }

        if (!mixed && !(anyDark && anyLight)) return anyLight ? ink.Light : ink.Dark;

        byte[] pixels = new byte[checked(width * height * 4)];
        if (ink.Strength.Length == width * height)
        {
            WatermarkInk.FillGrid(ink.Strength, width, height, shares, columns, rows, step, pixels);
        }

        return Frozen(BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4));
    }

    /// <summary>The brightness map of <paramref name="page"/>, built on first use and kept with it.</summary>
    internal static PageBrightness BrightnessOf(BitmapSource page) => Pages.GetValue(page, static source => FromPage(source));

    /// <summary>
    /// Measures the page a band of rows at a time, so a 4K capture is never copied whole.
    /// Formats whose bytes are already blue, green, red and a fourth byte are read as they are.
    /// </summary>
    private static PageBrightness FromPage(BitmapSource page)
    {
        PixelFormat format = page.Format;
        BitmapSource source = format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 || format == PixelFormats.Bgr32
            ? page
            : new FormatConvertedBitmap(page, PixelFormats.Bgra32, null, 0);
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        var builder = new PageBrightness.Builder(width, height);
        if (width <= 0 || height <= 0) return builder.Build();

        int stride = checked(width * 4);
        byte[] band = new byte[checked(stride * Math.Min(BandRows, height))];
        for (int y = 0; y < height; y += BandRows)
        {
            int count = Math.Min(BandRows, height - y);
            source.CopyPixels(new Int32Rect(0, y, width, count), band, stride, 0);
            builder.AddRows(band, y, count, stride);
        }

        return builder.Build();
    }

    private static (byte[] Pixels, int Width, int Height) Bgra(BitmapSource source)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        byte[] pixels = new byte[checked(width * height * 4)];
        converted.CopyPixels(pixels, width * 4, 0);
        return (pixels, width, height);
    }

    private static BitmapSource Frozen(BitmapSource bitmap)
    {
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A picture's ink strength and its all-dark and all-light overlays.</summary>
    private sealed class Ink
    {
        private Ink(float[] strength, int width, int height)
        {
            Strength = strength;
            Width = width;
            Height = height;
            Dark = Build(strength, width, height, 1);
            Light = Build(strength, width, height, 0);
        }

        internal float[] Strength { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal BitmapSource Dark { get; }
        internal BitmapSource Light { get; }

        internal static Ink From(BitmapSource picture)
        {
            (byte[] pixels, int width, int height) = Bgra(picture);
            return new Ink(WatermarkInk.Compute(pixels, width, height, width * 4), Math.Max(1, width), Math.Max(1, height));
        }

        private static BitmapSource Build(float[] strength, int width, int height, double darkShare)
        {
            byte[] pixels = new byte[checked(width * height * 4)];
            if (strength.Length == width * height)
            {
                WatermarkInk.FillUniform(strength, darkShare, pixels);
            }

            return Frozen(BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4));
        }
    }
}
