using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Pinning;
using MyCapture.Core.Capture;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ClipboardImageServiceTests
{
    [Fact]
    public async Task CacheReusesOnlyImageAndUrlWhileEveryWriteGetsCurrentTextAndSourceToken()
    {
        using var cache = new ClipboardImageService.PngCache();
        BitmapSource image = Image();
        int encodes = 0;
        var pngs = new List<byte[]>();
        var tokens = new List<string>();
        for (int copy = 0; copy < 2; copy++)
        {
            string text = $"exact\t{copy}\r\n";
            CaptureProvenance source = CaptureProvenance.Create(appName: $"Source {copy}");
            int writes = 0;
            Assert.True(await ClipboardImageService.CopyImageWithTextAsync(image, "https://example.com/one", text, data =>
            {
                writes++;
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                Assert.Equal(text, data.GetData(DataFormats.UnicodeText, autoConvert: false));
                Assert.Same(image, data.GetData(DataFormats.Bitmap, autoConvert: false));
                using var bytes = new MemoryStream();
                Assert.IsAssignableFrom<Stream>(data.GetData("PNG", autoConvert: false)).CopyTo(bytes);
                pngs.Add(bytes.ToArray());
                tokens.Add(Assert.IsType<string>(data.GetData(ClipboardProvenance.Format, autoConvert: false)));
            }, source, encodePng: Encode, pngCache: cache));
            Assert.Equal(1, writes);
            Assert.Equal(source, ClipboardProvenance.Resolve(tokens[^1]));
        }
        Assert.Equal(1, encodes);
        Assert.Equal(pngs[0], pngs[1]);
        Assert.NotEqual(tokens[0], tokens[1]);
        Assert.Equal(pngs[0].Length, cache.ByteCount);

        Assert.True(await ClipboardImageService.CopyImageWithTextAsync(image, "https://example.com/two", null,
            data =>
            {
                using var bytes = new MemoryStream();
                Assert.IsAssignableFrom<Stream>(data.GetData("PNG", autoConvert: false)).CopyTo(bytes);
                Assert.Equal("https://example.com/two", PngSourceUrlMetadata.TryReadSourceUrl(bytes.ToArray()));
                Assert.False(data.GetDataPresent(DataFormats.UnicodeText, autoConvert: false));
                Assert.False(data.GetDataPresent(ClipboardProvenance.Format, autoConvert: false));
            }, encodePng: Encode, pngCache: cache));
        Assert.Equal(2, encodes);
        Assert.True(await ClipboardImageService.CopyImageWithTextAsync(Image(), "https://example.com/two", null,
            _ => { }, encodePng: Encode, pngCache: cache));
        Assert.Equal(3, encodes);

        byte[] Encode(BitmapSource bitmap, string? url)
        {
            encodes++;
            return ImageCodec.EncodePng(bitmap, url);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearOrCloseDuringEncodingDoesNotRetainTheCompletedPayload(bool dispose)
    {
        using var cache = new ClipboardImageService.PngCache();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int writes = 0;
        Task<bool> copy = ClipboardImageService.CopyImageWithTextAsync(Image(), null, "source", _ => writes++,
            encodePng: (image, url) =>
            {
                started.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return ImageCodec.EncodePng(image, url);
            }, pngCache: cache);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (dispose) cache.Dispose();
            else cache.Clear();
            Assert.Equal(0, cache.ByteCount);
        }
        finally { release.Set(); }
        Assert.True(await copy.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, writes);
        Assert.Equal(0, cache.ByteCount);
    }

    [Fact]
    public async Task FailedEncodingCanRetryAndCacheDoesNotChangeCopyInvocationOrder()
    {
        using var cache = new ClipboardImageService.PngCache();
        BitmapSource image = Image();
        Assert.False(await ClipboardImageService.CopyImageWithTextAsync(image, null, null,
            _ => throw new InvalidOperationException("No write expected"),
            encodePng: (_, _) => throw new IOException("Encode failed"), pngCache: cache));
        Assert.Equal(0, cache.ByteCount);

        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<string>();
        Task<bool> first = ClipboardImageService.CopyImageWithTextAsync(image, null, "first",
            _ => writes.Add("first"), encodePng: (bitmap, url) =>
            {
                started.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return ImageCodec.EncodePng(bitmap, url);
            }, pngCache: cache);
        Task<bool>? selected = null;
        Task<bool>? repeated = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            selected = ClipboardImageService.RunCopySerializedAsync(() =>
            {
                writes.Add("selected");
                return Task.FromResult(true);
            });
            repeated = ClipboardImageService.CopyImageWithTextAsync(image, null, "repeat", _ => writes.Add("repeat"),
                encodePng: (_, _) => throw new InvalidOperationException("Cache hit must skip encoding"), pngCache: cache);
            Assert.Empty(writes);
        }
        finally { release.Set(); }
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await selected!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await repeated!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["first", "selected", "repeat"], writes);
    }

    [Fact]
    public void ManagerForwardsThePinsOwnCacheAndClosingReleasesIt() => StaTestHost.Run(() =>
    {
        ClipboardImageService.PngCache? forwarded = null;
        var manager = new PinManager(() => new PinSettings(), null,
            (image, url, text, source, cache) =>
            {
                forwarded = cache;
                cache!.GetOrEncode(image, url, ImageCodec.EncodePng);
                return Task.FromResult(true);
            }, _ => Task.FromResult(true), NullLogger.Instance);
        PinWindow pin = manager.PinImage(Image());
        try
        {
            var copyItem = pin.ContextMenu!.Items.OfType<System.Windows.Controls.MenuItem>()
                .Single(item => item.InputGestureText == "Ctrl+C");
            copyItem.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            Assert.Same(pin.ClipboardCache, forwarded);
            Assert.True(pin.ClipboardCache.ByteCount > 0);
            pin.Close();
            Assert.Equal(0, pin.ClipboardCache.ByteCount);
        }
        finally { manager.CloseAllForExit(); }
    });

    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(32, 24, 96, 96, PixelFormats.Bgra32, null, new byte[32 * 24 * 4], 32 * 4);
        image.Freeze();
        return image;
    }

    [Fact]
    public void RerenderingThePinDropsTheOldPng() => StaTestHost.Run(() =>
    {
        var pin = new PinWindow(PinContent.FromText(Image(), "rendered text", false),
            new PinViewState(32, 24, 1, 1, 0.1), -10000, -10000, () => new PinSettings());
        try
        {
            BitmapSource original = pin.PinnedContent.Image;
            pin.ClipboardCache.GetOrEncode(original, null, ImageCodec.EncodePng);
            Assert.True(pin.ClipboardCache.ByteCount > 0);
            pin.RerenderForDisplayScale(2);
            Assert.NotSame(original, pin.PinnedContent.Image);
            Assert.Equal(0, pin.ClipboardCache.ByteCount);
        }
        finally { pin.Close(); }
    });
}
