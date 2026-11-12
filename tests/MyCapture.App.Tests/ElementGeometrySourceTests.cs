using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

public sealed class ElementGeometrySourceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DefaultPathCreatesNoWorkerClientOrLookup()
    {
        var source = new ElementGeometrySource();
        Assert.False(source.WorkerStarted);
        Assert.Equal(0, source.LookupCount);
        Assert.Equal(0, source.ClientCreations);
        source.Dispose();
        await source.WorkerStopped.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(source.WorkerStarted);
    }

    [Fact]
    public async Task ForeignWindowReturnsNestedPhysicalRectangles()
    {
        using var fixture = await Fixture.Start();
        using var source = new ElementGeometrySource();
        var result = await ReadReady(source, fixture);
        output.WriteLine($"status={result.Status}, clients={source.ClientCreations}, chain={string.Join(";", result.Chain)}");
        Assert.Equal(ElementGeometryStatus.Success, result.Status);
        Assert.Contains(result.Chain, e => Near(e.Bounds, fixture.Outer));
        Assert.Contains(result.Chain, e => Near(e.Bounds, fixture.Inner));
        Assert.Contains(result.Chain, e => Near(e.Bounds, fixture.Raw));
        Assert.Contains(result.Chain, e => Near(e.Bounds, fixture.Button));
        Assert.Equal(source.LookupCount, source.ClientCreations);
        source.Dispose();
        await source.WorkerStopped.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task HungForeignUiThreadIsBoundedAndCallerRemainsResponsive()
    {
        double fastest = await FastestLookup(async (fixture, source) =>
        {
            await fixture.Hang();
            var elapsed = Stopwatch.StartNew();
            Task<ElementGeometryResult> read = source.ReadAsync(fixture.Candidate, fixture.Button.Center, default);
            Assert.True(elapsed.ElapsedMilliseconds < 100, "The caller must never wait on UI Automation.");
            // A fresh hang is not yet detected by IsHungAppWindow: this exercises UIA's own timeouts.
            var result = await read.WaitAsync(TimeSpan.FromSeconds(2));
            double milliseconds = elapsed.Elapsed.TotalMilliseconds;
            output.WriteLine($"status={result.Status}, elapsed={milliseconds:0.0}ms, clients={source.ClientCreations}");
            Assert.Equal(ElementGeometryStatus.TimedOut, result.Status);
            return milliseconds;
        });
        Assert.InRange(fastest, 0, LookupBound);
    }

    private const double LookupBound = ElementGeometrySource.LookupTimeoutMilliseconds + 400;

    // The bound is on the lookup, not on the machine: one lookup on a CI runner took 1,468 ms against the usual
    // 250 ms. Each attempt hangs or widens a fresh fixture, and the fastest of up to three is the one bounded.
    // UIA's own timeouts (2 s and 20 s) or calls chained past the budget would slow every attempt alike.
    private static async Task<double> FastestLookup(Func<Fixture, ElementGeometrySource, Task<double>> lookup)
    {
        double fastest = double.MaxValue;
        for (int attempt = 0; attempt < 3 && fastest > LookupBound; attempt++)
        {
            using var fixture = await Fixture.Start();
            using var source = new ElementGeometrySource();
            // Establish that this is a reachable real provider before the lookup under test.
            Assert.Equal(ElementGeometryStatus.Success, (await ReadReady(source, fixture)).Status);
            fastest = Math.Min(fastest, await lookup(fixture, source));
            source.Dispose();
            await source.WorkerStopped.WaitAsync(TimeSpan.FromSeconds(1));
        }
        return fastest;
    }

    [Fact]
    public async Task OwnProcessWindowNeverCreatesAutomationClient() => await Task.Run(() =>
        StaTestHost.Run(() =>
        {
            var parameters = new System.Windows.Interop.HwndSourceParameters("Element source own process test")
            { Width = 10, Height = 10, WindowStyle = unchecked((int)0x80000000) };
            using var hwnd = new System.Windows.Interop.HwndSource(parameters);
            using var source = new ElementGeometrySource();
            var window = new WindowCandidate(hwnd.Handle, new RectD(0, 0, 10, 10), "", ProcessId: (uint)Environment.ProcessId);
            var task = source.ReadAsync(window, new PointD(1, 1), default);
            Assert.True(task.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal(ElementGeometryStatus.Unavailable, task.Result.Status);
            Assert.Equal(0, source.ClientCreations);
        }));

    private static bool Near(RectD a, RectD b) => Math.Abs(a.X - b.X) < 2 && Math.Abs(a.Y - b.Y) < 2
        && Math.Abs(a.Width - b.Width) < 2 && Math.Abs(a.Height - b.Height) < 2;

    private static async Task<ElementGeometryResult> ReadReady(ElementGeometrySource source, Fixture fixture)
    {
        var clock = Stopwatch.StartNew();
        ElementGeometryResult result;
        do
        {
            result = await source.ReadAsync(fixture.Candidate, fixture.Button.Center, default).WaitAsync(TimeSpan.FromSeconds(2));
            if (result.Status == ElementGeometryStatus.Success && result.Chain.Any(e => Near(e.Bounds, fixture.Button))) return result;
            await Task.Delay(60);
        } while (clock.Elapsed < TimeSpan.FromSeconds(10));
        return result;
    }

    [Fact]
    public async Task WideForeignTreeRetainsContainerWhenBudgetOrNodeLimitIsReached()
    {
        double fastest = await FastestLookup(async (fixture, source) =>
        {
            await fixture.Wide();
            var clock = Stopwatch.StartNew();
            var result = await source.ReadAsync(fixture.Candidate, fixture.Candidate.ScreenBounds.Center, default).WaitAsync(TimeSpan.FromSeconds(2));
            double milliseconds = clock.Elapsed.TotalMilliseconds;
            output.WriteLine($"wide status={result.Status}, partial={result.Partial}, levels={result.Chain.Count}, elapsed={milliseconds:0.0}ms");
            Assert.Equal(ElementGeometryStatus.Success, result.Status);
            Assert.True(result.Partial);
            Assert.Contains(result.Chain, e => Near(e.Bounds, fixture.Outer));
            return milliseconds;
        });
        Assert.InRange(fastest, 0, LookupBound);
    }

    private sealed class Fixture(Process process, JsonElement info) : IDisposable
    {
        internal WindowCandidate Candidate => new(new IntPtr(info.GetProperty("Handle").GetInt64()), Box("Window"), "",
            ProcessId: info.GetProperty("ProcessId").GetUInt32());
        internal RectD Outer => Box("Outer");
        internal RectD Inner => Box("Inner");
        internal RectD Raw => Box("Raw");
        internal RectD Button => Box("Button");
        private RectD Box(string name)
        {
            JsonElement box = info.GetProperty(name);
            return new(box.GetProperty("X").GetDouble(), box.GetProperty("Y").GetDouble(),
                box.GetProperty("Width").GetDouble(), box.GetProperty("Height").GetDouble());
        }
        internal static async Task<Fixture> Start()
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "MyCapture.ElementFixture.dll"));
            var process = Process.Start(start)!;
            try
            {
                string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (line is null) throw new InvalidOperationException("Fixture failed: " + await process.StandardError.ReadToEndAsync());
                using JsonDocument document = JsonDocument.Parse(line);
                return new Fixture(process, document.RootElement.Clone());
            }
            catch { if (!process.HasExited) process.Kill(); process.Dispose(); throw; }
        }
        internal async Task Hang()
        {
            await process.StandardInput.WriteLineAsync("hang");
            await process.StandardInput.FlushAsync();
            Assert.Equal("hanging", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        }
        internal async Task Wide()
        {
            await process.StandardInput.WriteLineAsync("wide");
            await process.StandardInput.FlushAsync();
            Assert.Equal("wide", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        }
        public void Dispose()
        {
            if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); }
            process.Dispose();
        }
    }
}
