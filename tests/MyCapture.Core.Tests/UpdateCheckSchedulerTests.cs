using System.Text.Json;
using MyCapture.Core.Serialization;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.Core.Tests;

public sealed class UpdateCheckSchedulerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);

    private sealed class Harness(UpdateChannel channel = UpdateChannel.Store)
    {
        public DateTimeOffset Now { get; set; } = Start;
        public AppSettings Settings { get; set; } = new();
        public bool Deferred { get; set; }
        public int Checks { get; private set; }
        public int Saves { get; private set; }
        public List<Exception> Errors { get; } = [];
        public Action? BeforeCheck { get; set; }
        public Func<Task>? Check { get; set; }
        public Action? Save { get; set; }

        public UpdateCheckScheduler Scheduler { get; private set; } = null!;

        public Harness Initialize()
        {
            Scheduler = new(() => Settings.Updates, channel, () => Now);
            return this;
        }

        public Task<bool> Tick() => Scheduler.TryCheckAsync(() => Deferred, async () =>
        {
            Checks++;
            BeforeCheck?.Invoke();
            if (Check is not null) await Check();
        }, () =>
        {
            Saves++;
            Save?.Invoke();
        }, Errors.Add);
    }

    [Fact]
    public async Task FirstCheck_WaitsTwoFullMinutesAndPersistsBeforeNetworkAccess()
    {
        var h = new Harness().Initialize();
        h.Now = Start.AddMinutes(2).AddTicks(-1);
        Assert.False(await h.Tick());
        Assert.Equal(0, h.Checks);
        Assert.Equal(0, h.Saves);
        Assert.Null(h.Settings.Updates.LastCheckUtc);

        h.Now = Start.AddMinutes(2);
        h.BeforeCheck = () =>
        {
            Assert.Equal(1, h.Saves);
            Assert.Equal(h.Now, h.Settings.Updates.LastCheckUtc);
        };
        Assert.True(await h.Tick());
        Assert.Equal(1, h.Checks);
        Assert.Empty(h.Errors);
    }

    [Fact]
    public async Task AnotherLaunch_UsesThePersistedTwentyHourBoundary()
    {
        var first = new Harness().Initialize();
        first.Now = Start.AddMinutes(2);
        Assert.True(await first.Tick());
        string saved = JsonSerializer.Serialize(first.Settings, JsonDefaults.Readable);

        var second = new Harness { Now = first.Now.AddHours(19) }.Initialize();
        second.Settings = JsonSerializer.Deserialize<AppSettings>(saved, JsonDefaults.Readable)!;
        second.Now = first.Now.AddHours(20).AddTicks(-1);
        Assert.False(await second.Tick());
        second.Now = first.Now.AddHours(20);
        Assert.True(await second.Tick());
        Assert.Equal(1, second.Checks);
    }

    [Fact]
    public async Task RunningApp_RepeatsAfterTwentyFourHours()
    {
        var h = new Harness().Initialize();
        h.Now = Start.AddMinutes(2);
        Assert.True(await h.Tick());
        DateTimeOffset first = h.Now;
        h.Now = first.AddHours(20);
        Assert.False(await h.Tick());
        h.Now = first.AddHours(24).AddTicks(-1);
        Assert.False(await h.Tick());
        h.Now = first.AddHours(24);
        Assert.True(await h.Tick());
        Assert.Equal(2, h.Checks);
        Assert.Equal(2, h.Saves);
    }

    [Theory]
    [InlineData(UpdateChannel.Store)]
    [InlineData(UpdateChannel.GitHubInstall)]
    [InlineData(UpdateChannel.Portable)]
    public async Task SupportedChannels_CheckWhenEnabled(UpdateChannel channel)
    {
        var h = new Harness(channel).Initialize();
        h.Now = Start.AddMinutes(2);
        Assert.True(await h.Tick());
        Assert.Equal(1, h.Checks);
    }

    [Fact]
    public async Task DevelopmentBuild_NeverChecksOrPersists()
    {
        var h = new Harness(UpdateChannel.None).Initialize();
        h.Now = Start.AddYears(1);
        Assert.False(await h.Tick());
        Assert.Equal(0, h.Checks);
        Assert.Equal(0, h.Saves);
        Assert.Null(h.Settings.Updates.LastCheckUtc);
    }

    [Fact]
    public async Task DisabledChecks_RemainOffUntilTheLivePreferenceChanges()
    {
        var h = new Harness().Initialize();
        h.Settings.Updates.AutoCheck = false;
        h.Now = Start.AddDays(3);
        Assert.False(await h.Tick());
        Assert.Equal(0, h.Saves);
        h.Settings.Updates.AutoCheck = true;
        Assert.True(await h.Tick());
        Assert.Equal(1, h.Checks);
    }

    [Fact]
    public async Task BusyApp_PostponesWithoutConsumingTheCheck()
    {
        var h = new Harness { Deferred = true }.Initialize();
        h.Now = Start.AddMinutes(2);
        Assert.False(await h.Tick());
        h.Now = Start.AddHours(5);
        Assert.False(await h.Tick());
        Assert.Null(h.Settings.Updates.LastCheckUtc);
        Assert.Equal(0, h.Saves);
        h.Deferred = false;
        Assert.True(await h.Tick());
        Assert.Equal(h.Now, h.Settings.Updates.LastCheckUtc);
    }

    [Fact]
    public async Task FailedCheck_OnlyLogsAndWaitsForTheNextDailyAttempt()
    {
        var error = new HttpRequestException("Offline");
        var h = new Harness { Check = () => Task.FromException(error) }.Initialize();
        h.Now = Start.AddMinutes(2);
        Assert.True(await h.Tick());
        Assert.Same(error, Assert.Single(h.Errors));
        Assert.Equal(h.Now, h.Settings.Updates.LastCheckUtc);
        h.Now = h.Now.AddHours(23);
        Assert.False(await h.Tick());
        h.Now = h.Now.AddHours(1);
        Assert.True(await h.Tick());
        Assert.Equal(2, h.Checks);
        Assert.Equal(2, h.Errors.Count);
    }

    [Fact]
    public async Task FailedPersistence_OnlyLogsAndDoesNotStartNetworkAccess()
    {
        var error = new IOException("Settings write failed");
        var h = new Harness { Save = () => throw error }.Initialize();
        h.Now = Start.AddMinutes(2);
        Assert.True(await h.Tick());
        Assert.Same(error, Assert.Single(h.Errors));
        Assert.Equal(0, h.Checks);
        Assert.False(await h.Tick());
    }

    [Fact]
    public async Task CheckInProgress_PreventsAnOverlappingAttempt()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new Harness { Check = () => completed.Task }.Initialize();
        h.Now = Start.AddMinutes(2);
        Task<bool> first = h.Tick();
        Assert.False(first.IsCompleted);
        h.Now = h.Now.AddDays(2);
        Assert.False(await h.Tick());
        Assert.Equal(1, h.Checks);
        Assert.Equal(1, h.Saves);
        completed.SetResult();
        Assert.True(await first);
        Assert.True(await h.Tick());
    }

    [Fact]
    public async Task ManualCheckTimestamp_DefersTheScheduledCheck()
    {
        var h = new Harness().Initialize();
        h.Now = Start.AddMinutes(1);
        h.Settings.Updates.LastCheckUtc = h.Now;
        h.Now = Start.AddMinutes(2);
        Assert.False(await h.Tick());
        h.Now = Start.AddMinutes(1).AddHours(20);
        Assert.True(await h.Tick());
    }

    [Fact]
    public async Task ReplacingLiveSettings_KeepsTheScheduleAndWritesTheCurrentState()
    {
        var h = new Harness().Initialize();
        h.Now = Start.AddMinutes(2);
        Assert.True(await h.Tick());
        UpdateSettings oldState = h.Settings.Updates;
        h.Settings = h.Settings.DeepClone();
        h.Now = h.Now.AddHours(20);
        Assert.False(await h.Tick());
        h.Now = h.Now.AddHours(4);
        Assert.True(await h.Tick());
        Assert.Equal(Start.AddMinutes(2), oldState.LastCheckUtc);
        Assert.Equal(h.Now, h.Settings.Updates.LastCheckUtc);
    }
}
