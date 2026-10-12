using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Platform;

namespace MyCapture.App.Settings;

internal sealed partial class SettingsWindow
{
    internal Action<string> OpenSupportPage { get; set; } = uri => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
    internal Action<string> CopySupportText { get; set; } = text => Clipboard.SetText(text);

    private void OnDiagnosticsPreview(object sender, RoutedEventArgs e)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        DiagnosticsPreview.Text = SupportDiagnostics.Format(
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
            Environment.OSVersion.Version.ToString(), AppPackage.IsPackaged, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
            RecentErrorLog.Shared.Snapshot());
        DiagnosticsPreview.Visibility = Visibility.Visible;
        DiagnosticsCopy.Visibility = Visibility.Visible;
    }
    private void OnDiagnosticsCopy(object sender, RoutedEventArgs e)
    {
        try { CopySupportText(DiagnosticsPreview.Text); SetStatusMessage(UiText.Get("Support_Copied")); }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        { _log.LogWarning(error, "Could not copy diagnostic preview"); SetStatusMessage(UiText.Get("Support_CopyFailed")); }
    }
    private void OnSupportReport(object sender, RoutedEventArgs e)
    {
        try { OpenSupportPage(SupportDiagnostics.IssueUri); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { _log.LogWarning(error, "Could not open support page"); SetStatusMessage(UiText.Get("Support_OpenFailed")); }
    }
}
