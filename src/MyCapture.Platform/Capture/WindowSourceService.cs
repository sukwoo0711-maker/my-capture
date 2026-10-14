using System.Text;
using MyCapture.Core.Capture;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Capture;

/// <summary>
/// Source metadata of one window observed at one moment: the owning process, its application and,
/// its document or current Explorer folder. Two observations around the pixel grab are compared with
/// <see cref="WindowSourceService.StableDetails"/>; only what stayed the same is kept.
/// </summary>
public sealed record WindowSourceSnapshot(uint ProcessId, string AppName, string AppPath, string DocumentPath);

/// <summary>
/// Reads where a window's content comes from without blocking capture. The application is resolved
/// from the window's process (cheap kernel and file-version queries); the Explorer folder needs the
/// shell's cross-process objects and only runs for Explorer windows. Document apps use a separate,
/// read-only accessibility probe. Each part has its own deadline
/// and in-flight limit, and either part failing leaves the other intact.
/// </summary>
public sealed class WindowSourceService
{
    /// <summary>Deadline for resolving applications of one batch of windows.</summary>
    internal static readonly TimeSpan AppBudget = TimeSpan.FromMilliseconds(400);

    private static readonly SemaphoreSlim SharedAppGate = new(1, 1);
    private static readonly IReadOnlyDictionary<IntPtr, WindowSourceSnapshot> Empty =
        new Dictionary<IntPtr, WindowSourceSnapshot>();
    private static readonly IReadOnlyDictionary<IntPtr, AppObservation> NoApps =
        new Dictionary<IntPtr, AppObservation>();

    private readonly Func<IReadOnlyList<WindowCandidate>, Task<IReadOnlyDictionary<IntPtr, AppObservation>>> _readApps;
    private readonly Func<IReadOnlyList<IntPtr>, Task<IReadOnlyDictionary<IntPtr, string>>> _readFolders;
    private readonly DocumentPathService? _documents;

    public WindowSourceService() : this(new SourceAppResolver(), new ExplorerFolderService()) { }

    /// <param name="waitForBusyProbe">
    /// Wait, within each read's own deadline, for a probe that still owns its provider (the previous
    /// capture's) instead of returning nothing at once. For readers nothing on screen waits for.
    /// </param>
    public WindowSourceService(bool waitForBusyProbe)
        : this(new SourceAppResolver(), new ExplorerFolderService(waitForBusyProbe), waitForBusyProbe) { }

    internal WindowSourceService(SourceAppResolver apps, ExplorerFolderService folders, bool waitForBusyProbe = false)
        : this(CreateAppReader(apps ?? throw new ArgumentNullException(nameof(apps)), waitForBusyProbe),
            (folders ?? throw new ArgumentNullException(nameof(folders))).ReadAsync,
            new DocumentPathService(waitForBusyProbe)) { }

    internal WindowSourceService(
        Func<IReadOnlyList<WindowCandidate>, Task<IReadOnlyDictionary<IntPtr, AppObservation>>> readApps,
        Func<IReadOnlyList<IntPtr>, Task<IReadOnlyDictionary<IntPtr, string>>> readFolders,
        DocumentPathService? documents = null)
    {
        _readApps = readApps ?? throw new ArgumentNullException(nameof(readApps));
        _readFolders = readFolders ?? throw new ArgumentNullException(nameof(readFolders));
        _documents = documents;
    }

    /// <summary>Application observed for one window; ProcessId is the window's own process.</summary>
    internal readonly record struct AppObservation(uint ProcessId, SourceAppIdentity? App);

    /// <summary>Returns a snapshot for each window whose process could be read; never throws.</summary>
    public async Task<IReadOnlyDictionary<IntPtr, WindowSourceSnapshot>> ReadAsync(IReadOnlyList<WindowCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        WindowCandidate[] targets = candidates.Where(candidate => candidate.Handle != IntPtr.Zero).ToArray();
        if (targets.Length == 0) return Empty;

        IntPtr[] explorers = targets
            .Where(candidate => ExplorerFolderService.IsExplorerWindowClass(ClassNameOf(candidate)))
            .Select(candidate => candidate.Handle)
            .ToArray();
        Task<IReadOnlyDictionary<IntPtr, AppObservation>> apps = SafeAsync(() => _readApps(targets), NoApps);
        Task<IReadOnlyDictionary<IntPtr, string>> folders = explorers.Length == 0
            ? Task.FromResult<IReadOnlyDictionary<IntPtr, string>>(new Dictionary<IntPtr, string>())
            : SafeAsync(() => _readFolders(explorers), new Dictionary<IntPtr, string>());
        IReadOnlyDictionary<IntPtr, AppObservation> appResults = await apps.ConfigureAwait(false);
        Task<IReadOnlyDictionary<IntPtr, string>> documents = _documents is null
            ? Task.FromResult<IReadOnlyDictionary<IntPtr, string>>(new Dictionary<IntPtr, string>())
            : _documents.ReadAsync(targets, appResults);
        await Task.WhenAll(documents, folders).ConfigureAwait(false);
        IReadOnlyDictionary<IntPtr, string> folderPaths = await folders.ConfigureAwait(false);
        // Only the windows recognised as Explorer were asked; nothing else can carry a folder.
        var explorerFolders = explorers
            .Where(folderPaths.ContainsKey)
            .ToDictionary(handle => handle, handle => folderPaths[handle]);
        foreach (var document in await documents.ConfigureAwait(false))
            explorerFolders.TryAdd(document.Key, document.Value);
        return Combine(targets, appResults, explorerFolders);
    }

    internal static IReadOnlyDictionary<IntPtr, WindowSourceSnapshot> Combine(IReadOnlyList<WindowCandidate> candidates,
        IReadOnlyDictionary<IntPtr, AppObservation> apps, IReadOnlyDictionary<IntPtr, string> folders)
    {
        var result = new Dictionary<IntPtr, WindowSourceSnapshot>();
        foreach (WindowCandidate candidate in candidates)
        {
            // The process is the identity both observations are compared on; without it a folder
            // could belong to a window handle that was reused by another process.
            if (!apps.TryGetValue(candidate.Handle, out AppObservation app) || app.ProcessId == 0
                || (candidate.ProcessId != 0 && candidate.ProcessId != app.ProcessId)) continue;
            string folder = folders.TryGetValue(candidate.Handle, out string? path)
                ? CaptureSourceText.NormalizeLocalPath(path) : string.Empty;
            result[candidate.Handle] = new WindowSourceSnapshot(app.ProcessId,
                CaptureSourceText.NormalizeAppName(app.App?.Name), CaptureSourceText.NormalizeLocalPath(app.App?.Path),
                folder);
        }

        return result;
    }

    /// <summary>
    /// Keeps only what both observations around the frame agree on, for the same process. The
    /// application and the folder are judged separately: navigating Explorer to another folder during
    /// the grab drops the folder but still identifies Explorer.
    /// </summary>
    public static CaptureSourceDetails StableDetails(WindowSourceSnapshot? before, WindowSourceSnapshot? after,
        uint expectedProcessId = 0)
    {
        if (before is null || after is null || before.ProcessId == 0 || before.ProcessId != after.ProcessId
            || (expectedProcessId != 0 && expectedProcessId != before.ProcessId)) return CaptureSourceDetails.Empty;
        bool sameApp = string.Equals(before.AppName, after.AppName, StringComparison.Ordinal)
            && string.Equals(before.AppPath, after.AppPath, StringComparison.OrdinalIgnoreCase);
        bool sameDocument = string.Equals(before.DocumentPath, after.DocumentPath, StringComparison.Ordinal);
        return CaptureSourceDetails.Create(
            sameApp ? before.AppName : null,
            sameApp ? before.AppPath : null,
            sameDocument ? before.DocumentPath : null);
    }

    private static Func<IReadOnlyList<WindowCandidate>, Task<IReadOnlyDictionary<IntPtr, AppObservation>>> CreateAppReader(
        SourceAppResolver apps, bool waitForBusyProbe)
    {
        var probe = new BoundedNativeProbe(SharedAppGate, AppBudget, ApartmentState.MTA, "MyCapture source application",
            waitForBusyProbe);
        return candidates => probe.RunAsync(cancellation =>
        {
            var result = new Dictionary<IntPtr, AppObservation>();
            foreach (WindowCandidate candidate in candidates)
            {
                cancellation.ThrowIfCancellationRequested();
                _ = NativeMethods.GetWindowThreadProcessId(candidate.Handle, out uint processId);
                if (processId == 0 || (candidate.ProcessId != 0 && processId != candidate.ProcessId)) continue;
                uint content = SourceAppResolver.ResolveContentProcess(ClassNameOf(candidate), processId,
                    () => SourceAppResolver.ReadChildProcessIds(candidate.Handle));
                result[candidate.Handle] = new AppObservation(processId, content == 0 ? null : apps.Resolve(content));
            }

            return (IReadOnlyDictionary<IntPtr, AppObservation>)result;
        }, NoApps);
    }

    /// <summary>The candidate's frozen class name, or the live one for a window resolved at a point.</summary>
    private static string ClassNameOf(WindowCandidate candidate)
    {
        if (candidate.ClassName.Length > 0 || candidate.Handle == IntPtr.Zero) return candidate.ClassName;
        var buffer = new StringBuilder(NativeMethods.MaxClassNameLength + 1);
        return NativeMethods.GetClassName(candidate.Handle, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static async Task<T> SafeAsync<T>(Func<Task<T>> read, T fallback)
    {
        try { return await read().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return fallback; }
    }
}
