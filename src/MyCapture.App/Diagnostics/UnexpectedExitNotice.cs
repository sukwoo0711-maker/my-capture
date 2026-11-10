using System.Windows;
using System.Windows.Threading;
using MyCapture.App.Themes;

namespace MyCapture.App.Diagnostics;

/// <summary>
/// The next-launch notice for unexpected-exit records. Records are read off the UI thread and the notice
/// is shown once. Closing it marks the shown records handled (also off the UI thread), unless it was only
/// withdrawn by a step the app may survive; then the records stay pending for the next launch.
/// </summary>
internal sealed class UnexpectedExitNotice
{
    /// <summary>
    /// The longest a busy wait sleeps before reading the conditions again. A window that closes or hides
    /// wakes it sooner; this covers a state no window ends, such as a capture that is still starting.
    /// </summary>
    internal static readonly TimeSpan BusyRecheck = TimeSpan.FromSeconds(5);

    private readonly UnexpectedExitLog _exits;
    private readonly IReadOnlyList<UnexpectedExitRecord> _records;
    private bool _closed;
    private bool _withdrawn;

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
            if (!_withdrawn) Handled = Task.Run(() => _exits.MarkHandled(_records));
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
    /// A busy wait sleeps until <paramref name="waitForChange"/> completes: by default until a window
    /// closes or hides, or <see cref="BusyRecheck"/> passes.
    /// </summary>
    internal static async Task<UnexpectedExitNotice?> OfferAsync(UnexpectedExitLog exits, Func<Task> ready,
        Func<bool> stop, Func<bool> busy, Action<ThemedDialog> show, Func<Task>? waitForChange = null)
    {
        IReadOnlyList<UnexpectedExitRecord> records = await Task.Run(exits.ReadPending);
        if (records.Count == 0) return null;
        await ready();
        waitForChange ??= () => WindowChangeAsync(
            Application.Current?.Windows.OfType<Window>().ToArray() ?? [], BusyRecheck);
        while (true)
        {
            if (stop()) return null;
            if (!busy()) break;
            await waitForChange();
        }
        var notice = new UnexpectedExitNotice(exits, records);
        show(notice.Dialog);
        return notice;
    }

    /// <summary>
    /// Completes when one of <paramref name="windows"/> closes or hides, or after <paramref name="limit"/>.
    /// Call it on the windows' thread; nothing runs there while it waits.
    /// </summary>
    internal static Task WindowChangeAsync(IReadOnlyList<Window> windows, TimeSpan limit)
    {
        // A thread without a dispatcher has no windows to watch, and no timer to run one there.
        if (Dispatcher.FromThread(Thread.CurrentThread) is not { } dispatcher) return Task.Delay(limit);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherTimer? timer = null;
        foreach (Window window in windows)
        {
            window.Closed += OnClosed;
            window.IsVisibleChanged += OnVisibilityChanged;
        }
        // This constructor starts the timer.
        timer = new DispatcherTimer(limit, DispatcherPriority.Background, (_, _) => Finish(), dispatcher);
        return changed.Task;

        void OnClosed(object? sender, EventArgs e) => Finish();
        void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is false) Finish();
        }
        void Finish()
        {
            timer?.Stop();
            foreach (Window window in windows)
            {
                window.Closed -= OnClosed;
                window.IsVisibleChanged -= OnVisibilityChanged;
            }
            changed.TrySetResult();
        }
    }

    /// <summary>Closes a notice still on screen for a confirmed exit and returns the pending acknowledgement.</summary>
    internal Task CloseAsync()
    {
        if (!_closed) Dialog.Close();
        return Handled;
    }

    /// <summary>
    /// Takes the notice off screen for a step the app may survive (an import, a Store update). The
    /// person has not answered it, so its records stay pending for the next launch.
    /// </summary>
    internal void Withdraw()
    {
        if (_closed) return;
        _withdrawn = true;
        Dialog.Close();
    }
}
