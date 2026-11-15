using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Writes every drawn tool cursor at several pointer sizes, with its measured glyph box, for a
/// before/after comparison. Runs only when MYCAPTURE_EDTOOLS_PHASE and MYCAPTURE_EDTOOLS_OUT are set.
/// </summary>
public sealed class EditorToolCursorEvidenceTests
{
    private static readonly int[] Sizes = [32, 40, 48, 64, 96];

    private static readonly (string Name, ColorRgba Color)[] Inks =
    [
        ("red", ColorRgba.FromRgb(0xEF, 0x44, 0x44)),
        ("white", ColorRgba.White),
        ("black", ColorRgba.FromRgb(0x11, 0x18, 0x27)),
        ("clear", new ColorRgba(8, 0x3B, 0x82, 0xF6)),
    ];

    [Fact]
    public void RenderToolCursorSheet() => StaTestHost.Run(() =>
    {
        string? phase = Environment.GetEnvironmentVariable("MYCAPTURE_EDTOOLS_PHASE");
        string? output = Environment.GetEnvironmentVariable("MYCAPTURE_EDTOOLS_OUT");
        if (phase is not ("before" or "after") || string.IsNullOrWhiteSpace(output)) return;
        if (!Path.IsPathFullyQualified(output)) throw new IOException("Evidence requires an absolute output directory.");
        string folder = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(DiagnosticOutputPaths.Create(output), phase));

        EditorCursorKind[] kinds = Enum.GetValues<EditorCursorKind>().Where(EditorCursorPolicy.IsCustom).ToArray();
        var table = new StringBuilder("kind,ink,size,frameW,frameH,glyphW,glyphH,hotX,hotY\n");
        foreach ((string inkName, ColorRgba ink) in Inks)
        {
            const int cell = 112;
            const int scale = 2;
            var sheet = new DrawingVisual();
            using (DrawingContext dc = sheet.RenderOpen())
            {
                for (int row = 0; row < kinds.Length; row++)
                {
                    for (int column = 0; column < Sizes.Length; column++)
                    {
                        int size = Sizes[column];
                        EditorCursorImage image = EditorCursors.Render(kinds[row], size, 3, ink, EditorCursorStyle.White);
                        (int w, int h) = GlyphSize(image.Bitmap);
                        table.Append(CultureInfo.InvariantCulture, $"{kinds[row]},{inkName},{size},{image.Bitmap.PixelWidth},{image.Bitmap.PixelHeight},{w},{h},{image.HotSpotX},{image.HotSpotY}\n");
                        for (int half = 0; half < 2; half++)
                        {
                            var origin = new Point(column * cell * 2 + half * cell, row * cell);
                            dc.DrawRectangle(new SolidColorBrush(half == 0 ? Color.FromRgb(0xF4, 0xF4, 0xF4) : Color.FromRgb(0x1E, 0x23, 0x2B)), null,
                                new Rect(origin, new Size(cell, cell)));
                            // Pointer pixels shown at twice their size, the frame outlined in grey.
                            var placed = new Rect(origin.X + 4, origin.Y + 4, Math.Min(cell - 8, image.Bitmap.PixelWidth * scale / 2.0), Math.Min(cell - 8, image.Bitmap.PixelHeight * scale / 2.0));
                            RenderOptions.SetBitmapScalingMode(sheet, BitmapScalingMode.NearestNeighbor);
                            dc.DrawImage(image.Bitmap, new Rect(placed.X, placed.Y, image.Bitmap.PixelWidth, image.Bitmap.PixelHeight));
                            dc.DrawRectangle(null, new Pen(Brushes.Gray, 0.5), new Rect(placed.X, placed.Y, image.Bitmap.PixelWidth, image.Bitmap.PixelHeight));
                        }
                    }
                }
            }

            var target = new RenderTargetBitmap(Sizes.Length * cell * 2, kinds.Length * cell, 96, 96, PixelFormats.Pbgra32);
            target.Render(sheet);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using FileStream stream = File.Create(DiagnosticOutputPaths.Child(folder, $"cursors-{inkName}.png"));
            encoder.Save(stream);
        }

        File.WriteAllText(DiagnosticOutputPaths.Child(folder, "cursor-sizes.csv"), table.ToString());
    });

    /// <summary>The width and height of the visible (non-transparent) part of a cursor image.</summary>
    internal static (int Width, int Height) GlyphSize(BitmapSource bitmap)
    {
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        var converted = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (pixels[(((y * width) + x) * 4) + 3] == 0) continue;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
        }

        return right < 0 ? (0, 0) : (right - left + 1, bottom - top + 1);
    }
}
