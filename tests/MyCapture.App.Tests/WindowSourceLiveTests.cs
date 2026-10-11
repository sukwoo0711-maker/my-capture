using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in: reads source metadata from real windows through the production native paths.
/// </summary>
/// <remarks>
/// Never runs by default. With <c>MYCAPTURE_REAL_SOURCE_TEST=1</c> it opens one File Explorer window
/// in its own process (<c>explorer.exe /separate</c>) on a new empty temporary folder, reads that
/// window's application and folder, then closes only that window and deletes the folder. If Explorer
/// puts the folder into an existing window instead, the test stops without touching that window.
/// The second test only resolves application names of windows that are already open; it writes
/// application names and window classes to the test output, never titles, links or paths.
/// </remarks>
public sealed class WindowSourceLiveTests(ITestOutputHelper output)
{
    [RealWindowSourceFact]
    public async Task ExplorerWindowInItsOwnProcessReportsItsFolderAndApplication()
    {
        string folder = Path.Combine(Path.GetTempPath(), "MyCapture source test " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        HashSet<IntPtr> existing = [.. Windows().Where(window => window.ClassName == "CabinetWClass").Select(window => window.Handle)];
        IntPtr opened = IntPtr.Zero;
        try
        {
            using (Process.Start(new ProcessStartInfo("explorer.exe", "/separate,\"" + folder + "\"") { UseShellExecute = false })) { }
            var wait = Stopwatch.StartNew();
            while (opened == IntPtr.Zero && wait.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(250);
                opened = Windows().FirstOrDefault(window => window.ClassName == "CabinetWClass"
                    && !existing.Contains(window.Handle)
                    && window.Title.Contains(Path.GetFileName(folder), StringComparison.Ordinal)).Handle;
            }

            Assert.True(opened != IntPtr.Zero, "Explorer did not open a separate window for the test folder.");
            await Task.Delay(500); // Let the view finish navigating before the shell is asked.
            (IntPtr handle, string title, string className, uint processId) = Windows().Single(window => window.Handle == opened);
            var candidate = new WindowCandidate(handle, new RectD(0, 0, 100, 100), title, ProcessId: processId, ClassName: className);

            var service = new WindowSourceService();
            var timer = Stopwatch.StartNew();
            IReadOnlyDictionary<IntPtr, WindowSourceSnapshot> first = await service.ReadAsync([candidate]);
            double firstMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            IReadOnlyDictionary<IntPtr, WindowSourceSnapshot> second = await service.ReadAsync([candidate]);
            double secondMs = timer.Elapsed.TotalMilliseconds;

            WindowSourceSnapshot snapshot = Assert.Contains(handle, first);
            Assert.Equal(processId, snapshot.ProcessId);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), snapshot.DocumentPath, ignoreCase: true);
            Assert.EndsWith("explorer.exe", snapshot.AppPath, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.AppName));
            var stable = WindowSourceService.StableDetails(snapshot, Assert.Contains(handle, second), processId);
            Assert.Equal(snapshot.DocumentPath, stable.DocumentPath);
            Assert.Equal(snapshot.AppName, stable.AppName);
            output.WriteLine($"Explorer app name: {snapshot.AppName}; folder read: yes; first read {firstMs:0} ms, second read {secondMs:0} ms");
            Assert.True(secondMs < ExplorerFolderService.DefaultBudget.TotalMilliseconds,
                $"A warm read took {secondMs:0} ms, beyond the {ExplorerFolderService.DefaultBudget.TotalMilliseconds:0} ms budget.");
        }
        finally
        {
            if (opened != IntPtr.Zero) _ = PostMessage(opened, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            for (int attempt = 0; attempt < 20 && Directory.Exists(folder); attempt++)
            {
                try { Directory.Delete(folder); }
                catch (IOException) { await Task.Delay(250); }
            }
        }
    }

    [RealWindowSourceFact]
    public async Task OpenBrowsersAndStoreAppsResolveToTheirOwnApplication()
    {
        (IntPtr Handle, string Title, string ClassName, uint ProcessId)[] windows = Windows()
            .Where(window => window.ClassName is "Chrome_WidgetWin_1" or "MozillaWindowClass" or "ApplicationFrameWindow"
                && window.Title.Length > 0)
            .ToArray();
        WindowCandidate[] candidates = windows.Select(window => new WindowCandidate(window.Handle, new RectD(0, 0, 100, 100),
            window.Title, ProcessId: window.ProcessId, ClassName: window.ClassName)).ToArray();
        var timer = Stopwatch.StartNew();
        IReadOnlyDictionary<IntPtr, WindowSourceSnapshot> sources = await new WindowSourceService().ReadAsync(candidates);
        output.WriteLine($"{candidates.Length} windows resolved in {timer.Elapsed.TotalMilliseconds:0} ms");
        foreach ((IntPtr handle, _, string className, _) in windows)
        {
            string name = sources.TryGetValue(handle, out WindowSourceSnapshot? source) ? source.AppName : "(none)";
            string file = source is null ? string.Empty : Path.GetFileName(source.AppPath);
            output.WriteLine($"{className}: {name} [{file}]");
            if (className == "ApplicationFrameWindow" && source is { AppPath.Length: > 0 })
                Assert.NotEqual("ApplicationFrameHost.exe", file, StringComparer.OrdinalIgnoreCase);
        }

        // The selector's own batch: every window the capture overlay would consider, read twice the
        // way the frame is bracketed, next to the page-link probe it runs beside.
        IReadOnlyList<WindowCandidate> desktop = new WindowCandidateService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowCandidateService>.Instance)
            .GetCandidates(MyCapture.Platform.Display.MonitorEnumerator.GetVirtualDesktopBounds());
        var service = new WindowSourceService();
        var pages = new BrowserPageUrlService();
        for (int pass = 1; pass <= 2; pass++)
        {
            timer.Restart();
            Task<IReadOnlyDictionary<IntPtr, WindowSourceSnapshot>> sourceRead = service.ReadAsync(desktop);
            Task<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>> pageRead = pages.ReadAsync(desktop);
            IReadOnlyDictionary<IntPtr, WindowSourceSnapshot> all = await sourceRead;
            double sourceMs = timer.Elapsed.TotalMilliseconds;
            IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot> links = await pageRead;
            output.WriteLine($"Pass {pass}: {desktop.Count} windows, sources for {all.Count} in {sourceMs:0} ms "
                + $"({all.Values.Count(item => item.DocumentPath.Length > 0)} with a folder); page links for {links.Count}, "
                + $"both done in {timer.Elapsed.TotalMilliseconds:0} ms");
        }
    }

    private static List<(IntPtr Handle, string Title, string ClassName, uint ProcessId)> Windows()
    {
        var result = new List<(IntPtr, string, string, uint)>();
        _ = EnumWindows((handle, data) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var className = new StringBuilder(256);
            _ = GetClassName(handle, className, className.Capacity);
            var title = new StringBuilder(512);
            _ = GetWindowText(handle, title, title.Capacity);
            _ = GetWindowThreadProcessId(handle, out uint processId);
            result.Add((handle, title.ToString(), className.ToString(), processId));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder name, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}

/// <summary>Skips unless <c>MYCAPTURE_REAL_SOURCE_TEST=1</c>: the test opens and closes a real Explorer window.</summary>
internal sealed class RealWindowSourceFactAttribute : FactAttribute
{
    public RealWindowSourceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MYCAPTURE_REAL_SOURCE_TEST") != "1")
            Skip = "Opt-in real window source test: set MYCAPTURE_REAL_SOURCE_TEST=1 (it opens and closes one Explorer window).";
    }
}
