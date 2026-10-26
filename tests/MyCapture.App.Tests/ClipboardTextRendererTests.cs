using System.Runtime.ExceptionServices;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// WPF text rasterisation is exercised on a dedicated STA thread, matching the application's
/// clipboard dispatcher regardless of the test runner's apartment state.
/// </summary>
public sealed class ClipboardTextRendererTests
{
    [Fact]
    public void PlainText_ReturnsFrozenNonEmptyBitmap()
    {
        BitmapSource rendered = RenderOnSta(
            "A short clipboard note.\r\nThe second line remains readable.",
            isTabularContent: false);

        Assert.True(rendered.IsFrozen);
        Assert.True(rendered.PixelWidth > 0);
        Assert.True(rendered.PixelHeight > 0);
        Assert.Contains(CopyPixels(rendered), static channel => channel != 0);
    }

    [Fact]
    public void Table_PreservesEmptyCellsInGridSizing()
    {
        BitmapSource twoColumns = RenderOnSta("A\tC", isTabularContent: true);
        BitmapSource emptyMiddleColumn = RenderOnSta("A\t\tC", isTabularContent: true);
        BitmapSource trailingEmptyRow = RenderOnSta("A\tC\r\n", isTabularContent: true);

        Assert.True(emptyMiddleColumn.PixelWidth > twoColumns.PixelWidth);
        Assert.True(trailingEmptyRow.PixelHeight > twoColumns.PixelHeight);
        Assert.True(emptyMiddleColumn.IsFrozen);
        Assert.True(trailingEmptyRow.IsFrozen);
    }

    [Fact]
    public void UnicodeText_RendersDeterministically()
    {
        const string source = "한글 · 日本語 · café · 😀";

        BitmapSource first = RenderOnSta(source, isTabularContent: false);
        BitmapSource second = RenderOnSta(source, isTabularContent: false);
        BitmapSource empty = RenderOnSta(string.Empty, isTabularContent: false);

        Assert.Equal(first.PixelWidth, second.PixelWidth);
        Assert.Equal(first.PixelHeight, second.PixelHeight);
        Assert.True(CopyPixels(first).SequenceEqual(CopyPixels(second)));
        Assert.False(CopyPixels(empty).SequenceEqual(CopyPixels(first)));
    }

    [Fact]
    public void HugePlainAndTabularContent_StayWithinPreviewBounds()
    {
        string hugePlain = string.Concat(Enumerable.Repeat("아주 긴 클립보드 문자열 😀 ", 40_000));
        string wideRow = string.Join(
            '\t',
            Enumerable.Repeat(new string('값', 300), 64));
        string hugeTable = string.Join(
            "\r\n",
            Enumerable.Repeat(wideRow, 128));

        BitmapSource plain = RenderOnSta(hugePlain, isTabularContent: false);
        BitmapSource table = RenderOnSta(hugeTable, isTabularContent: true);

        Assert.InRange(plain.PixelWidth, 1, 1200);
        Assert.InRange(plain.PixelHeight, 1, 900);
        Assert.InRange(table.PixelWidth, 1, 1200);
        Assert.InRange(table.PixelHeight, 1, 900);
        Assert.True(plain.IsFrozen);
        Assert.True(table.IsFrozen);
    }

    private static BitmapSource RenderOnSta(string sourceText, bool isTabularContent) =>
        RunSta(() => ClipboardTextRenderer.Render(sourceText, isTabularContent));

    [Fact]
    public void LongTranslationIncludesLastLineBeyondTheOrdinaryPreviewHeight() => RunSta(() =>
    {
        string prefix = string.Concat(Enumerable.Repeat("번역된 문장을 표시합니다.\n", 60));
        BitmapSource first = ClipboardTextRenderer.RenderTranslation(prefix + "마지막 문장 END-015");
        BitmapSource second = ClipboardTextRenderer.RenderTranslation(prefix + "다른 마지막 문장 END-999");
        Assert.True(first.IsFrozen);
        Assert.InRange(first.Height, 1400, 8192);
        Assert.Equal(first.PixelHeight, second.PixelHeight);
        byte[] firstPixels = CopyPixels(first), secondPixels = CopyPixels(second);
        int lastRows = (int)(first.PixelWidth * 4 * 50);
        Assert.False(firstPixels.AsSpan(firstPixels.Length - lastRows)
            .SequenceEqual(secondPixels.AsSpan(secondPixels.Length - lastRows)));
        return true;
    });

    [Fact]
    public void LongTranslationAtHighDpiCapsPixelsWithoutDroppingLogicalLines() => RunSta(() =>
    {
        string source = string.Concat(Enumerable.Repeat(new string('문', 80) + "\n", 150));
        BitmapSource image = ClipboardTextRenderer.RenderTranslation(source, 8);
        Assert.True(image.IsFrozen);
        Assert.True((long)image.PixelWidth * image.PixelHeight <= 16_000_000);
        Assert.InRange(image.Height, 3500, 8193);
        return true;
    });

    private static T RunSta<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        T? result = default;
        Exception? failure = null;
        var thread = WpfTestThread.Create(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result!;
    }

    private static byte[] CopyPixels(BitmapSource source)
    {
        int bytesPerPixel = (source.Format.BitsPerPixel + 7) / 8;
        int stride = source.PixelWidth * bytesPerPixel;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
