using System.Text;
using System.Windows.Automation;
using MyCapture.Core.Capture;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Capture;

/// <summary>Optional, read-only document metadata, with one bounded provider shared by all captures.</summary>
internal sealed class DocumentPathService
{
    internal static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(300);
    private static readonly SemaphoreSlim SharedGate = new(1, 1);
    private readonly BoundedNativeProbe _probe;
    private readonly Func<WindowCandidate, string, CancellationToken, string> _read;

    internal DocumentPathService(bool waitForBusyProbe = false)
        : this(ReadNative, new BoundedNativeProbe(SharedGate, Budget, ApartmentState.STA,
            "MyCapture source document", waitForBusyProbe)) { }

    internal DocumentPathService(Func<WindowCandidate, string, CancellationToken, string> read, BoundedNativeProbe probe)
    { _read = read; _probe = probe; }

    internal Task<IReadOnlyDictionary<IntPtr, string>> ReadAsync(IReadOnlyList<WindowCandidate> candidates,
        IReadOnlyDictionary<IntPtr, WindowSourceService.AppObservation> apps)
    {
        WindowCandidate[] targets = candidates.Where(candidate => candidate.Handle != IntPtr.Zero
            && apps.TryGetValue(candidate.Handle, out var app) && app.ProcessId != 0
            && (candidate.ProcessId == 0 || app.ProcessId == candidate.ProcessId)
            && SourceDocument.IsSupportedApp(app.App?.Path)).ToArray();
        if (targets.Length == 0)
            return Task.FromResult<IReadOnlyDictionary<IntPtr, string>>(new Dictionary<IntPtr, string>());
        return _probe.RunAsync(cancellation =>
        {
            var result = new Dictionary<IntPtr, string>();
            foreach (WindowCandidate candidate in targets)
            {
                cancellation.ThrowIfCancellationRequested();
                var app = apps[candidate.Handle];
                try
                {
                    string path = CaptureSourceText.NormalizeLocalPath(
                        _read(candidate with { ProcessId = app.ProcessId }, app.App!.Path, cancellation));
                    if (path.Length > 0) result[candidate.Handle] = path;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException) { }
            }
            return (IReadOnlyDictionary<IntPtr, string>)result;
        }, new Dictionary<IntPtr, string>());
    }

    private static string ReadNative(WindowCandidate candidate, string appPath, CancellationToken cancellation)
    {
        if (!StillMatches(candidate)) return string.Empty;
        string path = SourceDocument.ResolvePath(appPath, candidate.Title, []);
        if (path.Length == 0 && SourceDocument.AppId(appPath) is "excel" or "winword" or "powerpnt")
            path = OfficeDocumentPath.Read(candidate.Handle, SourceDocument.AppId(appPath), cancellation);
        if (path.Length == 0)
        {
            var properties = new List<string>();
            AutomationElement root = AutomationElement.FromHandle(candidate.Handle);
            properties.Add(root.Current.Name);
            properties.Add(root.Current.HelpText);
            // Read selected tab metadata only, never editor text, ValuePattern or document contents.
            var walker = TreeWalker.ControlViewWalker;
            var pending = new Queue<(AutomationElement Element, int Depth)>();
            pending.Enqueue((root, 0));
            int visited = 0;
            while (pending.Count > 0 && visited++ < 80)
            {
                cancellation.ThrowIfCancellationRequested();
                var (element, depth) = pending.Dequeue();
                if (element.Current.ControlType == ControlType.Document)
                    properties.Add(element.Current.Name);
                if (element.Current.ControlType == ControlType.TabItem
                    && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selected)
                    && ((SelectionItemPattern)selected).Current.IsSelected)
                {
                    properties.Add(element.Current.Name);
                    properties.Add(element.Current.HelpText);
                }
                if (depth >= 4 || element.Current.ControlType == ControlType.Document
                    || element.Current.ControlType == ControlType.Edit) continue;
                int siblings = 0;
                for (AutomationElement? child = walker.GetFirstChild(element); child is not null && siblings++ < 40;
                    child = walker.GetNextSibling(child))
                {
                    cancellation.ThrowIfCancellationRequested();
                    pending.Enqueue((child, depth + 1));
                }
            }
            path = SourceDocument.ResolvePath(appPath, candidate.Title, properties);
        }
        return StillMatches(candidate) ? path : string.Empty;
    }

    private static bool StillMatches(WindowCandidate candidate)
    {
        _ = NativeMethods.GetWindowThreadProcessId(candidate.Handle, out uint processId);
        var title = new StringBuilder(4096);
        _ = NativeMethods.GetWindowText(candidate.Handle, title, title.Capacity);
        return processId == candidate.ProcessId && candidate.Title == title.ToString();
    }
}
