using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Ocr;

/// <summary>
/// The OCR result window's translation view. Until the model is on this PC it explains the
/// download and its size and waits for the user to turn translation on; afterwards it lists the
/// numbered blocks, each original above its Korean translation, with a note on values the
/// translation did not keep.
/// </summary>
internal sealed class OcrTranslationPanel : Grid
{
    internal enum ViewState { Idle, Checking, ModelNeeded, Downloading, DownloadFailed, Translating, Translated, NothingToTranslate, Cancelled, Failed }

    private readonly TextBlock _heading;
    private readonly StackPanel _message;
    private readonly TextBlock _messageTitle;
    private readonly TextBlock _messageBody;
    private readonly ProgressBar _progress;
    private readonly TextBlock _progressText;
    private readonly Button _primaryAction;
    private readonly Button _cancel;
    private readonly ScrollViewer _scroller;
    private readonly StackPanel _items;
    private IReadOnlyList<TranslatedBlock> _results = [];
    private TextBox? _selectionOwner;

    internal OcrTranslationPanel()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        SetResourceReference(BackgroundProperty, "Surface.Sunken");
        AutomationProperties.SetName(this, UiText.Get("Translate_Tab"));

        var header = new DockPanel { Margin = new Thickness(16, 12, 12, 6), LastChildFill = true };
        _heading = new TextBlock
        {
            Text = UiText.Get("Translate_Direction"), VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _heading.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        AutomationProperties.SetLiveSetting(_heading, AutomationLiveSetting.Polite);
        header.Children.Add(_heading);
        Children.Add(header);

        var content = new Grid();
        SetRow(content, 1);
        Children.Add(content);

        _message = new StackPanel { Margin = new Thickness(16, 8, 16, 16), MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left };
        _messageTitle = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        _messageTitle.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        AutomationProperties.SetLiveSetting(_messageTitle, AutomationLiveSetting.Polite);
        _messageBody = new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        _messageBody.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        _progress = new ProgressBar { Height = 4, Margin = new Thickness(0, 14, 0, 0), Minimum = 0, Maximum = 1 };
        _progressText = new TextBlock { Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
        _progressText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        _primaryAction = new Button { Margin = new Thickness(0, 0, 8, 0), MinWidth = 120 };
        _primaryAction.SetResourceReference(StyleProperty, "Button.Primary");
        _primaryAction.Click += (_, _) => (State is ViewState.ModelNeeded or ViewState.DownloadFailed
            ? DownloadRequested : RetryRequested)?.Invoke(this, EventArgs.Empty);
        _cancel = new Button { Content = UiText.Get("Dialog.Cancel"), MinWidth = 96 };
        _cancel.SetResourceReference(StyleProperty, "Button.Secondary");
        AutomationProperties.SetName(_cancel, UiText.Get("Dialog.Cancel"));
        _cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        actions.Children.Add(_primaryAction);
        actions.Children.Add(_cancel);
        _message.Children.Add(_messageTitle);
        _message.Children.Add(_messageBody);
        _message.Children.Add(_progress);
        _message.Children.Add(_progressText);
        _message.Children.Add(actions);
        content.Children.Add(_message);

        _items = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        _scroller = new ScrollViewer
        {
            Content = _items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(_scroller, UiText.Get("Translate_List"));
        content.Children.Add(_scroller);
        ShowIdle();
    }

    internal ViewState State { get; private set; }

    /// <summary>Turn translation on: download the model (also the retry after a failed download).</summary>
    internal event EventHandler? DownloadRequested;

    /// <summary>Translate again after a cancellation or a failure.</summary>
    internal event EventHandler? RetryRequested;

    internal event EventHandler? CancelRequested;

    internal event EventHandler? ContentChanged;

    /// <summary>The block under the pointer or keyboard focus, as an index into <see cref="Results"/>.</summary>
    internal event EventHandler<int>? BlockFocused;

    internal IReadOnlyList<TranslatedBlock> Results => _results;

    /// <summary>The translations in reading order, one block per paragraph.</summary>
    internal string CopyText => string.Join("\n\n", _results.Select(result => result.Translation.Text).Where(text => text.Length > 0));

    /// <summary>The current translation selection, retained when focus moves to the copy button.</summary>
    internal string SelectedText => _selectionOwner?.SelectedText ?? string.Empty;

    /// <summary>Each numbered original followed by its translation.</summary>
    internal string CopyWithSourceText => string.Join("\n\n", _results.Select(result =>
        $"{result.Block.Number.ToString(CultureInfo.InvariantCulture)}. {result.Block.Text}\n{result.Translation.Text}"));

    internal string MessageTitle => _messageTitle.Text;

    internal string MessageBody => _messageBody.Text;

    internal string PrimaryActionText => _primaryAction.Content as string ?? "";

    internal void ShowIdle() => ShowMessage(ViewState.Idle, "", "", primary: null, cancel: false);

    internal void ShowChecking() => ShowMessage(ViewState.Checking, UiText.Get("Translate_Checking"), "",
        primary: null, cancel: false, progress: double.NaN);

    internal void ShowModelNeeded(long downloadBytes) => ShowMessage(ViewState.ModelNeeded,
        UiText.Get("Translate_OffTitle"), UiText.Format("Translate_OffBody", Megabytes(downloadBytes)),
        primary: UiText.Format("Translate_TurnOn", Megabytes(downloadBytes)), cancel: false);

    internal void ShowDownloading(long receivedBytes, long totalBytes) => ShowMessage(ViewState.Downloading,
        UiText.Get("Translate_Downloading"), UiText.Get("Translate_DownloadingBody"), primary: null, cancel: true,
        progress: totalBytes <= 0 ? double.NaN : Math.Clamp((double)receivedBytes / totalBytes, 0, 1),
        progressText: UiText.Format("Translate_DownloadProgress", Megabytes(receivedBytes, floor: true), Megabytes(totalBytes)));

    internal void ShowDownloadFailed(string message) => ShowMessage(ViewState.DownloadFailed,
        UiText.Get("Translate_DownloadFailed"), message, primary: UiText.Get("Translate_Retry"), cancel: false);

    internal void ShowTranslating(int completed, int total) => ShowMessage(ViewState.Translating,
        UiText.Format("Translate_Progress", completed, total), UiText.Get("Translate_OnThisPc"), primary: null, cancel: true,
        progress: total <= 0 ? double.NaN : (double)completed / total);

    internal void ShowNothingToTranslate() => ShowMessage(ViewState.NothingToTranslate,
        UiText.Get("Translate_NothingTitle"), UiText.Get("Translate_NothingBody"), primary: null, cancel: false);

    internal void ShowCancelled() => ShowMessage(ViewState.Cancelled, UiText.Get("Translate_Cancelled"), "",
        primary: UiText.Get("Translate_Again"), cancel: false);

    internal void ShowFailed(string message) => ShowMessage(ViewState.Failed, UiText.Get("Translate_FailedTitle"), message,
        primary: UiText.Get("Translate_Again"), cancel: false);

    internal void ShowResults(IReadOnlyList<TranslatedBlock> results)
    {
        _selectionOwner = null;
        _results = results;
        State = ViewState.Translated;
        Announce(_heading, results.Count == 1 ? UiText.Get("Translate_SummaryOne") : UiText.Format("Translate_Summary", results.Count));
        _message.Visibility = Visibility.Collapsed;
        _items.Children.Clear();
        for (int index = 0; index < results.Count; index++) _items.Children.Add(BuildItem(results[index], index));
        _scroller.ScrollToTop();
        _scroller.Visibility = Visibility.Visible;
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Brings a block into view, such as the one picked on the original image.</summary>
    internal void RevealBlock(int index)
    {
        if (index < 0 || index >= _items.Children.Count) return;
        ((FrameworkElement)_items.Children[index]).BringIntoView();
    }

    private void ShowMessage(ViewState state, string title, string body, string? primary, bool cancel,
        double? progress = null, string? progressText = null)
    {
        State = state;
        _selectionOwner = null;
        _results = [];
        _items.Children.Clear();
        _scroller.Visibility = Visibility.Collapsed;
        _heading.Text = UiText.Get("Translate_Direction");
        _message.Visibility = state == ViewState.Idle ? Visibility.Collapsed : Visibility.Visible;
        Announce(_messageTitle, title);
        _messageBody.Text = body;
        _messageBody.Visibility = body.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _progress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        _progress.IsIndeterminate = progress is double value && double.IsNaN(value);
        if (progress is double fraction && !double.IsNaN(fraction)) _progress.Value = fraction;
        AutomationProperties.SetName(_progress, title);
        _progressText.Text = progressText ?? "";
        _progressText.Visibility = string.IsNullOrEmpty(progressText) ? Visibility.Collapsed : Visibility.Visible;
        _primaryAction.Content = primary;
        _primaryAction.Visibility = primary is null ? Visibility.Collapsed : Visibility.Visible;
        if (primary is not null) AutomationProperties.SetName(_primaryAction, primary);
        _cancel.Visibility = cancel ? Visibility.Visible : Visibility.Collapsed;
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private FrameworkElement BuildItem(TranslatedBlock result, int index)
    {
        string number = result.Block.Number.ToString(UiText.Culture);
        var row = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        // A fixed number column keeps every original aligned, whether its number has one digit or three.
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            MinWidth = 20, Height = 20, CornerRadius = new CornerRadius(10), Padding = new Thickness(5, 0, 5, 0),
            Margin = new Thickness(0, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = number, FontSize = 11, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        badge.SetResourceReference(Border.BackgroundProperty, "Surface.BadgeStrong");
        ((TextBlock)badge.Child).SetResourceReference(TextBlock.ForegroundProperty, "Text.Badge");
        row.Children.Add(badge);

        var texts = new StackPanel();
        SetColumn(texts, 1);
        var source = new TextBlock { Text = result.Block.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        source.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        AutomationProperties.SetName(source, UiText.Format("Translate_SourceName", number));
        texts.Children.Add(source);
        var translation = new TextBox
        {
            Text = result.Translation.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            Template = FlatTextTemplate, Margin = new Thickness(0, 4, 0, 0), FontSize = 14,
            // The theme's text field padding and height would indent the translation under its original.
            Padding = new Thickness(0), MinHeight = 0,
        };
        InputMethod.SetIsInputMethodEnabled(translation, false);
        translation.GotKeyboardFocus += (_, _) => _selectionOwner = translation;
        translation.SelectionChanged += (_, _) =>
        {
            if (translation.SelectionLength > 0) _selectionOwner = translation;
        };
        translation.SetResourceReference(Control.ForegroundProperty, "Text.Primary");
        AutomationProperties.SetName(translation, UiText.Format("Translate_TranslationName", number));
        texts.Children.Add(translation);
        if (result.Translation.NeedsReview)
        {
            var warning = new TextBlock { Margin = new Thickness(0, 4, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap };
            warning.SetResourceReference(TextBlock.ForegroundProperty, "State.Warning");
            warning.Text = string.Join("\n", ReviewNotes(result.Translation));
            texts.Children.Add(warning);
        }
        row.Children.Add(texts);

        var item = new Border { Padding = new Thickness(0, 10, 0, 10), BorderThickness = new Thickness(0, 0, 0, 1), Child = row };
        item.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        AutomationProperties.SetName(item, UiText.Format("Translate_BlockName", number));
        item.MouseEnter += (_, _) => BlockFocused?.Invoke(this, index);
        item.IsKeyboardFocusWithinChanged += (_, e) => { if (e.NewValue is true) BlockFocused?.Invoke(this, index); };
        return item;
    }

    /// <summary>
    /// Sets a live text and raises the live-region event a screen reader listens for; WPF does not
    /// raise it for a changed TextBlock.Text on its own.
    /// </summary>
    private static void Announce(TextBlock block, string text)
    {
        if (string.Equals(block.Text, text, StringComparison.Ordinal)) return;
        block.Text = text;
        if (!block.IsLoaded || text.Length == 0) return;
        AutomationPeer? peer = UIElementAutomationPeer.FromElement(block) ?? UIElementAutomationPeer.CreatePeerForElement(block);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    /// <summary>A read-only text box drawn as plain text: selectable and copyable, without a field frame.</summary>
    private static readonly ControlTemplate FlatTextTemplate = CreateFlatTextTemplate();

    private static ControlTemplate CreateFlatTextTemplate()
    {
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(FocusableProperty, false);
        var template = new ControlTemplate(typeof(TextBox)) { VisualTree = host };
        template.Seal();
        return template;
    }

    internal static IEnumerable<string> ReviewNotes(TranslatedText translation)
    {
        if (translation.Missing.Count > 0) yield return UiText.Format("Translate_WarningMissing", string.Join(", ", translation.Missing));
        if (translation.Invented.Count > 0) yield return UiText.Format("Translate_WarningInvented", string.Join(", ", translation.Invented));
    }

    private static string Megabytes(long bytes, bool floor = false)
    {
        double megabytes = bytes / 1_000_000d;
        return (floor ? Math.Floor(megabytes) : Math.Ceiling(megabytes)).ToString("N0", UiText.Culture);
    }
}

/// <summary>A numbered block and its translation, as listed in the translation view.</summary>
internal sealed record TranslatedBlock(TranslationBlock Block, TranslatedText Translation);
