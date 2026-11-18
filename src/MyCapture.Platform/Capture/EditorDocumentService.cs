using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Automation;
using MyCapture.Core.Capture;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Capture;

/// <summary>
/// The document of a code editor (<see cref="EditorDocumentMatch.Executables"/>) read again after the
/// capture. A Chromium-based editor builds its accessibility tree only once something asks for it: the
/// reads around the pixels wake it, and this read returns a moment later for the full path its selected
/// tab carries. It runs a few attempts within <see cref="Budget"/>, nothing on screen or in the save waits
/// for it, and it keeps a path only while the same window still belongs to the same process (ID and start
/// time) with the same title.
/// </summary>
internal sealed class EditorDocumentService
{
    /// <summary>Whole late read of one window, attempts and pauses included.</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(1);

    /// <summary>Pause before each attempt; the first waits too, so the tree the earlier reads woke can fill.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(150);

    internal const int MaxAttempts = 3;

    /// <summary>Nodes one attempt may visit; a VS Code window with a folder open has about 200.</summary>
    internal const int MaxNodes = 256;

    /// <summary>Deadline of one attempt's native walk.</summary>
    internal static readonly TimeSpan AttemptBudget = TimeSpan.FromMilliseconds(250);

    private static readonly SemaphoreSlim SharedGate = new(1, 1);

    private readonly Func<WindowCandidate, CancellationToken, IReadOnlyList<string>> _readSelectedTabs;
    private readonly Func<WindowCandidate, bool> _stillMatches;
    private readonly Func<uint, DateTime?> _processStarted;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly BoundedNativeProbe _probe;

    internal EditorDocumentService(bool waitForBusyProbe = false)
        : this(ReadSelectedTabs, StillMatches, RecentDocumentService.ReadProcessStart, File.Exists,
            static wait => Task.Delay(wait),
            new BoundedNativeProbe(SharedGate, AttemptBudget, ApartmentState.STA, "MyCapture editor document",
                waitForBusyProbe)) { }

    internal EditorDocumentService(Func<WindowCandidate, CancellationToken, IReadOnlyList<string>> readSelectedTabs,
        Func<WindowCandidate, bool> stillMatches, Func<uint, DateTime?> processStarted, Func<string, bool> fileExists,
        Func<TimeSpan, Task> delay, BoundedNativeProbe probe)
    {
        _readSelectedTabs = readSelectedTabs;
        _stillMatches = stillMatches;
        _processStarted = processStarted;
        _fileExists = fileExists;
        _delay = delay;
        _probe = probe;
    }

    /// <summary>
    /// Paths for the editor windows among <paramref name="candidates"/>: windows whose confirmed source is
    /// an editor (<see cref="WindowCandidate.Source"/>) and has no document yet. Never throws.
    /// </summary>
    internal async Task<IReadOnlyDictionary<IntPtr, string>> ReadAsync(IReadOnlyList<WindowCandidate> candidates)
    {
        var result = new Dictionary<IntPtr, string>();
        foreach (WindowCandidate candidate in candidates)
        {
            if (!IsTarget(candidate)) continue;
            try
            {
                string path = await ReadOneAsync(candidate).ConfigureAwait(false);
                if (path.Length > 0) result[candidate.Handle] = path;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Optional metadata; the capture keeps what it had.
            }
        }
        return result;
    }

    internal static bool IsTarget(WindowCandidate candidate) =>
        candidate.Handle != IntPtr.Zero && candidate.ProcessId != 0 && candidate.Source is { } source
        && source.DocumentPath.Length == 0 && EditorDocumentMatch.IsEditor(source.AppPath)
        && !SourceDocument.IsUnsaved(source.AppPath, candidate.Title);

    private async Task<string> ReadOneAsync(WindowCandidate candidate)
    {
        DateTime? started = _processStarted(candidate.ProcessId);
        if (started is null) return string.Empty;
        IReadOnlyList<string> titleNames = SourceDocument.TitleFileNames(candidate.Source!.AppPath, candidate.Title);
        var clock = Stopwatch.StartNew();
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (clock.Elapsed + Interval + AttemptBudget > Budget) break;
            await _delay(Interval).ConfigureAwait(false);
            if (!_stillMatches(candidate)) return string.Empty;
            IReadOnlyList<string> texts = await _probe.RunAsync(
                cancellation => _readSelectedTabs(candidate, cancellation), (IReadOnlyList<string>)[]).ConfigureAwait(false);
            // An empty or still-filling tree names nothing yet; the next attempt reads it again.
            string path = texts.Count == 0 ? string.Empty : EditorDocumentMatch.Choose(titleNames, texts, _fileExists);
            if (path.Length == 0) continue;
            return _stillMatches(candidate) && _processStarted(candidate.ProcessId) == started ? path : string.Empty;
        }
        return string.Empty;
    }

    /// <summary>
    /// Name and labels of each selected tab, the only parts read: never editor text, values or other
    /// controls. Explorer trees, lists and edit controls are skipped, so the walk stays within
    /// <see cref="MaxNodes"/> nodes.
    /// </summary>
    private static IReadOnlyList<string> ReadSelectedTabs(WindowCandidate candidate, CancellationToken cancellation)
    {
        if (!StillMatches(candidate)) return [];
        var texts = new List<string>();
        AutomationElement root = AutomationElement.FromHandle(candidate.Handle);
        TreeWalker walker = TreeWalker.ControlViewWalker;
        var pending = new Queue<AutomationElement>();
        pending.Enqueue(root);
        int visited = 0;
        while (pending.Count > 0 && visited++ < MaxNodes)
        {
            cancellation.ThrowIfCancellationRequested();
            AutomationElement element = pending.Dequeue();
            ControlType type = element.Current.ControlType;
            if (type == ControlType.TabItem)
            {
                if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selection)
                    && ((SelectionItemPattern)selection).Current.IsSelected)
                    AddLabels(element, walker, texts, cancellation);
                continue;
            }
            if (type == ControlType.Tree || type == ControlType.List || type == ControlType.Edit
                || type == ControlType.DataGrid || type == ControlType.Table) continue;
            for (AutomationElement? child = walker.GetFirstChild(element); child is not null;
                child = walker.GetNextSibling(child))
            {
                cancellation.ThrowIfCancellationRequested();
                pending.Enqueue(child);
            }
        }
        return texts;
    }

    /// <summary>The tab's own name and help text, and the names of its label groups (two levels).</summary>
    private static void AddLabels(AutomationElement tab, TreeWalker walker, List<string> texts, CancellationToken cancellation)
    {
        texts.Add(tab.Current.Name);
        texts.Add(tab.Current.HelpText);
        int labels = 0;
        for (AutomationElement? child = walker.GetFirstChild(tab); child is not null && labels < 8;
            child = walker.GetNextSibling(child))
        {
            cancellation.ThrowIfCancellationRequested();
            labels++;
            if (child.Current.ControlType == ControlType.Button) continue;
            texts.Add(child.Current.Name);
            for (AutomationElement? inner = walker.GetFirstChild(child); inner is not null && labels < 8;
                inner = walker.GetNextSibling(inner))
            {
                labels++;
                if (inner.Current.ControlType != ControlType.Button) texts.Add(inner.Current.Name);
            }
        }
    }

    private static bool StillMatches(WindowCandidate candidate)
    {
        _ = NativeMethods.GetWindowThreadProcessId(candidate.Handle, out uint processId);
        var title = new StringBuilder(4096);
        _ = NativeMethods.GetWindowText(candidate.Handle, title, title.Capacity);
        return processId == candidate.ProcessId && string.Equals(candidate.Title, title.ToString(), StringComparison.Ordinal);
    }
}
