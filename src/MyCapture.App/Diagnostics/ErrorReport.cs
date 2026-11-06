using System.Globalization;
using Microsoft.Extensions.Logging;

namespace MyCapture.App.Diagnostics;

internal enum ErrorReportKind { Unhandled, UnexpectedExit }

internal sealed record ErrorReport(ErrorReportKind Kind, SafeErrorRecord Primary,
    IReadOnlyList<SafeErrorRecord> RecentErrors, IReadOnlyList<string> RecentLogLines, string? RecordedVersion)
{
    internal static ErrorReport ForException(Exception error, RecentErrorLog errors, RollingFileLog? log, DateTimeOffset utc)
    {
        SafeErrorRecord primary = SafeErrorRecord.From(utc, LogLevel.Error, 0, "MyCapture.App.App", null, error);
        SafeErrorRecord[] recent = errors.Snapshot().Where(item =>
            item.ExceptionType != primary.ExceptionType || item.StackSignature != primary.StackSignature
            || Math.Abs((item.Utc - primary.Utc).TotalSeconds) > 2).TakeLast(5).ToArray();
        return new(ErrorReportKind.Unhandled, primary, recent, log?.RecentLines(8) ?? [], null);
    }

    internal string FormatSummary()
    {
        var lines = new List<string>();
        Add("Support_ErrorKind", UiText.Get(Kind == ErrorReportKind.UnexpectedExit
            ? "Support_ErrorKindUnexpectedExit" : "Support_ErrorKindUnhandled"));
        Add("Support_ErrorTime", Time(Primary.Utc));
        if (Kind == ErrorReportKind.UnexpectedExit) Add("Support_RecordedVersion", SafeLogLine.Token(RecordedVersion));
        string operation = Operation(Primary);
        if (operation != SafeLogLine.Unknown) Add("Support_ErrorOperation", operation);
        Add("Support_ErrorException", SafeLogLine.Token(Primary.ExceptionType) + " (" + HResult(Primary.HResult) + ")");
        if (SafeLogLine.Token(Primary.BaseExceptionType) is { } cause && cause != SafeLogLine.Unknown)
            Add("Support_ErrorCause", cause + " (" + HResult(Primary.BaseHResult) + ")");
        string site = SafeLogLine.Token(Primary.Site);
        string frame = SafeLogLine.Token(Primary.AppFrame);
        Add("Support_ErrorLocation", site + (frame != SafeLogLine.Unknown && frame != site ? " · " + frame : string.Empty));
        Add("Support_StackSignature", SafeErrorRecord.HashToken(Primary.StackSignature));
        if (RecentErrors.Count > 0)
        {
            lines.Add(UiText.Get("Support_RecentErrors") + ":");
            foreach (SafeErrorRecord item in RecentErrors.TakeLast(5))
            {
                string recent = Operation(item);
                lines.Add(Time(item.Utc) + " " + SafeLogLine.Token(item.ExceptionType) + " "
                    + HResult(item.HResult) + " " + SafeErrorRecord.HashToken(item.StackSignature)
                    + (recent == SafeLogLine.Unknown ? string.Empty : " · " + recent));
            }
        }
        string[] logs = RecentLogLines.Where(SafeLogLine.IsWellFormed).TakeLast(8).ToArray();
        if (logs.Length > 0)
        {
            lines.Add(UiText.Get("Support_RecentLog") + ":");
            lines.AddRange(logs);
        }
        return string.Join(Environment.NewLine, lines);

        void Add(string label, string value) => lines.Add(UiText.Get(label) + ": " + value);
    }

    /// <summary>
    /// The log call that reported a recorded error: category, event ID and message template ID. An
    /// unhandled exception or an exit has no log call, so it gives <see cref="SafeLogLine.Unknown"/>.
    /// </summary>
    private static string Operation(SafeErrorRecord record)
    {
        string template = SafeErrorRecord.HashToken(record.Template);
        return record.EventId == 0 && template == SafeLogLine.Unknown ? SafeLogLine.Unknown
            : SafeLogLine.Token(record.Category) + " · event " + record.EventId.ToString(CultureInfo.InvariantCulture)
                + " · msg " + template;
    }

    internal static string HResult(int? value) => value is { } code
        ? "0x" + code.ToString("X8", CultureInfo.InvariantCulture) : SafeLogLine.Unknown;

    private static string Time(DateTimeOffset utc) => utc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
}

internal sealed record SupportSubmission(string Diagnostics, string Message, string ErrorSummary, ErrorReport? Report)
{
    // Preserve the established support body exactly, including its trailing blank line.
    internal string Body => string.IsNullOrWhiteSpace(ErrorSummary) ? SupportReport.Body(Diagnostics, Message)
        : string.Join(Environment.NewLine + Environment.NewLine,
            new[] { Diagnostics, Message.Trim(), ErrorSummary.Trim() }.Where(part => !string.IsNullOrWhiteSpace(part)));
}
