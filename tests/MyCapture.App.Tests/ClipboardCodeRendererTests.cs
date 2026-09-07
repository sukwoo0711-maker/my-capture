using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.App.Threading;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ClipboardCodeRendererTests
{
    [Fact]
    public void DeepOrHighlyFragmentedMarkup_FallsBackWithoutRecursiveOverflow()
    {
        string nested = "<pre>" + string.Concat(Enumerable.Repeat("<span>", 2000)) + "hello"
            + string.Concat(Enumerable.Repeat("</span>", 2000)) + "</pre>";
        Assert.Null(ClipboardCodeRenderer.TryRender("hello", nested));
        string fragmented = "<pre>" + string.Concat(Enumerable.Repeat("<span>x</span>", 2500)) + "</pre>";
        Assert.Null(ClipboardCodeRenderer.TryRender(new string('x', 2500), fragmented));
    }

    [Fact]
    public async Task EditorHtml_PreservesBackgroundAndTokenColors()
    {
        BitmapSource? bitmap = await StaThreadTask.RunAsync(() => ClipboardCodeRenderer.TryRender("var x = 1;",
            "<div style=\"background-color: #123456; white-space: pre;\"><span style=\"color: #ff0000;\">var</span> x = 1;</div>"), "code test");
        Assert.NotNull(bitmap);
        Assert.True(bitmap.IsFrozen);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Assert.Equal(new byte[] { 0x56, 0x34, 0x12, 0xff }, pixels[..4]);
        Assert.Contains(Enumerable.Range(0, pixels.Length / 4), i => pixels[(i * 4) + 2] > 200 && pixels[(i * 4) + 1] < 40);
    }

    [Theory]
    [InlineData("<pre><script>different</script></pre>")]
    [InlineData("<!DOCTYPE pre SYSTEM 'https://example.invalid/secret'><pre>hello</pre>")]
    [InlineData("<pre>different</pre>")]
    [InlineData("<pre><span>hello</pre>")]
    public void UnsafeOrMismatchedMarkup_FallsBackToPlainText(string html) =>
        Assert.Null(ClipboardCodeRenderer.TryRender("hello", html));
}
