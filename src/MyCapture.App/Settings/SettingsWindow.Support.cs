using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Platform;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Settings;

/// <summary>The About page's privacy and feedback sections, including the diagnostics bundle.</summary>
internal sealed partial class SettingsWindow
{
    internal static int OcrModelMegabytes => OcrModelCatalog.DownloadMegabytesFor(OcrQuality.Accurate);

    internal static string OcrQualityDescription =>
        UiText.Format("SettingsUx_OcrQualityHint", OcrModelMegabytes);

    internal Action<string> OpenSupportPage { get; set; } = uri => { if (!SupportDiagnostics.TryOpenUri(uri)) throw new InvalidOperationException(UiText.Get("Support_OpenFailed")); };
    internal Action<string> CopySupportText { get; set; } = text => ClipboardWriter.SetText(text);

    private void InitializeAbout()
    {
        PrivacyModelText.Text = UiText.Format("About_PrivacyModel", OcrModelMegabytes);
        PrivacyTranslationText.Text = UiText.Format("About_PrivacyTranslation", TranslationModelCatalog.TotalMegabytes);
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

    /// <summary>The app's rotating file log; the bundle holds the summary alone without it.</summary>
    internal RollingFileLog? FileLog { get; set; }

    /// <summary>Where to save the diagnostics bundle, or null when cancelled. Tests skip the dialog.</summary>
    internal Func<string?>? ChooseDiagnosticsBundlePath { get; set; }

    /// <summary>The bundle's support.txt: the contact window's summary, then versions, DPI and recent error codes.</summary>
    // Qualified: SupportReport alone names the Contact support button in this window.
    internal string DiagnosticsBundleSummary() =>
        MyCapture.App.Diagnostics.SupportReport.Format(SupportDiagnosticSnapshot.Capture(this)) + Environment.NewLine + Environment.NewLine
            + SupportDiagnosticsText();

    internal void SaveDiagnosticsBundle(string path) => DiagnosticBundle.Write(path, DiagnosticsBundleSummary(), FileLog);

    private void OnSaveDiagnosticsBundle(object sender, RoutedEventArgs e)
    {
        string? path = (ChooseDiagnosticsBundlePath ?? ShowDiagnosticsBundleDialog)();
        if (path is null) return;
        try
        {
            SaveDiagnosticsBundle(path);
            SetStatusMessage(UiText.Get("About_DiagnosticsBundleSaved"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not save the diagnostics bundle");
            ThemedDialog.Error(this, UiText.Get("About_DiagnosticsBundle"), ex,
                () => OnSaveDiagnosticsBundle(sender, e), UiText.Get("Dialog.ChooseLocation"));
        }
    }

    private string? ShowDiagnosticsBundleDialog()
    {
        var dialog = new SaveFileDialog
        {
            Title = UiText.Get("About_DiagnosticsBundle"),
            Filter = UiText.Get("About_DiagnosticsBundleFilter"),
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"MyCapture-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

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
