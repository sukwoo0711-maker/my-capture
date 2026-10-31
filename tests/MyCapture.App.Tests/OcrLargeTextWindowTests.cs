using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using MyCapture.App.Ocr;
using MyCapture.Ocr;
using Xunit;
using static MyCapture.App.Tests.EditorKeysTests;

namespace MyCapture.App.Tests;

public sealed class OcrLargeTextWindowTests
{
    [Fact]
    public void LargeEditableResultKeepsFullSelectionAndAutomationTextWithoutImeRangeQueries() => Ocr410Fixture.RunSta(() =>
    {
        var window = new OcrResultWindow();
        Ocr410Fixture.LoadTheme(window);
        try
        {
            string content = string.Join('\n', Enumerable.Repeat("전체 원문 한글😀 abcXYZ0123456789", 4000));
            Assert.True(content.Length >= 100_000);
            window.ShowResult(OcrResult.Success(content, "ko-KR", [], TimeSpan.Zero), "Synthetic large result");
            window.UpdateLayout();
            TextBox box = Field<TextBox>(window, "_textBox");
            Assert.Equal(content, window.ExtractionText);
            Assert.Equal(content, box.SelectedText);
            Assert.False(box.IsReadOnly);
            // The opening select-all is above the limit, so TSF never measures it.
            Assert.False(InputMethod.GetIsInputMethodEnabled(box));
            var scroll = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
            Assert.True(scroll.ScrollableHeight > 0);
            var peer = new TextBoxAutomationPeer(box);
            var provider = Assert.IsAssignableFrom<ITextProvider>(peer.GetPattern(PatternInterface.Text));
            Assert.Equal(content, provider.DocumentRange.GetText(-1));
            box.Select(0, 2);
            Assert.Equal(content[..2], box.SelectedText);
            Assert.Equal(content[..2], Assert.Single(provider.GetSelection()).GetText(-1));
            Assert.True(InputMethod.GetIsInputMethodEnabled(box));
            box.SelectAll();
            Assert.Equal(content, box.SelectedText);
            Assert.False(InputMethod.GetIsInputMethodEnabled(box));
            Assert.True(window.ShortcutActions.ContainsKey("Copy"));
            box.Select(0, OcrResultWindow.InputMethodSelectionLimit);
            Assert.True(InputMethod.GetIsInputMethodEnabled(box));
            box.Select(0, OcrResultWindow.InputMethodSelectionLimit + 1);
            Assert.False(InputMethod.GetIsInputMethodEnabled(box));
            box.Select(0, 2);
            Assert.Equal(content[..2], box.SelectedText);
            Assert.True(InputMethod.GetIsInputMethodEnabled(box));
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void ReopeningResultKeepsSelectionContractAndDoesNotDisableEditableControls() => Ocr410Fixture.RunSta(() =>
    {
        var window = new OcrResultWindow();
        Ocr410Fixture.LoadTheme(window);
        try
        {
            const string content = "선택 내용😀\n두 번째 줄";
            OcrResult result = OcrResult.Success(content, "ko-KR", [], TimeSpan.Zero);
            window.ShowResult(result, "Synthetic");
            TextBox box = Field<TextBox>(window, "_textBox");
            box.Select(0, 2);
            window.ShowResult(result, "Synthetic geometry refresh", preserveSelection: true);
            Assert.Equal(content, box.SelectedText);
            Assert.Equal(content, box.Text);
            window.ShowResult(result, "Synthetic new result");
            Assert.Equal(content, box.SelectedText);
            var editable = new TextBox { Text = "한글 입력" };
            Assert.False(editable.IsReadOnly);
            Assert.True(InputMethod.GetIsInputMethodEnabled(editable));
        }
        finally { window.CloseForExit(); }
    });
}
