using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class EditorCropRoiTests
{
    [Theory]
    [InlineData("Bgra32", 0)] [InlineData("Bgra32", 1)]
    [InlineData("Bgra32", 2)] [InlineData("Bgra32", 3)]
    [InlineData("Pbgra32", 0)] [InlineData("Pbgra32", 1)]
    [InlineData("Pbgra32", 2)] [InlineData("Pbgra32", 3)]
    [InlineData("Bgr32", 0)] [InlineData("Bgr32", 1)]
    [InlineData("Bgr32", 2)] [InlineData("Bgr32", 3)]
    [InlineData("Bgr24", 0)] [InlineData("Bgr24", 1)]
    [InlineData("Bgr24", 2)] [InlineData("Bgr24", 3)]
    public void ProductionCropAndRedoReadFourBytePixelsDpiAndRotation(string format, int turns) => StaTestHost.Run(() =>
    {
        BitmapSource source = Pattern(79, 53, Format(format));
        AnnotationEditorControl editor = Editor(source);
        for (int turn = 0; turn < turns; turn++) Assert.True(editor.HandleShortcut(Key.W, ModifierKeys.None));
        BitmapSource oriented = editor.DisplayedBitmap;
        // Includes a fractional selection at the right/bottom edge; the production pixel
        // rounding and clamping remain part of the crop command, not the extraction helper.
        RectD region = new(oriented.PixelWidth - 26.4, oriented.PixelHeight - 18.7, 29, 23);
        RectD pixels = region.Normalized().ClampTo(new RectD(0, 0, oriented.PixelWidth, oriented.PixelHeight)).ToPixelBounds();
        BitmapSource expected = ReferenceCrop(oriented, (int)pixels.X, (int)pixels.Y, (int)pixels.Width, (int)pixels.Height);
        editor.ApplyCrop(region);
        AssertBitmap(expected, editor.DisplayedBitmap);
        SaveSnapshot(editor.DisplayedBitmap, $"{format}-{turns}-crop");
        Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
        AssertBitmap(oriented, editor.DisplayedBitmap);
        Assert.True(editor.HandleShortcut(Key.Y, ModifierKeys.Control));
        AssertBitmap(expected, editor.DisplayedBitmap);
        SaveSnapshot(editor.DisplayedBitmap, $"{format}-{turns}-redo");
        Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
        AssertBitmap(oriented, editor.DisplayedBitmap);
    });

    [Fact]
    public void LibraryReopenedCaptureCropsToItsOwnOpaquePixels() => StaTestHost.Run(() =>
    {
        // A capture is Bgr32 and its saved PNG is 24-bit, so the library editor reopens it as
        // Bgr24. Reading those three-byte pixels four bytes at a time striped and skewed the crop.
        BitmapSource capture = Pattern(773, 61, PixelFormats.Bgr32);
        string root = OwnedTestDirectory.Create("mc-crop24-");
        try
        {
            string path = Path.Combine(root, "original.png");
            ImageCodec.SavePng(capture, path);
            BitmapSource reopened = ImageCodec.TryLoad(path)!;
            Assert.Equal(PixelFormats.Bgr24, reopened.Format);
            AnnotationEditorControl editor = Editor(reopened);

            editor.ApplyCrop(new RectD(13, 9, 731, 40));

            byte[] source = Pixels(capture);
            byte[] expected = new byte[731 * 4 * 40];
            for (int row = 0; row < 40; row++)
                for (int column = 0; column < 731; column++)
                {
                    int from = (((9 + row) * 773) + 13 + column) * 4, to = ((row * 731) + column) * 4;
                    Buffer.BlockCopy(source, from, expected, to, 3);
                    expected[to + 3] = 255;
                }
            Assert.Equal(PixelFormats.Bgra32, editor.DisplayedBitmap.Format);
            Assert.Equal(expected, Pixels(editor.DisplayedBitmap));
            // Undo and redo cut the same pixels again from the reopened original.
            Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
            Assert.Equal(773, editor.DisplayedBitmap.PixelWidth);
            Assert.True(editor.HandleShortcut(Key.Y, ModifierKeys.Control));
            Assert.Equal(expected, Pixels(editor.DisplayedBitmap));
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void NestedCropsRestoreTheSameOriginAcrossUndoAndRedo() => StaTestHost.Run(() =>
    {
        BitmapSource source = Pattern(97, 61, PixelFormats.Bgra32);
        AnnotationEditorControl editor = Editor(source);
        editor.ApplyCrop(new RectD(11, 7, 63, 43));
        BitmapSource first = editor.DisplayedBitmap;
        editor.ApplyCrop(new RectD(8, 5, 29, 17));
        BitmapSource expected = ReferenceCrop(source, 19, 12, 29, 17);
        AssertBitmap(expected, editor.DisplayedBitmap);
        Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
        AssertBitmap(first, editor.DisplayedBitmap);
        Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
        AssertBitmap(source, editor.DisplayedBitmap);
        Assert.True(editor.HandleShortcut(Key.Y, ModifierKeys.Control));
        AssertBitmap(first, editor.DisplayedBitmap);
        Assert.True(editor.HandleShortcut(Key.Y, ModifierKeys.Control));
        AssertBitmap(expected, editor.DisplayedBitmap);
    });

    [Fact]
    public void ProductionCropAndHistoryAllocationEvidence() => StaTestHost.Run(() =>
    {
        // Opt-in evidence writes only. The normal suite still exercises the production
        // operations without a timing threshold or a performance-dependent pass result.
        string? output = Environment.GetEnvironmentVariable("MYCAPTURE_CROP_ROI_EVIDENCE");
        var measurements = new List<object>();
        foreach (PixelFormat format in new[] { PixelFormats.Bgra32, PixelFormats.Bgr24 })
        {
            BitmapSource source = Pattern(3840, 2160, format);
            AnnotationEditorControl editor = Editor(source);
            var region = new RectD(123, 87, 640, 480);
            BitmapSource expected = ReferenceCrop(source, 123, 87, 640, 480);
            // Warm the real editor handlers and WPF bitmap paths before measuring.
            editor.ApplyCrop(region);
            Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
            Assert.True(editor.HandleShortcut(Key.Y, ModifierKeys.Control));
            Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
            long cropBytes = 0, redoBytes = 0, cropTicks = 0, redoTicks = 0;
            const int iterations = 5;
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                editor.ApplyCrop(region);
                cropTicks += Stopwatch.GetTimestamp() - started;
                cropBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
                AssertBitmap(expected, editor.DisplayedBitmap);
                Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
                allocated = GC.GetAllocatedBytesForCurrentThread();
                started = Stopwatch.GetTimestamp();
                bool redone = editor.HandleShortcut(Key.Y, ModifierKeys.Control);
                redoTicks += Stopwatch.GetTimestamp() - started;
                redoBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
                Assert.True(redone);
                AssertBitmap(expected, editor.DisplayedBitmap);
                Assert.True(editor.HandleShortcut(Key.Z, ModifierKeys.Control));
                AssertBitmap(source, editor.DisplayedBitmap);
            }
            measurements.Add(new
            {
                Format = format.ToString(), SourceWidth = 3840, SourceHeight = 2160,
                CropWidth = 640, CropHeight = 480, Iterations = iterations,
                CropAllocatedBytes = cropBytes, RedoAllocatedBytes = redoBytes,
                CropMilliseconds = cropTicks * 1000d / Stopwatch.Frequency,
                RedoMilliseconds = redoTicks * 1000d / Stopwatch.Frequency,
                CropPixelsSha256 = Convert.ToHexString(SHA256.HashData(Pixels(expected))),
            });
        }
        if (!string.IsNullOrWhiteSpace(output))
        {
            output = Path.GetFullPath(output);
            if (File.Exists(output)) throw new IOException("Preserving existing crop evidence: " + output);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
        }
    });

    private static AnnotationEditorControl Editor(BitmapSource bitmap)
    {
        var bounds = new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        return new AnnotationEditorControl(new FrozenFrame(bitmap, bounds, null, 0), bounds, bitmap, null, null);
    }

    private static PixelFormat Format(string name) => name switch
    {
        "Bgra32" => PixelFormats.Bgra32,
        "Pbgra32" => PixelFormats.Pbgra32,
        "Bgr32" => PixelFormats.Bgr32,
        "Bgr24" => PixelFormats.Bgr24,
        _ => throw new ArgumentException(name),
    };

    private static BitmapSource Pattern(int width, int height, PixelFormat format)
    {
        int channels = format.BitsPerPixel / 8;
        int stride = width * channels;
        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int index = y * stride + x * channels;
                byte alpha = (byte)((x * 29 + y * 37) % 256);
                for (int channel = 0; channel < 3; channel++)
                {
                    byte value = (byte)((x * 17 + y * 13 + channel * 71) % 256);
                    pixels[index + channel] = format == PixelFormats.Pbgra32 ? (byte)(value * alpha / 255) : value;
                }
                if (channels == 4) pixels[index + 3] = alpha;
            }
        BitmapSource bitmap = BitmapSource.Create(width, height, 144, 120, format, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    // An independent whole-image reference: Bgra32 and Pbgra32 bytes as stored, any other
    // format converted to Bgra32 first (an opaque one gains alpha 255), then cut by index.
    // The old reference addressed Bgr24 four bytes at a time and so pinned the striped crop.
    private static BitmapSource ReferenceCrop(BitmapSource source, int x, int y, int width, int height)
    {
        int stride = source.PixelWidth * 4;
        byte[] full = new byte[stride * source.PixelHeight];
        FourByte(source).CopyPixels(full, stride, 0);
        int cropStride = width * 4;
        byte[] crop = new byte[cropStride * height];
        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(full, ((y + row) * stride) + x * 4, crop, row * cropStride, cropStride);
        BitmapSource bitmap = BitmapSource.Create(width, height, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, crop, cropStride);
        bitmap.Freeze();
        return bitmap;
    }

    internal static BitmapSource FourByte(BitmapSource source) =>
        source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32
            ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

    private static byte[] Pixels(BitmapSource source)
    {
        int stride = source.PixelWidth * 4;
        byte[] pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void AssertBitmap(BitmapSource expected, BitmapSource actual)
    {
        Assert.Equal(expected.PixelWidth, actual.PixelWidth);
        Assert.Equal(expected.PixelHeight, actual.PixelHeight);
        Assert.Equal(expected.DpiX, actual.DpiX, 5);
        Assert.Equal(expected.DpiY, actual.DpiY, 5);
        Assert.Equal(expected.Format, actual.Format);
        Assert.True(actual.IsFrozen);
        Assert.Equal(Pixels(expected), Pixels(actual));
    }

    private static void SaveSnapshot(BitmapSource bitmap, string name)
    {
        string? root = Environment.GetEnvironmentVariable("MYCAPTURE_CROP_ROI_SNAPSHOT_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, name + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write);
        encoder.Save(stream);
    }
}
