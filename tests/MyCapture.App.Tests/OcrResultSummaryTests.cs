using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using MyCapture.App.Ocr;
using MyCapture.Core.Localization;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The text recognition window names the language in words with the line count, and its
/// actions run strongest first: Copy, export, then the quiet rerun.
/// </summary>
public sealed class OcrResultSummaryTests
{
    [Theory]
    [InlineData("ko-KR", "ko", "public void Capture()\n{\n    Save();\n}\n검토용 캡처", "한국어 · 5줄")]
    [InlineData("en", "ko", "public void Capture()\n{\n    Save();\n}\n검토용 캡처", "Korean · 5 lines")]
    [InlineData("ko-KR", "en-US", "Total\r\n\r\n128,400", "영어 · 2줄")]
    [InlineData("en", "ja", "請求書", "Japanese · 1 line")]
    [InlineData("ko-KR", "", "one\ntwo", "2줄")]
    public void SubtitleNamesTheLanguageAndCountsTheLines(string language, string tag, string text, string expected)
    {
        using var scope = UiText.UseLanguage(language);
        Assert.Equal(expected, OcrResultWindow.ResultSummary(OcrResult.Success(text, tag, lines: [], elapsed: TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("ko-KR", "ko-KR", "한국어")]
    [InlineData("ko-KR", "zh-Hans-CN", "중국어(간체)")]
    [InlineData("ko-KR", "zh-TW", "중국어(번체)")]
    [InlineData("en", "zh-Hant", "Chinese (Traditional)")]
    [InlineData("en", "de-DE", "German")]
    [InlineData("ko-KR", "de-DE", "Deutsch")]
    public void LanguageTagsReadAsNames(string language, string tag, string expected)
    {
        using var scope = UiText.UseLanguage(language);
        Assert.Equal(expected, OcrResultWindow.LanguageName(tag));
    }

    [Fact]
    public void UnknownOrMissingLanguageIsLeftOut()
    {
        Assert.Null(OcrResultWindow.LanguageName(null));
        Assert.Null(OcrResultWindow.LanguageName(" "));
        Assert.Null(OcrResultWindow.LanguageName("not a tag!"));
    }

    [Fact]
    public void RecognitionWindowShowsSummaryAndAccuracyBeforeCopyWithRerunLast() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var window = new OcrResultWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false,
        };
        try
        {
            Ocr410Fixture.LoadTheme(window);
            window.ShowResult(OcrResult.Success("첫 줄\n둘째 줄", "ko", lines: [], elapsed: TimeSpan.Zero), "Capture");
            // The accuracy selector is built after the first render; this window is never shown.
            window.CompleteAccuracyPresentation();
            Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "한국어 · 2줄");
            Assert.DoesNotContain(Descendants(window).OfType<TextBlock>(), text => text.Text.Contains("· ko", StringComparison.Ordinal));

            Button copy = Descendants(window).OfType<Button>().Single(button => AutomationProperties.GetName(button) == UiText.Get("Text_20CF6A996B4D"));
            var footer = (WrapPanel)copy.Parent;
            // The shown buttons; copy with the original belongs to the translation tab and stays hidden here.
            Button[] buttons = [.. footer.Children.OfType<Button>().Where(button => button.Visibility == Visibility.Visible)];
            string[] order = [.. buttons.Select(FaceLabel)];
            Assert.Equal(["복사", "텍스트 내보내기", "다시 인식"], order);
            Assert.Equal(Visibility.Collapsed, footer.Children.OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == UiText.Get("Translate_CopyWithSource")).Visibility);
            Assert.Same(window.AccuracySelector,
                Assert.IsType<StackPanel>(footer.Children[0]).Children.OfType<ComboBox>().Single());
            Assert.Equal(UiText.Get("Ocr_Accuracy"), AutomationProperties.GetName(window.AccuracySelector));
            Assert.Equal(1, footer.Children.IndexOf(copy));
            // One primary in the group; export is the secondary, rerun the quiet one.
            Assert.Same(window.FindResource("Button.Primary"), buttons[0].Style);
            Assert.Same(window.FindResource("Button.Secondary"), buttons[1].Style);
            Assert.Same(window.FindResource("Button.Ghost"), buttons[2].Style);

            // Table mode keeps the order: its primary copy leads, rerun stays last.
            var table = footer.Children.OfType<WrapPanel>().Single();
            Assert.Equal(UiText.Get("OCR410_CopyTsv"), FaceLabel((Button)table.Children[0]));
            Assert.Same(window.FindResource("Button.Primary"), ((Button)table.Children[0]).Style);
            Assert.True(footer.Children.IndexOf(table) < footer.Children.IndexOf(buttons[2]));

            // The read-only result keeps the input method disconnected.
            TextBox result = Descendants(window).OfType<TextBox>().Single(box => box.IsReadOnly);
            Assert.False(InputMethod.GetIsInputMethodEnabled(result));
        }
        finally { window.CloseForExit(); }
    });

    private static string FaceLabel(Button button) =>
        Assert.IsType<TextBlock>(Assert.IsType<Grid>(button.Content).Children[0]).Text;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            yield return node;
            foreach (DependencyObject descendant in Descendants(node)) yield return descendant;
        }
    }
}
