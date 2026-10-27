using MyCapture.Core.Platform;
using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using MyCapture.App.Themes;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Settings;

/// <summary>The downloaded translation model: its size on this PC and its removal.</summary>
internal sealed partial class SettingsWindow
{
    private Func<long>? _translationModelBytes;
    private bool _deletingTranslationModel;

    /// <summary>Bytes of the downloaded translation model, 0 when there is none. Set by the app.</summary>
    internal Func<long>? TranslationModelBytes
    {
        get => _translationModelBytes;
        set
        {
            _translationModelBytes = value;
            RefreshTranslationModel();
        }
    }

    /// <summary>Releases the translation engine and deletes the model files. Set by the app.</summary>
    internal Func<CancellationToken, Task>? DeleteTranslationModel { get; set; }

    /// <summary>Asks before deleting the model of the given size; tests answer without a dialog.</summary>
    internal Func<long, bool>? ConfirmDeleteTranslationModel { get; set; }

    private void InitializeTranslationModel()
    {
        // The model can be downloaded from the OCR window while settings stays open.
        Activated += (_, _) => RefreshTranslationModel();
        RefreshTranslationModel();
    }

    internal void RefreshTranslationModel()
    {
        if (AppArchitecture.IsX86)
        {
            TranslationModelStatus.Text = UiText.Get("Settings.TranslationUnavailable.X86");
            TranslationModelDelete.IsEnabled = false;
            return;
        }
        long bytes = InstalledTranslationBytes();
        TranslationModelStatus.Text = bytes > 0
            ? UiText.Format("Settings_TranslationInstalled", Megabytes(bytes))
            : UiText.Get("Settings_TranslationNotInstalled");
        TranslationModelDelete.IsEnabled = bytes > 0 && !_deletingTranslationModel && DeleteTranslationModel is not null;
    }

    private async void OnDeleteTranslationModel(object sender, RoutedEventArgs e)
    {
        long bytes = InstalledTranslationBytes();
        if (bytes <= 0 || DeleteTranslationModel is null || _deletingTranslationModel) return;
        bool confirmed = ConfirmDeleteTranslationModel?.Invoke(bytes) ?? ThemedDialog.Show(this,
            UiText.Format("Settings_TranslationDeleteConfirm", Megabytes(bytes)), UiText.Get("Settings_TranslationDelete"),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel, destructive: true) == MessageBoxResult.OK;
        if (!confirmed) return;

        _deletingTranslationModel = true;
        RefreshTranslationModel();
        try
        {
            await DeleteTranslationModel(CancellationToken.None);
            SetStatusMessage(UiText.Get("Settings_TranslationDeleted"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TranslationModelException)
        {
            _log.LogWarning(ex, "Could not delete the translation model");
            SetStatusMessage(UiText.Get("Settings_TranslationDeleteFailed"));
        }
        finally
        {
            _deletingTranslationModel = false;
            RefreshTranslationModel();
        }
    }

    private long InstalledTranslationBytes()
    {
        try
        {
            return _translationModelBytes?.Invoke() ?? 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string Megabytes(long bytes) =>
        Math.Ceiling(bytes / 1_000_000d).ToString("N0", UiText.Culture);
}
