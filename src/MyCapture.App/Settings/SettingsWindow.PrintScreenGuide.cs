using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;

namespace MyCapture.App.Settings;

/// <summary>
/// Inline guidance on the shortcuts page when a shortcut uses PrintScreen while Windows opens
/// its own capture tool with that key.
/// </summary>
/// <remarks>
/// The Windows setting is only read, never changed: the guide explains the conflict and opens
/// the Windows Settings page where the user decides. It is re-read when the window is activated
/// so returning from Windows Settings updates the guide without reopening the window.
/// </remarks>
internal sealed partial class SettingsWindow
{
    private IWindowsPrintScreenSetting _printScreenSetting = new RegistryPrintScreenSetting();
    private bool _windowsPrintScreenCaptureEnabled;
    private SettingsDraft? _printScreenGuideDraft;

    /// <summary>Source of the Windows Print Screen setting; tests substitute a fake.</summary>
    internal IWindowsPrintScreenSetting PrintScreenSetting
    {
        get => _printScreenSetting;
        set
        {
            _printScreenSetting = value ?? throw new ArgumentNullException(nameof(value));
            ReadWindowsPrintScreenSetting();
            RefreshPrintScreenGuide();
        }
    }

    internal bool IsPrintScreenGuideVisible => PrtScGuidePanel.Visibility == Visibility.Visible;

    private void InitializePrintScreenGuide()
    {
        string message = PrintScreenKeyGuide.Message();
        Themes.KoreanWordWrap.Show(PrtScGuideMessage, message);
        System.Windows.Automation.AutomationProperties.SetHelpText(PrtScGuideOpenSettings, message);
        DataContextChanged += (_, _) =>
        {
            if (_printScreenGuideDraft is not null) _printScreenGuideDraft.PropertyChanged -= OnPrintScreenGuideDraftChanged;
            _printScreenGuideDraft = DataContext as SettingsDraft;
            if (_printScreenGuideDraft is not null) _printScreenGuideDraft.PropertyChanged += OnPrintScreenGuideDraftChanged;
            ReadWindowsPrintScreenSetting();
            RefreshPrintScreenGuide();
        };
        Activated += (_, _) =>
        {
            ReadWindowsPrintScreenSetting();
            RefreshPrintScreenGuide();
        };
    }

    private void OnPrintScreenGuideDraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Every shortcut property of the draft ends in "Hotkey"; a null name is a whole reload.
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName.EndsWith("Hotkey", StringComparison.Ordinal))
            RefreshPrintScreenGuide();
    }

    private void ReadWindowsPrintScreenSetting() =>
        _windowsPrintScreenCaptureEnabled = PrintScreenKeyGuide.IsWindowsCaptureKeyEnabled(
            _printScreenSetting, Environment.OSVersion.Version.Build);

    private void RefreshPrintScreenGuide()
    {
        bool show = _printScreenGuideDraft is { } draft
            && PrintScreenKeyGuide.ShouldWarn(draft.ToHotkeySettings(), _windowsPrintScreenCaptureEnabled);
        Visibility next = show ? Visibility.Visible : Visibility.Collapsed;
        if (PrtScGuidePanel.Visibility == next) return;
        PrtScGuidePanel.Visibility = next;
        if (show && PrtScGuidePanel.IsVisible)
        {
            UIElementAutomationPeer.CreatePeerForElement(PrtScGuidePanel)
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private void OnPrtScGuideOpenSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(PrintScreenKeyGuide.WindowsSettingsUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log.LogWarning(ex, "Could not open Windows keyboard settings");
            SetStatusMessage(PrintScreenKeyGuide.OpenSettingsFailed());
        }
    }
}
