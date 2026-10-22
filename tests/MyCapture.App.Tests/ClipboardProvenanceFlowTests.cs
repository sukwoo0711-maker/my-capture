using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.App.Pinning;
using MyCapture.Core.Capture;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ClipboardProvenanceFlowTests
{
    [Fact]
    public async Task ImageCopyCarriesOnlyOpaqueReferenceAndKeepsPngEncodingUnchanged()
    {
        BitmapSource image = Image();
        CaptureProvenance source = CaptureProvenance.Create(documentPath: @"C:\Private\report.xlsx", appName: "Excel");
        string? token = null;
        byte[]? png = null;
        Assert.True(await ClipboardImageService.CopyImageWithProvenanceAsync(image, null, source, data =>
        {
            token = Assert.IsType<string>(data.GetData(ClipboardProvenance.Format, autoConvert: false));
            using var bytes = new MemoryStream();
            Assert.IsAssignableFrom<Stream>(data.GetData("PNG", autoConvert: false)).CopyTo(bytes);
            png = bytes.ToArray();
            Assert.DoesNotContain(source.DocumentPath, token);
        }));
        Assert.Equal(ImageCodec.EncodePng(image, null), png);
        Assert.Equal(source, ClipboardProvenance.Resolve(token));
        Assert.Empty(PngSourceUrlMetadata.TryReadSourceUrl(png!));
    }

    [Fact]
    public async Task FailedCopyKeepsThePreviousClipboardSourceReference()
    {
        CaptureProvenance first = CaptureProvenance.Create(url: "https://example.com/first");
        string token = ClipboardProvenance.Remember(first);
        Assert.False(await ClipboardImageService.CopyImageWithProvenanceAsync(Image(), null,
            CaptureProvenance.Create(documentPath: @"C:\Private\other.xlsx"),
            _ => throw new COMException("clipboard busy", unchecked((int)0x800401D0))));
        Assert.Equal(first, ClipboardProvenance.Resolve(token));
    }

    [Fact]
    public async Task CaptureWithoutImmediateSourcePublishesTokenForTheLateSource()
    {
        var late = new TaskCompletionSource<CaptureProvenance>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? token = null;
        Assert.True(await ClipboardImageService.CopyImageWithProvenanceAsync(Image(), null, CaptureProvenance.None,
            data => token = Assert.IsType<string>(data.GetData(ClipboardProvenance.Format, autoConvert: false)), late.Task));
        CaptureProvenance source = CaptureProvenance.Create(documentPath: @"C:\Specs\report.xlsx", appName: "Excel");
        late.SetResult(source);
        await Task.Run(() => Assert.True(SpinWait.SpinUntil(() => ClipboardProvenance.Resolve(token) == source, TimeSpan.FromSeconds(5))));
    }

    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        image.Freeze();
        return image;
    }
}
