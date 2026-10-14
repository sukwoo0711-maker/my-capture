using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Threading;
using MyCapture.Core.Settings;
using MyCapture.Core.Queue;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ImageExportPolicyTests
{
    private const string Url = "https://example.com/private?item=42";

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FolderAndZipPublishTheSamePolicyAndLeaveStagedMediaUntouched(bool include)
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "MyCapture.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        string root = Path.Combine(repository.FullName, "artifacts", "export-policy-tests", Guid.NewGuid().ToString("N"));
        string stage = Path.Combine(root, "stage");
        string destination = Path.Combine(root, "output");
        Directory.CreateDirectory(stage); Directory.CreateDirectory(destination);
        byte[] png = await StaThreadTask.RunAsync(() => ImageCodec.EncodePng(Image(1600, 4), Url));
        const string name = "Capture title.png";
        string source = Path.Combine(stage, name); await File.WriteAllBytesAsync(source, png);
        var record = new CaptureRecord { Title = "Capture title" };
        var plan = new GalleryFolderExportPlan([new(record, 0, source, name)], [], [name]);
        // Retain owned fixtures; this test never invokes staging cleanup or file deletion.
        var prepared = new GalleryFolderExportService.PreparedFolderExport(stage, plan, []);
        var options = new ImageExportOptions { IncludeSourceUrl = include, LongEdge = 800 };
        IReadOnlyList<string> outputs = await GalleryFolderExportService.ExportDirectoryAsync(prepared, destination, options: options);
        string zipPath = Path.Combine(root, "export.zip");
        await GalleryFolderExportService.ExportZipAsync(prepared, zipPath, options: options);
        byte[] folderBytes = await File.ReadAllBytesAsync(Assert.Single(outputs));
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry entry = Assert.Single(archive.Entries);
        Assert.Equal(name, entry.FullName);
        using var zipBytes = new MemoryStream();
        await using (Stream content = entry.Open()) await content.CopyToAsync(zipBytes);
        Assert.Equal(folderBytes, zipBytes.ToArray());
        Assert.Equal(include ? Url : string.Empty, PngSourceUrlMetadata.TryReadSourceUrl(folderBytes));
        Assert.Equal((800, 2), await StaThreadTask.RunAsync(() => Size(folderBytes)));
        Assert.Equal(png, await File.ReadAllBytesAsync(source));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SaveAsCopyUsesOnlySizeAndUrlOptions(bool include) => await StaThreadTask.RunAsync(() =>
    {
        BitmapSource source = Image(1600, 800);
        byte[] exported = ImageExportEncoder.EncodePngCopy(source,
            new() { IncludeSourceUrl = include, LongEdge = 800, Margin = 128, Shadow = true }, Url);
        Assert.Equal((800, 400), Size(exported));
        Assert.Equal(include ? Url : string.Empty, PngSourceUrlMetadata.TryReadSourceUrl(exported));
        return true;
    });

    [Fact]
    public async Task StripRemovesDuplicateUrlsBeforeAndAfterImageDataAndPreservesOtherChunks() => await StaThreadTask.RunAsync(() =>
    {
        byte[] plain = ImageCodec.EncodePng(Image(15, 9));
        byte[] other = Chunk("tEXt", "Author\0Test fixture");
        byte[] expected = [.. plain.AsSpan(0, plain.Length - 12), .. other, .. plain.AsSpan(plain.Length - 12)];
        byte[] before = PngSourceUrlMetadata.WithSourceUrl(PngSourceUrlMetadata.WithSourceUrl(expected, Url), Url);
        byte[] after = Chunk("tEXt", "SourceURL\0" + Url);
        byte[] input = [.. before.AsSpan(0, before.Length - 12), .. after, .. before.AsSpan(before.Length - 12)];
        byte[] snapshot = input.ToArray();
        byte[] stripped = PngSourceUrlMetadata.StripSourceUrl(input);
        Assert.Equal(expected, stripped);
        Assert.Equal(snapshot, input);
        Assert.Equal(string.Empty, PngSourceUrlMetadata.TryReadSourceUrl(stripped));
        Assert.Same(stripped, PngSourceUrlMetadata.StripSourceUrl(stripped));
        AssertCrcs(stripped);
        Assert.Equal((15, 9), Size(stripped));
        return true;
    });

    [Fact]
    public async Task StripFailsClosedOnTruncationAndOversizedChunks() => await StaThreadTask.RunAsync(() =>
    {
        byte[] png = ImageCodec.EncodePng(Image(2, 2), Url);
        Assert.Throws<InvalidDataException>(() => PngSourceUrlMetadata.StripSourceUrl(png[..^1]));
        Assert.Throws<InvalidDataException>(() => PngSourceUrlMetadata.StripSourceUrl([1, 2, 3]));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(33, 4), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => PngSourceUrlMetadata.StripSourceUrl(png));
        return true;
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BatchResizesAndAppliesUrlPolicyWithoutChangingSource(bool include) => await StaThreadTask.RunAsync(() =>
    {
        byte[] png = ImageCodec.EncodePng(Image(1600, 400), Url);
        byte[] snapshot = png.ToArray();
        byte[] exported = GalleryImageExport.PreparePng(png, new() { IncludeSourceUrl = include, LongEdge = 800 });
        Assert.Equal((800, 200), Size(exported));
        Assert.Equal(include ? Url : string.Empty, PngSourceUrlMetadata.TryReadSourceUrl(exported));
        Assert.Equal(snapshot, png);
        AssertCrcs(exported);
        byte[] sameSize = GalleryImageExport.PreparePng(png, new() { IncludeSourceUrl = include });
        Assert.Equal((1600, 400), Size(sameSize));
        Assert.Equal(include ? Url : string.Empty, PngSourceUrlMetadata.TryReadSourceUrl(sameSize));
        return true;
    });

    [Theory]
    [InlineData(0)] [InlineData(24)] [InlineData(128)]
    public async Task StyledPixelsMatchRequestedCanvasAndPreserveAlpha(int margin) => await StaThreadTask.RunAsync(() =>
    {
        BitmapSource source = Image(80, 40);
        var options = new ImageExportOptions { Format = ImageExportFormat.Png, Margin = margin, CornerRadius = 12, Shadow = true };
        ImageExportResult result = ImageExportEncoder.Encode(source, ImageCodec.EncodePng(source, Url), options, Colors.Red, Url);
        Assert.Equal((80 + 2 * margin, 40 + 2 * margin), Size(result.Bytes));
        Assert.Equal(80 + 2 * margin, result.PixelWidth);
        Assert.Equal(40 + 2 * margin, result.PixelHeight);
        Assert.Equal(string.Empty, PngSourceUrlMetadata.TryReadSourceUrl(result.Bytes));
        using var stream = new MemoryStream(result.Bytes);
        BitmapSource decoded = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        byte[] corner = new byte[4];
        converted.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), corner, 4, 0);
        Assert.Equal(0, corner[3]);
        Assert.Equal((80, 40), (source.PixelWidth, source.PixelHeight));
        return true;
    });

    [Theory]
    [InlineData(ImageExportFormat.Png, false)] [InlineData(ImageExportFormat.Png, true)]
    [InlineData(ImageExportFormat.Jpeg, false)] [InlineData(ImageExportFormat.Jpeg, true)]
    public async Task ExplicitFormatsRespectUrlChoiceAndRemainDecodable(ImageExportFormat format, bool include) => await StaThreadTask.RunAsync(() =>
    {
        BitmapSource source = Image(101, 51);
        ImageExportResult result = ImageExportEncoder.Encode(source, ImageCodec.EncodePng(source, Url),
            new() { Format = format, IncludeSourceUrl = include, ReductionPercent = 50 }, Colors.Blue, Url);
        Assert.Equal(format == ImageExportFormat.Png ? ".png" : ".jpg", result.Extension);
        Assert.Equal((101, 51), Size(result.Bytes));
        Assert.Equal(include, Encoding.UTF8.GetString(result.Bytes).Contains(Url, StringComparison.Ordinal));
        return true;
    });

    [Fact]
    public async Task TransparentJpegFlattensOnWhiteAndPngKeepsChosenBackground() => await StaThreadTask.RunAsync(() =>
    {
        BitmapSource transparent = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, new byte[256], 32);
        transparent.Freeze();
        ImageExportResult jpeg = ImageExportEncoder.Encode(transparent, ImageCodec.EncodePng(transparent),
            new() { Format = ImageExportFormat.Jpeg }, Colors.Red, null);
        using var stream = new MemoryStream(jpeg.Bytes);
        var decoded = new FormatConvertedBitmap(BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[256]; decoded.CopyPixels(pixels, 32, 0);
        Assert.All(pixels, value => Assert.Equal(255, value));
        BitmapSource styled = ImageExportRenderer.Render(transparent, new() { Margin = 4, Background = ImageExportBackground.Accent }, Colors.Red);
        byte[] corner = new byte[4]; styled.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), corner, 4, 0);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, corner);
        return true;
    });

    [Fact]
    public async Task AutomaticReductionStillFindsJpegAndCancellationStopsExports() => await StaThreadTask.RunAsync(() =>
    {
        BitmapSource source = Image(257, 193);
        byte[] png = ImageCodec.EncodePng(source, Url);
        ImageExportResult result = ImageExportEncoder.Encode(source, png, new() { ReductionPercent = 50 }, Colors.Blue, Url);
        Assert.Equal(".jpg", result.Extension);
        Assert.True(result.TargetReached);
        Assert.DoesNotContain(Url, Encoding.UTF8.GetString(result.Bytes));
        Assert.Throws<OperationCanceledException>(() => ImageExportEncoder.Encode(source, png, new ImageExportOptions(), Colors.Blue, Url, new CancellationToken(true)));
        return true;
    });

    private static BitmapSource Image(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4]; new Random(97).NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze(); return image;
    }

    private static (int, int) Size(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        BitmapSource image = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        return (image.PixelWidth, image.PixelHeight);
    }

    private static byte[] Chunk(string type, string value)
    {
        byte[] text = Encoding.Latin1.GetBytes(value);
        byte[] chunk = new byte[text.Length + 12];
        BinaryPrimitives.WriteInt32BigEndian(chunk, text.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4); text.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), Crc(chunk.AsSpan(4, text.Length + 4)));
        return chunk;
    }

    private static void AssertCrcs(byte[] png)
    {
        for (int offset = 8; offset < png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            Assert.Equal(BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length, 4)), Crc(png.AsSpan(offset + 4, length + 4)));
            offset += length + 12;
        }
    }
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
        }
        return ~crc;
    }
}
