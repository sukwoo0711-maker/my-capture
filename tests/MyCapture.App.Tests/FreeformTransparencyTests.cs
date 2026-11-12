using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Pinning;
using MyCapture.Core.Annotations;
using MyCapture.Core.Capture;
using MyCapture.Core.Export;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>Downstream contracts exercised with a real mask of a synthetic frozen frame.</summary>
public sealed class FreeformTransparencyTests(ITestOutputHelper output)
{
    [Fact]
    public void PngRoundtripKeepsEveryStraightColorAndAlphaByte() => StaTestHost.Run(() =>
    {
        BitmapSource image = Image();
        byte[] expected = Pixels(image);
        Assert.Contains(expected.Where((_, index) => index % 4 == 3), alpha => alpha is > 0 and < 255);
        Assert.Equal(expected, Pixels(Assert.IsAssignableFrom<BitmapSource>(ImageCodec.TryDecodePng(ImageCodec.EncodePng(image)))));
    });

    [Fact]
    public async Task ClipboardOffersExactPngAndWhiteLegacyBitmapWithoutWritingTheClipboard()
    {
        BitmapSource image = Image();
        byte[] expected = Pixels(image);
        int writes = 0;
        Assert.True(await ClipboardImageService.CopyImageAsync(image, null, data =>
        {
            writes++;
            Assert.Contains("PNG", data.GetFormats(autoConvert: false));
            Assert.Contains(DataFormats.Bitmap, data.GetFormats(autoConvert: false));
            using var png = new MemoryStream();
            Assert.IsAssignableFrom<Stream>(data.GetData("PNG", false)).CopyTo(png);
            Assert.Equal(expected, Pixels(Assert.IsAssignableFrom<BitmapSource>(ImageCodec.TryDecodePng(png.ToArray()))));
            byte[] legacy = Pixels(Assert.IsAssignableFrom<BitmapSource>(data.GetData(DataFormats.Bitmap, false)));
            for (int i = 0; i < legacy.Length; i += 4)
            {
                Assert.Equal(255, legacy[i + 3]);
                for (int channel = 0; channel < 3; channel++)
                {
                    int matte = (expected[i + channel] * expected[i + 3] + 255 * (255 - expected[i + 3]) + 127) / 255;
                    // WPF rounds premultiplication and blending separately.
                    Assert.InRange((int)legacy[i + channel], Math.Max(0, matte - 1), Math.Min(255, matte + 1));
                }
            }
        }));
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task OpaqueClipboardCaptureRetainsItsOriginalBitmap()
    {
        BitmapSource image = FrozenPixels(32, 24);
        Assert.True(await ClipboardImageService.CopyImageAsync(image, null,
            data => Assert.Same(image, data.GetData(DataFormats.Bitmap, false))));
    }

    [Fact]
    public void CardPreviewUsesTheExactAlphaImageAndShowsItsBackground() => StaTestHost.Run(() =>
    {
        BitmapSource image = Image();
        QuickAccessWindow card = Card(image);
        try
        {
            _ = QuickAccessDetachedFixture.Layout(card);
            Assert.Same(image, card.PreviewForTest.Source);
            Image preview = card.PreviewForTest;
            Assert.IsType<Border>(preview.Parent).Child = null;
            preview.Width = 64;
            preview.Height = 48;
            BitmapSource rendered = Render(new Border { Background = Brushes.Magenta, Child = preview }, 64, 48);
            Assert.Equal(new byte[] { 255, 0, 255, 255 }, Pixel(rendered, 2, 45));
            Assert.Equal(Pixel(image, 10, 10), Pixel(rendered, 10, 10));
        }
        finally { card.Close(); }
    });

    [Fact]
    public void PinKeepsClearPixelsAndCompositesOverTheUnderlyingPattern() => StaTestHost.Run(() =>
    {
        BitmapSource image = Image();
        var pin = Pin(image);
        try
        {
            Assert.True(pin.AllowsTransparency);
            Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(pin.Background).Color);
            Assert.Equal(Pixels(image), Pixels(pin.PinnedContent.Image));
            var content = Assert.IsType<Grid>(pin.Content);
            pin.Content = null;
            Assert.Null(content.Background);
            BitmapSource rendered = Render(new Border { Background = Brushes.Magenta, Child = content }, 64, 48);
            Assert.Equal(new byte[] { 255, 0, 255, 255 }, Pixel(rendered, 6, 42));
            Assert.Equal(Pixel(image, 12, 12), Pixel(rendered, 12, 12));
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void EditorOpenAnnotationAndCopyAndClosePreserveUntouchedAlpha() => StaTestHost.Run(() =>
    {
        BitmapSource image = Image();
        var region = new RectD(0, 0, 64, 48);
        AnnotationDocument document = Document();
        var editor = new AnnotationEditorControl(new FrozenFrame(image, region, null, 0), region, image,
            initialDocument: document, initialAssets: null);
        BitmapSource opened = editor.RenderCurrentImageAsync().GetAwaiter().GetResult();
        Assert.Equal(0, Pixel(opened, 2, 45)[3]);
        Assert.Equal(new byte[] { 0, 220, 0, 255 }, Pixel(opened, 30, 12));
        bool completed = false;
        editor.EditingCompleted += (_, result) =>
        {
            Assert.Equal(EditorCommitAction.Done, result.Action);
            completed = true;
        };
        editor.CommitRequested = result =>
        {
            Assert.Equal(EditorCommitAction.Done, result.Action);
            BitmapSource rendered = CaptureCommitService.Flatten(result.SelectedBitmap, result.Document,
                result.ImageAssetBitmaps, null);
            Assert.Equal(0, Pixel(rendered, 2, 45)[3]);
            Assert.Equal(new byte[] { 0, 220, 0, 255 }, Pixel(rendered, 30, 12));
            return Task.FromResult(true);
        };
        Assert.True(editor.HandleShortcut(Key.C, ModifierKeys.Control));
        Assert.True(completed);
    });

    [Fact]
    public void ExplicitJpegUsesWhiteWhileAutomaticReductionRetainsPng() => StaTestHost.Run(() =>
    {
        BitmapSource image = Image(320, 192);
        byte[] png = ImageCodec.EncodePng(image);
        ImageExportResult automatic = ImageExportEncoder.Encode(image, png, 50);
        Assert.Equal(".png", automatic.Extension);
        Assert.True(automatic.PreservedTransparency);
        Assert.Equal(png, automatic.Bytes);
        ImageExportResult jpeg = ImageExportEncoder.Encode(image, png,
            new ImageExportOptions { Format = ImageExportFormat.Jpeg }, Colors.Black, null);
        Assert.Equal(".jpg", jpeg.Extension);
        Assert.False(jpeg.PreservedTransparency);
        byte[] corner = Pixel(Decode(jpeg.Bytes), 16, 170);
        Assert.All(corner, channel => Assert.InRange(channel, (byte)250, (byte)255));
    });

    [Fact]
    public void PdfContactSheetCompositesTransparentPixelsOntoWhite() => StaTestHost.Run(() =>
    {
        BitmapSource image = Image();
        var cell = new ContactSheetRect(0, 0, 72, 56);
        BitmapSource rendered = GalleryContactSheetRenderer.ComposeCell(image, cell, [], 72, CultureInfo.InvariantCulture);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(rendered, 6, 49));
        Assert.Equal(Pixel(image, 10, 10), Pixel(rendered, 14, 14));
        byte[] jpeg = ImageCodec.EncodeJpeg(rendered, 90);
        using var pdf = new MemoryStream();
        ContactSheetPdfWriter.Write(pdf, new(), 1, _ => new(jpeg, rendered.PixelWidth, rendered.PixelHeight));
        string structure = Encoding.ASCII.GetString(pdf.ToArray());
        Assert.Contains("/ColorSpace /DeviceRGB", structure);
        Assert.DoesNotContain("/SMask", structure);
    });

    [Fact]
    public async Task ItemOcrAndTextCopyUseTheMaskedImageAndRetainTextOnlyOutput()
    {
        BitmapSource image = Image();
        var recognizer = new Recognizer(image);
        var ocr = new WindowsOcrService(recognizer, ImageCodec.TryDecodePng, ImageCodec.TryLoad,
            NullLogger.Instance, useAdaptiveScale: false);
        string? copied = null;
        var service = new TextCaptureService(ocr, () => new OcrSettings { UpscaleFactor = 1 },
            text => { copied = text; return Task.FromResult(true); }, NullLogger.Instance);
        TextCaptureOutcome outcome = await service.RecognizeAndCopyAsync(image, CancellationToken.None);
        Assert.Equal(TextCaptureStatus.Copied, outcome.Status);
        Assert.Equal("masked text", copied);
        Assert.True(recognizer.Calls > 0);
        Assert.Equal(10, outcome.CharacterCount);
    }

    [Fact]
    public void RenderSyntheticCardAndPinEvidenceWhenRequested()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_FREEFORM_EVIDENCE") != "1") return;
        StaTestHost.Run(() =>
        {
            string directory = OwnedTestDirectory.Create("freeform-transparency-evidence-");
            using var language = MyCapture.Core.Localization.UiText.UseLanguage("ko");
            BitmapSource image = Image(320, 192);
            QuickAccessWindow card = Card(image);
            var pin = Pin(image);
            try
            {
                Border host = QuickAccessDetachedFixture.Layout(card);
                // This synthetic fixture gives the preview's existing backdrop a checker pattern.
                // No product visual or capture pixels are changed.
                Assert.IsType<Border>(card.PreviewForTest.Parent).Background = Pattern();
                BitmapSource cardImage = Render(host, (int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight));
                TestDiagnosticArtifacts.SavePng(directory, "card-pattern-100.png", cardImage);
                var content = Assert.IsType<Grid>(pin.Content);
                pin.Content = null;
                BitmapSource pinImage = Render(new Border { Background = Pattern(), Child = content }, 320, 192);
                TestDiagnosticArtifacts.SavePng(directory, "pin-pattern-100.png", pinImage);
                TestDiagnosticArtifacts.SavePng(directory, "freeform-result.png", image);
                Assert.Equal(new DpiScale(1, 1), VisualTreeHelper.GetDpi(host));
                output.WriteLine("Retained synthetic 100% evidence: " + directory);
            }
            finally { card.Close(); pin.Close(); }
        });
    }

    internal static BitmapSource Image(int width = 64, int height = 48)
    {
        var bounds = new RectD(0, 0, width, height);
        FreeformMask mask = Assert.IsType<FreeformMask>(FreeformMask.Create(
            [new(0, 0), new(width, 0), new(width, height), new(0, height / 2.0)], bounds));
        return FreeformCapture.Crop(new FrozenFrame(FrozenPixels(width, height), bounds, null, 0), mask);
    }

    private static BitmapSource FrozenPixels(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int index = (y * width + x) * 4;
            pixels[index] = 180;
            pixels[index + 1] = (byte)(90 + y * 90 / height);
            pixels[index + 2] = (byte)(80 + x * 130 / width);
            pixels[index + 3] = 255;
        }
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    internal static AnnotationDocument Document()
    {
        var document = new AnnotationDocument { CanvasWidth = 64, CanvasHeight = 48 };
        document.Items.Add(new RectangleAnnotation
        {
            Rect = new RectD(24, 6, 18, 16), Fill = ColorRgba.FromRgb(0, 220, 0), StrokeThickness = 0,
        });
        return document;
    }

    internal static byte[] Pixels(BitmapSource image)
    {
        BitmapSource converted = image.Format == PixelFormats.Bgra32 ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] result = new byte[image.PixelWidth * image.PixelHeight * 4];
        converted.CopyPixels(result, image.PixelWidth * 4, 0);
        return result;
    }

    internal static byte[] Pixel(BitmapSource image, int x, int y)
    {
        int index = (y * image.PixelWidth + x) * 4;
        return Pixels(image)[index..(index + 4)];
    }

    internal static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        BitmapSource image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        image.Freeze();
        return image;
    }

    private static QuickAccessWindow Card(BitmapSource image) => new(image, () => Task.FromResult(true), () => { },
        () => Task.CompletedTask, () => Task.FromResult(true), () => { }, () => Task.FromResult("unused.png"),
        excludeFromCapture: false, targetMonitor: new MonitorInfo("synthetic", new(0, 0, 1280, 800), new(0, 0, 1280, 760), 96, true));

    private static PinWindow Pin(BitmapSource image) => new(PinContent.FromImage(image),
        new PinViewState(image.PixelWidth, image.PixelHeight, 1, 1, 0.1), -10000, -10000, () => new PinSettings());

    private static BitmapSource Render(FrameworkElement element, int width, int height)
    {
        VisualTreeHelper.SetRootDpi(element, new DpiScale(1, 1));
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(element);
        result.Freeze();
        return result;
    }

    private static Brush Pattern()
    {
        var drawing = new DrawingGroup();
        using (DrawingContext context = drawing.Open())
        {
            context.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, 24, 24));
            context.DrawRectangle(Brushes.LightSteelBlue, null, new Rect(0, 0, 12, 12));
            context.DrawRectangle(Brushes.LightSteelBlue, null, new Rect(12, 12, 12, 12));
        }
        var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 24, 24) };
        brush.Freeze();
        return brush;
    }

    private sealed class Recognizer(BitmapSource expected) : IOcrRecognizer
    {
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["en-US"];
        public int MaxImageDimension => 4096;
        internal int Calls { get; private set; }
        public Task<RecognizedText?> RecognizeAsync(BitmapSource preparedBitmap, string? languageTag,
            CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(Pixels(expected), Pixels(preparedBitmap));
            return Task.FromResult<RecognizedText?>(new("en-US", [new("masked text",
                [new("masked", new(4, 4, 20, 10)), new("text", new(25, 4, 20, 10))])]));
        }
    }
}
