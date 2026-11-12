using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.App.Pinning;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Increment 5, without windows: which pictures the native source copies exactly (the opaque rule, decided in
/// the one normalization pass), and that a picture which dropped its bitmap gives back the same bytes, format,
/// resolution, metadata and encodings, on any thread, and keeps nothing it built.
/// </summary>
public sealed class PinPictureOneCopyTests
{
    private const string SourceUrl = "https://example.invalid/pinned";

    public static TheoryData<string> OpaqueKinds => new()
    {
        "Bgr32-padding-0", "Bgr32-padding-255", "Bgr32-padding-7", "Bgra32", "Pbgra32", "Bgr24",
        "Bgra32-decoded-png", "Bgr24-decoded-png", "Bgr24-session-decode", "Bgra32-odd-dpi",
    };

    [Theory]
    [MemberData(nameof(OpaqueKinds))]
    public void AnOpaquePictureIsCopiedExactlyAndBuiltAgainWithTheSameBytesFormatResolutionAndEncodings(string kind) => StaTestHost.Run(() =>
    {
        BitmapSource original = OneCopyTestImages.Opaque(kind, 37, 23);
        using NativePinPixelSource source = NativePinPixelSource.FromBitmap(original);
        Assert.True(source.IsExactOpaqueCopy);
        Assert.Equal(original.Format, source.ExactFormat);

        var picture = new PinPicture(original);
        Assert.True(picture.TryDrop(source));
        Assert.True(picture.IsBitmapDropped);
        Assert.Null(picture.Peek);
        long rebuilds = PinPicture.RebuildCount;

        BitmapSource rebuilt = picture.Bitmap;
        Assert.Equal(rebuilds + 1, PinPicture.RebuildCount);
        Assert.NotSame(original, rebuilt);
        Assert.True(rebuilt.IsFrozen);
        Assert.Same(rebuilt, picture.Bitmap);
        Assert.Equal(rebuilds + 1, PinPicture.RebuildCount);
        OneCopyTestImages.AssertSame(original, rebuilt);
        Assert.Equal(original is BitmapFrame, rebuilt is BitmapFrame);
    });

    [Theory]
    [MemberData(nameof(OpaqueKinds))]
    public void ABitmapBuiltOnAWorkerThreadAfterTheWindowsShareWentIsTheSame(string kind) => StaTestHost.Run(() =>
    {
        BitmapSource original = OneCopyTestImages.Opaque(kind, 41, 9);
        OneCopyTestImages.Collect();
        int sources = NativePinPixelSource.LiveAllocationCount;
        PinPicture picture = DroppedWithoutWindowShare(original);
        Assert.Equal(sources + 1, NativePinPixelSource.LiveAllocationCount);

        (byte[] Png, byte[] Pixels, PixelFormat Format, double DpiX) worker = Task.Run(() =>
        {
            BitmapSource built = picture.Bitmap;
            return (ImageCodec.EncodePng(built, SourceUrl), OneCopyTestImages.Raw(built), built.Format, built.DpiX);
        }).GetAwaiter().GetResult();
        Assert.Equal(ImageCodec.EncodePng(original, SourceUrl), worker.Png);
        Assert.Equal(OneCopyTestImages.Raw(original), worker.Pixels);
        Assert.Equal(original.Format, worker.Format);
        Assert.Equal(original.DpiX, worker.DpiX);
        OneCopyTestImages.Collect();
        Assert.Null(picture.Peek);
        GC.KeepAlive(picture);
    });

    public static TheoryData<string> NotOpaqueKinds => new()
    {
        "Bgra32-one-alpha-254-in-vector-part", "Bgra32-one-alpha-254-in-tail", "Pbgra32-one-alpha-254-in-vector-part",
        "Pbgra32-one-alpha-254-in-tail", "Bgra32-one-alpha-0", "Bgr32-mixed-padding", "Gray8-converted", "Bgr565-converted",
    };

    [Theory]
    [MemberData(nameof(NotOpaqueKinds))]
    public void APictureThatIsNotAnExactOpaqueCopyKeepsItsBitmap(string kind) => StaTestHost.Run(() =>
    {
        BitmapSource original = OneCopyTestImages.NotOpaque(kind, 37, 23);
        using NativePinPixelSource source = NativePinPixelSource.FromBitmap(original);
        Assert.False(source.IsExactOpaqueCopy);
        var picture = new PinPicture(original);
        Assert.False(picture.TryDrop(source));
        Assert.False(picture.IsBitmapDropped);
        Assert.Same(original, picture.Bitmap);
        Assert.Same(original, picture.Peek);
        Assert.Null(picture.ShareSource());
    });

    [Fact]
    public void TheOpaqueRuleLeavesTheNormalizedPixelsAsTheyWere() => StaTestHost.Run(() =>
    {
        foreach (PixelFormat format in new[] { PixelFormats.Bgr32, PixelFormats.Bgra32, PixelFormats.Pbgra32 })
        {
            byte[] row = new byte[4 * 13];
            for (int index = 0; index < row.Length; index++) row[index] = (byte)(index * 37 + 11);
            byte[] plain = (byte[])row.Clone();
            NativePinPixelSource.Normalize(plain, format);
            var seen = NativePinPixelSource.FourthByte.Start;
            NativePinPixelSource.Normalize(row, format, ref seen);
            Assert.Equal(plain, row);
            Assert.Equal(-1, seen.Uniform);
        }
    });

    [Fact]
    public void TheSourceLivesUntilItsLastOwnerAndThePictureReleasesItOnlyAfterKeepingABitmap() => StaTestHost.Run(() =>
    {
        BitmapSource original = OneCopyTestImages.Opaque("Bgr32-padding-0", 64, 32);
        OneCopyTestImages.Collect();
        int sources = NativePinPixelSource.LiveAllocationCount;
        long bytes = NativePinPixelSource.LiveBytes;
        NativePinPixelSource window = NativePinPixelSource.FromBitmap(original);
        var picture = new PinPicture(original);
        Assert.True(picture.TryDrop(window));
        Assert.Equal(sources + 1, NativePinPixelSource.LiveAllocationCount);

        // A second window for the same picture shares it; the pixels exist once.
        NativePinPixelSource second = Assert.IsType<NativePinPixelSource>(picture.ShareSource());
        Assert.Equal(sources + 1, NativePinPixelSource.LiveAllocationCount);
        Assert.Equal(bytes + window.ByteLength, NativePinPixelSource.LiveBytes);
        window.Dispose();
        second.Dispose();
        window.Dispose();
        // The picture's own share still holds the pixels: it can build its bitmap after the windows have gone.
        Assert.Equal(sources + 1, NativePinPixelSource.LiveAllocationCount);
        picture.ReleaseSource();
        Assert.Equal(sources + 1, NativePinPixelSource.LiveAllocationCount);
        OneCopyTestImages.AssertSame(original, picture.Bitmap);

        BitmapSource kept = picture.Keep();
        Assert.False(picture.IsBitmapDropped);
        Assert.Same(kept, picture.Bitmap);
        Assert.Null(picture.ShareSource());
        picture.ReleaseSource();
        Assert.Equal(sources, NativePinPixelSource.LiveAllocationCount);
        Assert.Equal(bytes, NativePinPixelSource.LiveBytes);
        OneCopyTestImages.AssertSame(original, picture.Bitmap);
        // Keeping does not drop again.
        using NativePinPixelSource again = NativePinPixelSource.FromBitmap(original);
        Assert.False(picture.TryDrop(again));
    });

    [Fact]
    public void KeepReusesABitmapBuiltEarlierThatIsStillReferenced() => StaTestHost.Run(() =>
    {
        BitmapSource original = OneCopyTestImages.Opaque("Pbgra32", 20, 10);
        using NativePinPixelSource window = NativePinPixelSource.FromBitmap(original);
        var picture = new PinPicture(original);
        Assert.True(picture.TryDrop(window));
        BitmapSource forCopy = picture.Bitmap;
        Assert.Same(forCopy, picture.Keep());
        picture.ReleaseSource();
    });

    [Fact]
    public void ADroppedPictureNobodyReferencesAnyMoreIsFreedWithItsLastReference() => StaTestHost.Run(() =>
    {
        OneCopyTestImages.Collect();
        int sources = NativePinPixelSource.LiveAllocationCount;
        WeakReference picture = DropAndForget(OneCopyTestImages.Opaque("Bgra32", 128, 64));
        Assert.Equal(sources + 1, NativePinPixelSource.LiveAllocationCount);
        OneCopyTestImages.Collect();
        Assert.False(picture.IsAlive);
        Assert.Equal(sources, NativePinPixelSource.LiveAllocationCount);
    });

    [Fact]
    public void ThePinPngCacheDoesNotKeepTheBitmapItEncoded() => StaTestHost.Run(() =>
    {
        BitmapSource original = OneCopyTestImages.Opaque("Pbgra32", 20, 10);
        using NativePinPixelSource window = NativePinPixelSource.FromBitmap(original);
        var picture = new PinPicture(original);
        Assert.True(picture.TryDrop(window));
        using var cache = new ClipboardImageService.PngCache();
        byte[] png = EncodeThroughCache(cache, picture);
        Assert.Equal(ImageCodec.EncodePng(original, SourceUrl), png);
        OneCopyTestImages.Collect();
        Assert.Null(picture.Peek);
        Assert.Equal(0, cache.ByteCount);
        // A held bitmap keeps its entry, as before.
        cache.Clear();
        byte[] kept = cache.GetOrEncode(original, SourceUrl, ImageCodec.EncodePng);
        Assert.Equal(png, kept);
        OneCopyTestImages.Collect();
        Assert.Same(kept, cache.TryGetEncoded(original, SourceUrl));
        Assert.Same(kept, cache.GetOrEncode(original, SourceUrl, static (_, _) => throw new InvalidOperationException("Encoded twice.")));
        Assert.Equal(kept.Length, cache.ByteCount);
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] EncodeThroughCache(ClipboardImageService.PngCache cache, PinPicture picture)
    {
        BitmapSource built = picture.Bitmap;
        byte[] png = cache.GetOrEncode(built, SourceUrl, ImageCodec.EncodePng);
        Assert.Same(png, cache.TryGetEncoded(picture.Bitmap, SourceUrl));
        return png;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PinPicture DroppedWithoutWindowShare(BitmapSource original)
    {
        NativePinPixelSource window = NativePinPixelSource.FromBitmap(original);
        var picture = new PinPicture(original);
        Assert.True(picture.TryDrop(window));
        window.Dispose();
        return picture;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DropAndForget(BitmapSource original)
    {
        PinPicture picture = DroppedWithoutWindowShare(original);
        OneCopyTestImages.AssertSame(original, picture.Bitmap);
        return new WeakReference(picture);
    }
}

/// <summary>Pictures in the formats a pin receives, and the comparisons that define "the same picture".</summary>
internal static class OneCopyTestImages
{
    private const string SourceUrl = "https://example.invalid/pinned";

    internal static BitmapSource Opaque(string kind, int width, int height) => kind switch
    {
        "Bgr32-padding-0" => Make(PixelFormats.Bgr32, width, height, static (_, _) => 0),
        "Bgr32-padding-255" => Make(PixelFormats.Bgr32, width, height, static (_, _) => 255),
        "Bgr32-padding-7" => Make(PixelFormats.Bgr32, width, height, static (_, _) => 7),
        "Bgra32" => Make(PixelFormats.Bgra32, width, height, static (_, _) => 255),
        "Pbgra32" => Make(PixelFormats.Pbgra32, width, height, static (_, _) => 255),
        "Bgr24" => Make(PixelFormats.Bgr24, width, height, static (_, _) => 255),
        "Bgra32-odd-dpi" => Make(PixelFormats.Bgra32, width, height, static (_, _) => 255, 119.98960113525391),
        "Bgra32-decoded-png" => DecodedLikeTheClipboard(Make(PixelFormats.Bgra32, width, height, static (_, _) => 255, 144)),
        "Bgr24-decoded-png" => DecodedLikeTheClipboard(Make(PixelFormats.Bgr32, width, height, static (_, _) => 0)),
        "Bgr24-session-decode" => ImageCodec.TryDecodePng(ImageCodec.EncodePng(
            Make(PixelFormats.Bgr32, width, height, static (_, _) => 0, 120), SourceUrl))!,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static BitmapSource NotOpaque(string kind, int width, int height)
    {
        // 37 pixels a row: 36 in the sixteen-byte part, one in the tail.
        int last = width - 1;
        return kind switch
        {
            "Bgra32-one-alpha-254-in-vector-part" => Make(PixelFormats.Bgra32, width, height, (x, y) => x == 5 && y == 7 ? (byte)254 : (byte)255),
            "Bgra32-one-alpha-254-in-tail" => Make(PixelFormats.Bgra32, width, height, (x, y) => x == last && y == 3 ? (byte)254 : (byte)255),
            "Pbgra32-one-alpha-254-in-vector-part" => Make(PixelFormats.Pbgra32, width, height, (x, y) => x == 5 && y == 7 ? (byte)254 : (byte)255),
            "Pbgra32-one-alpha-254-in-tail" => Make(PixelFormats.Pbgra32, width, height, (x, y) => x == last && y == height - 1 ? (byte)254 : (byte)255),
            "Bgra32-one-alpha-0" => Make(PixelFormats.Bgra32, width, height, (x, y) => x == 0 && y == 0 ? (byte)0 : (byte)255),
            "Bgr32-mixed-padding" => Make(PixelFormats.Bgr32, width, height, (x, y) => x == 9 && y == 2 ? (byte)255 : (byte)0),
            "Gray8-converted" => Converted(Make(PixelFormats.Bgra32, width, height, static (_, _) => 255), PixelFormats.Gray8),
            "Bgr565-converted" => Converted(Make(PixelFormats.Bgra32, width, height, static (_, _) => 255), PixelFormats.Bgr565),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    internal static BitmapSource Make(PixelFormat format, int width, int height, Func<int, int, byte> fourth, double dpi = 96)
    {
        int bytesPerPixel = format.BitsPerPixel / 8;
        int stride = width * bytesPerPixel;
        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * stride) + (x * bytesPerPixel);
                byte alpha = fourth(x, y);
                bool premultiplied = format == PixelFormats.Pbgra32;
                byte Channel(int value) => premultiplied ? (byte)((value * alpha + 127) / 255) : (byte)value;
                pixels[offset] = Channel((x * 29 + y * 7) % 256);
                pixels[offset + 1] = Channel((x * 11 + y * 31) % 256);
                pixels[offset + 2] = Channel((x * 3 + y * 17 + 40) % 256);
                if (bytesPerPixel == 4) pixels[offset + 3] = alpha;
            }
        BitmapSource image = BitmapSource.Create(width, height, dpi, dpi, format, null, pixels, stride);
        image.Freeze();
        return image;
    }

    private static BitmapSource Converted(BitmapSource image, PixelFormat format)
    {
        var converted = new FormatConvertedBitmap(image, format, null, 0);
        converted.Freeze();
        return converted;
    }

    // As ClipboardImageReader.DecodePng: a PNG with its source chunk, decoded, cloned and frozen.
    private static BitmapSource DecodedLikeTheClipboard(BitmapSource image)
    {
        byte[] png = ImageCodec.EncodePng(image, SourceUrl);
        BitmapDecoder decoder = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource copy = decoder.Frames[0].Clone();
        copy.Freeze();
        return copy;
    }

    internal static byte[] Raw(BitmapSource image)
    {
        int stride = image.PixelWidth * image.Format.BitsPerPixel / 8;
        byte[] pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    internal static byte[] Bmp(BitmapSource image)
    {
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Same pixels and bytes, format, resolution, and the same PNG and BMP files (the clipboard's formats).</summary>
    internal static void AssertSame(BitmapSource expected, BitmapSource actual)
    {
        Assert.Equal(expected.Format, actual.Format);
        Assert.Equal(expected.PixelWidth, actual.PixelWidth);
        Assert.Equal(expected.PixelHeight, actual.PixelHeight);
        Assert.Equal(expected.DpiX, actual.DpiX);
        Assert.Equal(expected.DpiY, actual.DpiY);
        Assert.Equal(Raw(expected), Raw(actual));
        Assert.Equal(ImageCodec.EncodePng(expected), ImageCodec.EncodePng(actual));
        Assert.Equal(ImageCodec.EncodePng(expected, SourceUrl), ImageCodec.EncodePng(actual, SourceUrl));
        Assert.Equal(Bmp(expected), Bmp(actual));
    }

    internal static void Collect()
    {
        for (int pass = 0; pass < 3; pass++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }
}
