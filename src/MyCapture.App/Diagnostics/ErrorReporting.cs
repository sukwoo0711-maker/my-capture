using System.Windows;
using System.Windows.Threading;

namespace MyCapture.App.Diagnostics;

/// <summary>Local report construction and preview. Sending belongs to an explicit support-window action.</summary>
internal static class ErrorReporting
{
    internal static RollingFileLog? FileLog { get; set; }
    internal static UnexpectedExitLog? Exits { get; set; }

    /// <summary>The recent errors a report lists. Tests use a log of their own.</summary>
    internal static RecentErrorLog Errors { get; set; } = RecentErrorLog.Shared;

    /// <summary>The time a report gives its failure. Tests use a fixed clock.</summary>
    internal static Func<DateTimeOffset> Clock { get; set; } = static () => DateTimeOffset.UtcNow;

    internal static Action<ErrorReport> Open { get; set; } = report =>
    {
        Dispatcher dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        dispatcher.BeginInvoke(new Action(() => SupportWindow.ShowErrorReport(report)));
    };

    /// <summary>
    /// Takes what a report about <paramref name="error"/> needs at the moment it is shown: the time, the
    /// recent errors and the latest log lines, which still include the error's own line. The report is
    /// built from them only on request, so a later click neither moves the time nor loses that line.
    /// </summary>
    internal static Func<ErrorReport> Capture(Exception error)
    {
        DateTimeOffset utc = Clock();
        SafeErrorRecord[] errors = Errors.Snapshot();
        string[] lines = FileLog?.RecentLines(ErrorReport.RecentLogLineCount) ?? [];
        return () => ErrorReport.ForException(error, errors, lines, utc);
    }
}
