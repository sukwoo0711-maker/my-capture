using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed partial class StoragePublicationFaultTests
{
    [Fact]
    public async Task CrashMarkerExclusiveLock_ReleaseAllowsExactRead()
    {
        MarkerReadFixture fixture = MarkerReadFixture.Create("release");
        var observation = new CrashMarkerReadObservation();
        var timer = Stopwatch.StartNew();
        FileStream? held = new(fixture.Marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Task<string?>? pending = null;
        bool completed = false;
        int ownerChecks = 0;
        try
        {
            pending = ReadCrashMarkerWithinDeadlineAsync(fixture.Marker, timer,
                WaitBudget, () => ownerChecks++, observation);
            // The async helper reaches its first failed real open before yielding to Delay.
            Assert.True(observation.TransientRetryCount >= 1);
            Assert.Equal(unchecked((int)0x80070020), observation.LastTransientHResult);
            Assert.False(pending.IsCompleted);
            held.Dispose();
            held = null;
            string? text = await pending.WaitAsync(WaitBudget);
            Assert.Equal(fixture.ExpectedText, text);
            using JsonDocument marker = JsonDocument.Parse(text!);
            Assert.Equal(2, marker.RootElement.GetProperty("boundary").GetInt32());
            Assert.True(ownerChecks >= 1);
            Assert.Equal(observation.TransientRetryCount + 1, observation.ReadAttempts);
            Assert.False(observation.DeadlineExpired);
            fixture.AssertUnchanged();
            completed = true;
        }
        finally
        {
            held?.Dispose();
            try
            {
                if (pending is not null) await pending.WaitAsync(WaitBudget);
            }
            finally { fixture.WriteReceipt(observation, timer, completed, "real FileShare.None, then release"); }
        }
    }

    [Fact]
    public async Task CrashMarkerPersistentExclusiveLock_ExpiresWithoutExtendingDeadline()
    {
        MarkerReadFixture fixture = MarkerReadFixture.Create("persistent-lock");
        var observation = new CrashMarkerReadObservation();
        var timer = Stopwatch.StartNew();
        TimeSpan budget = TimeSpan.FromMilliseconds(125);
        bool completed = false;
        try
        {
            using (var held = new FileStream(fixture.Marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                string? marker = await ReadCrashMarkerWithinDeadlineAsync(fixture.Marker, timer,
                    budget, () => { }, observation).WaitAsync(WaitBudget);
                Assert.Null(marker);
                Assert.True(observation.DeadlineExpired);
                Assert.True(observation.TransientRetryCount >= 1);
                Assert.Equal(observation.TransientRetryCount, observation.ReadAttempts);
                Assert.Equal(unchecked((int)0x80070020), observation.LastTransientHResult);
                Assert.True(timer.Elapsed >= budget);
            }
            fixture.AssertUnchanged();
            completed = true;
        }
        finally { fixture.WriteReceipt(observation, timer, completed, "real lock kept until short test deadline"); }
    }

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    public async Task CrashMarkerOnlySharingAndLockViolations_AreRetried(int win32Error)
    {
        MarkerReadFixture fixture = MarkerReadFixture.Create($"transient-{win32Error}");
        var observation = new CrashMarkerReadObservation();
        var timer = Stopwatch.StartNew();
        int calls = 0;
        int ownerChecks = 0;
        int hresult = unchecked((int)0x80070000) | win32Error;
        bool completed = false;
        try
        {
            string? text = await ReadCrashMarkerWithinDeadlineAsync(fixture.Marker, timer, WaitBudget,
                () => ownerChecks++, observation, path => ++calls == 1
                    ? throw new IOException("Injected transient marker read error.", hresult)
                    : File.ReadAllText(path)).WaitAsync(WaitBudget);
            Assert.Equal(fixture.ExpectedText, text);
            Assert.Equal(2, calls);
            Assert.Equal(2, observation.ReadAttempts);
            Assert.Equal(1, ownerChecks);
            Assert.Equal(1, observation.TransientRetryCount);
            Assert.Equal(hresult, observation.LastTransientHResult);
            Assert.False(observation.DeadlineExpired);
            fixture.AssertUnchanged();
            completed = true;
        }
        finally { fixture.WriteReceipt(observation, timer, completed, "injected exact HRESULT, then actual file read"); }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(112)]
    public async Task CrashMarkerOtherIoErrors_PropagateWithoutRetry(int win32Error)
    {
        MarkerReadFixture fixture = MarkerReadFixture.Create($"other-io-{win32Error}");
        var observation = new CrashMarkerReadObservation();
        var timer = Stopwatch.StartNew();
        int ownerChecks = 0;
        var expected = new IOException("Injected non-transient marker read error.",
            unchecked((int)0x80070000) | win32Error);
        bool completed = false;
        try
        {
            IOException actual = await Assert.ThrowsAsync<IOException>(() =>
                ReadCrashMarkerWithinDeadlineAsync(fixture.Marker, timer, WaitBudget,
                    () => ownerChecks++, observation, _ => throw expected));
            Assert.Same(expected, actual);
            Assert.Equal(1, observation.ReadAttempts);
            Assert.Equal(0, observation.TransientRetryCount);
            Assert.Null(observation.LastTransientHResult);
            Assert.Equal(0, ownerChecks);
            Assert.False(observation.DeadlineExpired);
            fixture.AssertUnchanged();
            completed = true;
        }
        finally { fixture.WriteReceipt(observation, timer, completed, "injected non-transient IOException; no retry"); }
    }

    [Fact]
    public async Task CrashMarkerInvalidJson_FailsAtParseWithoutRetry()
    {
        MarkerReadFixture fixture = MarkerReadFixture.Create("invalid-json", "{invalid synthetic marker");
        var observation = new CrashMarkerReadObservation();
        var timer = Stopwatch.StartNew();
        bool completed = false;
        try
        {
            string? text = await ReadCrashMarkerWithinDeadlineAsync(fixture.Marker, timer,
                WaitBudget, () => { }, observation).WaitAsync(WaitBudget);
            Assert.Equal(fixture.ExpectedText, text);
            Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(text!));
            Assert.Equal(1, observation.ReadAttempts);
            Assert.Equal(0, observation.TransientRetryCount);
            Assert.False(observation.DeadlineExpired);
            fixture.AssertUnchanged();
            completed = true;
        }
        finally { fixture.WriteReceipt(observation, timer, completed, "actual invalid JSON; parse remains outside retry"); }
    }

    [Fact]
    public async Task CrashMarkerOwnedChildExit_StopsBeforeDeadline()
    {
        MarkerReadFixture fixture = MarkerReadFixture.Create("child-exited");
        var observation = new CrashMarkerReadObservation();
        var timer = Stopwatch.StartNew();
        var expected = new InvalidOperationException("Synthetic owned-child exit before marker.");
        string unpublished = Path.Combine(fixture.Root, "not-published.json");
        bool completed = false;
        try
        {
            Assert.False(File.Exists(unpublished));
            InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ReadCrashMarkerWithinDeadlineAsync(unpublished, timer, WaitBudget,
                    () => throw expected, observation));
            Assert.Same(expected, actual);
            Assert.Equal(0, observation.ReadAttempts);
            Assert.Equal(0, observation.TransientRetryCount);
            Assert.False(observation.DeadlineExpired);
            fixture.AssertUnchanged();
            completed = true;
        }
        finally { fixture.WriteReceipt(observation, timer, completed, "owned-exit guard callback; no child process started by this focused test"); }
    }

    private sealed class MarkerReadFixture
    {
        private const string DefaultText = "{\"boundary\":2,\"fixture\":\"owned-synthetic-marker\"}";
        private MarkerReadFixture(string root, string text)
        {
            Root = root;
            ExpectedText = text;
            Marker = Path.Combine(root, "child-ready.json");
            File.WriteAllText(Marker, text);
            BeforeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Marker)));
        }

        public string Root { get; }
        public string Marker { get; }
        public string ExpectedText { get; }
        public string BeforeSha256 { get; }

        public static MarkerReadFixture Create(string scenario, string text = DefaultText)
        {
            string root = Path.Combine(Fixture.RepositoryRoot(), "artifacts", "storage-marker-read-tests",
                scenario + "-" + Guid.NewGuid().ToString("N"));
            Assert.False(Directory.Exists(root));
            Directory.CreateDirectory(root);
            return new MarkerReadFixture(root, text);
        }

        public void AssertUnchanged() =>
            Assert.Equal(BeforeSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Marker))));

        public void WriteReceipt(CrashMarkerReadObservation observation, Stopwatch timer, bool completed, string scope)
        {
            using var stream = new FileStream(Path.Combine(Root, "receipt.json"), FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(stream, new
            {
                status = completed ? "PASS" : "INCOMPLETE_OR_FAILED", scope,
                observation.ReadAttempts, observation.TransientRetryCount,
                observation.LastTransientHResult, observation.DeadlineExpired,
                elapsedMilliseconds = timer.Elapsed.TotalMilliseconds, beforeSha256 = BeforeSha256,
                afterSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Marker))),
                evidencePreserved = true,
            }, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
