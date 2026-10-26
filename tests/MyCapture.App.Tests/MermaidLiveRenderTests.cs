using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Pinning;
using Xunit;

namespace MyCapture.App.Tests;

// Runs only when MYCAPTURE_MERMAID_LIVE names an output folder: it starts the real hidden WebView2 with
// the bundled Mermaid, renders the sample diagrams, writes PNGs and a timing/process report, and waits
// out the idle timeout to prove the browser is released. It never reads the clipboard or a user profile;
// its browser profile is a fresh folder under the output folder.
public sealed class MermaidLiveRenderTests
{
    private const string DefaultSamples = @"C:\GitRepositories\my-capture\work\pin-content-20261027\test-samples";

    [Fact]
    public Task LiveRendersSamplesAndReleasesTheBrowserWhenIdle()
    {
        string? output = Environment.GetEnvironmentVariable("MYCAPTURE_MERMAID_LIVE");
        if (string.IsNullOrWhiteSpace(output)) return Task.CompletedTask;
        string samples = Environment.GetEnvironmentVariable("MYCAPTURE_SAMPLES") ?? DefaultSamples;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = WpfTestThread.Create(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await RunAsync(output, samples, dispatcher); completion.TrySetResult(true); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task RunAsync(string output, string samples, Dispatcher dispatcher)
    {
        Directory.CreateDirectory(output);
        DateTime started = DateTime.Now;
        using var renderer = new MermaidRenderer(Path.Combine(output, "browser-profile"), dispatcher);
        var report = new List<object>();
        string[] names = ["A3-mermaid-flowchart.mmd", "A3-mermaid-sequence.mmd", "A3-mermaid-class.mmd", "A3-mermaid-state.mmd",
            "A3-mermaid-pie.mmd", "A3-mermaid-gantt.mmd", "A3-mermaid-er.mmd", "T7-mermaid-flowchart-en.mmd", "A3-mermaid-invalid.mmd"];
        foreach (string name in names)
        {
            string path = Path.Combine(samples, name);
            if (!File.Exists(path)) continue;
            string source = File.ReadAllText(path);
            var clock = Stopwatch.StartNew();
            MermaidRenderResult result = await renderer.RenderAsync(source, 1, CancellationToken.None);
            clock.Stop();
            bool invalid = name.Contains("invalid", StringComparison.Ordinal);
            if (invalid) Assert.Null(result.Image); else Assert.NotNull(result.Image);
            if (result.Image is { } image) Save(image, Path.Combine(output, Path.GetFileNameWithoutExtension(name) + ".png"));
            report.Add(new { name, ms = clock.Elapsed.TotalMilliseconds, ok = result.Image is not null, result.Error });
        }

        uint browser = renderer.BrowserProcessId;
        int alive = Process.GetProcessesByName("msedgewebview2").Count(process => Started(process, started));
        Assert.True(renderer.IsBrowserCreated);
        // The release timer starts after the last render; wait it out with a margin.
        await Task.Delay(MermaidRenderer.IdleTimeout + TimeSpan.FromSeconds(5));
        bool created = renderer.IsBrowserCreated;
        int aliveAfter = Process.GetProcessesByName("msedgewebview2").Count(process => Started(process, started));
        bool browserGone = browser == 0 || !ProcessExists((int)browser);
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            renders = report, browserPid = browser, webViewProcessesWhileIdle = alive,
            browserCreatedAfterIdle = created, webViewProcessesAfterIdle = aliveAfter, browserProcessGone = browserGone,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.False(created);
        Assert.True(browserGone);
    }

    private static bool Started(Process process, DateTime since)
    {
        try { return process.StartTime >= since.AddSeconds(-1); }
        catch (Exception) { return false; }
    }

    private static bool ProcessExists(int id)
    {
        try { using Process process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
