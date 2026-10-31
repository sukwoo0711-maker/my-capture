using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MyCapture.App.Ocr;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// In the translation tab the footer's main action copies only the translation, the original-and-
/// translation copy sits beside it, and export saves the translation. Other tabs keep their actions.
/// </summary>
public sealed class OcrTranslationFooterTests
{
    [Theory]
    [InlineData("ko", "번역 복사", "원문과 함께 복사")]
    [InlineData("en", "Copy translation", "Copy with original")]
    public void TranslationTabFooterLeadsWithCopyTranslationAndKeepsCopyWithOriginalBesideIt(string language,
        string primaryLabel, string withSourceLabel) => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage(language);
        OcrResultWindow window = TranslatedWindow(out _);
        try
        {
            Button primary = Footer(window, UiText.Get("Translate_CopyHelp"));
            Button withSource = Footer(window, UiText.Get("Translate_CopyWithSource"));
            Assert.Equal(primaryLabel, FaceText(primary));
            Assert.Equal(withSourceLabel, FaceText(withSource));
            Assert.Same(window.FindResource("Button.Primary"), primary.Style);
            Assert.Same(window.FindResource("Button.Secondary"), withSource.Style);
            Assert.True(primary.IsEnabled);
            Assert.True(withSource.IsEnabled);
            // Side by side in the footer, the main action first; the old link above the list is gone.
            Assert.Same(primary.Parent, withSource.Parent);
            var footer = (Panel)primary.Parent;
            Assert.True(footer.Children.IndexOf(primary) < footer.Children.IndexOf(withSource));
            Assert.DoesNotContain(Descendants(window.TranslationPanel).OfType<Button>(),
                button => AutomationProperties.GetName(button) == UiText.Get("Translate_CopyWithSource"));
            // The tooltip says what the button copies now, with its key.
            Assert.Equal($"{UiText.Get("Translate_CopyHelp")} ({AutomationProperties.GetAcceleratorKey(primary)})", primary.ToolTip);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void CopyTranslationCopiesOnlyTheTranslationsAndCopyWithOriginalCopiesBoth() => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage("ko");
        OcrResultWindow window = TranslatedWindow(out _);
        try
        {
            IReadOnlyList<TranslatedBlock> results = window.TranslationPanel.Results;
            Assert.NotEmpty(results);
            long writes = RecordingClipboard.WriteCount;

            Click(Footer(window, UiText.Get("Translate_CopyHelp")));
            Assert.Equal(writes + 1, RecordingClipboard.WriteCount);
            Assert.Equal(string.Join("\n\n", results.Select(result => result.Translation.Text)), LastText());
            // No numbered original lines: the translation alone is on the clipboard.
            Assert.DoesNotContain($"1. {TranslationFixture.Title}", LastText(), StringComparison.Ordinal);
            Assert.StartsWith("KO " + TranslationFixture.Title, LastText(), StringComparison.Ordinal);
            Assert.Equal(UiText.Get("OCR410_Copied"), Status(window));

            Click(Footer(window, UiText.Get("Translate_CopyWithSource")));
            Assert.Equal(writes + 2, RecordingClipboard.WriteCount);
            Assert.Equal(string.Join("\n\n", results.Select(result =>
                $"{result.Block.Number}. {result.Block.Text}\n{result.Translation.Text}")), LastText());
            Assert.StartsWith($"1. {TranslationFixture.Title}\nKO {TranslationFixture.Title}", LastText(), StringComparison.Ordinal);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void ExportInTheTranslationTabSavesOnlyTheTranslationsAsUtf8WithoutABom() => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage("ko");
        string folder = TestRecycleBin.CreateTempSubdirectory("mc-ocr-footer-").FullName;
        string path = Path.Combine(folder, "translation.txt");
        OcrResultWindow window = TranslatedWindow(out _);
        try
        {
            int asked = 0;
            window.TextExportPathChooser = () => { asked++; return path; };
            IReadOnlyList<TranslatedBlock> results = window.TranslationPanel.Results;

            Button export = Footer(window, UiText.Get("Ocr_ExportText"));
            Assert.True(export.IsEnabled);
            Click(export);

            Assert.Equal(1, asked);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "UTF-8 BOM");
            Assert.Equal(string.Join("\n\n", results.Select(result => result.Translation.Text)), System.Text.Encoding.UTF8.GetString(bytes));
            Assert.StartsWith(UiText.Get("Ocr_ExportDone"), Status(window), StringComparison.Ordinal);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void NothingIsCopiedOrExportedBeforeThereIsATranslation() => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage("ko");
        var translator = new OcrTranslationWindowTests.FakeTranslator { Installed = false };
        (var image, OcrResult result) = TranslationFixture.RegisterTableWithImage();
        var window = new OcrResultWindow { ShowInTaskbar = false, Translator = translator };
        Ocr410Fixture.LoadTheme(window);
        try
        {
            window.ShowResult(result, "fixture", image);
            window.SelectExtractionMode(OcrExtractionMode.Translation);
            OcrTranslationWindowTests.PumpUntil(() => window.TranslationWork.IsCompleted);
            Assert.Equal(OcrTranslationPanel.ViewState.ModelNeeded, window.TranslationPanel.State);

            // The footer already names the action; with nothing to copy it waits.
            Button primary = Footer(window, UiText.Get("Translate_CopyHelp"));
            Assert.Equal(UiText.Get("Translate_Copy"), FaceText(primary));
            Assert.False(primary.IsEnabled);
            Assert.False(Footer(window, UiText.Get("Translate_CopyWithSource")).IsEnabled);
            Assert.False(Footer(window, UiText.Get("Ocr_ExportText")).IsEnabled);
            long writes = RecordingClipboard.WriteCount;
            Click(primary);
            window.ShortcutActions["Copy"]();
            Assert.Equal(writes, RecordingClipboard.WriteCount);

            // The translation arrives: the same buttons turn on without a tab change.
            Click(Descendants(window.TranslationPanel).OfType<Button>()
                .First(button => button.Visibility == Visibility.Visible && AutomationProperties.GetName(button) == window.TranslationPanel.PrimaryActionText));
            OcrTranslationWindowTests.PumpUntil(() => window.TranslationWork.IsCompleted);
            Assert.Equal(OcrTranslationPanel.ViewState.Translated, window.TranslationPanel.State);
            Assert.True(primary.IsEnabled);
            Assert.True(Footer(window, UiText.Get("Translate_CopyWithSource")).IsEnabled);
            Assert.True(Footer(window, UiText.Get("Ocr_ExportText")).IsEnabled);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void OtherTabsKeepTheirCopyAndExportAndHideCopyWithOriginal() => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage("ko");
        string folder = TestRecycleBin.CreateTempSubdirectory("mc-ocr-footer-").FullName;
        string path = Path.Combine(folder, "text.txt");
        OcrResultWindow window = TranslatedWindow(out _);
        try
        {
            window.TextExportPathChooser = () => path;
            window.SelectExtractionMode(OcrExtractionMode.Text);
            Ocr410Fixture.Pump();

            Button copy = Footer(window, UiText.Get("Text_20CF6A996B4D"));
            Assert.Equal(UiText.Get("Text_37B3D3B11B26"), FaceText(copy));
            Assert.Equal($"{UiText.Get("Text_20CF6A996B4D")} ({AutomationProperties.GetAcceleratorKey(copy)})", copy.ToolTip);
            Assert.Same(window.FindResource("Button.Primary"), copy.Style);
            Assert.True(copy.IsEnabled);
            Assert.NotEqual(Visibility.Visible, Footer(window, UiText.Get("Translate_CopyWithSource"), visibleOnly: false).Visibility);
            Assert.NotEmpty(window.ExtractionText);
            Assert.DoesNotContain("KO ", window.ExtractionText, StringComparison.Ordinal);

            Click(copy);
            Assert.Equal(window.ExtractionText, LastText());
            Click(Footer(window, UiText.Get("Ocr_ExportText")));
            Assert.Equal(window.ExtractionText, File.ReadAllText(path));

            // Back in the translation tab the translation actions return.
            window.SelectExtractionMode(OcrExtractionMode.Translation);
            Ocr410Fixture.Pump();
            Assert.Equal(UiText.Get("Translate_Copy"), FaceText(Footer(window, UiText.Get("Translate_CopyHelp"))));
            Assert.Equal(Visibility.Visible, Footer(window, UiText.Get("Translate_CopyWithSource")).Visibility);
        }
        finally { window.CloseForExit(); }
    });

    private static OcrResultWindow TranslatedWindow(out OcrTranslationWindowTests.FakeTranslator translator)
    {
        translator = new OcrTranslationWindowTests.FakeTranslator();
        (var image, OcrResult result) = TranslationFixture.RegisterTableWithImage();
        var window = new OcrResultWindow { ShowInTaskbar = false, Translator = translator };
        Ocr410Fixture.LoadTheme(window);
        window.ShowResult(result, "fixture", image);
        window.SelectExtractionMode(OcrExtractionMode.Translation);
        OcrTranslationWindowTests.PumpUntil(() => window.TranslationWork.IsCompleted);
        Assert.Equal(OcrTranslationPanel.ViewState.Translated, window.TranslationPanel.State);
        return window;
    }

    private static Button Footer(OcrResultWindow window, string name, bool visibleOnly = true) =>
        Descendants(window).OfType<Button>().First(button => AutomationProperties.GetName(button) == name
            && (!visibleOnly || button.Visibility == Visibility.Visible));

    private static string FaceText(Button button) => button.Content is Grid { Name: "CommandButtonFace" } face
        ? Assert.IsType<TextBlock>(face.Children[0]).Text : Assert.IsType<string>(button.Content);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static string LastText() =>
        Assert.IsType<string>(RecordingClipboard.LastWrite!.Formats[DataFormats.UnicodeText]);

    private static string Status(OcrResultWindow window) => KoreanWordWrap.Strip(Descendants(window).OfType<TextBlock>()
        .First(block => block.GetType().Name == "LiveTextBlock").Text);

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
