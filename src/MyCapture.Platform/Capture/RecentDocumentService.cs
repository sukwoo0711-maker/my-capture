using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using MyCapture.Core.Capture;

namespace MyCapture.Platform.Capture;

/// <summary>
/// Finds the file of a document window whose title names only the file, from the user's Recent Items
/// (%APPDATA%\Microsoft\Windows\Recent). Only shortcuts named after a title's file name are opened, and
/// only their stored target is read: a shortcut is never resolved, so nothing searches the disk or the
/// network. <see cref="RecentDocumentMatch"/> decides; this class supplies its inputs on one bounded
/// background probe that never blocks capture.
/// </summary>
internal sealed class RecentDocumentService
{
    /// <summary>Deadline for one batch. A shortcut read is a local file read; this bounds a stalled disk.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    /// <summary>Shortcuts read per file name. Recent Items keeps one per name; more is not a normal folder.</summary>
    private const int MaxLinksPerName = 8;

    private static readonly SemaphoreSlim SharedGate = new(1, 1);
    private readonly BoundedNativeProbe _probe;
    private readonly Func<string> _recentFolder;
    private readonly Func<string, string, IEnumerable<string>> _findLinks;
    private readonly Func<string, RecentDocumentLink?> _readLink;
    private readonly Func<uint, DateTime?> _processStarted;
    private readonly Func<string, bool> _fileExists;

    internal RecentDocumentService(bool waitForBusyProbe = false)
        : this(() => Environment.GetFolderPath(Environment.SpecialFolder.Recent), FindLinks, ReadLink,
            ReadProcessStart, File.Exists,
            new BoundedNativeProbe(SharedGate, Budget, ApartmentState.STA, "MyCapture recent document", waitForBusyProbe)) { }

    internal RecentDocumentService(Func<string> recentFolder, Func<string, string, IEnumerable<string>> findLinks,
        Func<string, RecentDocumentLink?> readLink, Func<uint, DateTime?> processStarted, Func<string, bool> fileExists,
        BoundedNativeProbe probe)
    {
        _recentFolder = recentFolder;
        _findLinks = findLinks;
        _readLink = readLink;
        _processStarted = processStarted;
        _fileExists = fileExists;
        _probe = probe;
    }

    /// <summary>The file of each document window that has exactly one qualifying shortcut; never throws.</summary>
    internal Task<IReadOnlyDictionary<IntPtr, string>> ReadAsync(IReadOnlyList<WindowCandidate> candidates,
        IReadOnlyDictionary<IntPtr, WindowSourceService.AppObservation> apps)
    {
        var targets = new List<(WindowCandidate Candidate, uint ProcessId, IReadOnlyList<string> Names)>();
        foreach (WindowCandidate candidate in candidates)
        {
            if (candidate.Handle == IntPtr.Zero || !apps.TryGetValue(candidate.Handle, out var app) || app.ProcessId == 0
                || (candidate.ProcessId != 0 && candidate.ProcessId != app.ProcessId)) continue;
            IReadOnlyList<string> names = SourceDocument.TitleFileNames(app.App?.Path, candidate.Title);
            if (names.Count > 0) targets.Add((candidate, app.ProcessId, names));
        }

        if (targets.Count == 0)
            return Task.FromResult<IReadOnlyDictionary<IntPtr, string>>(new Dictionary<IntPtr, string>());
        return _probe.RunAsync(cancellation =>
        {
            var result = new Dictionary<IntPtr, string>();
            string folder = CaptureSourceText.NormalizeLocalPath(_recentFolder());
            if (folder.Length == 0) return (IReadOnlyDictionary<IntPtr, string>)result;
            foreach ((WindowCandidate candidate, uint processId, IReadOnlyList<string> names) in targets)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    var links = new List<RecentDocumentLink>();
                    foreach (string name in names)
                    foreach (string link in _findLinks(folder, name).Take(MaxLinksPerName))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (_readLink(link) is { } read) links.Add(read);
                    }

                    string path = RecentDocumentMatch.Choose(names, links, _processStarted(processId), _fileExists);
                    if (path.Length > 0) result[candidate.Handle] = path;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    // An unreadable shortcut or a closed process contributes nothing.
                }
            }

            return (IReadOnlyDictionary<IntPtr, string>)result;
        }, new Dictionary<IntPtr, string>());
    }

    /// <summary>
    /// Shortcuts Windows names after <paramref name="name"/>: <c>name.lnk</c>, or a numbered variant such as
    /// <c>name (2).lnk</c>. Names come from a validated file name, so they hold no wildcard characters.
    /// </summary>
    private static IEnumerable<string> FindLinks(string folder, string name)
    {
        string exact = Path.Combine(folder, name + ".lnk");
        if (File.Exists(exact)) yield return exact;
        IEnumerable<string> numbered;
        try { numbered = Directory.EnumerateFiles(folder, name + " (*).lnk", SearchOption.TopDirectoryOnly); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (string link in numbered) yield return link;
    }

    private static RecentDocumentLink? ReadLink(string linkPath)
    {
        DateTime written = File.GetLastWriteTimeUtc(linkPath);
        object? instance = null;
        try
        {
            Type type = Type.GetTypeFromCLSID(ClsidShellLink, throwOnError: true)!;
            instance = Activator.CreateInstance(type)!;
            ((IPersistFile)instance).Load(linkPath, 0 /* STGM_READ */);
            var target = new StringBuilder(CaptureSourceText.MaxPathLength);
            // Reads the stored path only. IShellLink.Resolve, which can search for a moved target, is never called.
            if (((IShellLinkW)instance).GetPath(target, target.Capacity, IntPtr.Zero, SlgpRawPath) != 0) return null;
            string path = Environment.ExpandEnvironmentVariables(target.ToString());
            return path.Length == 0 ? null : new RecentDocumentLink(path, written);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
        }
    }

    /// <summary>When <paramref name="processId"/> started, in UTC; null when the process cannot be read.</summary>
    internal static DateTime? ReadProcessStart(uint processId)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            return GetProcessTimes(process, out long created, out _, out _, out _) && created > 0
                ? DateTime.FromFileTimeUtc(created) : null;
        }
        finally { _ = CloseHandle(process); }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint SlgpRawPath = 0x4;
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    // Only the prefix through GetPath is declared; later methods keep their vtable slots unused.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig]
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int capacity, IntPtr findData, uint flags);
    }
}
