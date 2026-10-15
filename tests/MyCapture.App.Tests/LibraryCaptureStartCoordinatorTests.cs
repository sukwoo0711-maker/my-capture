using MyCapture.App.Gallery;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class LibraryCaptureStartCoordinatorTests
{
    [Theory]
    [InlineData("recording")]
    [InlineData("region capture")]
    public async Task HidesAndConfirmsBeforeStarting_AndIgnoresRepeatedClicks(string command)
    {
        var events = new List<string>();
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new LibraryCaptureStartCoordinator(async () =>
        {
            events.Add("hidden");
            await confirmed.Task;
            events.Add("confirmed");
        }, () => events.Add("restored"));

        Task pending = coordinator.StartAsync(() => events.Add(command));
        Assert.Equal(new[] { "hidden" }, events);
        Assert.False(pending.IsCompleted);
        await coordinator.StartAsync(() => events.Add("duplicate"));
        confirmed.SetResult();
        await pending;
        Assert.Equal(new[] { "hidden", "confirmed", command }, events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureRestoresLibraryAndAllowsRetry(bool startFails)
    {
        int attempts = 0;
        int started = 0;
        int restored = 0;
        var coordinator = new LibraryCaptureStartCoordinator(() =>
        {
            if (++attempts == 1 && !startFails) throw new TimeoutException();
            return Task.CompletedTask;
        }, () => restored++);

        await Assert.ThrowsAsync<TimeoutException>(() => coordinator.StartAsync(() => throw new TimeoutException()));
        Assert.Equal(1, restored);
        await coordinator.StartAsync(() => started++);
        Assert.Equal(1, started);
        Assert.Equal(2, attempts);
        Assert.Equal(1, restored);
    }
}
