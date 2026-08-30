using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
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
/// nothing the user types can leak into a capture taken while the window is open, and Cancel
/// is a guaranteed no-op on the running configuration.
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
    private readonly ILogger _log;

    private SettingsDraft _draft;
    private bool _allowClose;

    internal SettingsWindow(
        Func<AppSettings> currentSettings,
        Func<AppSettings, SettingsApplyResult> apply,
        ILogger log)
    {
        _currentSettings = currentSettings ?? throw new ArgumentNullException(nameof(currentSettings));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        InitializeComponent();

        _draft = new SettingsDraft(_currentSettings());
        _draft.ErrorsChanged += (_, _) => RefreshErrorSummary();
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
        new("적용", nameof(ApplyCommand), typeof(SettingsWindow));

    internal static readonly RoutedUICommand CancelCommand =
        new("취소", nameof(CancelCommand), typeof(SettingsWindow));

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

    private void ReloadDraft()
    {
        _draft = new SettingsDraft(_currentSettings());
        _draft.ErrorsChanged += (_, _) => RefreshErrorSummary();
        DataContext = _draft;
        RefreshErrorSummary();
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
            // conflict) and retry without re-entering everything. Nothing took effect, so
            // the shell is not notified of an apply.
            MessageBox.Show(
                this,
                string.Join(Environment.NewLine, result.Messages),
                "설정 저장 실패",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
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
                "설정 적용",
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
        ReloadDraft();
        Hide();
    }

    // ---- Folder browse -------------------------------------------------------------

    private void OnBrowseCapturesDirectory(object sender, RoutedEventArgs e) =>
        BrowseInto(value => _draft.CapturesDirectoryOverride = value, _draft.CapturesDirectoryOverride, "캡처 저장 폴더 선택");

    private void OnBrowseQuickSaveDirectory(object sender, RoutedEventArgs e) =>
        BrowseInto(value => _draft.QuickSaveDirectoryOverride = value, _draft.QuickSaveDirectoryOverride, "빠른 저장 폴더 선택");

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
