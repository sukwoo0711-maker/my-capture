using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using MyCapture.App.Themes;
using MyCapture.Core.Settings;
using MyCapture.Platform.Shell;

namespace MyCapture.App.Settings;

/// <summary>
/// The single reusable settings window.
/// </summary>
/// <remarks>
/// <para>
/// One instance, opened from the tray's <c>SettingsRequested</c>. A normal close (the X or
/// Cancel) hides it back to the tray and discards edits by reloading the draft from the live
/// settings; only an explicit application exit closes it for real. This mirrors the gallery's
/// lifecycle, which the process's <c>OnExplicitShutdown</c> mode depends on.
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
        Action<string>? showSaveFailureMessage = null)
    {
        _currentSettings = currentSettings ?? throw new ArgumentNullException(nameof(currentSettings));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _settingsStore = settingsStore;
        _showSaveFailureMessage = showSaveFailureMessage ?? (message => MessageBox.Show(
            this, message, UiText.Get("Text_13A717215F51"), MessageBoxButton.OK, MessageBoxImage.Warning));

        InitializeComponent();
        InitializeUpdates();

        _draft = new SettingsDraft(_currentSettings());
        AttachDraftEvents();
        _settingsEdited = false;
        DataContext = _draft;

        // ApplyCommand / CancelCommand back the Ctrl+S and Esc key bindings. They are added in
        // code-behind (not XAML) because these RoutedUICommand fields are internal, which the
        // XAML x:Static resolver cannot reach at runtime; the DataContext is the SettingsDraft,
        // which intentionally exposes no commands. This mirrors GalleryWindow's approach.
        CommandBindings.Add(new CommandBinding(ApplyCommand, (_, _) => Apply()));
        CommandBindings.Add(new CommandBinding(CancelCommand, (_, _) => CancelToTray()));
        InputBindings.Add(new KeyBinding(ApplyCommand, Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(CancelCommand, Key.Escape, ModifierKeys.None));

        RefreshErrorSummary();
    }

    internal static readonly RoutedUICommand ApplyCommand =
        new(UiText.Get("Text_6A1C963D5BC5"), nameof(ApplyCommand), typeof(SettingsWindow));

    internal static readonly RoutedUICommand CancelCommand =
        new(UiText.Get("Text_BE876433993A"), nameof(CancelCommand), typeof(SettingsWindow));

    /// <summary>Raised after a successful apply, so the shell can react (e.g. refresh state).</summary>
    internal event EventHandler<SettingsApplyResult>? Applied;

    /// <summary>
    /// Shows the window, rebuilding the draft from the live settings so a value changed
    /// elsewhere (or a discarded prior edit) is reflected on reopen.
    /// </summary>
    internal void ShowSettings()
    {
        ReloadDraft();

        if (!IsVisible)
        {
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
        ++_updateProgressGeneration;
        _updates.Cancel();
        if (!_installingUpdate) _stagedUpdate?.Cleanup();
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // A normal close returns to the tray and discards edits; only an explicit exit closes.
        if (!_allowClose)
        {
            e.Cancel = true;
            CancelToTray();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        DetachDraftEvents();
        ThemeService.ApplyFromSettings(_currentSettings().General.Theme);
        base.OnClosed(e);
    }

    private void ReloadDraft() => ReloadDraftFrom(_currentSettings());

    private void ReloadDraftFrom(AppSettings settings)
    {
        DetachDraftEvents();
        _draft = new SettingsDraft(settings);
        AttachDraftEvents();
        _settingsEdited = false;
        DataContext = _draft;
        ThemeService.ApplyFromSettings(_draft.Theme);
        SetStatusMessage(string.Empty);
        RefreshErrorSummary();
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
        _settingsEdited = true;
        // ResetToDefaults raises a whole-draft notification instead of one for Theme.
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(SettingsDraft.Theme))
            ThemeService.ApplyFromSettings(_draft.Theme);
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
        _draft.ResetToDefaults();
        RefreshErrorSummary();
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
            SetStatusMessage(ex.Message);
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
            ReloadDraftFrom(imported);
            // Exports never carry the GitHub PAT, so the import must not wipe the live one.
            _draft.GitHubToken = _currentSettings().GitHub.Token;
            // Import replaces the entire draft; an empty saved token may not raise any
            // property change. Keep update/restart blocked until Apply or discard.
            _settingsEdited = true;
            SetStatusMessage(UiText.Get("Settings_ImportDone"));
            RefreshErrorSummary();
        }
        catch (Exception ex)
        {
            string failure = UiText.Get("Settings_ImportFailed");
            SetStatusMessage(string.Equals(failure, ex.Message, StringComparison.Ordinal)
                ? failure
                : failure + " " + ex.Message);
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

        AppSettings next = _draft.ToAppSettings();
        SettingsApplyResult result = _apply(next);

        if (!result.Saved)
        {
            // Persistence failed and every OS-visible change was rolled back. Keep the
            // window open and the user's draft exactly as typed — do NOT reload or hide —
            // so they can fix the underlying problem (read-only folder, full disk, path
            // conflict) and retry without re-entering everything. Its theme remains a
            // reversible preview; the shell is not notified of a saved apply.
            ThemeService.ApplyFromSettings(_draft.Theme);
            _showSaveFailureMessage(string.Join(Environment.NewLine, result.Messages));
            return;
        }

        // A partial failure (hotkey collision, autostart) reports through the result but
        // still saved the rest; reload the draft so it reflects what actually took effect.
        ReloadDraft();
        Applied?.Invoke(this, result);

        if (result.Messages.Count > 0)
        {
            MessageBox.Show(
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
        ++_updateProgressGeneration;
        _updates.Cancel();
        // Discard edits by dropping the draft, then hide.
        if (_draft is not null)
        {
            ReloadDraft();
        }
        Hide();
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
            ApplyButton.IsEnabled = true;
            return;
        }

        ErrorList.ItemsSource = errors;
        ErrorSummary.Visibility = Visibility.Visible;
        // Keep Apply pressable so a keyboard user can trigger the announce-and-focus path;
        // Apply itself refuses to close while errors exist.
        ApplyButton.IsEnabled = true;
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
