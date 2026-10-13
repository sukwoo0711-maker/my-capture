using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Platform;

namespace MyCapture.App.Settings;

/// <summary>The About page's privacy and feedback sections.</summary>
internal sealed partial class SettingsWindow
{
    /// <summary>
    /// Download size of the text recognition model, in MB as Windows shows sizes: the files of
    /// <c>OcrModelCatalog.Required</c>, korean_PP-OCRv5_rec_mobile.onnx (13,488,748 bytes) and
    /// ppocrv5_korean_dict.txt (47,451 bytes), 12.9 MB together.
    /// </summary>
    internal const int OcrModelMegabytes = 13;

    /// <summary>
    /// Download size of the upscaling model used by the most accurate quality: realesrgan_x4.onnx
    /// (3,158,313 bytes) and its weights (66,737,664 bytes), 66.7 MB together.
    /// </summary>
    internal const int UpscaleModelMegabytes = 67;

    internal Action<string> OpenSupportPage { get; set; } = uri => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
    internal Action<string> CopySupportText { get; set; } = text => Clipboard.SetText(text);

    private void InitializeAbout() =>
        PrivacyModelText.Text = UiText.Format("About_PrivacyModel", OcrModelMegabytes, UpscaleModelMegabytes);

    /// <summary>The diagnostics a report starts with: versions, DPI and recent errors, never paths or addresses.</summary>
    internal string SupportDiagnosticsText()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        return SupportDiagnostics.Format(
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
            Environment.OSVersion.Version.ToString(), AppPackage.IsPackaged, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
            RecentErrorLog.Shared.Snapshot());
    }

    /// <summary>
    /// Reporting a problem is one step: the diagnostics go to the clipboard, then the report page
    /// opens, and the status line says to paste them. A clipboard failure still opens the page.
    /// </summary>
    private void OnSupportReport(object sender, RoutedEventArgs e)
    {
        bool copied;
        try
        {
            CopySupportText(SupportDiagnosticsText());
            copied = true;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            _log.LogWarning(error, "Could not copy diagnostics for a problem report");
            copied = false;
        }

        try
        {
            OpenSupportPage(SupportDiagnostics.IssueUri);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            _log.LogWarning(error, "Could not open support page");
            SetStatusMessage(UiText.Get("Support_OpenFailed"));
            return;
        }

        SetStatusMessage(UiText.Get(copied ? "About_ReportCopied" : "About_ReportCopyFailed"));
    }
}
