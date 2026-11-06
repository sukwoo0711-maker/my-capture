using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Extensions.Logging;
using MyCapture.App.Diagnostics;
using MyCapture.Core.Storage;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class UnexpectedExitLogTests
{
    [Fact]
    public void DiskRecordsAndBundleContainOnlyValidatedIdentifiers()
    {
        string root = OwnedTestDirectory.Create("mc-error-report-privacy-");
        try
        {
            var exits = Log(root);
            SafeErrorRecord primary = ErrorReportTests.Record(ErrorReportTests.CaptureFailure());
            Assert.True(exits.TryWrite(primary, ["unsafe " + RollingFileLogTests.ClipboardText], "5.0.0", "5.0.0+abc", false, "10.0.1"));
            UnexpectedExitRecord pending = Assert.Single(exits.ReadPending());
            Assert.Equal(primary, pending.Primary);
            Assert.Equal("5.0.0", pending.Version);
            Assert.False(pending.Packaged);
            Assert.Empty(pending.RecentLogLines);
            string stored = File.ReadAllText(Path.Combine(exits.Folder, pending.Name));
            ErrorReportTests.AssertPrivate(stored);
            Assert.Contains(primary.ExceptionType, stored, StringComparison.Ordinal);
            Assert.Contains("0x80131509", stored, StringComparison.Ordinal);
            Assert.Contains(primary.Site, stored, StringComparison.Ordinal);
            Assert.Contains(primary.StackSignature, stored, StringComparison.Ordinal);
            var report = new ErrorReport(ErrorReportKind.UnexpectedExit, primary, [], [], pending.Version);
            string bundle = Path.Combine(root, "report.zip");
            DiagnosticBundle.Write(bundle, new SupportSubmission("MyCapture: 5.0", "", report.FormatSummary(), report).Body, null, exits);
            using (ZipArchive zip = ZipFile.OpenRead(bundle))
            {
                Assert.Equal(2, zip.Entries.Count);
                Assert.Contains(zip.Entries, entry => entry.FullName == "unexpected-exits/" + pending.Name);
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    string text = reader.ReadToEnd();
                    ErrorReportTests.AssertPrivate(text);
                    Assert.Contains(primary.ExceptionType, text, StringComparison.Ordinal);
                    Assert.Contains(primary.StackSignature, text, StringComparison.Ordinal);
                }
            }
            exits.MarkHandled([pending]);
            Assert.Empty(exits.ReadPending());
            Assert.EndsWith(".seen", Assert.Single(exits.SafeEntries()).Name, StringComparison.Ordinal);
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public void DiskPruningMovesOldestMatchingFilesThroughInjectedRemoval()
    {
        string root = OwnedTestDirectory.Create("mc-error-report-prune-");
        try
        {
            var removed = new List<string>();
            var exits = Log(root, removed);
            string token = new('a', 180);
            string line = "2026-11-06T17:25:00.123Z ERR MyCapture.App.Test a=" + token + " b=" + token + " c=" + token + " d=" + token + " e=" + token;
            Assert.True(SafeLogLine.IsWellFormed(line));
            var names = new List<string>();
            for (int i = 0; i < 15; i++)
            {
                Assert.True(exits.TryWrite(ErrorReportTests.Record(ErrorReportTests.CaptureFailure()) with { Utc = ErrorReportTests.FixedTime.AddMilliseconds(i) },
                    Enumerable.Repeat(line, 8).ToArray()));
            }
            names.AddRange(Directory.GetFiles(exits.Folder).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal));
            File.WriteAllText(Path.Combine(exits.Folder, "unrelated.exit"), "leave this alone");
            IReadOnlyList<UnexpectedExitRecord> records = exits.ReadPending();
            Assert.InRange(records.Count, 1, 10);
            Assert.True(Directory.GetFiles(exits.Folder, "exit-*").Sum(path => new FileInfo(path).Length) <= 64 * 1024);
            Assert.Equal(names.Take(removed.Count), removed);
            Assert.True(File.Exists(Path.Combine(exits.Folder, "unrelated.exit")));
            Assert.Equal(records.OrderByDescending(record => record.Name, StringComparer.Ordinal), records);
            Assert.All(removed, name => Assert.True(File.Exists(Path.Combine(root, "trash", name))));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public void DiskReaderDropsTamperedValuesBadHeadersOversizeFilesAndForeignNames()
    {
        string root = OwnedTestDirectory.Create("mc-error-report-invalid-");
        try
        {
            var exits = Log(root);
            SafeErrorRecord primary = ErrorReportTests.Record(ErrorReportTests.CaptureFailure());
            Assert.True(exits.TryWrite(primary));
            string path = Assert.Single(Directory.GetFiles(exits.Folder));
            string text = File.ReadAllText(path).Replace("site=" + primary.Site, "site=" + RollingFileLogTests.CapturePath, StringComparison.Ordinal)
                .Replace("stack=" + primary.StackSignature, "stack=NOTHEX!!", StringComparison.Ordinal)
                .Replace("hresult=0x80131509", "hresult=0x8013150g", StringComparison.Ordinal);
            File.WriteAllText(path, text + "log=" + RollingFileLogTests.ClipboardText + "\nunknown=" + RollingFileLogTests.PrivateUrl + "\n");
            File.WriteAllText(Path.Combine(exits.Folder, "exit-20261106T172501123Z-1.exit"), "wrong header");
            File.WriteAllText(Path.Combine(exits.Folder, "exit-20261106T172502123Z-1.exit"), new string('x', 8193));
            File.WriteAllText(Path.Combine(exits.Folder, "exit-20269999T172502123Z-1.exit"), text);
            UnexpectedExitRecord record = Assert.Single(exits.ReadPending());
            Assert.Equal("-", record.Primary.Site);
            Assert.Equal("-", record.Primary.StackSignature);
            Assert.Null(record.Primary.HResult);
            Assert.Empty(record.RecentLogLines);
            ErrorReportTests.AssertPrivate(Assert.Single(exits.SafeEntries()).Text);
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public void DiskCrashLoopGuardDoesNotWriteAtTwentyMatchingFiles()
    {
        string root = OwnedTestDirectory.Create("mc-error-report-guard-");
        try
        {
            var exits = Log(root);
            SafeErrorRecord primary = ErrorReportTests.Record(ErrorReportTests.CaptureFailure());
            for (int i = 0; i < 20; i++) Assert.True(exits.TryWrite(primary with { Utc = primary.Utc.AddMilliseconds(i) }));
            Assert.False(exits.TryWrite(primary with { Utc = primary.Utc.AddSeconds(1) }));
            Assert.Equal(20, Directory.GetFiles(exits.Folder).Length);
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public void LoggingErrorsCreatesNoUnexpectedExitFolderOrFiles()
    {
        string root = OwnedTestDirectory.Create("mc-error-report-memory-");
        try
        {
            var exits = Log(root);
            using var provider = new RecentErrorLogProvider(new RecentErrorLog());
            ILogger logger = provider.CreateLogger("MyCapture.App.Test");
            for (int i = 0; i < 10; i++) logger.LogError(ErrorReportTests.CaptureFailure(), "Failed");
            Assert.False(Directory.Exists(exits.Folder));
            Assert.Empty(Directory.GetFileSystemEntries(root));
            Assert.Equal(Path.Combine(root, "unexpected-exits"), UnexpectedExitLog.FolderFor(AppPaths.CreateForRoot(root)));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public void DiskLogRecentRingReturnsNewestValidLinesOldestFirst()
    {
        string root = OwnedTestDirectory.Create("mc-error-report-lines-");
        try
        {
            using var log = new RollingFileLog(root);
            string[] lines = Enumerable.Range(0, 20).Select(i => SafeLogLine.Format(ErrorReportTests.FixedTime,
                LogLevel.Error, "MyCapture.App.Test", i, null, null)).ToArray();
            foreach (string line in lines) log.Append(line);
            log.Append(RollingFileLogTests.ClipboardText);
            Assert.Equal(lines.TakeLast(5), log.RecentLines(5));
            Assert.Equal(lines.Skip(5), log.RecentLines(100));
            Assert.Empty(log.RecentLines(0));
            Assert.Empty(log.RecentLines(-1));
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    [Fact]
    public void DiskLogAppendDoesNotWaitForTheIoGateAndFlushWritesAllLines()
    {
        string root = OwnedTestDirectory.Create("mc-error-report-nonblocking-");
        try
        {
            using var log = new RollingFileLog(root);
            // Hold the existing private IO gate to model a stalled disk without modifying production behavior.
            object gate = typeof(RollingFileLog).GetField("_ioGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(log)!;
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = new Thread(() => { lock (gate) { held.Set(); release.Wait(); } });
            holder.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
            string line = SafeLogLine.Format(ErrorReportTests.FixedTime, LogLevel.Error, "MyCapture.App.Test", 1, null, null);
            var elapsed = Stopwatch.StartNew();
            try
            {
                for (int i = 0; i < 1000; i++) log.Append(line);
                elapsed.Stop();
                Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(250), elapsed.Elapsed.ToString());
            }
            finally { release.Set(); holder.Join(); }
            Assert.True(log.Flush(TimeSpan.FromSeconds(5)));
            // Read beside the still-open writer so the count proves Flush itself wrote every line.
            using (var stream = new FileStream(log.CurrentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                Assert.Equal(1000, reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        }
        finally { OwnedTestDirectory.Delete(root); }
    }

    private static UnexpectedExitLog Log(string root, List<string>? removed = null) => new(Path.Combine(root, "unexpected-exits"), path =>
    {
        string name = Path.GetFileName(path);
        Directory.CreateDirectory(Path.Combine(root, "trash"));
        File.Move(path, Path.Combine(root, "trash", name));
        removed?.Add(name);
    });
}
