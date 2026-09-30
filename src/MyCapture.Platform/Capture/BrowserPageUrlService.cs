using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Capture;

/// <summary>A browser document observed before the capture selector takes focus.</summary>
public sealed record BrowserDocumentSnapshot(uint ProcessId, string WindowTitle, string DocumentId, string Url);

/// <summary>
/// Reads actual browser document URLs, never editable address-bar contents. One background
/// probe owns the native provider until it finishes, including after the caller times out.
/// An unavailable or slow accessibility provider therefore cannot block capture or create
/// an unbounded number of native calls. Callers discard mismatched frame-bracketing reads.
/// </summary>
public sealed class BrowserPageUrlService
{
    private static readonly SemaphoreSlim SharedProbeGate = new(1, 1);
    private static readonly IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot> Empty =
        new Dictionary<IntPtr, BrowserDocumentSnapshot>();
    private readonly Func<IReadOnlyList<WindowCandidate>, CancellationToken,
        IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>> _probe;
    private readonly TimeSpan _budget;
    private readonly SemaphoreSlim _gate;

    public BrowserPageUrlService() : this(ReadNative, TimeSpan.FromMilliseconds(200), SharedProbeGate) { }

    internal BrowserPageUrlService(
        Func<IReadOnlyList<WindowCandidate>, CancellationToken,
            IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>> probe,
        TimeSpan budget,
        SemaphoreSlim gate)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromSeconds(2))
            throw new ArgumentOutOfRangeException(nameof(budget));
        _budget = budget;
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    public async Task<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>> ReadAsync(
        IReadOnlyList<WindowCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0 || !_gate.Wait(0)) return Empty;

        var completion = new TaskCompletionSource<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new CancellationTokenSource();
        WindowCandidate[] frozenCandidates = candidates.ToArray();
        var worker = new Thread(() =>
        {
            IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot> result = Empty;
            try
            {
                result = _probe(frozenCandidates, cancellation.Token);
                if (cancellation.IsCancellationRequested) result = Empty;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Accessibility is optional metadata. Do not log document contents/URLs.
            }
            finally
            {
                cancellation.Dispose();
                _gate.Release();
                completion.TrySetResult(result);
            }
        }) { IsBackground = true, Name = "MyCapture browser document URL" };
        worker.SetApartmentState(ApartmentState.MTA);
        try
        {
            worker.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            cancellation.Dispose();
            _gate.Release();
            return Empty;
        }

        Task winner = await Task.WhenAny(completion.Task, Task.Delay(_budget)).ConfigureAwait(false);
        if (winner == completion.Task) return await completion.Task.ConfigureAwait(false);
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* The provider finished concurrently with the deadline. */ }
        return Empty;
    }

    private static IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot> ReadNative(
        IReadOnlyList<WindowCandidate> candidates, CancellationToken cancellation)
    {
        var result = new Dictionary<IntPtr, BrowserDocumentSnapshot>();
        NativeAutomation? automation = null;
        try
        {
            foreach (WindowCandidate candidate in candidates)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    if (!TryValidateBrowserWindow(candidate, out uint processId)) continue;
                    automation ??= new NativeAutomation();
                    BrowserDocumentValue? document = FindVisibleDocument(
                        automation.FromHandle(candidate.Handle), candidate.ScreenBounds, cancellation);
                    if (document is null || !TryValidateBrowserWindow(candidate, out uint afterProcessId)
                        || processId != afterProcessId) continue;
                    result[candidate.Handle] = new BrowserDocumentSnapshot(processId,
                        candidate.Title, document.DocumentId, document.Url);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    // A closed window or unsupported provider contributes no URL.
                }
            }
        }
        finally { automation?.Dispose(); }
        return result;
    }

    private static bool TryValidateBrowserWindow(WindowCandidate candidate, out uint processId)
    {
        processId = 0;
        if (candidate.Handle == IntPtr.Zero || !NativeMethods.IsWindowVisible(candidate.Handle)
            || NativeMethods.IsIconic(candidate.Handle)) return false;
        _ = NativeMethods.GetWindowThreadProcessId(candidate.Handle, out processId);
        if (processId == 0 || (candidate.ProcessId != 0 && candidate.ProcessId != processId)
            || !string.Equals(new WindowTitleService().ReadTitle(candidate.Handle), candidate.Title,
                StringComparison.Ordinal)) return false;
        using Process process = Process.GetProcessById(checked((int)processId));
        var className = new StringBuilder(256);
        if (GetClassName(candidate.Handle, className, className.Capacity) <= 0) return false;
        return IsSupportedBrowser(process.ProcessName, className.ToString());
    }

    internal static bool IsSupportedBrowser(string processName, string windowClass) =>
        processName.Equals("firefox", StringComparison.OrdinalIgnoreCase)
            ? windowClass == "MozillaWindowClass"
            : windowClass == "Chrome_WidgetWin_1" &&
              (processName.Equals("chrome", StringComparison.OrdinalIgnoreCase)
               || processName.Equals("msedge", StringComparison.OrdinalIgnoreCase)
               || processName.Equals("brave", StringComparison.OrdinalIgnoreCase)
               || processName.Equals("vivaldi", StringComparison.OrdinalIgnoreCase)
               || processName.Equals("opera", StringComparison.OrdinalIgnoreCase));

    internal static bool IsSemanticDocument(object? controlType, object? isControlElement, object? isContentElement) =>
        controlType is int type && type == 50030 && (isControlElement is true || isContentElement is true);

    /// <summary>
    /// Stops at top-level documents, so iframe content can never supply the page URL.
    /// Incomplete walks, invalid documents and multiple visible documents are ambiguous.
    /// </summary>
    internal static BrowserDocumentValue? FindVisibleDocument(IBrowserDocumentNode root, RectD windowBounds,
        CancellationToken cancellation = default, int maxNodes = 1024, int maxDepth = 24)
    {
        int visited = 0;
        bool complete = true;
        BrowserDocumentValue? found = null;
        Visit(root, 0);
        return complete ? found : null;

        void Visit(IBrowserDocumentNode node, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (++visited > maxNodes) { complete = false; return; }
            BrowserDocumentNodeInfo info = node.ReadInfo();
            if (info.IsOffscreen) return;
            if (info.IsDocument)
            {
                // Raw UIA also exposes empty placeholder documents. Neither they nor
                // their nested frames can establish the visible top-level page URL.
                if (!Intersects(info.Bounds, windowBounds)) return;
                if (found is not null)
                {
                    complete = false;
                    return;
                }
                BrowserDocumentValues values = node.ReadDocumentValues();
                string value = SourcePageUrl.Normalize(values.Value);
                string legacyValue = SourcePageUrl.Normalize(values.LegacyValue);
                if (value.Length != 0 && legacyValue.Length != 0 && value != legacyValue)
                {
                    complete = false;
                    return;
                }
                string url = value.Length != 0 ? value : legacyValue;
                string identity = node.ReadIdentity();
                if (url.Length == 0 || string.IsNullOrEmpty(identity)) complete = false;
                else found = new BrowserDocumentValue(identity, url);
                return;
            }

            IBrowserDocumentNode? child = node.FirstChild();
            if (depth >= maxDepth && child is not null) { complete = false; return; }
            while (child is not null && complete)
            {
                Visit(child, depth + 1);
                if (!complete) break;
                cancellation.ThrowIfCancellationRequested();
                child = child.NextSibling();
            }
        }
    }

    private static bool Intersects(RectD a, RectD b) => !a.IsEmpty && !b.IsEmpty &&
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int capacity);

    // The native property surface includes LegacyIAccessible.Value, which is absent from
    // the managed UIAutomationClient wrapper. No dependency or browser extension is needed.
    private sealed class NativeAutomation : IDisposable
    {
        private readonly HashSet<object> _owned = new(ReferenceEqualityComparer.Instance);
        private readonly IUiAutomation _automation;
        private readonly IUiAutomationTreeWalker _walker;
        private readonly IUiAutomationCacheRequest _cacheRequest;

        internal NativeAutomation()
        {
            try
            {
                Type type = Type.GetTypeFromCLSID(new Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E"), true)!;
                _automation = Own((IUiAutomation)Activator.CreateInstance(type)!);
                // Chromium can mark its root web document IsControlElement=false. The
                // control view then removes it while retaining editable descendants.
                _walker = Own(_automation.GetRawViewWalker());
                // Each probe owns fresh element-only caches. Batch only the two fields read
                // for every node; document semantics, bounds, URLs and identity stay live.
                _cacheRequest = Own(_automation.CreateCacheRequest());
                _cacheRequest.SetTreeScope(1); // TreeScope.Element: never prefetch descendants.
                _cacheRequest.SetTreeFilter(Own(_automation.GetRawViewCondition()));
                _cacheRequest.SetAutomationElementMode(1); // Full: current document queries remain available.
                _cacheRequest.AddProperty(30022); // IsOffscreen
                _cacheRequest.AddProperty(30003); // ControlType
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private T Own<T>(T value) where T : class { _owned.Add(value); return value; }
        internal IBrowserDocumentNode FromHandle(IntPtr handle) =>
            new NativeNode(this, Own(_automation.ElementFromHandleBuildCache(handle, _cacheRequest)));
        private IBrowserDocumentNode? Wrap(IUiAutomationElement? element) =>
            element is null ? null : new NativeNode(this, Own(element));

        public void Dispose()
        {
            foreach (object item in _owned)
            {
                if (Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
            }
            _owned.Clear();
        }

        private sealed class NativeNode(NativeAutomation owner, IUiAutomationElement element) : IBrowserDocumentNode
        {
            public BrowserDocumentNodeInfo ReadInfo()
            {
                // Unsupported visibility is not evidence that this is the active page.
                bool offscreen = element.GetCachedPropertyValue(30022) is not false;
                object controlType = element.GetCachedPropertyValue(30003);
                // Raw view includes layout-only WebView document wrappers. Their semantic
                // flags are both false; descend through those containers to the page.
                bool document = controlType is int type && type == 50030
                    && IsSemanticDocument(controlType, element.GetCurrentPropertyValue(30016),
                        element.GetCurrentPropertyValue(30017));
                RectD bounds = RectD.Empty;
                if (document && element.GetCurrentPropertyValue(30001) is double[] { Length: 4 } rectangle)
                    bounds = new RectD(rectangle[0], rectangle[1], rectangle[2], rectangle[3]);
                return new BrowserDocumentNodeInfo(document, offscreen, bounds);
            }

            public BrowserDocumentValues ReadDocumentValues() => new(
                element.GetCurrentPropertyValue(30045) as string,
                element.GetCurrentPropertyValue(30093) as string);
            public string ReadIdentity() => string.Join(',', element.GetRuntimeId());
            public IBrowserDocumentNode? FirstChild() =>
                owner.Wrap(owner._walker.GetFirstChildElementBuildCache(element, owner._cacheRequest));
            public IBrowserDocumentNode? NextSibling() =>
                owner.Wrap(owner._walker.GetNextSiblingElementBuildCache(element, owner._cacheRequest));
        }
    }

    // Only the prefix through the last called method is declared. Unused methods retain
    // their COM vtable slots and are never invoked.
    [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUiAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        IUiAutomationElement ElementFromHandle(IntPtr hwnd);
        void ElementFromPoint();
        void GetFocusedElement();
        void GetRootElementBuildCache();
        IUiAutomationElement ElementFromHandleBuildCache(IntPtr hwnd, IUiAutomationCacheRequest cacheRequest);
        void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache();
        void CreateTreeWalker();
        IUiAutomationTreeWalker GetControlViewWalker();
        void GetContentViewWalker();
        IUiAutomationTreeWalker GetRawViewWalker();
        IUiAutomationCondition GetRawViewCondition();
        void GetControlViewCondition();
        void GetContentViewCondition();
        IUiAutomationCacheRequest CreateCacheRequest();
    }

    [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUiAutomationElement
    {
        void SetFocus();
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)]
        int[] GetRuntimeId();
        void FindFirst();
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCurrentPropertyValue(int propertyId);
        void GetCurrentPropertyValueEx();
        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCachedPropertyValue(int propertyId);
    }

    [ComImport, Guid("4042C624-389C-4AFC-A630-9DF854A541FC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUiAutomationTreeWalker
    {
        void GetParentElement();
        IUiAutomationElement? GetFirstChildElement(IUiAutomationElement element);
        void GetLastChildElement();
        IUiAutomationElement? GetNextSiblingElement(IUiAutomationElement element);
        void GetPreviousSiblingElement();
        void NormalizeElement();
        void GetParentElementBuildCache();
        IUiAutomationElement? GetFirstChildElementBuildCache(IUiAutomationElement element, IUiAutomationCacheRequest cacheRequest);
        void GetLastChildElementBuildCache();
        IUiAutomationElement? GetNextSiblingElementBuildCache(IUiAutomationElement element, IUiAutomationCacheRequest cacheRequest);
    }

    [ComImport, Guid("B32A92B5-BC25-4078-9C08-D7EE95C48E03"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUiAutomationCacheRequest
    {
        void AddProperty(int propertyId);
        void AddPattern();
        void Clone();
        void GetTreeScope();
        void SetTreeScope(int scope);
        void GetTreeFilter();
        void SetTreeFilter(IUiAutomationCondition filter);
        void GetAutomationElementMode();
        void SetAutomationElementMode(int mode);
    }

    [ComImport, Guid("352FFBA8-0973-437C-A61F-F64CAFD81DF9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUiAutomationCondition { }
}

internal readonly record struct BrowserDocumentNodeInfo(bool IsDocument, bool IsOffscreen, RectD Bounds);
internal readonly record struct BrowserDocumentValues(string? Value, string? LegacyValue);
internal sealed record BrowserDocumentValue(string DocumentId, string Url);

internal interface IBrowserDocumentNode
{
    BrowserDocumentNodeInfo ReadInfo();
    BrowserDocumentValues ReadDocumentValues();
    string ReadIdentity();
    IBrowserDocumentNode? FirstChild();
    IBrowserDocumentNode? NextSibling();
}
