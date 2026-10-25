using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MyCapture.App.Diagnostics;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RollingFileLogTests
{
    // What a capture tool can see: a capture path, a window title, clipboard text, a URL and a user name.
    internal const string CapturePath = @"C:\Users\kimminji\Pictures\Captures\bank-statement.png";
    internal const string WindowTitle = "KB국민은행 인터넷뱅킹 — 계좌 조회";
    internal const string ClipboardText = "SECRET_CLIPBOARD 비밀번호 1234";
    internal const string PrivateUrl = "https://private.example/token=abc";

    internal static readonly string[] PersonalFragments =
        ["kimminji", "bank-statement", @"C:\", "C:/", "Users", "국민은행", "계좌", "SECRET", "비밀번호", "https://", "private.example", "token="];

    private static readonly DateTimeOffset FixedTime = new(2026, 10, 23, 4, 30, 15, 123, TimeSpan.Zero);

    [Fact]
    public void FileLineKeepsCodesAndIdentifiersButNeverMessageValuesOrExceptionText()
    {
        string root = OwnedTestDirectory.Create("mc-file-log-filter-");
        try
        {
            using var log = new RollingFileLog(Path.Combine(root, "logs"), clock: () => FixedTime);
            using var provider = new RollingFileLogProvider(log, () => FixedTime);
            ILogger settings = provider.CreateLogger("MyCapture.App.Settings.SettingsWindow");
            ILogger unsafeCategory = provider.CreateLogger(CapturePath);

            settings.LogInformation("Saved capture to {Path} from {Title}", CapturePath, WindowTitle);
            settings.LogDebug("Clipboard held {Text}", ClipboardText);
            settings.LogError(ThrowPrivateFailure(), "Could not save {Path}", CapturePath);
            unsafeCategory.Log(LogLevel.Warning, new EventId(7, WindowTitle), ClipboardText,
                new IOException(PrivateUrl), (_, _) => throw new InvalidOperationException("The formatter must never run."));
            log.Dispose(); // Writes every queued line and closes the file for reading.

            string[] lines = File.ReadAllLines(log.CurrentPath);
            Assert.Equal(3, lines.Length); // Debug stays out of the file.
            Assert.All(lines, line => Assert.True(SafeLogLine.IsWellFormed(line), line));
            string text = string.Join("\n", lines);
            foreach (string fragment in PersonalFragments) Assert.DoesNotContain(fragment, text, StringComparison.Ordinal);

            Assert.Equal("2026-10-23T04:30:15.123Z INF MyCapture.App.Settings.SettingsWindow event=0 msg="
                + SafeLogLine.TemplateId("Saved capture to {Path} from {Title}"), lines[0]);
            Assert.StartsWith("2026-10-23T04:30:15.123Z ERR MyCapture.App.Settings.SettingsWindow event=0 msg="
                + SafeLogLine.TemplateId("Could not save {Path}") + " ex=System.InvalidOperationException hresult=0x80131509"
                + " site=MyCapture.App.Tests.RollingFileLogTests.ThrowPrivateFailure"
                + " base=System.UnauthorizedAccessException base-hresult=0x80070005", lines[1], StringComparison.Ordinal);
            // A category that is not a type name and a state without a template keep only their codes.
            Assert.Equal("2026-10-23T04:30:15.123Z WRN - event=7 msg=- ex=System.IO.IOException hresult=0x80131620 site=-", lines[2]);
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    }

    [Fact]
    public void FilesRotateAtTheSizeCapAndOnlyTheNewestFilesRemain()
    {
        string root = OwnedTestDirectory.Create("mc-file-log-rotate-");
        try
        {
            using var log = new RollingFileLog(root, maximumFileBytes: 1024, maximumFiles: 3, clock: () => FixedTime);
            for (int index = 0; index < 200; index++)
                log.Append(SafeLogLine.Format(FixedTime, LogLevel.Information, "MyCapture.App.App", index, "Tick", null));
            log.Dispose();

            Assert.Equal(["mycapture.log", "mycapture.1.log", "mycapture.2.log"],
                Directory.GetFiles(root).Select(file => Path.GetFileName(file)).OrderBy(name => name.Length).ThenBy(name => name, StringComparer.Ordinal));
            Assert.All(log.ExistingFiles(), file => Assert.InRange(new FileInfo(file).Length, 1, 1024));

            // Oldest file first, and within the kept window no line is lost or reordered.
            int[] events = log.ExistingFiles().SelectMany(File.ReadAllLines)
                .Select(line => int.Parse(line.Split(' ')[3]["event=".Length..], System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            Assert.Equal(199, events[^1]);
            Assert.Equal(Enumerable.Range(events[0], events.Length), events);
            Assert.True(events.Length < 200, "Older files beyond the cap were deleted.");
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    }

    [Fact]
    public void UnwritableFolderDropsLinesWithoutThrowingAndReportsTheCountLater()
    {
        string root = OwnedTestDirectory.Create("mc-file-log-blocked-");
        try
        {
            DateTimeOffset now = FixedTime;
            string folder = Path.Combine(root, "logs");
            File.WriteAllText(folder, "a file where the folder should be");
            var log = new RollingFileLog(folder, clock: () => now);
            for (int index = 0; index < 5; index++)
                log.Append(SafeLogLine.Format(now, LogLevel.Error, "MyCapture.App.App", index, "Blocked", null));
            Assert.True(log.Flush(TimeSpan.FromSeconds(5)));
            Assert.True(File.Exists(folder));

            global::MyCapture.Core.Storage.RecoverableFileRemoval.DeleteFile(folder);
            now += TimeSpan.FromSeconds(31);
            log.Append(SafeLogLine.Format(now, LogLevel.Information, "MyCapture.App.App", 9, "Resumed", null));
            log.Dispose();
            log.Append("ignored after dispose");

            string[] lines = File.ReadAllLines(log.CurrentPath);
            Assert.Equal("2026-10-23T04:30:46.123Z WRN log dropped=5", lines[0]);
            Assert.Contains("event=9", lines[1], StringComparison.Ordinal);
            Assert.Equal(2, lines.Length);
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    }

    [Fact]
    public void SessionLineNamesTheBuildAndWindowsVersionOnly()
    {
        string line = SafeLogLine.Session(FixedTime);
        Assert.True(SafeLogLine.IsWellFormed(line), line);
        Assert.StartsWith("2026-10-23T04:30:15.123Z INF session version=4.", line, StringComparison.Ordinal);
        Assert.Contains(" windows=10.0.", line, StringComparison.Ordinal);
        Assert.Equal("2026-10-23T04:30:15.123Z INF session version=- windows=- packaged=true",
            SafeLogLine.Session(FixedTime, CapturePath, WindowTitle, packaged: true));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static InvalidOperationException ThrowPrivateFailure()
    {
        try
        {
            throw new InvalidOperationException("Could not write " + CapturePath + " for " + WindowTitle,
                new UnauthorizedAccessException(ClipboardText + " " + PrivateUrl));
        }
        catch (InvalidOperationException error)
        {
            return error;
        }
    }
}
