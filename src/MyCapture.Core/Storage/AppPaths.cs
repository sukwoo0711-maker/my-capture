using MyCapture.Core.Platform;

namespace MyCapture.Core.Storage;

/// <summary>
/// Every filesystem location the application uses, resolved in one place.
/// </summary>
/// <remarks>
/// <para>
/// Constructed rather than static so tests can point the whole storage layer at a
/// temporary directory. Scattering <c>Environment.GetFolderPath</c> calls through
/// the codebase is what makes storage code untestable, so it happens exactly once,
/// here.
/// </para>
/// <para>
/// The data root defaults to <c>%APPDATA%</c> (roaming) rather than
/// <c>%LOCALAPPDATA%</c>. That is a deliberate trade-off: captures can total
/// gigabytes and roaming profiles are a poor place for bulk data, so the *index and
/// settings* live under the data root while the bulk capture directory can be
/// relocated independently through settings, see <see cref="WithCapturesRoot"/>.
/// The default capture directory is placed under the data root for a
/// zero-configuration first run.
/// </para>
/// </remarks>
public sealed class AppPaths
{
    public const string AppFolderName = "MyCapture";

    private AppPaths(string dataRoot, string capturesRoot, string quickSaveRoot, string ocrModelsRoot,
        string translationModelsRoot)
    {
        DataRoot = CanonicalizeRoot(dataRoot, nameof(dataRoot));
        CapturesRoot = CanonicalizeRoot(capturesRoot, nameof(capturesRoot));
        QuickSaveRoot = CanonicalizeRoot(quickSaveRoot, nameof(quickSaveRoot));
        OcrModelsRoot = CanonicalizeRoot(ocrModelsRoot, nameof(ocrModelsRoot));
        TranslationModelsRoot = CanonicalizeRoot(translationModelsRoot, nameof(translationModelsRoot));
    }

    /// <summary>
    /// Holds settings, the capture index, and logs.
    /// </summary>
    public string DataRoot { get; }

    /// <summary>
    /// Holds capture images, thumbnails and annotation layer files.
    /// </summary>
    public string CapturesRoot { get; }

    /// <summary>
    /// Default destination for quick-save exports.
    /// </summary>
    public string QuickSaveRoot { get; }

    /// <summary>
    /// Downloaded PP-OCR recognition models. Kept under local app data so roaming
    /// profiles do not copy tens of megabytes of ONNX weights.
    /// </summary>
    public string OcrModelsRoot { get; }

    /// <summary>
    /// Downloaded offline translation models, fetched only when translation is turned on.
    /// Kept under local app data for the same reason as <see cref="OcrModelsRoot"/>.
    /// </summary>
    public string TranslationModelsRoot { get; }

    public string SettingsFile => Path.Combine(DataRoot, "settings.json");

    public string IndexFile => Path.Combine(DataRoot, "index.json");

    public string LogsRoot => Path.Combine(DataRoot, "logs");

    public string PinSessionRoot => Path.Combine(DataRoot, "pins");

    /// <summary>
    /// Standard per-user locations, or the package's own folders for the Microsoft Store build.
    /// </summary>
    public static AppPaths CreateDefault()
    {
        string pictures = Environment.GetFolderPath(
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolderOption.Create);

        if (AppPackage.TryGetDataFolders(out string localState, out string localCache, out _))
        {
            return CreateForPackage(localState, localCache, pictures);
        }

        string appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create);

        // Fall back to the data root when the Pictures folder cannot be resolved,
        // which happens on stripped-down or redirected profiles.
        string quickSaveBase = string.IsNullOrEmpty(pictures)
            ? Path.Combine(appData, AppFolderName)
            : pictures;

        string dataRoot = Path.Combine(appData, AppFolderName);
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        // Downloaded models fall back to the data root when local app data cannot be resolved.
        string modelsBase = string.IsNullOrEmpty(localAppData)
            ? dataRoot
            : Path.Combine(localAppData, AppFolderName);

        return new AppPaths(
            dataRoot: dataRoot,
            capturesRoot: Path.Combine(dataRoot, "captures"),
            quickSaveRoot: Path.Combine(quickSaveBase, "Captures"),
            ocrModelsRoot: Path.Combine(modelsBase, "ocr-models"),
            translationModelsRoot: Path.Combine(modelsBase, "translation-models"));
    }

    /// <summary>
    /// Microsoft Store build. New files under %APPDATA% would be virtualized into a package
    /// store that Explorer, viewers and drop targets cannot open, so the library lives in the
    /// package's real LocalState and downloaded models in its LocalCache. Quick-save exports
    /// stay in Pictures, which is not virtualized.
    /// </summary>
    internal static AppPaths CreateForPackage(string localState, string localCache, string? pictures)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localState);
        ArgumentException.ThrowIfNullOrWhiteSpace(localCache);

        return new AppPaths(
            dataRoot: localState,
            capturesRoot: Path.Combine(localState, "captures"),
            quickSaveRoot: Path.Combine(string.IsNullOrEmpty(pictures) ? localState : pictures, "Captures"),
            ocrModelsRoot: Path.Combine(localCache, "ocr-models"),
            translationModelsRoot: Path.Combine(localCache, "translation-models"));
    }

    /// <summary>
    /// Roots everything under <paramref name="root"/>. For tests.
    /// </summary>
    public static AppPaths CreateForRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        return new AppPaths(
            dataRoot: root,
            capturesRoot: Path.Combine(root, "captures"),
            quickSaveRoot: Path.Combine(root, "quicksave"),
            ocrModelsRoot: Path.Combine(root, "ocr-models"),
            translationModelsRoot: Path.Combine(root, "translation-models"));
    }

    /// <summary>
    /// Returns a copy with the bulk capture directory relocated, leaving settings
    /// and the index where they are.
    /// </summary>
    public AppPaths WithCapturesRoot(string capturesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capturesRoot);
        return new AppPaths(DataRoot, capturesRoot, QuickSaveRoot, OcrModelsRoot, TranslationModelsRoot);
    }

    public AppPaths WithQuickSaveRoot(string quickSaveRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quickSaveRoot);
        return new AppPaths(DataRoot, CapturesRoot, quickSaveRoot, OcrModelsRoot, TranslationModelsRoot);
    }

    /// <summary>
    /// Directory holding all files for one capture.
    /// </summary>
    /// <remarks>
    /// Captures are bucketed by year and month. A single flat directory with a few
    /// thousand entries makes Explorer crawl and makes manual inspection during
    /// support impractical.
    /// </remarks>
    public string GetCaptureDirectory(Guid id, DateTimeOffset createdAt) =>
        Path.Combine(
            CapturesRoot,
            createdAt.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
            id.ToString("N", System.Globalization.CultureInfo.InvariantCulture));

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(CapturesRoot);
        Directory.CreateDirectory(LogsRoot);
    }

    private static string CanonicalizeRoot(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        return Path.GetFullPath(path);
    }
}
