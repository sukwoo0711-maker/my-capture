using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class UnexpectedExitNoticeTests
{
    [Theory]
    [InlineData("ko-KR", true)]
    [InlineData("en-US", true)]
    [InlineData("ko-KR", false)]
    [InlineData("en-US", false)]
    public void NoticeOffersLocalReportAndDefaultClose(string language, bool report) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        int calls = 0;
        ThemedDialog dialog = ThemedDialog.CreateUnexpectedExitNotice(() => calls++);
        try
        {
            Assert.Equal(0, calls);
            Show(dialog);
            Assert.Equal(0, calls);
            Assert.Null(dialog.Owner);
            Button[] buttons = ActionButtons(dialog);
            Assert.Equal([UiText.Get("Support_ReportError"), UiText.Get("Dialog.Close")],
                buttons.Select(SurfaceShortcutHintTests.PrimaryCaption));
            Assert.False(buttons[0].IsDefault || buttons[0].IsCancel);
            Assert.True(buttons[1].IsDefault && buttons[1].IsCancel);
            var panel = (StackPanel)((ScrollViewer)dialog.Content).Content;
            Assert.Equal(UiText.Get("Support_ExitNoticeTitle"),
                Strip(panel.Children.OfType<TextBlock>().First().Text));
            Assert.Contains(UiText.Get("Support_ExitNoticeBody"),
                panel.Children.OfType<TextBlock>().Select(block => Strip(block.Text)));

            Button chosen = buttons[report ? 0 : 1];
            chosen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(dialog.IsVisible);
            Assert.Equal(report ? 1 : 0, calls);
            if (report)
            {
                // A queued second click must not open a second preview after the dialog has closed.
                chosen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, calls);
            }
        }
        finally { dialog.Close(); }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void UnhandledReportIsBuiltOnlyOnClickAndNeverOpensUri(string language) => StaTestHost.Run(() =>
    {
        using var scope = UiText.UseLanguage(language);
        Action<ErrorReport> previousOpen = ErrorReporting.Open;
        Action<string> previousUri = SupportDiagnostics.OpenUri;
        var reports = new List<ErrorReport>();
        var uris = new List<string>();
        Exception error = ThrownError();
        ThemedDialog? dialog = null;
        try
        {
            ErrorReporting.Open = reports.Add;
            SupportDiagnostics.OpenUri = uris.Add;
            dialog = ThemedDialog.CreateUnhandled(error);
            Show(dialog);
            Assert.Empty(reports);
            Assert.Empty(uris);
            Button[] buttons = ActionButtons(dialog);
            Assert.Equal([UiText.Get("Support_ReportError"), UiText.Get("Dialog.Close")],
                buttons.Select(SurfaceShortcutHintTests.PrimaryCaption));
            Assert.True(buttons[1].IsDefault && buttons[1].IsCancel);
            buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ErrorReport result = Assert.Single(reports);
            Assert.Equal(ErrorReportKind.Unhandled, result.Kind);
            Assert.Equal(typeof(InvalidOperationException).FullName, result.Primary.ExceptionType);
            Assert.Equal(error.HResult, result.Primary.HResult);
            Assert.Matches("^[0-9a-f]{8}$", result.Primary.StackSignature);
            Assert.False(dialog.IsVisible);
            Assert.Empty(uris);
        }
        finally
        {
            dialog?.Close();
            ErrorReporting.Open = previousOpen;
            SupportDiagnostics.OpenUri = previousUri;
        }
    });

    [Fact]
    public void UnhandledReportKeepsTheMomentItWasShownWhenTheClickComesLater() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-error-report-late-click-");
        RecentErrorLog previousErrors = ErrorReporting.Errors;
        RollingFileLog? previousLog = ErrorReporting.FileLog;
        Func<DateTimeOffset> previousClock = ErrorReporting.Clock;
        Action<ErrorReport> previousOpen = ErrorReporting.Open;
        DateTimeOffset now = ErrorReportTests.FixedTime.AddMinutes(-1);
        var reports = new List<ErrorReport>();
        var file = new RollingFileLog(Path.Combine(root, "logs"), clock: () => now);
        ThemedDialog? dialog = null;
        try
        {
            var errors = new RecentErrorLog(() => now);
            ErrorReporting.Errors = errors;
            ErrorReporting.FileLog = file;
            ErrorReporting.Clock = () => now;
            ErrorReporting.Open = reports.Add;
            using var recent = new RecentErrorLogProvider(errors);
            using var written = new RollingFileLogProvider(file, () => now);
            void LogBoth(string category, Action<ILogger> write)
            {
                write(recent.CreateLogger(category));
                write(written.CreateLogger(category));
            }

            // An earlier, different failure stays a recent error of the report.
            LogBoth("MyCapture.App.Test", logger => logger.LogError(ErrorReportTests.CaptureFailure(alternate: true), "Save failed"));
            // The dispatcher handler logs the exception, then shows the dialog in the same moment.
            now = ErrorReportTests.FixedTime;
            Exception error = ErrorReportTests.CaptureFailure();
            LogBoth("MyCapture.App.App", logger => logger.LogError(error, "Unhandled exception on the dispatcher thread"));
            string[] atFailure = file.RecentLines(ErrorReport.RecentLogLineCount);
            dialog = ThemedDialog.CreateUnhandled(error);
            Show(dialog);

            // The person reads it for a while; meanwhile the app logs more lines than a report carries.
            now = ErrorReportTests.FixedTime.AddSeconds(3);
            for (int step = 0; step < ErrorReport.RecentLogLineCount + 2; step++)
                LogBoth("MyCapture.App.Test", logger => logger.LogInformation("Background step {Step}", step));
            Assert.DoesNotContain(atFailure[^1], file.RecentLines(ErrorReport.RecentLogLineCount));
            ActionButtons(dialog)[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            ErrorReport report = Assert.Single(reports);
            Assert.Equal(ErrorReportTests.FixedTime, report.Primary.Utc);
            Assert.Equal(atFailure, report.RecentLogLines);
            Assert.Contains(" ERR MyCapture.App.App ", report.RecentLogLines[^1], StringComparison.Ordinal);
            // The handler's own record of this exception is the failure itself, so it is listed once.
            SafeErrorRecord earlier = Assert.Single(report.RecentErrors);
            Assert.NotEqual(report.Primary.StackSignature, earlier.StackSignature);
            string summary = report.FormatSummary();
            Assert.Contains("2026-11-06 17:25:00 UTC", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("17:25:03", summary, StringComparison.Ordinal);
            Assert.Single(summary.Split(Environment.NewLine), line =>
                line.Contains(SafeErrorRecord.HashToken(report.Primary.StackSignature), StringComparison.Ordinal));
        }
        finally
        {
            dialog?.Close();
            ErrorReporting.Errors = previousErrors;
            ErrorReporting.FileLog = previousLog;
            ErrorReporting.Clock = previousClock;
            ErrorReporting.Open = previousOpen;
            file.Dispose();
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void BusyWaitWakesWhenAWindowClosesOrHidesAndOtherwiseAtItsLimit() => StaTestHost.Run(() =>
    {
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Window closing = OffscreenWindow(), hiding = OffscreenWindow();
        try
        {
            Task closed = UnexpectedExitNotice.WindowChangeAsync([closing, hiding], TimeSpan.FromMinutes(10));
            Ocr410Fixture.Pump();
            Assert.False(closed.IsCompleted);
            closing.Close();
            Complete(closed);

            Task hidden = UnexpectedExitNotice.WindowChangeAsync([hiding], TimeSpan.FromMinutes(10));
            Ocr410Fixture.Pump();
            Assert.False(hidden.IsCompleted);
            hiding.Hide();
            Complete(hidden);

            // A busy state no window ends, such as a capture still starting, is read again at the limit.
            Complete(UnexpectedExitNotice.WindowChangeAsync([hiding], TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            closing.Close();
            hiding.Close();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    });

    [Fact]
    public void ABusyLaunchReadsItsConditionsAgainOnlyWhenAWindowCloses() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-error-report-notice-wait-");
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Window question = OffscreenWindow();
        UnexpectedExitNotice? notice = null;
        try
        {
            UnexpectedExitLog exits = Log(root);
            Assert.True(exits.TryWrite(ErrorReportTests.Record(ErrorReportTests.CaptureFailure())));
            var shown = new List<ThemedDialog>();
            int busyChecks = 0;
            Task<UnexpectedExitNotice?> offer = UnexpectedExitNotice.OfferAsync(exits, () => Task.CompletedTask,
                stop: () => false, busy: () => { busyChecks++; return question.IsVisible; },
                show: dialog => { shown.Add(dialog); Show(dialog); },
                waitForChange: () => UnexpectedExitNotice.WindowChangeAsync([question], TimeSpan.FromMinutes(10)));
            PumpUntil(() => busyChecks > 0);
            // While the first-run question stays open, nothing reads the conditions again.
            PumpFor(TimeSpan.FromMilliseconds(300));
            Assert.Equal(1, busyChecks);
            Assert.Empty(shown);

            question.Close();
            notice = Assert.IsType<UnexpectedExitNotice>(Complete(offer));
            Assert.Equal(2, busyChecks);
            Assert.Same(notice.Dialog, Assert.Single(shown));
            notice.Dialog.Close();
            Complete(notice.Handled);
            Assert.Empty(exits.ReadPending());
        }
        finally
        {
            notice?.Dialog.Close();
            question.Close();
            SynchronizationContext.SetSynchronizationContext(previousContext);
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void WindowCloseDoesNotOpenAReport() => StaTestHost.Run(() =>
    {
        int calls = 0;
        ThemedDialog dialog = ThemedDialog.CreateUnexpectedExitNotice(() => calls++);
        Show(dialog);
        dialog.Close();
        Assert.Equal(0, calls);
        Assert.False(dialog.IsVisible);
    });

    [Fact]
    public void PendingRecordsShowOneNoticeWhoseReportUsesTheNewestAndClosingMarksThemHandled() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-error-report-notice-");
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Action<ErrorReport> previousOpen = ErrorReporting.Open;
        var reports = new List<ErrorReport>();
        try
        {
            ErrorReporting.Open = reports.Add;
            UnexpectedExitLog exits = Log(root);
            SafeErrorRecord older = ErrorReportTests.Record(ErrorReportTests.CaptureFailure());
            SafeErrorRecord newer = ErrorReportTests.Record(ErrorReportTests.CaptureFailure(alternate: true)) with
            { Utc = older.Utc.AddMinutes(1) };
            Assert.True(exits.TryWrite(older, version: "5.0.1.0"));
            Assert.True(exits.TryWrite(newer, version: "5.1.0.0"));

            var shown = new List<ThemedDialog>();
            int readyCalls = 0, busyChecks = 0;
            UnexpectedExitNotice notice = Assert.IsType<UnexpectedExitNotice>(Complete(UnexpectedExitNotice.OfferAsync(exits,
                () => { readyCalls++; return Task.CompletedTask; }, stop: () => false,
                busy: () => ++busyChecks < 3, show: dialog => { shown.Add(dialog); Show(dialog); },
                waitForChange: () => Task.CompletedTask)));
            Assert.Equal(1, readyCalls);
            Assert.Equal(3, busyChecks);
            Assert.Same(notice.Dialog, Assert.Single(shown));
            Assert.Empty(reports);
            Assert.Equal(2, exits.ReadPending().Count);

            ActionButtons(notice.Dialog)[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ErrorReport report = Assert.Single(reports);
            Assert.Equal(ErrorReportKind.UnexpectedExit, report.Kind);
            Assert.Equal(newer, report.Primary);
            Assert.Equal([older], report.RecentErrors);
            Assert.Equal("5.1.0.0", report.RecordedVersion);
            Assert.False(notice.Dialog.IsVisible);
            Complete(notice.Handled);
            Assert.Empty(exits.ReadPending());
            Assert.All(exits.SafeEntries(), entry => Assert.EndsWith(".seen", entry.Name, StringComparison.Ordinal));
            Assert.Same(notice.Handled, notice.CloseAsync());

            // Handled records are never offered again.
            Assert.Null(Complete(UnexpectedExitNotice.OfferAsync(exits, () => Task.CompletedTask, () => false, () => false,
                _ => throw new InvalidOperationException("A handled record must not show a notice"))));
        }
        finally
        {
            ErrorReporting.Open = previousOpen;
            SynchronizationContext.SetSynchronizationContext(previousContext);
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void NoRecordShowsNothingAndStopKeepsRecordsPendingForTheNextLaunch() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-error-report-notice-stop-");
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            UnexpectedExitLog exits = Log(root);
            Assert.Null(Complete(UnexpectedExitNotice.OfferAsync(exits,
                () => throw new InvalidOperationException("Nothing waits without a record"), () => false, () => false,
                _ => throw new InvalidOperationException("No record, no notice"))));
            Assert.False(Directory.Exists(exits.Folder));

            Assert.True(exits.TryWrite(ErrorReportTests.Record(ErrorReportTests.CaptureFailure())));
            bool stopping = false;
            Assert.Null(Complete(UnexpectedExitNotice.OfferAsync(exits, () => Task.CompletedTask,
                stop: () => stopping, busy: () => { stopping = true; return true; },
                show: _ => throw new InvalidOperationException("An exiting app shows no notice"),
                waitForChange: () => Task.CompletedTask)));
            Assert.Single(exits.ReadPending());
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            OwnedTestDirectory.Delete(root);
        }
    });

    private static T Complete<T>(Task<T> task)
    {
        Complete((Task)task);
        return task.GetAwaiter().GetResult();
    }

    private static void Complete(Task task)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), "The notice flow did not complete.");
            Ocr410Fixture.Pump();
        }
        task.GetAwaiter().GetResult();
    }

    private static UnexpectedExitLog Log(string root) => new(Path.Combine(root, "unexpected-exits"), path =>
    {
        Directory.CreateDirectory(Path.Combine(root, "trash"));
        File.Move(path, Path.Combine(root, "trash", Path.GetFileName(path)));
    });

    private static Exception ThrownError()
    {
        try { throw new InvalidOperationException("private exception message"); }
        catch (InvalidOperationException error) { return error; }
    }

    private static string Strip(string text) => KoreanWordWrap.Strip(text);

    private static Button[] ActionButtons(ThemedDialog dialog) =>
        ((StackPanel)((ScrollViewer)dialog.Content).Content).Children.OfType<WrapPanel>().Single()
            .Children.OfType<Button>().ToArray();

    private static void PumpUntil(Func<bool> condition)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), "The notice flow did not get there.");
            Ocr410Fixture.Pump();
        }
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(duration, DispatcherPriority.Background, (_, _) => frame.Continue = false,
            Dispatcher.CurrentDispatcher);
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static Window OffscreenWindow()
    {
        var window = new Window { Width = 160, Height = 120 };
        Show(window);
        return window;
    }

    private static void Show(Window dialog)
    {
        Ocr410Fixture.LoadTheme(dialog);
        FluidMotion.SetWindowEntrance(dialog, false);
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -10000;
        dialog.Top = -10000;
        dialog.ShowActivated = false;
        dialog.ShowInTaskbar = false;
        dialog.Show();
        dialog.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(dialog.UpdateLayout));
    }
}
