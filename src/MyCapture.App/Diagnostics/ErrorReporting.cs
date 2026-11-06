using System.Windows;
using System.Windows.Threading;

namespace MyCapture.App.Diagnostics;

/// <summary>Local report construction and preview. Sending belongs to an explicit support-window action.</summary>
internal static class ErrorReporting
{
    internal static RollingFileLog? FileLog { get; set; }
    internal static UnexpectedExitLog? Exits { get; set; }

    internal static Action<ErrorReport> Open { get; set; } = report =>
    {
        Dispatcher dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        dispatcher.BeginInvoke(new Action(() => SupportWindow.ShowErrorReport(report)));
    };

    internal static ErrorReport ForException(Exception error) =>
        ErrorReport.ForException(error, RecentErrorLog.Shared, FileLog, DateTimeOffset.UtcNow);
}
