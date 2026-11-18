using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.App.Threading;
using MyCapture.Core.Pin;
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

    // Six lines, so the picture's height is lines x line height + padding and not the 80 DIP minimum.
    // The first line is long enough that the picture is wider than its 240 DIP minimum.
    private const string Snippet = "#define MAX_LEN 256 /* maximum length of one specification row in the sheet */\n"
        + "static int count = 0;\nint main(void)\n{\n    return count;\n}";

    /// <summary>The CF_HTML VS Code publishes (as in ClipboardMarkdownDeliveryTests): one div per line.</summary>
    private static string VsCodeHtml(string source, string metrics = "font-size: 14px;line-height: 19px;") =>
        "Version:0.9\r\n<html>\r\n<body>\r\n<!--StartFragment--><div style=\"color: #bbbebf;background-color: #121314;"
        + "font-family: Consolas, 'Courier New', monospace;font-weight: normal;" + metrics + "white-space: pre;\">"
        + string.Concat(source.Split('\n').Select(static line => line.Length == 0 ? "<br>"
            : "<div><span style=\"color: #bbbebf;\">" + System.Net.WebUtility.HtmlEncode(line) + "</span></div>"))
        + "</div><!--EndFragment-->\r\n</body>\r\n</html>";

    [Fact]
    public async Task VsCodeHtml_DrawsCodeAtTheEditorsFontSizeAndLineHeight()
    {
        (BitmapSource after, BitmapSource before, PinCodeDocument document) = await StaThreadTask.RunAsync(() =>
        {
            PinCodeDocument parsed = Assert.IsType<PinCodeDocument>(ClipboardCodeRenderer.TryParse(Snippet, VsCodeHtml(Snippet)));
            PinCodeDocument defaults = parsed with { FontSizeDip = null, LineHeightDip = null, FontFamily = null };
            return (ClipboardCodeRenderer.Render(parsed), ClipboardCodeRenderer.Render(defaults), parsed);
        }, "code metrics test");

        Assert.Equal(14, document.FontSizeDip);
        Assert.Equal(19, document.LineHeightDip);
        // Consolas is the default code font: nothing to try before it.
        Assert.Null(document.FontFamily);
        Assert.Equal((6 * 19) + (2 * ClipboardCodeRenderer.VerticalPaddingDip), after.PixelHeight);
        Assert.Equal((6 * ClipboardCodeRenderer.LineHeightDip) + (2 * ClipboardCodeRenderer.VerticalPaddingDip), before.PixelHeight);
        Assert.True(after.PixelWidth < before.PixelWidth, $"14 px Consolas must be narrower than 15 px: {after.PixelWidth} vs {before.PixelWidth}.");

        PinOriginalSizeTests.SaveEvidence(before, "code-14px-19px-before.png");
        PinOriginalSizeTests.SaveEvidence(after, "code-14px-19px-after.png");
    }

    [Theory]
    [InlineData("", null, null)]                                        // no font in the HTML
    [InlineData("font-size: 6px;line-height: 8px;", null, null)]       // too small
    [InlineData("font-size: 60px;line-height: 80px;", null, null)]     // too large
    [InlineData("font-size: 1.2em;line-height: 19px;", null, null)]    // relative unit
    [InlineData("font-size: abcpx;", null, null)]
    [InlineData("font-size: 14px;line-height: 200px;", 14.0, 19.0)]    // line height out of range: follows the size
    [InlineData("font-size: 14px;line-height: 10px;", 14.0, 19.0)]     // overlapping lines: follows the size
    [InlineData("font-size: 14px;", 14.0, 19.0)]
    [InlineData("font-size: 14px;line-height: normal;", 14.0, 19.0)]
    [InlineData("font-size: 16px;line-height: 1.5;", 16.0, 24.0)]      // unitless factor
    [InlineData("font-size: 12pt !important;line-height: 20pt;", 16.0, 80.0 / 3)]
    public void FontMetrics_OutOfRangeOrMissing_UseTheDefaults(string metrics, double? size, double? line)
    {
        PinCodeDocument document = Assert.IsType<PinCodeDocument>(ClipboardCodeRenderer.TryParse(Snippet, VsCodeHtml(Snippet, metrics)));
        Assert.Equal(size, document.FontSizeDip);
        Assert.Equal(line.HasValue, document.LineHeightDip.HasValue);
        if (line is { } expectedLine) Assert.Equal(expectedLine, document.LineHeightDip!.Value, 6);
        Assert.True(document.IsValid());
        Assert.Equal(size ?? ClipboardCodeRenderer.FontSizeDip, ClipboardCodeRenderer.FontSizeFor(document));
        Assert.Equal(line is null ? ClipboardCodeRenderer.LineHeightDip : Math.Round(line.Value, 6),
            Math.Round(ClipboardCodeRenderer.LineHeightFor(document), 6));
    }

    [Fact]
    public async Task OtherEditorsHtml_KeepsTheirFontAndStillRenders()
    {
        const string code = "int x = 1;\nreturn x;";
        // JetBrains IDEs: one pre with the font in points, no line height.
        string jetBrains = "<html><body><!--StartFragment--><pre style=\"background-color:#2b2b2b;color:#a9b7c6;"
            + "font-family:'JetBrains Mono',monospace;font-size:9.8pt;\"><span style=\"color:#cc7832;\">int </span>x = "
            + "<span style=\"color:#6897bb;\">1</span>;\nreturn x;</pre><!--EndFragment--></body></html>";
        // Visual Studio style: one pre naming an unquoted family and px size, with non-hex colour names.
        string visualStudio = "<html><body><!--StartFragment--><pre style=\"font-family:Cascadia Mono;font-size:13px;"
            + "color:black;background:white;\"><span style=\"color:blue;\">int</span>&nbsp;x&nbsp;=&nbsp;1;\r\n"
            + "return&nbsp;x;</pre><!--EndFragment--></body></html>";
        // A family list that names files, URIs or generic families keeps only the plain names.
        string unsafeFamilies = "<pre style=\"font-family: url(x.ttf), 'C:\\Fonts\\#Evil', monospace, 'D2Coding', Consolas, Arial;"
            + "font-size: 15px; line-height: 22px\">int x = 1;\nreturn x;</pre>";

        PinCodeDocument jb = Assert.IsType<PinCodeDocument>(ClipboardCodeRenderer.TryParse(code, jetBrains));
        Assert.Equal(9.8 * 96 / 72, jb.FontSizeDip!.Value, 6);
        Assert.Equal(18, jb.LineHeightDip);
        Assert.Equal("JetBrains Mono", jb.FontFamily);
        Assert.Equal("#2B2B2B", jb.Background);

        PinCodeDocument vs = Assert.IsType<PinCodeDocument>(ClipboardCodeRenderer.TryParse(code, visualStudio));
        Assert.Equal(13, vs.FontSizeDip);
        Assert.Equal(18, vs.LineHeightDip);
        Assert.Equal("Cascadia Mono", vs.FontFamily);

        PinCodeDocument guarded = Assert.IsType<PinCodeDocument>(ClipboardCodeRenderer.TryParse(code, unsafeFamilies));
        Assert.Equal("D2Coding", guarded.FontFamily);
        Assert.Equal((15.0, 22.0), (guarded.FontSizeDip!.Value, guarded.LineHeightDip!.Value));

        BitmapSource[] pictures = await StaThreadTask.RunAsync(() => new[] { jb, vs, guarded }
            .Select(document => ClipboardCodeRenderer.Render(document)).ToArray(), "other editors test");
        Assert.All(pictures, picture => Assert.True(picture.PixelWidth >= 240 && picture.PixelHeight >= 80));
        Assert.Equal("D2Coding, Consolas", ClipboardCodeRenderer.FontFamilyFor(guarded).Source);
        Assert.Equal(ClipboardCodeRenderer.FontFamilyName, ClipboardCodeRenderer.FontFamilyFor(null).Source);
    }

    [Theory]
    [InlineData("<pre><script>different</script></pre>")]
    [InlineData("<!DOCTYPE pre SYSTEM 'https://example.invalid/secret'><pre>hello</pre>")]
    [InlineData("<pre>different</pre>")]
    [InlineData("<pre><span>hello</pre>")]
    public void UnsafeOrMismatchedMarkup_FallsBackToPlainText(string html) =>
        Assert.Null(ClipboardCodeRenderer.TryRender("hello", html));
}
