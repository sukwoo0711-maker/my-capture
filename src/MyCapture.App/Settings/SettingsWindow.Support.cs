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

    internal Action<string> OpenSupportPage { get; set; } = uri => { if (!SupportDiagnostics.TryOpenUri(uri)) throw new InvalidOperationException(UiText.Get("Support_OpenFailed")); };
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

    internal Action<SupportWindow> OpenSupportDialog { get; set; } = dialog => dialog.ShowDialog();

    private void OnSupportReport(object sender, RoutedEventArgs e)
    {
        var dialog = new SupportWindow(new SettingsSupportActions(this)) { Owner = this };
        OpenSupportDialog(dialog);
    }

    private sealed class SettingsSupportActions(SettingsWindow owner) : ISupportActions
    {
        public void CopyText(string text) => owner.CopySupportText(text);
        public bool TryOpenUri(string uri)
        {
            try { owner.OpenSupportPage(uri); return true; }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { return false; }
        }
    }
}