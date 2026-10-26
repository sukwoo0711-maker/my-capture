using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Themes;

namespace MyCapture.App.Pinning;

internal enum PinTranslationState { Unavailable, Available, Disabled, Recognizing, Checking, ModelNeeded, Downloading, Translating, Translated, Failed }

internal sealed partial class PinWindow
{
    private PinContent? _translationOriginal;
    private PinContent? _translationResult;
    private bool _showingTranslation;
    private bool _translationNeedsReview;
    private string? _translationReviewMessage;
    private long _translationDownloadBytes;
    private bool _showingMarkdownSource;
    private PinContent? _markdownSourceContent;
    private string? _recognizedOriginalText;
    private string? _translationDisabledReason;
    private string? _translationTargetLanguage;
    internal string? OriginalCopyText => _originalText ?? _recognizedOriginalText;
    internal PinContent CurrentDisplayContent => _content;
    internal event Action? MermaidDisplayChanged;
    internal PinTranslationState TranslationState { get; private set; }
    internal bool IsShowingTranslation => _showingTranslation;
    internal string? DisplayedText => _content.OriginalText;
    internal Task TranslationWork { get; set; } = Task.CompletedTask;
    internal event Action<bool>? TranslationRequested;
    internal Func<long, bool>? ConfirmTranslationModelDownload { get; set; }
    internal bool IsShowingMarkdownSource => _showingMarkdownSource;
    private PinContent DisplayContent => _showingMarkdownSource ? _markdownSourceContent! : _content;

    internal void RetainRecognizedOriginal(string source) => _recognizedOriginalText ??= source;

    internal void SetTranslationDisabled(string reason)
    {
        _translationDisabledReason = reason;
        SetTranslationState(PinTranslationState.Disabled);
    }

    internal void SetTranslationTarget(string target)
    {
        if (_translationTargetLanguage == target) return;
        if (_showingTranslation) SetTranslationDisplay(false);
        _translationTargetLanguage = target;
        _translationResult = null;
        _translationOriginal = null;
        _translationNeedsReview = false;
        _translationReviewMessage = null;
        _translationDisabledReason = null;
        TranslationState = PinTranslationState.Unavailable;
    }

    // Persist the source and offer translation when reopened. A Korean preview must never be
    // saved beside an English payload as if they were the same clipboard representation.
    internal PinContent TranslationSourceContent => _translationOriginal is null ? _content
        : _translationOriginal.WithProvenance(_content.Provenance);

    private double TranslationFitScale => !_showingTranslation && !_showingMarkdownSource ? 1 : Math.Min(1,
        Math.Min(_state.ImageWidthDip / DisplayContent.LayoutWidthDip, _state.ImageHeightDip / DisplayContent.LayoutHeightDip));

    internal static string TranslationModelConsentText(long bytes) =>
        UiText.Format("Pin_TranslationConsent", Math.Ceiling(bytes / 1_000_000d));

    internal bool ConfirmTranslationModelInstallation(long bytes)
    {
        if (_isClosed) return false;
        if (ConfirmTranslationModelDownload is { } confirm) return confirm(bytes);
        HideHoverBar();
        return ThemedDialog.Show(this, TranslationModelConsentText(bytes), UiText.Get("Pin_TranslationInstall"),
            MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel,
            okLabel: UiText.Get("Pin_TranslationInstallAction")) == MessageBoxResult.OK;
    }

    internal void SetTranslationState(PinTranslationState state, long downloadBytes = 0)
    {
        if (_isClosed) return;
        TranslationState = state;
        _translationDownloadBytes = downloadBytes;
        if (state is PinTranslationState.Translating or PinTranslationState.Downloading or PinTranslationState.Recognizing)
            ShowFeedback(UiText.Get(state == PinTranslationState.Downloading ? "Pin_TranslationDownloading"
                : state == PinTranslationState.Recognizing ? "Pin_TranslationRecognizing" : "Pin_Translating"), FeedbackKind.Progress);
        else if (state == PinTranslationState.Failed)
            ShowFeedback(UiText.Get("Pin_TranslationFailed"), FeedbackKind.ActionRequired);
        else if (state is PinTranslationState.Unavailable or PinTranslationState.Available
            or PinTranslationState.Disabled or PinTranslationState.ModelNeeded)
            DismissFeedback();
        RefreshTranslationActions();
    }

    internal void ApplyTranslation(PinContent translated, bool needsReview, string? reviewMessage = null)
    {
        ArgumentNullException.ThrowIfNull(translated);
        if (_isClosed) return;
        _translationOriginal ??= _content;
        if (translated.IsMarkdown && _translationOriginal.MermaidResults is { } diagrams
            && ClipboardMarkdownRenderer.ExtractMermaidBlocks(translated.OriginalText!).Select(block => block.Source)
                .SequenceEqual(ClipboardMarkdownRenderer.ExtractMermaidBlocks(_translationOriginal.OriginalText!).Select(block => block.Source)))
            translated = translated.WithMermaidResults(diagrams, MermaidDisplayScale);
        _translationResult = translated;
        _translationNeedsReview = needsReview;
        _translationReviewMessage = needsReview ? reviewMessage : null;
        TranslationState = PinTranslationState.Translated;
        // Don't replace text under an active selection. The language action remains available.
        if (!IsTextSelectionMode && !_showingMarkdownSource) SetTranslationDisplay(true);
        else RefreshTranslationActions();
        ShowFeedback(_translationReviewMessage ?? UiText.Get(needsReview ? "Pin_TranslationReview" : "Pin_Translated"),
            needsReview ? FeedbackKind.Warning : FeedbackKind.Confirmation);
    }

    internal void SetTranslationDisplay(bool translated)
    {
        if (_isClosed || _translationOriginal is null || _translationResult is null) return;
        if (IsTextSelectionMode) LeaveTextSelection(restoreFocus: false);
        _showingMarkdownSource = false;
        EndDrag();
        PinContent next = translated ? _translationResult : _translationOriginal;
        _showingTranslation = translated;
        if (!translated && _translationReviewMessage is { } review && _feedbackKind == FeedbackKind.Warning
            && _feedbackText.Text == review)
        {
            _feedbackCountdown.Stop();
            HideFeedback(immediate: true);
        }
        _content = next.WithProvenance(_content.Provenance).RenderedAt(next.MermaidResults is not null
            ? MermaidDisplayScale : VisualTreeHelper.GetDpi(this).DpiScaleX);
        ClipboardCache.Clear();
        _imageElement.Source = _content.Image;
        // Keep position, frame, zoom and opacity stable when changing languages.
        _chrome.Background = translated ? Brushes.White : null;
        UpdateImageRasterLayout();
        RefreshTranslationActions();
        MermaidDisplayChanged?.Invoke();
    }

    internal void InvokeTranslationAction()
    {
        if (_isClosed) return;
        if (TranslationState == PinTranslationState.Translated)
            SetTranslationDisplay(!_showingTranslation);
        else if (TranslationState is PinTranslationState.Available or PinTranslationState.ModelNeeded or PinTranslationState.Failed)
        {
            // A manual translation request chooses the rendered language view. If the user
            // switches back to source while inference is running, ApplyTranslation respects it.
            if (_showingMarkdownSource) SetMarkdownSourceDisplay(false);
            TranslationRequested?.Invoke(false);
        }
    }

    private string TranslationActionLabel => UiText.Get(_showingTranslation
        ? "Pin_TranslationOriginal" : "Pin_Translate");

    private string TranslationActionHelp => TranslationState == PinTranslationState.Disabled
        ? _translationDisabledReason ?? UiText.Get("Pin_TranslationNoText")
        : _showingTranslation && _translationNeedsReview && _translationReviewMessage is { } review
            ? review : UiText.Get(TranslationState switch
    {
        PinTranslationState.ModelNeeded => "Pin_TranslationInstall",
        PinTranslationState.Failed => "Pin_TranslationRetry",
        PinTranslationState.Downloading => "Pin_TranslationDownloading",
        PinTranslationState.Recognizing => "Pin_TranslationRecognizing",
        PinTranslationState.Checking or PinTranslationState.Translating => "Pin_Translating",
        _ when _showingTranslation && _translationNeedsReview && !TranslationSourceContent.IsMarkdown => "Pin_TranslationSourceReview",
        _ when _showingTranslation => "Pin_TranslationOriginal",
        _ => "Pin_Translate",
    });

    private bool TranslationActionEnabled => TranslationState is PinTranslationState.Available or PinTranslationState.Translated
        or PinTranslationState.ModelNeeded or PinTranslationState.Failed;

    private void RefreshTranslationButton(PinHoverBar bar)
    {
        string help = TranslationState == PinTranslationState.ModelNeeded
            ? UiText.Format("Pin_TranslationModelNeeded", Math.Ceiling(_translationDownloadBytes / 1_000_000d))
            : TranslationActionHelp;
        bar.SetTranslationAction(TranslationState != PinTranslationState.Unavailable,
            TranslationActionEnabled, TranslationActionLabel, help);
    }

    private void RefreshTranslationActions()
    {
        if (_hoverBar is { } bar)
        {
            RefreshTranslationButton(bar);
            _labeledHoverBarWidth = bar.MeasureLabeledWidth();
            PlaceHoverBar();
        }
        BuildContextMenu();
    }

    private void AddTranslationMenuItem(ContextMenu menu)
    {
        if (TranslationState == PinTranslationState.Unavailable) return;
        var action = MenuItemFor(TranslationActionLabel, (_, _) => InvokeTranslationAction());
        action.IsEnabled = TranslationActionEnabled;
        action.ToolTip = TranslationActionHelp;
        ToolTipService.SetShowOnDisabled(action, true);
        menu.Items.Add(action);
        if (_translationResult?.OriginalText is not null)
            menu.Items.Add(MenuItemFor(UiText.Get("Pin_CopyTranslation"), (_, _) => CopyTranslationText()));
        menu.Items.Add(new Separator());
    }

    internal void CopyTranslationText()
    {
        if (_isClosed || _translationResult?.OriginalText is not { } text) return;
        OriginalTextCopyRequested?.Invoke(this, text);
    }

    internal void SetMarkdownSourceDisplay(bool source)
    {
        if (_isClosed || !TranslationSourceContent.IsMarkdown) return;
        if (source && _showingTranslation) SetTranslationDisplay(false);
        if (IsTextSelectionMode) LeaveTextSelection(restoreFocus: false);
        EndDrag();
        if (source)
        {
            _markdownSourceContent ??= PinContent.FromText(
                ClipboardTextRenderer.Render(_originalText!, false), _originalText!, false, _content.Provenance);
            _markdownSourceContent = _markdownSourceContent.RenderedAt(VisualTreeHelper.GetDpi(this).DpiScaleX);
        }
        _showingMarkdownSource = source;
        _imageElement.Source = DisplayContent.Image;
        _chrome.Background = _showingTranslation || source ? Brushes.White : null;
        UpdateImageRasterLayout();
        BuildContextMenu();
    }

    private void AddMarkdownViewMenuItem(ContextMenu menu)
    {
        if (!TranslationSourceContent.IsMarkdown) return;
        menu.Items.Add(MenuItemFor(UiText.Get(_showingMarkdownSource ? "Pin_MarkdownRenderedView" : "Pin_MarkdownSourceView"),
            (_, _) => SetMarkdownSourceDisplay(!_showingMarkdownSource)));
        menu.Items.Add(new Separator());
    }
}
