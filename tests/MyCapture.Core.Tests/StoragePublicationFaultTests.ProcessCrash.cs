using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MyCapture.Core.Serialization;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed partial class StoragePublicationFaultTests
{
    internal const string ChildRootVariable = "MYCAPTURE_STORAGE_FAULT_CHILD_ROOT";
    private const string ChildBoundaryVariable = "MYCAPTURE_STORAGE_FAULT_CHILD_BOUNDARY";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public Task OwnedProcessKilledAtPublicationBoundary_RestartsWithExpectedDurableState(int boundary) =>
        RunCrashBoundaryAsync(boundary, $"process-kill-{boundary}");

    private async Task<string> RunCrashBoundaryAsync(int boundary, string scenario)
    {
        Fixture fixture = await Fixture.CreateAsync(scenario);
        string dotnet = Environment.GetEnvironmentVariable("MYCAPTURE_STORAGE_TEST_DOTNET")
            ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        string ready = Path.Combine(fixture.Root, "child-ready.json");
        string assembly = typeof(StoragePublicationFaultTests).Assembly.Location;
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = Fixture.RepositoryRoot(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[]
        {
            "vstest", assembly,
            "/TestCaseFilter:FullyQualifiedName=MyCapture.Core.Tests.StoragePublicationFaultTests.CrashBoundaryWorker",
            "/ResultsDirectory:" + Path.Combine(fixture.Root, "child-results"),
            "/Logger:trx;LogFileName=child.trx",
        }) start.ArgumentList.Add(argument);
        start.Environment.Remove(CampaignVariable);
        start.Environment[ChildRootVariable] = fixture.Root;
        start.Environment[ChildBoundaryVariable] = boundary.ToString(System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(fixture.Root, "child-command.json"), JsonSerializer.Serialize(new
        {
            executable = dotnet, arguments = start.ArgumentList.ToArray(), cwd = start.WorkingDirectory,
            fixture = fixture.Root, boundary,
        }, JsonDefaults.Readable));

        using var child = new Process { StartInfo = start };
        Assert.True(child.Start());
        // Keep the started Process instance and its OS handle throughout. Never reopen by PID.
        _ = child.SafeHandle;
        int ownedPid = child.Id;
        DateTime ownedStart = child.StartTime.ToUniversalTime();
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        bool reachedBoundary = false;
        bool killed = false;
        int? observedWorkerPid = null;
        Process? worker = null;
        var lineage = new List<object>();
        var markerRead = new CrashMarkerReadObservation();
        try
        {
            var deadline = Stopwatch.StartNew();
            string? markerText = await ReadCrashMarkerWithinDeadlineAsync(ready, deadline,
                TimeSpan.FromSeconds(45), () =>
                {
                    if (child.HasExited)
                        throw new InvalidOperationException($"Owned storage worker exited before boundary {boundary}; exit {child.ExitCode}.");
                }, markerRead);
            if (markerText is not null)
            {
                // The marker is a single atomic rename after its full JSON is written.
                // Parsing and all ownership checks remain outside transient I/O retries.
                using JsonDocument marker = JsonDocument.Parse(markerText);
                Assert.Equal(boundary, marker.RootElement.GetProperty("boundary").GetInt32());
                observedWorkerPid = marker.RootElement.GetProperty("workerPid").GetInt32();
                Assert.Equal(fixture.Root, marker.RootElement.GetProperty("fixture").GetString());
                worker = Process.GetProcessById(observedWorkerPid.Value);
                _ = worker.SafeHandle;
                Assert.Equal(marker.RootElement.GetProperty("workerStartUtcTicks").GetInt64(), worker.StartTime.ToUniversalTime().Ticks);
                AssertOwnedLineage(worker, child, ownedStart, lineage);
                reachedBoundary = true;
            }
            Assert.True(reachedBoundary, "Owned storage worker did not reach its bounded publication marker.");
            Assert.False(child.HasExited);
            Assert.Equal(ownedPid, child.Id);
            Assert.Equal(ownedStart, child.StartTime.ToUniversalTime());
            // Only the runner created above and its testhost descendants are terminated.
            child.Kill(entireProcessTree: true);
            killed = true;
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await worker!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(worker.HasExited);
            Assert.NotEqual(0, child.ExitCode);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                killed = true;
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            // WaitForExit on the runner alone does not prove descendant termination.
            // The actual testhost handle is pinned before kill and checked independently.
            bool workerExited = false;
            string? workerWaitError = null;
            if (worker is not null)
            {
                try
                {
                    await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    workerExited = worker.HasExited;
                }
                catch (TimeoutException error) { workerWaitError = error.ToString(); }
            }
            File.WriteAllText(Path.Combine(fixture.Root, "child-stdout.log"), await stdout.WaitAsync(WaitBudget));
            File.WriteAllText(Path.Combine(fixture.Root, "child-stderr.log"), await stderr.WaitAsync(WaitBudget));
            File.WriteAllText(Path.Combine(fixture.Root, "child-exit.json"), JsonSerializer.Serialize(new
            {
                ownedPid, ownedStartUtc = ownedStart, observedWorkerPid, reachedBoundary, killed, workerExited, workerWaitError, lineage,
                exited = child.HasExited, exitCode = child.HasExited ? child.ExitCode : (int?)null,
                boundary, model = "owned process tree termination at publication file boundaries; no OS reset",
                markerRead = new { markerRead.ReadAttempts, markerRead.TransientRetryCount,
                    markerRead.LastTransientHResult, markerRead.DeadlineExpired },
            }, JsonDefaults.Readable));
            worker?.Dispose();
            Assert.True(worker is null || workerExited, workerWaitError);
        }

        fixture.AssertDiskBoundary(boundary);
        fixture.WriteReceipt(new { status = "RAW_AFTER_KILL", boundary }, "after-kill.json");
        // A valid primary schema-2 index remains authoritative even if newer completed
        // metadata or the folder recovery sidecar was published before interruption.
        fixture.AssertReloaded(replacement: boundary == 3);
        fixture.AssertPayloadsUnchanged();
        fixture.WriteReceipt(new { status = "PASS", boundary, reachedBoundary, killed,
            expected = boundary == 3 ? "completed generation" : "previous committed index generation" });
        return fixture.Root;
    }

    private sealed class CrashMarkerReadObservation
    {
        public int ReadAttempts { get; set; }
        public int TransientRetryCount { get; set; }
        public int? LastTransientHResult { get; set; }
        public bool DeadlineExpired { get; set; }
    }

    private static async Task<string?> ReadCrashMarkerWithinDeadlineAsync(string path,
        Stopwatch deadline, TimeSpan budget, Action ensureOwnedChildRunning,
        CrashMarkerReadObservation observation, Func<string, string>? readAllText = null)
    {
        Func<string, string> read = readAllText ?? File.ReadAllText;
        while (deadline.Elapsed < budget)
        {
            if (File.Exists(path))
            {
                try
                {
                    observation.ReadAttempts++;
                    return read(path);
                }
                catch (IOException error) when (error.HResult == unchecked((int)0x80070020)
                    || error.HResult == unchecked((int)0x80070021))
                {
                    observation.TransientRetryCount++;
                    observation.LastTransientHResult = error.HResult;
                }
            }
            ensureOwnedChildRunning();
            await Task.Delay(25);
        }
        observation.DeadlineExpired = true;
        return null;
    }

    [StorageFaultWorkerFact]
    public async Task CrashBoundaryWorker()
    {
        string root = Environment.GetEnvironmentVariable(ChildRootVariable)
            ?? throw new InvalidOperationException("The crash worker requires its owning parent fixture.");
        int boundary = int.Parse(Environment.GetEnvironmentVariable(ChildBoundaryVariable)!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(boundary, 0, 3);
        Fixture fixture = Fixture.OpenChild(root);
        fixture.SetReplacement();
        string[] targets = fixture.PublicationPaths;
        fixture.Queue.BeforePublicationWriteForTest = (path, _) =>
        {
            if (boundary < 3 && path == targets[boundary]) PublishMarkerAndBlock();
        };
        await fixture.PublishAsync();
        Assert.Equal(3, boundary);
        PublishMarkerAndBlock();

        void PublishMarkerAndBlock()
        {
            using Process worker = Process.GetCurrentProcess();
            string marker = Path.Combine(root, "child-ready.json");
            string preparing = Path.Combine(root, "child-ready.prepared.json");
            File.WriteAllText(preparing, JsonSerializer.Serialize(new
            {
                boundary, workerPid = Environment.ProcessId, fixture = root,
                workerStartUtcTicks = worker.StartTime.ToUniversalTime().Ticks,
                completionAcknowledged = boundary == 3,
            }, JsonDefaults.Readable));
            File.Move(preparing, marker);
            // Only the parent kill releases this worker. No timeout can turn an unobserved
            // boundary into a successful child test. Parent owns the bounded deadline.
            Thread.Sleep(Timeout.Infinite);
        }
    }

    private static void AssertOwnedLineage(Process worker, Process ownedRunner, DateTime ownedStart, List<object> lineage)
    {
        Process current = worker;
        bool disposeCurrent = false;
        try
        {
            for (int depth = 0; depth < 8; depth++)
            {
                DateTime started = current.StartTime.ToUniversalTime();
                Assert.True(started >= ownedStart, "Worker lineage predates the owned runner.");
                int status = NtQueryInformationProcess(current.SafeHandle.DangerousGetHandle(), 0,
                    out ProcessBasicInformation information, Marshal.SizeOf<ProcessBasicInformation>(), out _);
                Assert.Equal(0, status);
                int parentId = checked((int)information.ParentProcessId);
                lineage.Add(new { pid = current.Id, startUtc = started, parentId });
                if (parentId == ownedRunner.Id)
                {
                    Assert.False(ownedRunner.HasExited);
                    Assert.Equal(ownedStart, ownedRunner.StartTime.ToUniversalTime());
                    return;
                }
                Process parent = Process.GetProcessById(parentId);
                _ = parent.SafeHandle;
                if (parent.StartTime.ToUniversalTime() > started)
                {
                    parent.Dispose();
                    throw new InvalidOperationException("Parent PID was reused after the worker started.");
                }
                if (disposeCurrent) current.Dispose();
                current = parent;
                disposeCurrent = true;
            }
            throw new InvalidOperationException("Marker worker is not a bounded descendant of the owned runner.");
        }
        finally { if (disposeCurrent) current.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        internal nint Reserved1;
        internal nint PebAddress;
        internal nint Reserved2;
        internal nint Reserved3;
        internal nint ProcessId;
        internal nint ParentProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint process, int informationClass,
        out ProcessBasicInformation information, int length, out int returnedLength);
}

internal sealed class StorageFaultWorkerFactAttribute : FactAttribute
{
    public StorageFaultWorkerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(StoragePublicationFaultTests.ChildRootVariable)))
            Skip = "Child-only crash worker: executed and killed by the four OwnedProcessKilledAtPublicationBoundary parent cases.";
    }
}
