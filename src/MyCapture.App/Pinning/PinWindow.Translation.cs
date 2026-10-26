using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Themes;

namespace MyCapture.App.Pinning;

internal enum PinTranslationState { Unavailable, Available, Checking, ModelNeeded, Downloading, Translating, Translated, Failed }

internal sealed partial class PinWindow
{
    private PinContent? _translationOriginal;
    private PinContent? _translationResult;
    private bool _showingTranslation;
    private bool _translationNeedsReview;
    private long _translationDownloadBytes;
    private bool _showingMarkdownSource;
    private PinContent? _markdownSourceContent;
    internal PinTranslationState TranslationState { get; private set; }
    internal bool IsShowingTranslation => _showingTranslation;
    internal string? DisplayedText => _content.OriginalText;
    internal Task TranslationWork { get; set; } = Task.CompletedTask;
    internal event Action<bool>? TranslationRequested;
    internal Func<long, bool>? ConfirmTranslationModelDownload { get; set; }
    internal bool IsShowingMarkdownSource => _showingMarkdownSource;
    private PinContent DisplayContent => _showingMarkdownSource ? _markdownSourceContent! : _content;

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
        if (state is PinTranslationState.Translating or PinTranslationState.Downloading)
            ShowFeedback(UiText.Get(state == PinTranslationState.Downloading ? "Pin_TranslationDownloading" : "Pin_Translating"), FeedbackKind.Progress);
        else if (state == PinTranslationState.Failed)
            ShowFeedback(UiText.Get("Pin_TranslationFailed"), FeedbackKind.ActionRequired);
        else if (state is PinTranslationState.Unavailable or PinTranslationState.Available)
            DismissFeedback();
        RefreshTranslationActions();
    }

    internal void ApplyTranslation(PinContent translated, bool needsReview)
    {
        ArgumentNullException.ThrowIfNull(translated);
        if (_isClosed) return;
        _translationOriginal ??= _content;
        _translationResult = translated;
        _translationNeedsReview = needsReview;
        TranslationState = PinTranslationState.Translated;
        // Don't replace text under an active selection. The language action remains available.
        if (!IsTextSelectionMode && !_showingMarkdownSource) SetTranslationDisplay(true);
        else RefreshTranslationActions();
        ShowFeedback(UiText.Get(needsReview ? "Pin_TranslationReview" : "Pin_Translated"),
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
        _content = next.WithProvenance(_content.Provenance).RenderedAt(VisualTreeHelper.GetDpi(this).DpiScaleX);
        ClipboardCache.Clear();
        _imageElement.Source = _content.Image;
        // Keep position, frame, zoom and opacity stable when changing languages.
        _chrome.Background = translated ? Brushes.White : null;
        UpdateImageRasterLayout();
        RefreshTranslationActions();
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
        ? "Pin_TranslationOriginal" : "Pin_TranslationKorean");

    private string TranslationActionHelp => UiText.Get(TranslationState switch
    {
        PinTranslationState.ModelNeeded => "Pin_TranslationInstall",
        PinTranslationState.Failed => "Pin_TranslationRetry",
        PinTranslationState.Downloading => "Pin_TranslationDownloading",
        PinTranslationState.Checking or PinTranslationState.Translating => "Pin_Translating",
        _ when _showingTranslation && _translationNeedsReview => "Pin_TranslationSourceReview",
        _ when _showingTranslation => "Pin_TranslationOriginal",
        _ => "Pin_TranslationKorean",
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
        menu.Items.Add(action);
        menu.Items.Add(new Separator());
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
