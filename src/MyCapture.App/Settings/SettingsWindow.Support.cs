using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using MyCapture.Ocr;

namespace MyCapture.App.Settings;

/// <summary>The About page's privacy and feedback sections.</summary>
internal sealed partial class SettingsWindow
{
    internal static int OcrModelMegabytes => OcrModelCatalog.DownloadMegabytesFor(OcrQuality.Accurate);
    internal static int EnhancedModelMegabytes => OcrModelCatalog.DownloadMegabytesFor(OcrQuality.Enhanced);
    internal static int UpscaleModelMegabytes => (int)Math.Ceiling(OcrModelCatalog.SuperResolutionArchiveBytes / 1_000_000d);

    internal static string OcrQualityDescription =>
        UiText.Format("SettingsUx_OcrQualityHint", OcrModelMegabytes, EnhancedModelMegabytes);

    internal Action<string> OpenSupportPage { get; set; } = uri => { if (!SupportDiagnostics.TryOpenUri(uri)) throw new InvalidOperationException(UiText.Get("Support_OpenFailed")); };
    internal Action<string> CopySupportText { get; set; } = text => ClipboardWriter.SetText(text);

    private void InitializeAbout()
    {
        PrivacyModelText.Text = UiText.Format("About_PrivacyModel", OcrModelMegabytes, EnhancedModelMegabytes);
        OcrQualityHintText.Text = OcrQualityDescription;
    }

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
