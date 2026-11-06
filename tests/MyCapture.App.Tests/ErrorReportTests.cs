using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ErrorReportTests
{
    internal static readonly DateTimeOffset FixedTime = new(2026, 11, 6, 17, 25, 0, 123, TimeSpan.Zero);

    [Fact]
    public void MemoryRecordsAndSummaryKeepOnlySafeExceptionIdentifiers()
    {
        Exception error = CaptureFailure();
        SafeErrorRecord record = Record(error);
        Assert.Equal("System.InvalidOperationException", record.ExceptionType);
        Assert.Equal("System.UnauthorizedAccessException", record.BaseExceptionType);
        Assert.Equal(unchecked((int)0x80131509), record.HResult);
        Assert.Equal(unchecked((int)0x80070005), record.BaseHResult);
        Assert.EndsWith(".ThrowPrivate", record.Site, StringComparison.Ordinal);
        Assert.EndsWith(".ThrowPrivate", record.AppFrame, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{8}$", record.StackSignature);
        var report = new ErrorReport(ErrorReportKind.Unhandled, record, [record], [], null);
        string summary = report.FormatSummary();
        var submission = new SupportSubmission("MyCapture: 5.0", "", summary, report);
        // Record ToString prints public members only; inspect every internal diagnostic field as well.
        string allFields = string.Join(" ", typeof(SafeErrorRecord)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(property => property.GetValue(record)));
        foreach (string value in new[] { allFields, summary, submission.Body })
        {
            AssertPrivate(value);
            Assert.Contains(record.ExceptionType, value, StringComparison.Ordinal);
            Assert.Contains(record.Site, value, StringComparison.Ordinal);
            Assert.Contains(record.StackSignature, value, StringComparison.Ordinal);
        }
        Assert.Contains("0x80131509", summary, StringComparison.Ordinal);
        Assert.Contains("2026-11-06 17:25:00 UTC", summary, StringComparison.Ordinal);
        // A logged error names its log call on the operation line and on its recent-errors line.
        string operation = "MyCapture.App.Test · event 42 · msg " + record.Template;
        Assert.Equal(2, summary.Split(Environment.NewLine).Count(line => line.EndsWith(operation, StringComparison.Ordinal)));
    }

    [Fact]
    public void SignaturesAreStableForTheSamePathAndDistinguishAnotherPath()
    {
        string first = Record(CaptureFailure()).StackSignature;
        Assert.Equal(first, Record(CaptureFailure()).StackSignature);
        Assert.NotEqual(first, Record(CaptureFailure(alternate: true)).StackSignature);
        Assert.Equal("-", Record(new InvalidOperationException()).StackSignature);
    }

    [Fact]
    public void FramesWithoutMetadataKeepTheSignatureAndTheAppFrame()
    {
        // Emitted code (serializers, compiled expressions) has frames without a declaring type.
        Exception error = ThrowThroughEmittedCode();
        Assert.Contains(new StackTrace(error, fNeedFileInfo: false).GetFrames(), frame => frame.GetMethod()?.DeclaringType is null);
        SafeErrorRecord record = Record(error);
        Assert.Matches("^[0-9a-f]{8}$", record.StackSignature);
        Assert.Equal(typeof(ErrorReportTests).FullName + "." + nameof(ThrowThroughEmittedCode), record.AppFrame);
        Assert.Equal(record.StackSignature, Record(ThrowThroughEmittedCode()).StackSignature);
    }

    [Fact]
    public void RingKeepsNewestTwelveOldestFirstAndUsesInjectedClock()
    {
        var log = new RecentErrorLog(() => FixedTime);
        using var provider = new RecentErrorLogProvider(log);
        ILogger logger = provider.CreateLogger("MyCapture.App.Test");
        for (int i = 0; i < 20; i++)
            logger.Log(LogLevel.Error, new EventId(i), new[] { new KeyValuePair<string, object?>("{OriginalFormat}", "Save failed") },
                CaptureFailure(), (_, _) => throw new InvalidOperationException("Formatter must not run"));
        SafeErrorRecord[] records = log.Snapshot();
        Assert.Equal(Enumerable.Range(8, 12), records.Select(record => record.EventId));
        Assert.All(records, record =>
        {
            Assert.Equal(FixedTime, record.Utc);
            Assert.Equal("MyCapture.App.Test", record.Category);
            Assert.Equal(SafeLogLine.TemplateId("Save failed"), record.Template);
        });
    }

    [Fact]
    public void InformationAndWarningCallsAllocateNothingAfterWarmup()
    {
        var log = new RecentErrorLog();
        using var provider = new RecentErrorLogProvider(log);
        ILogger logger = provider.CreateLogger("MyCapture.App.Test");
        logger.LogInformation("Information"); logger.LogWarning("Warning");
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            logger.LogInformation("Information");
            logger.LogWarning("Warning");
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(0, allocated);
        Assert.Empty(log.Snapshot());
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.False(logger.IsEnabled(LogLevel.Warning));
    }

    [Fact]
    public void ExceptionReportExcludesMatchingRecentErrorAndKeepsFiveOthers()
    {
        Exception error = CaptureFailure();
        var errors = new RecentErrorLog(() => FixedTime);
        for (int i = 0; i < 8; i++) errors.Record(i, new ArgumentException());
        errors.Record(9, error);
        ErrorReport report = ErrorReport.ForException(error, errors, null, FixedTime.AddSeconds(1));
        Assert.Equal(Enumerable.Range(3, 5), report.RecentErrors.Select(record => record.EventId));
        Assert.Equal("MyCapture.App.App", report.Primary.Category);
        Assert.Empty(report.RecentLogLines);
        // An unhandled exception has no log call, so its summary has no operation line.
        Assert.DoesNotContain(UiText.Get("Support_ErrorOperation") + ":", report.FormatSummary(), StringComparison.Ordinal);
    }

    [Fact]
    public void SummaryRevalidatesRecordsAndPreservesLegacySubmissionBody()
    {
        SafeErrorRecord record = Record(CaptureFailure()) with
        {
            Category = RollingFileLogTests.CapturePath, Site = RollingFileLogTests.PrivateUrl,
            Template = RollingFileLogTests.ClipboardText, StackSignature = RollingFileLogTests.WindowTitle,
        };
        var report = new ErrorReport(ErrorReportKind.UnexpectedExit, record, [], [RollingFileLogTests.ClipboardText], RollingFileLogTests.CapturePath);
        AssertPrivate(report.FormatSummary());
        Assert.Equal(SupportReport.Body("diagnostics", " text "), new SupportSubmission("diagnostics", " text ", "", null).Body);
        Assert.Equal(SupportReport.Body("", ""), new SupportSubmission("", "", "", null).Body);
        Assert.Equal("edited", new SupportSubmission("", "", " edited ", report).Body);
    }

    [Fact]
    public void IssueUrlUsesErrorTitleAndReportsRuneSafeTrimming()
    {
        var report = new ErrorReport(ErrorReportKind.Unhandled, Record(CaptureFailure()), [], [], null);
        var normal = new SupportSubmission("diagnostics", "hello", "summary", report);
        string url = SupportReport.IssueUrl(normal, out bool trimmed);
        Assert.False(trimmed);
        Assert.Contains(Uri.EscapeDataString(UiText.Format("Support_ErrorIssueTitle", report.Primary.ExceptionType)), url, StringComparison.Ordinal);
        // The issue address carries the formatted summary but none of the exception's private text.
        string full = SupportReport.IssueUrl(new SupportSubmission("diagnostics", "", report.FormatSummary(), report), out _);
        Assert.Contains(Uri.EscapeDataString(report.Primary.StackSignature), full, StringComparison.Ordinal);
        // Check the title and body the address carries; the GitHub address itself is fixed.
        AssertPrivate(Uri.UnescapeDataString(full[(full.IndexOf('?', StringComparison.Ordinal) + 1)..]));
        var longSubmission = normal with { Message = string.Concat(Enumerable.Repeat("한글😀", 4000)) };
        string longUrl = SupportReport.IssueUrl(longSubmission, out trimmed);
        Assert.True(trimmed);
        Assert.InRange(longUrl.Length, 1, SupportReport.MaximumIssueUrlLength);
        string decoded = Uri.UnescapeDataString(longUrl[(longUrl.IndexOf("&body=", StringComparison.Ordinal) + 6)..]);
        Assert.DoesNotContain('\uFFFD', decoded);
        Assert.StartsWith(decoded, longSubmission.Body, StringComparison.Ordinal);
    }

    internal static SafeErrorRecord Record(Exception error) => SafeErrorRecord.From(FixedTime, LogLevel.Error, 42,
        "MyCapture.App.Test", SafeLogLine.TemplateId("Save failed"), error);

    internal static void AssertPrivate(string value)
    {
        foreach (string fragment in RollingFileLogTests.PersonalFragments)
            Assert.DoesNotContain(fragment, value, StringComparison.Ordinal);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static Exception CaptureFailure(bool alternate = false)
    {
        try
        {
            if (alternate) OtherPath(); else ThrowPrivate();
            throw new InvalidOperationException("Unreachable");
        }
        catch (InvalidOperationException error) { return error; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void OtherPath() => ThrowPrivate();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception ThrowThroughEmittedCode()
    {
        var method = new DynamicMethod("EmittedThrow", typeof(void), Type.EmptyTypes, typeof(ErrorReportTests).Module);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Throw);
        var emitted = (Action)method.CreateDelegate(typeof(Action));
        try
        {
            emitted();
            throw new InvalidOperationException("Unreachable");
        }
        catch (InvalidOperationException error) { return error; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPrivate()
    {
        var cause = new UnauthorizedAccessException(RollingFileLogTests.ClipboardText + RollingFileLogTests.PrivateUrl);
        var error = new InvalidOperationException(RollingFileLogTests.CapturePath + RollingFileLogTests.WindowTitle, cause);
        error.Data[RollingFileLogTests.PrivateUrl] = RollingFileLogTests.ClipboardText;
        throw error;
    }
}
