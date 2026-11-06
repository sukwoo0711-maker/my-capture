using MyCapture.App.Themes;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// The next-launch notice for unexpected-exit records. Records are read off the UI thread, the notice is
/// shown once, and whatever closes it marks the shown records handled (also off the UI thread).
/// </summary>
internal sealed class UnexpectedExitNotice
{
    private static readonly TimeSpan BusyRetry = TimeSpan.FromMilliseconds(100);
    private readonly UnexpectedExitLog _exits;
    private readonly IReadOnlyList<UnexpectedExitRecord> _records;
    private bool _closed;

    private UnexpectedExitNotice(UnexpectedExitLog exits, IReadOnlyList<UnexpectedExitRecord> records)
    {
        _exits = exits;
        _records = records;
        UnexpectedExitRecord newest = records[0];
        Report = new ErrorReport(ErrorReportKind.UnexpectedExit, newest.Primary,
            records.Skip(1).Take(5).Select(record => record.Primary).ToArray(), newest.RecentLogLines, newest.Version);
        Dialog = ThemedDialog.CreateUnexpectedExitNotice(() => ErrorReporting.Open(Report));
        Dialog.Closed += (_, _) =>
        {
            _closed = true;
            Handled = Task.Run(() => _exits.MarkHandled(_records));
        };
    }

    internal ThemedDialog Dialog { get; }
    internal ErrorReport Report { get; }

    /// <summary>Completes once the shown records are marked handled after the notice closed.</summary>
    internal Task Handled { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Shows the notice when pending records exist. <paramref name="ready"/> runs only then; while
    /// <paramref name="busy"/> holds (a first-run question, a capture) the notice waits, and when
    /// <paramref name="stop"/> holds (exit, update) it is not shown and the records stay pending.
    /// </summary>
    internal static async Task<UnexpectedExitNotice?> OfferAsync(UnexpectedExitLog exits, Func<Task> ready,
        Func<bool> stop, Func<bool> busy, Action<ThemedDialog> show, Func<TimeSpan, Task>? delay = null)
    {
        IReadOnlyList<UnexpectedExitRecord> records = await Task.Run(exits.ReadPending);
        if (records.Count == 0) return null;
        await ready();
        delay ??= Task.Delay;
        while (true)
        {
            if (stop()) return null;
            if (!busy()) break;
            await delay(BusyRetry);
        }
        var notice = new UnexpectedExitNotice(exits, records);
        show(notice.Dialog);
        return notice;
    }

    /// <summary>Closes a notice still on screen (app exit) and returns the pending acknowledgement.</summary>
    internal Task CloseAsync()
    {
        if (!_closed) Dialog.Close();
        return Handled;
    }
}
