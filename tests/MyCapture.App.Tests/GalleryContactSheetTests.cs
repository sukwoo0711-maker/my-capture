using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Gallery;
using MyCapture.Core.Export;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryContactSheetTests
{
    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void DialogLoadsLocalizedChoicesAndSafeDefaults(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        var dialog = new GalleryContactSheetDialog([], 2, () => true);
        try
        {
            var controls = Descendants(dialog).ToArray();
            ComboBox[] choices = controls.OfType<ComboBox>().ToArray();
            Assert.Equal(3, choices.Length);
            Assert.Equal(new[] { 3, 5, 4 }, choices.Select(choice => choice.Items.Count));
            Assert.Equal(new[] { 0, 2, 0 }, choices.Select(choice => choice.SelectedIndex));
            Assert.NotEqual(true, Assert.Single(controls.OfType<CheckBox>()).IsChecked);
            Assert.Equal(UiText.Get("ContactSheet.Action"), dialog.Title);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public void KoreanCaptionsProduceVisiblePixelsAndJpegPdf() => StaTestHost.Run(() =>
    {
        byte[] pixels = Enumerable.Repeat((byte)255, 40 * 20 * 3).ToArray();
        var source = BitmapSource.Create(40, 20, 96, 96, PixelFormats.Rgb24, null, pixels, 40 * 3);
        ContactSheetRect cell = new ContactSheetLayout(new()).Cell(0);
        BitmapSource rendered = GalleryContactSheetRenderer.ComposeCell(source, cell,
            ["한글 제목 및 촬영 시각", "2026년 10월 14일"], ContactSheetLayout.ExportDpi, CultureInfo.GetCultureInfo("ko-KR"));
        Assert.Equal((int)Math.Ceiling(cell.Width * 200 / 72), rendered.PixelWidth);
        Assert.Equal((int)Math.Ceiling(cell.Height * 200 / 72), rendered.PixelHeight);
        int stride = rendered.PixelWidth * 4;
        byte[] raster = new byte[stride * rendered.PixelHeight];
        rendered.CopyPixels(raster, stride, 0);
        int captionStart = (int)((cell.Height - 30) * 200 / 72) * stride;
        Assert.Contains(raster[captionStart..], value => value < 128);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(rendered, PixelFormats.Rgb24, null, 0)));
        using var image = new MemoryStream();
        encoder.Save(image);
        image.Position = 0;
        var decoded = BitmapDecoder.Create(image, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.Equal(rendered.PixelWidth, decoded.Frames[0].PixelWidth);
        using var pdf = new MemoryStream();
        ContactSheetPdfWriter.Write(pdf, new(), 1, _ => new(image.ToArray(), rendered.PixelWidth, rendered.PixelHeight));
        Assert.Contains("/ColorSpace /DeviceRGB", Encoding.ASCII.GetString(pdf.ToArray()));
        if (Environment.GetEnvironmentVariable("MYCAPTURE_CONTACT_SHEET_EVIDENCE") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            using var evidence = new FileStream(Path.Combine(directory, "contact-sheet-korean.pdf"), FileMode.CreateNew);
            pdf.Position = 0;
            pdf.CopyTo(evidence);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(rendered));
            using var preview = new FileStream(Path.Combine(directory, "contact-sheet-korean-cell.png"), FileMode.CreateNew);
            png.Save(preview);
        }
    });

    [Fact]
    public void DenseLandscapeLayoutKeepsAllCaptionLinesInsideCell() => StaTestHost.Run(() =>
    {
        var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Rgb24, null, new byte[] { 255, 255, 255 }, 3);
        ContactSheetRect cell = new ContactSheetLayout(new(ContactSheetPageSize.A4Landscape, ContactSheetGrid.ThreeByFour)).Cell(0);
        BitmapSource rendered = GalleryContactSheetRenderer.ComposeCell(source, cell,
            [new string('가', 200), "2026-10-14 09:30", "출처 앱", "창 제목"], 200, CultureInfo.GetCultureInfo("ko-KR"));
        Assert.True(rendered.IsFrozen);
        Assert.InRange(rendered.PixelHeight, 1, 500);
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not DependencyObject element) continue;
            yield return element;
            foreach (DependencyObject descendant in Descendants(element)) yield return descendant;
        }
    }
}
