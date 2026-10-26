using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Themes;

namespace MyCapture.App.Pinning;

internal enum PinTranslationState { Unavailable, Available, Checking, ModelNeeded, Downloading, Translating, Translated, Failed, Disabled }

internal sealed partial class PinWindow
{
    private PinContent? _translationOriginal;
    private PinContent? _translationResult;
    private bool _showingTranslation;
    private bool _translationNeedsReview;
    private bool _translationToKorean = true;
    private long _translationDownloadBytes;
    private string? _translationDisabledHelp;
    private (double Width, double Height)? _naturalSizeBeforeTranslation;
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

    /// <summary>The text Ctrl+C and the copy button place beside the image: what the pin shows now.</summary>
    internal string? VisibleText => _showingTranslation && _translationResult is { } translation
        ? translation.OriginalText : _originalText;

    /// <summary>The help shown while a pin's translation command is turned off, or null when it is not.</summary>
    internal string? TranslationDisabledHelp => TranslationState == PinTranslationState.Disabled ? _translationDisabledHelp : null;

    // Persist the source and offer translation when reopened. A translated preview must never be
    // saved beside the source payload as if they were the same clipboard representation.
    internal PinContent TranslationSourceContent => _translationOriginal is null ? _content
        : _translationOriginal.WithProvenance(_content.Provenance);

    /// <summary>The content whose diagrams the pin is showing: the translation while it is shown.</summary>
    internal PinContent DisplayedDocumentContent => _showingTranslation && _translationResult is not null
        ? _translationResult : TranslationSourceContent;

    private double TranslationFitScale => !_showingTranslation && !_showingMarkdownSource ? 1 : Math.Min(1,
        Math.Min(_state.ImageWidthDip / DisplayContent.LayoutWidthDip, _state.ImageHeightDip / DisplayContent.LayoutHeightDip));

    internal static string TranslationModelConsentText(long bytes, bool toKorean = true) =>
        UiText.Format("Pin_TranslationConsent", Math.Ceiling(bytes / 1_000_000d),
            UiText.Get(toKorean ? "Pin_LanguageKorean" : "Pin_LanguageEnglish"));

    internal bool ConfirmTranslationModelInstallation(long bytes)
    {
        if (_isClosed) return false;
        if (ConfirmTranslationModelDownload is { } confirm) return confirm(bytes);
        HideHoverBar();
        return ThemedDialog.Show(this, TranslationModelConsentText(bytes, _translationToKorean), UiText.Get("Pin_TranslationInstall"),
            MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel,
            okLabel: UiText.Get("Pin_TranslationInstallAction")) == MessageBoxResult.OK;
    }

    /// <summary>Turns the command off with a reason (already in the target language, unsupported language).</summary>
    internal void SetTranslationDisabled(string help)
    {
        if (_isClosed) return;
        _translationDisabledHelp = help;
        SetTranslationState(PinTranslationState.Disabled);
    }

    internal void SetTranslationDirection(bool toKorean)
    {
        _translationToKorean = toKorean;
        if (_hoverBar is not null) RefreshTranslationActions();
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
        else if (state is PinTranslationState.Unavailable or PinTranslationState.Available or PinTranslationState.Disabled)
            DismissFeedback();
        RefreshTranslationActions();
    }

    /// <summary>Tells the user why nothing changed (for example an image pin that is already in the target language).</summary>
    internal void ReportTranslationNote(string text)
    {
        if (_isClosed) return;
        ShowFeedback(text, FeedbackKind.Result);
    }

    internal void ApplyTranslation(PinContent translated, bool needsReview, bool toKorean = true)
    {
        ArgumentNullException.ThrowIfNull(translated);
        if (_isClosed) return;
        _translationOriginal ??= _content;
        _naturalSizeBeforeTranslation ??= (_state.ImageWidthDip, _state.ImageHeightDip);
        _translationToKorean = toKorean;
        _translationResult = translated;
        _translationNeedsReview = needsReview;
        TranslationState = PinTranslationState.Translated;
        // Don't replace text under an active selection. The command remains available.
        if (!IsTextSelectionMode && !_showingMarkdownSource) SetTranslationDisplay(true);
        else RefreshTranslationActions();
        ShowFeedback(UiText.Get(needsReview ? "Pin_TranslationReview"
                : toKorean ? "Pin_TranslatedKorean" : "Pin_TranslatedEnglish"),
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
        _content = next.WithProvenance(_content.Provenance).RenderedAt(next.MermaidResults is not null
            ? MermaidDisplayScale : VisualTreeHelper.GetDpi(this).DpiScaleX);
        ClipboardCache.Clear();
        _imageElement.Source = _content.Image;
        // Keep position, frame, zoom and opacity stable when changing languages, except when a picture
        // becomes a text document (or back): that has its own natural size, so the frame follows it.
        bool pictureSwap = !_translationOriginal.CanRerender && _translationResult.CanRerender;
        if (pictureSwap) FitFrameToDisplayedContent(translated);
        _chrome.Background = translated ? Brushes.White : null;
        UpdateImageRasterLayout();
        RefreshTranslationActions();
        ViewStateChanged?.Invoke();
    }

    private void FitFrameToDisplayedContent(bool translated)
    {
        (double width, double height) = translated
            ? (_content.LayoutWidthDip, _content.LayoutHeightDip)
            : _naturalSizeBeforeTranslation ?? (_state.ImageWidthDip, _state.ImageHeightDip);
        _state.ReplaceImageSize(width, height);
        if (translated)
        {
            // A long translated document must not outgrow the desktop the picture fit on.
            var desktop = VirtualDesktopDip();
            double fit = Math.Min(1, Math.Min(desktop.Width * 0.8 / width, desktop.Height * 0.8 / height));
            _state.SetZoom(Math.Min(_state.Zoom, fit));
        }
        Width = _state.WidthDip;
        Height = _state.HeightDip;
        ApplyPositionKeepingGrabbable(Left, Top);
    }

    internal void InvokeTranslationAction()
    {
        if (_isClosed) return;
        if (TranslationState == PinTranslationState.Translated)
            SetTranslationDisplay(!_showingTranslation);
        else if (TranslationState is PinTranslationState.Available or PinTranslationState.ModelNeeded or PinTranslationState.Failed)
        {
            // A manual translation request chooses the translated view. If the user
            // switches back to source while inference is running, ApplyTranslation respects it.
            if (_showingMarkdownSource) SetMarkdownSourceDisplay(false);
            TranslationRequested?.Invoke(false);
        }
    }

    private string TranslationActionLabel => UiText.Get(_showingTranslation ? "Pin_TranslationOriginal" : "Pin_Translate");

    private string TranslationActionHelp => TranslationState switch
    {
        PinTranslationState.Disabled => _translationDisabledHelp ?? UiText.Get("Pin_Translate"),
        PinTranslationState.ModelNeeded => UiText.Get("Pin_TranslationInstall"),
        PinTranslationState.Failed => UiText.Get("Pin_TranslationRetry"),
        PinTranslationState.Downloading => UiText.Get("Pin_TranslationDownloading"),
        PinTranslationState.Checking or PinTranslationState.Translating => UiText.Get("Pin_Translating"),
        _ when _showingTranslation && _translationNeedsReview => UiText.Get("Pin_TranslationReviewHelp"),
        _ when _showingTranslation => UiText.Get("Pin_TranslationOriginalHelp"),
        _ => UiText.Get("Pin_TranslateHelp"),
    };

    private bool TranslationActionEnabled => TranslationState is PinTranslationState.Available or PinTranslationState.Translated
        or PinTranslationState.ModelNeeded or PinTranslationState.Failed;

    private void RefreshTranslationButton(PinHoverBar bar)
    {
        string help = TranslationState == PinTranslationState.ModelNeeded
            ? UiText.Format("Pin_TranslationModelNeeded", Math.Ceiling(_translationDownloadBytes / 1_000_000d),
                UiText.Get(_translationToKorean ? "Pin_LanguageKorean" : "Pin_LanguageEnglish"))
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
        if (TranslationState == PinTranslationState.Disabled) action.ToolTip = TranslationActionHelp;
        menu.Items.Add(action);
        if (_showingTranslation && _translationResult?.OriginalText is not null)
            menu.Items.Add(MenuItemFor(UiText.Get("Pin_CopyTranslation"), (_, _) => RequestTranslatedTextCopy()));
        menu.Items.Add(new Separator());
    }

    private void RequestTranslatedTextCopy()
    {
        if (_translationResult?.OriginalText is not { } text) return;
        ShowFeedback(UiText.Get("Text_BDB7C68F7452"), FeedbackKind.Progress);
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
