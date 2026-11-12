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
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A pin says what its copy puts on the clipboard: the notice, the copy button's tooltip and automation
/// name, and the menu names. The clipboard itself is a stand-in; nothing reaches the real one.
/// </summary>
public sealed class PinCopyClarityTests
{
    private const string Source = "#define MAX_LEN 256\r\nint value = MAX_LEN;\r\n";

    private sealed record Words(string Language, string Image, string TextImage, string Selected, string ImageOnly,
        string BusyImage, string DoneImage, string FailImage, string BusyTextImage, string DoneTextImage, string FailTextImage,
        string CopyLabel);

    private static readonly Words Korean = new("ko-KR", "이미지 복사", "텍스트와 이미지 복사", "선택한 텍스트 복사", "이미지만 복사",
        "이미지 복사 중…", "이미지 복사됨", "이미지 복사 실패", "텍스트와 이미지 복사 중…", "텍스트와 이미지 복사됨", "텍스트와 이미지 복사 실패", "복사");

    private static readonly Words English = new("en-US", "Copy image", "Copy text and image", "Copy selected text", "Copy image only",
        "Copying image…", "Image copied", "Image copy failed", "Copying text and image…", "Text and image copied", "Text and image copy failed",
        "Copy");

    public static TheoryData<string> Languages => new() { "ko-KR", "en-US" };

    private static Words For(string language) => language == "ko-KR" ? Korean : English;

    [Theory]
    [MemberData(nameof(Languages))]
    public void NoticeNamesWhatTheCopyCarried(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        Words w = For(language);
        PinWindow image = NewPin(null);
        PinWindow text = NewPin(Source);
        try
        {
            image.Show();
            text.Show();

            Press(image, Key.C, ModifierKeys.Control);
            image.AdvanceProgressForTest(FeedbackTiming.ProgressRevealDelay);
            Assert.Equal((w.BusyImage, FeedbackKind.Progress), (image.FeedbackForTest.Text, image.FeedbackForTest.Kind));
            image.ReportCopyResult(true);
            Assert.Equal((w.DoneImage, FeedbackKind.Confirmation), (image.FeedbackForTest.Text, image.FeedbackForTest.Kind));
            image.ReportCopyResult(false);
            Assert.Equal((w.FailImage, FeedbackKind.ActionRequired), (image.FeedbackForTest.Text, image.FeedbackForTest.Kind));

            Press(text, Key.C, ModifierKeys.Control);
            text.AdvanceProgressForTest(FeedbackTiming.ProgressRevealDelay);
            Assert.Equal((w.BusyTextImage, FeedbackKind.Progress), (text.FeedbackForTest.Text, text.FeedbackForTest.Kind));
            text.ReportCopyResult(true);
            Assert.Equal((w.DoneTextImage, FeedbackKind.Confirmation), (text.FeedbackForTest.Text, text.FeedbackForTest.Kind));
            text.ReportCopyResult(false);
            Assert.Equal((w.FailTextImage, FeedbackKind.ActionRequired), (text.FeedbackForTest.Text, text.FeedbackForTest.Kind));

            // What the copy call carried decides the wording, not what the pin happens to show.
            text.ReportCopyResult(true, withText: false);
            Assert.Equal(w.DoneImage, text.FeedbackForTest.Text);
            image.ReportCopyResult(true, withText: true);
            Assert.Equal(w.DoneTextImage, image.FeedbackForTest.Text);
        }
        finally { image.Close(); text.Close(); }
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void ManagerReportsByWhatItPutOnTheClipboard(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        Words w = For(language);
        var copies = new List<string?>();
        var manager = new PinManager(() => new PinSettings(), null,
            (_, _, text) => { copies.Add(text); return Task.FromResult(true); },
            _ => throw new InvalidOperationException("The text-only path must not run."), NullLogger.Instance);
        try
        {
            PinWindow pin = manager.PinRenderedText(Picture(), Source, false);
            pin.Show();

            Press(pin, Key.C, ModifierKeys.Control);
            Assert.True(WaitForCopies(copies, 1));
            Assert.Equal(Source, copies[0]);
            Assert.Equal(w.DoneTextImage, pin.FeedbackForTest.Text);

            ImageOnlyItem(pin).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(WaitForCopies(copies, 2));
            Assert.Null(copies[1]);
            Assert.Equal(w.DoneImage, pin.FeedbackForTest.Text);
        }
        finally { manager.CloseAllForExit(); }
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void CopyButtonTooltipAndAutomationNameFollowWhatIsCopied(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        Words w = For(language);
        foreach (int width in new[] { 480, 120 })
        {
            PinWindow image = NewPin(null, width);
            PinWindow text = NewPin(Source, width);
            try
            {
                image.Show();
                text.Show();
                foreach ((PinWindow pin, string name) in new[] { (image, w.Image), (text, w.TextImage) })
                {
                    pin.ShowHoverBar();
                    PinHoverBar bar = pin.HoverBarForTest!;
                    bar.UpdateLayout();
                    // The visible name and its width never change, whatever is copied.
                    var primary = Assert.IsType<StackPanel>(SurfaceShortcutHintTests.PrimaryContent(bar.CopyButton));
                    Assert.Equal(w.CopyLabel, Assert.Single(primary.Children.OfType<TextBlock>()).Text);
                    Assert.Equal(name, AutomationProperties.GetName(bar.CopyButton));
                    Assert.Equal($"{name} (Ctrl+C)", bar.CopyButton.ToolTip);
                    Assert.Equal($"{name} (Ctrl+C)", AutomationProperties.GetHelpText(bar.CopyButton));
                    pin.HideHoverBar();
                }
                // The narrow pin shows icons only; the same tooltip names the copy.
                Assert.Equal(width == 120, text.HoverBarForTest!.Compact);
            }
            finally { image.Close(); text.Close(); }
        }
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void SelectedTextAndTranslationChangeTheCopyName(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        Words w = For(language);
        PinWindow pin = NewPin(Source);
        try
        {
            pin.Show();
            Press(pin, Key.F2);
            Assert.Equal(w.TextImage, pin.CopyActionName);
            pin.TextSelectionForTest!.TextBox!.Select(8, 7);
            Assert.Equal(w.Selected, pin.CopyActionName);
            // The menu's first copy item copies the selection too, so it says so once the menu opens.
            Assert.Equal(w.TextImage, FirstCopyItem(pin).Header);
            pin.ContextMenu!.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Assert.Equal(w.Selected, FirstCopyItem(pin).Header);
            // The tooltip is read again when it opens, so the selection made after the bar appeared is named.
            Press(pin, Key.Escape);
            Assert.Equal(w.TextImage, pin.CopyActionName);

            pin.ApplyTranslation(PinContent.FromText(Picture(), "translated", false), false);
            Assert.True(pin.IsShowingTranslation);
            Assert.Equal(w.TextImage, pin.CopyActionName);
            Assert.Equal(w.TextImage, FirstCopyItem(pin).Header);
        }
        finally { pin.Close(); }
    });

    [Theory]
    [MemberData(nameof(Languages))]
    public void MenuNamesTheCopyAndOffersImageOnlyWhereTextRidesAlong(string language) => StaTestHost.Run(() =>
    {
        using var culture = UiText.UseLanguage(language);
        Words w = For(language);
        PinWindow image = NewPin(null);
        PinWindow text = NewPin(Source);
        try
        {
            string[] imageHeaders = Headers(image);
            Assert.Equal(w.Image, FirstCopyItem(image).Header);
            Assert.Equal("Ctrl+C", FirstCopyItem(image).InputGestureText);
            Assert.DoesNotContain(w.ImageOnly, imageHeaders);
            Assert.Contains(UiText.Get("Text_03A1596F1681"), imageHeaders);

            string[] textHeaders = Headers(text);
            Assert.Equal(w.TextImage, FirstCopyItem(text).Header);
            Assert.Equal("Ctrl+C", FirstCopyItem(text).InputGestureText);
            int original = Array.IndexOf(textHeaders, UiText.Get("Text_976A6C92B870"));
            Assert.True(original > 0);
            // The image-only copy follows the original-text copy.
            Assert.Equal(w.ImageOnly, textHeaders[original + 1]);

            // While the translation shows, it follows the translation copy instead.
            text.ApplyTranslation(PinContent.FromText(Picture(), "translated", false), false);
            textHeaders = Headers(text);
            int translated = Array.IndexOf(textHeaders, UiText.Get("Pin_CopyTranslation"));
            Assert.True(translated >= 0);
            Assert.Equal(w.ImageOnly, textHeaders[translated + 1]);
            Assert.Single(textHeaders, header => header == w.ImageOnly);
            Assert.Contains(UiText.Get("Text_976A6C92B870"), textHeaders);
        }
        finally { image.Close(); text.Close(); }
    });

    [Fact]
    public void ImageOnlyCopyRequestCarriesNoText() => StaTestHost.Run(() =>
    {
        PinWindow pin = NewPin(Source);
        PinImageCopyRequest? request = null;
        pin.CopyRequested += (_, copy) => request = copy;
        try
        {
            pin.Show();
            ImageOnlyItem(pin).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.NotNull(request);
            Assert.Null(request.Value.OriginalText);
            Assert.NotNull(request.Value.Image);
            request = null;
            Press(pin, Key.C, ModifierKeys.Control);
            Assert.Equal(Source, request!.Value.OriginalText);
        }
        finally { pin.Close(); }
    });

    private static bool WaitForCopies(List<string?> copies, int count)
    {
        for (int i = 0; i < 200 && copies.Count < count; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Background, () => { });
            Thread.Sleep(10);
        }
        // Let the manager's continuation report the result on this dispatcher.
        for (int i = 0; i < 20; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Background, () => { });
            Thread.Sleep(10);
        }
        return copies.Count >= count;
    }

    private static string[] Headers(PinWindow pin) =>
        pin.ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? string.Empty).ToArray();

    private static MenuItem FirstCopyItem(PinWindow pin) =>
        Assert.Single(pin.ContextMenu!.Items.OfType<MenuItem>(), item => item.InputGestureText == "Ctrl+C");

    private static MenuItem ImageOnlyItem(PinWindow pin) => Assert.Single(pin.ContextMenu!.Items.OfType<MenuItem>(),
        item => Equals(item.Header, UiText.Get("Pin_CopyImageOnly")));

    private static BitmapSource Picture()
    {
        var image = BitmapSource.Create(480, 180, 96, 96, PixelFormats.Bgr32, null, new byte[480 * 180 * 4], 480 * 4);
        image.Freeze();
        return image;
    }

    private static PinWindow NewPin(string? source, int width = 480)
    {
        BitmapSource image = Picture();
        PinContent content = source is null ? PinContent.FromImage(image) : PinContent.FromText(image, source, false);
        return new PinWindow(content, new PinViewState(width, 180, 1, 1, 0.1), -10000, -10000, () => new PinSettings())
        {
            ShowActivated = false,
            ReadWorkArea = _ => new RectD(-12000, -12000, 4000, 4000),
        };
    }

    private static void Press(PinWindow pin, Key key, ModifierKeys modifiers = ModifierKeys.None) =>
        pin.HandleKeyDown(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pin), 0, key)
            { RoutedEvent = Keyboard.KeyDownEvent }, modifiers);
}
