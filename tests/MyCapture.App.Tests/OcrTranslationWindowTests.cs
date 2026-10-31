using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Ocr;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrTranslationWindowTests
{
    [Fact]
    public void TranslationTabStaysHiddenWithoutATranslator() => Ocr410Fixture.RunSta(() =>
    {
        var window = new OcrResultWindow { ShowInTaskbar = false };
        Ocr410Fixture.LoadTheme(window);
        try
        {
            TabItem tab = TranslationTab(window);
            Assert.Equal(Visibility.Collapsed, tab.Visibility);
            window.Translator = new FakeTranslator();
            Assert.Equal(Visibility.Visible, tab.Visibility);
        }
        finally { window.CloseForExit(); }
    });

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public void NothingDownloadsUntilTheUserTurnsTranslationOnAfterSeeingTheSize(string language) => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage(language);
        var translator = new FakeTranslator { Installed = false };
        var (image, result) = TranslationFixture.RegisterTableWithImage();
        var window = new OcrResultWindow { ShowInTaskbar = false, Translator = translator };
        Ocr410Fixture.LoadTheme(window);
        try
        {
            window.ShowResult(result, "fixture", image);
            window.SelectExtractionMode(OcrExtractionMode.Translation);
            PumpUntil(() => window.TranslationWork.IsCompleted);

            Assert.Equal(OcrTranslationPanel.ViewState.ModelNeeded, window.TranslationPanel.State);
            Assert.Contains("246", window.TranslationPanel.MessageBody, StringComparison.Ordinal);
            Assert.Contains("246", window.TranslationPanel.PrimaryActionText, StringComparison.Ordinal);
            Assert.Equal(0, translator.Downloads);
            Assert.Equal(0, translator.Translations);
            Assert.False(Button(window, UiText.Get("Translate_CopyHelp")).IsEnabled);
            // The blocks to translate are outlined; numbers wait for the numbered list.
            Assert.NotEmpty(window.ImageOverlay.Regions);
            Assert.Empty(window.ImageOverlay.Labels);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void TurningTranslationOnDownloadsThenListsNumberedBlocksBesideTheOriginal() => Ocr410Fixture.RunSta(() =>
    {
        using var culture = UiText.UseLanguage("ko");
        var translator = new FakeTranslator { Installed = false };
        var (image, result) = TranslationFixture.RegisterTableWithImage();
        var window = new OcrResultWindow { ShowInTaskbar = false, Translator = translator };
        Ocr410Fixture.LoadTheme(window);
        try
        {
            window.ShowResult(result, "fixture", image);
            window.SelectExtractionMode(OcrExtractionMode.Translation);
            PumpUntil(() => window.TranslationWork.IsCompleted);

            Click(PanelButton(window, window.TranslationPanel.PrimaryActionText));
            PumpUntil(() => window.TranslationWork.IsCompleted);

            Assert.Equal(1, translator.Downloads);
            Assert.Equal(OcrTranslationPanel.ViewState.Translated, window.TranslationPanel.State);
            IReadOnlyList<TranslatedBlock> results = window.TranslationPanel.Results;
            Assert.Equal(TranslationFixture.Title, results[0].Block.Text);
            Assert.Equal("KO " + TranslationFixture.Title, results[0].Translation.Text);
            Assert.Equal(results.Select(item => item.Block.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                window.ImageOverlay.Labels);
            Assert.Equal(results.Count, window.ImageOverlay.Regions.Count);
            Assert.Equal(string.Join("\n\n", results.Select(item => item.Translation.Text)), window.ExtractionText);
            Assert.True(Button(window, UiText.Get("Translate_CopyHelp")).IsEnabled);

            // A block whose translation lost a value carries a note for the reader.
            TranslatedBlock flagged = Assert.Single(results, item => item.Translation.NeedsReview);
            Assert.Contains(UiText.Format("Translate_WarningMissing", "CEN"), TextOf(window.TranslationPanel), StringComparison.Ordinal);
            Assert.StartsWith("Counter enable.", flagged.Block.Text, StringComparison.Ordinal);

            // A click on a numbered block shows it in the list instead of narrowing the view.
            window.ImageOverlay.SetSelection(results[2].Block.Region);
            Assert.Null(window.ImageOverlay.Selection);
            Assert.Equal(2, window.ImageOverlay.HighlightedRegion);
            Assert.Same(results, window.TranslationPanel.Results);

            // Other views keep their own text and regions.
            window.SelectExtractionMode(OcrExtractionMode.Text);
            Assert.Empty(window.ImageOverlay.Labels);
            Assert.Contains("Auto-reload preload enable.", window.ExtractionText, StringComparison.Ordinal);
            window.SelectExtractionMode(OcrExtractionMode.Translation);
            Assert.Equal(1, translator.Translations);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void CancellingTheDownloadReturnsToTheConsent() => Ocr410Fixture.RunSta(() =>
    {
        var translator = new FakeTranslator { Installed = false, HoldDownload = true };
        var (image, result) = TranslationFixture.RegisterTableWithImage();
        var window = new OcrResultWindow { ShowInTaskbar = false, Translator = translator };
        Ocr410Fixture.LoadTheme(window);
        try
        {
            window.ShowResult(result, "fixture", image);
            window.SelectExtractionMode(OcrExtractionMode.Translation);
            PumpUntil(() => window.TranslationWork.IsCompleted);
            Click(PanelButton(window, window.TranslationPanel.PrimaryActionText));
            PumpUntil(() => window.TranslationPanel.State == OcrTranslationPanel.ViewState.Downloading && translator.Downloads == 1);

            Click(PanelButton(window, UiText.Get("Dialog.Cancel")));
            PumpUntil(() => window.TranslationWork.IsCompleted);

            Assert.Equal(OcrTranslationPanel.ViewState.ModelNeeded, window.TranslationPanel.State);
            Assert.Equal(0, translator.Translations);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void FailedDownloadExplainsTheCauseAndOffersARetry() => Ocr410Fixture.RunSta(() =>
    {
        var translator = new FakeTranslator { Installed = false, DownloadError = new TranslationModelException(TranslationModelError.DiskFull, "full") };
        var (image, result) = TranslationFixture.RegisterTableWithImage();
        var window = new OcrResultWindow { ShowInTaskbar = false, Translator = translator };
        Ocr410Fixture.LoadTheme(window);
        try
        {
            window.ShowResult(result, "fixture", image);
            window.SelectExtractionMode(OcrExtractionMode.Translation);
            PumpUntil(() => window.TranslationWork.IsCompleted);
            Click(PanelButton(window, window.TranslationPanel.PrimaryActionText));
            PumpUntil(() => window.TranslationWork.IsCompleted);

            Assert.Equal(OcrTranslationPanel.ViewState.DownloadFailed, window.TranslationPanel.State);
            Assert.Equal(UiText.Format("Translate_ErrorDiskFull", 246), window.TranslationPanel.MessageBody);
            Assert.Equal(UiText.Get("Translate_Retry"), window.TranslationPanel.PrimaryActionText);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void KoreanTableHasNothingToTranslate() => Ocr410Fixture.RunSta(() =>
    {
        var translator = new FakeTranslator();
        var (image, result) = Ocr410Fixture.Table();
        var window = new OcrResultWindow { ShowInTaskbar = false, Translator = translator };
        Ocr410Fixture.LoadTheme(window);
        try
        {
            window.ShowResult(result, "fixture", image);
            window.SelectExtractionMode(OcrExtractionMode.Translation);

            Assert.Equal(OcrTranslationPanel.ViewState.NothingToTranslate, window.TranslationPanel.State);
            Assert.Empty(window.ImageOverlay.Regions);
            Assert.Equal(0, translator.Translations);
        }
        finally { window.CloseForExit(); }
    });

    [Fact]
    public void ANewResultCancelsTheTranslationOfThePreviousOne() => Ocr410Fixture.RunSta(() =>
    {
        var translator = new FakeTranslator { HoldTranslation = true };
        var (image, result) = TranslationFixture.RegisterTableWithImage();
        var window = new OcrResultWindow { ShowInTaskbar = false, Translator = translator };
        Ocr410Fixture.LoadTheme(window);
        try
        {
            window.ShowResult(result, "fixture", image);
            window.SelectExtractionMode(OcrExtractionMode.Translation);
            PumpUntil(() => window.TranslationPanel.State == OcrTranslationPanel.ViewState.Translating && translator.Translations == 1);
            Task first = window.TranslationWork;

            window.ShowBusy("next");
            PumpUntil(() => first.IsCompleted);

            Assert.True(translator.LastToken.IsCancellationRequested);
            Assert.Equal(OcrTranslationPanel.ViewState.Idle, window.TranslationPanel.State);
            Assert.Empty(window.TranslationPanel.Results);
        }
        finally { window.CloseForExit(); }
    });

    internal static void PumpUntil(Func<bool> condition, int timeoutMilliseconds = 10_000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMilliseconds) throw new TimeoutException("The translation view did not settle.");
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        Ocr410Fixture.Pump();
    }

    private static TabItem TranslationTab(OcrResultWindow window) =>
        Descendants(window).OfType<TabItem>().Single(tab => AutomationProperties.GetName(tab) == UiText.Get("Translate_Tab"));

    private static Button Button(DependencyObject root, string name) =>
        Descendants(root).OfType<Button>().First(button => AutomationProperties.GetName(button) == name);

    private static Button PanelButton(OcrResultWindow window, string name) =>
        Descendants(window.TranslationPanel).OfType<Button>()
            .First(button => button.Visibility == Visibility.Visible && AutomationProperties.GetName(button) == name);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private static string TextOf(DependencyObject root) => string.Join("\n", Descendants(root).OfType<TextBlock>().Select(block => KoreanWordWrap.Strip(block.Text)));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject element) continue;
            yield return element;
            foreach (DependencyObject descendant in Descendants(element)) yield return descendant;
        }
    }

    internal sealed class FakeTranslator : ITextTranslator
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Installed { get; set; } = true;

        internal bool HoldDownload { get; init; }

        internal bool HoldTranslation { get; init; }

        internal Exception? DownloadError { get; init; }

        internal int Downloads { get; private set; }

        internal int Translations { get; private set; }

        internal CancellationToken LastToken { get; private set; }

        public long ModelDownloadBytes => 245_400_000;

        public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken) => Task.FromResult(Installed);

        public async Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            Downloads++;
            LastToken = cancellationToken;
            progress?.Report(new TranslationDownloadProgress(100_000_000, ModelDownloadBytes));
            if (HoldDownload) await _release.Task.WaitAsync(cancellationToken);
            if (DownloadError is not null) throw DownloadError;
            Installed = true;
        }

        public async Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
            IProgress<TranslationProgress>? progress, CancellationToken cancellationToken)
        {
            Translations++;
            LastToken = cancellationToken;
            progress?.Report(new TranslationProgress(0, sources.Count));
            if (HoldTranslation) await _release.Task.WaitAsync(cancellationToken);
            await Task.Yield();
            return [.. sources.Select(source => new TranslatedText(source, "KO " + source,
                source.StartsWith("Counter enable", StringComparison.Ordinal) ? ["CEN"] : [], []))];
        }
    }
}
