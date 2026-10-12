using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Storage;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Settings;

/// <param name="CompletedAfterCancel">Cancel arrived after publication began; settings are then not loaded.</param>
internal sealed record LegacyImportSelection(LegacyMigrationResult Library, bool IncludeSettings, bool CompletedAfterCancel = false);

/// <summary>The app supplies its quiesced queue and shared save/discard/cancel protection.</summary>
internal sealed record LegacyImportOperations(
    ILegacyLibraryMigrationService Service, AppPaths TargetPaths,
    Func<CancellationToken, Task<bool>> Prepare, Action Release,
    Func<string, IProgress<LegacyMigrationProgress>, CancellationToken, Task<LegacyMigrationResult>> Import,
    Action Refresh, Func<Task> Recover);

/// <summary>
/// Scanning and copying can be canceled; publication is one short index write that is not.
/// The window can always be closed once no write is running, including after a failure.
/// </summary>
internal sealed class LegacyDataImportWindow : Window
{
    private readonly LegacyImportOperations _operations;
    private CancellationTokenSource? _work;
    private long _generation;
    private bool _copying, _publishing, _cancelRequested, _closed, _closeWhenStopped, _hasSettings;
    private bool _recoveryRequired, _protectionHeld;
    private Task _activeWork = Task.CompletedTask;
    private LegacyMigrationStage? _announcedStage;
    private readonly TextBlock _source, _summary, _settingsNote, _status;
    private readonly Button _choose, _copy, _cancel;
    private readonly CheckBox _includeSettings;
    private readonly ProgressBar _progress;
    internal string SourceRoot { get; private set; }
    internal LegacyImportSelection? Selection { get; private set; }
    internal bool IsWorking => _copying;
    internal bool IsRecoveryRequired => _recoveryRequired;
    internal int AnnouncementCountForTest { get; private set; }

    internal LegacyDataImportWindow(LegacyImportOperations operations, string sourceRoot)
    {
        _operations = operations;
        SourceRoot = sourceRoot;
        Title = UiText.Get("LegacyImport.Title");
        Width = 560; MinWidth = 420; Height = 500; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        StandardWindowTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "Surface.Raised");
        SetResourceReference(ForegroundProperty, "Text.Primary");
        AutomationProperties.SetName(this, Title);
        var root = new DockPanel { Margin = new Thickness(24) };
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        _copy = MakeButton(UiText.Get("LegacyImport.Copy"), "Button.Primary");
        _copy.IsEnabled = false;
        _copy.IsDefault = true;
        _copy.Click += async (_, _) => await ImportAsync();
        _cancel = MakeButton(UiText.Get("Dialog.Close"), "Button.Secondary");
        // Esc follows the dismissal button: it closes, cancels a copy, and waits out publication.
        _cancel.IsCancel = true;
        _cancel.Click += (_, _) => RequestDismissal();
        actions.Children.Add(_copy); actions.Children.Add(_cancel);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var feedback = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        feedback.Children.Add(_status);
        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(_progress, UiText.Get("LegacyImport.ProgressName"));
        feedback.Children.Add(_progress);
        DockPanel.SetDock(feedback, Dock.Bottom); root.Children.Add(feedback);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = UiText.Get("LegacyImport.Description"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16) });
        panel.Children.Add(new TextBlock { Text = UiText.Get("LegacyImport.Source"), FontWeight = FontWeights.SemiBold });
        _source = new TextBlock { Text = sourceRoot, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
        panel.Children.Add(_source);
        _choose = MakeButton(UiText.Get("LegacyImport.ChooseFolder"), "Button.Secondary");
        _choose.HorizontalAlignment = HorizontalAlignment.Left;
        _choose.Click += async (_, _) =>
        {
            string? selected = FolderBrowseDialog.Browse(new System.Windows.Interop.WindowInteropHelper(this).Handle,
                UiText.Get("LegacyImport.ChooseFolder"), SourceRoot);
            if (selected is null) return;
            SourceRoot = selected; _source.Text = selected;
            await ScanAsync();
        };
        panel.Children.Add(_choose);
        _summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) };
        panel.Children.Add(_summary);
        _includeSettings = new CheckBox { Content = UiText.Get("LegacyImport.IncludeSettings"), IsChecked = false, IsEnabled = false };
        AutomationProperties.SetName(_includeSettings, UiText.Get("LegacyImport.IncludeSettings"));
        panel.Children.Add(_includeSettings);
        _settingsNote = new TextBlock { Text = UiText.Get("LegacyImport.SettingsUnreadable"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
        panel.Children.Add(_settingsNote);
        panel.Children.Add(new TextBlock { Text = UiText.Get("LegacyImport.KeepLibrary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(new TextBlock { Text = UiText.Get("LegacyImport.SettingsDraft"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) });
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Loaded += async (_, _) => await ScanAsync();
        Closed += (_, _) =>
        {
            _closed = true; ++_generation; _work?.Cancel();
            // Closing after a failure returns every protected window and the exit command.
            ReleaseProtection();
        };
    }

    private Button MakeButton(string text, string style)
    {
        var button = new Button { Content = text, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        button.SetResourceReference(StyleProperty, style);
        AutomationProperties.SetName(button, text);
        return button;
    }

    internal async Task ScanAsync()
    {
        long generation = ++_generation;
        _work?.Cancel(); _work?.Dispose(); _work = new();
        _choose.IsEnabled = _copy.IsEnabled = _includeSettings.IsEnabled = false;
        SetStatus(UiText.Get("LegacyImport.Scanning"));
        _progress.Visibility = Visibility.Visible; _progress.IsIndeterminate = true;
        try
        {
            LegacyMigrationScan scan = await _operations.Service.ScanAsync(SourceRoot, _operations.TargetPaths, _work.Token);
            if (_closed || generation != _generation) return;
            _summary.Text = UiText.Format("LegacyImport.Counts", scan.ImageCount, scan.VideoCount, scan.FolderCount)
                + (scan.UnusableCount > 0 ? Environment.NewLine + UiText.Format("LegacyImport.Unusable", scan.UnusableCount) : "");
            _settingsNote.Visibility = scan.SettingsUnreadable ? Visibility.Visible : Visibility.Collapsed;
            _hasSettings = scan.HasSettings;
            _includeSettings.IsEnabled = scan.HasSettings && scan.Status == LegacyMigrationScanStatus.Ready;
            if (!_includeSettings.IsEnabled) _includeSettings.IsChecked = false;
            _copy.IsEnabled = scan.Status == LegacyMigrationScanStatus.Ready;
            SetStatus(scan.Status switch
            {
                LegacyMigrationScanStatus.Ready => UiText.Get("LegacyImport.Ready"),
                LegacyMigrationScanStatus.NoData => UiText.Get("LegacyImport.NoData"),
                _ => ErrorText(scan.ErrorCode),
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        { if (!_closed && generation == _generation) SetStatus(UiText.Format("LegacyImport.FailedDetail", ThemedDialog.RecoveryMessage(error))); }
        finally
        {
            if (!_closed && generation == _generation)
            { _choose.IsEnabled = true; _progress.Visibility = Visibility.Collapsed; }
        }
    }

    internal async Task ImportAsync()
    {
        if (_copying || !_copy.IsEnabled) return;
        if (_recoveryRequired) { await RetryRecoveryAsync(); return; }
        _copying = true; _publishing = false; _cancelRequested = false; _announcedStage = null;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeWork = stopped.Task;
        long generation = ++_generation;
        _work?.Cancel(); _work?.Dispose(); _work = new();
        bool includeSettings = _includeSettings.IsChecked == true;
        bool importStarted = false;
        _choose.IsEnabled = _copy.IsEnabled = _includeSettings.IsEnabled = false;
        SetCancel(UiText.Get("Dialog.Cancel"), enabled: true);
        SetStatus(UiText.Get("LegacyImport.Preparing"));
        _progress.Visibility = Visibility.Visible; _progress.IsIndeterminate = true;
        try
        {
            if (!await _operations.Prepare(_work.Token)) { SetStatus(UiText.Get("LegacyImport.Deferred")); return; }
            _protectionHeld = true;
            _work.Token.ThrowIfCancellationRequested();
            importStarted = true;
            LegacyMigrationResult result = await _operations.Import(SourceRoot,
                new DispatcherProgress(Dispatcher, value =>
                {
                    if (_closed || !_copying || generation != _generation) return;
                    ShowProgress(value);
                }), _work.Token);
            if (result.Status == LegacyMigrationStatus.Completed)
            {
                // A cancel that arrived after publication began cannot undo it; it still withholds the settings draft.
                bool afterCancel = _cancelRequested;
                _operations.Refresh();
                Selection = new(result, includeSettings && !afterCancel, afterCancel);
                SetStatus(afterCancel ? CompletedBeforeCancelText(result) : CompletedText(result));
            }
            else
            {
                _recoveryRequired = result.ErrorCode == "RecoveryRequired";
                SetStatus(result.Status == LegacyMigrationStatus.Canceled ? UiText.Get("LegacyImport.Canceled") : ErrorText(result));
            }
        }
        catch (OperationCanceledException)
        {
            // The service rolls back every cancellation before it returns; nothing is left to recover.
            SetStatus(UiText.Get("LegacyImport.Canceled"));
        }
        catch (Exception error)
        {
            _recoveryRequired = importStarted;
            SetStatus(UiText.Format("LegacyImport.FailedDetail", ThemedDialog.RecoveryMessage(error))
                + (_recoveryRequired ? " " + ErrorText("RecoveryRequired") : ""));
        }
        finally
        {
            _copying = false; _publishing = false; ++_generation;
            if (!_recoveryRequired) ReleaseProtection();
            ResetActions();
            stopped.TrySetResult();
            if (_closeWhenStopped && !_closed) Close();
        }
    }

    private void ShowProgress(LegacyMigrationProgress value)
    {
        if (value.Stage == LegacyMigrationStage.Publishing && !_publishing)
        {
            _publishing = true;
            // The single index write that follows cannot be canceled; say so instead of pretending.
            SetCancel(UiText.Get("Dialog.Cancel"), enabled: false);
        }
        if (_cancelRequested && !_publishing) return;
        string text = value.Stage switch
        {
            LegacyMigrationStage.Publishing => UiText.Get(_cancelRequested ? "LegacyImport.PublishingAfterCancel" : "LegacyImport.PublishingNoCancel"),
            LegacyMigrationStage.Recovering => UiText.Get("LegacyImport.Recovering"),
            LegacyMigrationStage.Scanning when value.TotalItems > 0 =>
                UiText.Format("LegacyImport.ScanProgress", value.CompletedItems, value.TotalItems),
            LegacyMigrationStage.Scanning => UiText.Get("LegacyImport.Scanning"),
            _ => UiText.Format("LegacyImport.CopyProgress", value.CompletedItems, value.TotalItems),
        };
        // Screen readers hear each stage once, not every progress tick.
        SetStatus(text, announce: value.Stage != _announcedStage);
        _announcedStage = value.Stage;
        double? fraction = value.Stage switch
        {
            LegacyMigrationStage.Copying when value.TotalBytes > 0 => (double)value.CopiedBytes / value.TotalBytes,
            LegacyMigrationStage.Scanning when value.TotalItems > 0 => (double)value.CompletedItems / value.TotalItems,
            _ => null,
        };
        _progress.IsIndeterminate = fraction is null;
        if (fraction is { } done) _progress.Value = Math.Clamp(done * 100, 0, 100);
    }

    private sealed class DispatcherProgress(Dispatcher dispatcher, Action<LegacyMigrationProgress> apply)
        : IProgress<LegacyMigrationProgress>
    {
        public void Report(LegacyMigrationProgress value)
        {
            if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
                _ = dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => apply(value)));
        }
    }

    internal async Task RetryRecoveryAsync()
    {
        if (_copying || !_recoveryRequired) return;
        _copying = true;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeWork = stopped.Task;
        _copy.IsEnabled = false;
        SetCancel(UiText.Get("Dialog.Close"), enabled: false);
        SetStatus(UiText.Get("LegacyImport.Recovering"));
        _progress.Visibility = Visibility.Visible; _progress.IsIndeterminate = true;
        try
        {
            // Publication recovery is not canceled midway and never releases a stale live queue.
            await _operations.Recover();
            _operations.Refresh();
            ReleaseProtection();
            _recoveryRequired = false;
            SetStatus(UiText.Get("LegacyImport.Recovered"));
        }
        catch (Exception error)
        {
            SetStatus(UiText.Format("LegacyImport.FailedDetail", ThemedDialog.RecoveryMessage(error))
                + " " + ErrorText("RecoveryRequired"));
        }
        finally
        {
            _copying = false;
            ResetActions();
            stopped.TrySetResult();
            if (_closeWhenStopped && !_closed) Close();
        }
    }

    /// <summary>App exit: cancel a copy, let a running publication finish, then close.</summary>
    internal async Task StopForExitAsync()
    {
        _closeWhenStopped = true;
        if (_copying && !_publishing && !_recoveryRequired) CancelCopy();
        await _activeWork;
        if (!_closed) Close();
    }

    private void ReleaseProtection()
    {
        if (!_protectionHeld) return;
        _operations.Release();
        _protectionHeld = false;
    }

    private void SetCancel(string text, bool enabled)
    {
        _cancel.Content = text;
        AutomationProperties.SetName(_cancel, text);
        _cancel.IsEnabled = enabled;
    }

    private void ResetActions()
    {
        _progress.Visibility = Visibility.Collapsed;
        SetCancel(UiText.Get("Dialog.Close"), enabled: true);
        _copy.Content = UiText.Get(_recoveryRequired ? "LegacyImport.RetryRecovery" : "LegacyImport.Copy");
        AutomationProperties.SetName(_copy, (string)_copy.Content);
        _copy.IsEnabled = Selection is null;
        _choose.IsEnabled = Selection is null && !_recoveryRequired;
        _includeSettings.IsEnabled = Selection is null && _hasSettings && !_recoveryRequired;
        if (Selection is null) return;
        // The finished action leaves the dialog; focus and Enter move to the remaining Close button.
        bool focusLost = FocusManager.GetFocusedElement(this) is not UIElement focused
            || ReferenceEquals(focused, _copy) || !focused.IsEnabled;
        _copy.Visibility = Visibility.Collapsed;
        _copy.IsDefault = false;
        _cancel.IsDefault = true;
        if (focusLost)
        {
            FocusManager.SetFocusedElement(this, _cancel);
            _ = _cancel.Focus();
        }
    }

    private void RequestDismissal()
    {
        if (!_copying) { Close(); return; }
        _closeWhenStopped = true;
        // The window closes as soon as the index write or a recovery attempt finishes.
        if (_publishing || _recoveryRequired) return;
        CancelCopy();
    }

    private void CancelCopy()
    {
        _cancelRequested = true;
        _work?.Cancel();
        SetStatus(UiText.Get("LegacyImport.Canceling"));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Only a running write defers closing; a failure or pending recovery never traps the window.
        if (_copying) { e.Cancel = true; RequestDismissal(); }
        base.OnClosing(e);
    }

    private void SetStatus(string text, bool announce = true)
    {
        _status.Text = text;
        if (!announce) return;
        AnnouncementCountForTest++;
        (UIElementAutomationPeer.FromElement(_status) ?? UIElementAutomationPeer.CreatePeerForElement(_status))
            ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    internal static string CompletedText(LegacyMigrationResult result) => result.UnusableCount > 0
        ? UiText.Format("LegacyImport.CompletedWithUnusable", result.ImportedCount, result.SkippedCount, result.UnusableCount)
        : UiText.Format("LegacyImport.Completed", result.ImportedCount, result.SkippedCount);

    internal static string CompletedBeforeCancelText(LegacyMigrationResult result) =>
        UiText.Get("LegacyImport.CompletedBeforeCancel") + " " + CompletedText(result);

    internal static string ErrorText(LegacyMigrationResult result) =>
        result.ErrorCode == "InsufficientSpace" && result.RequiredBytes > result.AvailableBytes
            ? UiText.Format("LegacyImport.InsufficientSpaceAmount", FormatBytes(result.RequiredBytes - result.AvailableBytes))
            : ErrorText(result.ErrorCode);

    internal static string ErrorText(string? code) => UiText.Get(code switch
    {
        "SourceMissing" or "InvalidSource" => "LegacyImport.InvalidSource",
        "InvalidTarget" => "LegacyImport.InvalidTarget",
        "OverlappingRoots" => "LegacyImport.OverlappingRoots",
        "TargetChanged" => "LegacyImport.TargetChanged",
        "RecoveryRequired" => "LegacyImport.RecoveryRequired",
        "SourceChanged" => "LegacyImport.SourceChanged",
        "MissingPayload" => "LegacyImport.MissingPayload",
        "MissingIndex" => "LegacyImport.MissingIndex",
        "InsufficientSpace" => "LegacyImport.InsufficientSpace",
        "TargetUnreadable" => "LegacyImport.TargetUnreadable",
        "InvalidJournal" => "LegacyImport.InvalidJournal",
        _ => "LegacyImport.Failed",
    });

    internal static string FormatBytes(long bytes)
    {
        double megabytes = Math.Max(bytes, 0) / 1024.0 / 1024.0;
        return megabytes < 1024
            ? Math.Ceiling(megabytes).ToString("0", CultureInfo.CurrentCulture) + " MB"
            : (Math.Ceiling(megabytes / 1024 * 10) / 10).ToString("0.0", CultureInfo.CurrentCulture) + " GB";
    }
}
