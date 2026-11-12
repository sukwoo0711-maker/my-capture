using System.Windows;
using System.Windows.Media;
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

    /// <summary>
    /// Every text picture fills its rectangle to the corners and to its last device pixel, so the
    /// pin's one-DIP outline is the only edge it shows. A plain card with its own rounded border had
    /// transparent corners: the desktop showed through them inside a square Windows 10 pin, a second
    /// arc ran inside a Windows 11 pin's outline, and they were black wherever a copied bitmap's
    /// alpha is dropped. At fractional scales every text picture also left its last column or row
    /// half transparent, because its background stopped at the DIP size the pixels round up from.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public async Task TextPictures_FillTheirCornersAndEdges(double scale)
    {
        await StaThreadTask.RunAsync(() =>
        {
            PinCodeDocument code = Assert.IsType<PinCodeDocument>(ClipboardCodeRenderer.TryParse(CodeText, CodeHtml));
            PinCodeDocument Code(string text) => new(text, [], "#123456", "#DCDCDC");
            List<(string Kind, string Name, BitmapSource Image, uint Fill)> pictures =
            [
                ("plain", "plain", ClipboardTextRenderer.Render(PlainText, false, scale), 0xFFFCFCFD),
                ("plain", "short note", ClipboardTextRenderer.Render("sample", false, scale), 0xFFFCFCFD),
                ("plain", "plain with a tab", ClipboardTextRenderer.Render("name\tvalue", false, scale), 0xFFFCFCFD),
                ("table", "table", ClipboardTextRenderer.Render(TableText, true, scale), 0xFFFCFCFD),
                ("table", "two by two", ClipboardTextRenderer.Render("a\tb\r\nc\td", true, scale), 0xFFFCFCFD),
                ("table", "one row", ClipboardTextRenderer.Render("column\tlonger value", true, scale), 0xFFFCFCFD),
                ("translation", "translation", ClipboardTextRenderer.RenderTranslation(PlainText, scale), 0xFFFFFFFF),
                ("translation", "short translation", ClipboardTextRenderer.RenderTranslation("sample", scale), 0xFFFFFFFF),
                ("translation", "Korean translation", ClipboardTextRenderer.RenderTranslation("번역 결과를 확인합니다.", scale), 0xFFFFFFFF),
                ("code", "code", ClipboardCodeRenderer.Render(code, scale), 0xFF123456),
                ("code", "one line", ClipboardCodeRenderer.Render(Code("return value;"), scale), 0xFF123456),
                ("code", "three lines", ClipboardCodeRenderer.Render(Code("int a = 1;\nint bb = 22;\nint ccc = 333;"), scale), 0xFF123456),
                ("markdown", "note", ClipboardMarkdownRenderer.Render("# Title\nA short note.", scale), 0xFFFFFFFF),
                ("markdown", "list", ClipboardMarkdownRenderer.Render("- one\n- two\n- three", scale), 0xFFFFFFFF),
                ("markdown", "Korean", ClipboardMarkdownRenderer.Render("## 점검\n배포 전에 이름을 확인합니다.", scale), 0xFFFFFFFF),
            ];
            // Pictures that grow a little at a time, so at a fractional scale every kind includes some
            // whose pixel size rounds up past their DIP size. Code pictures have a 240 by 80 DIP minimum,
            // so their samples are longer than that; Markdown pages have a fixed width, so they grow down.
            string[] blocks = ["\n\nA paragraph.", "\n- item", "\n## Heading", "\n> quote", "\n1. first", "\n\nAnother paragraph.", "\n---", "\n- last"];
            for (int extra = 1; extra <= blocks.Length; extra++)
            {
                string tail = "abcdefgh"[..extra];
                pictures.Add(("plain", $"sample {tail}", ClipboardTextRenderer.Render("sample " + tail, false, scale), 0xFFFCFCFD));
                pictures.Add(("table", $"table {tail}", ClipboardTextRenderer.Render("column\tvalue " + tail, true, scale), 0xFFFCFCFD));
                pictures.Add(("translation", $"translation {tail}", ClipboardTextRenderer.RenderTranslation("sample " + tail, scale), 0xFFFFFFFF));
                string codeText = "var total = items.Sum(item => item.Price); // " + tail + string.Concat(Enumerable.Repeat("\nreturn total;", extra));
                pictures.Add(("code", $"code {tail}", ClipboardCodeRenderer.Render(Code(codeText), scale), 0xFF123456));
                pictures.Add(("markdown", $"markdown {extra}", ClipboardMarkdownRenderer.Render("# Title" + string.Concat(blocks[..extra]), scale), 0xFFFFFFFF));
            }
            if (scale != Math.Floor(scale))
            {
                // Each kind has a picture whose pixel size rounds up, so its last column or row is tested.
                foreach (string kind in pictures.Select(picture => picture.Kind).Distinct())
                {
                    Assert.Contains(pictures, picture => picture.Kind == kind
                        && (IsFractional(picture.Image.PixelWidth / scale) || IsFractional(picture.Image.PixelHeight / scale)));
                }
            }
            foreach ((_, string name, BitmapSource image, uint fill) in pictures)
            {
                var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                int right = image.PixelWidth - 1, bottom = image.PixelHeight - 1;
                (int X, int Y)[] samples =
                [
                    (0, 0), (right, 0), (0, bottom), (right, bottom),
                    (image.PixelWidth / 2, 0), (image.PixelWidth / 2, bottom), (0, image.PixelHeight / 2), (right, image.PixelHeight / 2),
                ];
                foreach ((int x, int y) in samples)
                {
                    byte[] pixel = new byte[4];
                    bgra.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
                    uint actual = BitConverter.ToUInt32(pixel);
                    Assert.True(actual == fill, $"{name} at {scale}x: pixel ({x}, {y}) is #{actual:X8}, not the fill #{fill:X8}.");
                }
            }
            return true;
        }, "text picture edge test");
    }

    private static bool IsFractional(double value) => Math.Abs(value - Math.Round(value)) > 1e-6;

    private static byte[] CopyPixels(BitmapSource image)
    {
        int stride = image.PixelWidth * ((image.Format.BitsPerPixel + 7) / 8);
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
