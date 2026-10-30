using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyCapture.Core.Settings;

namespace MyCapture.App.Ocr;

internal sealed partial class OcrResultWindow
{
    private OcrAccuracyOptions? _accuracy;
    private Func<OcrQuality, Task>? _changeQuality;
    private bool _updatingQuality;
    private bool _qualityOperation;
    private bool _recognitionOperation;
    private bool _accuracyReleased;
    private bool? _modelReady;
    private Task<bool>? _modelReadiness;
    private long _accuracyRefreshVersion;
    private string _generatedText = string.Empty;

    internal bool SuppressPresentation { get; set; }
    internal Task AccuracyModelCheckTask { get; private set; } = Task.CompletedTask;
    internal ComboBox AccuracySelector { get; private set; } = null!;
    internal FrameworkElement AccuracySuggestion { get; private set; } = null!;
    internal TextBlock AccuracySuggestionText { get; private set; } = null!;
    internal Button AccuracyAction { get; private set; } = null!;
    internal Button AccuracyCancel { get; private set; } = null!;
    internal Button AccuracyDismiss { get; private set; } = null!;
    internal ProgressBar AccuracyProgress { get; private set; } = null!;
    internal OcrQuality? ResultQuality { get; private set; }
    internal TextBox ResultTextBox => _textBox;
    internal bool HasEditedText => !string.Equals(_textBox.Text, _generatedText, StringComparison.Ordinal);
    internal Func<bool> ConfirmReplaceEditedText { get; set; } = null!;

    private void BuildAccuracyControls(StackPanel heading, WrapPanel commands)
    {
        ConfirmReplaceEditedText = () => MessageBox.Show(this,
            UiText.Get("Ocr_ReplaceEditedText"), Title, MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

        var choice = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0) };
        var label = new TextBlock { Text = UiText.Get("Ocr_Accuracy"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        choice.Children.Add(label);
        AccuracySelector = new ComboBox { MinWidth = 100, MinHeight = 32, SelectedValuePath = "Tag" };
        AutomationProperties.SetName(AccuracySelector, UiText.Get("Ocr_Accuracy"));
        foreach ((OcrQuality quality, string key) in new[] {
            (OcrQuality.Fast, "Ocr_QualityFast"), (OcrQuality.Balanced, "Ocr_QualityBalanced"),
            (OcrQuality.Accurate, "Ocr_QualityAccurate") })
            AccuracySelector.Items.Add(new ComboBoxItem { Content = UiText.Get(key), Tag = quality });
        AccuracySelector.SelectionChanged += async (_, _) =>
        {
            if (_updatingQuality) return;
            if (_recognitionOperation || _qualityOperation)
            {
                RestoreQualitySelection();
                return;
            }
            if (AccuracySelector.SelectedValue is OcrQuality selected
                && selected != ResultQuality && _changeQuality is not null)
                await _changeQuality(selected);
        };
        choice.Children.Add(AccuracySelector);
        commands.Children.Add(choice);

        var suggestion = new WrapPanel { Margin = new Thickness(0, 10, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        AccuracySuggestion = suggestion;
        AccuracySuggestionText = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 560,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4), FontSize = 12 };
        AccuracySuggestionText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        AutomationProperties.SetLiveSetting(AccuracySuggestionText, AutomationLiveSetting.Polite);
        suggestion.Children.Add(AccuracySuggestionText);
        AccuracyAction = MakeButton(UiText.Get("Ocr_AccurateAgain"), UiText.Get("Ocr_AccurateAgain"));
        AccuracyAction.SetResourceReference(StyleProperty, "Button.Secondary");
        AccuracyAction.Click += async (_, _) =>
        {
            if (!_recognitionOperation && !_qualityOperation && _changeQuality is not null)
                await _changeQuality(OcrQuality.Accurate);
        };
        suggestion.Children.Add(AccuracyAction);
        AccuracyProgress = new ProgressBar { Minimum = 0, Maximum = 1, Width = 120, Height = 4,
            Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(AccuracyProgress, UiText.Get("Ocr_ModelDownloading"));
        suggestion.Children.Add(AccuracyProgress);
        AccuracyCancel = MakeButton(UiText.Get("Ocr_CancelDownload"), UiText.Get("Ocr_CancelDownload"));
        AccuracyCancel.SetResourceReference(StyleProperty, "Button.Ghost");
        AccuracyCancel.Visibility = Visibility.Collapsed;
        suggestion.Children.Add(AccuracyCancel);
        AccuracyDismiss = MakeButton("×", UiText.Get("Ocr_DismissAccuracy"));
        AccuracyDismiss.SetResourceReference(StyleProperty, "Button.Icon");
        AccuracyDismiss.MinWidth = 32;
        AccuracyDismiss.ToolTip = UiText.Get("Ocr_DismissAccuracy");
        AccuracyDismiss.Click += (_, _) => DismissAccuracySuggestion();
        suggestion.Children.Add(AccuracyDismiss);
        heading.Children.Add(suggestion);
    }

    internal void ConfigureAccuracy(OcrAccuracyOptions options, Func<OcrQuality, Task> changeQuality, Action cancel)
    {
        _accuracy = options;
        _modelReady = null;
        _modelReadiness = null;
        _changeQuality = changeQuality;
        AccuracyCancel.Click += (_, _) => cancel();
        RefreshAccuracy();
    }

    internal bool ConfirmReplacement() => !HasEditedText || ConfirmReplaceEditedText();

    internal void SetResultQuality(OcrQuality? quality)
    {
        _accuracyReleased = false;
        ResultQuality = quality is { } value ? OcrQualityNames.Normalize(value) : null;
        RestoreQualitySelection();
        RefreshAccuracy();
    }

    internal void RestoreQualitySelection()
    {
        _updatingQuality = true;
        AccuracySelector.SelectedValue = ResultQuality;
        if (ResultQuality is null) AccuracySelector.SelectedIndex = -1;
        _updatingQuality = false;
    }

    internal void RefreshAccuracy()
    {
        long version = ++_accuracyRefreshVersion;
        UpdateAccuracyControls();
        if (_accuracy is not { } accuracy || !accuracy.Model.IsSupported || _qualityOperation || _accuracyReleased)
        {
            AccuracyModelCheckTask = Task.CompletedTask;
            return;
        }
        // IsReady can verify model hashes or wait on the store's verification lock.
        // Only the worker reads it; rendering uses the last completed answer.
        Task<bool> check = _modelReadiness is { IsCompleted: false } pending ? pending
            : _modelReadiness = Task.Run(() =>
            {
                try { return accuracy.Model.IsReady; }
                catch (Exception) { return false; }
            });
        AccuracyModelCheckTask = ApplyModelReadinessAsync(accuracy, check, version);
    }

    private async Task ApplyModelReadinessAsync(OcrAccuracyOptions accuracy, Task<bool> check, long version)
    {
        bool ready = await check.ConfigureAwait(false);
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_accuracy, accuracy) || !ReferenceEquals(_modelReadiness, check)) return;
                _modelReady = ready;
                if (version == _accuracyRefreshVersion) UpdateAccuracyControls();
            });
        }
        catch (TaskCanceledException) when (Dispatcher.HasShutdownStarted) { }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted) { }
    }

    private void UpdateAccuracyControls()
    {
        if (_accuracyReleased)
        {
            AccuracySuggestion.Visibility = Visibility.Collapsed;
            AccuracySelector.IsEnabled = false;
            return;
        }
        if (_qualityOperation) return;
        AccuracySelector.IsEnabled = !_recognitionOperation && _changeQuality is not null;
        AccuracyAction.IsEnabled = !_recognitionOperation;
        AccuracyDismiss.IsEnabled = !_recognitionOperation;
        AccuracyProgress.Visibility = Visibility.Collapsed;
        AccuracyCancel.Visibility = Visibility.Collapsed;
        AccuracyAction.Visibility = Visibility.Visible;
        AccuracyDismiss.Visibility = Visibility.Visible;
        bool show = _accuracy is not null && _accuracy.Model.IsSupported && _modelReady.HasValue
            && ResultQuality != OcrQuality.Accurate
            && !_accuracy.ReadSettings().AccuracySuggestionDismissed;
        AccuracySuggestion.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        OcrAccuracyOptions accuracy = _accuracy!;
        bool ready = _modelReady == true;
        string key = ready ? "Ocr_AccurateAgain" : "Ocr_DownloadAccurateAgain";
        AccuracyAction.Content = UiText.Get(key);
        AutomationProperties.SetName(AccuracyAction, UiText.Get(key));
        AccuracySuggestionText.Text = UiText.Get("Ocr_AccuracySuggestion") + (ready ? string.Empty : " " +
            UiText.Format("Ocr_AccuracyDownloadSize", Math.Ceiling(accuracy.Model.DownloadBytes / 1_000_000d)) +
            (accuracy.Model.IsMetered ? " " + UiText.Get("Ocr_AccuracyMetered") : string.Empty));
    }

    private void DismissAccuracySuggestion()
    {
        if (_accuracy is null || _qualityOperation || _recognitionOperation) return;
        OcrSettings settings = _accuracy.ReadSettings();
        bool previous = settings.AccuracySuggestionDismissed;
        try
        {
            settings.AccuracySuggestionDismissed = true;
            _accuracy.SaveSettings();
            RefreshAccuracy();
        }
        catch (Exception)
        {
            settings.AccuracySuggestionDismissed = previous;
            ShowAccuracyFailure(UiText.Get("Ocr_AccuracySaveFailed"));
        }
    }

    internal void BeginQualityOperation(bool downloading)
    {
        ++_accuracyRefreshVersion;
        _modelReadiness = null;
        _deferredSelection = _imageOverlay.Selection;
        _qualityOperation = true;
        AccuracySelector.IsEnabled = false;
        _rerunButton.IsEnabled = false;
        _tabs.IsEnabled = false;
        AccuracySuggestion.Visibility = Visibility.Visible;
        AccuracyAction.Visibility = Visibility.Collapsed;
        AccuracyDismiss.Visibility = Visibility.Collapsed;
        AccuracyCancel.Visibility = Visibility.Visible;
        AccuracyCancel.Content = UiText.Get(downloading ? "Ocr_CancelDownload" : "Ocr_CancelRecognition");
        AutomationProperties.SetName(AccuracyCancel, AccuracyCancel.Content.ToString());
        AccuracyProgress.Value = 0;
        AccuracyProgress.IsIndeterminate = !downloading;
        AccuracyProgress.Visibility = Visibility.Visible;
        AccuracySuggestionText.Text = UiText.Get(downloading ? "Ocr_ModelDownloading" : "Ocr_RecognizingAgain");
        if (downloading && _accuracy?.Model.IsMetered == true)
            AccuracySuggestionText.Text += " " + UiText.Get("Ocr_AccuracyMetered");
    }

    internal void ReportQualityProgress(double progress)
    {
        if (!_qualityOperation) return;
        AccuracyProgress.IsIndeterminate = false;
        AccuracyProgress.Value = Math.Clamp(progress, 0, 1);
        AccuracySuggestionText.Text = UiText.Format("Ocr_ModelDownloadProgress", Math.Round(AccuracyProgress.Value * 100));
    }

    internal void EndQualityOperation()
    {
        _qualityOperation = false;
        _rerunButton.IsEnabled = true;
        _tabs.IsEnabled = true;
        RestoreQualitySelection();
        RefreshAccuracy();
    }

    internal void ShowAccuracyFailure(string text)
    {
        EndQualityOperation();
        ++_accuracyRefreshVersion;
        AccuracySuggestion.Visibility = Visibility.Visible;
        AccuracySuggestionText.Text = text;
    }

    private void SetRecognitionOperation(bool active)
    {
        if (active) _accuracyReleased = false;
        _recognitionOperation = active;
        ++_accuracyRefreshVersion;
        if (active) UpdateAccuracyControls();
        else RefreshAccuracy();
    }

    private void ReleaseAccuracy()
    {
        ++_accuracyRefreshVersion;
        _modelReadiness = null;
        _recognitionOperation = false;
        _qualityOperation = false;
        _accuracyReleased = true;
        AccuracySuggestion.Visibility = Visibility.Collapsed;
        AccuracySelector.IsEnabled = false;
    }
}
