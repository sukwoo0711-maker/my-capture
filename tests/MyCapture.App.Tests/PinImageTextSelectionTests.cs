using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PinImageTextSelectionTests
{
    private const string FullText = "한글 English words\r\nwide    gap\r\nlast line";

    [Theory]
    [InlineData(0, 6)]
    [InlineData(6, 0)]
    public void HighlightRunsJoinNormalSpacesKeepWideGapsAndSeparateLines(int anchor, int active)
    {
        var selection = new PinImageTextSelection(Layout());
        Assert.Equal(new[] { new Rect(20, 20, 220, 20), new Rect(20, 60, 60, 20),
            new Rect(300, 60, 50, 20), new Rect(20, 100, 120, 20) },
            PinImageTextSelection.HighlightRuns(selection.Words, anchor, active));
        Assert.Empty(PinImageTextSelection.HighlightRuns(selection.Words, -1, -1));
        // Filling a space for painting does not turn it into a word hit target.
        Assert.Equal(-1, selection.HitTest(new Point(70, 30)));
        Assert.Equal(7, selection.Words.Length);
    }

    [Theory]
    [InlineData(30, 1)]
    [InlineData(30.1, 2)]
    public void HighlightRunUsesTallerNeighbourAndUnionOfVerticalEdges(double gap, int count)
    {
        PinImageTextSelection.Word[] words = [new("one", new Rect(10, 20, 20, 10), 0, 0, 3),
            new("two", new Rect(30 + gap, 15, 30, 30), 0, 4, 7)];
        IReadOnlyList<Rect> runs = PinImageTextSelection.HighlightRuns(words, 0, 1);
        Assert.Equal(count, runs.Count);
        if (count == 1) Assert.Equal(new Rect(10, 15, 50 + gap, 30), runs[0]);
        else Assert.Equal(words.Select(word => word.Bounds), runs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HighlightRunKeepsOneBoxLineFallback(bool corrected)
    {
        var box = new OcrRect(10, 20, 100, 25);
        var result = OcrResult.Success("corrected line", "en",
            [new OcrLine("corrected line", box, corrected ? [new OcrWord("original", box)] : [])], TimeSpan.Zero);
        var selection = new PinImageTextSelection(result);
        Assert.Single(selection.Words);
        Assert.Equal(new[] { new Rect(10, 20, 100, 25) }, PinImageTextSelection.HighlightRuns(selection.Words, 0, 0));
        selection.SelectAll();
        Assert.Equal(result.Text, selection.SelectedText);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void PartialSelectionKeepsTheResultsLineBreakConvention(string lineBreak)
    {
        OcrResult source = Layout();
        string text = FullText.Replace("\r\n", lineBreak, StringComparison.Ordinal);
        var selection = new PinImageTextSelection(OcrResult.Success(text, "ko", source.Lines, TimeSpan.Zero));
        selection.Select(1, 3);
        Assert.Equal("English words" + lineBreak + "wide", selection.SelectedText);
        selection.Select(3, 1);
        Assert.Equal("English words" + lineBreak + "wide", selection.SelectedText);
        selection.SelectAll();
        Assert.Equal(text, selection.SelectedText);
    }

    [Theory]
    [InlineData(.5)]
    [InlineData(1)]
    [InlineData(4)]
    public void BlankSpaceDragWaitsForSystemThresholdThenSelectsFromNearestWord(double scale) => StaTestHost.Run(() =>
    {
        var view = new PinImageTextSelectionView(Layout(), 480, 180);
        view.Measure(new Size(480 * scale, 180 * scale));
        view.Arrange(new Rect(0, 0, 480 * scale, 180 * scale));
        Point blank = new(2 * scale, 30 * scale);
        Assert.True(view.BeginDrag(blank));
        Assert.Null(view.Selection.SelectedText);
        view.ContinueDrag(new Point(blank.X + SystemParameters.MinimumHorizontalDragDistance,
            blank.Y + SystemParameters.MinimumVerticalDragDistance));
        Assert.Null(view.Selection.SelectedText);
        view.ContinueDrag(new Point(100 * scale, 30 * scale));
        Assert.Equal("한글 English", view.Selection.SelectedText);
        view.ContinueDrag(new Point(310 * scale, 70 * scale));
        Assert.Equal("한글 English words\r\nwide    gap", view.Selection.SelectedText);
        view.EndDrag();
        view.ContinueDrag(new Point(120 * scale, 110 * scale));
        Assert.Equal("한글 English words\r\nwide    gap", view.Selection.SelectedText);
    });

    [Fact]
    public void BlankSpaceClickClearsSelectionAndShiftClickExtendsWithoutWaiting() => StaTestHost.Run(() =>
    {
        var view = new PinImageTextSelectionView(Layout(), 480, 180);
        view.Measure(new Size(480, 180));
        view.Arrange(new Rect(0, 0, 480, 180));
        view.SelectAll();
        Assert.True(view.BeginDrag(new Point(2, 30)));
        view.EndDrag();
        Assert.Null(view.Selection.SelectedText);
        Assert.True(view.SelectAt(new Point(100, 30)));
        Assert.True(view.BeginDrag(new Point(355, 70), extend: true));
        Assert.Equal("English words\r\nwide    gap", view.Selection.SelectedText);
        view.EndDrag();
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NewPinBuildsMenuOnceLikeBaselineWithOrWithoutRecognition(bool text, bool recognition) => StaTestHost.Run(() =>
    {
        int calls = 0;
        var manager = new PinManager(() => new PinSettings(), null, (_, _, _) => Task.FromResult(true),
            _ => Task.FromResult(true), NullLogger.Instance)
        {
            RecognizeSelection = recognition ? (_, _) => { calls++; return Task.FromResult(Layout()); } : null,
            ShowPinForTest = static (_, _) => { },
            ActivatePinForTest = static _ => { },
        };
        try
        {
            PinWindow pin = text ? manager.PinRenderedText(Image(), "text", false) : manager.PinImage(Image());
            // dfc497d4 has a single BuildContextMenu call, in the PinWindow constructor.
            Assert.Equal(1, pin.ContextMenuBuildCountForTest);
            ContextMenu menu = pin.ContextMenu!;
            Assert.Equal(text || recognition, menu.Items.OfType<MenuItem>().Any(item => item.InputGestureText == "F2"));
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.Same(menu, pin.ContextMenu);
            Assert.Equal(1, pin.ContextMenuBuildCountForTest);
            Assert.Equal(0, calls);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Fact]
    public void MenuReadsChangedRecognitionDelegateOnOpeningWithoutRebuilding() => StaTestHost.Run(() =>
    {
        var pin = new PinWindow(PinContent.FromImage(Image()), new PinViewState(480, 180, 1, 1, .1), 200, 150);
        try
        {
            ContextMenu menu = pin.ContextMenu!;
            pin.RecognizeSelection = (_, _) => Task.FromResult(Layout());
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => item.InputGestureText == "F2");
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.Single(menu.Items.OfType<MenuItem>(), item => item.InputGestureText == "F2");
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.Single(menu.Items.OfType<MenuItem>(), item => item.InputGestureText == "F2");
            pin.RecognizeSelection = null;
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => item.InputGestureText == "F2");
            Assert.Same(menu, pin.ContextMenu);
            Assert.Equal(1, pin.ContextMenuBuildCountForTest);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData("ko-KR", "글자 인식 중…", "텍스트 인식에 실패했습니다.")]
    [InlineData("en-US", "Recognizing text…", "Text recognition failed.")]
    public Task RecognitionProgressAndFailureDescribeRecognition(string language, string progress, string failure) => RunAsync(async () =>
    {
        using var culture = UiText.UseLanguage(language);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pin = NewPin(new FakeOcr(_ => { started.SetResult(true); return result.Task; }));
        try
        {
            Press(pin, Key.F2);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            pin.AdvanceProgressForTest(FeedbackTiming.ProgressRevealDelay);
            Assert.Equal(progress, pin.FeedbackForTest.Text);
            Assert.Equal(FeedbackKind.Progress, pin.FeedbackForTest.Kind);
            result.SetException(new InvalidOperationException("Synthetic recognition failure"));
            await pin.SelectionWork;
            Assert.Equal(failure, pin.FeedbackForTest.Text);
            Assert.Equal(FeedbackKind.Warning, pin.FeedbackForTest.Kind);
            Assert.False(pin.IsTextSelectionMode);
            Assert.False(pin.IsRecognizingSelection);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData("ko-KR", "이미지에서 글자를 찾지 못했습니다.", false)]
    [InlineData("ko-KR", "이미지에서 글자를 찾지 못했습니다.", true)]
    [InlineData("en-US", "No text was found in the image.", false)]
    [InlineData("en-US", "No text was found in the image.", true)]
    public Task NoTextOrNoUsableBoxesShowsNoTextFeedback(string language, string message, bool invalidBoxes) => RunAsync(async () =>
    {
        using var culture = UiText.UseLanguage(language);
        OcrResult result = invalidBoxes ? OcrResult.Success("text", "en",
            [new OcrLine("text", new OcrRect(0, 0, 0, 0), [])], TimeSpan.Zero) : OcrResult.NoText("en", TimeSpan.Zero);
        var pin = NewPin(new FakeOcr(_ => Task.FromResult(result)));
        try
        {
            Press(pin, Key.F2);
            await pin.SelectionWork;
            Assert.Equal(message, pin.FeedbackForTest.Text);
            Assert.Equal(FeedbackKind.Warning, pin.FeedbackForTest.Kind);
            Assert.False(pin.IsTextSelectionMode);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RecognitionReceivesFrozenPixelsAndLeavesMutableSourceOwnedByUi(bool frozen) => RunAsync(async () =>
    {
        BitmapSource original = frozen ? Image() : Image().CloneCurrentValue();
        BitmapSource? received = null;
        bool receivedFrozen = false;
        int recognitionThread = 0, uiThread = Environment.CurrentManagedThreadId;
        var pin = NewPin(new FakeOcr(), image: original);
        pin.RecognizeSelection = (image, _) =>
        {
            received = image;
            receivedFrozen = image.IsFrozen;
            recognitionThread = Environment.CurrentManagedThreadId;
            image.CopyPixels(new byte[480 * 180 * 4], 480 * 4, 0);
            return Task.FromResult(Layout());
        };
        try
        {
            Press(pin, Key.F2);
            await pin.SelectionWork;
            Assert.True(pin.IsTextSelectionMode);
            Assert.True(receivedFrozen);
            Assert.NotEqual(uiThread, recognitionThread);
            Assert.Equal(frozen, original.IsFrozen);
            Assert.Same(original, pin.PinnedContent.Image);
            if (frozen) Assert.Same(original, received);
            else Assert.NotSame(original, received);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(0, 1, "한글 English")]
    [InlineData(1, 3, "English words\r\nwide")]
    [InlineData(3, 4, "wide    gap")]
    [InlineData(4, 1, "English words\r\nwide    gap")]
    public void RangesPreserveReadingOrderAndRecognizedSeparators(int anchor, int active, string expected)
    {
        var selection = new PinImageTextSelection(Layout());
        selection.Select(anchor, active);
        Assert.Equal(expected, selection.SelectedText);
    }

    [Fact]
    public void WordLineAllAndKeyboardRangesUseTheSameModel()
    {
        var selection = new PinImageTextSelection(Layout());
        selection.Select(1, 1);
        Assert.Equal("English", selection.SelectedText);
        selection.SelectLine(4);
        Assert.Equal("wide    gap", selection.SelectedText);
        selection.Select(1, 1);
        selection.Move(Key.Right, extend: true);
        Assert.Equal("English words", selection.SelectedText);
        selection.Move(Key.Down, extend: true);
        Assert.Equal("English words\r\nwide    gap", selection.SelectedText);
        selection.SelectAll();
        Assert.Equal(FullText, selection.SelectedText);
        selection.Clear();
        Assert.Null(selection.SelectedText);
    }

    [Theory]
    [InlineData(.1, 1)]
    [InlineData(.5, 1)]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(8, 1)]
    [InlineData(.5, 1.5)]
    [InlineData(1, 2)]
    [InlineData(4, 2)]
    public void ArrangedImageSizeMapsPixelsAtZoomAndDpi(double zoom, double dpi) => StaTestHost.Run(() =>
    {
        var view = new PinImageTextSelectionView(Layout(), 480, 180);
        var size = new Size(480 * zoom / dpi, 180 * zoom / dpi);
        view.Measure(size);
        view.Arrange(new Rect(size));
        Point word = new(100 * zoom / dpi, 30 * zoom / dpi);
        Assert.True(view.SelectAt(word, clicks: 2));
        Assert.Equal("English", view.Selection.SelectedText);
        Assert.Equal(100, view.ToPixels(word).X, precision: 10);
        Assert.Equal(30, view.ToPixels(word).Y, precision: 10);
        Assert.True(view.SelectAt(new Point(310 * zoom / dpi, 70 * zoom / dpi), extend: true));
        Assert.Equal("English words\r\nwide    gap", view.Selection.SelectedText);
        Assert.True(view.SelectAt(word, clicks: 3));
        Assert.Equal("한글 English words", view.Selection.SelectedText);
    });

    [Fact]
    public void AccessibilityExposesModeWordsAndSelectedRange() => StaTestHost.Run(() =>
    {
        var view = new PinImageTextSelectionView(Layout(), 480, 180);
        Assert.Equal(UiText.Get("Pin_SelectText"), AutomationProperties.GetName(view));
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(view);
        var provider = Assert.IsAssignableFrom<ISelectionProvider>(peer.GetPattern(PatternInterface.Selection));
        Assert.Empty(provider.GetSelection());
        Assert.Equal(7, peer.GetChildren().Count);
        Assert.Equal("English", peer.GetChildren()[1].GetName());
        var word = Assert.IsAssignableFrom<ISelectionItemProvider>(peer.GetChildren()[1].GetPattern(PatternInterface.SelectionItem));
        word.Select();
        Assert.Equal("English", view.Selection.SelectedText);
        Assert.Single(provider.GetSelection());
        Assert.True(word.IsSelected);
        view.SelectAll();
        Assert.Equal(7, provider.GetSelection().Length);
    });

    [Fact]
    public Task FirstEntryIsLazyBackgroundAndNextEntryReusesCopyAndTranslationRecognition() => RunAsync(async () =>
    {
        var ocr = new FakeOcr();
        var settings = new OcrSettings();
        var input = new DragInput();
        var pin = NewPin(ocr, settings, input);
        try
        {
            pin.Show();
            pin.ShowHoverBar();
            MouseDown(pin);
            Assert.Equal(1, input.Captures);
            Assert.Equal(0, ocr.Count);
            int uiThread = Environment.CurrentManagedThreadId;
            Press(pin, Key.F2);
            await pin.SelectionWork;
            Assert.True(pin.IsTextSelectionMode);
            Assert.Equal(1, ocr.Count);
            Assert.NotEqual(uiThread, ocr.ThreadId);
            Assert.False(pin.IsHoverBarShown);
            MouseDown(pin);
            Assert.Equal(1, input.Captures);
            double zoom = pin.State.Zoom;
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.MouseWheelEvent };
            pin.HandleMouseWheel(wheel, ModifierKeys.None);
            Assert.True(wheel.Handled);
            Assert.Equal(zoom, pin.State.Zoom);
            PinTextSelectionView oldView = pin.TextSelectionForTest!;
            Press(pin, Key.F2);
            Assert.False(pin.IsTextSelectionMode);
            Assert.Null(pin.TextSelectionForTest);
            Assert.Null(oldView.Parent);
            Press(pin, Key.F2);
            await pin.SelectionWork;
            Assert.True(pin.IsTextSelectionMode);
            Assert.NotSame(oldView, pin.TextSelectionForTest);
            Assert.Equal(1, ocr.Count);
            string? copied = null;
            Assert.True(await PinTextCopyService.CopyAsync(pin.PinnedContent.Image, ocr,
                text => { copied = text; return Task.FromResult(true); }, settings));
            Assert.Equal(FullText, copied);
            Assert.NotNull(await PinTranslationEngines.RecognizeAsync(ocr, pin.PinnedContent.Image, settings, CancellationToken.None));
            Assert.Equal(1, ocr.Count);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public Task MenuEntryPartialAndEmptySelectionCopyThroughExistingManagerWriter() => RunAsync(async () =>
    {
        var ocr = new FakeOcr();
        var copied = new List<string>();
        var manager = new PinManager(() => new PinSettings(), null,
            (_, _, _) => throw new InvalidOperationException("Image copy must not run in image selection mode."),
            text => { copied.Add(text); return Task.FromResult(true); }, NullLogger.Instance)
        {
            RecognizeSelection = (image, token) => PinTextCopyService.RecognizeAsync(image, ocr, new OcrSettings(), token),
            ShowPinForTest = static (_, _) => { },
            ActivatePinForTest = static _ => { },
            BeforeShowForTest = static pin => pin.SelectionFocus = new NoFocus(),
        };
        try
        {
            var pin = manager.PinImage(Image());
            var item = Assert.Single(pin.ContextMenu!.Items.OfType<MenuItem>(), candidate => candidate.InputGestureText == "F2");
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await pin.SelectionWork;
            Assert.True(item.IsChecked);
            Assert.Null(pin.PinnedContent.OriginalText);
            Press(pin, Key.C, ModifierKeys.Control);
            Assert.True(await manager.LastTextCopyOperationForTest!);
            pin.TextSelectionForTest!.ImageSelection!.Selection.Select(1, 3);
            Press(pin, Key.C, ModifierKeys.Control);
            Assert.True(await manager.LastTextCopyOperationForTest!);
            Press(pin, Key.A, ModifierKeys.Control);
            Press(pin, Key.C, ModifierKeys.Control);
            Assert.True(await manager.LastTextCopyOperationForTest!);
            Assert.Equal(new[] { FullText, "English words\r\nwide", FullText }, copied);
            Press(pin, Key.Escape);
            Assert.False(pin.IsTextSelectionMode);
            Assert.False(pin.IsClosed);
            Assert.False(item.IsChecked);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Theory]
    [InlineData(Key.Escape)]
    [InlineData(Key.F2)]
    public Task CancellingRecognitionKeepsNormalPinAndCancelsLastReader(Key key) => RunAsync(async () =>
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken received = default;
        var ocr = new FakeOcr(async token =>
        {
            received = token;
            started.SetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            return Layout();
        });
        var input = new DragInput();
        var pin = NewPin(ocr, input: input);
        try
        {
            Press(pin, Key.F2);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(pin.IsRecognizingSelection);
            Assert.False(pin.IsTextSelectionMode);
            MouseDown(pin);
            Assert.Equal(1, input.Captures);
            Press(pin, key);
            await pin.SelectionWork.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(received.IsCancellationRequested);
            Assert.False(pin.IsClosed);
            Assert.False(pin.IsRecognizingSelection);
            Assert.False(pin.IsTextSelectionMode);
            Assert.Null(pin.TextSelectionForTest);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public Task NoTextShowsExistingFeedbackWithoutEnteringMode() => RunAsync(async () =>
    {
        var pin = NewPin(new FakeOcr(_ => Task.FromResult(OcrResult.NoText("en", TimeSpan.Zero))));
        try
        {
            Press(pin, Key.F2);
            await pin.SelectionWork;
            Assert.False(pin.IsTextSelectionMode);
            Assert.Equal(UiText.Get("Pin_TranslationNoText"), pin.FeedbackForTest.Text);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public Task ClosingDropsCachedLayoutAndNewPinWithSamePixelsRecognizesAgain() => RunAsync(async () =>
    {
        var ocr = new FakeOcr();
        BitmapSource image = Image();
        var pin = NewPin(ocr, image: image);
        Press(pin, Key.F2);
        await pin.SelectionWork;
        pin.Close();
        Assert.Null(pin.TextSelectionForTest);
        var reopened = NewPin(ocr, image: image);
        try
        {
            Assert.Equal(1, ocr.Count);
            Press(reopened, Key.F2);
            await reopened.SelectionWork;
            Assert.Equal(2, ocr.Count);
        }
        finally { reopened.Close(); }
    });

    [Fact]
    public Task TranslatedPictureUsesExistingTextProjectionAndSourceSwitchLeavesMode() => RunAsync(async () =>
    {
        var ocr = new FakeOcr();
        var pin = NewPin(ocr);
        try
        {
            pin.ApplyTranslation(PinContent.FromText(Image(), "번역된 문장", false), needsReview: false);
            Press(pin, Key.F2);
            Assert.NotNull(pin.TextSelectionForTest!.TextBox);
            Assert.Null(pin.TextSelectionForTest.ImageSelection);
            Assert.Equal("번역된 문장", pin.TextSelectionForTest.TextBox.Text);
            Assert.Equal(0, ocr.Count);
            pin.SetTranslationDisplay(false);
            Assert.Null(pin.TextSelectionForTest);
            Press(pin, Key.F2);
            await pin.SelectionWork;
            Assert.NotNull(pin.TextSelectionForTest!.ImageSelection);
            Assert.Equal(1, ocr.Count);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task HideOrCloseDuringRecognitionCannotAttachLateView(bool close) => RunAsync(async () =>
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pin = NewPin(new FakeOcr(_ => { started.SetResult(true); return result.Task; }));
        try
        {
            Press(pin, Key.F2);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (close) pin.Close();
            else pin.HidePin();
            result.SetResult(Layout());
            await pin.SelectionWork.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pin.IsRecognizingSelection);
            Assert.Null(pin.TextSelectionForTest);
        }
        finally { if (!pin.IsClosed) pin.Close(); }
    });

    [Fact]
    public Task RenderedSelectionPreservesPixelsAndCanRetainBeforeAfterEvidence() => RunAsync(async () =>
    {
        using var language = UiText.UseLanguage("en-US");
        var drawing = new DrawingVisual();
        using (DrawingContext dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 480, 180));
            foreach (OcrLine line in Layout().Lines)
                foreach (OcrWord word in line.Words)
                    dc.DrawText(new FormattedText(word.Text, System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface("Segoe UI"), 18, Brushes.Black, 1),
                        new Point(word.Bounds.X, word.Bounds.Y));
        }
        var image = new RenderTargetBitmap(480, 180, 96, 96, PixelFormats.Pbgra32);
        image.Render(drawing);
        image.Freeze();
        byte[] original = Pixels(image);
        var pin = NewPin(new FakeOcr(), image: image);
        try
        {
            FrameworkElement chrome = (FrameworkElement)pin.Content;
            BitmapSource before = Render(chrome);
            Press(pin, Key.F2);
            await pin.SelectionWork;
            pin.ForceFeedbackTimeoutForTest();
            var selection = pin.TextSelectionForTest!.ImageSelection!;
            Render(chrome);
            Assert.True(selection.SelectAt(new Point(100, 30)));
            Assert.True(selection.SelectAt(new Point(310, 70), extend: true));
            BitmapSource after = Render(chrome);
            Assert.Equal("English words\r\nwide    gap", selection.Selection.SelectedText);
            Assert.Equal(original, Pixels(image));
            Assert.False(Pixels(before).SequenceEqual(Pixels(after)));
            if (Environment.GetEnvironmentVariable("MYCAPTURE_PIN_SELECTION_EVIDENCE") == "1")
                await SaveSentenceEvidenceAsync();
        }
        finally { pin.Close(); }

        static async Task SaveSentenceEvidenceAsync()
        {
            const string sentence = "한글 문장에 English words가 함께 있습니다.";
            var visual = new DrawingVisual();
            var words = new List<OcrWord>();
            var columns = new List<OcrWord>();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 480, 180));
                double x = 20;
                foreach (string word in sentence.Split(' '))
                {
                    FormattedText text = Text(word);
                    dc.DrawText(text, new Point(x, 20));
                    words.Add(new OcrWord(word, new OcrRect(x, 20, text.Width, text.Height)));
                    x += Text(word + " ").WidthIncludingTrailingWhitespace;
                }
                foreach ((string word, double left) in new[] { ("wide", 20d), ("gap", 300d) })
                {
                    FormattedText text = Text(word);
                    dc.DrawText(text, new Point(left, 70));
                    columns.Add(new OcrWord(word, new OcrRect(left, 70, text.Width, text.Height)));
                }
            }
            OcrLine Line(string text, List<OcrWord> boxes) => new(text,
                boxes.Select(word => word.Bounds).Aggregate(OcrRect.Union), boxes);
            var result = OcrResult.Success(sentence + "\nwide    gap", "ko",
                [Line(sentence, words), Line("wide    gap", columns)], TimeSpan.Zero);
            var bitmap = new RenderTargetBitmap(480, 180, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            var evidencePin = NewPin(new FakeOcr(_ => Task.FromResult(result)), image: bitmap);
            try
            {
                var chrome = (FrameworkElement)evidencePin.Content;
                BitmapSource before = Render(chrome);
                Press(evidencePin, Key.F2);
                await evidencePin.SelectionWork;
                evidencePin.ForceFeedbackTimeoutForTest();
                evidencePin.TextSelectionForTest!.SelectAll();
                BitmapSource after = Render(chrome);
                Assert.Equal(result.Text, evidencePin.TextSelectionForTest.SelectedOriginalText);
                Assert.Equal(3, PinImageTextSelection.HighlightRuns(
                    evidencePin.TextSelectionForTest.ImageSelection!.Selection.Words, 0, words.Count + columns.Count - 1).Count);
                string directory = OwnedTestDirectory.Create("pin-selection-evidence-");
                TestDiagnosticArtifacts.SavePng(directory, "before.png", before);
                TestDiagnosticArtifacts.SavePng(directory, "after.png", after);
                Console.WriteLine("Selection evidence retained: " + directory);
            }
            finally { evidencePin.Close(); }

            static FormattedText Text(string text) => new(text, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 18, Brushes.Black, 1);
        }

        static BitmapSource Render(FrameworkElement chrome)
        {
            chrome.Measure(new Size(480, 180));
            chrome.Arrange(new Rect(0, 0, 480, 180));
            chrome.UpdateLayout();
            var bitmap = new RenderTargetBitmap(480, 180, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(chrome);
            bitmap.Freeze();
            return bitmap;
        }

        static byte[] Pixels(BitmapSource bitmap)
        {
            byte[] bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
            return bytes;
        }
    });

    internal static OcrResult Layout()
    {
        OcrWord W(string text, double x, double y, double width) => new(text, new OcrRect(x, y, width, 20));
        OcrLine L(string text, params OcrWord[] words) => new(text,
            words.Select(word => word.Bounds).Aggregate(OcrRect.Union), words);
        return OcrResult.Success(FullText, "ko", [
            L("한글 English words", W("한글", 20, 20, 40), W("English", 80, 20, 80), W("words", 180, 20, 60)),
            L("wide    gap", W("wide", 20, 60, 60), W("gap", 300, 60, 50)),
            L("last line", W("last", 20, 100, 50), W("line", 90, 100, 50))], TimeSpan.Zero);
    }

    private static BitmapSource Image()
    {
        BitmapSource image = BitmapSource.Create(480, 180, 96, 96, PixelFormats.Bgra32, null, new byte[480 * 180 * 4], 480 * 4);
        image.Freeze();
        return image;
    }

    private static PinWindow NewPin(FakeOcr ocr, OcrSettings? settings = null, IPinDragInput? input = null, BitmapSource? image = null) =>
        new(PinContent.FromImage(image ?? Image()), new PinViewState(480, 180, 1, 1, .1), 200, 150, dragInput: input,
            recognizeSelection: (source, token) => PinTextCopyService.RecognizeAsync(source, ocr, settings ?? new OcrSettings(), token))
        {
            ShowActivated = false,
            SelectionFocus = new NoFocus(),
        };

    private static void Press(PinWindow pin, Key key, ModifierKeys modifiers = ModifierKeys.None) =>
        pin.HandleKeyDown(new KeyEventArgs(Keyboard.PrimaryDevice, new Source(), 0, key) { RoutedEvent = Keyboard.KeyDownEvent }, modifiers);

    private static void MouseDown(PinWindow pin) => pin.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = UIElement.MouseLeftButtonDownEvent });

    private sealed class Source : PresentationSource
    {
        public override Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private sealed class NoFocus : IPinSelectionFocus
    {
        public bool IsActive(PinWindow owner) => false;
        public void Enter(PinWindow owner, PinTextSelectionView selection) { }
        public void Restore(PinWindow owner) { }
    }

    private sealed class DragInput : IPinDragInput
    {
        internal int Captures;
        private bool _captured;
        public ModifierKeys Modifiers => ModifierKeys.None;
        public (int X, int Y) CursorPosition => (300, 200);
        public Point LocalAnchor(MouseButtonEventArgs args, PinWindow owner) => new(10, 10);
        public bool LeftButtonPressed(MouseEventArgs args) => true;
        public bool TryCapture(PinWindow owner) { Captures++; return _captured = true; }
        public bool HasCapture(PinWindow owner) => _captured;
        public void Release(PinWindow owner) => _captured = false;
    }

    private sealed class FakeOcr(Func<CancellationToken, Task<OcrResult>>? recognize = null) : IOcrService
    {
        internal int Count;
        internal int ThreadId;
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["ko", "en"];
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            ThreadId = Environment.CurrentManagedThreadId;
            return recognize?.Invoke(cancellationToken) ?? Task.FromResult(Layout());
        }
    }

    private static Task RunAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = WpfTestThread.Create(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(true); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
