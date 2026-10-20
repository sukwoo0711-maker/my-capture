using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Pinning;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinCoreFlowTests
{
    private const string Source = "#define VALUE\t42  \r\nif (value) {\n\tRun();  \r}\r\n";
    private const string TableSource = "항목\t수량\r\n연필\t3  \r\n";

    [Theory]
    [InlineData("keyboard", "text")]
    [InlineData("hover", "text")]
    [InlineData("menu", "text")]
    [InlineData("keyboard", "table")]
    [InlineData("hover", "table")]
    [InlineData("menu", "table")]
    [InlineData("keyboard", "image")]
    [InlineData("hover", "image")]
    [InlineData("menu", "image")]
    public void CopyGesturesCarryExactSourceThroughManager(string gesture, string kind) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage("en-US");
        BitmapSource image = Image();
        string? text = kind == "image" ? null : kind == "table" ? TableSource : Source;
        PinContent content = kind switch
        {
            "image" => PinContent.FromImage(image, "https://example.com/reference"),
            "table" => PinContent.FromTablePicture(image, TableSource),
            _ => PinContent.FromText(image, Source, isTable: false),
        };
        int copies = 0;
        PinImageCopyRequest? request = null;
        (BitmapSource Image, string? Url, string? Text)? forwarded = null;
        var manager = new PinManager(() => NewInstallDefaults.Create().Pin, null,
            (bitmap, url, original) =>
            {
                copies++;
                forwarded = (bitmap, url, original);
                return Task.FromResult(true);
            }, _ => throw new InvalidOperationException("The text-only path must not run."), NullLogger.Instance);
        PinWindow? shown = null;
        manager.BeforeShowForTest = pin =>
        {
            shown = pin;
            pin.CopyRequested += (_, copy) => request = copy;
        };
        try
        {
            Assert.Equal(PasteResult.Pinned, manager.PasteFromClipboardAsync(
                () => Task.FromResult(ClipboardImageReader.PinReadAttempt.Success(content))).GetAwaiter().GetResult());
            PinWindow pin = Assert.IsType<PinWindow>(shown);
            switch (gesture)
            {
                case "keyboard":
                    var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pin), 0, Key.C)
                        { RoutedEvent = Keyboard.KeyDownEvent };
                    pin.HandleKeyDown(key, ModifierKeys.Control);
                    Assert.True(key.Handled);
                    break;
                case "hover":
                    pin.ShowHoverBar();
                    pin.HoverBarForTest!.CopyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    break;
                case "menu":
                    MenuItem copy = Assert.Single(pin.ContextMenu!.Items.OfType<MenuItem>(),
                        item => item.InputGestureText == "Ctrl+C");
                    copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    break;
            }
            Assert.Equal(1, copies);
            Assert.True(forwarded.HasValue);
            Assert.Same(image, forwarded.Value.Image);
            Assert.Equal(content.SourcePageUrl, forwarded.Value.Url);
            Assert.Equal(text, forwarded.Value.Text);
            Assert.True(request.HasValue);
            Assert.Same(image, request.Value.Image);
            Assert.Equal(text, request.Value.OriginalText);
            Assert.Equal(content.SourcePageUrl, request.Value.SourcePageUrl);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Source)]
    [InlineData(TableSource)]
    public void ClipboardWriterPublishesOneDataObjectWithExactTextPngAndBitmap(string? source) => StaTestHost.Run(() =>
    {
        BitmapSource image = Image();
        int writes = 0;
        bool copied = ClipboardImageService.CopyImageWithTextAsync(image, "https://example.com/reference", source, data =>
        {
            writes++;
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            Assert.Equal(source is not null, data.GetDataPresent(DataFormats.UnicodeText, autoConvert: false));
            Assert.Equal(source, data.GetData(DataFormats.UnicodeText, autoConvert: false));
            Assert.False(data.GetDataPresent(DataFormats.Text, autoConvert: false));
            Assert.True(data.GetDataPresent(DataFormats.Bitmap, autoConvert: false));
            Assert.Same(image, data.GetData(DataFormats.Bitmap, autoConvert: false));
            Assert.True(data.GetDataPresent("PNG", autoConvert: false));
            using var bytes = new MemoryStream();
            Assert.IsAssignableFrom<Stream>(data.GetData("PNG", autoConvert: false)).CopyTo(bytes);
            Assert.Equal("https://example.com/reference", PngSourceUrlMetadata.TryReadSourceUrl(bytes.ToArray()));
            bytes.Position = 0;
            BitmapSource decoded = BitmapDecoder.Create(bytes, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal(image.PixelWidth, decoded.PixelWidth);
            Assert.Equal(image.PixelHeight, decoded.PixelHeight);
        }).GetAwaiter().GetResult();
        Assert.True(copied);
        Assert.Equal(1, writes);
    });

    [Fact]
    public void ClipboardPinShowsPassivelyWhileCaptureTextAndRestoreStillActivate() => StaTestHost.Run(() =>
    {
        var manager = new PinManager(() => NewInstallDefaults.Create().Pin, NullLogger.Instance);
        var shown = new List<PinWindow>();
        var activated = new List<PinWindow>();
        manager.BeforeShowForTest = pin =>
        {
            Assert.False(pin.IsVisible);
            Assert.Equal(shown.Count != 0, pin.ShowActivated);
            shown.Add(pin);
        };
        manager.ActivatePinForTest = activated.Add;
        try
        {
            Assert.Equal(PasteResult.Pinned, manager.PasteFromClipboardAsync(() => Task.FromResult(
                ClipboardImageReader.PinReadAttempt.Success(PinContent.FromText(Image(), Source, false)))).GetAwaiter().GetResult());
            PinWindow clipboardPin = Assert.Single(shown);
            Assert.False(clipboardPin.ShowActivated);
            Assert.True(clipboardPin.Topmost);
            Assert.True(clipboardPin.Focusable);
            Assert.Empty(activated);
            clipboardPin.ShowHoverBar();
            Assert.False(clipboardPin.HoverBarForTest!.ShowActivated);
            Assert.Empty(activated);

            PinWindow capturePin = manager.PinImage(Image());
            Assert.True(capturePin.ShowActivated);
            Assert.Equal([capturePin], activated);
            PinWindow textPin = manager.PinRenderedText(Image(), Source, false);
            Assert.True(textPin.ShowActivated);
            Assert.Equal([capturePin, textPin], activated);
            clipboardPin.Close();
            PinWindow restored = Assert.IsType<PinWindow>(manager.RestoreLastClosed());
            Assert.True(restored.ShowActivated);
            Assert.Equal([capturePin, textPin, restored], activated);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClipboardReaderFailureCreatesNoPin(bool busy) => StaTestHost.Run(() =>
    {
        var manager = new PinManager(() => NewInstallDefaults.Create().Pin, NullLogger.Instance);
        manager.BeforeShowForTest = _ => throw new InvalidOperationException("No pin should open.");
        PasteResult result = manager.PasteFromClipboardAsync(() => Task.FromResult(busy
            ? ClipboardImageReader.PinReadAttempt.Busy()
            : ClipboardImageReader.PinReadAttempt.NoContent())).GetAwaiter().GetResult();
        Assert.Equal(busy ? PasteResult.ClipboardBusy : PasteResult.NoSupportedContent, result);
        Assert.Equal(0, manager.Count);
    });

    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(32, 24, 96, 96, PixelFormats.Bgra32, null, new byte[32 * 24 * 4], 32 * 4);
        image.Freeze();
        return image;
    }
}
