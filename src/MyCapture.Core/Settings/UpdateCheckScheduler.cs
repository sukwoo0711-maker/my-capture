namespace MyCapture.Core.Settings;

public enum UpdateChannel
{
    None,
    Store,
    GitHubInstall,
    Portable,
}

/// <summary>Schedules idle-time checks without owning a timer, network service or UI.</summary>
public sealed class UpdateCheckScheduler
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MinimumCheckInterval = TimeSpan.FromHours(20);
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromHours(24);

    private readonly Func<UpdateSettings> _settings;
    private readonly UpdateChannel _channel;
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset _nextCheckUtc;
    private int _checking;

    public UpdateCheckScheduler(UpdateSettings state, UpdateChannel channel, Func<DateTimeOffset> clock)
        : this(() => state, channel, clock)
    {
        ArgumentNullException.ThrowIfNull(state);
    }

    /// <summary>The supplier follows live settings replacements without resetting the schedule.</summary>
    public UpdateCheckScheduler(Func<UpdateSettings> settings, UpdateChannel channel, Func<DateTimeOffset> clock)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        _settings = settings;
        _channel = channel;
        _clock = clock;
        _nextCheckUtc = clock().ToUniversalTime() + StartupDelay;
    }

    /// <summary>Returns whether a check was attempted, including a failed attempt logged here.</summary>
    public async Task<bool> TryCheckAsync(Func<bool> isDeferred, Func<Task> check, Action persist, Action<Exception> log)
    {
        ArgumentNullException.ThrowIfNull(isDeferred);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(persist);
        ArgumentNullException.ThrowIfNull(log);
        if (Interlocked.Exchange(ref _checking, 1) != 0) return false;

        try
        {
            UpdateSettings state = _settings();
            DateTimeOffset now = _clock().ToUniversalTime();
            if (_channel == UpdateChannel.None || !state.AutoCheck || now < _nextCheckUtc
                || state.LastCheckUtc is { } last && now - last < MinimumCheckInterval
                || isDeferred()) return false;

            // Record attempts before network access so failures and another launch obey the same limit.
            _nextCheckUtc = now + RepeatInterval;
            state.LastCheckUtc = now;
            try
            {
                persist();
                await check();
            }
            catch (Exception ex)
            {
                log(ex);
            }

            return true;
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }
}
