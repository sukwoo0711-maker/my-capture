using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinDpiTests : KoreanCaptionTest
{
    [Fact]
    public void OpenPin_UsesPlacementMonitorScale_WithoutChangingTextLayout() => StaTestHost.Run(() =>
    {
        const string text = "reference";
        BitmapSource original = ClipboardTextRenderer.Render(text, false);
        PinManager normal = Manager(1);
        PinManager scaled = Manager(1.5);
        try
        {
            PinWindow at100 = normal.PinRenderedText(original, text, false);
            PinWindow at150 = scaled.PinRenderedText(original, text, false);

            Assert.Same(original, at100.PinnedContent.Image);
            Assert.Equal((int)Math.Ceiling(original.PixelWidth * 1.5), at150.PinnedContent.Image.PixelWidth);
            Assert.Equal((int)Math.Ceiling(original.PixelHeight * 1.5), at150.PinnedContent.Image.PixelHeight);
            Assert.Equal(144, at150.PinnedContent.Image.DpiX);
            Assert.Equal(at100.State.ImageWidthDip, at150.State.ImageWidthDip);
            Assert.Equal(at100.State.ImageHeightDip, at150.State.ImageHeightDip);
            Assert.Equal(at100.Width, at150.Width);
            Assert.Equal(at100.Height, at150.Height);

            BitmapSource image = Image();
            Assert.Same(image, scaled.PinImage(image).PinnedContent.Image);
        }
        finally
        {
            normal.CloseAllForExit();
            scaled.CloseAllForExit();
        }
    });

    [Fact]
    public void DpiChange_SwapsCurrentBitmap_KeepsPresentationAndCopySaveSnapshot() => StaTestHost.Run(() =>
    {
        const string text = "#define MAX_LEN 256";
        PinManager manager = Manager(1);
        try
        {
            PinWindow pin = manager.PinRenderedText(ClipboardTextRenderer.Render(text, false), text, false);
            pin.State.SetZoom(1.4);
            pin.Width = pin.State.WidthDip;
            pin.Height = pin.State.HeightDip;
            pin.State.SetOpacity(0.6);
            pin.Opacity = 0.6;
            pin.Left = 40;
            pin.Top = 50;
            pin.ApplyClickThrough(true);
            pin.ReportCopyResult(false);
            var presentation = (pin.Width, pin.Height, pin.State.Zoom, pin.Left, pin.Top, pin.Opacity);
            var feedback = pin.FeedbackForTest;
            PinContent old = pin.PinnedContent;
            int notifications = 0;
            manager.OpenPinsChanged += () => notifications++;

            pin.SimulateDpiChangeForTest(2);

            PinContent current = pin.PinnedContent;
            Assert.NotSame(old, current);
            Assert.NotSame(old.Image, current.Image);
            Assert.Equal(old.Image.PixelWidth * 2, current.Image.PixelWidth);
            Assert.Equal(old.Image.PixelHeight * 2, current.Image.PixelHeight);
            Assert.Equal(192, current.Image.DpiX);
            Assert.Equal(presentation, (pin.Width, pin.Height, pin.State.Zoom, pin.Left, pin.Top, pin.Opacity));
            Assert.True(pin.State.IsClickThrough);
            Assert.Equal(feedback, pin.FeedbackForTest);
            Assert.True(pin.IsFeedbackVisible);
            Assert.Equal(1, notifications);
            Assert.Same(current.Image, Assert.IsType<Image>(Assert.IsType<Grid>(pin.Content).Children[0]).Source);
            Assert.Same(current, Assert.Single(manager.SnapshotOpenPins()).Content);

            PinImageCopyRequest? copied = null;
            BitmapSource? saved = null;
            pin.CopyRequested += (_, request) => copied = request;
            pin.SaveRequested += (_, request) => saved = request.Image;
            ContextMenu menu = Assert.IsType<ContextMenu>(pin.ContextMenu);
            MenuItem copy = menu.Items.OfType<MenuItem>().Single(item => item.InputGestureText == "Ctrl+C");
            copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            pin.SimulateSaveRequestForTest(PinSaveMode.SaveAs);
            PinImageCopyRequest request = Assert.IsType<PinImageCopyRequest>(copied);
            Assert.Same(current.Image, request.Image);
            Assert.Equal(text, request.OriginalText);
            Assert.Same(current.Image, saved);
            copied = null;
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pin), 0, Key.C)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            };
            pin.HandleKeyDown(key, ModifierKeys.Control);
            Assert.True(key.Handled);
            Assert.Same(current.Image, Assert.IsType<PinImageCopyRequest>(copied).Image);
            BitmapSource? recognized = null;
            manager.OcrRequested += (_, image) => recognized = image;
            typeof(PinWindow).GetMethod("RequestOcr",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(pin, null);
            Assert.Same(current.Image, recognized);

            pin.SimulateDpiChangeForTest(2);
            Assert.Same(current, pin.PinnedContent);
            long expectedBytes = current.EstimatedRetainedBytes;
            Assert.Equal(ClosedPinMemory.EstimatePixelBytes(current.Image.PixelWidth, current.Image.PixelHeight, 32)
                + ClosedPinMemory.EstimateTextBytes(text), expectedBytes);
            pin.Close();
            Assert.Equal(expectedBytes, manager.ClosedRetainedBytes);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(1.75, true)]
    public void DpiOverrideRerendersAndKeepsCodeSelectionDuringZoom(double zoom, bool liveZoom) => StaTestHost.Run(() =>
    {
        const string text = "#define MAX_LEN 256\r\nreturn MAX_LEN;";
        PinCodeDocument document = Assert.IsType<PinCodeDocument>(ClipboardCodeRenderer.TryParse(text,
            "<pre style=\"background-color:#123456;color:#abcdef\">#define MAX_LEN 256\r\nreturn MAX_LEN;</pre>"));
        PinContent content = PinContent.FromCode(ClipboardCodeRenderer.Render(document), text, document);
        var state = new PinViewState(content.LayoutWidthDip, content.LayoutHeightDip, zoom, 0.7, 0.1);
        var pin = new PinWindow(content, state, 40, 50)
        {
            ShowActivated = false,
            SelectionFocus = new PassiveSelectionFocus(),
        };
        try
        {
            pin.Show();
            pin.SimulateDpiChangeForTest(1);
            pin.HandleKeyDown(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pin), 0, Key.F2)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            }, ModifierKeys.None);
            PinTextSelectionView selection = Assert.IsType<PinTextSelectionView>(pin.TextSelectionForTest);
            TextBox textBox = Assert.IsType<TextBox>(selection.TextBox);
            textBox.Select(8, 7);
            if (liveZoom)
            {
                pin.HandleKeyDown(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pin), 0, Key.OemPlus)
                {
                    RoutedEvent = Keyboard.KeyDownEvent,
                }, ModifierKeys.None);
                Assert.True(pin.IsLiveZoomingForTest);
            }
            pin.UpdateLayout();
            var geometry = (pin.Width, pin.Height, pin.Left, pin.Top, state.Zoom, pin.Opacity);
            int notifications = 0;
            int dpiEvents = 0;
            pin.ViewStateChanged += () => notifications++;
            pin.DpiChanged += (_, args) =>
            {
                dpiEvents++;
                Assert.Equal(2, args.NewDpi.DpiScaleX);
                Assert.Equal(192, pin.PinnedContent.Image.DpiX);
            };
            var dpiOverride = typeof(PinWindow).GetMethod("OnDpiChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            PinContent before = pin.PinnedContent;
            Assert.Equal(96, before.Image.DpiX);
            // Exercise the production override and its base event, without changing the desktop's DPI.
            dpiOverride.Invoke(pin, [new DpiScale(1, 1), new DpiScale(2, 2)]);
            pin.UpdateLayout();

            Assert.NotSame(before, pin.PinnedContent);
            Assert.NotSame(before.Image, pin.PinnedContent.Image);
            Assert.Equal(192, pin.PinnedContent.Image.DpiX);
            Assert.Equal(content.Image.PixelWidth * 2, pin.PinnedContent.Image.PixelWidth);
            Assert.Equal(content.Image.PixelHeight * 2, pin.PinnedContent.Image.PixelHeight);
            Assert.Equal(geometry, (pin.Width, pin.Height, pin.Left, pin.Top, state.Zoom, pin.Opacity));
            Assert.Equal(1, dpiEvents);
            Assert.Equal(1, notifications);
            Assert.Same(selection, pin.TextSelectionForTest);
            Assert.Equal("MAX_LEN", selection.SelectedOriginalText);
            Assert.Equal(ClipboardCodeRenderer.FontFamilyName, textBox.FontFamily.Source);
            Assert.Equal(ClipboardCodeRenderer.FontSizeDip * state.Zoom, textBox.FontSize);
            Assert.Equal(ClipboardCodeRenderer.LineHeightDip * state.Zoom, TextBlock.GetLineHeight(textBox));
            Assert.Equal(new Thickness(ClipboardCodeRenderer.HorizontalPaddingDip * state.Zoom,
                ClipboardCodeRenderer.VerticalPaddingDip * state.Zoom,
                ClipboardCodeRenderer.HorizontalPaddingDip * state.Zoom,
                ClipboardCodeRenderer.VerticalPaddingDip * state.Zoom), textBox.Padding);
            Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), Assert.IsType<SolidColorBrush>(selection.Background).Color);
            Assert.Same(selection.Background, textBox.Background);
            Assert.Equal(liveZoom, pin.IsLiveZoomingForTest);

            PinContent current = pin.PinnedContent;
            dpiOverride.Invoke(pin, [new DpiScale(2, 2), new DpiScale(2, 2)]);
            Assert.Same(current, pin.PinnedContent);
            Assert.Equal(2, dpiEvents);
            Assert.Equal(1, notifications);
            pin.ForceZoomSettleForTest();
            Assert.False(pin.IsLiveZoomingForTest);
            Assert.Same(selection, pin.TextSelectionForTest);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.5)]
    public void FractionalRasterPadding_IsClippedWithoutResamplingAndZoomKeepsFrameInset(double zoom) => StaTestHost.Run(() =>
    {
        string text = Enumerable.Range(20, 60).Select(length => new string('m', length))
            .First(value => ClipboardTextRenderer.Render(value, false).PixelWidth % 4 != 0);
        PinContent content = PinContent.FromText(ClipboardTextRenderer.Render(text, false), text, false);
        var state = new PinViewState(content.LayoutWidthDip, content.LayoutHeightDip, 1, 1, 0.1);
        var pin = new PinWindow(content, state, 0, 0) { ShowActivated = false };
        try
        {
            pin.Show();
            pin.SimulateDpiChangeForTest(1.25);
            Image image = Assert.IsType<Image>(Assert.IsType<Grid>(pin.Content).Children[0]);
            Assert.Equal(Stretch.None, image.Stretch);
            Assert.Equal(HorizontalAlignment.Left, image.HorizontalAlignment);
            Assert.Equal(VerticalAlignment.Top, image.VerticalAlignment);
            Assert.True(pin.PinnedContent.Image.Width > state.ImageWidthDip);
            Assert.Equal(1, Assert.IsType<ScaleTransform>(image.LayoutTransform).ScaleX);
            Assert.Equal(content.LayoutWidthDip, pin.Width);

            state.SetZoom(zoom);
            pin.Width = state.WidthDip;
            pin.Height = state.HeightDip;
            pin.Measure(new Size(pin.Width, pin.Height));
            pin.Arrange(new Rect(0, 0, pin.Width, pin.Height));
            pin.UpdateLayout();
            var transform = Assert.IsType<ScaleTransform>(image.LayoutTransform);
            var clip = Assert.IsType<RectangleGeometry>(image.Clip);
            Assert.Equal(state.Zoom, transform.ScaleX);
            Assert.Equal(state.Zoom, transform.ScaleY);
            Assert.Equal(0.5, clip.Rect.X * state.Zoom, 6);
            Assert.Equal(state.WidthDip - 1, clip.Rect.Width * state.Zoom, 6);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void DpiChange_DoesNotRerenderImagesSpreadsheetPicturesOrLegacyText() => StaTestHost.Run(() =>
    {
        BitmapSource bitmap = Image();
        PinContent[] contents =
        [
            PinContent.FromImage(bitmap),
            PinContent.FromTablePicture(bitmap, "a\tb"),
            PinContent.FromSession(bitmap, PinContentKind.Text, "old code", "", true),
        ];
        foreach (PinContent content in contents)
        {
            var pin = new PinWindow(content, new PinViewState(80, 60, 1, 1, 0.1), 0, 0);
            try
            {
                int changes = 0;
                pin.ViewStateChanged += () => changes++;
                pin.SimulateDpiChangeForTest(2);
                Assert.Same(content, pin.PinnedContent);
                Assert.Same(bitmap, pin.PinnedContent.Image);
                Assert.Equal(0, changes);
            }
            finally { pin.Close(); }
        }
    });

    [Fact]
    public void RestoreSessionCode_RerendersAtResolvedMonitorScale_WithSameColors() => StaTestHost.Run(() =>
    {
        const string text = "int count = 1;";
        PinCodeDocument document = Assert.IsType<PinCodeDocument>(
            ClipboardCodeRenderer.TryParse(text, "<pre style=\"background-color:#123456;color:#abcdef\">int count = 1;</pre>"));
        BitmapSource saved = ClipboardCodeRenderer.Render(document, 1);
        PinContent content = PinContent.FromSession(saved, PinContentKind.Text, text, "", true, PinTextStyle.Code, document);
        PinManager manager = Manager(2);
        manager.ReadRestoreMonitorsForTest = () => [Monitor(2)];
        try
        {
            PinWindow restored = manager.RestoreSessionPin(new PinManager.ClosedPin(
                content, saved.Width, saved.Height, 1.25, 0.7,
                new PinClosedPlacement(new RectD(100, 100, 600, 200), "fake", Monitor(2).Bounds)));
            Assert.Equal(192, restored.PinnedContent.Image.DpiX);
            Assert.Equal(saved.PixelWidth * 2, restored.PinnedContent.Image.PixelWidth);
            Assert.Equal(saved.Width, restored.State.ImageWidthDip);
            Assert.Equal(saved.Height, restored.State.ImageHeightDip);
            Assert.Equal(1.25, restored.State.Zoom);
            Assert.Equal(0.7, restored.State.Opacity);
            Assert.Equal(document, restored.PinnedContent.CodeDocument);
            Assert.Equal(Pixels(ClipboardCodeRenderer.Render(document, 2)), Pixels(restored.PinnedContent.Image));
        }
        finally { manager.CloseAllForExit(); }
    });

    private static PinManager Manager(double scale) => new(() => new PinSettings(), NullLogger.Instance)
    {
        ReadPlacementMonitorForTest = () => Monitor(scale),
        BeforeShowForTest = pin => pin.ShowActivated = false,
        ActivatePinForTest = _ => { },
    };

    private sealed class PassiveSelectionFocus : IPinSelectionFocus
    {
        public bool IsActive(PinWindow owner) => false;
        public void Enter(PinWindow owner, PinTextSelectionView selection) { }
        public void Restore(PinWindow owner) => throw new InvalidOperationException("The passive pin must not request focus.");
    }

    private static MonitorInfo Monitor(double scale) => new("fake",
        new RectD(0, 0, 3840, 2160), new RectD(0, 0, 3840, 2100), (uint)(96 * scale), true);

    private static BitmapSource Image()
    {
        var image = new RenderTargetBitmap(80, 60, 96, 96, PixelFormats.Pbgra32);
        image.Freeze();
        return image;
    }

    private static byte[] Pixels(BitmapSource image)
    {
        int stride = (image.PixelWidth * image.Format.BitsPerPixel + 7) / 8;
        var bytes = new byte[stride * image.PixelHeight];
        image.CopyPixels(bytes, stride, 0);
        return bytes;
    }
}
