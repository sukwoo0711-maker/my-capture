using System.IO;
using System.Buffers.Binary;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Media;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class MediaDetailsTests
{
    [Fact]
    public async Task ReaderUsesExistingMetadataWithoutDecodingAndFallsBackToOriginal()
    {
        string directory = OwnedTestDirectory.Create("mc-media-details-");
        try
        {
            string original = Path.Combine(directory, "original.png");
            // Deliberately not a PNG: cached dimensions must not cause decoder work.
            File.WriteAllBytes(original, new byte[2516582]);
            var source = new MediaDetailsSource(Path.Combine(directory, "rendered.png"), 800, 600,
                CanvasWidth: 400, CanvasHeight: 300, FallbackFilePath: original);
            MediaDetails details = Assert.Single(await MediaDetailsReader.ReadAsync([source]));
            Assert.Equal(original, details.FilePath);
            Assert.Equal(2516582, details.SizeBytes);
            Assert.Equal("800 × 600 px", details.Resolution);
            Assert.Equal("PNG", details.Format);
            Assert.NotNull(details.CreatedAt);
            Assert.NotNull(details.ModifiedAt);
            Assert.False(details.FileUnavailable);
            Assert.Equal(400, details.Source.CanvasWidth);
        }
        finally { OwnedTestDirectory.Delete(directory); }
    }

    [Fact]
    public async Task MissingSourceKeepsKnownFactsAndDoesNotInventSize()
    {
        var source = new MediaDetailsSource(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4"), 1920, 1080, 61567);
        MediaDetails details = Assert.Single(await MediaDetailsReader.ReadAsync([source]));
        Assert.True(details.FileUnavailable);
        Assert.Null(details.SizeBytes);
        Assert.Null(details.CreatedAt);
        Assert.Equal("1920 × 1080 px", details.Resolution);
        Assert.Equal("MP4", details.Format);
        using var language = UiText.UseLanguage("en-US");
        Assert.Contains("1:01.5", MediaDetailsWindow.TooltipText(details));
    }

    [Fact]
    public async Task RenderedImageUsesActualDimensionsWhenOriginalCachedDimensionsDiffer()
    {
        string directory = OwnedTestDirectory.Create("mc-media-dimensions-");
        try
        {
            string rendered = Path.Combine(directory, "rendered.png");
            StaTestHost.Run(() =>
            {
                BitmapSource image = BitmapSource.Create(7, 3, 96, 96, PixelFormats.Bgra32, null, new byte[7 * 3 * 4], 7 * 4);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var file = File.Create(rendered);
                encoder.Save(file);
            });
            var source = new MediaDetailsSource(rendered, 800, 600, ReadImageDimensions: true);
            MediaDetails details = Assert.Single(await MediaDetailsReader.ReadAsync([source]));
            Assert.Equal("7 × 3 px", details.Resolution);
            Assert.True(details.SizeBytes > 0);
            Assert.Equal(800, details.Source.Width);
        }
        finally { OwnedTestDirectory.Delete(directory); }
    }

    [Fact]
    public async Task MissingVideoRenderUsesOriginalTrackDurationInsteadOfEditedCachedDuration()
    {
        string directory = OwnedTestDirectory.Create("mc-media-duration-");
        try
        {
            string original = Path.Combine(directory, "source.mp4");
            byte[] movieHeader = new byte[20], trackHeader = new byte[24], handler = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(movieHeader.AsSpan(12), 1000);
            BinaryPrimitives.WriteUInt32BigEndian(trackHeader.AsSpan(20), 61567);
            Encoding.ASCII.GetBytes("vide").CopyTo(handler, 8);
            File.WriteAllBytes(original, Box("moov", [.. Box("mvhd", movieHeader),
                .. Box("trak", [.. Box("tkhd", trackHeader), .. Box("mdia", Box("hdlr", handler))])]));
            var source = new MediaDetailsSource(Path.Combine(directory, "rendered.mp4"), 1920, 1080, 10000,
                FallbackFilePath: original);
            MediaDetails details = Assert.Single(await MediaDetailsReader.ReadAsync([source]));
            Assert.Equal(original, details.FilePath);
            Assert.Equal(61567, details.Source.DurationMs);
            Assert.Equal("1920 × 1080 px", details.Resolution);
        }
        finally { OwnedTestDirectory.Delete(directory); }

        static byte[] Box(string type, byte[] payload)
        {
            byte[] box = new byte[payload.Length + 8];
            BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
            payload.CopyTo(box, 8);
            return box;
        }
    }

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void DetailsSizeContainsBothHumanAndExactBytes(string language)
    {
        using var scope = UiText.UseLanguage(language);
        string text = MediaDetailsWindow.SizeText(2516582);
        Assert.Contains("2.4 MB", text);
        Assert.Contains("2,516,582", text);
        Assert.DoesNotContain("MediaInfo.", text);
    }
}
