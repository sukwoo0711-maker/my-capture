using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Core.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Ocr;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class PinTranslationIntegrationTests
{
    [Fact]
    public Task PictureOverlayReadsOnceAndTogglesAllViewsWithoutChangingImageFrame() => RunAsync(async () =>
    {
        var translator = new FakeTranslator();
        PinManager manager = Manager(translator);
        int recognizes = 0;
        manager.ConfigureImageTranslation((_, _) =>
        {
            recognizes++;
            return Task.FromResult(PinTranslationOverlayTests.Recognition(PinTranslationOverlayTests.Line(English, 20, 20, 400, 30)));
        });
        try
        {
            BitmapSource source = Image();
            PinWindow pin = manager.PinImage(source);
            var frame = (pin.Left, pin.Top, pin.Width, pin.Height, pin.State.Zoom, pin.Opacity);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.True(pin.IsShowingTranslation);
            Assert.False(pin.IsShowingTranslationDocument);
            Assert.Equal(Korean, pin.VisibleText);
            Assert.Equal(frame, (pin.Left, pin.Top, pin.Width, pin.Height, pin.State.Zoom, pin.Opacity));
            Assert.Contains(pin.ContextMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("Pin_TranslationDocumentView")));
            pin.SetTranslationDocumentDisplay(true);
            Assert.True(pin.IsShowingTranslationDocument);
            Assert.Contains(pin.ContextMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("Pin_TranslationPictureView")));
            pin.SetTranslationDocumentDisplay(false);
            Assert.Equal(frame, (pin.Left, pin.Top, pin.Width, pin.Height, pin.State.Zoom, pin.Opacity));
            pin.InvokeTranslationAction();
            Assert.False(pin.IsShowingTranslation);
            Assert.Same(source, pin.PinnedContent.Image);
            Assert.Null(pin.DisplayedText);
            pin.InvokeTranslationAction();
            Assert.False(pin.IsShowingTranslationDocument);
            Assert.Equal(frame, (pin.Left, pin.Top, pin.Width, pin.Height, pin.State.Zoom, pin.Opacity));
            pin.SimulateDpiChangeForTest(2);
            Assert.Equal(600, Assert.IsType<Image>(Assert.IsType<Grid>(pin.Content).Children[0]).Source.Width);
            Assert.Equal(1, recognizes);
            Assert.Equal(1, translator.Translations);
            Assert.Equal(1, translator.WarmUps);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task ARecognizedPictureAlreadyInTheTargetLanguageNeitherChecksNorLoadsAModel() => RunAsync(async () =>
    {
        var translator = new FakeTranslator();
        PinManager manager = Manager(translator);
        manager.ConfigureImageTranslation((_, _) => Task.FromResult(PinTranslationOverlayTests.Recognition(
            PinTranslationOverlayTests.Line(Korean, 20, 20, 400, 30))));
        try
        {
            PinWindow pin = manager.PinImage(Image());
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.False(pin.IsShowingTranslation);
            Assert.Equal((0, 0, 0), (translator.InstallChecks, translator.WarmUps, translator.Translations));
        }
        finally { manager.CloseAll(); }
    });

    [Theory]
    [InlineData(English, 1)]
    [InlineData(Korean, 0)]
    public Task AFirstReadingThatTranslatesLoadsTheModelWhileTheRowsAreReadAgain(string firstReading, int warmUpsDuringTheReading)
        => RunAsync(async () =>
    {
        var translator = new FakeTranslator();
        PinManager manager = Manager(translator);
        int warmUps = -1;
        manager.ConfigureImageTranslation((_, first, _) =>
        {
            first?.Invoke(PinTranslationOverlayTests.Recognition(PinTranslationOverlayTests.Line(firstReading, 20, 20, 400, 30)));
            warmUps = translator.WarmUps;
            return Task.FromResult(PinTranslationOverlayTests.Recognition(PinTranslationOverlayTests.Line(English, 20, 20, 400, 30)));
        });
        try
        {
            PinWindow pin = manager.PinImage(Image());
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            Assert.True(pin.IsShowingTranslation);
            Assert.Equal(warmUpsDuringTheReading, warmUps);
            Assert.Equal((1, 1), (translator.WarmUps, translator.Translations));
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task PictureTranslationShowsItsLayersOnlyAtOneDevicePixelPerSourcePixel() => RunAsync(async () =>
    {
        PinManager manager = Manager(new FakeTranslator());
        manager.ReadPlacementMonitorForTest = () => new MonitorInfo("fake",
            new RectD(0, 0, 3840, 2160), new RectD(0, 0, 3840, 2100), 96, true);
        manager.ConfigureImageTranslation((_, _) => Task.FromResult(PinTranslationOverlayTests.Recognition(
            PinTranslationOverlayTests.Line(English, 20, 20, 400, 30))));
        try
        {
            PinWindow pin = manager.PinImage(Image());
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            // Not yet measured: the layers, without a flattened copy.
            Assert.IsType<DrawingImage>(pin.ImageSourceForTest);
            Grid root = Assert.IsType<Grid>(pin.Content);
            Image image = Assert.IsType<Image>(root.Children[0]);
            void Layout()
            {
                // A window that was never shown lays out nothing, so the content is laid out at the window's size.
                root.Measure(new Size(pin.Width, pin.Height));
                root.Arrange(new Rect(0, 0, pin.Width, pin.Height));
                root.UpdateLayout();
            }
            Layout();
            MethodInfo dpiChanged = typeof(PinWindow).GetMethod("OnDpiChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var native = new DpiScale(600 / image.RenderSize.Width, 240 / image.RenderSize.Height);
            var larger = new DpiScale(native.DpiScaleX * 1.5, native.DpiScaleY * 1.5);
            dpiChanged.Invoke(pin, [larger, native]);
            Assert.IsType<DrawingImage>(pin.ImageSourceForTest);

            // A scaled source is interpolated past the erased ink, so another monitor scale or a zoom
            // shows the flattened picture, the same bitmap a copy gets.
            dpiChanged.Invoke(pin, [native, larger]);
            Assert.Same(pin.DisplayedImageForTest, pin.ImageSourceForTest);
            dpiChanged.Invoke(pin, [larger, native]);
            Assert.IsType<DrawingImage>(pin.ImageSourceForTest);
            typeof(PinWindow).GetMethod("ApplyZoom", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(pin, [2.0, pin.Left, pin.Top, false]);
            pin.ForceZoomSettleForTest();
            Layout();
            Assert.Same(pin.DisplayedImageForTest, pin.ImageSourceForTest);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task OverlayCtrlCCopiesVisibleImageAndTextWhileSaveAndDoubleClickUseOriginal() => RunAsync(async () =>
    {
        PinManager manager = Manager(new FakeTranslator());
        manager.ConfigureImageTranslation((_, _) => Task.FromResult(PinTranslationOverlayTests.Recognition(
            PinTranslationOverlayTests.Line(English, 20, 20, 400, 30))));
        try
        {
            BitmapSource source = Image();
            PinWindow pin = manager.PinImage(source);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            PinImageCopyRequest? copied = null;
            pin.CopyRequested += (_, request) => copied = request;
            // ShowPinForTest keeps the pin hidden, so the key event borrows a probe source.
            using var keySource = new HwndSource(new HwndSourceParameters("Pin overlay copy test"));
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, keySource, 0, Key.C)
                { RoutedEvent = Keyboard.KeyDownEvent };
            pin.HandleKeyDown(key, ModifierKeys.Control);
            PinImageCopyRequest overlayCopy = Assert.IsType<PinImageCopyRequest>(copied);
            Assert.Equal(Korean, overlayCopy.OriginalText);
            Assert.NotSame(source, overlayCopy.Image);
            BitmapSource? ocrSource = null, saved = null;
            pin.OcrRequested += (_, image) => ocrSource = image;
            pin.SimulateCtrlDoubleClickForTest();
            Assert.Same(source, ocrSource);
            pin.SaveRequested += (_, request) => saved = request.Image;
            pin.SimulateSaveRequestForTest(PinSaveMode.SaveAs);
            Assert.Same(source, saved);
            pin.SetTranslationDisplay(false);
            pin.HandleKeyDown(key, ModifierKeys.Control);
            PinImageCopyRequest originalCopy = Assert.IsType<PinImageCopyRequest>(copied);
            Assert.Same(source, originalCopy.Image);
            Assert.Null(originalCopy.OriginalText);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task DocumentViewSessionAndCloseRestoreKeepOriginalPictureAndImageGeometry() => RunAsync(async () =>
    {
        PinManager manager = Manager(new FakeTranslator());
        manager.ConfigureImageTranslation((_, _) => Task.FromResult(PinTranslationOverlayTests.Recognition(
            PinTranslationOverlayTests.Line(English, 20, 20, 400, 30))));
        try
        {
            BitmapSource source = Image();
            PinWindow pin = manager.PinImage(source);
            var natural = (pin.State.ImageWidthDip, pin.State.ImageHeightDip, pin.State.Zoom);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            pin.SetTranslationDocumentDisplay(true);
            PinManager.ClosedPin snapshot = Assert.Single(manager.SnapshotOpenPins());
            Assert.Same(source, snapshot.Content.Image);
            Assert.Null(snapshot.Content.OriginalText);
            Assert.Equal(natural, (snapshot.ImageWidthDip, snapshot.ImageHeightDip, snapshot.Zoom));
            PinWindow session = manager.RestoreSessionPin(snapshot);
            Assert.False(session.IsShowingTranslation);
            Assert.Equal(PinContentKind.Image, session.PinnedContent.Kind);
            Assert.Same(source, session.PinnedContent.Image);
            Assert.Equal(natural, (session.State.ImageWidthDip, session.State.ImageHeightDip, session.State.Zoom));
            pin.Close();
            PinWindow restored = Assert.IsType<PinWindow>(await manager.RestoreLastClosedAsync());
            Assert.False(restored.IsShowingTranslation);
            Assert.Equal(PinContentKind.Image, restored.PinnedContent.Kind);
            Assert.Null(restored.PinnedContent.OriginalText);
            Assert.Equal((source.PixelWidth, source.PixelHeight), (restored.PinnedContent.Image.PixelWidth, restored.PinnedContent.Image.PixelHeight));
            Assert.Equal(natural, (restored.State.ImageWidthDip, restored.State.ImageHeightDip, restored.State.Zoom));
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task LateProvenanceResolutionSurvivesEveryPictureTranslationView() => RunAsync(async () =>
    {
        PinManager manager = Manager(new FakeTranslator());
        manager.ConfigureImageTranslation((_, _) => Task.FromResult(PinTranslationOverlayTests.Recognition(
            PinTranslationOverlayTests.Line(English, 20, 20, 400, 30))));
        var pending = new TaskCompletionSource<CaptureProvenance>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            PinContent content = PinContent.FromImage(Image()) with { PendingProvenance = pending.Task };
            await manager.PasteFromClipboardAsync(() => Task.FromResult(ClipboardImageReader.PinReadAttempt.Success(content)));
            PinWindow pin = Assert.Single(manager.SnapshotOpenPinWindows());
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            CaptureProvenance provenance = CaptureProvenance.Create(url: "https://example.org/overlay");
            pending.SetResult(provenance);
            await pin.PendingProvenanceUpdateForTest;
            Assert.Equal(provenance, pin.PinnedContent.Provenance);
            pin.SetTranslationDocumentDisplay(true);
            Assert.Equal(provenance, pin.PinnedContent.Provenance);
            pin.SetTranslationDocumentDisplay(false);
            Assert.Equal(provenance, pin.PinnedContent.Provenance);
            pin.SetTranslationDisplay(false);
            Assert.Equal(provenance, pin.PinnedContent.Provenance);
        }
        finally { manager.CloseAll(); }
    });
}
