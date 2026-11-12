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
    // A picture translation's text view, rendered when it is first shown.
    private Lazy<PinContent>? _translationDocument;
    private bool _showingTranslationDocument;
    private (double Width, double Height, double Zoom, double Left, double Top)? _pictureViewBeforeDocument;
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
    internal bool IsShowingTranslationDocument => _showingTranslation && _showingTranslationDocument;
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
        : _translationOriginal.WithCurrentProvenance(_content.Provenance);

    internal (double Width, double Height, double Zoom) TranslationSourceView =>
        TranslationSourceContent.Kind == PinContentKind.Image && _pictureViewBeforeDocument is { } view
            ? (view.Width, view.Height, view.Zoom) : (_state.ImageWidthDip, _state.ImageHeightDip, _state.Zoom);

    /// <summary>The content whose diagrams the pin is showing: the translation while it is shown.</summary>
    internal PinContent DisplayedDocumentContent => _showingTranslation && _translationResult is not null
        ? _showingTranslationDocument && _translationDocument is not null ? _translationDocument.Value : _translationResult
        : TranslationSourceContent;

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
        var shown = TranslationActionView;
        _translationToKorean = toKorean;
        if (_hoverBar is not null && TranslationActionView != shown) RefreshTranslationActions();
    }

    internal void SetTranslationState(PinTranslationState state, long downloadBytes = 0)
    {
        if (_isClosed) return;
        var shown = TranslationActionView;
        TranslationState = state;
        _translationDownloadBytes = downloadBytes;
        if (state is PinTranslationState.Translating or PinTranslationState.Downloading)
        {
            string text = UiText.Get(state == PinTranslationState.Downloading ? "Pin_TranslationDownloading" : "Pin_Translating");
            BeginTranslationProgress(text);
            ShowFeedback(text, FeedbackKind.Progress);
        }
        else
        {
            // Every other state ends the progress, including one that shows nothing of its own (an install question).
            EndTranslationProgress();
            if (state == PinTranslationState.Failed)
                ShowFeedback(_translationFailureMessage ?? UiText.Get("Pin_TranslationFailed"), FeedbackKind.ActionRequired);
            else if (state is PinTranslationState.Unavailable or PinTranslationState.Available or PinTranslationState.Disabled)
                DismissFeedback();
        }
        // Checking and translating look the same; the button and the menu are rebuilt only when what they show changes.
        if (TranslationActionView != shown) RefreshTranslationActions();
        OnTranslationStateForReveal(state);
    }

    /// <summary>Tells the user why nothing changed (for example an image pin that is already in the target language).</summary>
    internal void ReportTranslationNote(string text)
    {
        if (_isClosed) return;
        ShowFeedback(text, FeedbackKind.Result);
    }

    internal void ApplyTranslation(PinContent translated, bool needsReview, bool toKorean = true, Lazy<PinContent>? document = null)
    {
        ArgumentNullException.ThrowIfNull(translated);
        if (_isClosed) return;
        _translationOriginal ??= _content;
        _naturalSizeBeforeTranslation ??= (_state.ImageWidthDip, _state.ImageHeightDip);
        _translationToKorean = toKorean;
        _translationResult = translated;
        _translationDocument = document;
        _showingTranslationDocument = false;
        _translationNeedsReview = needsReview;
        TranslationState = PinTranslationState.Translated;
        EndTranslationProgress();
        // Don't replace text under an active selection. The command remains available.
        if (!IsTextSelectionMode && !IsRecognizingSelection && !_showingMarkdownSource) SetTranslationDisplay(true);
        else RefreshTranslationActions();
        OnTranslationAppliedForReveal();
        ShowFeedback(UiText.Get(needsReview ? "Pin_TranslationReview"
                : toKorean ? "Pin_TranslatedKorean" : "Pin_TranslatedEnglish"),
            needsReview ? FeedbackKind.Warning : FeedbackKind.Confirmation);
    }

    internal void SetTranslationDisplay(bool translated)
    {
        if (_isClosed || _translationOriginal is null || _translationResult is null) return;
        if (IsTextSelectionMode || IsRecognizingSelection) LeaveTextSelection(restoreFocus: false);
        var revealBefore = RevealSnapshot();
        _showingMarkdownSource = false;
        EndDrag();
        bool wasDocument = !_translationOriginal.CanRerender && _content.CanRerender;
        if (!translated) _showingTranslationDocument = false;
        PinContent next = translated
            ? _showingTranslationDocument && _translationDocument is not null ? _translationDocument.Value : _translationResult
            : _translationOriginal;
        bool isDocument = !_translationOriginal.CanRerender && next.CanRerender;
        if (isDocument && !wasDocument)
            _pictureViewBeforeDocument = (_state.ImageWidthDip, _state.ImageHeightDip, _state.Zoom, Left, Top);
        _showingTranslation = translated;
        _content = next.WithCurrentProvenance(_content.Provenance).RenderedAt(next.MermaidResults is not null
            ? MermaidDisplayScale : VisualTreeHelper.GetDpi(this).DpiScaleX);
        ClipboardCache.Clear();
        ShowDisplayAt(_imageElement.RenderSize, VisualTreeHelper.GetDpi(this));
        // Keep position, frame, zoom and opacity stable when changing languages, except when a picture
        // becomes a text document (or back): that has its own natural size, so the frame follows it.
        if (isDocument != wasDocument)
        {
            if (isDocument) FitFrameToDisplayedContent(true);
            else if (_pictureViewBeforeDocument is { } view)
            {
                _state.ReplaceImageSize(view.Width, view.Height);
                _state.SetZoom(view.Zoom);
                Width = _state.WidthDip;
                Height = _state.HeightDip;
                ApplyPositionKeepingGrabbable(view.Left, view.Top);
                _pictureViewBeforeDocument = null;
            }
            else FitFrameToDisplayedContent(false);
        }
        _chrome.Background = translated && _content.Kind != PinContentKind.Image ? Brushes.White : null;
        UpdateImageRasterLayout();
        RefreshTranslationActions();
        OnTranslationDisplayChangedForReveal(revealBefore, translated);
        ViewStateChanged?.Invoke();
    }

    internal void SetTranslationDocumentDisplay(bool document)
    {
        if (!_showingTranslation || _translationDocument is null || _translationResult?.Kind != PinContentKind.Image) return;
        _showingTranslationDocument = document;
        SetTranslationDisplay(true);
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
        // Translation is drawn by the WPF host only; the pin moves there when the action would do something.
        if (_native is not null && TranslationState is PinTranslationState.Translated or PinTranslationState.Available
            or PinTranslationState.ModelNeeded or PinTranslationState.Failed)
        {
            SwitchToWpf(PinSwitchReason.Command, InvokeTranslationAction);
            return;
        }
        if (TranslationState == PinTranslationState.Translated)
            SetTranslationDisplay(!_showingTranslation);
        else if (TranslationState is PinTranslationState.Available or PinTranslationState.ModelNeeded or PinTranslationState.Failed)
        {
            // A manual translation request chooses the translated view. If the user
            // switches back to source while inference is running, ApplyTranslation respects it.
            if (_showingMarkdownSource) SetMarkdownSourceDisplay(false);
            TranslationRequested?.Invoke(false);
        }
        else if (IsTranslationRunning) TranslationCancelRequested?.Invoke();
    }

    private string TranslationActionLabel => UiText.Get(IsTranslationRunning ? "Pin_TranslationCancel"
        : _showingTranslation ? "Pin_TranslationOriginal" : "Pin_Translate");

    private string TranslationActionHelp => TranslationState switch
    {
        PinTranslationState.Disabled => _translationDisabledHelp ?? UiText.Get("Pin_Translate"),
        PinTranslationState.ModelNeeded => UiText.Get("Pin_TranslationInstall"),
        PinTranslationState.Failed => UiText.Get("Pin_TranslationRetry"),
        PinTranslationState.Checking or PinTranslationState.Downloading or PinTranslationState.Translating =>
            UiText.Get("Pin_TranslationCancel"),
        _ when _showingTranslation && _translationNeedsReview => UiText.Get("Pin_TranslationReviewHelp"),
        _ when _showingTranslation => UiText.Get("Pin_TranslationOriginalHelp"),
        _ => UiText.Get("Pin_TranslateHelp"),
    };

    private bool TranslationActionEnabled => TranslationState is PinTranslationState.Available or PinTranslationState.Translated
        or PinTranslationState.ModelNeeded or PinTranslationState.Failed || IsTranslationRunning;

    private string TranslationButtonHelp => TranslationState == PinTranslationState.ModelNeeded
        ? UiText.Format("Pin_TranslationModelNeeded", Math.Ceiling(_translationDownloadBytes / 1_000_000d),
            UiText.Get(_translationToKorean ? "Pin_LanguageKorean" : "Pin_LanguageEnglish"))
        : TranslationActionHelp;

    /// <summary>Everything of the translation state that the hover button and the menu entry show.</summary>
    private (bool Visible, bool Enabled, string Label, string Help, string? MenuHelp) TranslationActionView =>
        (TranslationState != PinTranslationState.Unavailable, TranslationActionEnabled, TranslationActionLabel,
            TranslationButtonHelp, TranslationState == PinTranslationState.Disabled ? TranslationActionHelp : null);

    private bool RefreshTranslationButton(PinHoverBar bar)
    {
        bar.SetCopyAction(CopyActionName);
        return RefreshTranslationButtonCore(bar);
    }

    private bool RefreshTranslationButtonCore(PinHoverBar bar) =>
        bar.SetTranslationAction(TranslationState != PinTranslationState.Unavailable,
            TranslationActionEnabled, TranslationActionLabel, TranslationButtonHelp);

    private void RefreshTranslationActions()
    {
        // The command dims while it runs; measuring and placing the bar again took the UI thread a moment for nothing.
        if (_hoverBar is { } bar && RefreshTranslationButton(bar))
        {
            _labeledHoverBarWidth = bar.MeasureLabeledWidth();
            PlaceHoverBar();
        }
        BuildContextMenu();
    }

    private bool TranslationCopyItemShown => _showingTranslation && _translationResult?.OriginalText is not null;

    private void AddTranslationMenuItem(ContextMenu menu)
    {
        if (TranslationState == PinTranslationState.Unavailable) return;
        var action = MenuItemFor(TranslationActionLabel, (_, _) => InvokeTranslationAction());
        action.IsEnabled = TranslationActionEnabled;
        if (TranslationState == PinTranslationState.Disabled) action.ToolTip = TranslationActionHelp;
        menu.Items.Add(action);
        if (TranslationCopyItemShown)
        {
            menu.Items.Add(MenuItemFor(UiText.Get("Pin_CopyTranslation"), (_, _) => RequestTranslatedTextCopy()));
            menu.Items.Add(CopyImageOnlyMenuItem());
        }
        if (_showingTranslation && _translationDocument is not null && _translationResult?.Kind == PinContentKind.Image)
            menu.Items.Add(MenuItemFor(UiText.Get(_showingTranslationDocument ? "Pin_TranslationPictureView" : "Pin_TranslationDocumentView"),
                (_, _) => SetTranslationDocumentDisplay(!_showingTranslationDocument)));
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
        if (IsTextSelectionMode || IsRecognizingSelection) LeaveTextSelection(restoreFocus: false);
        EndDrag();
        if (source)
        {
            _markdownSourceContent ??= PinContent.FromText(
                ClipboardTextRenderer.Render(_originalText!, false), _originalText!, false, _content.Provenance);
            _markdownSourceContent = _markdownSourceContent.RenderedAt(VisualTreeHelper.GetDpi(this).DpiScaleX);
        }
        _showingMarkdownSource = source;
        _imageElement.Source = DisplayContent.Display;
        _chrome.Background = _showingTranslation || source ? Brushes.White : null;
        UpdateImageRasterLayout();
        if (_state.IsCollapsed) ShowThumbnail(VisualTreeHelper.GetDpi(this));
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
