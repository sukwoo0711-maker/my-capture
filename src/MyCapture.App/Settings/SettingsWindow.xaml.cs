using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using MyCapture.App.Themes;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Settings;

/// <summary>
/// The single reusable settings window.
/// </summary>
/// <remarks>
/// <para>
/// One instance, opened from the tray's <c>SettingsRequested</c>. A normal close hides it back
/// to the tray; only an explicit application exit closes it for real. This mirrors the gallery's
/// lifecycle, which the process's <c>OnExplicitShutdown</c> mode depends on.
/// </para>
/// <para>
/// Cancel discards the edits deliberately. The title bar close, Alt+F4 and Esc first ask
/// [Apply and close][Don't apply][Cancel] when the draft holds unapplied changes, and close at
/// once when it does not. Only "Don't apply" (or Cancel) restores the saved theme.
/// </para>
/// <para>
/// The window binds to a <see cref="SettingsDraft"/> — a deep copy of the live settings — so
/// operational settings remain unchanged while editing. Theme selection previews the live
/// palette; cancelling restores the palette from the saved settings.
/// </para>
/// <para>
/// Apply validates first: if the draft has errors it does not close, reveals the error
/// summary, and moves focus there so assistive technology announces it. Only a clean draft is
/// mapped and handed to the injected apply callback.
/// </para>
/// </remarks>
internal sealed partial class SettingsWindow : Window
{
    private readonly Func<AppSettings> _currentSettings;
    private readonly Func<AppSettings, SettingsApplyResult> _apply;
    private readonly Func<AppSettings, bool, bool, SettingsApplyResult>? _applyDraft;
    private readonly Func<SettingsStore>? _settingsStore;
    private readonly ILogger _log;
    private readonly Action<string> _showSaveFailureMessage;

    private SettingsDraft _draft;
    private bool _allowClose;

    internal SettingsWindow(
        Func<AppSettings> currentSettings,
        Func<AppSettings, SettingsApplyResult> apply,
        ILogger log,
        Func<SettingsStore>? settingsStore = null,
        Action<string>? showSaveFailureMessage = null,
        Func<AppSettings, bool, bool, SettingsApplyResult>? applyDraft = null)
    {
        _currentSettings = currentSettings ?? throw new ArgumentNullException(nameof(currentSettings));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _applyDraft = applyDraft;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _settingsStore = settingsStore;
        _showSaveFailureMessage = showSaveFailureMessage ?? (message => MyCapture.App.Themes.ThemedDialog.Show(
            this, UiText.Get("Dialog.Storage"), UiText.Get("Text_13A717215F51"), MessageBoxButton.OK,
            MessageBoxImage.Warning, details: message));

        InitializeComponent();
        InitializeAbout();
        InitializeUpdates();
        InitializeStoreRating();
        InitializePrintScreenGuide();
        InitializeRecordingBorderNote();

        _draft = new SettingsDraft(_currentSettings());
        AttachDraftEvents();
        DataContext = _draft;

        // ApplyCommand / CloseCommand back the Ctrl+S and Esc key bindings. They are added in
        // code-behind (not XAML) because these RoutedUICommand fields are internal, which the
        // XAML x:Static resolver cannot reach at runtime; the DataContext is the SettingsDraft,
        // which intentionally exposes no commands. This mirrors GalleryWindow's approach.
        // Esc behaves like the title bar close (it asks about unapplied changes); the Cancel
        // button alone discards without asking.
        CommandBindings.Add(new CommandBinding(ApplyCommand, (_, _) => Apply()));
        CommandBindings.Add(new CommandBinding(CancelCommand, (_, _) => CancelToTray()));
        CommandBindings.Add(new CommandBinding(CloseCommand, (_, _) => RequestClose()));
        InputBindings.Add(new KeyBinding(ApplyCommand, Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(CloseCommand, Key.Escape, ModifierKeys.None));
        ConfirmUnappliedChanges = () => UnappliedChangesDialog.Ask(this);

        _loadedRetentionHours = _draft.ImageRetentionHours;
        RefreshErrorSummary();
        SyncGitHubTokenBox();
        RefreshRetention();
        SyncFontFamilyBox();
        RefreshCaptureFolderNote();
        RefreshCommandState();
    }

    /// <summary>Windows 10 cannot hide the capture border, so the recording tab says it stays out of the video.</summary>
    private void InitializeRecordingBorderNote() =>
        RecordingBorderNote.Visibility = WindowsFeatureSupport.HasBorderlessCapture(Environment.OSVersion.Version.Build)
            ? Visibility.Collapsed
            : Visibility.Visible;

    internal static readonly RoutedUICommand ApplyCommand =
        new(UiText.Get("Text_6A1C963D5BC5"), nameof(ApplyCommand), typeof(SettingsWindow));

    /// <summary>Discards the edits and hides the window: the Cancel button.</summary>
    internal static readonly RoutedUICommand CancelCommand =
        new(UiText.Get("Text_BE876433993A"), nameof(CancelCommand), typeof(SettingsWindow));

    /// <summary>Closes like the title bar button, asking first about unapplied changes: Esc.</summary>
    internal static readonly RoutedUICommand CloseCommand =
        new(UiText.Get("Dialog.Close"), nameof(CloseCommand), typeof(SettingsWindow));

    /// <summary>Asks what to do with unapplied changes on close. Tests replace the dialog.</summary>
    internal Func<SettingsCloseChoice> ConfirmUnappliedChanges { get; set; }

    /// <summary>Raised after a successful apply, so the shell can react (e.g. refresh state).</summary>
    internal event EventHandler<SettingsApplyResult>? Applied;

    /// <summary>
    /// Shows the window. A hidden window rebuilds its draft from the live settings so a value
    /// changed elsewhere is reflected on reopen; a window that is already open only comes to
    /// the front, keeping the edits the user has not applied yet.
    /// </summary>
    internal void ShowSettings()
    {
        if (!IsVisible)
        {
            ReloadDraft();
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        _ = Activate();
        Topmost = true;
        Topmost = false;

        // Focus the selected tab item rather than the TabControl host. The tab style draws its
        // focus indicator on TabItem.IsKeyboardFocused, so this guarantees a visible starting
        // point and lets arrow keys move between categories immediately.
        if (CategoryTabs.SelectedItem is TabItem selectedTab)
        {
            _ = selectedTab.Focus();
            _ = Keyboard.Focus(selectedTab);
        }
        else
        {
            _ = CategoryTabs.Focus();
        }
    }

    /// <summary>Closes the window for real, used only on an explicit application exit.</summary>
    internal void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // A normal close (title bar, Alt+F4) returns to the tray after asking about unapplied
        // changes; only an explicit exit closes the window for real.
        if (!_allowClose)
        {
            e.Cancel = true;
            RequestClose();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        DetachDraftEvents();
        ThemeService.ApplyFromSettings(_currentSettings().General);
        base.OnClosed(e);
    }

    private void ReloadDraft() => ReloadDraftFrom(_currentSettings());

    private void ReloadDraftFrom(AppSettings settings)
    {
        _legacySettingsDraft = null;
        _legacyProtectedBaseline = null;
        DetachDraftEvents();
        _draft = new SettingsDraft(settings);
        AttachDraftEvents();
        DataContext = _draft;
        ThemeService.ApplyFromSettings(_draft.Theme, _draft.CaptureGuideColorSetting, _draft.AccentColor);
        SetStatusMessage(string.Empty);
        _loadedRetentionHours = _draft.ImageRetentionHours;
        RefreshErrorSummary();
        SyncGitHubTokenBox();
        RefreshRetention();
        SyncFontFamilyBox();
        RefreshCaptureFolderNote();
        RefreshCommandState();
    }

    private void AttachDraftEvents()
    {
        _draft.ErrorsChanged += OnDraftErrorsChanged;
        _draft.PropertyChanged += OnDraftPropertyChanged;
    }

    private void DetachDraftEvents()
    {
        _draft.ErrorsChanged -= OnDraftErrorsChanged;
        _draft.PropertyChanged -= OnDraftPropertyChanged;
    }

    private void OnDraftErrorsChanged(object? sender, DataErrorsChangedEventArgs e) => RefreshErrorSummary();

    private void OnDraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // ResetToDefaults raises a whole-draft notification instead of one for Theme.
        bool all = string.IsNullOrEmpty(e.PropertyName);
        if (all || e.PropertyName is nameof(SettingsDraft.Theme) or nameof(SettingsDraft.AccentColor)
            or nameof(SettingsDraft.CaptureGuideColorMode) or nameof(SettingsDraft.CaptureGuideColorHex))
            ThemeService.ApplyFromSettings(_draft.Theme, _draft.CaptureGuideColorSetting, _draft.AccentColor);
        if (all || e.PropertyName == nameof(SettingsDraft.GitHubToken)) SyncGitHubTokenBox();
        if (all || e.PropertyName == nameof(SettingsDraft.ImageRetentionHours)) RefreshRetention();
        if (all || e.PropertyName == nameof(SettingsDraft.FontFamily)) SyncFontFamilyBox();
        if (all || e.PropertyName == nameof(SettingsDraft.CapturesDirectoryOverride)) RefreshCaptureFolderNote();
        RefreshCommandState();
    }

    /// <summary>
    /// Apply is enabled only while something is unapplied; with nothing to discard, Cancel reads
    /// Close. Runs on every draft change, which only compares the draft's current values.
    /// </summary>
    private void RefreshCommandState()
    {
        bool pending = _draft.HasUnappliedChanges;
        ApplyButton.IsEnabled = pending;
        string label = UiText.Get(pending ? "Text_BE876433993A" : "Dialog.Close");
        string help = UiText.Get(pending ? "SettingsUx_CancelTip" : "SettingsUx_CloseTip");
        if (!Equals(CancelButton.Content, label))
        {
            CancelButton.Content = label;
            CancelButton.ToolTip = help;
            AutomationProperties.SetName(CancelButton, label);
            AutomationProperties.SetHelpText(CancelButton, help);
        }
    }

    /// <summary>Reports settings file operations independently of the selected category.</summary>
    private void SetStatusMessage(string message)
    {
        SettingsStatus.Text = message;
        SettingsStatusRegion.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        if (SettingsStatusRegion.IsVisible)
        {
            UIElementAutomationPeer.CreatePeerForElement(SettingsStatus)
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    // ---- Commands / buttons --------------------------------------------------------

    private void OnApply(object sender, RoutedEventArgs e) => Apply();

    private void OnCancel(object sender, RoutedEventArgs e) => CancelToTray();

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        // Deliberate and reversible: resets the draft only. Nothing is written until Apply,
        // so Cancel (or reopening) restores the prior values.
        _legacySettingsDraft = null;
        _legacyProtectedBaseline = null;
        _draft.ResetToDefaults();
        RefreshErrorSummary();
        RefreshCommandState();
    }

    private void OnExportSettings(object sender, RoutedEventArgs e)
    {
        if (_settingsStore is null)
        {
            SetStatusMessage(UiText.Get("Settings_Export"));
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = UiText.Get("Settings_Export"),
            Filter = "MyCapture settings (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"MyCapture-settings-{DateTime.Now:yyyyMMdd}.json",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _settingsStore().ExportTo(_currentSettings(), dialog.FileName);
            SetStatusMessage(UiText.Get("Settings_ExportDone"));
        }
        catch (Exception ex)
        {
            MyCapture.App.Themes.ThemedDialog.Error(this, UiText.Get("Settings_Export"), ex,
                () => OnExportSettings(sender, e), UiText.Get("Dialog.ChooseLocation"));
        }
    }

    private void OnImportSettings(object sender, RoutedEventArgs e)
    {
        if (_settingsStore is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = UiText.Get("Settings_Import"),
            Filter = "MyCapture settings (*.json)|*.json",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ImportSettingsFrom(dialog.FileName);
    }

    internal void ImportSettingsFrom(string path)
    {
        if (_settingsStore is null) return;
        try
        {
            AppSettings imported = _settingsStore().ImportFrom(path);
            AppSettings current = _currentSettings();
            // Exports never carry the GitHub PAT, so the import must not wipe the live one.
            imported.GitHub.Token = current.GitHub.Token;
            imported.Hotkeys.CoreShortcutsAsked = current.Hotkeys.CoreShortcutsAsked;
            // The import replaces the values but stays an unapplied change until Apply.
            _legacySettingsDraft = null;
            _legacyProtectedBaseline = null;
            _draft.ReplaceValues(imported);
            SetStatusMessage(UiText.Get("Settings_ImportDone"));
            RefreshErrorSummary();
        }
        catch (Exception ex)
        {
            string failure = UiText.Get("Settings_ImportFailed");
            _log.LogWarning(ex, "Could not import settings");
            SetStatusMessage(ex is System.IO.InvalidDataException ? failure
                : $"{failure} {System.IO.Path.GetFileName(path)} · {ThemedDialog.RecoveryMessage(ex)}");
        }
    }

    private void Apply()
    {
        if (_draft.HasErrors)
        {
            RefreshErrorSummary();
            AnnounceErrors();
            return; // Do not close on errors.
        }

        AppSettings next = LegacyApplyCandidate(_draft.ToAppSettings());
        SettingsApplyResult result = _applyDraft?.Invoke(next, _draft.FolderDestinationEdited, _draft.ExportOptionsReplaced) ?? _apply(next);

        if (!result.Saved)
        {
            // Persistence failed and every OS-visible change was rolled back. Keep the
            // window open and the user's draft exactly as typed — do NOT reload or hide —
            // so they can fix the underlying problem (read-only folder, full disk, path
            // conflict) and retry without re-entering everything. Its theme remains a
            // reversible preview; the shell is not notified of a saved apply.
            ThemeService.ApplyFromSettings(_draft.Theme, _draft.CaptureGuideColorSetting, _draft.AccentColor);
            _showSaveFailureMessage(string.Join(Environment.NewLine, result.Messages));
            return;
        }

        // A partial failure (hotkey collision, autostart) reports through the result but
        // still saved the rest; reload the draft so it reflects what actually took effect.
        ReloadDraft();
        Applied?.Invoke(this, result);

        if (result.Messages.Count > 0)
        {
            MyCapture.App.Themes.ThemedDialog.Show(
                this,
                string.Join(Environment.NewLine, result.Messages),
                UiText.Get("Text_93CBE1538A34"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            CancelToTray(); // Clean apply with nothing to report: hide back to the tray.
        }
    }

    private void CancelToTray()
    {
        // Discard edits by dropping the draft, then hide.
        if (_draft is not null)
        {
            ReloadDraft();
        }
        Hide();
    }

    /// <summary>
    /// The title bar close, Alt+F4 and Esc. Closes at once when nothing is unapplied; otherwise
    /// asks first. Apply closes only when it applies cleanly; Cancel keeps every edit, including
    /// a previewed theme.
    /// </summary>
    internal void RequestClose()
    {
        if (_draft.HasUnappliedChanges)
        {
            switch (ConfirmUnappliedChanges())
            {
                case SettingsCloseChoice.Apply:
                    Apply();
                    return;
                case SettingsCloseChoice.Discard:
                    break;
                default:
                    return;
            }
        }

        CancelToTray();
    }

    internal bool PrepareForAppExit(bool keepVisible)
    {
        if (!_draft.HasUnappliedChanges) return true;
        switch (ConfirmUnappliedChanges())
        {
            case SettingsCloseChoice.Apply:
                Apply();
                if (_draft.HasUnappliedChanges) return false;
                if (keepVisible) ShowSettings();
                return true;
            case SettingsCloseChoice.Discard:
                ReloadDraft();
                return true;
            default:
                return false;
        }
    }

    // ---- GitHub token ---------------------------------------------------------------

    private bool _syncingGitHubToken;

    /// <summary>
    /// Mirrors the draft token into the PasswordBox, which cannot bind. The value is copied in
    /// memory only; it is never logged or exposed as an automation name.
    /// </summary>
    private void SyncGitHubTokenBox()
    {
        if (string.Equals(GitHubTokenBox.Password.Trim(), _draft.GitHubToken, StringComparison.Ordinal)) return;
        _syncingGitHubToken = true;
        try { GitHubTokenBox.Password = _draft.GitHubToken; }
        finally { _syncingGitHubToken = false; }
    }

    private void OnGitHubTokenChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingGitHubToken) return;
        _draft.GitHubToken = GitHubTokenBox.Password;
    }

    // ---- Folder browse -------------------------------------------------------------

    private void OnBrowseCapturesDirectory(object sender, RoutedEventArgs e) =>
        BrowseInto(value => _draft.CapturesDirectoryOverride = value, _draft.CapturesDirectoryOverride, UiText.Get("Text_F04B7153934A"));

    private void OnBrowseQuickSaveDirectory(object sender, RoutedEventArgs e) =>
        BrowseInto(value => _draft.QuickSaveDirectoryOverride = value, _draft.QuickSaveDirectoryOverride, UiText.Get("Text_0CC5375DCF91"));

    private void BrowseInto(Action<string> assign, string current, string title)
    {
        IntPtr owner = new WindowInteropHelper(this).Handle;
        string? chosen = FolderBrowseDialog.Browse(owner, title, string.IsNullOrWhiteSpace(current) ? null : current);
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            assign(chosen);
        }
    }

    // ---- Error summary -------------------------------------------------------------

    private void RefreshErrorSummary()
    {
        IReadOnlyList<string> errors = _draft.AllErrors();
        if (errors.Count == 0)
        {
            ErrorSummary.Visibility = Visibility.Collapsed;
            ErrorList.ItemsSource = null;
            RefreshCommandState();
            return;
        }

        ErrorList.ItemsSource = errors;
        ErrorSummary.Visibility = Visibility.Visible;
        // An invalid entry is an unapplied change, so Apply stays pressable and a keyboard user
        // reaches the announce-and-focus path; Apply itself refuses to close while errors exist.
        RefreshCommandState();
    }

    private void AnnounceErrors()
    {
        // The summary border is an assertive live region; raising a peer notification nudges
        // screen readers to read it, and moving focus there makes the errors reachable.
        if (ErrorSummary.Visibility != Visibility.Visible)
        {
            return;
        }

        ErrorSummaryHeading.Focusable = true;
        _ = ErrorSummaryHeading.Focus();

        AutomationPeer? peer = UIElementAutomationPeer.CreatePeerForElement(ErrorSummary);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
