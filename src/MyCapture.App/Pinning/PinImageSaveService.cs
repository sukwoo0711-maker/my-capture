using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MyCapture.App.Editing;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Pinning;

internal enum PinSaveMode
{
    SaveAs,
    QuickSave,
}

internal enum PinSaveStatus
{
    Saved,
    Cancelled,
    Failed,
}

internal readonly record struct PinSaveResult(
    PinSaveStatus Status,
    string? Path = null,
    string? ErrorMessage = null);

internal sealed class PinSaveRequestedEventArgs(PinSaveMode mode, BitmapSource image) : EventArgs
{
    internal PinSaveMode Mode { get; } = mode;

    internal BitmapSource Image { get; } = image;
}

/// <summary>
/// Resolves pin export names and writes the frozen source image away from the UI thread.
/// </summary>
/// <remarks>
/// The native save dialog remains on the owning pin's STA thread. PNG encoding and disk I/O
/// then run in the background because a tall scrolling capture can otherwise make every WPF
/// window appear frozen. The source is cloned and frozen before dispatch, so closing the pin
/// during a save cannot invalidate the export.
/// </remarks>
internal sealed class PinImageSaveService
{
    private readonly Func<AppSettings> _settings;
    private readonly Func<AppPaths> _paths;
    private readonly ILogger<PinImageSaveService> _log;

    internal PinImageSaveService(
        Func<AppSettings> settings,
        Func<AppPaths> paths,
        ILogger<PinImageSaveService> log)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Optional dialog seam. The arguments are owner and suggested path; <see langword="null"/>
    /// means the user cancelled.
    /// </summary>
    internal Func<Window?, string, string?>? SaveAsPrompt { get; set; }

    internal async Task<PinSaveResult> SaveAsAsync(BitmapSource image, Window? owner)
    {
        ArgumentNullException.ThrowIfNull(image);

        // Serialize suggestion/dialog/write as one transaction. If two pins target the same
        // name, the second dialog observes the first completed file and shows its normal
        // overwrite confirmation instead of silently replacing a just-created export.
        return await ImageExportTransaction.RunAsync(async () =>
        {
            string suggested;
            string? chosen;
            try
            {
                suggested = BuildSuggestedPath();
                chosen = SaveAsPrompt is null
                    ? ShowSaveDialog(owner, suggested)
                    : SaveAsPrompt(owner, suggested);
            }
            catch (Exception ex) when (IsExpectedSaveFailure(ex))
            {
                _log.LogWarning(ex, "Could not prepare pinned-image Save As");
                return new PinSaveResult(PinSaveStatus.Failed, ErrorMessage: ex.Message);
            }

            if (chosen is null)
            {
                _log.LogInformation("Pinned-image Save As cancelled");
                return new PinSaveResult(PinSaveStatus.Cancelled);
            }

            if (!HasPngExtension(chosen))
            {
                const string message = "PNG 파일 이름(.png)을 선택해 주세요.";
                _log.LogWarning("Rejected pinned-image export with a non-PNG extension: {Path}", chosen);
                return new PinSaveResult(PinSaveStatus.Failed, ErrorMessage: message);
            }

            return await SaveToPathAsync(image, chosen);
        });
    }

    internal async Task<PinSaveResult> QuickSaveAsync(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return await ImageExportTransaction.RunAsync(async () =>
        {
            try
            {
                AppSettings settings = _settings();
                string directory = string.IsNullOrWhiteSpace(settings.Export.QuickSaveDirectoryOverride)
                    ? _paths().QuickSaveRoot
                    : settings.Export.QuickSaveDirectoryOverride;
                string stem = QuickSaveNaming.BuildStem(
                    settings.Export.FileNamePattern,
                    DateTimeOffset.Now);
                BitmapSource frozen = Freeze(image);
                (string path, long bytes) = await Task.Run(() =>
                {
                    byte[] encoded = ImageCodec.EncodePng(frozen);
                    string savedPath = QuickSaveNaming.WriteCollisionFreeExport(
                        directory,
                        stem,
                        ".png",
                        encoded);
                    return (savedPath, (long)encoded.Length);
                });

                _log.LogInformation("Quick-saved pinned image ({Bytes} bytes) to {Path}", bytes, path);
                return new PinSaveResult(PinSaveStatus.Saved, path);
            }
            catch (Exception ex) when (IsExpectedSaveFailure(ex))
            {
                _log.LogWarning(ex, "Could not prepare pinned-image quick save");
                return new PinSaveResult(PinSaveStatus.Failed, ErrorMessage: ex.Message);
            }
        });
    }

    private async Task<PinSaveResult> SaveToPathAsync(BitmapSource image, string path)
    {
        BitmapSource frozen = Freeze(image);

        try
        {
            long bytes = await Task.Run(() => ImageCodec.SavePngExport(frozen, path))
                .ConfigureAwait(false);
            _log.LogInformation("Saved pinned image ({Bytes} bytes) to {Path}", bytes, path);
            return new PinSaveResult(PinSaveStatus.Saved, path);
        }
        catch (Exception ex) when (IsExpectedSaveFailure(ex))
        {
            _log.LogWarning(ex, "Could not save pinned image to {Path}", path);
            return new PinSaveResult(PinSaveStatus.Failed, path, ex.Message);
        }
    }

    private string BuildSuggestedPath()
    {
        AppSettings settings = _settings();
        string directory = string.IsNullOrWhiteSpace(settings.Export.QuickSaveDirectoryOverride)
            ? _paths().QuickSaveRoot
            : settings.Export.QuickSaveDirectoryOverride;
        string stem = QuickSaveNaming.BuildStem(
            settings.Export.FileNamePattern,
            DateTimeOffset.Now);
        return QuickSaveNaming.ResolvePath(directory, stem, ".png");
    }

    private static string? ShowSaveDialog(Window? owner, string suggested)
    {
        string? directory = Path.GetDirectoryName(suggested);
        var dialog = new SaveFileDialog
        {
            Title = "고정 이미지 저장",
            Filter = "PNG 이미지 (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            OverwritePrompt = true,
            ValidateNames = true,
            InitialDirectory = directory is not null && Directory.Exists(directory) ? directory : null,
            FileName = Path.GetFileName(suggested),
        };

        dialog.FileOk += (_, e) =>
        {
            if (HasPngExtension(dialog.FileName))
            {
                return;
            }

            e.Cancel = true;
            _ = owner is null
                ? MessageBox.Show(
                    "PNG 파일 이름(.png)을 선택해 주세요.",
                    "고정 이미지 저장",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information)
                : MessageBox.Show(
                    owner,
                    "PNG 파일 이름(.png)을 선택해 주세요.",
                    "고정 이미지 저장",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
        };

        bool? accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return accepted == true ? dialog.FileName : null;
    }

    private static bool HasPngExtension(string path)
    {
        try
        {
            return string.Equals(
                Path.GetExtension(path),
                ".png",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsExpectedSaveFailure(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;

    private static BitmapSource Freeze(BitmapSource image)
    {
        if (image.IsFrozen)
        {
            return image;
        }

        BitmapSource copy = image.Clone();
        copy.Freeze();
        return copy;
    }
}
