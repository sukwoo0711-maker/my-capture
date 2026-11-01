using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MyCapture.App.Ocr;
using MyCapture.Core.Localization;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrSelectionCopyTests
{
    [Theory]
    [InlineData(OcrExtractionMode.Text, "first selected words\nsecond line", "selected words")]
    [InlineData(OcrExtractionMode.Code, "public class Sample {\n    public int Value = 42;\n}", "class Sample")]
    public void CopyButtonUsesSelectionThenWholeTextWhenSelectionIsCleared(
        OcrExtractionMode mode, string source, string selected) => Ocr410Fixture.RunSta(() =>
    {
        var window = CreateWindow();
        try
        {
            window.ShowResult(OcrResult.Success(source, "en-US", [], TimeSpan.Zero), "fixture");
            window.SelectExtractionMode(mode);
            string fullText = window.ResultTextBox.Text;
            int start = fullText.IndexOf(selected, StringComparison.Ordinal);
            Assert.True(start >= 0);
            window.ResultTextBox.Select(start, selected.Length);
            Button copy = ButtonNamed(window, UiText.Get("Text_20CF6A996B4D"));
            long writes = RecordingClipboard.WriteCount;

            Click(copy);

            Assert.Equal(writes + 1, RecordingClipboard.WriteCount);
            Assert.Equal(selected, LastText());
            Assert.Equal(fullText, window.ResultTextBox.Text);

            window.ResultTextBox.Select(start, 0);
            Click(copy);

            Assert.Equal(writes + 2, RecordingClipboard.WriteCount);
            Assert.Equal(fullText, LastText());
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void TranslationCopyButtonUsesCurrentSelectionThenAllTranslations() => Ocr410Fixture.RunSta(() =>
    {
        OcrResultWindow window = CreateTranslatedWindow();
        try
        {
            TextBox[] translations = TranslationTextBoxes(window);
            string allTranslations = window.TranslationPanel.CopyText;
            translations[0].Select(0, 3);
            Button copy = ButtonNamed(window, UiText.Get("Translate_CopyHelp"));
            long writes = RecordingClipboard.WriteCount;

            Click(copy);

            Assert.Equal(writes + 1, RecordingClipboard.WriteCount);
            Assert.Equal("번역된", LastText());
            Assert.Equal(allTranslations, window.TranslationPanel.CopyText);

            translations[0].Select(0, 0);
            Click(copy);

            Assert.Equal(writes + 2, RecordingClipboard.WriteCount);
            Assert.Equal("번역된 첫 문장\n\n둘째 번역 문장", LastText());
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void TranslationCopyUsesLatestBlockSelectionAndDoesNotReviveAnOlderSelection() => Ocr410Fixture.RunSta(() =>
    {
        OcrResultWindow window = CreateTranslatedWindow();
        try
        {
            TextBox[] translations = TranslationTextBoxes(window);
            translations[0].Select(0, 3);
            translations[1].Select(3, 2);
            Button copy = ButtonNamed(window, UiText.Get("Translate_CopyHelp"));

            Click(copy);
            Assert.Equal("번역", LastText());
            Assert.Equal("번역된", translations[0].SelectedText);

            translations[1].Select(0, 0);
            Click(copy);
            Assert.Equal(window.TranslationPanel.CopyText, LastText());
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void NewTranslationResultsForgetThePreviousSelection() => Ocr410Fixture.RunSta(() =>
    {
        OcrResultWindow window = CreateTranslatedWindow();
        try
        {
            TextBox oldTranslation = TranslationTextBoxes(window)[0];
            oldTranslation.Select(0, 3);
            Assert.Equal("번역된", window.TranslationPanel.SelectedText);

            window.TranslationPanel.ShowResults([Translated(1, "New source", "새로운 번역")]);
            Click(ButtonNamed(window, UiText.Get("Translate_CopyHelp")));

            Assert.Empty(window.TranslationPanel.SelectedText);
            Assert.Equal("새로운 번역", LastText());
            Assert.Equal("번역된", oldTranslation.SelectedText);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void TranslationStatusClearsSelectionAndHasNothingToCopy() => Ocr410Fixture.RunSta(() =>
    {
        OcrResultWindow window = CreateTranslatedWindow();
        try
        {
            TranslationTextBoxes(window)[0].Select(0, 3);
            window.TranslationPanel.ShowCancelled();
            long writes = RecordingClipboard.WriteCount;

            window.ShortcutActions["Copy"]();

            Assert.Empty(window.TranslationPanel.SelectedText);
            Assert.Empty(window.TranslationPanel.CopyText);
            Assert.Equal(writes, RecordingClipboard.WriteCount);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void CopyWithOriginalKeepsWholeBlocksWhenATranslationHasSelection() => Ocr410Fixture.RunSta(() =>
    {
        OcrResultWindow window = CreateTranslatedWindow();
        try
        {
            TranslationTextBoxes(window)[0].Select(0, 3);

            Click(ButtonNamed(window, UiText.Get("Translate_CopyWithSource")));

            Assert.Equal(window.TranslationPanel.CopyWithSourceText, LastText());
            Assert.Contains("First source", LastText(), StringComparison.Ordinal);
            Assert.Contains("둘째 번역 문장", LastText(), StringComparison.Ordinal);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void TableCopyCommandsKeepWholeTableDespiteHiddenTextSelection() => Ocr410Fixture.RunSta(() =>
    {
        var window = CreateWindow();
        try
        {
            var (image, result) = Ocr410Fixture.Table();
            window.ShowResult(result, "fixture", image);
            window.SelectExtractionMode(OcrExtractionMode.Table);
            window.ResultTextBox.Select(0, 1);

            Click(ButtonNamed(window, UiText.Get("OCR410_CopyCsv")));

            Assert.Equal(OcrTableExport.Csv(window.Extraction!.Table), LastText());
            Assert.Contains("키보드", LastText(), StringComparison.Ordinal);
        }
        finally { window.CloseForExit(); }
    });

    private static OcrResultWindow CreateWindow()
    {
        var window = new OcrResultWindow { SuppressPresentation = true, ShowInTaskbar = false };
        Ocr410Fixture.LoadTheme(window);
        return window;
    }

    private static OcrResultWindow CreateTranslatedWindow()
    {
        OcrResultWindow window = CreateWindow();
        window.Translator = new OcrTranslationWindowTests.FakeTranslator { Installed = false };
        window.ShowResult(OcrResult.Success("First source\n\nSecond source", "en-US", [], TimeSpan.Zero), "fixture");
        window.SelectExtractionMode(OcrExtractionMode.Translation);
        OcrTranslationWindowTests.PumpUntil(() => window.TranslationWork.IsCompleted);
        window.TranslationPanel.ShowResults([
            Translated(1, "First source", "번역된 첫 문장"),
            Translated(2, "Second source", "둘째 번역 문장"),
        ]);
        return window;
    }

    private static TranslatedBlock Translated(int number, string source, string translation) =>
        new(new TranslationBlock(number, source, default), new TranslatedText(source, translation, [], []));

    private static TextBox[] TranslationTextBoxes(OcrResultWindow window) =>
        Descendants(window.TranslationPanel).OfType<TextBox>().ToArray();

    private static Button ButtonNamed(OcrResultWindow window, string name) => Descendants(window).OfType<Button>()
        .First(button => AutomationProperties.GetName(button) == name);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static string LastText() => Assert.IsType<string>(RecordingClipboard.LastWrite!.Formats[DataFormats.UnicodeText]);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject element) continue;
            yield return element;
            foreach (DependencyObject descendant in Descendants(element)) yield return descendant;
        }
    }
}
