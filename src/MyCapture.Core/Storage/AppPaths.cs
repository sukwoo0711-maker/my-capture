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

    private AppPaths(string dataRoot, string capturesRoot, string quickSaveRoot)
    {
        DataRoot = dataRoot;
        CapturesRoot = capturesRoot;
        QuickSaveRoot = quickSaveRoot;
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

    public string SettingsFile => Path.Combine(DataRoot, "settings.json");

    public string IndexFile => Path.Combine(DataRoot, "index.json");

    public string LogsRoot => Path.Combine(DataRoot, "logs");

    /// <summary>
    /// Standard per-user locations.
    /// </summary>
    public static AppPaths CreateDefault()
    {
        string appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create);

        string pictures = Environment.GetFolderPath(
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolderOption.Create);

        // Fall back to the data root when the Pictures folder cannot be resolved,
        // which happens on stripped-down or redirected profiles.
        string quickSaveBase = string.IsNullOrEmpty(pictures)
            ? Path.Combine(appData, AppFolderName)
            : pictures;

        string dataRoot = Path.Combine(appData, AppFolderName);

        return new AppPaths(
            dataRoot: dataRoot,
            capturesRoot: Path.Combine(dataRoot, "captures"),
            quickSaveRoot: Path.Combine(quickSaveBase, "Captures"));
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
            quickSaveRoot: Path.Combine(root, "quicksave"));
    }

    /// <summary>
    /// Returns a copy with the bulk capture directory relocated, leaving settings
    /// and the index where they are.
    /// </summary>
    public AppPaths WithCapturesRoot(string capturesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capturesRoot);
        return new AppPaths(DataRoot, capturesRoot, QuickSaveRoot);
    }

    public AppPaths WithQuickSaveRoot(string quickSaveRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quickSaveRoot);
        return new AppPaths(DataRoot, CapturesRoot, quickSaveRoot);
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
}
