using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
                delay: _ => Task.CompletedTask)));
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
                delay: _ => Task.CompletedTask)));
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

    private static void Show(ThemedDialog dialog)
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
