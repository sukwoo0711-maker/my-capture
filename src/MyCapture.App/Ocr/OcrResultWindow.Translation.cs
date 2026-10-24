using System.IO;
using System.Windows;
using System.Windows.Controls;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Ocr;

/// <summary>
/// The translation view: English blocks of the current extraction translated into Korean on
/// this PC, next to numbered outlines on the original. Nothing is downloaded until the user
/// turns translation on in the view, which shows the model size first.
/// </summary>
internal sealed partial class OcrResultWindow
{
    private OcrTranslationPanel _translationPanel = null!;
    private TabItem _translationTab = null!;
    private ITextTranslator? _translator;
    private CancellationTokenSource? _translationWork;
    private OcrExtraction? _translatedExtraction;
    private IReadOnlyList<TranslationBlock> _translationBlocks = [];
    private long _translationVersion;

    /// <summary>Translates recognized English; without one the translation view stays hidden.</summary>
    internal ITextTranslator? Translator
    {
        get => _translator;
        set
        {
            ResetTranslation();
            _translator = value;
            _translationTab.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
            if (value is null && ExtractionMode == OcrExtractionMode.Translation) _tabs.SelectedIndex = (int)OcrExtractionMode.Text;
        }
    }

    internal OcrTranslationPanel TranslationPanel => _translationPanel;

    /// <summary>The latest model check, download or translation, for tests to await.</summary>
    internal Task TranslationWork { get; private set; } = Task.CompletedTask;

    private OcrTranslationPanel BuildTranslationPanel()
    {
        _translationPanel = new OcrTranslationPanel { Visibility = Visibility.Collapsed };
        _translationPanel.DownloadRequested += (_, _) =>
        {
            if (_translator is null) return;
            _translationPanel.ShowDownloading(0, _translator.ModelDownloadBytes);
            StartTranslationWork(DownloadAndTranslateAsync);
        };
        _translationPanel.RetryRequested += (_, _) =>
        {
            _translationPanel.ShowChecking();
            StartTranslationWork(CheckAndTranslateAsync);
        };
        _translationPanel.CancelRequested += (_, _) => _translationWork?.Cancel();
        _translationPanel.CopyWithSourceRequested += (_, _) => CopyValue(_translationPanel.CopyWithSourceText);
        _translationPanel.BlockFocused += (_, index) => HighlightBlock(index);
        return _translationPanel;
    }

    /// <summary>Starts work for a new extraction; the same extraction keeps its translation.</summary>
    private void EnsureTranslation()
    {
        if (_translator is null || _currentExtraction is null) return;
        if (ReferenceEquals(_translatedExtraction, _currentExtraction)
            && _translationPanel.State is not (OcrTranslationPanel.ViewState.Idle or OcrTranslationPanel.ViewState.Cancelled))
            return;
        CancelTranslationWork();
        _translatedExtraction = _currentExtraction;
        _translationBlocks = TranslationBlocks.Build(_currentExtraction);
        if (_translationBlocks.Count == 0)
        {
            _translationPanel.ShowNothingToTranslate();
            return;
        }
        _translationPanel.ShowChecking();
        StartTranslationWork(CheckAndTranslateAsync);
    }

    /// <summary>
    /// Runs the work as an operation of this window's dispatcher, so its continuations and progress
    /// reports come back to the window's thread even when the caller has no synchronization context.
    /// </summary>
    private void StartTranslationWork(Func<ITextTranslator, long, CancellationToken, Task> work)
    {
        if (_translator is null) return;
        CancelTranslationWork();
        var cancellation = new CancellationTokenSource();
        _translationWork = cancellation;
        long version = ++_translationVersion;
        ITextTranslator translator = _translator;
        TranslationWork = Dispatcher.InvokeAsync(() => IsCurrent(version)
            ? work(translator, version, cancellation.Token) : Task.CompletedTask).Task.Unwrap();
    }

    private async Task CheckAndTranslateAsync(ITextTranslator translator, long version, CancellationToken token)
    {
        bool installed;
        try
        {
            installed = await translator.IsModelInstalledAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(version)) _translationPanel.ShowCancelled();
            return;
        }
        catch (Exception)
        {
            if (IsCurrent(version)) _translationPanel.ShowFailed(UiText.Get("Translate_LoadFailed"));
            return;
        }

        if (!IsCurrent(version)) return;
        if (!installed)
        {
            _translationPanel.ShowModelNeeded(translator.ModelDownloadBytes);
            return;
        }
        await TranslateBlocksAsync(translator, version, token).ConfigureAwait(true);
    }

    private async Task DownloadAndTranslateAsync(ITextTranslator translator, long version, CancellationToken token)
    {
        var progress = new Progress<TranslationDownloadProgress>(update =>
        {
            if (IsCurrent(version) && _translationPanel.State == OcrTranslationPanel.ViewState.Downloading)
                _translationPanel.ShowDownloading(update.ReceivedBytes, update.TotalBytes);
        });
        try
        {
            await translator.DownloadModelAsync(progress, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (IsCurrent(version)) _translationPanel.ShowModelNeeded(translator.ModelDownloadBytes);
            return;
        }
        catch (Exception ex)
        {
            // Includes a request timeout, which surfaces as a cancellation the user did not ask for.
            if (IsCurrent(version)) _translationPanel.ShowDownloadFailed(DownloadFailureMessage(ex, translator.ModelDownloadBytes));
            return;
        }

        if (IsCurrent(version)) await TranslateBlocksAsync(translator, version, token).ConfigureAwait(true);
    }

    private async Task TranslateBlocksAsync(ITextTranslator translator, long version, CancellationToken token)
    {
        IReadOnlyList<TranslationBlock> blocks = _translationBlocks;
        _translationPanel.ShowTranslating(0, blocks.Count);
        var progress = new Progress<TranslationProgress>(update =>
        {
            if (IsCurrent(version) && _translationPanel.State == OcrTranslationPanel.ViewState.Translating)
                _translationPanel.ShowTranslating(update.Completed, update.Total);
        });
        IReadOnlyList<TranslatedText> translations;
        try
        {
            translations = await translator.TranslateAsync([.. blocks.Select(block => block.Text)], progress, token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(version)) _translationPanel.ShowCancelled();
            return;
        }
        catch (Exception)
        {
            // A model that cannot be opened is reported in the view; the window itself stays usable.
            if (IsCurrent(version)) _translationPanel.ShowFailed(UiText.Get("Translate_LoadFailed"));
            return;
        }

        if (!IsCurrent(version)) return;
        _translationPanel.ShowResults([.. blocks.Zip(translations, (block, text) => new TranslatedBlock(block, text))]);
        if (ExtractionMode == OcrExtractionMode.Translation)
        {
            ShowTranslationRegions();
            _textBox.Text = _translationPanel.CopyText;
            bool hasContent = _textBox.Text.Length > 0;
            _copyButton.IsEnabled = hasContent;
            _plainExportButton.IsEnabled = hasContent;
        }
    }

    /// <summary>
    /// A click on a numbered block shows its translation in the list; dragging a new area still
    /// narrows the view like the other modes.
    /// </summary>
    private bool RevealTranslationBlock(OcrRect? selection)
    {
        int index = selection is OcrRect picked ? _translationBlocks.ToList().FindIndex(block => block.Region == picked) : -1;
        if (index < 0) return false;
        _imageOverlay.SetSelection(null, notify: false);
        HighlightBlock(index);
        _translationPanel.RevealBlock(index);
        return true;
    }

    /// <summary>Outlines the blocks to translate; their numbers appear once the numbered list does.</summary>
    private void ShowTranslationRegions()
    {
        bool numbered = _translationPanel.State == OcrTranslationPanel.ViewState.Translated;
        _imageOverlay.SetRegions(_translationBlocks.Select(block => block.Region), numbered
            ? [.. _translationBlocks.Select(block => block.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))]
            : null);
    }

    /// <summary>Outlines a block more strongly; blocks without a valid region have no outline.</summary>
    private void HighlightBlock(int index)
    {
        int region = index >= 0 && index < _translationBlocks.Count
            ? _imageOverlay.Regions.ToList().IndexOf(_translationBlocks[index].Region) : -1;
        _imageOverlay.HighlightedRegion = region < 0 ? null : region;
    }

    private bool IsCurrent(long version) => version == _translationVersion;

    private void CancelTranslationWork()
    {
        _translationVersion++;
        CancellationTokenSource? work = _translationWork;
        _translationWork = null;
        work?.Cancel();
    }

    /// <summary>Forgets the translation of the previous result, cancelling any work for it.</summary>
    private void ResetTranslation()
    {
        CancelTranslationWork();
        _translatedExtraction = null;
        _translationBlocks = [];
        _translationPanel.ShowIdle();
        _imageOverlay.HighlightedRegion = null;
    }

    internal static string DownloadFailureMessage(Exception error, long downloadBytes) => error switch
    {
        TranslationModelException { Error: TranslationModelError.DiskFull } =>
            UiText.Format("Translate_ErrorDiskFull", Math.Ceiling(downloadBytes / 1_000_000d)),
        TranslationModelException { Error: TranslationModelError.Corrupt } or System.Security.Cryptography.CryptographicException =>
            UiText.Get("Translate_ErrorCorrupt"),
        TranslationModelException { Error: TranslationModelError.Disk } or IOException or UnauthorizedAccessException =>
            UiText.Get("Translate_ErrorDisk"),
        _ => UiText.Get("Translate_ErrorNetwork"),
    };
}
