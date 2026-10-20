using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.App.Threading;
using MyCapture.Core.Pin;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class StoreScreenshotPinLayerFixtureTests
{
    [Fact]
    public async Task CodeEditorFixture_PreservesOriginalTextAndUsesTheCodeRenderer()
    {
        const string original = "#if defined(BOARD_REV_B)\r\n"
                                + "#define PERIPH_CLK_HZ  84000000u\r\n"
                                + "#else\r\n"
                                + "#define PERIPH_CLK_HZ  42000000u\r\n"
                                + "#endif";
        ClipboardImageReader.CapturedAttempt captured = await StaThreadTask.RunAsync(() =>
        {
            DataObject data = StoreScreenshotSelfTest.CreateCodePinClipboardFixture();
            Assert.Equal(original, Assert.IsType<string>(data.GetData(DataFormats.UnicodeText)));
            string fragment = AssertCfHtmlByteOffsets(data);
            Assert.Contains("white-space: pre;", fragment);
            Assert.Contains("font-family: Consolas, 'Courier New', monospace;", fragment);
            return ClipboardImageReader.CaptureFrom(data, clipboardSequence: 41, () => null);
        }, "store code fixture reader");

        Assert.False(captured.IsTabularText);
        Assert.NotNull(captured.CodePreview);
        ClipboardImageReader.PinReadAttempt completed = await CompleteFixtureAsync(captured);

        PinContent content = Assert.IsType<PinContent>(completed.Content);
        Assert.Equal(ClipboardImageStatus.Success, completed.Outcome.Status);
        Assert.Equal(PinContentKind.Text, content.Kind);
        Assert.Equal(original, content.OriginalText);
        Assert.True(content.UsesDeviceIndependentPixels);
        Assert.True(content.Image.IsFrozen);
        Assert.Same(captured.CodePreview, content.Image);
        Assert.Equal(new byte[] { 0x1E, 0x1E, 0x1E, 0xFF }, CopyAsBgra32(content.Image)[..4]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpreadsheetFixture_PreservesExactCellTextAndPublishedPng(bool english)
    {
        string original = english
            ? "Register\tOffset\tReset\tDescription\r\n"
              + "UART_SR\t0x00\t0x00C0\tStatus\r\n"
              + "UART_DR\t0x04\t0x0000\tData\r\n"
              + "UART_BRR\t0x08\t0x0000\tBaud divisor\r\n"
              + "UART_CR1\t0x0C\t0x0000\tControl 1\r\n"
              + "UART_CR2\t0x10\t0x0000\tControl 2\r\n"
            : "레지스터\t오프셋\t리셋 값\t설명\r\n"
              + "UART_SR\t0x00\t0x00C0\t상태\r\n"
              + "UART_DR\t0x04\t0x0000\t데이터\r\n"
              + "UART_BRR\t0x08\t0x0000\t보율 분주\r\n"
              + "UART_CR1\t0x0C\t0x0000\t제어 1\r\n"
              + "UART_CR2\t0x10\t0x0000\t제어 2\r\n";
        var fixture = await StaThreadTask.RunAsync(() =>
        {
            DataObject data = StoreScreenshotSelfTest.CreateTablePinClipboardFixture(english);
            Assert.Equal(original, Assert.IsType<string>(data.GetData(DataFormats.UnicodeText)));
            string fragment = AssertCfHtmlByteOffsets(data);
            Assert.StartsWith("<table", fragment);
            Assert.Equal(6, Regex.Matches(fragment, "<tr(?:\\s|>)").Count);
            Assert.Contains(english ? "Baud divisor" : "보율 분주", fragment);
            using MemoryStream png = Assert.IsType<MemoryStream>(data.GetData("PNG", autoConvert: false));
            BitmapSource picture = Assert.IsAssignableFrom<BitmapSource>(
                ClipboardImageReader.TryDecodePngPayload(png.ToArray()));
            return (Captured: ClipboardImageReader.CaptureFrom(data, clipboardSequence: 42, () => null), Picture: picture);
        }, "store table fixture reader");

        Assert.True(fixture.Captured.IsTabularText);
        Assert.NotNull(fixture.Captured.PngBytes);
        Assert.Null(fixture.Captured.CodePreview);
        ClipboardImageReader.PinReadAttempt completed = await CompleteFixtureAsync(fixture.Captured);

        PinContent content = Assert.IsType<PinContent>(completed.Content);
        Assert.Equal(ClipboardImageStatus.Success, completed.Outcome.Status);
        Assert.Equal(PinContentKind.Table, content.Kind);
        Assert.Equal(original, content.OriginalText);
        Assert.False(content.UsesDeviceIndependentPixels);
        Assert.True(content.Image.IsFrozen);
        Assert.Equal(fixture.Picture.PixelWidth, content.Image.PixelWidth);
        Assert.Equal(fixture.Picture.PixelHeight, content.Image.PixelHeight);
        Assert.Equal(CopyAsBgra32(fixture.Picture), CopyAsBgra32(content.Image));
    }

    private static async Task<ClipboardImageReader.PinReadAttempt> CompleteFixtureAsync(
        ClipboardImageReader.CapturedAttempt captured)
    {
        bool renderedPlainText = false;
        bool requestedFallback = false;
        ClipboardImageReader.PinReadAttempt completed = await ClipboardImageReader.CompletePinAttemptAsync(
            captured,
            ClipboardImageReader.TryDecodePngPayload,
            (text, isTabular) =>
            {
                renderedPlainText = true;
                return StaThreadTask.RunAsync(
                    () => ClipboardTextRenderer.Render(text, isTabular),
                    "store fixture text renderer");
            },
            _ =>
            {
                requestedFallback = true;
                return Task.FromResult(ClipboardImageReader.CapturedAttempt.NoImage());
            });
        Assert.False(renderedPlainText);
        Assert.False(requestedFallback);
        return completed;
    }

    private static string AssertCfHtmlByteOffsets(DataObject data)
    {
        string html = Assert.IsType<string>(data.GetData(DataFormats.Html, autoConvert: false));
        Assert.StartsWith("Version:0.9\r\n", html);
        int Offset(string name)
        {
            Match match = Regex.Match(html, "^" + name + @":(\d+)\r?$", RegexOptions.Multiline);
            Assert.True(match.Success, "Missing CF_HTML offset: " + name);
            return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        const string startMarker = "<!--StartFragment-->";
        const string endMarker = "<!--EndFragment-->";
        int htmlIndex = html.IndexOf("<html>", StringComparison.Ordinal);
        int fragmentIndex = html.IndexOf(startMarker, StringComparison.Ordinal);
        int fragmentEndIndex = html.IndexOf(endMarker, StringComparison.Ordinal);
        Assert.True(htmlIndex >= 0 && fragmentIndex >= htmlIndex && fragmentEndIndex > fragmentIndex);
        fragmentIndex += startMarker.Length;

        byte[] bytes = Encoding.UTF8.GetBytes(html);
        Assert.Equal(Encoding.UTF8.GetByteCount(html[..htmlIndex]), Offset("StartHTML"));
        Assert.Equal(bytes.Length, Offset("EndHTML"));
        Assert.Equal(Encoding.UTF8.GetByteCount(html[..fragmentIndex]), Offset("StartFragment"));
        Assert.Equal(Encoding.UTF8.GetByteCount(html[..fragmentEndIndex]), Offset("EndFragment"));
        string fragment = Encoding.UTF8.GetString(bytes, Offset("StartFragment"), Offset("EndFragment") - Offset("StartFragment"));
        Assert.Equal(html[fragmentIndex..fragmentEndIndex], fragment);
        return fragment;
    }

    private static byte[] CopyAsBgra32(BitmapSource source)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
