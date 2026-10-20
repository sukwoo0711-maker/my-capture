using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.App.Threading;
using MyCapture.Core.Pin;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ClipboardRendererDpiTests
{
    private const string PlainText = "A retained clipboard note.\r\n한글 · 日本語 · café · 😀\r\n"
        + "A longer line whose exact width is kept when a pin crosses monitors.";
    private const string TableText = "이름\t값\t메모\r\nalpha\t123\t\r\nbeta\t\tcafé 😀";
    private const string CodeText = "#define MAX_LEN 256\r\nif (ready) {\r\n\treturn value;\r\n}";
    private const string CodeHtml = "<pre style=\"background-color:#123456;color:#DCDCDC\">"
        + "<span style=\"color:#FF0000\">#define</span> MAX_LEN 256\r\n"
        + "<span style=\"color:#569CD6\">if</span> (ready) {\r\n\t"
        + "<span style=\"color:#C586C0\">return</span> value;\r\n}</pre>";

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    public async Task PlainGridAndCode_KeepCanonicalLayoutWithNativePixelDensity(double scale)
    {
        await StaThreadTask.RunAsync(() =>
        {
            PinCodeDocument code = Assert.IsType<PinCodeDocument>(ClipboardCodeRenderer.TryParse(CodeText, CodeHtml));
            BitmapSource[] baseline =
            [
                ClipboardTextRenderer.Render(PlainText, false),
                ClipboardTextRenderer.Render(TableText, true),
                ClipboardCodeRenderer.Render(code),
            ];
            BitmapSource[] rendered =
            [
                ClipboardTextRenderer.Render(PlainText, false, scale),
                ClipboardTextRenderer.Render(TableText, true, scale),
                ClipboardCodeRenderer.Render(code, scale),
            ];
            for (int index = 0; index < rendered.Length; index++)
            {
                Assert.Equal((int)Math.Ceiling(baseline[index].Width * scale), rendered[index].PixelWidth);
                Assert.Equal((int)Math.Ceiling(baseline[index].Height * scale), rendered[index].PixelHeight);
                Assert.Equal(96 * scale, rendered[index].DpiX);
                Assert.Equal(96 * scale, rendered[index].DpiY);
                // Integer physical pixels cannot represent every fractional DIP boundary.
                Assert.InRange(rendered[index].Width - baseline[index].Width, -0.000001, (1 / scale) + 0.000001);
                Assert.InRange(rendered[index].Height - baseline[index].Height, -0.000001, (1 / scale) + 0.000001);
                Assert.True(rendered[index].IsFrozen);
            }
            return true;
        }, "clipboard render DPI test");
    }

    [Fact]
    public async Task LegacyOverloads_EqualExplicitScaleOnePixelForPixel()
    {
        await StaThreadTask.RunAsync(() =>
        {
            AssertPixelsEqual(ClipboardTextRenderer.Render(PlainText, false),
                ClipboardTextRenderer.Render(PlainText, false, 1));
            AssertPixelsEqual(ClipboardTextRenderer.Render(TableText, true),
                ClipboardTextRenderer.Render(TableText, true, 1));
            BitmapSource legacyCode = Assert.IsAssignableFrom<BitmapSource>(ClipboardCodeRenderer.TryRender(CodeText, CodeHtml));
            AssertPixelsEqual(legacyCode,
                Assert.IsAssignableFrom<BitmapSource>(ClipboardCodeRenderer.TryRender(CodeText, CodeHtml, 1)));
            AssertPixelsEqual(legacyCode,
                ClipboardCodeRenderer.Render(Assert.IsType<PinCodeDocument>(ClipboardCodeRenderer.TryParse(CodeText, CodeHtml))));
            return true;
        }, "clipboard legacy render test");
    }

    [Fact]
    public void ParsedCode_TrimsTrailingRunsAndRetainsDetachedImmutableColors()
    {
        PinCodeDocument code = Assert.IsType<PinCodeDocument>(ClipboardCodeRenderer.TryParse("hello",
            "<pre style=\"background-color:#123456\"><span style=\"color:#ff0000\">hello\r\n</span></pre>"));
        Assert.Equal("hello", code.DisplayText);
        Assert.Equal("#123456", code.Background);
        Assert.Equal("#DCDCDC", code.Foreground);
        Assert.Equal(new PinCodeRun(0, 5, "#FF0000"), Assert.Single(code.Runs));
        Assert.True(code.IsValid());
        IList<PinCodeRun> runs = Assert.IsAssignableFrom<IList<PinCodeRun>>(code.Runs);
        Assert.True(runs.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => runs[0] = new PinCodeRun(0, 1, "#000000"));
    }

    [Fact]
    public void ParsedCode_KeepsMarkupAndOriginalTextSizeLimits()
    {
        Assert.Null(ClipboardCodeRenderer.TryParse("hello",
            "<pre>hello" + new string(' ', 65_536) + "</pre>"));
        Assert.Null(ClipboardCodeRenderer.TryParse("hello" + new string(' ', 16_384),
            "<pre>hello</pre>"));
    }

    [Fact]
    public async Task CodeRasterization_PreservesBackgroundAndTokenColorsAtEveryScale()
    {
        await StaThreadTask.RunAsync(() =>
        {
            PinCodeDocument code = Assert.IsType<PinCodeDocument>(ClipboardCodeRenderer.TryParse(CodeText, CodeHtml));
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
            {
                BitmapSource image = ClipboardCodeRenderer.Render(code, scale);
                byte[] pixels = CopyPixels(image);
                Assert.Equal(new byte[] { 0x56, 0x34, 0x12, 0xFF }, pixels[..4]);
                Assert.Contains(Enumerable.Range(0, pixels.Length / 4),
                    index => pixels[(index * 4) + 2] > 200 && pixels[(index * 4) + 1] < 40);
            }
            return true;
        }, "clipboard code colours DPI test");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(9)]
    public void InvalidScale_IsRejectedBeforeAllocating(double scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipboardTextRenderer.Render("text", false, scale));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipboardCodeRenderer.TryRender("hello", "<pre>hello</pre>", scale));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundedPreview_KeepsWrappedLayoutAtEveryScale(bool table)
    {
        string text = table
            ? string.Join("\r\n", Enumerable.Repeat(string.Join('\t', Enumerable.Repeat(new string('값', 200), 14)), 30))
            : string.Concat(Enumerable.Repeat("long wrapping text with 한글 😀 ", 800));
        await StaThreadTask.RunAsync(() =>
        {
            BitmapSource baseline = ClipboardTextRenderer.Render(text, table);
            foreach (double scale in new[] { 1.25, 1.5, 1.75, 2.0 })
            {
                BitmapSource rendered = ClipboardTextRenderer.Render(text, table, scale);
                Assert.Equal((int)Math.Ceiling(baseline.PixelWidth * scale), rendered.PixelWidth);
                Assert.Equal((int)Math.Ceiling(baseline.PixelHeight * scale), rendered.PixelHeight);
            }
            return true;
        }, "clipboard bounded layout DPI test");
    }

    private static void AssertPixelsEqual(BitmapSource expected, BitmapSource actual)
    {
        Assert.Equal(expected.PixelWidth, actual.PixelWidth);
        Assert.Equal(expected.PixelHeight, actual.PixelHeight);
        Assert.Equal(expected.DpiX, actual.DpiX);
        Assert.Equal(expected.DpiY, actual.DpiY);
        Assert.Equal(CopyPixels(expected), CopyPixels(actual));
    }

    private static byte[] CopyPixels(BitmapSource image)
    {
        int stride = image.PixelWidth * ((image.Format.BitsPerPixel + 7) / 8);
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
