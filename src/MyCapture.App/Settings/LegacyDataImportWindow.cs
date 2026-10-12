using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MyCapture.App.Themes;
using MyCapture.Core.Storage;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Settings;

internal sealed record LegacyImportSelection(LegacyMigrationResult Library, bool IncludeSettings);

/// <summary>The app supplies its quiesced queue and shared save/discard/cancel protection.</summary>
internal sealed record LegacyImportOperations(
    ILegacyLibraryMigrationService Service, AppPaths TargetPaths,
    Func<CancellationToken, Task<bool>> Prepare, Action Release,
    Func<string, IProgress<LegacyMigrationProgress>, CancellationToken, Task<LegacyMigrationResult>> Import,
    Action Refresh, Func<Task> Recover);

internal sealed class LegacyDataImportWindow : Window
{
    private readonly LegacyImportOperations _operations;
    private CancellationTokenSource? _work;
    private long _generation;
    private bool _copying, _closed, _closeWhenStopped, _hasSettings;
    private bool _recoveryRequired, _protectionHeld;
    private readonly TextBlock _source, _summary, _status;
    private readonly Button _choose, _copy, _cancel;
    private readonly CheckBox _includeSettings;
    private readonly ProgressBar _progress;
    internal string SourceRoot { get; private set; }
    internal LegacyImportSelection? Selection { get; private set; }
    internal bool IsWorking => _copying;
    internal bool IsRecoveryRequired => _recoveryRequired;

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
        _copy.Click += async (_, _) => await ImportAsync();
        _cancel = MakeButton(UiText.Get("Dialog.Close"), "Button.Secondary");
        _cancel.Click += (_, _) => RequestDismissal();
        actions.Children.Add(_copy); actions.Children.Add(_cancel);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var feedback = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        feedback.Children.Add(_status);
        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
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
        panel.Children.Add(new TextBlock { Text = UiText.Get("LegacyImport.KeepLibrary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(new TextBlock { Text = UiText.Get("LegacyImport.SettingsDraft"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) });
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Loaded += async (_, _) => await ScanAsync();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; RequestDismissal(); } };
        Closed += (_, _) => { _closed = true; ++_generation; _work?.Cancel(); };
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
        _status.Text = UiText.Get("LegacyImport.Scanning");
        _progress.Visibility = Visibility.Visible; _progress.IsIndeterminate = true;
        try
        {
            LegacyMigrationScan scan = await _operations.Service.ScanAsync(SourceRoot, _operations.TargetPaths, _work.Token);
            if (_closed || generation != _generation) return;
            _summary.Text = UiText.Format("LegacyImport.Counts", scan.ImageCount, scan.VideoCount, scan.FolderCount);
            _hasSettings = scan.HasSettings;
            _includeSettings.IsEnabled = scan.HasSettings && scan.Status == LegacyMigrationScanStatus.Ready;
            if (!_includeSettings.IsEnabled) _includeSettings.IsChecked = false;
            _copy.IsEnabled = scan.Status == LegacyMigrationScanStatus.Ready;
            _status.Text = scan.Status switch
            {
                LegacyMigrationScanStatus.Ready => UiText.Get("LegacyImport.Ready"),
                LegacyMigrationScanStatus.NoData => UiText.Get("LegacyImport.NoData"),
                _ => ErrorText(scan.ErrorCode),
            };
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        { if (!_closed && generation == _generation) _status.Text = UiText.Format("LegacyImport.FailedDetail", ThemedDialog.RecoveryMessage(error)); }
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
        _copying = true;
        long generation = ++_generation;
        _work?.Cancel(); _work?.Dispose(); _work = new();
        bool includeSettings = _includeSettings.IsChecked == true;
        bool importStarted = false;
        _choose.IsEnabled = _copy.IsEnabled = _includeSettings.IsEnabled = false;
        _cancel.Content = UiText.Get("Dialog.Cancel");
        _status.Text = UiText.Get("LegacyImport.Preparing");
        _progress.Visibility = Visibility.Visible; _progress.IsIndeterminate = true;
        try
        {
            if (!await _operations.Prepare(_work.Token)) { _status.Text = UiText.Get("LegacyImport.Deferred"); return; }
            _protectionHeld = true;
            _work.Token.ThrowIfCancellationRequested();
            importStarted = true;
            LegacyMigrationResult result = await _operations.Import(SourceRoot,
                new DispatcherProgress(Dispatcher, value =>
                {
                    if (_closed || !_copying || generation != _generation) return;
                    _status.Text = value.Stage switch
                    {
                        LegacyMigrationStage.Publishing => UiText.Get("LegacyImport.Publishing"),
                        LegacyMigrationStage.Recovering => UiText.Get("LegacyImport.Recovering"),
                        LegacyMigrationStage.Scanning => UiText.Get("LegacyImport.Scanning"),
                        _ => UiText.Format("LegacyImport.CopyProgress", value.CompletedItems, value.TotalItems),
                    };
                    _progress.IsIndeterminate = value.Stage != LegacyMigrationStage.Copying || value.TotalBytes <= 0;
                    if (!_progress.IsIndeterminate)
                        _progress.Value = Math.Clamp((double)value.CopiedBytes / value.TotalBytes * 100, 0, 100);
                }), _work.Token);
            if (result.Status == LegacyMigrationStatus.Completed)
            {
                Selection = new(result, includeSettings);
                _operations.Refresh();
                _status.Text = UiText.Format("LegacyImport.Completed", result.ImportedCount, result.SkippedCount);
            }
            else
            {
                _recoveryRequired = result.ErrorCode == "RecoveryRequired";
                _status.Text = result.Status == LegacyMigrationStatus.Canceled ? UiText.Get("LegacyImport.Canceled") : ErrorText(result.ErrorCode);
            }
        }
        catch (OperationCanceledException)
        {
            _recoveryRequired = importStarted;
            _status.Text = _recoveryRequired ? ErrorText("RecoveryRequired") : UiText.Get("LegacyImport.Canceled");
        }
        catch (Exception error)
        {
            _recoveryRequired = importStarted;
            _status.Text = UiText.Format("LegacyImport.FailedDetail", ThemedDialog.RecoveryMessage(error));
            if (_recoveryRequired) _status.Text += " " + ErrorText("RecoveryRequired");
        }
        finally
        {
            _copying = false; ++_generation;
            if (!_recoveryRequired) ReleaseProtection();
            ResetActions();
            if (_closeWhenStopped && !_recoveryRequired) Close();
        }
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
        _copy.IsEnabled = _cancel.IsEnabled = false;
        _status.Text = UiText.Get("LegacyImport.Recovering");
        _progress.Visibility = Visibility.Visible; _progress.IsIndeterminate = true;
        try
        {
            // Publication recovery is not canceled midway and never releases a stale live queue.
            await _operations.Recover();
            _recoveryRequired = false;
            _operations.Refresh();
            ReleaseProtection();
            _status.Text = UiText.Get("LegacyImport.Recovered");
        }
        catch (Exception error)
        {
            _status.Text = UiText.Format("LegacyImport.FailedDetail", ThemedDialog.RecoveryMessage(error))
                + " " + ErrorText("RecoveryRequired");
        }
        finally { _copying = false; ResetActions(); }
    }

    private void ReleaseProtection()
    {
        if (!_protectionHeld) return;
        _operations.Release();
        _protectionHeld = false;
    }

    private void ResetActions()
    {
        _progress.Visibility = Visibility.Collapsed;
        _cancel.Content = UiText.Get("Dialog.Close");
        _cancel.IsEnabled = !_recoveryRequired;
        _copy.Content = UiText.Get(_recoveryRequired ? "LegacyImport.RetryRecovery" : "LegacyImport.Copy");
        AutomationProperties.SetName(_copy, (string)_copy.Content);
        _copy.IsEnabled = Selection is null;
        _choose.IsEnabled = Selection is null && !_recoveryRequired;
        _includeSettings.IsEnabled = Selection is null && _hasSettings && !_recoveryRequired;
        if (Selection is not null) _copy.Visibility = Visibility.Collapsed;
        if (_recoveryRequired) _closeWhenStopped = false;
    }

    private void RequestDismissal()
    {
        if (_recoveryRequired) return;
        if (!_copying) { Close(); return; }
        _closeWhenStopped = true;
        _work?.Cancel();
        _status.Text = UiText.Get("LegacyImport.Canceling");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_copying || _recoveryRequired) { e.Cancel = true; RequestDismissal(); }
        base.OnClosing(e);
    }

    internal static string ErrorText(string? code) => UiText.Get(code switch
    {
        "SourceMissing" or "InvalidSource" => "LegacyImport.InvalidSource",
        "InvalidTarget" => "LegacyImport.InvalidTarget",
        "OverlappingRoots" => "LegacyImport.OverlappingRoots",
        "TargetChanged" => "LegacyImport.TargetChanged",
        "RecoveryRequired" => "LegacyImport.RecoveryRequired",
        "SourceChanged" => "LegacyImport.SourceChanged",
        "MissingPayload" => "LegacyImport.MissingPayload",
        _ => "LegacyImport.Failed",
    });
}
