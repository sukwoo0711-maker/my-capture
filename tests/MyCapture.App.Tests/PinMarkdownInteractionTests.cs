using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.Core.Localization;
using MyCapture.Core.Pin;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

// Copy commands terminate in memory. No clipboard, user profile, model or network access.
public sealed class PinMarkdownInteractionTests
{
    private const string Source = "# Release notes\r\n\r\n- Save **your work** before closing.\r\n- Run `npm install`.\r\n";
    private const string Korean = "# 출시 안내\r\n\r\n- 닫기 전에 **작업**을 저장합니다.\r\n- `npm install`을 실행합니다.\r\n";

    [Fact]
    public void MarkdownCopyRemainsExactInRenderedRawAndTranslatedViews() => StaTestHost.Run(() =>
    {
        PinWindow pin = NewPin();
        string? copied = null;
        PinImageCopyRequest? copiedImage = null;
        int imageCopies = 0;
        pin.OriginalTextCopyRequested += (_, text) => copied = text;
        pin.CopyRequested += (_, image) => { imageCopies++; copiedImage = image; };
        try
        {
            Press(pin, Key.C, ModifierKeys.Control);
            Assert.Null(copied);
            Assert.Equal(Source, copiedImage!.Value.OriginalText);
            Assert.Same(pin.PinnedContent.Image, copiedImage.Value.Image);
            pin.SimulateCtrlDoubleClickForTest();
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(Source), System.Text.Encoding.UTF8.GetBytes(copied!));
            pin.SetMarkdownSourceDisplay(true);
            copied = null;
            Press(pin, Key.C, ModifierKeys.Control);
            Assert.Null(copied);
            Assert.Equal(Source, copiedImage.Value.OriginalText);
            Assert.NotSame(pin.PinnedContent.Image, copiedImage.Value.Image);
            pin.SimulateCtrlDoubleClickForTest();
            Assert.Equal(Source, copied);
            pin.SetMarkdownSourceDisplay(false);
            pin.ApplyTranslation(Content(Korean), false);
            Assert.True(pin.IsShowingTranslation);
            copied = null;
            Press(pin, Key.C, ModifierKeys.Control);
            Assert.Null(copied);
            Assert.Equal(Korean, copiedImage.Value.OriginalText);
            Assert.NotSame(pin.PinnedContent.Image, copiedImage.Value.Image);
            pin.SimulateCtrlDoubleClickForTest();
            Assert.Equal(Source, copied);
            pin.CopyTranslationText();
            Assert.Equal(Korean, copied);
            Assert.Equal(3, imageCopies);
            Assert.True(pin.PinnedContent.IsMarkdown);
            Assert.Equal(Source, pin.PinnedContent.OriginalText);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void RawViewAndF2KeepSourceFrameAndMemoryOnlyCopy() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        PinWindow pin = NewPin();
        string? copied = null;
        pin.OriginalTextCopyRequested += (_, text) => copied = text;
        try
        {
            var frame = (pin.Left, pin.Top, pin.Width, pin.Height, pin.State.Zoom, pin.Opacity);
            Press(pin, Key.F2);
            Assert.NotNull(pin.TextSelectionForTest!.MarkdownBox);
            Assert.False(ApplicationCommands.Copy.CanExecute(null, pin.TextSelectionForTest.MarkdownBox));
            Press(pin, Key.A, ModifierKeys.Control);
            Press(pin, Key.C, ModifierKeys.Control);
            Assert.Equal(Source, copied);
            Assert.True(SelectionMenu(pin).IsChecked);
            MenuItem raw = Assert.Single(pin.ContextMenu.Items.OfType<MenuItem>(),
                item => Equals(item.Header, UiText.Get("Pin_MarkdownSourceView")));
            raw.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(pin.IsShowingMarkdownSource);
            Assert.False(pin.IsTextSelectionMode);
            Assert.False(SelectionMenu(pin).IsChecked);
            Press(pin, Key.F2);
            Assert.NotNull(pin.TextSelectionForTest!.TextBox);
            Assert.Null(pin.TextSelectionForTest.MarkdownBox);
            Assert.False(ApplicationCommands.Copy.CanExecute(null, pin.TextSelectionForTest.TextBox));
            copied = null;
            Press(pin, Key.A, ModifierKeys.Control);
            Press(pin, Key.C, ModifierKeys.Control);
            Assert.Equal(Source, copied);
            Assert.True(pin.PinnedContent.IsMarkdown);
            Assert.Equal(Source, pin.PinnedContent.OriginalText);
            Assert.Equal(frame, (pin.Left, pin.Top, pin.Width, pin.Height, pin.State.Zoom, pin.Opacity));
            pin.SetMarkdownSourceDisplay(false);
            Assert.False(pin.IsTextSelectionMode);
            Press(pin, Key.F2);
            Assert.NotNull(pin.TextSelectionForTest!.MarkdownBox);
            Assert.Equal(frame, (pin.Left, pin.Top, pin.Width, pin.Height, pin.State.Zoom, pin.Opacity));
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void RawToManualTranslationAndBackKeepsCanonicalMarkdownAndF2() => StaTestHost.Run(() =>
    {
        PinWindow pin = NewPin();
        try
        {
            pin.SetTranslationState(PinTranslationState.Available);
            pin.SetMarkdownSourceDisplay(true);
            pin.TranslationRequested += _ => pin.ApplyTranslation(Content(Korean), false);
            pin.InvokeTranslationAction();
            Assert.False(pin.IsShowingMarkdownSource);
            Assert.True(pin.IsShowingTranslation);
            Assert.Equal(Korean, pin.DisplayedText);
            Press(pin, Key.F2);
            Assert.NotNull(pin.TextSelectionForTest!.MarkdownBox);
            pin.InvokeTranslationAction();
            Assert.False(pin.IsTextSelectionMode);
            Assert.False(pin.IsShowingTranslation);
            Assert.Equal(Source, pin.DisplayedText);
            Press(pin, Key.F2);
            Assert.NotNull(pin.TextSelectionForTest!.MarkdownBox);
            pin.SetTranslationState(PinTranslationState.Translated);
            Assert.True(SelectionMenu(pin).IsChecked);
            Assert.Same(pin.ContextMenu, pin.TextSelectionForTest.ContextMenu);
            Assert.Equal(Source, pin.PinnedContent.OriginalText);
            Assert.True(pin.PinnedContent.IsMarkdown);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawViewSaveRequestsTheExactDisplayedBitmap(bool saveAs) => StaTestHost.Run(() =>
    {
        PinWindow pin = NewPin();
        PinSaveRequestedEventArgs? request = null;
        pin.SaveRequested += (_, args) => request = args;
        try
        {
            pin.SetMarkdownSourceDisplay(true);
            pin.SimulateDpiChangeForTest(1.5);
            Image image = Assert.Single(Assert.IsType<Grid>(pin.Content).Children.OfType<Image>());
            Assert.NotSame(pin.PinnedContent.Image, image.Source);
            Press(pin, Key.S, ModifierKeys.Control | (saveAs ? ModifierKeys.Shift : ModifierKeys.None));
            PinSaveRequestedEventArgs saved = Assert.IsType<PinSaveRequestedEventArgs>(request);
            Assert.Equal(saveAs ? PinSaveMode.SaveAs : PinSaveMode.QuickSave, saved.Mode);
            Assert.Same(image.Source, saved.Image);
            Assert.Equal(Source, pin.PinnedContent.OriginalText);
            Assert.True(pin.PinnedContent.IsMarkdown);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void ResultArrivingAfterChoosingRawViewDoesNotReplaceTheView() => StaTestHost.Run(() =>
    {
        PinWindow pin = NewPin();
        try
        {
            pin.SetTranslationState(PinTranslationState.Translating);
            pin.SetMarkdownSourceDisplay(true);
            pin.ApplyTranslation(Content(Korean), false);
            Assert.True(pin.IsShowingMarkdownSource);
            Assert.False(pin.IsShowingTranslation);
            pin.InvokeTranslationAction();
            Assert.False(pin.IsShowingMarkdownSource);
            Assert.True(pin.IsShowingTranslation);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void ClosingRawViewRestoresTheOriginalMarkdownKindAndPayload() => StaTestHost.Run(() =>
    {
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance)
        {
            ShowPinForTest = static (_, _) => { }, ActivatePinForTest = static _ => { },
        };
        try
        {
            manager.PasteFromClipboardAsync(() => Task.FromResult(ClipboardImageReader.PinReadAttempt.Success(Content(Source))))
                .GetAwaiter().GetResult();
            PinWindow pin = Assert.Single(manager.SnapshotOpenPinWindows());
            pin.ApplyTranslation(Content(Korean), false);
            pin.SetMarkdownSourceDisplay(true);
            pin.Close();
            PinWindow restored = Assert.IsType<PinWindow>(manager.RestoreLastClosed());
            Assert.True(restored.PinnedContent.IsMarkdown);
            Assert.Equal(Source, restored.PinnedContent.OriginalText);
            Assert.Equal(Source, restored.DisplayedText);
            Assert.False(restored.IsShowingMarkdownSource);
            Assert.False(restored.IsShowingTranslation);
        }
        finally { manager.CloseAll(); }
    });

    private static PinContent Content(string source) => PinContent.FromMarkdown(ClipboardMarkdownRenderer.Render(source), source);

    private static PinWindow NewPin()
    {
        PinContent content = Content(Source);
        return new PinWindow(content, new PinViewState(content.LayoutWidthDip, content.LayoutHeightDip, 1, 1, 0.1), -10000, -10000,
            () => new PinSettings()) { SelectionFocus = new NoFocus() };
    }

    private static MenuItem SelectionMenu(PinWindow pin) => Assert.Single(pin.ContextMenu.Items.OfType<MenuItem>(), item => item.InputGestureText == "F2");

    private static void Press(PinWindow pin, Key key, ModifierKeys modifiers = ModifierKeys.None) =>
        pin.HandleKeyDown(new KeyEventArgs(Keyboard.PrimaryDevice, new MemoryPresentationSource(), 0, key)
            { RoutedEvent = Keyboard.KeyDownEvent }, modifiers);

    private sealed class MemoryPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = new DrawingVisual();
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private sealed class NoFocus : IPinSelectionFocus
    {
        public bool IsActive(PinWindow owner) => false;
        public void Enter(PinWindow owner, PinTextSelectionView selection) { }
        public void Restore(PinWindow owner) { }
    }
}
