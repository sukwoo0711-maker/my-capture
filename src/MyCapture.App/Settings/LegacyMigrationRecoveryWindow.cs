using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using MyCapture.App.Themes;
using MyCapture.Core.Storage;

namespace MyCapture.App.Settings;

/// <summary>
/// Startup recovery of an interrupted import. Hidden when it finishes within the grace period;
/// shown with a choice only when an uncommitted import still cannot finish. Either choice lets
/// MyCapture start: canceling the import keeps the library and the legacy folder as they were.
/// </summary>
internal sealed class LegacyMigrationRecoveryWindow : Window
{
    private readonly Func<Task<LegacyRecoveryResult>> _recover, _abandon;
    private readonly Task<LegacyRecoveryResult> _initial;
    private readonly TextBlock _message;
    private readonly ProgressBar _progress;
    private readonly Button _retry, _cancel;
    private readonly WrapPanel _actions;
    private bool _busy, _finished;
    internal LegacyRecoveryResult? Outcome { get; private set; }
    internal Exception? Failure { get; private set; }
    internal bool IsAskingForDecision => _actions.Visibility == Visibility.Visible;

    internal LegacyMigrationRecoveryWindow(Task<LegacyRecoveryResult> initial, Func<Task<LegacyRecoveryResult>> recover,
        Func<Task<LegacyRecoveryResult>> abandon)
    {
        _initial = initial; _recover = recover; _abandon = abandon;
        Title = UiText.Get("LegacyImport.Title");
        Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        StandardWindowTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "Surface.Raised");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        AutomationProperties.SetName(this, Title);
        _message = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite);
        _progress = new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 20, 0, 0) };
        AutomationProperties.SetName(_progress, UiText.Get("LegacyImport.ProgressName"));
        _retry = ThemedDialog.CreateActionButton(UiText.Get("LegacyImport.Retry"), primary: true);
        _retry.IsDefault = true;
        _retry.Click += async (_, _) => await RetryAsync();
        _cancel = ThemedDialog.CreateActionButton(UiText.Get("LegacyImport.Abandon"), primary: false);
        _cancel.IsCancel = true;
        _cancel.Click += async (_, _) => await AbandonAsync();
        foreach (Button button in new[] { _retry, _cancel }) AutomationProperties.SetName(button, (string)button.Content);
        _actions = ThemedDialog.CreateActionRow(_retry, _cancel);
        _actions.Margin = new Thickness(0, 20, 0, 0);
        _actions.Visibility = Visibility.Collapsed;
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Children =
            {
                KoreanWordWrap.Show(new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = TypeScale.Subtitle, FontWeight = FontWeights.SemiBold }, Title),
                _message, _progress, _actions,
            },
        };
        Loaded += async (_, _) => await TrackAsync(_initial, UiText.Get("LegacyImport.Recovering"));
    }

    /// <summary>
    /// Runs recovery before the library loads. Never throws: an unexpected failure becomes the
    /// same retry-or-cancel choice, and canceling always lets startup continue.
    /// </summary>
    internal static async Task RunAsync(Func<Task<LegacyRecoveryResult>> recover, Func<Task<LegacyRecoveryResult>> abandon,
        Func<Task> delay, Action<LegacyMigrationRecoveryWindow> show)
    {
        Task<LegacyRecoveryResult> first = Guard(recover);
        if (!first.IsCompleted) await Task.WhenAny(first, delay());
        if (first.IsCompleted && first.Result.Status == LegacyRecoveryStatus.Clean) return;
        show(new LegacyMigrationRecoveryWindow(first, () => Guard(recover), () => Guard(abandon)));
    }

    private static async Task<LegacyRecoveryResult> Guard(Func<Task<LegacyRecoveryResult>> run)
    {
        try { return await run(); }
        catch (Exception) { return new(LegacyRecoveryStatus.DecisionRequired, "IoFailure"); }
    }

    internal Task RetryAsync() => _busy ? Task.CompletedTask : TrackAsync(_recover(), UiText.Get("LegacyImport.Recovering"));

    internal async Task AbandonAsync()
    {
        if (_busy || _finished) return;
        ShowRunning(UiText.Get("LegacyImport.Canceling"));
        try { Outcome = await _abandon(); }
        catch (Exception error) { Failure = error; }
        Finish();
    }

    private async Task TrackAsync(Task<LegacyRecoveryResult> running, string status)
    {
        ShowRunning(status);
        LegacyRecoveryResult result;
        try { result = await running; }
        catch (Exception error) { Failure = error; result = new(LegacyRecoveryStatus.DecisionRequired, "IoFailure"); }
        _busy = false;
        Outcome = result;
        if (result.Status == LegacyRecoveryStatus.Clean) { Finish(); return; }
        _progress.Visibility = Visibility.Collapsed;
        _actions.Visibility = Visibility.Visible;
        _retry.IsEnabled = _cancel.IsEnabled = true;
        SetMessage(UiText.Get("LegacyImport.StartupFailed") + " " + ReasonText(result.ErrorCode)
            + Environment.NewLine + Environment.NewLine + UiText.Get("LegacyImport.StartupChoice"));
        _ = _retry.Focus();
    }

    private void ShowRunning(string status)
    {
        _busy = true;
        _retry.IsEnabled = _cancel.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        SetMessage(status);
    }

    private void SetMessage(string text)
    {
        KoreanWordWrap.Show(_message, text);
        (UIElementAutomationPeer.FromElement(_message) ?? UIElementAutomationPeer.CreatePeerForElement(_message))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    internal static string ReasonText(string? code) => UiText.Get(code switch
    {
        "InsufficientSpace" => "LegacyImport.StartupReasonSpace",
        "MissingPayload" or "InvalidJournal" => "LegacyImport.StartupReasonDamaged",
        _ => "LegacyImport.StartupReasonAccess",
    });

    private void Finish()
    {
        _busy = false;
        _finished = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The title-bar close means the same as Cancel import; it never leaves a decision open.
        if (!_finished)
        {
            e.Cancel = true;
            // Close cannot be called from Closing; cancel the import once this event returns.
            if (!_busy && IsAskingForDecision) _ = Dispatcher.BeginInvoke(new Action(() => _ = AbandonAsync()));
        }
        base.OnClosing(e);
    }
}
