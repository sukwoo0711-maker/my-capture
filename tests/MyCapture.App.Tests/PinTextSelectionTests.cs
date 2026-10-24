using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinTextSelectionTests
{
    private const string Source = "#include <stdio.h>\r\n#define MAX_LEN 256\r\n";

    [Fact]
    public void F2AndMenuToggleSelectionAndEscapeLeavesBeforeClosing() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        var pin = NewPin();
        try
        {
            pin.Show();
            var item = Assert.Single(pin.ContextMenu!.Items.OfType<MenuItem>(), item => item.InputGestureText == "F2");
            var items = pin.ContextMenu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(UiText.Get("Text_976A6C92B870"), items[items.IndexOf(item) - 1].Header);
            Assert.True(item.IsCheckable);
            Assert.False(item.IsChecked);
            Press(pin, Key.F2);
            Assert.True(pin.IsTextSelectionMode);
            Assert.True(item.IsChecked);
            Assert.Equal("Select text", AutomationProperties.GetName(pin.TextSelectionForTest!));
            Assert.Equal(UiText.Get("Pin_SelectTextStarted"), pin.FeedbackForTest.Text);
            Assert.True(pin.TextSelectionForTest!.TextBox!.IsKeyboardFocusWithin);
            Press(pin, Key.F2);
            Assert.False(pin.IsTextSelectionMode);
            Assert.False(item.IsChecked);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(pin.IsTextSelectionMode);
            Assert.True(item.IsChecked);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(pin.IsTextSelectionMode);
            Assert.False(item.IsChecked);
            Press(pin, Key.F2);
            Press(pin, Key.Escape);
            Assert.False(pin.IsTextSelectionMode);
            Assert.False(pin.IsClosed);
            Assert.Equal(UiText.Get("Pin_SelectTextEnded"), pin.FeedbackForTest.Text);
            Press(pin, Key.Escape);
            Assert.True(pin.IsClosed);
        }
        finally { if (!pin.IsClosed) pin.Close(); }
    });

    [Fact]
    public void ImagePinHasNoSelectionMenuAndF2ShowsOcrHint() => StaTestHost.Run(() =>
    {
        var pin = new PinWindow(PinContent.FromImage(Image()), State(), 200, 150);
        try
        {
            pin.Show();
            Press(pin, Key.F2);
            Assert.False(pin.IsTextSelectionMode);
            Assert.DoesNotContain(pin.ContextMenu!.Items.OfType<MenuItem>(), item => item.InputGestureText == "F2");
            Assert.Equal(UiText.Get("Pin_SelectTextImageHint"), pin.FeedbackForTest.Text);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(20, 19, "#define MAX_LEN 256")]
    [InlineData(0, 39, "#include <stdio.h>\r\n#define MAX_LEN 256")]
    [InlineData(0, 41, Source)]
    public void SelectionCopiesExactSubstringWithoutImage(int start, int length, string expected) => StaTestHost.Run(() =>
    {
        var pin = NewPin();
        string? copied = null;
        int images = 0;
        pin.OriginalTextCopyRequested += (_, text) => copied = text;
        pin.CopyRequested += (_, _) => images++;
        try
        {
            pin.Show();
            Press(pin, Key.F2);
            pin.TextSelectionForTest!.TextBox!.Select(start, length);
            Press(pin, Key.C, ModifierKeys.Control);
            Assert.Equal(expected, copied);
            Assert.Equal(0, images);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void NoSelectionCopiesOriginalTextAndImageAndCtrlASelectsAll() => StaTestHost.Run(() =>
    {
        var pin = NewPin();
        PinImageCopyRequest? imageCopy = null;
        string? textCopy = null;
        pin.CopyRequested += (_, request) => imageCopy = request;
        pin.OriginalTextCopyRequested += (_, text) => textCopy = text;
        try
        {
            pin.Show();
            Press(pin, Key.F2);
            Press(pin, Key.C, ModifierKeys.Control);
            Assert.NotNull(imageCopy);
            Assert.Equal(Source, imageCopy.Value.OriginalText);
            Assert.Null(textCopy);
            Press(pin, Key.A, ModifierKeys.Control);
            imageCopy = null;
            Press(pin, Key.C, ModifierKeys.Control);
            Assert.Equal(Source, textCopy);
            Assert.Null(imageCopy);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void SelectedTextFlowsThroughManagerAndTextServiceToFakeWriter() => StaTestHost.Run(() =>
    {
        string? copied = null;
        var manager = new PinManager(() => new PinSettings(), null,
            (_, _, _) => throw new InvalidOperationException("Image copying must not run."),
            text => { copied = text; return Task.FromResult(true); }, NullLogger.Instance);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), Source, false);
            Press(pin, Key.F2);
            pin.TextSelectionForTest!.TextBox!.Select(20, 19);
            Press(pin, Key.C, ModifierKeys.Control);
            Assert.True(manager.LastTextCopyOperationForTest!.GetAwaiter().GetResult());
            Assert.Equal("#define MAX_LEN 256", copied);
            Assert.Equal(UiText.Get("Text_5C9B3C62EC41"), pin.FeedbackForTest.Text);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void SelectionBlocksDragDoubleClickCtrlGesturesAndHoverThenRestoresDrag() => StaTestHost.Run(() =>
    {
        var input = new DragInput();
        var pin = NewPin(input);
        int copies = 0;
        pin.CopyRequested += (_, _) => copies++;
        pin.OriginalTextCopyRequested += (_, _) => copies++;
        pin.OcrRequested += (_, _) => copies++;
        try
        {
            pin.Show();
            pin.ShowHoverBar();
            Assert.True(pin.IsHoverBarShown);
            Press(pin, Key.F2);
            Assert.False(pin.IsHoverBarShown);
            pin.ShowHoverBar();
            Assert.False(pin.IsHoverBarShown);
            double left = pin.Left;
            InvokeMouseDown(pin);
            InvokeMouseDown(pin, doubleClick: true);
            Assert.False(pin.IsClosed);
            Assert.Equal(0, input.Captures);
            Assert.Equal(left, pin.Left);
            pin.SimulateCtrlSingleClickForTest();
            pin.SimulateCtrlDoubleClickForTest();
            Assert.False(pin.IsCtrlClickTimerRunning);
            Assert.Equal(0, copies);
            Press(pin, Key.Left);
            Assert.Equal(left, pin.Left);
            Press(pin, Key.F2);
            InvokeMouseDown(pin);
            Assert.Equal(1, input.Captures);
            Assert.True(input.Captured);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void HideAndClosedPinRestoreDiscardSelectionMode() => StaTestHost.Run(() =>
    {
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance);
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), Source, false);
            PinContent original = pin.PinnedContent;
            Press(pin, Key.F2);
            pin.HidePin();
            Assert.False(pin.IsTextSelectionMode);
            pin.ShowPin();
            Assert.False(pin.IsTextSelectionMode);
            Press(pin, Key.F2);
            Assert.Same(original, pin.PinnedContent);
            pin.Close();
            PinWindow restored = Assert.IsType<PinWindow>(manager.RestoreLastClosed());
            Assert.False(restored.IsTextSelectionMode);
            Assert.Same(original, restored.PinnedContent);
            Assert.Equal(Source, restored.OriginalText);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void HideAllLeavesSelectionWithoutRequestingFocusOrActivation() => StaTestHost.Run(() =>
    {
        var focus = new SelectionFocus { Active = true };
        int activations = 0;
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance)
        {
            BeforeShowForTest = pin =>
            {
                pin.ShowActivated = false;
                pin.SelectionFocus = focus;
            },
            ActivatePinForTest = _ => activations++,
        };
        try
        {
            PinWindow pin = manager.PinRenderedText(Image(), Source, false);
            Press(pin, Key.F2);
            Assert.Equal(1, focus.Entries);
            Assert.Equal(1, activations);
            Assert.True(pin.IsTextSelectionMode);

            manager.HideOrShowAll();

            Assert.True(manager.AreHidden);
            Assert.True(pin.State.IsHidden);
            Assert.False(pin.IsTextSelectionMode);
            Assert.Equal(1, focus.Entries);
            Assert.Equal(0, focus.ActiveReads);
            Assert.Equal(0, focus.Restores);
            Assert.Equal(1, activations);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Theory]
    [InlineData(Key.Escape, true, 1)]
    [InlineData(Key.Escape, false, 0)]
    [InlineData(Key.F2, true, 1)]
    [InlineData(Key.F2, false, 0)]
    public void ExplicitSelectionExitRestoresFocusOnlyToAnActiveVisiblePin(Key key, bool active, int restores) => StaTestHost.Run(() =>
    {
        var focus = new SelectionFocus { Active = active };
        var pin = NewPin();
        pin.SelectionFocus = focus;
        try
        {
            pin.Show();
            Press(pin, Key.F2);
            Assert.Equal(1, focus.Entries);

            var exit = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pin), 0, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            pin.TextSelectionForTest!.TextBox!.RaiseEvent(exit);

            Assert.True(exit.Handled);
            Assert.False(pin.IsTextSelectionMode);
            Assert.False(pin.IsClosed);
            Assert.Equal(1, focus.ActiveReads);
            Assert.Equal(restores, focus.Restores);
            Assert.Equal(1, focus.Entries);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void TablePictureUsesGridAndCopyAndArrowsSelectCells() => StaTestHost.Run(() =>
    {
        const string table = "A\tB\r\nC\t\"D\tE\"\r\n";
        var pin = new PinWindow(PinContent.FromTablePicture(Image(), table), State(), 200, 150);
        string? copied = null;
        pin.OriginalTextCopyRequested += (_, value) => copied = value;
        try
        {
            pin.Show();
            Press(pin, Key.F2);
            var view = pin.TextSelectionForTest!;
            Assert.NotNull(view.TableGrid);
            Assert.Null(view.TextBox);
            view.SelectCells(1, 1, 1, 1);
            Press(pin, Key.C, ModifierKeys.Control);
            Assert.Equal("D\tE", copied);
            view.SelectCells(0, 0, 0, 0);
            double left = pin.Left;
            Press(pin, Key.Right, ModifierKeys.Shift);
            Press(pin, Key.Down, ModifierKeys.Shift);
            Assert.Equal(left, pin.Left);
            Press(pin, Key.C, ModifierKeys.Control);
            Assert.Equal("A\tB\r\nC\t\"D\tE\"", copied);
            Press(pin, Key.A, ModifierKeys.Control);
            Press(pin, Key.C, ModifierKeys.Control);
            Assert.Equal(table, copied);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void SelectionKeepsZoomAndImageSaveKeys() => StaTestHost.Run(() =>
    {
        var pin = NewPin();
        var saves = new List<PinSaveMode>();
        pin.SaveRequested += (_, request) =>
        {
            saves.Add(request.Mode);
            pin.ReportSaveResult(new PinSaveResult(PinSaveStatus.Cancelled));
        };
        try
        {
            pin.Show();
            Press(pin, Key.F2);
            var view = pin.TextSelectionForTest!;
            double initialFont = view.TextBox!.FontSize;
            Press(pin, Key.OemPlus);
            Assert.True(pin.State.Zoom > 1);
            Assert.True(view.TextBox.FontSize > initialFont);
            Press(pin, Key.D0);
            Assert.Equal(1, pin.State.Zoom);
            Assert.Equal(initialFont, view.TextBox.FontSize);
            Press(pin, Key.S, ModifierKeys.Control);
            Press(pin, Key.S, ModifierKeys.Control | ModifierKeys.Shift);
            Assert.Equal([PinSaveMode.QuickSave, PinSaveMode.SaveAs], saves);
            Assert.True(pin.IsTextSelectionMode);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void WheelScrollsSelectionAndStopsAtBoundaryWhileControlAndZoomKeysStillWork() => StaTestHost.Run(() =>
    {
        string text = string.Join("\r\n", Enumerable.Range(0, 80).Select(index => $"line {index}"));
        var pin = new PinWindow(PinContent.FromText(Image(), text, false), State(), 200, 150);
        try
        {
            pin.Show();
            Press(pin, Key.F2);
            pin.UpdateLayout();
            TextBox textBox = pin.TextSelectionForTest!.TextBox!;
            Wheel(pin, -120);
            pin.UpdateLayout();
            Assert.True(textBox.VerticalOffset > 0);
            Assert.Equal(1, pin.State.Zoom);
            Wheel(pin, -120, ModifierKeys.Control);
            Assert.Equal(0.9, pin.State.Opacity, precision: 5);
            Assert.Equal(1, pin.State.Zoom);
            textBox.ScrollToEnd();
            pin.UpdateLayout();
            Wheel(pin, -120);
            Assert.Equal(1, pin.State.Zoom);
            Press(pin, Key.OemPlus);
            Assert.True(pin.State.Zoom > 1);
            Press(pin, Key.OemMinus);
            Assert.Equal(1, pin.State.Zoom, precision: 5);
            Press(pin, Key.OemPlus);
            Press(pin, Key.D0);
            Assert.Equal(1, pin.State.Zoom);
            Assert.True(pin.IsTextSelectionMode);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void SmallSelectionWheelDeltasAccumulateWithoutChangingZoom() => StaTestHost.Run(() =>
    {
        string text = string.Join("\r\n", Enumerable.Range(0, 80).Select(index => $"line {index}"));
        var pin = new PinWindow(PinContent.FromText(Image(), text, false), State(), 200, 150);
        try
        {
            pin.Show();
            Press(pin, Key.F2);
            pin.UpdateLayout();
            TextBox textBox = pin.TextSelectionForTest!.TextBox!;
            for (int index = 0; index < 7; index++) Wheel(pin, -15);
            pin.UpdateLayout();
            Assert.Equal(0, textBox.VerticalOffset);
            Wheel(pin, -15);
            pin.UpdateLayout();
            double oneNotch = textBox.VerticalOffset;
            Assert.True(oneNotch > 0);
            Wheel(pin, -240);
            pin.UpdateLayout();
            Assert.Equal(oneNotch * 3, textBox.VerticalOffset, precision: 5);
            Wheel(pin, 60);
            Wheel(pin, -60);
            pin.UpdateLayout();
            Assert.Equal(oneNotch * 3, textBox.VerticalOffset, precision: 5);
            Assert.Equal(1, pin.State.Zoom);
            textBox.ScrollToHome();
            pin.UpdateLayout();
            Wheel(pin, 120);
            Assert.Equal(1, pin.State.Zoom);
            Press(pin, Key.Escape);
            Wheel(pin, -120);
            Assert.True(pin.State.Zoom < 1);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void SelectionOverlayStaysInsideRoundedChromeAndBelowOutline() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        BitmapSource bitmap = ClipboardTextRenderer.Render(Source, false);
        var pin = new PinWindow(PinContent.FromText(bitmap, Source, false),
            new PinViewState(bitmap.Width, bitmap.Height, 1, 1, 0.1), 200, 150);
        try
        {
            pin.Show();
            pin.HideHoverBar();
            pin.UpdateLayout();
            Press(pin, Key.F2);
            pin.ForceFeedbackTimeoutForTest();
            pin.TextSelectionForTest!.TextBox!.Select(20, 19);
            pin.UpdateLayout();
            var chrome = Assert.IsType<Grid>(pin.Content);
            Assert.Same(pin.TextSelectionForTest, chrome.Children[1]);
            Assert.IsType<Border>(chrome.Children[2]);
            var clip = Assert.IsType<RectangleGeometry>(pin.TextSelectionForTest.Clip);
            Assert.Equal(PinWindow.ImageClipBounds(pin.RenderSize), clip.Rect);
            Assert.Equal(255, Assert.IsType<SolidColorBrush>(pin.TextSelectionForTest.Background).Color.A);
        }
        finally
        {
            pin.Close();
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    private static PinWindow NewPin(IPinDragInput? input = null) =>
        new(PinContent.FromText(Image(), Source, false), State(), 200, 150,
            () => new PinSettings { CloseOnDoubleClick = true }, input) { ShowActivated = false };

    private static PinViewState State() => new(480, 180, 1, 1, 0.1);

    private static BitmapSource Image()
    {
        var image = new WriteableBitmap(480, 180, 96, 96, PixelFormats.Bgr32, null);
        image.Freeze();
        return image;
    }

    private static void Press(PinWindow pin, Key key, ModifierKeys modifiers = ModifierKeys.None) =>
        pin.HandleKeyDown(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pin), 0, key)
            { RoutedEvent = Keyboard.KeyDownEvent }, modifiers);

    private static void Wheel(PinWindow pin, int delta, ModifierKeys modifiers = ModifierKeys.None)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = Mouse.MouseWheelEvent };
        pin.HandleMouseWheel(args, modifiers);
        Assert.True(args.Handled);
    }

    private static void InvokeMouseDown(PinWindow pin, bool doubleClick = false)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
        if (doubleClick)
        {
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!
                .SetValue(args, 2);
        }
        typeof(PinWindow).GetMethod("OnMouseLeftButtonDown", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(pin, [args]);
    }

    private sealed class DragInput : IPinDragInput
    {
        internal int Captures { get; private set; }
        internal bool Captured { get; private set; }
        public ModifierKeys Modifiers => ModifierKeys.None;
        public (int X, int Y) CursorPosition => (300, 200);
        public Point LocalAnchor(MouseButtonEventArgs args, PinWindow owner) => new(10, 10);
        public bool LeftButtonPressed(MouseEventArgs args) => true;
        public bool TryCapture(PinWindow owner) { Captures++; Captured = true; return true; }
        public bool HasCapture(PinWindow owner) => Captured;
        public void Release(PinWindow owner) => Captured = false;
    }

    private sealed class SelectionFocus : IPinSelectionFocus
    {
        internal bool Active { get; init; }
        internal int ActiveReads { get; private set; }
        internal int Entries { get; private set; }
        internal int Restores { get; private set; }
        public bool IsActive(PinWindow owner) { ActiveReads++; return Active; }
        public void Enter(PinWindow owner, PinTextSelectionView selection) => Entries++;
        public void Restore(PinWindow owner) => Restores++;
    }
}
